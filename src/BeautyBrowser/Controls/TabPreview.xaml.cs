using BeautyBrowser.Shell;
using BeautyKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace BeautyBrowser.Controls;

/// <summary>The tab preview card. <see cref="MainWindow"/> positions and shows it.</summary>
public sealed partial class TabPreview : UserControl
{
    public const double CardWidth = 300;
    private const double ThumbnailHeight = 168;

    private BrowserShell? _shell;

    public TabPreview()
    {
        InitializeComponent();
    }

    public BrowserTab? Tab { get; private set; }

    /// <summary>An action changed something (duplicate, favorites), so the window closes the preview.</summary>
    public event EventHandler? ActionInvoked;

    public void Attach(BrowserShell shell) => _shell = shell;

    public void Show(BrowserTab tab)
    {
        Tab = tab;
        TitleText.Text = tab.Title;
        SiteText.Text = Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? HostName.WithoutWww(uri.Host)
            : tab.Url;

        var scale = XamlRoot?.RasterizationScale ?? 1;
        var thumbnail = tab.CaptureThumbnail((int)Math.Round(CardWidth * scale), (int)Math.Round(ThumbnailHeight * scale));
        Thumbnail.Source = thumbnail;
        EmptyIcon.Source = thumbnail is null ? tab.Favicon : null;

        var pinned = _shell?.Favorites.Contains(tab.Url) == true;
        PinIcon.Glyph = pinned ? "" : "";
        ToolTipService.SetToolTip(PinButton, pinned ? L.T("Remove from favorites", "Quitar de favoritos") : L.T("Add to favorites", "Agregar a favoritos"));
        MuteIcon.Glyph = tab.IsAudioMuted ? "" : "";
        ToolTipService.SetToolTip(MuteButton, tab.IsAudioMuted ? L.T("Unmute", "Activar sonido") : L.T("Mute tab", "Silenciar pestaña"));
        CopyIcon.Glyph = "";
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null || _shell is null) return;
        if (_shell.Favorites.Contains(Tab.Url)) _shell.Unpin(Tab.Url);
        else _shell.Pin(Tab);
        ActionInvoked?.Invoke(this, EventArgs.Empty);
    }

    private async void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null) return;
        var package = new DataPackage();
        package.SetText(Tab.Url);
        Clipboard.SetContent(package);
        CopyIcon.Glyph = ""; // a check mark for a moment
        await Task.Delay(1200);
        CopyIcon.Glyph = "";
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null || _shell is null) return;
        _shell.DuplicateTab(Tab);
        ActionInvoked?.Invoke(this, EventArgs.Empty);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null) return;
        Tab.SetAudioMuted(!Tab.IsAudioMuted);
        MuteIcon.Glyph = Tab.IsAudioMuted ? "" : "";
        ToolTipService.SetToolTip(MuteButton, Tab.IsAudioMuted ? L.T("Unmute", "Activar sonido") : L.T("Mute tab", "Silenciar pestaña"));
    }
}
