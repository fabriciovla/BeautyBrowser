namespace BeautyKit;

/// <summary>A page asked to open an app: the site that asked ("" when unknown), the link and the app's name.</summary>
public sealed record ExternalAppRequest(string Origin, string Url, string App);

public sealed partial class BrowserTab
{
    /// <summary>
    /// The page followed a link to an app outside the browser (mailto:, zoommtg:…). The page stays where it was; the
    /// app asks the user and opens it with <see cref="ExternalApps.Open"/>.
    /// </summary>
    public event EventHandler<ExternalAppRequest>? ExternalAppRequested;

    /// <summary>
    /// True when <paramref name="url"/> is not the browser's to load, so the navigation or popup must be cancelled.
    /// Links to an app that is registered are reported; the rest are dropped, as Chrome does.
    /// </summary>
    private bool TryHandleExternal(string url, string? pageUrl)
    {
        if (!ExternalApps.IsExternal(url, out var scheme)) return false;
        if (ExternalApps.AppFor(scheme) is { } app)
        {
            var request = new ExternalAppRequest(OriginOf(pageUrl), url, app);
            OnUi(() => ExternalAppRequested?.Invoke(this, request));
        }
        return true;
    }
}
