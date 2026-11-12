using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BeautyBrowser.Services;

/// <summary>
/// The icon Windows shows for a file type (SHGetFileInfo by extension, so the file does not need to exist), cached per
/// extension. When the shell cannot give one, <see cref="GlyphFor"/> picks a Segoe glyph by type.
/// </summary>
public static partial class FileIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Must run on the UI thread (it creates a WriteableBitmap). Null if the shell gave nothing.</summary>
    public static ImageSource? For(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (Cache.TryGetValue(ext, out var cached)) return cached;
        ImageSource? icon = null;
        try { icon = Load(ext.Length == 0 ? "file" : "file" + ext); } catch { /* falls back to the glyph */ }
        Cache[ext] = icon;
        return icon;
    }

    public static string GlyphFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".iso" => "",
        ".pdf" => "",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" or ".ico" or ".avif" => "",
        ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".wmv" or ".flv" => "",
        ".exe" or ".msi" or ".msix" or ".bat" or ".cmd" or ".ps1" => "",
        _ => "",
    };

    private static ImageSource? Load(string name)
    {
        var info = new ShFileInfo();
        if (SHGetFileInfo(name, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<ShFileInfo>(),
                SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES) == 0 || info.hIcon == 0)
            return null;

        nint colorBitmap = 0, maskBitmap = 0;
        try
        {
            if (!GetIconInfo(info.hIcon, out var iconInfo)) return null;
            colorBitmap = iconInfo.hbmColor;
            maskBitmap = iconInfo.hbmMask;
            if (colorBitmap == 0) return null;

            GetObject(colorBitmap, Marshal.SizeOf<NativeBitmap>(), out var bmp);
            int width = bmp.bmWidth, height = bmp.bmHeight;
            if (width <= 0 || height <= 0) return null;

            var pixels = GetPixels(colorBitmap, width, height);
            if (pixels is null) return null;

            // Icons without an alpha channel carry their transparency in the mask.
            var hasAlpha = false;
            for (var i = 3; i < pixels.Length; i += 4)
                if (pixels[i] != 0) { hasAlpha = true; break; }
            if (!hasAlpha)
            {
                var mask = maskBitmap != 0 ? GetPixels(maskBitmap, width, height) : null;
                for (var i = 0; i < pixels.Length; i += 4)
                    pixels[i + 3] = mask is null || mask[i] == 0 ? (byte)255 : (byte)0;
            }

            var bitmap = new WriteableBitmap(width, height);
            pixels.CopyTo(0, bitmap.PixelBuffer, 0, pixels.Length);
            bitmap.Invalidate();
            return bitmap;
        }
        finally
        {
            if (colorBitmap != 0) DeleteObject(colorBitmap);
            if (maskBitmap != 0) DeleteObject(maskBitmap);
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>Top-down 32-bit BGRA pixels of an HBITMAP.</summary>
    private static byte[]? GetPixels(nint hBitmap, int width, int height)
    {
        var header = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        var dc = GetDC(0);
        try
        {
            return GetDIBits(dc, hBitmap, 0, (uint)height, pixels, ref header, 0) == 0 ? null : pixels;
        }
        finally
        {
            ReleaseDC(0, dc);
        }
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int fIcon, xHotspot, yHotspot;
        public nint hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public nint bmBits;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(nint icon, out IconInfo info);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint obj, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
}
