using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BeautyBrowser.Controls;

/// <summary>
/// Smooth scrolling (ScrollView, animated on the GPU) can stop at half a pixel, leaving text blurry between two
/// pixels. When scrolling ends, the offset is snapped to the nearest whole pixel without animating.
/// </summary>
internal static class ScrollSnap
{
    private static readonly HashSet<ScrollView> Attached = [];

    /// <summary>Enables snapping on every ScrollView inside <paramref name="root"/>, once each.</summary>
    public static void AttachAll(DependencyObject root)
    {
        if (root is ScrollView scroll && Attached.Add(scroll)) scroll.StateChanged += OnStateChanged;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) AttachAll(VisualTreeHelper.GetChild(root, i));
    }

    private static void OnStateChanged(ScrollView scroll, object args)
    {
        if (scroll.State != ScrollingInteractionState.Idle || scroll.XamlRoot is null) return;
        var scale = scroll.XamlRoot.RasterizationScale;
        var snapped = Math.Round(scroll.VerticalOffset * scale) / scale;
        if (Math.Abs(snapped - scroll.VerticalOffset) < 0.01) return;
        scroll.ScrollTo(scroll.HorizontalOffset, snapped, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
    }
}
