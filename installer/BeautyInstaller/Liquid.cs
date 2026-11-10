#nullable disable
// From Beauty Screenshot (setup/Liquid.cs): the same liquid background as Beauty Shell.
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BeautyInstaller
{
    /// <summary>
    /// The welcome background, the same as Beauty Shell's (src/liquid.ts there): four huge, soft color spots drift
    /// slowly and the borders between them ripple a little, like a still liquid. Moving to the next step pushes the
    /// spots forward and they settle gently. That shader is ported here line by line and computed on the CPU at a
    /// quarter of the window's resolution: it is all gradient, so the Image stretching it up loses nothing.
    /// </summary>
    sealed class Liquid
    {
        const int Scale = 4;

        public readonly WriteableBitmap Bitmap;
        readonly int w, h;
        readonly int[] pixels;
        readonly double[][] colors;   // linear light: mixing in sRGB muddies light colors toward brown
        readonly double dim;
        double time, push, pushTarget;

        public Liquid(double width, double height, Color[] palette, bool dark)
        {
            w = (int)Math.Ceiling(width / Scale);
            h = (int)Math.Ceiling(height / Scale);
            pixels = new int[w * h];
            Bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            colors = Array.ConvertAll(palette, c => new[] { Lin(c.R), Lin(c.G), Lin(c.B) });
            dim = dark ? 0.5 : 1;
            Render();
        }

        /// <summary>Pushes the liquid: 1 forward, -1 back.</summary>
        public void Flow(int dir) => pushTarget += dir;

        public void Tick(double dt)
        {
            dt = Math.Min(0.05, dt);
            time += dt;
            push += (pushTarget - push) * (1 - Math.Exp(-dt * 1.8));
            Render();
        }

        void Render()
        {
            double aspect = (double)w / h;
            double s = time * 0.05 + push * 1.3;
            double stir = Math.Min(1, Math.Abs(pushTarget - push));
            double wave = 0.02 + stir * 0.05;

            double a1x = 0.15 * aspect + 0.16 * Math.Sin(s * 0.9), a1y = 0.85 + 0.16 * Math.Cos(s * 0.7);
            double a2x = 0.85 * aspect + 0.16 * Math.Cos(s * 0.6), a2y = 0.8 + 0.16 * Math.Sin(s * 0.8);
            double a3x = 0.6 * aspect + 0.16 * Math.Sin(s * 0.7 + 2.0), a3y = 0.1 + 0.16 * Math.Cos(s * 0.5);
            double a4x = 0.2 * aspect + 0.16 * Math.Cos(s * 0.8 + 1.0), a4y = 0.25 + 0.16 * Math.Sin(s * 0.6);
            double[] c1 = colors[0], c2 = colors[1], c3 = colors[2], c4 = colors[3];
            const double baseWeight = 0.08;

            for (int y = 0; y < h; y++)
            {
                double py0 = 1 - (y + 0.5) / h;   // the shader's y grows upward
                for (int x = 0; x < w; x++)
                {
                    double px = (x + 0.5) / w * aspect, py = py0;
                    double ox = wave * Math.Sin(py * 2.2 + s * 1.3), oy = wave * Math.Sin(px * 2.2 - s * 1.1);
                    px += ox;
                    py += oy;

                    double w1 = Weight(px, py, a1x, a1y), w2 = Weight(px, py, a2x, a2y);
                    double w3 = Weight(px, py, a3x, a3y), w4 = Weight(px, py, a4x, a4y) * 0.7;
                    double sum = w1 + w2 + w3 + w4 + baseWeight;
                    double r = Gamma((c1[0] * w1 + c2[0] * w2 + c3[0] * w3 + c4[0] * w4 + c2[0] * baseWeight) / sum);
                    double g = Gamma((c1[1] * w1 + c2[1] * w2 + c3[1] * w3 + c4[1] * w4 + c2[1] * baseWeight) / sum);
                    double b = Gamma((c1[2] * w1 + c2[2] * w2 + c3[2] * w3 + c4[2] * w4 + c2[2] * baseWeight) / sum);

                    // A touch more saturation, as in the shader.
                    double luma = r * 0.299 + g * 0.587 + b * 0.114;
                    r = luma + (r - luma) * 1.15;
                    g = luma + (g - luma) * 1.15;
                    b = luma + (b - luma) * 1.15;

                    pixels[y * w + x] = (Byte(r * dim) << 16) | (Byte(g * dim) << 8) | Byte(b * dim);
                }
            }
            Bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        }

        static double Weight(double px, double py, double cx, double cy)
        {
            double dx = px - cx, dy = py - cy;
            return Math.Exp(-(dx * dx + dy * dy) * 2.4);
        }

        static double Lin(byte v) => Math.Pow(v / 255.0, 2.2);

        // Back to sRGB through a table: three Math.Pow per pixel were most of each frame.
        static readonly double[] gammaTable = BuildGammaTable();
        static double[] BuildGammaTable()
        {
            var t = new double[4097];
            for (int i = 0; i < t.Length; i++) t[i] = Math.Pow(i / 4096.0, 1 / 2.2);
            return t;
        }
        static double Gamma(double v) => gammaTable[v <= 0 ? 0 : v >= 1 ? 4096 : (int)(v * 4096)];
        static int Byte(double v) => v <= 0 ? 0 : v >= 1 ? 255 : (int)(v * 255 + 0.5);

        /// <summary>A fine grain over the gradient, tiled; on the smooth liquid it is barely a touch.</summary>
        public static ImageBrush Grain()
        {
            const int size = 128;
            var noise = new byte[size * size * 4];
            var random = new Random(7);
            for (int i = 0; i < noise.Length; i += 4)
            {
                byte v = (byte)random.Next(256);
                noise[i] = noise[i + 1] = noise[i + 2] = v;
                noise[i + 3] = 255;
            }
            var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, noise, size * 4);
            var brush = new ImageBrush(bmp)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, size, size),
                ViewportUnits = BrushMappingMode.Absolute,
            };
            brush.Freeze();
            return brush;
        }
    }
}
