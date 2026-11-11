using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BeautyKit;

/// <summary>
/// Links to apps outside the browser: mailto:, zoommtg:, msteams:, spotify:, steam:… Chromium cannot load them, and
/// without this they replaced the page with ERR_UNKNOWN_URL_SCHEME. As in Chrome, the page stays and the user is asked
/// before Windows opens the app.
/// </summary>
public static class ExternalApps
{
    // Schemes the browser loads itself.
    private static readonly HashSet<string> BrowserSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "file", "ftp", "data", "blob", "about", "javascript", "ws", "wss", "filesystem",
        "chrome", "chrome-error", "chrome-untrusted", "devtools", "view-source", StartPage.Scheme,
    };

    // Never handed to Windows: Chrome's list plus schemes that were used to run code from a web page (ms-msdt,
    // search-ms, ms-officecmd…).
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "afp", "disk", "disks", "hcp", "ie.http", "ms-help", "nntp", "res", "shell", "vbscript", "vnd.ms.radio",
        "ms-msdt", "search", "search-ms", "ms-officecmd", "ms-cxh", "ms-cxh-full", "ms-appinstaller",
    };

    /// <summary>Whether <paramref name="url"/> belongs to an app rather than to the browser.</summary>
    public static bool IsExternal(string? url, out string scheme)
    {
        scheme = "";
        if (string.IsNullOrEmpty(url)) return false;
        var colon = url.IndexOf(':');
        if (colon < 2) return false; // also "C:\…"
        var candidate = url[..colon];
        if (!char.IsAsciiLetter(candidate[0]) ||
            candidate.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))) return false;
        if (BrowserSchemes.Contains(candidate)) return false;
        scheme = candidate.ToLowerInvariant();
        return true;
    }

    /// <summary>The name of the app Windows opens for <paramref name="scheme"/> ("Zoom", "Outlook"), or null when none
    /// is registered or the scheme is not allowed.</summary>
    public static string? AppFor(string scheme)
    {
        if (Blocked.Contains(scheme)) return null;
        // No default app chosen yet (mailto: on a PC without a mail app): Windows asks which one, and its name for that
        // ("Pick an app") reads badly in "Open …?".
        if (Query(scheme, AssocStrExecutable) is { } handler &&
            Path.GetFileName(handler).Equals("OpenWith.exe", StringComparison.OrdinalIgnoreCase))
            return L.T("an app", "una app");
        var name = Query(scheme, AssocStrFriendlyAppName);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        // Registered but nameless: the program's file name ("zoom.exe" → "zoom").
        return Query(scheme, AssocStrExecutable) is { Length: > 0 } exe ? Path.GetFileNameWithoutExtension(exe) : null;
    }

    /// <summary>Opens the link in its app. False when Windows could not.</summary>
    public static bool Open(string url)
    {
        if (!IsExternal(url, out var scheme) || AppFor(scheme) is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const int AssocFIsProtocol = 0x1000;
    private const int AssocStrExecutable = 2;
    private const int AssocStrFriendlyAppName = 4;

    private static string? Query(string scheme, int what)
    {
        uint length = 512;
        var buffer = new StringBuilder((int)length);
        return AssocQueryStringW(AssocFIsProtocol, what, scheme, null, buffer, ref length) == 0 ? buffer.ToString() : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(int flags, int str, string assoc, string? extra, StringBuilder? output, ref uint length);
}
