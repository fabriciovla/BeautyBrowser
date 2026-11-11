using BeautyKit;
using Microsoft.UI.Xaml;

namespace BeautyBrowser;

// Notices with two choices, in a card at the bottom left of the page: a blocked popup (allow the site for good) and a
// dangerous download (keep or discard). A notice tied to a tab only shows while that tab is the one on screen.
public sealed partial class MainWindow
{
    private sealed record Notice(
        object Key, BrowserTab? Tab, string Glyph, string Text, string? Detail,
        string PrimaryLabel, Action Primary, string SecondaryLabel, Action Secondary);

    private readonly List<Notice> _notices = [];
    private Notice? _shownNotice;

    private void SetupNotices()
    {
        _shell.CurrentTabChanged += (_, _) => ShowNextNotice();

        _shell.PopupBlocked += (_, blocked) => AddNotice(PopupNotice(blocked.Tab, blocked.Origin, blocked.Url));
        _shell.TabCrashChanged += (_, e) =>
        {
            if (e.Crashed) AddNotice(CrashNotice(e.Tab));
            else
            {
                _notices.RemoveAll(n => n.Key.Equals((e.Tab, "crashed")));
                ShowNextNotice();
            }
        };
        _shell.DownloadApprovalRequested += (_, item) => AddNotice(DangerousDownloadNotice(item.Approval));
        _shell.ExternalAppRequested += (_, item) => AddNotice(ExternalAppNotice(item.Tab, item.Request));
    }

    private void AddNotice(Notice notice)
    {
        _notices.RemoveAll(n => n.Key.Equals(notice.Key)); // a newer one of the same kind replaces the old
        _notices.Add(notice);
        ShowNextNotice();
    }

    private void ShowNextNotice()
    {
        _shownNotice = _notices.FirstOrDefault(n => n.Tab is null || n.Tab == _shell.CurrentTab);
        if (_shownNotice is not { } notice)
        {
            NoticeBar.Visibility = Visibility.Collapsed;
            return;
        }
        NoticeIcon.Glyph = notice.Glyph;
        NoticeText.Text = notice.Text;
        NoticeDetail.Text = notice.Detail ?? "";
        NoticeDetail.Visibility = notice.Detail is null ? Visibility.Collapsed : Visibility.Visible;
        NoticePrimary.Content = notice.PrimaryLabel;
        NoticeSecondary.Content = notice.SecondaryLabel;
        NoticeBar.Visibility = Visibility.Visible;
    }

    private void AnswerNotice(bool primary)
    {
        if (_shownNotice is not { } notice) return;
        _notices.Remove(notice);
        _shownNotice = null;
        try
        {
            (primary ? notice.Primary : notice.Secondary)();
        }
        finally
        {
            ShowNextNotice();
        }
    }

    private void NoticePrimary_Click(object sender, RoutedEventArgs e) => AnswerNotice(primary: true);
    private void NoticeSecondary_Click(object sender, RoutedEventArgs e) => AnswerNotice(primary: false);

    private Notice PopupNotice(BrowserTab tab, string origin, string url)
    {
        var site = SiteName(origin) is { Length: > 0 } name ? name : L.T("This page", "Esta página");
        return new Notice(
            Key: (tab, "popup"), Tab: tab, Glyph: "",
            Text: L.T($"{site} tried to open a pop-up window and it was blocked.", $"{site} intentó abrir una ventana emergente y se bloqueó."),
            Detail: null,
            PrimaryLabel: L.T("Always allow", "Permitir siempre"),
            Primary: () =>
            {
                if (origin.Length == 0) return;
                _shell.SitePermissions.Remember(origin, PagePermission.Popups, allowed: true);
                BrowserTab.AllowPopupsFrom(origin);
                // The popup that was blocked opens now, since the user wants it.
                if (url.Length > 0 && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) _shell.NewTab(url);
            },
            SecondaryLabel: L.T("Dismiss", "Cerrar"),
            Secondary: () => { });
    }

    private static Notice CrashNotice(BrowserTab tab) => new(
        Key: (tab, "crashed"), Tab: tab, Glyph: "",
        Text: L.T("This page ran into a problem.", "Esta página tuvo un problema."),
        Detail: null,
        PrimaryLabel: L.T("Reload", "Recargar"), Primary: () => tab.Reload(),
        SecondaryLabel: L.T("Dismiss", "Cerrar"), Secondary: () => { });

    private Notice DangerousDownloadNotice(DownloadApproval approval)
    {
        var site = SiteName(BrowserTab.OriginOf(approval.Url));
        return new Notice(
            Key: approval, Tab: null, Glyph: "",
            Text: L.T($"{approval.FileName} can harm your computer.", $"{approval.FileName} puede dañar tu equipo."),
            Detail: site.Length == 0
                ? L.T("Only keep it if you trust where it came from.", "Conservalo solo si confiás en su origen.")
                : L.T($"Only keep it if you trust {site}.", $"Conservalo solo si confiás en {site}."),
            PrimaryLabel: L.T("Keep", "Conservar"), Primary: () => approval.Decide(true),
            SecondaryLabel: L.T("Discard", "Descartar"), Secondary: () => approval.Decide(false));
    }

    // A link to an app (mailto:, zoommtg:…): Windows opens it only if the user says so, as in Chrome.
    private static Notice ExternalAppNotice(BrowserTab tab, ExternalAppRequest request)
    {
        var site = SiteName(request.Origin);
        return new Notice(
            Key: (tab, "app"), Tab: tab, Glyph: "",
            Text: L.T($"Open {request.App}?", $"¿Abrir {request.App}?"),
            Detail: site.Length == 0 ? null : L.T($"{site} wants to open this app.", $"{site} quiere abrir esta app."),
            PrimaryLabel: L.T("Open", "Abrir"), Primary: () => ExternalApps.Open(request.Url),
            SecondaryLabel: L.T("Cancel", "Cancelar"), Secondary: () => { });
    }

    private static string SiteName(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", "") : "";
}
