using System.Diagnostics;
using BeautyBrowser.Services;
using BeautyKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace BeautyBrowser.Controls;

public sealed partial class DownloadsPanel : UserControl
{
    private readonly DownloadsService _downloads;

    public DownloadsPanel(DownloadsService downloads)
    {
        _downloads = downloads;
        InitializeComponent();

        FolderText.Text = BrowserTab.DownloadsDirectory;
        List.ItemsSource = downloads.Items;
        downloads.Items.CollectionChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();
    }

    private void UpdateEmpty()
    {
        var empty = _downloads.Items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Item_Click(object sender, RoutedEventArgs e)
    {
        // The download goes in Tag: ItemsRepeater with x:Bind does not set DataContext.
        if ((sender as FrameworkElement)?.Tag is DownloadEntry { IsComplete: true } entry) OpenFile(entry.FullPath);
    }

    private void Item_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DownloadEntry entry) return;

        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = L.T("Open", "Abrir"), IsEnabled = entry.IsComplete };
        open.Click += (_, _) => OpenFile(entry.FullPath);
        var show = new MenuFlyoutItem { Text = L.T("Show in folder", "Mostrar en la carpeta") };
        show.Click += (_, _) => ShowInFolder(entry.FullPath);
        var remove = new MenuFlyoutItem { Text = L.T("Remove from list", "Quitar de la lista") };
        remove.Click += (_, _) => { _downloads.Items.Remove(entry); _downloads.Save(); };

        menu.Items.Add(open);
        menu.Items.Add(show);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(remove);
        menu.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
        e.Handled = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Start("explorer.exe", $"\"{BrowserTab.DownloadsDirectory}\"");

    private static void OpenFile(string path)
    {
        if (File.Exists(path)) Start(path);
    }

    private static void ShowInFolder(string path) =>
        Start("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"");

    private static void Start(string fileName, string arguments = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
        }
        catch
        {
            // Deleted file or no associated program: nothing useful to do.
        }
    }
}
