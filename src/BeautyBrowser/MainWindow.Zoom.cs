using System.ComponentModel;
using BeautyKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace BeautyBrowser;

// A small "125 %" over the page while the zoom changes, so zooming is not done blind. The sidebar keeps showing it
// while it is not the default one.
public sealed partial class MainWindow
{
    private BrowserTab? _zoomWatched;
    private DispatcherTimer? _zoomToastTimer;

    private void SetupZoomToast()
    {
        _shell.CurrentTabChanged += (_, _) => WatchZoom(_shell.CurrentTab);
        WatchZoom(_shell.CurrentTab);
    }

    private void WatchZoom(BrowserTab? tab)
    {
        if (_zoomWatched == tab) return;
        if (_zoomWatched is not null) _zoomWatched.PropertyChanged -= ZoomWatched_PropertyChanged;
        _zoomWatched = tab;
        if (tab is not null) tab.PropertyChanged += ZoomWatched_PropertyChanged;
        HideZoomToast(); // another tab: its zoom is not news
    }

    private void HideZoomToast()
    {
        _zoomToastTimer?.Stop();
        ZoomToast.Opacity = 0;
        ZoomToast.IsHitTestVisible = false;
    }

    // The toast stays while the pointer is over it, so the button can be reached.
    private void ZoomToast_PointerEntered(object sender, PointerRoutedEventArgs e) => _zoomToastTimer?.Stop();

    private void ZoomToast_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ZoomToast.Opacity > 0) _zoomToastTimer?.Start();
    }

    private void ZoomToastReset_Click(object sender, RoutedEventArgs e) => _zoomWatched?.ResetZoom();

    private void ZoomWatched_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BrowserTab.ZoomPercent) || sender is not BrowserTab tab) return;
        ZoomToastText.Text = $"{tab.ZoomPercent} %";
        ZoomToastReset.Content = L.T("Reset", "Restablecer");
        ZoomToastReset.Visibility = tab.ZoomPercent == BrowserTab.DefaultZoomPercent ? Visibility.Collapsed : Visibility.Visible;
        ZoomToast.Opacity = 1;
        ZoomToast.IsHitTestVisible = true;

        _zoomToastTimer ??= CreateZoomToastTimer();
        _zoomToastTimer.Stop();
        _zoomToastTimer.Start();
    }

    private DispatcherTimer CreateZoomToastTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) => HideZoomToast();
        return timer;
    }
}
