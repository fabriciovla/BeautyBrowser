using System.ComponentModel;
using BeautyKit;
using Microsoft.UI.Xaml;

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
        ZoomToast.Opacity = 0; // another tab: its zoom is not news
    }

    private void ZoomWatched_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BrowserTab.ZoomPercent) || sender is not BrowserTab tab) return;
        ZoomToastText.Text = $"{tab.ZoomPercent} %";
        ZoomToast.Opacity = 1;

        _zoomToastTimer ??= CreateZoomToastTimer();
        _zoomToastTimer.Stop();
        _zoomToastTimer.Start();
    }

    private DispatcherTimer CreateZoomToastTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ZoomToast.Opacity = 0;
        };
        return timer;
    }
}
