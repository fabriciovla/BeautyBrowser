using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeautyKit.Blocking;

namespace BeautyKit;

/// <summary>
/// A site's Boost, as in Arc: its own look in this browser. A font, a forced dark mode, elements hidden with a click and
/// CSS of one's own. Kept per site ("youtube.com") in boosts.json; the render process applies it before the page
/// paints (<see cref="PageInjector"/>), and the open tab gets it live when it changes.
/// </summary>
public sealed class SiteBoost
{
    public string Site { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>"", "serif", "sans" or "mono".</summary>
    public string Font { get; set; } = "";
    /// <summary>Inverts the page's colors (images and videos keep theirs).</summary>
    public bool Dark { get; set; }
    /// <summary>CSS selectors of the elements hidden with "Hide elements".</summary>
    public List<string> Hidden { get; set; } = [];
    public string Css { get; set; } = "";

    [JsonIgnore]
    public bool IsEmpty => Font.Length == 0 && !Dark && Hidden.Count == 0 && string.IsNullOrWhiteSpace(Css);
}

public sealed class BoostsData
{
    public List<SiteBoost> Sites { get; set; } = [];
}

public static class Boosts
{
    internal static string FilePath => Path.Combine(BlockerAssets.Folder, "boosts.json");

    private static BoostsData? _data;

    private static BoostsData Data
    {
        get
        {
            if (_data is not null) return _data;
            try
            {
                _data = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), BlockingJson.Default.BoostsData) ?? new BoostsData()
                    : new BoostsData();
            }
            catch
            {
                _data = new BoostsData();
            }
            return _data;
        }
    }

    /// <summary>"https://www.youtube.com/watch" → "youtube.com"; empty for pages that cannot be boosted.</summary>
    public static string SiteOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? HostName.WithoutWww(uri.Host).ToLowerInvariant() : "";

    /// <summary>The site's Boost (a new, empty one when it has none yet; not saved until <see cref="Save"/>).</summary>
    public static SiteBoost For(string site) =>
        Data.Sites.FirstOrDefault(b => b.Site == site) ?? new SiteBoost { Site = site };

    public static bool Has(string site) => Data.Sites.Any(b => b.Site == site && b.Enabled && !b.IsEmpty);

    /// <summary>Stores the Boost (an empty one is removed) for the next pages of the site.</summary>
    public static void Save(SiteBoost boost)
    {
        Data.Sites.RemoveAll(b => b.Site == boost.Site);
        if (!boost.IsEmpty) Data.Sites.Add(boost);
        try
        {
            Directory.CreateDirectory(BlockerAssets.Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Data, BlockingJson.Default.BoostsData));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // Not saved: the open page still has it; the next load will not.
        }
    }

    /// <summary>The CSS a Boost adds to its site's pages.</summary>
    public static string CssFor(SiteBoost boost)
    {
        if (!boost.Enabled) return "";
        var css = new StringBuilder();
        var family = boost.Font switch
        {
            "serif" => "Georgia, \"Iowan Old Style\", \"Times New Roman\", serif",
            "sans" => "\"Segoe UI Variable Text\", \"Segoe UI\", system-ui, sans-serif",
            "mono" => "\"Cascadia Code\", Consolas, \"Courier New\", monospace",
            _ => null,
        };
        // Everything except icon fonts (Material Icons, Font Awesome…) and code, which keep theirs.
        if (family is not null)
            css.Append("body, body :not(i):not(svg):not(svg *):not(code):not(pre):not(kbd):not([class*=\"icon\" i]):not([class*=\"material\" i]):not([class*=\"fa-\"])")
               .Append(" { font-family: ").Append(family).Append(" !important; }\n");
        if (boost.Dark)
            css.Append("html { filter: invert(1) hue-rotate(180deg) !important; background: #fff !important; }\n")
               .Append("img, video, picture, canvas, iframe, embed, object, svg image, [style*=\"background-image\"] { filter: invert(1) hue-rotate(180deg) !important; }\n");
        var hidden = boost.Hidden.Where(IsSafeSelector).ToList();
        if (hidden.Count > 0) css.Append(string.Join(",\n", hidden)).Append(" { display: none !important; }\n");
        if (!string.IsNullOrWhiteSpace(boost.Css)) css.Append(boost.Css.Replace("</", "<\\/")).Append('\n');
        return css.ToString();
    }

    /// <summary>A selector from "Hide elements": short and without anything that could end the rule.</summary>
    public static bool IsSafeSelector(string selector) =>
        selector.Length is > 0 and <= 400 && selector.IndexOfAny(['{', '}', '<', ';', '@']) < 0;

    /// <summary>A script that puts (or replaces, or removes when empty) the Boost's style on the page.</summary>
    internal static string StyleScript(string css) =>
        "(function(c){var id='__beauty_boost';function put(){var s=document.getElementById(id);" +
        "if(!c){if(s)s.remove();return;}if(!s){s=document.createElement('style');s.id=id;" +
        "(document.head||document.documentElement).appendChild(s);}s.textContent=c;}" +
        "if(document.documentElement)put();else document.addEventListener('readystatechange',put,{once:true});" +
        // Pages that rebuild their <head> (single-page apps) would drop it: it comes back.
        "new MutationObserver(function(){if(c&&!document.getElementById(id))put();}).observe(document,{childList:true,subtree:true});" +
        "})(" + JsonSerializer.Serialize(css, BlockingJson.Default.String) + ");";

    // ---- render process side ----

    private static DateTime _rendererStamp;
    private static BoostsData _rendererData = new();

    /// <summary>The CSS for a page about to load, read in the render process (the file is cached by its date).</summary>
    internal static string CssForPage(string url)
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return "";
            var stamp = File.GetLastWriteTimeUtc(path);
            if (stamp != _rendererStamp)
            {
                _rendererData = JsonSerializer.Deserialize(File.ReadAllText(path), BlockingJson.Default.BoostsData) ?? new BoostsData();
                _rendererStamp = stamp;
            }
            var site = SiteOf(url);
            return site.Length == 0 ? "" : _rendererData.Sites.FirstOrDefault(b => b.Site == site) is { } boost ? CssFor(boost) : "";
        }
        catch
        {
            return "";
        }
    }
}
