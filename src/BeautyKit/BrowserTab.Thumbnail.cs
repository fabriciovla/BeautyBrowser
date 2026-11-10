using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BeautyKit;

/// <summary>
/// Page thumbnail for the tab preview: the last frame Chromium rendered (kept even while the tab is hidden), cropped
/// to the top and downscaled with a box filter.
/// </summary>
public sealed partial class BrowserTab
{
    private WriteableBitmap? _thumbnail;
    private long _thumbnailFrame = -1;
    private long _frameCount;

    /// <summary>
    /// The top of the page at <paramref name="width"/>×<paramref name="height"/> pixels, or null if the tab has never
    /// been rendered (a restored tab that was not opened yet). Call it on the UI thread.
    /// </summary>
    public ImageSource? CaptureThumbnail(int width, int height)
    {
        if (_disposed || width <= 0 || height <= 0) return null;
        if (_thumbnail is not null && _thumbnailFrame == _frameCount &&
            _thumbnail.PixelWidth == width && _thumbnail.PixelHeight == height) return _thumbnail;

        byte[]? pixels = null;
        try
        {
            if (_gpuFrame is not null) pixels = FromGpu(_gpuFrame, width, height);
            else if (Frame is { } frame) pixels = FromBitmap(frame, width, height);
        }
        catch (Exception ex)
        {
            Gpu.Trace("Thumbnail: " + ex.Message);
        }
        if (pixels is null) return null;

        var bitmap = new WriteableBitmap(width, height);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels);
        bitmap.Invalidate();
        _thumbnail = bitmap;
        _thumbnailFrame = _frameCount;
        return bitmap;
    }

    private void FrameChanged() => _frameCount++;

    private static unsafe byte[]? FromGpu(ID3D11Texture2D frame, int width, int height)
    {
        var desc = frame.Description;
        var bgra = desc.Format is Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb;
        var rgba = desc.Format is Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb;
        if (!bgra && !rgba) return null;

        // Only copy the part the thumbnail shows: the top, at its aspect ratio.
        var srcW = (int)desc.Width;
        var srcH = Math.Min((int)desc.Height, (int)Math.Round(srcW * (double)height / width));
        using var staging = Gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)srcW,
            Height = (uint)srcH,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        Gpu.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, frame, 0, new Box(0, 0, 0, srcW, srcH, 1));
        var map = Gpu.Context.Map(staging, 0, MapMode.Read);
        try
        {
            return Downscale((byte*)map.DataPointer, (int)map.RowPitch, srcW, srcH, width, height, swapRedBlue: rgba);
        }
        finally
        {
            Gpu.Context.Unmap(staging, 0);
        }
    }

    private static unsafe byte[]? FromBitmap(WriteableBitmap frame, int width, int height)
    {
        var srcW = frame.PixelWidth;
        var srcH = Math.Min(frame.PixelHeight, (int)Math.Round(srcW * (double)height / width));
        var buffer = frame.PixelBuffer.ToArray();
        fixed (byte* p = buffer) return Downscale(p, srcW * 4, srcW, srcH, width, height, swapRedBlue: false);
    }

    /// <summary>Downscales by averaging every block of pixels, which stays sharp without stair-stepping.</summary>
    private static unsafe byte[] Downscale(byte* src, int pitch, int srcW, int srcH, int width, int height, bool swapRedBlue)
    {
        var output = new byte[width * height * 4];
        var scaleX = (double)srcW / width;
        var scaleY = (double)srcH / height;
        for (var y = 0; y < height; y++)
        {
            var y0 = (int)(y * scaleY);
            var y1 = Math.Max(y0 + 1, Math.Min(srcH, (int)((y + 1) * scaleY)));
            for (var x = 0; x < width; x++)
            {
                var x0 = (int)(x * scaleX);
                var x1 = Math.Max(x0 + 1, Math.Min(srcW, (int)((x + 1) * scaleX)));
                int b = 0, g = 0, r = 0, n = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = src + sy * pitch;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var px = row + sx * 4;
                        b += px[0]; g += px[1]; r += px[2]; n++;
                    }
                }
                var o = (y * width + x) * 4;
                n = Math.Max(n, 1);
                output[o] = (byte)((swapRedBlue ? r : b) / n);
                output[o + 1] = (byte)(g / n);
                output[o + 2] = (byte)((swapRedBlue ? b : r) / n);
                output[o + 3] = 255;
            }
        }
        return output;
    }
}
