using BeautyBrowser.Shell;
using BeautyKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BeautyBrowser.Controls;

/// <summary>
/// What a page's "Site settings" flyout shows: its zoom and whether the ad blocker works on this site. Shared by the
/// sidebar and the top bar; call <see cref="Refresh"/> when the flyout opens.
/// </summary>
public sealed partial class SiteSettingsPanel : UserControl
{
    private BrowserShell? _shell;

    public SiteSettingsPanel() => InitializeComponent();

    public void Attach(BrowserShell shell) => _shell = shell;

    public void Refresh()
    {
        RefreshZoom();
        RefreshSiteBlocker();
    }

    private void RefreshSiteBlocker()
    {
        var tab = _shell?.CurrentTab;
        var site = tab is null ? "" : BeautyKit.Blocking.ContentBlocker.SiteOf(tab.Url);
        var enabled = BeautyKit.Blocking.ContentBlocker.Enabled;
        var on = enabled && site.Length > 0 && !BeautyKit.Blocking.ContentBlocker.IsSiteAllowed(tab!.Url);

        SiteBlockerRow.IsEnabled = enabled && site.Length > 0;
        SiteBlockerHint.Text = !enabled ? L.T("Off on every site (Extensions)", "Apagado en todos los sitios (Extensiones)")
            : site.Length == 0 ? L.T("Not used on this page", "No se usa en esta página")
            : on ? L.T($"{tab!.BlockedCount} blocked on this page", $"{tab!.BlockedCount} bloqueados en esta página")
            : L.T($"Off on {site}", $"Apagado en {site}");
        SiteBlockerState.Text = on ? L.T("On", "Activado") : L.T("Off", "Apagado");
        SiteBlockerState.Foreground = (Brush)Application.Current.Resources[on ? "BeautyAccentBrush" : "BeautyTextSecondaryBrush"];
        SiteBlockerIcon.Foreground = SiteBlockerState.Foreground;
    }

    private void SiteBlocker_Click(object sender, RoutedEventArgs e)
    {
        if (_shell?.CurrentTab is not { } tab) return;
        var allowed = BeautyKit.Blocking.ContentBlocker.IsSiteAllowed(tab.Url);
        BeautyKit.Blocking.ContentBlocker.SetSiteAllowed(tab.Url, !allowed);
        RefreshSiteBlocker();
        tab.Reload(); // the change shows right away
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) { _shell?.CurrentTab?.ZoomIn(); RefreshZoom(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { _shell?.CurrentTab?.ZoomOut(); RefreshZoom(); }
    private void ZoomReset_Click(object sender, RoutedEventArgs e) { _shell?.CurrentTab?.ResetZoom(); RefreshZoom(); }

    private void RefreshZoom() =>
        ZoomText.Text = $"{_shell?.CurrentTab?.ZoomPercent ?? BrowserTab.DefaultZoomPercent} %";
}
