using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BeautyBrowser;

/// <summary>Draggable vertical splitter between the page and DevTools; reports the drag delta in DIPs.</summary>
public sealed partial class ResizeHandle : Grid
{
    private double? _lastX;

    public ResizeHandle()
    {
        Width = 8;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // hit-testable
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        PointerPressed += (_, e) =>
        {
            CapturePointer(e.Pointer);
            _lastX = e.GetCurrentPoint(null).Position.X;
            e.Handled = true;
        };
        PointerMoved += (_, e) =>
        {
            if (_lastX is not { } last) return;
            var x = e.GetCurrentPoint(null).Position.X;
            _lastX = x;
            Dragged?.Invoke(this, x - last);
        };
        PointerReleased += (_, e) => { _lastX = null; ReleasePointerCapture(e.Pointer); };
        PointerCaptureLost += (_, _) => _lastX = null;
    }

    public event EventHandler<double>? Dragged;
}
