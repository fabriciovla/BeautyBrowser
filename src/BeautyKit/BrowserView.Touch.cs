using CefSharp;
using CefSharp.Enums;
using CefSharp.Structs;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.UI.ViewManagement;

namespace BeautyKit;

// Touch screens: fingers go to Chromium as touches, not as a mouse, so it scrolls, taps, long-presses (context menu) and
// pinches the way Chrome does. When the page focuses a text field after a touch, the touch keyboard comes up.
public sealed partial class BrowserView
{
    private readonly HashSet<uint> _touches = [];
    private bool _lastInputWasTouch;

    private static bool IsTouch(PointerRoutedEventArgs e) => e.Pointer.PointerDeviceType == PointerDeviceType.Touch;

    private void SendTouch(PointerRoutedEventArgs e, TouchEventType type)
    {
        e.Handled = true;
        if (_tab is null) return;
        var id = e.Pointer.PointerId;
        if (type == TouchEventType.Pressed)
        {
            if (_touches.Count == 0) Focus(FocusState.Pointer);
            _touches.Add(id);
            CapturePointer(e.Pointer);
        }
        else if (!_touches.Contains(id))
        {
            return; // a touch that started elsewhere, or already ended
        }

        var point = e.GetCurrentPoint(this);
        if (type == TouchEventType.Pressed) LastPressPosition = point.Position;
        var contact = point.Properties.ContactRect;
        _tab.SendTouch(new TouchEvent
        {
            Id = (int)id,
            X = (float)point.Position.X,
            Y = (float)point.Position.Y,
            RadiusX = (float)(contact.Width / 2),
            RadiusY = (float)(contact.Height / 2),
            Pressure = point.Properties.Pressure,
            Type = type,
            Modifiers = KeyboardFlags(),
            PointerType = PointerType.Touch,
        });

        if (type is TouchEventType.Released or TouchEventType.Cancelled)
        {
            _touches.Remove(id);
            if (type == TouchEventType.Released) ReleasePointerCapture(e.Pointer);
        }
    }

    private void OnVirtualKeyboardRequested(bool show)
    {
        try
        {
            if (XamlRoot?.ContentIslandEnvironment?.AppWindowId is not { } windowId) return;
            var pane = InputPaneInterop.GetForWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(windowId));
            if (show && _lastInputWasTouch) pane.TryShow();
            else if (!show) pane.TryHide();
        }
        catch
        {
            // no touch keyboard on this PC
        }
    }
}
