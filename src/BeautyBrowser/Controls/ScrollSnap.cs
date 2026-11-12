using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BeautyBrowser.Controls;

/// <summary>
/// Smooth scrolling (ScrollView, animated on the GPU) can stop at half a pixel, leaving text blurry between two
/// pixels. When scrolling ends, the offset is snapped to the nearest whole pixel without animating.
/// It also gives the mouse wheel Chrome's step: the ScrollView moves very little per click, so the wheel is handled here
/// (3 lines of 33 px, or the Windows setting, per click, animated). Touchpad deltas are applied as they come.
/// </summary>
internal static class ScrollSnap
{
    private static readonly HashSet<ScrollView> Attached = [];

    /// <summary>Enables snapping on every ScrollView inside <paramref name="root"/>, once each.</summary>
    public static void AttachAll(DependencyObject root)
    {
        if (root is ScrollView scroll && Attached.Add(scroll))
        {
            scroll.StateChanged += OnStateChanged;
            scroll.IgnoredInputKinds |= ScrollingInputKinds.MouseWheel; // handled in OnWheel
            scroll.PointerWheelChanged += OnWheel;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) AttachAll(VisualTreeHelper.GetChild(root, i));
    }

    private const double LinePixels = 100.0 / 3; // Chrome: 3 lines = 100 px per click

    private static void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var scroll = (ScrollView)sender;
        var properties = e.GetCurrentPoint(scroll).Properties;
        if (properties.IsHorizontalMouseWheel || scroll.ScrollableHeight <= 0) return;

        var delta = properties.MouseWheelDelta; // 120 per click; precision touchpads send smaller steps
        var lines = WheelScrollLines();
        var pixels = lines == uint.MaxValue ? scroll.ViewportHeight * 0.9 * delta / 120 : delta / 120.0 * lines * LinePixels;
        var click = delta % 120 == 0;
        scroll.ScrollBy(0, -pixels, new ScrollingScrollOptions(click ? ScrollingAnimationMode.Enabled : ScrollingAnimationMode.Disabled));
        e.Handled = true;
    }

    private static uint WheelScrollLines() =>
        SystemParametersInfo(SPI_GETWHEELSCROLLLINES, 0, out var lines, 0) && lines > 0 ? lines : 3;

    private const uint SPI_GETWHEELSCROLLLINES = 0x0068;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, out uint value, uint winIni);

    private static void OnStateChanged(ScrollView scroll, object args)
    {
        if (scroll.State != ScrollingInteractionState.Idle || scroll.XamlRoot is null) return;
        var scale = scroll.XamlRoot.RasterizationScale;
        var snapped = Math.Round(scroll.VerticalOffset * scale) / scale;
        if (Math.Abs(snapped - scroll.VerticalOffset) < 0.01) return;
        scroll.ScrollTo(scroll.HorizontalOffset, snapped, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
    }
}
