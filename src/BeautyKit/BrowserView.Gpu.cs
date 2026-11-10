using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BeautyKit;

/// <summary>
/// GPU presentation: the tab's frame (and its popup, if one is open) is copied GPU to GPU into a SwapChainPanel as soon
/// as Chromium finishes it, without going through the CPU.
/// </summary>
public sealed partial class BrowserView
{
    private const int SwapChainBuffers = 2;

    private SwapChainPanel? _gpuPanel;
    private IDXGISwapChain1? _swapChain;
    private Format _swapFormat;
    private float _swapScale;

    [MemberNotNull(nameof(_gpuPanel))]
    private void CreateGpuPanel(Grid layers)
    {
        _gpuPanel = new SwapChainPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        _gpuPanel.CompositionScaleChanged += (_, _) => PresentGpu();
        layers.Children.Insert(0, _gpuPanel);
        _page.Visibility = Visibility.Collapsed;
        _popup.Visibility = Visibility.Collapsed;
        Unloaded += (_, _) => ReleaseSwapChain();
    }

    private void PresentGpu()
    {
        if (_gpuPanel is null)
        {
            if (!BrowserTab.UseGpu) return;
            CreateGpuPanel(_layers);
        }
        var frame = _tab?.GpuFrame;
        if (frame is null)
        {
            _gpuPanel.Visibility = Visibility.Collapsed; // white background until the first frame
            return;
        }

        try
        {
            var desc = frame.Description;
            var scale = (float)(XamlRoot?.RasterizationScale ?? (_gpuPanel.CompositionScaleX > 0 ? _gpuPanel.CompositionScaleX : 1));

            // The frame's real size in DIPs, anchored to the top left like the CPU path.
            _gpuPanel.Width = desc.Width / scale;
            _gpuPanel.Height = desc.Height / scale;
            _gpuPanel.Visibility = Visibility.Visible;

            EnsureSwapChain(desc.Width, desc.Height, desc.Format, scale);

            using (var back = _swapChain!.GetBuffer<ID3D11Texture2D>(0))
            {
                Gpu.Context.CopyResource(back, frame);
                if (_tab?.GpuPopup is { } popup && _tab.PopupRectPx is { } rect) DrawPopup(back, popup, rect, desc.Width, desc.Height);

                // Rounded corners (XAML's CornerRadius does not clip this layer) and everything else opaque, also in
                // full screen with radius 0. See GpuCorners.
                _backTarget ??= Gpu.Device.CreateRenderTargetView(back);
                GpuCorners.Apply(_backTarget, desc.Width, desc.Height, (float)(CornerRadius.TopLeft * scale));
            }
            _swapChain.Present(0, PresentFlags.None);
            if (++Gpu.FramesPresented == 1) Gpu.WriteLog(BrowserTab.FrameRate);
        }
        catch (Exception ex)
        {
            Gpu.FramesFailed++;
            Gpu.Trace("Could not present the frame: " + ex.Message);
            ReleaseSwapChain(); // recreated with the next frame
        }
    }

    private static void DrawPopup(ID3D11Texture2D back, ID3D11Texture2D popup, (int X, int Y, int Width, int Height) rect, uint width, uint height)
    {
        // Clip the popup to the view: CopySubresourceRegion cannot write out of bounds.
        var pd = popup.Description;
        int left = Math.Max(0, -rect.X), top = Math.Max(0, -rect.Y);
        int x = Math.Max(0, rect.X), y = Math.Max(0, rect.Y);
        int right = (int)Math.Min(pd.Width, width - (uint)x + (uint)left);
        int bottom = (int)Math.Min(pd.Height, height - (uint)y + (uint)top);
        if (right <= left || bottom <= top || pd.Format != back.Description.Format) return;
        Gpu.Context.CopySubresourceRegion(back, 0, (uint)x, (uint)y, 0, popup, 0, new Box(left, top, 0, right, bottom, 1));
    }

    private void EnsureSwapChain(uint width, uint height, Format format, float scale)
    {
        if (_swapChain is null || _swapFormat != format)
        {
            ReleaseSwapChain();
            ReleaseBackTarget();
            _swapChain = Gpu.Factory.CreateSwapChainForComposition(Gpu.Device, new SwapChainDescription1
            {
                Width = width,
                Height = height,
                Format = format,
                BufferCount = SwapChainBuffers,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipSequential,
                Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Premultiplied, // the rounded corners are transparent
            });
            _swapFormat = format;
            _swapScale = 0;
            Gpu.AttachToPanel(_gpuPanel!, _swapChain);
        }
        else
        {
            var desc = _swapChain.Description1;
            if (desc.Width != width || desc.Height != height)
            {
                ReleaseBackTarget(); // ResizeBuffers requires releasing every reference to the buffers
                _swapChain.ResizeBuffers(SwapChainBuffers, width, height, format, SwapChainFlags.None).CheckError();
            }
        }

        // The swap chain is in physical pixels; scale it down to DIPs so it shows 1:1.
        if (Math.Abs(_swapScale - scale) > 0.0001f)
        {
            using var swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
            swapChain2.MatrixTransform = Matrix3x2.CreateScale(1 / scale);
            _swapScale = scale;
        }
    }

    // Render target view of buffer 0. With the flip model D3D11 keeps it pointing at the current back buffer, so it
    // is created once and released when resizing or recreating the swap chain.
    private ID3D11RenderTargetView? _backTarget;

    private void ReleaseBackTarget()
    {
        if (_backTarget is null) return;
        if (Gpu.IsAvailable) // on shutdown the GPU can go away before the view
        {
            Gpu.Context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
            Gpu.Context.ClearState();
        }
        _backTarget.Dispose();
        _backTarget = null;
    }

    private void ReleaseSwapChain()
    {
        ReleaseBackTarget();
        if (_swapChain is null) return;
        if (_gpuPanel is not null)
        {
            try { Gpu.AttachToPanel(_gpuPanel, null); } catch { }
        }
        _swapChain.Dispose();
        _swapChain = null;
    }
}
