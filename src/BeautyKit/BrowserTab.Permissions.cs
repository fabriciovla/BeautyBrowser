using CefSharp;

namespace BeautyKit;

/// <summary>What a page asks to use. Several can come in one request (camera and microphone for a call).</summary>
[Flags]
public enum PagePermission
{
    None = 0,
    Microphone = 1,
    Camera = 2,
    ScreenShare = 4,
    Notifications = 8,
    Location = 16,
    Clipboard = 32,
    Midi = 64,
    Other = 128,

    /// <summary>Opening popup windows without a click. Not asked by the page: offered after one was blocked.</summary>
    Popups = 256,
}

/// <summary>
/// A page asks for a permission. The app asks the user (or answers from what was decided before for that site) and
/// calls <see cref="Respond"/> exactly once.
/// </summary>
public sealed class PermissionRequest
{
    private readonly Action<bool> _respond;
    private bool _answered;

    internal PermissionRequest(string origin, PagePermission permissions, Action<bool> respond)
    {
        Origin = origin;
        Permissions = permissions;
        _respond = respond;
    }

    /// <summary>The site asking, such as "https://web.whatsapp.com".</summary>
    public string Origin { get; }

    public PagePermission Permissions { get; }

    /// <summary>The request ended without an answer (navigated away, tab closed): the app hides its prompt.</summary>
    public event EventHandler? Dismissed;

    public bool IsAnswered => _answered;

    public void Respond(bool allow)
    {
        if (_answered) return;
        _answered = true;
        _respond(allow);
    }

    internal void Dismiss()
    {
        if (_answered) return;
        _answered = true;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed partial class BrowserTab
{
    /// <summary>
    /// The page asks for the microphone, camera, location, notifications… Without a listener every request is denied,
    /// which is what Chromium does on its own here (it has no prompt of its own when embedded).
    /// </summary>
    public event EventHandler<PermissionRequest>? PermissionRequested;

    private readonly Dictionary<ulong, PermissionRequest> _prompts = [];
    private readonly List<PermissionRequest> _openRequests = [];

    private void RequestPermission(string origin, PagePermission permissions, Action<bool> respond, ulong? promptId = null)
    {
        OnUi(() =>
        {
            PermissionRequest? request = null;
            request = new PermissionRequest(origin, permissions, allow =>
            {
                _openRequests.Remove(request!);
                if (_disposed) return;
                try { respond(allow); }
                catch { /* the page or its browser went away meanwhile */ }
            });
            _openRequests.Add(request);
            if (promptId is { } id) _prompts[id] = request;
            if (PermissionRequested is { } handler) handler(this, request);
            else request.Respond(false);
        });
    }

    /// <summary>The tab closed: whatever it was still asking goes away with it.</summary>
    private void DismissPermissionRequests()
    {
        foreach (var request in _openRequests.ToList()) request.Dismiss();
        _openRequests.Clear();
        _prompts.Clear();
    }

    private sealed class TabPermissionHandler(BrowserTab tab) : CefSharp.Handler.PermissionHandler
    {
        // Camera, microphone and screen sharing (getUserMedia, getDisplayMedia).
        protected override bool OnRequestMediaAccessPermission(IWebBrowser chromiumWebBrowser, IBrowser browser,
            IFrame frame, string requestingOrigin, MediaAccessPermissionType requestedPermissions, IMediaAccessCallback callback)
        {
            var permissions = PagePermission.None;
            if (requestedPermissions.HasFlag(MediaAccessPermissionType.AudioCapture)) permissions |= PagePermission.Microphone;
            if (requestedPermissions.HasFlag(MediaAccessPermissionType.VideoCapture)) permissions |= PagePermission.Camera;
            if (requestedPermissions.HasFlag(MediaAccessPermissionType.DesktopAudioCapture) ||
                requestedPermissions.HasFlag(MediaAccessPermissionType.DesktopVideoCapture)) permissions |= PagePermission.ScreenShare;
            if (permissions == PagePermission.None) return false;

            tab.RequestPermission(requestingOrigin, permissions, allow =>
            {
                using (callback)
                {
                    if (allow) callback.Continue(requestedPermissions);
                    else callback.Cancel();
                }
            });
            return true;
        }

        // Everything else: notifications, location, clipboard…
        protected override bool OnShowPermissionPrompt(IWebBrowser chromiumWebBrowser, IBrowser browser, ulong promptId,
            string requestingOrigin, PermissionRequestType requestedPermissions, IPermissionPromptCallback callback)
        {
            var permissions = PagePermission.None;
            if (requestedPermissions.HasFlag(PermissionRequestType.Notifications)) permissions |= PagePermission.Notifications;
            if (requestedPermissions.HasFlag(PermissionRequestType.Geolocation)) permissions |= PagePermission.Location;
            if (requestedPermissions.HasFlag(PermissionRequestType.Clipboard)) permissions |= PagePermission.Clipboard;
            if (requestedPermissions.HasFlag(PermissionRequestType.MidiSysex)) permissions |= PagePermission.Midi;
            if (requestedPermissions.HasFlag(PermissionRequestType.CameraStream)) permissions |= PagePermission.Camera;
            if (requestedPermissions.HasFlag(PermissionRequestType.MicStream)) permissions |= PagePermission.Microphone;
            if (permissions == PagePermission.None) permissions = PagePermission.Other;

            tab.RequestPermission(requestingOrigin, permissions, allow =>
            {
                tab._prompts.Remove(promptId);
                using (callback) callback.Continue(allow ? PermissionRequestResult.Accept : PermissionRequestResult.Deny);
            }, promptId);
            return true;
        }

        protected override void OnDismissPermissionPrompt(IWebBrowser chromiumWebBrowser, IBrowser browser, ulong promptId,
            PermissionRequestResult result) => tab.OnUi(() =>
        {
            if (!tab._prompts.Remove(promptId, out var request)) return;
            tab._openRequests.Remove(request);
            request.Dismiss();
        });
    }
}

/// <summary>When a tab creates its Chromium browser.</summary>
public enum TabCreation
{
    /// <summary>Right away.</summary>
    Now,

    /// <summary>The first time it is shown or navigated: restored tabs do not all load at startup.</summary>
    WhenShown,

    /// <summary>Chromium creates it, as a page's popup window.</summary>
    Popup,
}
