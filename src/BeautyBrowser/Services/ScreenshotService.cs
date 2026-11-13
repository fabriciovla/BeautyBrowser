using System.Runtime.InteropServices.WindowsRuntime;
using BeautyKit;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BeautyBrowser.Services;

/// <summary>What is done with a screenshot once taken: cut to the chosen area, copied and saved.</summary>
internal static class ScreenshotService
{
    /// <summary>
    /// Cuts <paramref name="area"/> (in the view's DIPs) out of a capture of the whole view of size
    /// <paramref name="view"/>. The capture may have more pixels than DIPs (screen scale), so the area is scaled.
    /// </summary>
    public static async Task<byte[]?> CropAsync(byte[] png, Rect area, Size view)
    {
        try
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(png.AsBuffer());
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);

            var scaleX = decoder.PixelWidth / Math.Max(1, view.Width);
            var scaleY = decoder.PixelHeight / Math.Max(1, view.Height);
            var x = (uint)Math.Clamp(Math.Round(area.X * scaleX), 0, decoder.PixelWidth - 1);
            var y = (uint)Math.Clamp(Math.Round(area.Y * scaleY), 0, decoder.PixelHeight - 1);
            var width = (uint)Math.Clamp(Math.Round(area.Width * scaleX), 1, decoder.PixelWidth - x);
            var height = (uint)Math.Clamp(Math.Round(area.Height * scaleY), 1, decoder.PixelHeight - y);

            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { Bounds = new BitmapBounds { X = x, Y = y, Width = width, Height = height } },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);

            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height,
                decoder.DpiX, decoder.DpiY, pixels.DetachPixelData());
            await encoder.FlushAsync();

            output.Seek(0);
            var bytes = new byte[output.Size];
            await output.ReadAsync(bytes.AsBuffer(), (uint)output.Size, InputStreamOptions.None);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Joins the pieces of a whole-page capture into one picture.</summary>
    public static async Task<byte[]?> StitchAsync(PageTiles page)
    {
        try
        {
            byte[]? canvas = null;
            uint width = 0, height = 0, stride = 0;
            double dpiX = 96, dpiY = 96;
            foreach (var tile in page.Tiles)
            {
                using var input = new InMemoryRandomAccessStream();
                await input.WriteAsync(tile.Png.AsBuffer());
                input.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(input);
                var pixels = (await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();

                var scale = decoder.PixelHeight / Math.Max(1, page.ViewHeight);
                if (canvas is null)
                {
                    width = decoder.PixelWidth;
                    stride = width * 4;
                    height = (uint)Math.Clamp(Math.Round(page.ContentHeight * scale), decoder.PixelHeight, 32_000);
                    dpiX = decoder.DpiX;
                    dpiY = decoder.DpiY;
                    canvas = new byte[(long)stride * height];
                }
                var top = (long)Math.Round(tile.ScrollY * scale);
                if (decoder.PixelWidth != width || top >= height) continue;
                var rows = (int)Math.Min(decoder.PixelHeight, height - top);
                System.Buffer.BlockCopy(pixels, 0, canvas, (int)(top * stride), rows * (int)stride);
            }
            if (canvas is null) return null;

            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, dpiX, dpiY, canvas);
            await encoder.FlushAsync();
            output.Seek(0);
            var bytes = new byte[output.Size];
            await output.ReadAsync(bytes.AsBuffer(), (uint)output.Size, InputStreamOptions.None);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Puts the picture on the clipboard and saves it in <paramref name="folder"/> ("Screenshot title date.png").
    /// Returns the file's path (null if it could not be saved) and whether the clipboard took it.
    /// </summary>
    public static async Task<(string? Path, bool Copied)> SaveAndCopyAsync(byte[] png, string title, string folder)
    {
        var copied = false;
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            copied = true;
        }
        catch
        {
            // The clipboard is in use by another app (or the picture is too big for it): the file is still saved.
        }

        try
        {
            Directory.CreateDirectory(folder);
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(title.Select(c => Array.IndexOf(invalid, c) >= 0 ? ' ' : c).ToArray()).Trim(' ', '.');
            if (clean.Length > 40) clean = clean[..40].TrimEnd(' ', '.');
            var name = $"{L.T("Screenshot", "Captura")} {(clean.Length > 0 ? clean + " " : "")}{DateTime.Now:yyyy-MM-dd HH-mm-ss}";
            var path = Path.Combine(folder, name + ".png");
            for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{name} ({n}).png");
            await File.WriteAllBytesAsync(path, png);
            return (path, copied);
        }
        catch
        {
            return (null, copied);
        }
    }
}
