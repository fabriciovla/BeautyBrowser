using System.Text.Json;
using CefSharp;

namespace BeautyKit;

/// <summary>What a page says about itself as an app: its manifest, theme color and icons (for "Install as app").</summary>
/// <param name="AppName">The name the site gives itself (application-name or og:site_name): "GitHub", not the page title.</param>
public sealed record WebAppHints(string Url, string Title, string? AppName, string? ManifestUrl, string? ThemeColor, IReadOnlyList<WebAppIconHint> Icons);

/// <summary>An icon the page links to: its address, declared sizes ("192x192", "any") and rel.</summary>
public sealed record WebAppIconHint(string Href, string Sizes, string Rel);

public sealed partial class BrowserTab
{
    // Read from the page itself, so it sees what the browser sees (after redirects, with the user's session).
    private const string WebAppScript =
        "(function(){var m=document.querySelector('link[rel=\"manifest\"]');" +
        "var t=document.querySelector('meta[name=\"theme-color\"]');" +
        "var n=document.querySelector('meta[name=\"application-name\"]')||document.querySelector('meta[property=\"og:site_name\"]');" +
        "var icons=[].slice.call(document.querySelectorAll('link[rel~=\"icon\"],link[rel=\"apple-touch-icon\"],link[rel=\"apple-touch-icon-precomposed\"]'))" +
        ".map(function(l){return {href:l.href,sizes:(l.sizes&&l.sizes.value)||'',rel:l.rel||''};});" +
        "return JSON.stringify({url:location.href,title:document.title,name:n?n.content:null,manifest:m?m.href:null,theme:t?t.content:null,icons:icons});})()";

    /// <summary>The page's app details, or null when the page cannot be read (not loaded, an internal page).</summary>
    public async Task<WebAppHints?> ReadWebAppHintsAsync()
    {
        try
        {
            if (!_browser.IsBrowserInitialized || _browser.GetMainFrame() is not { IsValid: true } frame) return null;
            var response = await frame.EvaluateScriptAsync(WebAppScript, timeout: TimeSpan.FromSeconds(3));
            if (!response.Success || response.Result is not string json) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var icons = root.GetProperty("icons").EnumerateArray()
                .Select(i => new WebAppIconHint(i.GetProperty("href").GetString() ?? "", i.GetProperty("sizes").GetString() ?? "", i.GetProperty("rel").GetString() ?? ""))
                .Where(i => i.Href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || i.Href.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return new WebAppHints(Text("url") ?? Url, Text("title") ?? Title, Text("name"), Text("manifest"), Text("theme"), icons);
        }
        catch
        {
            return null;
        }
    }
}
