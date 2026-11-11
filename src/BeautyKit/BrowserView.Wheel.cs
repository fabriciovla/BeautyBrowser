using System.Runtime.InteropServices;
using CefSharp;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace BeautyKit;

// The mouse wheel. Offscreen, Chromium scrolls each notch in one jump and ignores the Windows setting for lines per
// notch, so the view spreads each notch over a few frames and scales it by that setting, as Chrome does.
public sealed partial class BrowserView
{
    private const int Notch = 120;

    private double _wheelPendingX, _wheelPendingY; // what is left to send of the notches in progress
    private (int X, int Y) _wheelPoint;
    private CefEventFlags _wheelFlags;
    private bool _wheelAnimating;
    private int _zoomWheel; // Ctrl+wheel, adds up until a whole notch (touchpads send small steps)

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_tab is null) return;
        var point = e.GetCurrentPoint(this);
        var delta = point.Properties.MouseWheelDelta;
        e.Handled = true;

        // Ctrl+wheel (and touchpad pinch, which arrives as it) zooms the tab, so the zoom shown stays right.
        if (IsDown(VirtualKey.Control))
        {
            _zoomWheel += delta;
            for (; _zoomWheel >= Notch; _zoomWheel -= Notch) _tab.ZoomIn();
            for (; _zoomWheel <= -Notch; _zoomWheel += Notch) _tab.ZoomOut();
            return;
        }
        _zoomWheel = 0;

        // Shift turns the wheel horizontal, as in Chrome; offscreen, Chromium leaves that to the app.
        var horizontal = point.Properties.IsHorizontalMouseWheel || IsDown(VirtualKey.Shift);
        _wheelPoint = ((int)point.Position.X, (int)point.Position.Y);
        _wheelFlags = Flags(point.Properties);

        // Touchpads and precise wheels already send small steps: those go through as they come.
        if (delta % Notch != 0)
        {
            _tab.SendMouseWheel(_wheelPoint.X, _wheelPoint.Y, _wheelFlags, horizontal ? delta : 0, horizontal ? 0 : delta);
            return;
        }

        var amount = delta * WheelFactor(horizontal);
        if (horizontal) _wheelPendingX = Math.Sign(_wheelPendingX) == -Math.Sign(amount) ? amount : _wheelPendingX + amount;
        else _wheelPendingY = Math.Sign(_wheelPendingY) == -Math.Sign(amount) ? amount : _wheelPendingY + amount;

        if (_wheelAnimating) return;
        _wheelAnimating = true;
        CompositionTarget.Rendering += OnWheelFrame;
    }

    // Each frame sends a part of what is left: fast at first, slowing down at the end (about 150 ms per notch).
    private void OnWheelFrame(object? sender, object e)
    {
        var x = Step(ref _wheelPendingX);
        var y = Step(ref _wheelPendingY);
        if (_tab is not null && (x != 0 || y != 0)) _tab.SendMouseWheel(_wheelPoint.X, _wheelPoint.Y, _wheelFlags, x, y);
        if (_tab is not null && (_wheelPendingX != 0 || _wheelPendingY != 0)) return;

        _wheelPendingX = _wheelPendingY = 0;
        _wheelAnimating = false;
        CompositionTarget.Rendering -= OnWheelFrame;
    }

    private static int Step(ref double pending)
    {
        if (Math.Abs(pending) < 1) { pending = 0; return 0; }
        var step = pending * 0.3;
        var whole = (int)(Math.Abs(step) < 4 ? Math.Min(Math.Abs(pending), 4) * Math.Sign(pending) : Math.Round(step));
        pending -= whole;
        return whole;
    }

    // Windows' "lines per notch" (3 by default, which is Chromium's own step) for vertical scrolling and "characters per
    // notch" for horizontal; "one screen at a time" is left as a regular notch.
    private static double WheelFactor(bool horizontal)
    {
        uint value = 3;
        if (!SystemParametersInfo(horizontal ? SpiGetWheelScrollChars : SpiGetWheelScrollLines, 0, ref value, 0)) return 1;
        return value is 0 or > 100 ? 1 : value / 3.0;
    }

    private const uint SpiGetWheelScrollLines = 0x0068;
    private const uint SpiGetWheelScrollChars = 0x006C;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, ref uint value, uint winIni);
}
