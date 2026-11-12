using BeautyKit;

namespace BeautyBrowser.Services;

/// <summary>
/// What the user decided for each site (microphone, camera, location…), so the site is not asked about again, as in
/// Chrome. Stored in site-permissions.json.
/// </summary>
public sealed class SitePermissionsService
{
    private const string FileName = "site-permissions.json";

    // origin → permission name → allowed
    private readonly Dictionary<string, Dictionary<string, bool>> _decisions;

    public SitePermissionsService()
    {
        _decisions = JsonStore.Load<Dictionary<string, Dictionary<string, bool>>>(FileName);
    }

    /// <summary>True or false when every permission asked for was already decided the same way; otherwise null.</summary>
    public bool? Decided(string origin, PagePermission permissions)
    {
        if ((permissions & AlwaysAsk) != 0) return null;
        if (!_decisions.TryGetValue(Key(origin), out var site)) return null;
        bool? answer = null;
        foreach (var name in Names(permissions))
        {
            if (!site.TryGetValue(name, out var allowed)) return null;
            if (answer is { } previous && previous != allowed) return null;
            answer = allowed;
        }
        return answer;
    }

    /// <summary>The sites where this permission was allowed for good.</summary>
    public IEnumerable<string> OriginsAllowing(PagePermission permission) =>
        _decisions.Where(site => site.Value.TryGetValue(permission.ToString(), out var allowed) && allowed).Select(site => site.Key);

    public void Remember(string origin, PagePermission permissions, bool allowed)
    {
        permissions &= ~AlwaysAsk;
        if (permissions == PagePermission.None) return;
        var key = Key(origin);
        if (!_decisions.TryGetValue(key, out var site)) _decisions[key] = site = [];
        foreach (var name in Names(permissions)) site[name] = allowed;
        JsonStore.Save(FileName, _decisions);
    }

    // Asked every time, as in Chrome: sharing the screen, and features without a name of their own ("use a feature of
    // your device"), where one yes would otherwise allow every future unnamed request from the site.
    private const PagePermission AlwaysAsk = PagePermission.ScreenShare | PagePermission.Other;

    private static IEnumerable<string> Names(PagePermission permissions) =>
        Enum.GetValues<PagePermission>()
            .Where(p => p != PagePermission.None && permissions.HasFlag(p))
            .Select(p => p.ToString());

    // "https://web.whatsapp.com/" and "https://web.whatsapp.com" are the same site.
    private static string Key(string origin) => origin.TrimEnd('/').ToLowerInvariant();
}
