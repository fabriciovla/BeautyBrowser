using Vortice.Direct3D11;

namespace BeautyKit;

/// <summary>
/// GPU rendering: with <see cref="UseGpu"/> Chromium hands over its shared texture (OnAcceleratedPaint) instead of
/// copying frames to system memory. A GPU-side copy is kept so the frame can be shown again when switching tabs.
/// </summary>
public sealed partial class BrowserTab
{
    /// <summary>Decided when the engine starts: hardware acceleration is on and a GPU is available.</summary>
    internal static bool UseGpu { get; set; }

    /// <summary>Frames per second Chromium renders at (the monitor refresh rate when using the GPU).</summary>
    internal static int FrameRate { get; set; } = 60;

    private ID3D11Texture2D? _gpuFrame;
    private ID3D11Texture2D? _gpuPopup;

    internal ID3D11Texture2D? GpuFrame => _gpuFrame;

    /// <summary>Last frame of the popup widget (&lt;select&gt; and the like).</summary>
    internal ID3D11Texture2D? GpuPopup => _gpuPopup;

    internal event Action? GpuFrameArrived;

    /// <summary>The popup in physical pixels of the view, or null when it is closed.</summary>
    internal (int X, int Y, int Width, int Height)? PopupRectPx =>
        PopupBounds is { } b
            ? ((int)Math.Round(b.X * _scale), (int)Math.Round(b.Y * _scale), (int)Math.Round(b.Width * _scale), (int)Math.Round(b.Height * _scale))
            : null;

    private void AcceleratedPaint(bool popup, nint sharedHandle)
    {
        if (_disposed || sharedHandle == 0 || !Gpu.IsAvailable) return;
        try
        {
            if (popup) Gpu.CopyShared(sharedHandle, ref _gpuPopup);
            else
            {
                Gpu.CopyShared(sharedHandle, ref _gpuFrame);
                FrameChanged();
            }
            GpuFrameArrived?.Invoke();
        }
        catch (Exception ex)
        {
            Gpu.FramesFailed++;
            Gpu.Trace("GPU frame lost: " + ex.Message);
        }
    }

    private void DisposeGpu()
    {
        _gpuFrame?.Dispose();
        _gpuPopup?.Dispose();
        _gpuFrame = null;
        _gpuPopup = null;
    }
}
