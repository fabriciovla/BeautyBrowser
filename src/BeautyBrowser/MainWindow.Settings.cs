using BeautyKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace BeautyBrowser;

// The settings tab (beauty://settings): a regular tab whose page the app covers with its own settings. It opens from
// the library's Settings button, Ctrl+, and Ctrl+Shift+Delete (at the rows that clear data).
public sealed partial class MainWindow
{
    private BrowserTab? _settingsWatched;

    private void InitSettingsTab()
    {
        SettingsPage.Attach(_shell);
        Library.SettingsRequested += (_, _) => OpenSettings();
        _shell.CurrentTabChanged += (_, _) => WatchSettingsTab(_shell.CurrentTab);

        // Ctrl+, as in Chrome, Edge and Firefox. The comma has no VirtualKey name, so it is added here.
        var accelerator = new KeyboardAccelerator { Key = (VirtualKey)188, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            OpenSettings();
        };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>Goes to the settings tab, opening one if there is none in this space.</summary>
    private void OpenSettings(bool revealClearData = false)
    {
        HidePreview();
        if (_fullscreen) _shell.CurrentTab?.ExitFullscreen();
        CloseLibrary(returnToSidebar: false);
        if (_shell.CurrentSpace.Tabs.FirstOrDefault(t => StartPage.IsSettings(t.Url)) is { } existing) _shell.SelectTab(existing);
        else if (StartPage.IsSettings(_shell.CurrentTab?.Url)) { } // already there (a favorite)
        else if (_shell.CurrentTab is { } current && StartPage.Is(current.Url)) current.Navigate(StartPage.SettingsUrl); // reuse an empty new tab
        else _shell.NewTab(StartPage.SettingsUrl);
        UpdateSettingsPage();
        if (revealClearData) SettingsPage.RevealClearData();
    }

    private void WatchSettingsTab(BrowserTab? tab)
    {
        if (_settingsWatched == tab) return;
        if (_settingsWatched is not null) _settingsWatched.PropertyChanged -= SettingsWatched_PropertyChanged;
        _settingsWatched = tab;
        if (tab is not null) tab.PropertyChanged += SettingsWatched_PropertyChanged;
        UpdateSettingsPage();
    }

    private void SettingsWatched_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.Url)) UpdateSettingsPage();
    }

    private void UpdateSettingsPage()
    {
        var show = StartPage.IsSettings(_shell.CurrentTab?.Url);
        if ((SettingsPage.Visibility == Visibility.Visible) == show) return;
        SettingsPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) SettingsPage.Focus(FocusState.Programmatic); // keys stay out of the empty page underneath
    }
}
