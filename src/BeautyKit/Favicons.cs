using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BeautyKit;

/// <summary>
/// Favicons that stay sharp on scaled displays.
/// <para>
/// <c>BitmapImage(url)</c> looks pixelated: many pages declare a 16 px icon first and Windows takes the first frame of
/// an .ico (also 16 px), which gets stretched at 125-150%. Instead the file is downloaded, its largest frame is picked
/// and scaled to <see cref="Size"/> px with the Fant filter. SVG goes through SvgImageSource, but Windows' SVG renderer
/// ignores CSS and paints everything black, so a large raster icon is preferred when the page has one and SVG styles
/// are inlined as attributes first (<see cref="InlineSvgStyles"/>).
/// </para>
/// </summary>
public static class Favicons
{
    private const int Size = 32;      // shown at 16-20 DIP; 32 px covers up to 200% scaling

    /// <summary>Cache key: every requested size is its own image, since shrinking an already small image blurs it.</summary>
    private static string Key(string url, int size) => size == Size ? url : $"{url}#{size}";
    private const int MaxCache = 400;

    private static readonly HttpClient Http = CreateHttp();
    private static readonly Dictionary<string, Task<byte[]?>> Pixels = [];      // url → premultiplied BGRA, Size×Size
    private static readonly Dictionary<string, Task<int>> NativeSizes = [];     // url → side of the best frame (0 = unusable)
    private static readonly Dictionary<string, ImageSource> Images = [];
    private static readonly Dictionary<string, Task<bool>> SvgLoads = [];      // url → it could be rendered

    /// <summary>
    /// The favicon image, filled in when the download finishes. <paramref name="pixels"/> is the side in physical
    /// pixels it will be shown at, so it is not scaled again when drawn. Call it on the UI thread.
    /// </summary>
    public static ImageSource? FromUrl(string? url, int pixels = Size)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        pixels = Math.Clamp(pixels, 8, 256);
        var key = Key(uri.OriginalString, pixels);
        if (Images.TryGetValue(key, out var cached)) return cached;
        if (Images.Count > MaxCache) Images.Clear();

        ImageSource image;
        if (IsSvg(uri))
        {
            var svg = new SvgImageSource { RasterizePixelWidth = pixels, RasterizePixelHeight = pixels };
            SvgLoads[key] = LoadSvgAsync(svg, uri.OriginalString);
            image = svg;
        }
        else
        {
            var bitmap = NewTransparentBitmap(pixels);
            _ = FillAsync(bitmap, uri.OriginalString, pixels);
            image = bitmap;
        }
        Images[key] = image;
        return image;
    }

    /// <summary>
    /// Like <see cref="FromUrl"/>, but waits until the image is ready and returns null when it cannot be read, so the
    /// caller can show another icon instead of an empty square. Call it on the UI thread.
    /// </summary>
    public static async Task<ImageSource?> LoadAsync(string? url, int pixels = Size)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        pixels = Math.Clamp(pixels, 8, 256);
        var key = Key(uri.OriginalString, pixels);
        if (IsSvg(uri))
        {
            var image = FromUrl(url, pixels);
            if (SvgLoads.TryGetValue(key, out var loaded) && await loaded) return image;
            Images.Remove(key); // failed: retried next time
            SvgLoads.Remove(key);
            return null;
        }
        if (await PixelsAsync(uri.OriginalString, pixels) is null) return null;
        return FromUrl(url, pixels); // already decoded: filled in immediately
    }

    /// <summary>
    /// The favicon for an <see cref="Microsoft.UI.Xaml.Controls.Image"/> of <paramref name="dip"/> DIPs, with exactly
    /// the pixels that display's scale needs. Null when it cannot be read.
    /// </summary>
    public static async Task<ImageSource?> LoadForAsync(string? url, Microsoft.UI.Xaml.FrameworkElement target, double dip)
    {
        var scale = target.XamlRoot?.RasterizationScale ?? await LoadedScaleAsync(target);
        return await LoadAsync(url, (int)Math.Ceiling(dip * scale));
    }

    private static Task<double> LoadedScaleAsync(Microsoft.UI.Xaml.FrameworkElement element)
    {
        var done = new TaskCompletionSource<double>();
        Microsoft.UI.Xaml.RoutedEventHandler? loaded = null;
        loaded = (_, _) =>
        {
            element.Loaded -= loaded;
            done.TrySetResult(element.XamlRoot?.RasterizationScale ?? 1);
        };
        element.Loaded += loaded;
        return done.Task;
    }

    private static async Task<bool> LoadSvgAsync(SvgImageSource image, string url)
    {
        try
        {
            if (await DownloadAsync(url) is not { Length: > 0 } bytes) return false;
            var text = InlineSvgStyles(System.Text.Encoding.UTF8.GetString(bytes));
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text).AsBuffer());
            stream.Seek(0);
            return await image.SetSourceAsync(stream) == SvgImageSourceLoadStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Windows' SVG renderer ignores CSS, both &lt;style&gt; blocks and the style attribute. Every class rule and style
    /// is turned into presentation attributes (fill="red"), which it does understand. @media rules are ignored.
    /// </summary>
    internal static string InlineSvgStyles(string svg)
    {
        const RegexOptions Opts = RegexOptions.Singleline | RegexOptions.IgnoreCase;
        var blocks = Regex.Matches(svg, @"<style[^>]*>(.*?)</style>", Opts);
        var rules = new List<(string Class, string Property, string Value)>();
        foreach (Match block in blocks)
        {
            var css = Regex.Replace(block.Groups[1].Value, @"/\*.*?\*/|<!\[CDATA\[|\]\]>", "", Opts);
            css = Regex.Replace(css, @"@media[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", "", Opts);
            foreach (Match rule in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}", Opts))
            {
                var declarations = ParseDeclarations(rule.Groups[2].Value);
                foreach (var selector in rule.Groups[1].Value.Split(','))
                {
                    var name = selector.Trim();
                    if (!Regex.IsMatch(name, @"^\.[\w-]+$")) continue; // only ".class" selectors, which is what icons use
                    foreach (var (property, value) in declarations) rules.Add((name[1..], property, value));
                }
            }
        }
        if (blocks.Count == 0 && !svg.Contains("style=", StringComparison.OrdinalIgnoreCase)) return svg;
        svg = Regex.Replace(svg, @"<style[^>]*>.*?</style>", "", Opts);

        return Regex.Replace(svg, @"<([a-zA-Z][\w:-]*)(\s[^<>]*?)?(/?)>", tag =>
        {
            var attributes = tag.Groups[2].Value;
            var classes = Regex.Match(attributes, @"\sclass\s*=\s*[""']([^""']*)[""']", Opts);
            var inline = Regex.Match(attributes, @"\sstyle\s*=\s*[""']([^""']*)[""']", Opts);
            if (!classes.Success && !inline.Success) return tag.Value;

            // Precedence: attribute < class rules (in CSS order) < style="…".
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (classes.Success)
            {
                var names = classes.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var (cls, property, value) in rules)
                    if (names.Contains(cls)) values[property] = value;
            }
            if (inline.Success)
            {
                foreach (var (property, value) in ParseDeclarations(inline.Groups[1].Value)) values[property] = value;
                attributes = attributes.Remove(inline.Index, inline.Length);
            }
            foreach (var (property, value) in values)
            {
                attributes = Regex.Replace(attributes, $@"\s{Regex.Escape(property)}\s*=\s*(""[^""]*""|'[^']*')", "", Opts);
                attributes += $" {property}=\"{value.Replace("\"", "'")}\"";
            }
            return $"<{tag.Groups[1].Value}{attributes}{tag.Groups[3].Value}>";
        }, Opts);
    }

    private static List<(string Property, string Value)> ParseDeclarations(string css) =>
        css.Split(';')
            .Select(d => d.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0)
            .Select(p => (p[0].Trim().ToLowerInvariant(), p[1].Replace("!important", "").Trim()))
            .ToList();

    /// <summary>A transparent frame; a freshly created WriteableBitmap renders black.</summary>
    private static WriteableBitmap NewTransparentBitmap(int size)
    {
        var bitmap = new WriteableBitmap(size, size);
        new byte[size * size * 4].CopyTo(bitmap.PixelBuffer);
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    /// The best looking of the page's icons: a raster one of at least <see cref="Size"/> px (looks like Chrome), else
    /// the SVG, else the largest raster one. Null when none is usable.
    /// </summary>
    public static async Task<string?> PickBestAsync(IReadOnlyList<string> urls)
    {
        var candidates = urls.Where(u => Uri.TryCreate(u, UriKind.Absolute, out _)).Distinct().ToList();
        if (candidates.Count == 0) return null;
        var svg = candidates.FirstOrDefault(u => IsSvg(new Uri(u)));
        var rasters = candidates.Where(u => u != svg).ToList();

        var sizes = await Task.WhenAll(rasters.Select(NativeSizeAsync));
        var best = -1;
        for (var i = 0; i < rasters.Count; i++)
            if (sizes[i] > 0 && (best < 0 || sizes[i] > sizes[best])) best = i;

        if (best >= 0 && sizes[best] >= Size) return rasters[best];
        if (svg is not null) return svg;
        return best < 0 ? null : rasters[best];
    }

    private static async Task FillAsync(WriteableBitmap bitmap, string url, int size)
    {
        if (await PixelsAsync(url, size) is not { } pixels) return;
        pixels.CopyTo(bitmap.PixelBuffer);
        bitmap.Invalidate();
    }

    private static Task<int> NativeSizeAsync(string url)
    {
        if (!NativeSizes.TryGetValue(url, out var task))
        {
            if (NativeSizes.Count > MaxCache) NativeSizes.Clear();
            NativeSizes[url] = task = DecodeAsync(url, onlySize: true, Size).ContinueWith(t => t.Result.Size,
                TaskContinuationOptions.ExecuteSynchronously);
            _ = ForgetIfFailed(NativeSizes, url, task);
        }
        return task;
    }

    private static Task<byte[]?> PixelsAsync(string url, int size)
    {
        var key = Key(url, size);
        if (!Pixels.TryGetValue(key, out var task))
        {
            if (Pixels.Count > MaxCache) Pixels.Clear();
            Pixels[key] = task = DecodeAsync(url, onlySize: false, size).ContinueWith(t => t.Result.Pixels,
                TaskContinuationOptions.ExecuteSynchronously);
            _ = ForgetIfFailed(Pixels, key, task);
        }
        return task;
    }

    /// <summary>Failures (offline, slow server…) are evicted from the cache so they are retried later.</summary>
    private static async Task ForgetIfFailed<T>(Dictionary<string, Task<T>> cache, string url, Task<T> task)
    {
        var result = await task;
        if (result is null or 0 && cache.TryGetValue(url, out var current) && current == task)
        {
            cache.Remove(url);
            Images.Remove(url);
        }
    }

    private static readonly Dictionary<string, Task<byte[]?>> Downloads = [];

    private static Task<byte[]?> DownloadAsync(string url)
    {
        if (!Downloads.TryGetValue(url, out var task))
        {
            if (Downloads.Count > MaxCache) Downloads.Clear();
            Downloads[url] = task = FetchBytesAsync(url);
            _ = ForgetIfFailed(Downloads, url, task);
        }
        return task;
    }

    private static async Task<byte[]?> FetchBytesAsync(string url)
    {
        try
        {
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = url.IndexOf(',');
                if (comma < 0) return null;
                var data = url[(comma + 1)..];
                return url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(data)
                    : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data));
            }
            return await Http.GetByteArrayAsync(url).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decodes the file, picks the best frame (an .ico has several) and scales it to Size×Size.</summary>
    private static async Task<(int Size, byte[]? Pixels)> DecodeAsync(string url, bool onlySize, int Size)
    {
        try
        {
            if (await DownloadAsync(url).ConfigureAwait(false) is not { Length: > 0 } bytes) return (0, null);

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);

            // The smallest frame that reaches Size, or else the largest one.
            BitmapFrame? best = null;
            for (uint i = 0; i < decoder.FrameCount; i++)
            {
                var frame = await decoder.GetFrameAsync(i);
                var side = Math.Max(frame.PixelWidth, frame.PixelHeight);
                if (best is null) { best = frame; continue; }
                var bestSide = Math.Max(best.PixelWidth, best.PixelHeight);
                if (bestSide < Size ? side > bestSide : side >= Size && side < bestSide) best = frame;
            }
            if (best is null || best.PixelWidth == 0 || best.PixelHeight == 0) return (0, null);

            var native = (int)Math.Max(best.PixelWidth, best.PixelHeight);
            if (onlySize) return (native, null);

            // Scale keeping the aspect ratio and center it in the square.
            var scale = (double)Size / native;
            var w = (uint)Math.Clamp(Math.Round(best.PixelWidth * scale), 1, Size);
            var h = (uint)Math.Clamp(Math.Round(best.PixelHeight * scale), 1, Size);
            byte[] src;
            try
            {
                var transform = new BitmapTransform { ScaledWidth = w, ScaledHeight = h, InterpolationMode = BitmapInterpolationMode.Fant };
                var data = await best.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                src = data.DetachPixelData();
            }
            catch
            {
                var data = await best.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                src = BoxDownscale(data.DetachPixelData(), (int)best.PixelWidth, (int)best.PixelHeight, (int)w, (int)h);
            }
            if (src.Length < w * h * 4 || IsFullyTransparent(src)) return (0, null);

            var pixels = new byte[Size * Size * 4];
            var left = (Size - (int)w) / 2;
            var top = (Size - (int)h) / 2;
            for (var y = 0; y < h; y++)
                System.Buffer.BlockCopy(src, y * (int)w * 4, pixels, ((top + y) * Size + left) * 4, (int)w * 4);
            return (native, pixels);
        }
        catch
        {
            return (0, null);
        }
    }

    /// <summary>Downscales by averaging each block of premultiplied BGRA pixels.</summary>
    private static byte[] BoxDownscale(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
            for (var x = 0; x < dw; x++)
            {
                int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                int b = 0, g = 0, r = 0, a = 0, n = 0;
                for (var yy = y0; yy < y1; yy++)
                    for (var xx = x0; xx < x1; xx++)
                    {
                        var i = (yy * sw + xx) * 4;
                        b += src[i]; g += src[i + 1]; r += src[i + 2]; a += src[i + 3]; n++;
                    }
                var o = (y * dw + x) * 4;
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(r / n); dst[o + 3] = (byte)(a / n);
            }
        }
        return dst;
    }

    private static bool IsFullyTransparent(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) return false;
        return true;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        // Some servers reject clients without a browser User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36");
        return http;
    }

    public static bool IsSvgUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsSvg(uri);

    private static bool IsSvg(Uri uri) =>
        uri.Scheme == "data"
            ? uri.OriginalString.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase)
            : uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
}
