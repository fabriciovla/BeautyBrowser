using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BeautyBrowser.Controls;

/// <summary>
/// Noise texture over the glass of the sidebar, the library and the window edges. It tiles a single image drawn 1:1
/// with screen pixels so the grain stays fine and sharp. Texture, amount and softness are shared by every layer and
/// chosen in settings (<see cref="Configure"/>).
/// </summary>
public sealed partial class NoiseLayer : UserControl
{
    private const int TilePixels = 256;               // a power of two, so textures tile seamlessly
    private const double MaxOpacity = 0.25;           // grain at 100%

    private static readonly Dictionary<(string Texture, int Softness), WriteableBitmap> Tiles = [];
    private static string _texture = "grain";
    private static int _softness;                     // 0 to 20 in 5% steps, so images get reused
    private static double _amount;                    // 0 to 1
    private static event Action? Changed;

    private readonly Canvas _canvas = new();
    private WriteableBitmap? _shownTile;
    private double _builtScale;
    private Windows.Foundation.Size _builtSize;

    public NoiseLayer()
    {
        IsHitTestVisible = false;
        IsTabStop = false;
        Content = _canvas;
        Loaded += (_, _) =>
        {
            Changed += Apply;
            XamlRoot.Changed += XamlRoot_Changed;
            Apply();
        };
        Unloaded += (_, _) =>
        {
            Changed -= Apply;
            if (XamlRoot is not null) XamlRoot.Changed -= XamlRoot_Changed;
        };
        SizeChanged += (_, _) => Rebuild();
    }

    /// <summary>
    /// Changes the texture on every layer at once. <paramref name="amount"/> and <paramref name="softness"/> go from 0
    /// to 1. Changing only the amount is free (opacity); texture and softness rebuild the image once.
    /// </summary>
    public static void Configure(string texture, double amount, double softness)
    {
        amount = Math.Clamp(amount, 0, 1);
        var steps = (int)Math.Round(Math.Clamp(softness, 0, 1) * 20);
        if (texture == _texture && steps == _softness && Math.Abs(amount - _amount) < 0.0001) return;
        _texture = texture;
        _softness = steps;
        _amount = amount;
        Changed?.Invoke();
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => Rebuild();

    private void Apply()
    {
        Opacity = _amount * MaxOpacity;
        Visibility = _amount > 0 ? Visibility.Visible : Visibility.Collapsed;
        Rebuild();
    }

    /// <summary>Covers the control with copies of the texture; they share one image, so they cost no extra memory.</summary>
    private void Rebuild()
    {
        if (_amount <= 0 || XamlRoot is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var tile = Tile(_texture, _softness);
        var scale = XamlRoot.RasterizationScale;
        var size = new Windows.Foundation.Size(ActualWidth, ActualHeight);

        // Same size and scale: swapping the image of the existing copies is enough.
        if (Math.Abs(scale - _builtScale) < 0.001 && size.Width <= _builtSize.Width && size.Height <= _builtSize.Height)
        {
            if (tile != _shownTile)
            {
                foreach (var image in _canvas.Children.OfType<Image>()) image.Source = tile;
                _shownTile = tile;
            }
            return;
        }

        var tileDips = TilePixels / scale;
        _canvas.Children.Clear();
        for (double y = 0; y < size.Height; y += tileDips)
        {
            for (double x = 0; x < size.Width; x += tileDips)
            {
                var image = new Image { Source = tile, Width = tileDips, Height = tileDips, Stretch = Stretch.Fill };
                Canvas.SetLeft(image, x);
                Canvas.SetTop(image, y);
                _canvas.Children.Add(image);
            }
        }
        _canvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, size.Width, size.Height) };
        _shownTile = tile;
        _builtScale = scale;
        _builtSize = new Windows.Foundation.Size(Math.Ceiling(size.Width / tileDips) * tileDips, Math.Ceiling(size.Height / tileDips) * tileDips);
    }


    /// <summary>
    /// The image of a texture: every pixel is white (positive value) or black (negative) with the value as its alpha.
    /// It averages to neutral, so it adds texture without darkening or lightening the glass.
    /// </summary>
    private static WriteableBitmap Tile(string texture, int softness)
    {
        if (Tiles.TryGetValue((texture, softness), out var cached)) return cached;
        if (Tiles.Count > 12) Tiles.Clear(); // dragging the softness slider generates several; do not hoard them

        var (field, contrast) = texture switch
        {
            "sand" => (Sand(), 0.62),
            "fine" => (Grain(), 0.34),
            "paper" => (Paper(), 0.5),
            "tweed" => (Tweed(), 0.6),
            "denim" => (Denim(), 0.55),
            _ => (Grain(), 0.58),
        };

        // Softness: blur (three box passes ≈ gaussian) and restore the contrast so it stays visible.
        var radius = (int)Math.Round(softness / 20.0 * 3);
        if (radius > 0)
            for (var pass = 0; pass < 3; pass++) BoxBlur(field, radius);
        Normalize(field, contrast);

        var pixels = new byte[TilePixels * TilePixels * 4];
        for (var i = 0; i < field.Length; i++)
        {
            var v = Math.Clamp(field[i], -1, 1);
            var alpha = (byte)Math.Round(Math.Abs(v) * 255);
            var value = v > 0 ? alpha : (byte)0; // premultiplied: white = alpha, black = 0
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = value;
            pixels[i * 4 + 3] = alpha;
        }
        var bitmap = new WriteableBitmap(TilePixels, TilePixels);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels);
        bitmap.Invalidate();
        Tiles[(texture, softness)] = bitmap;
        return bitmap;
    }

    /// <summary>Classic grain: every pixel random.</summary>
    private static double[] Grain()
    {
        var field = new double[TilePixels * TilePixels];
        for (var i = 0; i < field.Length; i++) field[i] = Hash(i, 7);
        return field;
    }

    /// <summary>Sand: 2×2 grains with a few loose sparkles.</summary>
    private static double[] Sand()
    {
        var field = new double[TilePixels * TilePixels];
        for (var y = 0; y < TilePixels; y++)
        for (var x = 0; x < TilePixels; x++)
        {
            var grain = Hash(x / 2, y / 2 + 1000) * 0.8;
            var speck = Hash(x, y + 5000) > 0.93 ? Math.Sign(Hash(x, y + 9000)) : 0;
            field[y * TilePixels + x] = grain + speck;
        }
        return field;
    }

    /// <summary>Matte paper: large soft blotches with fine grain on top.</summary>
    private static double[] Paper()
    {
        const int cell = 16; // 256 / 16, so the grid tiles exactly
        const int cells = TilePixels / cell;
        var field = new double[TilePixels * TilePixels];
        for (var y = 0; y < TilePixels; y++)
        for (var x = 0; x < TilePixels; x++)
        {
            var (cx, cy) = (x / cell, y / cell);
            var (fx, fy) = (Smooth((x % cell) / (double)cell), Smooth((y % cell) / (double)cell));
            double Corner(int dx, int dy) => Hash((cx + dx) % cells, (cy + dy) % cells + 300);
            var top = Lerp(Corner(0, 0), Corner(1, 0), fx);
            var bottom = Lerp(Corner(0, 1), Corner(1, 1), fx);
            field[y * TilePixels + x] = Lerp(top, bottom, fy) * 0.75 + Hash(x, y + 700) * 0.3;
        }
        return field;
    }

    /// <summary>Tweed: short strokes on both diagonals, like mixed threads.</summary>
    private static double[] Tweed()
    {
        var field = new double[TilePixels * TilePixels];
        for (var y = 0; y < TilePixels; y++)
        for (var x = 0; x < TilePixels; x++)
        {
            var a = Hash((x - y + TilePixels) % TilePixels, (x + y) % TilePixels / 6 + 2000);
            var b = Hash((x + y) % TilePixels, (x - y + TilePixels) % TilePixels / 6 + 4000);
            field[y * TilePixels + x] = a * 0.65 + b * 0.65 + Hash(x, y + 800) * 0.25;
        }
        return field;
    }

    /// <summary>Denim: a regular diagonal twill with threads of different tones and grain.</summary>
    private static double[] Denim()
    {
        var field = new double[TilePixels * TilePixels];
        for (var y = 0; y < TilePixels; y++)
        for (var x = 0; x < TilePixels; x++)
        {
            var twill = (x + y) % 4 < 2 ? 1 : -1;
            var thread = 0.55 + 0.45 * Hash(x, 6000);
            field[y * TilePixels + x] = twill * thread * 0.55 + Hash(x, y + 900) * 0.35;
        }
        return field;
    }

    /// <summary>Horizontal and vertical box blur that wraps around the edges, since the texture tiles.</summary>
    private static void BoxBlur(double[] field, int radius)
    {
        var line = new double[TilePixels];
        var width = radius * 2 + 1;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var a = 0; a < TilePixels; a++)
            {
                for (var b = 0; b < TilePixels; b++) line[b] = field[Index(pass, a, b)];
                var sum = 0.0;
                for (var k = -radius; k <= radius; k++) sum += line[(k + TilePixels) % TilePixels];
                for (var b = 0; b < TilePixels; b++)
                {
                    field[Index(pass, a, b)] = sum / width;
                    sum += line[(b + radius + 1) % TilePixels] - line[(b - radius + TilePixels) % TilePixels];
                }
            }
        }

        static int Index(int pass, int a, int b) => pass == 0 ? a * TilePixels + b : b * TilePixels + a;
    }

    /// <summary>Centers on zero and brings the mean intensity (RMS) to the texture's contrast.</summary>
    private static void Normalize(double[] field, double contrast)
    {
        var mean = field.Average();
        var rms = Math.Sqrt(field.Sum(v => (v - mean) * (v - mean)) / field.Length);
        var gain = rms > 1e-6 ? contrast / rms : 0;
        for (var i = 0; i < field.Length; i++) field[i] = (field[i] - mean) * gain;
    }

    /// <summary>A fixed random number in [-1, 1] for (a, b).</summary>
    private static double Hash(int a, int b)
    {
        unchecked
        {
            var h = (uint)(a * 374761393 + b * 668265263);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return h / (double)uint.MaxValue * 2 - 1;
        }
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
