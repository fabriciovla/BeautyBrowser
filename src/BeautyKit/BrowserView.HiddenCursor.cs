using System.Runtime.InteropServices;
using Microsoft.UI;

namespace BeautyKit;

// Pages hide the cursor while a video plays and the pointer rests (YouTube in full screen). WinUI has no "no cursor"
// shape, so a fully transparent Win32 cursor is applied instead: right away, and again on every WM_SETCURSOR, which
// is when WinUI would put its own cursor back.
public sealed partial class BrowserView
{
    private const uint WmSetCursor = 0x0020;
    private const int HtClient = 1;

    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);
    private delegate bool EnumProc(nint hwnd, nint lParam);

    private static readonly SubclassProc s_subclass = CursorSubclass; // static: native code keeps calling it
    private static readonly EnumProc s_enum = SubclassWindow;
    private static readonly nint s_transparentCursor = CreateTransparentCursor();
    private static readonly HashSet<nint> s_subclassed = [];
    private static bool s_cursorHidden;

    private void SetCursorHidden(bool hidden)
    {
        s_cursorHidden = hidden;
        if (!hidden || s_transparentCursor == 0 || XamlRoot is null) return;
        var top = Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        if (top == 0) return;
        SubclassWindow(top, 0);
        EnumChildWindows(top, s_enum, 0);
        SetCursor(s_transparentCursor);
    }

    private static bool SubclassWindow(nint hwnd, nint _)
    {
        if (s_subclassed.Add(hwnd)) SetWindowSubclass(hwnd, s_subclass, 1, 0);
        return true;
    }

    private static nint CursorSubclass(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == WmSetCursor && s_cursorHidden && (lParam & 0xFFFF) == HtClient)
        {
            SetCursor(s_transparentCursor);
            return 1;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private static nint CreateTransparentCursor()
    {
        var and = new byte[128];
        Array.Fill(and, (byte)0xFF); // AND all ones + XOR all zeros = the screen shows through
        return CreateCursor(0, 0, 0, 32, 32, and, new byte[128]);
    }

    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] private static extern nint CreateCursor(nint instance, int xHot, int yHot, int width, int height, byte[] andPlane, byte[] xorPlane);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumProc proc, nint lParam);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);
}
