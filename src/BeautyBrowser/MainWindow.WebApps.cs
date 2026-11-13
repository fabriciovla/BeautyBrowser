using BeautyBrowser.Services;
using BeautyKit;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BeautyBrowser;

// Installed web apps (WebAppWindow): opening them, installing from a tab, and keeping the browser alive (hidden)
// while app windows are open, as Chrome does.
public sealed partial class MainWindow
{
    private bool _browserShown;
    private bool _quitting;

    private void SetupWebApps()
    {
        Activated += (_, _) => _browserShown = true;
        _shell.BrowserWindowRequested += (_, _) => ShowBrowserWindow();
        _shell.InstallAppRequested += async (_, tab) => await InstallAppAsync(tab);
        _shell.OpenAppRequested += (_, id) => OpenWebApp(id);

        // Closing the browser while apps are open only hides it; the last app window closing ends everything if the
        // browser is not on screen.
        AppWindow.Closing += (_, e) =>
        {
            if (_quitting || WebAppWindow.Open.Count == 0) return;
            e.Cancel = true;
            _browserShown = false;
            AppWindow.Hide();
        };
        WebAppWindow.LastClosed += (_, _) =>
        {
            if (_browserShown && AppWindow.IsVisible) return;
            _quitting = true;
            Close();
        };
    }

    /// <summary>Messages from another BeautyBrowser started by a shortcut or the taskbar.</summary>
    private void OnInstanceCommand(string message)
    {
        if (message.StartsWith(ExternalLinks.AppMessage, StringComparison.Ordinal))
        {
            if (!OpenWebApp(message[ExternalLinks.AppMessage.Length..])) ShowBrowserWindow();
        }
        else ShowBrowserWindow();
    }

    /// <summary>Opens (or brings forward) an installed app's window. False when there is no such app.</summary>
    public bool OpenWebApp(string id, string? url = null)
    {
        if (_shell.WebApps.Find(id) is not { } app) return false;
        if (url is null && WebAppWindow.Open.FirstOrDefault(w => w.App == app) is { } existing)
        {
            existing.Bring();
            return true;
        }
        var window = new WebAppWindow(_shell, app, url);
        window.Activate();
        return true;
    }

    private void ShowBrowserWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        AppWindow.Show();
        Activate();
    }

    /// <summary>Asks before installing, as Chrome does: the icon and an editable name. Null when cancelled.</summary>
    private async Task<string?> ConfirmInstallAsync(string name, string iconPath)
    {
        var box = new TextBox { Text = name, MinWidth = 300, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, "InstallAppName");
        var icon = new Image
        {
            Width = 48,
            Height = 48,
            Source = new BitmapImage(new Uri(iconPath)) { CreateOptions = BitmapCreateOptions.IgnoreImageCache },
        };
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { icon, box } });
        content.Children.Add(new TextBlock
        {
            Text = L.T("It opens in its own window, with its icon on the taskbar, in the Start menu and on the desktop.",
                "Se abre en su propia ventana, con su ícono en la barra de tareas, en Inicio y en el escritorio."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            MaxWidth = 380,
        });
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = L.T("Install app", "Instalar app"),
            Content = content,
            PrimaryButtonText = L.T("Install", "Instalar"),
            CloseButtonText = L.T("Cancel", "Cancelar"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = AppTheme.IsDark ? ElementTheme.Dark : ElementTheme.Light,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    /// <summary>"Install as app" on a tab: installs it, moves the page into the app's window and closes the tab.</summary>
    private async Task InstallAppAsync(BrowserTab tab)
    {
        if (_shell.WebApps.ForUrl(tab.Url) is { } installed)
        {
            OpenWebApp(installed.Id, tab.Url);
            return;
        }
        var (app, failed) = await _shell.WebApps.InstallAsync(tab, ConfirmInstallAsync);
        if (app is null && !failed) return; // cancelled
        if (app is null)
        {
            AddNotice(new Notice(("install-app", tab), tab, "\uE783",
                L.T("This page can't be installed as an app", "Esta página no se puede instalar como app"),
                L.T("Only regular web pages (http or https) outside the private space can be installed.",
                    "Solo se pueden instalar páginas web comunes (http o https) fuera del espacio privado."),
                L.T("OK", "Entendido"), () => { }, L.T("Dismiss", "Cerrar"), () => { }));
            return;
        }
        var url = tab.Url;
        _shell.CloseTab(tab);
        OpenWebApp(app.Id, url);
    }
}
