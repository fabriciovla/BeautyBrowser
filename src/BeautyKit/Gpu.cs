using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace BeautyKit;

/// <summary>
/// The Direct3D device shared by every tab. Chromium renders each frame on the GPU and hands over the texture, which
/// is copied GPU to GPU to the screen (see BrowserView.Gpu). Everything runs on the UI thread, which is also
/// Chromium's thread (external message pump).
/// </summary>
internal static class Gpu
{
    private static ID3D11Device1? _device;
    private static ID3D11DeviceContext? _context;
    private static IDXGIFactory2? _factory;

    public static bool IsAvailable => _device is not null;

    public static string AdapterName { get; private set; } = "";
    public static long FramesPresented;
    public static long FramesFailed;
    public static string? LastError;
    public static ID3D11Device1 Device => _device ?? throw new InvalidOperationException("GPU not initialized");
    public static ID3D11DeviceContext Context => _context ?? throw new InvalidOperationException("GPU not initialized");
    public static IDXGIFactory2 Factory => _factory ?? throw new InvalidOperationException("GPU not initialized");

    /// <summary>
    /// Creates the device on the default adapter, the same one Chromium's GPU process uses: a shared texture can only
    /// be opened from the adapter that created it.
    /// </summary>
    public static bool TryInitialize()
    {
        if (_device is not null) return true;
        try
        {
            var result = D3D11.D3D11CreateDevice(
                null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out ID3D11Device? device, out ID3D11DeviceContext? context);
            if (result.Failure || device is null || context is null)
            {
                Trace($"D3D11CreateDevice failed (0x{result.Code:X8})");
                return false;
            }

            _device = device.QueryInterface<ID3D11Device1>();
            device.Dispose();
            _context = context;

            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            AdapterName = adapter.Description.Description;
            _factory = adapter.GetParent<IDXGIFactory2>();
            return true;
        }
        catch (Exception ex)
        {
            Trace("Could not start the GPU: " + ex.Message);
            Shutdown();
            return false;
        }
    }

    public static void Shutdown()
    {
        GpuCorners.Release();
        _factory?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _factory = null;
        _context = null;
        _device = null;
    }

    public static void CopyShared(nint sharedHandle, ref ID3D11Texture2D? target)
    {
        using var shared = Device.OpenSharedResource1<ID3D11Texture2D>(sharedHandle);
        var desc = shared.Description;
        if (target is null || target.Description.Width != desc.Width || target.Description.Height != desc.Height || target.Description.Format != desc.Format)
        {
            target?.Dispose();
            target = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            });
        }
        Context.CopyResource(target, shared);
        Context.Flush(); // Chromium's texture is only valid during the callback, so the copy has to be submitted now
    }

    public static unsafe void AttachToPanel(Microsoft.UI.Xaml.Controls.SwapChainPanel panel, IDXGISwapChain1? swapChain)
    {
        // ISwapChainPanelNative (microsoft.ui.xaml.media.dxinterop.h): SetSwapChain is its first own method.
        var iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
        var unknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var native));
        try
        {
            var vtable = *(nint**)native;
            var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain?.NativePointer ?? 0));
        }
        finally
        {
            Marshal.Release(native);
        }
    }

    public static void Trace(string message)
    {
        LastError = message;
        System.Diagnostics.Debug.WriteLine("[BeautyKit.Gpu] " + message);
        WriteLog(BrowserTab.FrameRate);
    }

    /// <summary>Writes a line to %LOCALAPPDATA%\BeautyBrowser\gpu.log at startup, on present and on shutdown.</summary>
    public static void WriteLog(int frameRate)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeautyBrowser");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "gpu.log"),
                $"{DateTime.Now:s} gpu={(BrowserTab.UseGpu ? "yes" : "no")} adapter=\"{AdapterName}\" fps={frameRate} " +
                $"presented={FramesPresented} failed={FramesFailed} last_error=\"{LastError}\"" + Environment.NewLine);
        }
        catch { }
    }
}
