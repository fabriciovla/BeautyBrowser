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

    private BrowserTab? _watched;

    public void Attach(BrowserShell shell)
    {
        _shell = shell;
        Unloaded += (_, _) => Watch(null);
    }

    public void Refresh()
    {
        Watch(_shell?.CurrentTab); // the counter keeps going up while the flyout is open
        RefreshZoom();
        RefreshSiteBlocker();
    }

    private void Watch(BrowserTab? tab)
    {
        if (_watched == tab) return;
        if (_watched is not null) _watched.PropertyChanged -= Tab_PropertyChanged;
        _watched = tab;
        if (tab is not null) tab.PropertyChanged += Tab_PropertyChanged;
    }

    private void Tab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.BlockedCount)) RefreshCount();
    }

    private void RefreshCount()
    {
        var count = _watched?.BlockedCount ?? 0;
        BlockedNumber.Text = count.ToString("#,0");
        BlockedLabel.Text = count == 1
            ? L.T("blocked on this page", "bloqueado en esta página")
            : L.T("blocked on this page", "bloqueados en esta página");
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
            : on ? L.T($"On {site}", $"Activo en {site}")
            : L.T($"Off on {site}", $"Apagado en {site}");
        RefreshCount();
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
