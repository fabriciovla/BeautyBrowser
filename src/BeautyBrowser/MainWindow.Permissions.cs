using BeautyKit;
using Microsoft.UI.Xaml;

namespace BeautyBrowser;

// Site permissions (microphone, camera, location, notifications…): a card over the page asks, as Chrome does, and the
// answer is remembered for the site. Requests are asked one at a time.
public sealed partial class MainWindow
{
    private readonly Queue<(BrowserTab Tab, PermissionRequest Request)> _permissionQueue = [];
    private PermissionRequest? _shownPermission;
    private bool _shownIsPrivate;
    private BrowserTab? _shownTab;

    // "Only this time": what each tab was allowed, per site, until the tab closes. Nothing is written to disk.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BrowserTab, Dictionary<string, PagePermission>> _allowedOnce = [];

    private void SetupPermissions()
    {
        _shell.PermissionRequested += (_, item) =>
        {
            // Private tabs neither use nor leave decisions: the saved list would record the sites visited.
            if (!item.Tab.IsPrivate && _shell.SitePermissions.Decided(item.Request.Origin, item.Request.Permissions) is { } allowed)
            {
                item.Request.Respond(allowed);
                return;
            }
            // A call asks for the camera more than once: "only this time" covers the whole stay in that tab.
            if (_allowedOnce.TryGetValue(item.Tab, out var once) && once.TryGetValue(SiteKey(item.Request.Origin), out var granted) &&
                (granted & item.Request.Permissions) == item.Request.Permissions)
            {
                item.Request.Respond(true);
                return;
            }
            item.Request.Dismissed += (_, _) => { if (_shownPermission == item.Request) ShowNextPermission(); };
            _permissionQueue.Enqueue(item);
            if (_shownPermission is null) ShowNextPermission();
        };
    }

    private void ShowNextPermission()
    {
        _shownPermission = null;
        while (_permissionQueue.TryDequeue(out var item))
        {
            if (item.Request.IsAnswered) continue; // dismissed meanwhile (navigated away, tab closed)
            _shownPermission = item.Request;
            _shownIsPrivate = item.Tab.IsPrivate;
            _shownTab = item.Tab;
            PermissionText.Text = PermissionMessage(item.Request);
            PermissionIcon.Glyph = PermissionGlyph(item.Request.Permissions);
            // "Only this time" is a choice where "Allow" is for good: not in a private tab nor for what is asked every time.
            PermissionOnce.Visibility = !_shownIsPrivate && Services.SitePermissionsService.Remembers(item.Request.Permissions)
                ? Visibility.Visible
                : Visibility.Collapsed;
            PermissionBar.Visibility = Visibility.Visible;
            return;
        }
        _shownTab = null;
        PermissionBar.Visibility = Visibility.Collapsed;
    }

    /// <param name="remember">Keep the answer for the site; without it, a yes lasts while the tab is open and a no is only for now.</param>
    private void AnswerPermission(bool allow, bool remember = true)
    {
        if (_shownPermission is { } request)
        {
            if (remember && !_shownIsPrivate) _shell.SitePermissions.Remember(request.Origin, request.Permissions, allow);
            else if (allow && _shownTab is { } tab)
            {
                // Also "Allow" in a private tab, which keeps nothing: the yes lasts while that tab is open.
                var once = _allowedOnce.GetOrCreateValue(tab);
                var site = SiteKey(request.Origin);
                once[site] = once.GetValueOrDefault(site) | Services.SitePermissionsService.Kept(request.Permissions);
            }
            request.Respond(allow);
        }
        ShowNextPermission();
    }

    // The same site with or without the final slash.
    private static string SiteKey(string origin) => origin.TrimEnd('/').ToLowerInvariant();

    private void PermissionAllow_Click(object sender, RoutedEventArgs e) => AnswerPermission(allow: true);
    private void PermissionBlock_Click(object sender, RoutedEventArgs e) => AnswerPermission(allow: false);
    private void PermissionOnce_Click(object sender, RoutedEventArgs e) => AnswerPermission(allow: true, remember: false);

    // The X: not now. The site gets a no, and it can ask again another time.
    private void PermissionDismiss_Click(object sender, RoutedEventArgs e) => AnswerPermission(allow: false, remember: false);

    private static string PermissionMessage(PermissionRequest request)
    {
        var site = Uri.TryCreate(request.Origin, UriKind.Absolute, out var uri) ? HostName.WithoutWww(uri.Host) : request.Origin;
        var what = request.Permissions
            .ToString()
            .Split(", ")
            .Select(Enum.Parse<PagePermission>)
            .Select(p => p switch
            {
                PagePermission.Microphone => L.T("use your microphone", "usar tu micrófono"),
                PagePermission.Camera => L.T("use your camera", "usar tu cámara"),
                PagePermission.ScreenShare => L.T("share your screen", "compartir tu pantalla"),
                PagePermission.Notifications => L.T("show notifications", "mostrar notificaciones"),
                PagePermission.Location => L.T("know your location", "saber tu ubicación"),
                PagePermission.Clipboard => L.T("see text and images copied to the clipboard", "ver lo que copiaste al portapapeles"),
                PagePermission.Midi => L.T("use your MIDI devices", "usar tus dispositivos MIDI"),
                _ => L.T("use a feature of your device", "usar una función de tu dispositivo"),
            })
            .ToList();
        var list = what.Count switch
        {
            1 => what[0],
            _ => string.Join(", ", what[..^1]) + L.T(" and ", " y ") + what[^1],
        };
        return L.T($"{site} wants to {list}", $"{site} quiere {list}");
    }

    private static string PermissionGlyph(PagePermission permissions) =>
        permissions.HasFlag(PagePermission.Camera) ? ""          // video
        : permissions.HasFlag(PagePermission.Microphone) ? ""    // microphone
        : permissions.HasFlag(PagePermission.ScreenShare) ? ""   // screen
        : permissions.HasFlag(PagePermission.Notifications) ? "" // bell
        : permissions.HasFlag(PagePermission.Location) ? ""      // location
        : "";                                                    // lock
}
