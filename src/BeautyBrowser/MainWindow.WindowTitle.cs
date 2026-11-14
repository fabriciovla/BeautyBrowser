using System.ComponentModel;
using BeautyKit;

namespace BeautyBrowser;

// The window's title (taskbar, Alt+Tab): "<page title> — Beauty Browser". The private space shows only the app's name, so
// what is being read does not show up in the taskbar or in a screen share.
public sealed partial class MainWindow
{
    private const string AppName = "Beauty Browser";
    private BrowserTab? _titleWatched;

    private void SetupWindowTitle()
    {
        _shell.CurrentTabChanged += (_, _) => WatchTitle();
        _shell.CurrentSpaceChanged += (_, _) => WatchTitle();
        WatchTitle();
    }

    private void WatchTitle()
    {
        var tab = _shell.CurrentTab;
        if (_titleWatched != tab)
        {
            if (_titleWatched is not null) _titleWatched.PropertyChanged -= TitleWatched_PropertyChanged;
            _titleWatched = tab;
            if (tab is not null) tab.PropertyChanged += TitleWatched_PropertyChanged;
        }
        UpdateWindowTitle();
    }

    private void TitleWatched_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.Title)) UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        var tab = _shell.CurrentTab;
        var title = tab is null || tab.IsPrivate || _shell.CurrentSpace.IsPrivate || string.IsNullOrWhiteSpace(tab.Title)
            ? AppName
            : $"{tab.Title.Trim()} — {AppName}";
        if (Title != title) Title = title;
    }
}
