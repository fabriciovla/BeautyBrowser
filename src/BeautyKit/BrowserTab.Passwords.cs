using System.Text.Json;
using CefSharp;

namespace BeautyKit;

/// <summary>A login field with focus, in physical pixels from the page's top-left: the account list goes under it.</summary>
public sealed record LoginField(string Origin, double X, double Bottom, double Width, bool IsPassword);

/// <summary>A login the page just sent.</summary>
public sealed record SubmittedLogin(string Origin, string User, string Password);

// The password manager's side in the tab: what the injected script (PageScripts.Passwords) reports, and filling.
public sealed partial class BrowserTab
{
    /// <summary>A login field got focus (the field), or lost it, was typed into or scrolled away (null).</summary>
    public event EventHandler<LoginField?>? LoginFieldChanged;

    /// <summary>The page sent a user name and password.</summary>
    public event EventHandler<SubmittedLogin>? LoginSubmitted;

    /// <summary>The page's origin ("https://site.com"), or null for pages that are not http(s).</summary>
    public string? Origin =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Authority) : null;

    // origin: the page that sent it, which may differ from Url when it navigated right after sending.
    private void ReceiveLogin(IDictionary<string, object?> m, string origin)
    {
        if (OriginOf(Url) != origin) return; // the tab is on another site now
        string Text(string key) => m.TryGetValue(key, out var v) && v is string s ? s : "";
        double Number(string key) => m.TryGetValue(key, out var v) ? v switch { int i => i, double d => d, long l => l, _ => 0 } : 0;

        switch (Text("kind"))
        {
            case "focus":
                LoginFieldChanged?.Invoke(this, new LoginField(origin, Number("x"), Number("y"), Number("width"),
                    m.TryGetValue("password", out var p) && p is true));
                break;
            case "blur":
                LoginFieldChanged?.Invoke(this, null);
                break;
            case "submit":
                var password = Text("password");
                if (password.Length is > 0 and < 512) LoginSubmitted?.Invoke(this, new SubmittedLogin(origin, Text("user"), password));
                break;
        }
    }

    /// <summary>
    /// Fills the focused login form with an account, only if the page is still on <paramref name="origin"/> (it may
    /// have navigated since the list was shown).
    /// </summary>
    public void FillLogin(string origin, string user, string password)
    {
        if (Host() is null || _browser.GetBrowser()?.MainFrame is not { IsValid: true } frame) return;
        if (Uri.TryCreate(frame.Url, UriKind.Absolute, out var uri) && uri.GetLeftPart(UriPartial.Authority) != origin) return;
        frame.ExecuteJavaScriptAsync(
            $"window.__bbPwFill && window.__bbPwFill({JsonSerializer.Serialize(user)}, {JsonSerializer.Serialize(password)})");
    }
}
