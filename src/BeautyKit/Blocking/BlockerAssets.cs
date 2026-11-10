using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeautyKit.Blocking;

/// <summary>A default filter list (the ones uBlock Origin enables out of the box).</summary>
internal sealed record FilterListSource(string Id, string Title, string Url, bool Trusted, string? Mirror = null);

/// <summary>
/// Downloads and stores everything the blocker uses: filter lists, the Public Suffix List, uBlock Origin's scriptlet
/// code and its redirect resources (noop.js, google-ima.js…). Everything lives in %LOCALAPPDATA%\BeautyBrowser\Blocker.
/// </summary>
internal static class BlockerAssets
{
    public static readonly IReadOnlyList<FilterListSource> Lists =
    [
        new("ublock-filters", "uBlock filters", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters.txt", true,
            "https://ublockorigin.github.io/uAssets/filters/filters.txt"),
        new("ublock-privacy", "uBlock filters – Privacy", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/privacy.txt", true,
            "https://ublockorigin.github.io/uAssets/filters/privacy.txt"),
        new("ublock-badware", "uBlock filters – Badware risks", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/badware.txt", true,
            "https://ublockorigin.github.io/uAssets/filters/badware.txt"),
        new("ublock-unbreak", "uBlock filters – Unbreak", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/unbreak.txt", true,
            "https://ublockorigin.github.io/uAssets/filters/unbreak.txt"),
        new("ublock-quick-fixes", "uBlock filters – Quick fixes", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/quick-fixes.txt", true,
            "https://ublockorigin.github.io/uAssets/filters/quick-fixes.txt"),
        new("easylist", "EasyList", "https://easylist.to/easylist/easylist.txt", false,
            "https://ublockorigin.github.io/uAssets/thirdparties/easylist.txt"),
        new("easyprivacy", "EasyPrivacy", "https://easylist.to/easylist/easyprivacy.txt", false,
            "https://ublockorigin.github.io/uAssets/thirdparties/easyprivacy.txt"),
        new("peter-lowe", "Peter Lowe's Ad and tracking server list",
            "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=adblockplus&showintro=0&mimetype=plaintext", false),
    ];

    private const string PslUrl = "https://publicsuffix.org/list/public_suffix_list.dat";
    private const string UboRepo = "gorhill/uBlock";
    private const string UboFallbackTag = "1.76.0";

    // uBlock scriptlet modules, in case the GitHub API does not answer.
    private static readonly string[] UboResourceFiles =
    [
        "abort-current-script.js", "attribute.js", "base.js", "cookie.js", "create-html.js", "href-sanitizer.js",
        "json-edit.js", "json-prune.js", "localstorage.js", "noeval.js", "object-prune.js", "parse-replace.js",
        "prevent-addeventlistener.js", "prevent-bab.js", "prevent-clipboard-write.js", "prevent-dialog.js",
        "prevent-fetch.js", "prevent-innerHTML.js", "prevent-navigation.js", "prevent-settimeout.js", "prevent-xhr.js",
        "proxy-apply.js", "replace-argument.js", "run-at.js", "safe-self.js", "scriptlets.js", "set-constant.js",
        "shared.js", "spoof-css.js", "stack-trace.js", "utils.js", "vod.js",
    ];

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeautyBrowser", "Blocker");

    public static string ListsFolder => Path.Combine(Folder, "lists");
    public static string UboFolder => Path.Combine(Folder, "ubo");
    public static string PslPath => Path.Combine(Folder, "public_suffix_list.dat");
    public static string ScriptletsPath => Path.Combine(Folder, "scriptlets.json");
    public static string PageRulesPath => Path.Combine(Folder, "page-rules.json");
    public static string StatePath => Path.Combine(Folder, "state.json");
    public static string UpdatedPath => Path.Combine(Folder, "updated.txt");

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BeautyBrowser-ContentBlocker/1.0");
        return http;
    }

    /// <summary>When the last full update finished, or null if nothing was ever downloaded.</summary>
    public static DateTime? LastUpdated
    {
        get
        {
            try { return File.Exists(UpdatedPath) ? DateTime.Parse(File.ReadAllText(UpdatedPath).Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind) : null; }
            catch { return null; }
        }
    }

    public static bool HasLists => Lists.Any(l => File.Exists(ListPath(l.Id)));

    public static string ListPath(string id) => Path.Combine(ListsFolder, id + ".txt");

    /// <summary>Reads the saved lists, resolving their !#include directives, to compile them.</summary>
    public static List<(FilterListText List, Func<string, string?> Include)> LoadLists()
    {
        var result = new List<(FilterListText, Func<string, string?>)>();
        foreach (var source in Lists)
        {
            var path = ListPath(source.Id);
            if (!File.Exists(path)) continue;
            var id = source.Id;
            string? Include(string name)
            {
                var included = Path.Combine(ListsFolder, id + "__" + SafeName(name));
                return File.Exists(included) ? File.ReadAllText(included) : null;
            }
            result.Add((new FilterListText(source.Id, File.ReadAllText(path), source.Trusted), Include));
        }
        return result;
    }

    public static PublicSuffix LoadPsl()
    {
        try { return File.Exists(PslPath) ? PublicSuffix.Parse(File.ReadAllText(PslPath)) : PublicSuffix.Fallback; }
        catch { return PublicSuffix.Fallback; }
    }


    /// <summary>Downloads everything again; whatever fails keeps its previous version. Returns whether anything changed.</summary>
    public static async Task<bool> DownloadAllAsync(CancellationToken cancel = default)
    {
        Directory.CreateDirectory(ListsFolder);
        var changed = false;

        var psl = await TryGetStringAsync(PslUrl, cancel);
        if (psl is not null && psl.Contains("// ===BEGIN ICANN DOMAINS===", StringComparison.Ordinal))
            changed |= WriteIfChanged(PslPath, psl);

        foreach (var source in Lists)
        {
            var text = await TryGetStringAsync(source.Url, cancel)
                       ?? (source.Mirror is null ? null : await TryGetStringAsync(source.Mirror, cancel));
            if (text is null || !LooksLikeFilterList(text)) continue;
            changed |= WriteIfChanged(ListPath(source.Id), text);

            // "!#include file" is downloaded next to the list, relative to its URL.
            foreach (Match m in Regex.Matches(text, @"^!#include\s+(\S+)", RegexOptions.Multiline))
            {
                var name = m.Groups[1].Value.Trim();
                if (!Uri.TryCreate(new Uri(source.Url), name, out var includeUrl)) continue;
                var included = await TryGetStringAsync(includeUrl.ToString(), cancel);
                if (included is null && source.Mirror is not null && Uri.TryCreate(new Uri(source.Mirror), name, out var mirrorUrl))
                    included = await TryGetStringAsync(mirrorUrl.ToString(), cancel);
                if (included is null) continue;
                changed |= WriteIfChanged(Path.Combine(ListsFolder, source.Id + "__" + SafeName(name)), included);
            }
        }

        changed |= await DownloadUboAsync(cancel);
        File.WriteAllText(UpdatedPath, DateTime.UtcNow.ToString("o"));
        return changed;
    }

    /// <summary>
    /// Source of uBlock Origin's scriptlets (latest release). It later runs in a hidden tab to extract every function
    /// exactly as uBlock uses it (see <see cref="ScriptletExtractor"/>).
    /// </summary>
    private static async Task<bool> DownloadUboAsync(CancellationToken cancel)
    {
        var tag = await LatestUboTagAsync(cancel) ?? UboFallbackTag;
        var raw = $"https://raw.githubusercontent.com/{UboRepo}/{tag}/src";
        var resourceFiles = await UboResourceListAsync(tag, cancel) ?? UboResourceFiles;

        var files = resourceFiles.Select(f => "js/resources/" + f)
            .Concat(["js/urlskip.js", "js/jsonpath.js", "js/arglist-parser.js", "js/redirect-resources.js"])
            .ToList();

        var downloaded = new Dictionary<string, string>();
        foreach (var file in files)
        {
            var text = await TryGetStringAsync($"{raw}/{file}", cancel);
            if (text is null) return false; // incomplete: keep the whole previous version
            downloaded[file] = text;
        }

        var changed = false;
        foreach (var (file, text) in downloaded)
        {
            var path = Path.Combine(UboFolder, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            changed |= WriteIfChanged(path, text);
        }

        // Redirect resources (web_accessible_resources); their names come from redirect-resources.js.
        var warFolder = Path.Combine(UboFolder, "war");
        Directory.CreateDirectory(warFolder);
        foreach (Match m in Regex.Matches(downloaded["js/redirect-resources.js"], @"^\s*\[\s*'([^']+)'\s*,", RegexOptions.Multiline))
        {
            var name = m.Groups[1].Value;
            var path = Path.Combine(warFolder, SafeName(name));
            var bytes = await TryGetBytesAsync($"{raw}/web_accessible_resources/{Uri.EscapeDataString(name)}", cancel);
            if (bytes is null) continue;
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                File.WriteAllBytes(path, bytes);
                changed = true;
            }
        }

        File.WriteAllText(Path.Combine(UboFolder, "version.txt"), tag);
        return changed;
    }

    private static async Task<string?> LatestUboTagAsync(CancellationToken cancel)
    {
        var json = await TryGetStringAsync($"https://api.github.com/repos/{UboRepo}/releases/latest", cancel);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        }
        catch { return null; }
    }

    private static async Task<string[]?> UboResourceListAsync(string tag, CancellationToken cancel)
    {
        var json = await TryGetStringAsync($"https://api.github.com/repos/{UboRepo}/contents/src/js/resources?ref={tag}", cancel);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var names = doc.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("name").GetString() ?? "")
                .Where(n => n.EndsWith(".js", StringComparison.Ordinal))
                .ToArray();
            return names.Length > 0 ? names : null;
        }
        catch { return null; }
    }

    private static bool LooksLikeFilterList(string text) =>
        text.Length > 100 && !text.TrimStart().StartsWith('<'); // not an HTML error page

    private static bool WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return false;
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
        return true;
    }

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) || c == '/' ? '_' : c).ToArray());
    }

    private static async Task<string?> TryGetStringAsync(string url, CancellationToken cancel)
    {
        try
        {
            using var response = await Http.GetAsync(url, cancel);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancel) : null;
        }
        catch { return null; }
    }

    private static async Task<byte[]?> TryGetBytesAsync(string url, CancellationToken cancel)
    {
        try
        {
            using var response = await Http.GetAsync(url, cancel);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancel) : null;
        }
        catch { return null; }
    }
}
