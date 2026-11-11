using BeautyBrowser.Services;
using BeautyKit;
using Microsoft.UI.Xaml;

namespace BeautyBrowser;

// Updates: when a new version is downloaded a card says so; it installs when the browser closes, or right away with
// "Restart now" (see UpdateService and Shutdown).
public sealed partial class MainWindow
{
    private readonly UpdateService _updates;

    private void SetupUpdates()
    {
        _updates.UpdateReady += (_, version) =>
        {
            UpdateTitle.Text = L.T($"Beauty Browser {version} is ready", $"Beauty Browser {version} está lista");
            UpdateBar.Visibility = Visibility.Visible;
        };
        _updates.Start();
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateBar.Visibility = Visibility.Collapsed;

    // Closes like a restart: the session is saved and the installer opens the browser again when it is done.
    private void UpdateRestart_Click(object sender, RoutedEventArgs e) => App.Restart();
}
