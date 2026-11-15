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
        _shell.TabsArchived += (_, count) =>
        {
            _archivedUnseen += count; // a notice still on screen counts the new ones too
            AddNotice(ArchivedNotice(_archivedUnseen));
        };
        // "Clear" closes many tabs with one click: the notice at the top offers to bring them back.
        _shell.SpaceCleared += (_, cleared) => ShowInfoToast(
            cleared.Count == 1 ? L.T("1 tab closed", "Se cerró 1 pestaña") : L.T($"{cleared.Count} tabs closed", $"Se cerraron {cleared.Count} pestañas"),
            L.T("Tap to undo", "Tocá para deshacer"), "\uE7A7",
            () => _shell.UndoClear(cleared.Space, cleared.Count), seconds: 8);
        _shell.ExternalAppRequested += (_, item) =>
        {
            // While one is pending the new request is ignored: a page could swap the app right before the click.
            if (_notices.Any(n => n.Key.Equals((item.Tab, "app")))) return;
            AddNotice(ExternalAppNotice(item.Tab, item.Request));
        };
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
                if (!tab.IsPrivate) _shell.SitePermissions.Remember(origin, PagePermission.Popups, allowed: true); // private: this session only
                BrowserTab.AllowPopupsFrom(origin);
                // The popup that was blocked opens now, since the user wants it.
                // Only web addresses: a data: page would show content the site wrote, with no address of its own.
                if (Uri.TryCreate(url, UriKind.Absolute, out var target) && target.Scheme is "http" or "https") _shell.NewTab(url);
            },
            SecondaryLabel: L.T("Dismiss", "Cerrar"),
            Secondary: () => { });
    }

    private int _archivedUnseen;

    // Tabs nobody used went to the library on their own: say so, or they seem to have vanished.
    private Notice ArchivedNotice(int count) => new(
        Key: "archived", Tab: null, Glyph: "\uE7B8",
        Text: count == 1
            ? L.T("1 tab you were not using was archived.", "Se archivó 1 pestaña que no usabas.")
            : L.T($"{count} tabs you were not using were archived.", $"Se archivaron {count} pestañas que no usabas."),
        Detail: L.T("They are in the library, under Archived. Settings lets you change when this happens.",
            "Están en la biblioteca, en Archivadas. En Ajustes podés cambiar cuándo pasa."),
        PrimaryLabel: L.T("See them", "Verlas"),
        Primary: () => { _archivedUnseen = 0; OpenLibrary(Controls.LibrarySection.Archived); },
        SecondaryLabel: L.T("Dismiss", "Cerrar"),
        Secondary: () => _archivedUnseen = 0);

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
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? HostName.WithoutWww(uri.Host) : "";
}
