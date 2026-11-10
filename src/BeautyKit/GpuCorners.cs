using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace BeautyKit;

/// <summary>
/// Rounded page corners drawn into the frame itself. Windows does not apply XAML or composition clips to a
/// SwapChainPanel (it can be shown as a direct overlay plane), so a small pixel shader multiplies every pixel by its
/// coverage of the rounded rectangle, with one pixel of anti-aliasing.
/// </summary>
internal static class GpuCorners
{
    private const string Shader = """
        cbuffer Corners : register(b0) { float2 size; float radius; float unused; };

        float4 vs(uint id : SV_VertexID) : SV_Position
        {
            float2 uv = float2((id << 1) & 2, id & 2);           // one triangle that covers the whole target
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }

        float4 ps(float4 position : SV_Position) : SV_Target
        {
            if (radius <= 0) return float4(1, 1, 1, 1);         // no corners: just make everything opaque
            float2 p = position.xy;                              // pixel center
            float2 center = clamp(p, radius, size - radius);     // center of the nearest arc, or the point itself
            float coverage = saturate(radius + 0.5 - length(p - center));
            return coverage.xxxx;                                // color × coverage, alpha = coverage (see the blend state)
        }
        """;

    private static ID3D11VertexShader? _vertex;
    private static ID3D11PixelShader? _pixel;
    private static ID3D11BlendState? _multiply;
    private static ID3D11Buffer? _constants;
    private static bool _failed;

    /// <summary>Rounds the corners of the premultiplied <paramref name="target"/>. Returns false when it cannot.</summary>
    public static bool Apply(ID3D11RenderTargetView target, uint width, uint height, float radiusPx)
    {
        if (_failed) return false;
        if (_vertex is null && !Create()) return false;

        var context = Gpu.Context;
        var data = new Constants { Width = width, Height = height, Radius = radiusPx };
        context.UpdateSubresource(in data, _constants!);

        context.OMSetRenderTargets(target);
        context.OMSetBlendState(_multiply);
        context.RSSetViewport(new Viewport(0, 0, width, height));
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertex);
        context.PSSetShader(_pixel);
        context.PSSetConstantBuffer(0, _constants);
        context.Draw(3, 0);
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        return true;
    }

    public static void Release()
    {
        _vertex?.Dispose();
        _pixel?.Dispose();
        _multiply?.Dispose();
        _constants?.Dispose();
        _vertex = null;
        _pixel = null;
        _multiply = null;
        _constants = null;
    }

    private static bool Create()
    {
        try
        {
            var device = Gpu.Device;
            _vertex = device.CreateVertexShader(Compile("vs", "vs_4_0"));
            _pixel = device.CreatePixelShader(Compile("ps", "ps_4_0"));

            // color = color × coverage and alpha = coverage: the inside ends up fully opaque even where Chromium left
            // semi-transparent pixels (the top edge of YouTube videos showed the background as a thin line), and the
            // corners become transparent.
            var blend = new BlendDescription();
            blend.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = Blend.Zero,
                DestinationBlend = Blend.SourceColor,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.Zero,
                BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All,
            };
            _multiply = device.CreateBlendState(blend);
            _constants = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer));
            return true;
        }
        catch (Exception ex)
        {
            _failed = true; // square corners, but the page still shows
            Gpu.Trace("Rounded corners unavailable: " + ex.Message);
            Release();
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public float Width;
        public float Height;
        public float Radius;
        public float Unused;
    }

    // d3dcompiler_47.dll ships with Chromium, next to the executable.

    private static unsafe byte[] Compile(string entryPoint, string profile)
    {
        var source = System.Text.Encoding.ASCII.GetBytes(Shader);
        var hr = D3DCompile(source, (nuint)source.Length, "corners", 0, 0, entryPoint, profile, 1 << 11 /* OPTIMIZATION_LEVEL3 */, 0,
            out var code, out var errors);
        try
        {
            if (hr < 0 || code == 0)
                throw new InvalidOperationException(errors != 0 ? Marshal.PtrToStringAnsi(BlobPointer(errors)) : $"0x{hr:X8}");
            var bytes = new byte[(int)BlobSize(code)];
            Marshal.Copy(BlobPointer(code), bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (code != 0) Marshal.Release(code);
            if (errors != 0) Marshal.Release(errors);
        }
    }

    // ID3DBlob: GetBufferPointer (3) and GetBufferSize (4) follow IUnknown.
    private static unsafe nint BlobPointer(nint blob) => ((delegate* unmanaged[Stdcall]<nint, nint>)(*(nint**)blob)[3])(blob);
    private static unsafe nuint BlobSize(nint blob) => ((delegate* unmanaged[Stdcall]<nint, nuint>)(*(nint**)blob)[4])(blob);

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] srcData, nuint srcDataSize, string sourceName, nint defines, nint include,
        string entryPoint, string target, uint flags1, uint flags2, out nint code, out nint errorMessages);
}
