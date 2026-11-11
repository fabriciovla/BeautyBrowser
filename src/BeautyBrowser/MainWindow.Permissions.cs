using BeautyKit;
using Microsoft.UI.Xaml;

namespace BeautyBrowser;

// Site permissions (microphone, camera, location, notifications…): a card over the page asks, as Chrome does, and the
// answer is remembered for the site. Requests are asked one at a time.
public sealed partial class MainWindow
{
    private readonly Queue<(BrowserTab Tab, PermissionRequest Request)> _permissionQueue = [];
    private PermissionRequest? _shownPermission;

    private void SetupPermissions()
    {
        _shell.PermissionRequested += (_, item) =>
        {
            if (_shell.SitePermissions.Decided(item.Request.Origin, item.Request.Permissions) is { } allowed)
            {
                item.Request.Respond(allowed);
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
            PermissionText.Text = PermissionMessage(item.Request);
            PermissionIcon.Glyph = PermissionGlyph(item.Request.Permissions);
            PermissionBar.Visibility = Visibility.Visible;
            return;
        }
        PermissionBar.Visibility = Visibility.Collapsed;
    }

    private void AnswerPermission(bool allow)
    {
        if (_shownPermission is { } request)
        {
            _shell.SitePermissions.Remember(request.Origin, request.Permissions, allow);
            request.Respond(allow);
        }
        ShowNextPermission();
    }

    private void PermissionAllow_Click(object sender, RoutedEventArgs e) => AnswerPermission(allow: true);
    private void PermissionBlock_Click(object sender, RoutedEventArgs e) => AnswerPermission(allow: false);

    private static string PermissionMessage(PermissionRequest request)
    {
        var site = Uri.TryCreate(request.Origin, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", "") : request.Origin;
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
