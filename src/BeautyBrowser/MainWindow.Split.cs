using BeautyKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace BeautyBrowser;

// Split view: two tabs side by side, as in Arc, Zen and Edge. The current tab is always in WebView (find, zoom,
// passwords… keep working on it) and its partner in SplitView; each tab keeps its side, so clicking the other pane only
// moves the accent ring. The divider is dragged; a pane is never narrower than a fifth of the page.
public sealed partial class MainWindow
{
    private const double SplitGap = 8;
    private double _splitRatio = 0.5; // the left pane's share

    private void SetupSplit()
    {
        _shell.SplitChanged += (_, _) => UpdateSplit();
        _shell.FullscreenChanged += (_, _) => UpdateSplit(); // a video in full screen takes the whole window
        SplitHandle.Dragged += (_, dx) =>
        {
            var width = Panes.ActualWidth - SplitGap;
            if (width < 1) return;
            _splitRatio = Math.Clamp(_splitRatio + dx / width, 0.2, 0.8);
            ApplySplitWidths(true);
        };
        // A click in the other pane makes its tab the current one (the address bar, shortcuts and find follow it).
        SplitView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            if (SplitView.Tab is { } tab && tab != _shell.CurrentTab) _shell.SelectTab(tab);
        }), handledEventsToo: true);
    }

    /// <summary>Before WebView takes a new tab: if SplitView has it, let go of it (a tab is shown in one view only).</summary>
    private void ReleaseSplitTab()
    {
        if (SplitView.Tab is { } shown && shown == _shell.CurrentTab) SplitView.Tab = null;
    }

    private void UpdateSplit()
    {
        var current = _shell.CurrentTab;
        if (_fullscreen || _shell.SplitOf(current) is not { } pair || current is null)
        {
            SplitView.Tab = null;
            SplitView.Visibility = SplitHandle.Visibility = ActivePaneRing.Visibility = Visibility.Collapsed;
            Grid.SetColumn(WebView, 0);
            ApplySplitWidths(false);
            return;
        }

        var currentIsLeft = pair.Left == current;
        var partner = currentIsLeft ? pair.Right : pair.Left;
        Grid.SetColumn(WebView, currentIsLeft ? 0 : 2);
        Grid.SetColumn(SplitView, currentIsLeft ? 2 : 0);
        Grid.SetColumn(ActivePaneRing, currentIsLeft ? 0 : 2);
        if (SplitView.Tab != partner) SplitView.Tab = partner;
        SplitView.Visibility = SplitHandle.Visibility = ActivePaneRing.Visibility = Visibility.Visible;
        ApplySplitWidths(true);
    }

    private void ApplySplitWidths(bool split)
    {
        LeftPane.Width = new GridLength(split ? _splitRatio : 1, GridUnitType.Star);
        PaneGap.Width = new GridLength(split ? SplitGap : 0);
        RightPane.Width = split ? new GridLength(1 - _splitRatio, GridUnitType.Star) : new GridLength(0);
    }
}
