using System.ComponentModel;
using BeautyKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace BeautyBrowser;

// A link opened in the background (middle click, Ctrl+click, "Open link in new tab") does not take the user away from
// the page. A small notice says it opened; tapping it goes to that tab, and it goes away by itself.
public sealed partial class MainWindow
{
    private BrowserTab? _toastTab;
    private int _toastCount;
    private DispatcherTimer? _newTabToastTimer;

    private void SetupNewTabToast()
    {
        _shell.BackgroundTabOpened += (_, tab) => ShowNewTabToast(tab);
        _shell.CurrentTabChanged += (_, _) =>
        {
            if (_toastTab is not null && _shell.CurrentTab == _toastTab) HideNewTabToast(); // already there
        };
    }

    private void ShowNewTabToast(BrowserTab tab)
    {
        // Tabs on top: the new tab is seen appearing in the strip, so it just glows for a moment.
        if (TabsOnTop && TopBar.Visibility == Visibility.Visible)
        {
            TopBar.FlashTab(tab);
            return;
        }
        if (_fullscreen) return;
        // Several in a row: one notice that counts them, and a tap goes to the last.
        _toastCount = NewTabToast.Opacity > 0 ? _toastCount + 1 : 1;
        WatchToastTab(tab);
        NewTabToastText.Text = _toastCount == 1
            ? L.T("New tab opened", "Se abrió una pestaña nueva")
            : L.T($"{_toastCount} new tabs opened", $"Se abrieron {_toastCount} pestañas nuevas");
        NewTabToastTitle.Text = tab.Title;
        HideZoomToast();
        NewTabToast.Opacity = 1;
        NewTabToast.IsHitTestVisible = true;

        _newTabToastTimer ??= CreateNewTabToastTimer();
        _newTabToastTimer.Stop();
        _newTabToastTimer.Start();
    }

    // The page's title arrives after the tab is created: the notice starts with its domain and updates.
    private void WatchToastTab(BrowserTab tab)
    {
        if (_toastTab is not null) _toastTab.PropertyChanged -= ToastTab_PropertyChanged;
        _toastTab = tab;
        tab.PropertyChanged += ToastTab_PropertyChanged;
    }

    private void ToastTab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BrowserTab.Title) || sender != _toastTab || NewTabToast.Opacity == 0) return;
        NewTabToastTitle.Text = _toastTab.Title;
    }

    private void HideNewTabToast()
    {
        _newTabToastTimer?.Stop();
        NewTabToast.Opacity = 0;
        NewTabToast.IsHitTestVisible = false;
        if (_toastTab is not null) _toastTab.PropertyChanged -= ToastTab_PropertyChanged;
        _toastTab = null;
    }

    // It stays while the pointer is over it, so it can be tapped.
    private void NewTabToast_PointerEntered(object sender, PointerRoutedEventArgs e) => _newTabToastTimer?.Stop();

    private void NewTabToast_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (NewTabToast.Opacity > 0) _newTabToastTimer?.Start();
    }

    private void NewTabToast_Click(object sender, RoutedEventArgs e)
    {
        var tab = _toastTab;
        HideNewTabToast();
        if (tab is not null) _shell.SelectTab(tab);
    }

    private DispatcherTimer CreateNewTabToastTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => HideNewTabToast();
        return timer;
    }
}
