using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace BeautyKit.Blocking;

/// <summary>The blocker's lifetime total. Bytes and time are estimates.</summary>
public readonly record struct BlockerTotals(long Ads, long Trackers, long Bytes, TimeSpan TimeSaved, DateTime Since)
{
    public long Total => Ads + Trackers;
}

/// <summary>
/// Built-in ad and tracker blocker, like Brave's, using uBlock Origin's lists and scriptlets. It has three parts: the
/// network engine, which decides every request of every tab (queried by <see cref="BrowserTab"/> on the network
/// thread); page rules, cosmetic filters and scriptlets applied before the site's scripts run (compiled here, applied
/// by <see cref="PageInjector"/> inside each render process); and ad popup (popunder) blocking. The lists download
/// and update themselves every few days.
/// </summary>
public static class ContentBlocker
{
    private static readonly TimeSpan UpdateEvery = TimeSpan.FromDays(3);

    private static volatile NetworkEngine? _engine;
    private static volatile Dictionary<string, (string Mime, byte[] Data)> _redirects = [];
    private static volatile HashSet<string> _allowed = new(StringComparer.Ordinal);
    private static volatile bool _enabled = true;
    private static readonly TaskCompletionSource ReadySource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static DispatcherQueue? _ui;
    private static long _blockedTotal;
    private static bool _updating;

    // The lifetime total (it survives restarts). Saved a few seconds after it changes, and when the app closes.
    private static long _ads, _trackers, _bytes, _milliseconds;
    private static DateTime _statsSince = DateTime.UtcNow;
    private static int _statsDirty;
    private static Timer? _statsTimer;
    private static readonly object StatsLock = new();

    /// <summary>State changed (enabled, updating, lists…). Raised on the UI thread.</summary>
    public static event EventHandler? StatusChanged;

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            SaveState();
        }
    }

    /// <summary>Sites where the blocker is off, by registrable domain ("clarin.com").</summary>
    public static IReadOnlyCollection<string> AllowedSites => _allowed;

    /// <summary>The engine finished loading from disk at startup.</summary>
    public static Task Ready => ReadySource.Task;

    public static bool IsReady => _engine is not null;
    public static bool IsUpdating => _updating;
    public static long BlockedTotal => Interlocked.Read(ref _blockedTotal);
    /// <summary>The total since the blocker started counting: ads, trackers, estimated bytes and estimated time.</summary>
    public static BlockerTotals Totals => new(Interlocked.Read(ref _ads), Interlocked.Read(ref _trackers),
        Interlocked.Read(ref _bytes), TimeSpan.FromMilliseconds(Interlocked.Read(ref _milliseconds)), _statsSince);

    public static int FilterCount { get; private set; }
    public static int CosmeticCount { get; private set; }
    public static int ScriptletCount { get; private set; }
    public static string? LastError { get; private set; }
    public static DateTime? LastUpdated => BlockerAssets.LastUpdated;
    public static IEnumerable<string> ListTitles => BlockerAssets.Lists.Select(l => l.Title);

    /// <summary>Starts the blocker. Call it once, on the UI thread, after Chromium has started.</summary>
    internal static void Initialize(DispatcherQueue ui)
    {
        _ui = ui;
        LoadState();
        LoadStats();
        _ = Task.Run(async () =>
        {
            await CompileAsync();
            ReadySource.TrySetResult();
            var stale = LastUpdated is not { } updated || DateTime.UtcNow - updated > UpdateEvery || !BlockerAssets.HasLists;
            if (stale) ui.TryEnqueue(() => _ = UpdateAsync());
        });
    }


    public static string SiteOf(string url)
    {
        var lower = url.ToLowerInvariant();
        if (!RequestContext.TryHost(lower, out var start, out var end)) return "";
        return Psl.Domain(lower[start..end]);
    }

    /// <summary>The Public Suffix List the blocker loaded, or the built-in approximation until it is ready.</summary>
    internal static PublicSuffix Psl => _engine?.Psl ?? PublicSuffix.Fallback;

    public static bool IsSiteAllowed(string url)
    {
        var site = SiteOf(url);
        return site.Length > 0 && _allowed.Contains(site);
    }

    /// <summary>Turns the blocker off (or back on) for the site of this URL.</summary>
    public static void SetSiteAllowed(string url, bool allowed)
    {
        var site = SiteOf(url);
        if (site.Length == 0) return;
        var next = new HashSet<string>(_allowed, StringComparer.Ordinal);
        if (allowed) next.Add(site); else next.Remove(site);
        _allowed = next;
        SaveState();
    }


    /// <summary>
    /// Decides a request. <paramref name="docUrl"/> is the document making it (iframes included) and
    /// <paramref name="topUrl"/> the tab's page.
    /// </summary>
    internal static BlockDecision Evaluate(string url, string? docUrl, string? topUrl, RequestType type, string method)
    {
        if (!_enabled || _engine is not { } engine) return BlockDecision.Allow;
        if (!string.IsNullOrEmpty(topUrl))
        {
            if (_allowed.Count > 0 && IsSiteAllowed(topUrl)) return BlockDecision.Allow;
            if (engine.IsDocumentAllowed(topUrl)) return BlockDecision.Allow;
        }
        var ctx = RequestContext.Create(url, string.IsNullOrEmpty(docUrl) ? topUrl : docUrl, type, method, engine.Psl);
        if (ctx is null) return BlockDecision.Allow;
        var decision = engine.Evaluate(ctx);
        if (decision.Block)
        {
            Interlocked.Increment(ref _blockedTotal);
            Record(type, decision.Tracker);
        }
        return decision;
    }

    internal static bool IsAdPopup(string targetUrl, string? openerUrl)
    {
        if (!_enabled || _engine is not { } engine || string.IsNullOrEmpty(openerUrl)) return false;
        if (IsSiteAllowed(openerUrl) || engine.IsDocumentAllowed(openerUrl)) return false;
        var ctx = RequestContext.Create(targetUrl, openerUrl, RequestType.Popup, "get", engine.Psl);
        if (ctx is null || !engine.BlockPopup(ctx)) return false;
        Interlocked.Increment(ref _blockedTotal);
        Record(RequestType.Popup, tracker: false);
        return true;
    }

    /// <summary>Content of a redirect resource (noop.js, 1x1.gif…), or null.</summary>
    internal static (string Mime, byte[] Data)? Redirect(string name) =>
        _redirects.TryGetValue(name, out var resource) ? resource : null;


    /// <summary>Downloads the lists and scriptlets again and recompiles. Call it on the UI thread.</summary>
    public static async Task UpdateAsync()
    {
        if (_updating) return;
        _updating = true;
        RaiseStatus();
        try
        {
            var changed = await Task.Run(() => BlockerAssets.DownloadAllAsync());
            // Scriptlets are extracted with Chromium, on the UI thread (Chromium's thread).
            if (changed || !File.Exists(BlockerAssets.ScriptletsPath))
            {
                var extracted = await ScriptletExtractor.RunAsync();
                if (extracted is not null)
                    File.WriteAllText(BlockerAssets.ScriptletsPath, JsonSerializer.Serialize(extracted, ExtractedJson.Default.ExtractedResources));
            }
            await CompileAsync();
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log("Update: " + ex);
        }
        finally
        {
            _updating = false;
            RaiseStatus();
        }
    }

    /// <summary>Builds the engine from what is on disk and writes the rules for the render processes.</summary>
    private static async Task CompileAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var clock = Stopwatch.StartNew();
                var psl = BlockerAssets.LoadPsl();
                var compiler = new FilterCompiler(psl);
                foreach (var (list, include) in BlockerAssets.LoadLists()) compiler.AddList(list, include);
                var (network, rules) = compiler.Build();

                var resources = LoadExtracted();
                if (resources is not null) AddScriptletResources(rules, resources);
                _redirects = LoadRedirects(resources);

                Directory.CreateDirectory(BlockerAssets.Folder);
                var temp = BlockerAssets.PageRulesPath + ".tmp";
                using (var stream = File.Create(temp))
                    JsonSerializer.Serialize(stream, rules, BlockingJson.Default.PageRules);
                File.Move(temp, BlockerAssets.PageRulesPath, overwrite: true);

                _engine = network;
                FilterCount = compiler.NetworkCount;
                CosmeticCount = compiler.CosmeticCount;
                ScriptletCount = compiler.ScriptletCount;
                Log($"Compiled in {clock.ElapsedMilliseconds} ms: {compiler.Summary()} resources={_redirects.Count} scriptlets={rules.Resources.Count}");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log("Compile: " + ex);
            }
        });
        RaiseStatus();
    }

    private static ExtractedResources? LoadExtracted()
    {
        try
        {
            return File.Exists(BlockerAssets.ScriptletsPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(BlockerAssets.ScriptletsPath), ExtractedJson.Default.ExtractedResources)
                : null;
        }
        catch { return null; }
    }

    private static void AddScriptletResources(PageRules rules, ExtractedResources resources)
    {
        foreach (var s in resources.Scriptlets)
        {
            var name = s.Name.EndsWith(".js", StringComparison.Ordinal) ? s.Name[..^3] : s.Name;
            rules.Resources[name] = new ScriptletResource
            {
                Function = s.Function,
                Code = s.Code,
                Dependencies = s.Dependencies.Select(d => d.EndsWith(".js", StringComparison.Ordinal) ? d[..^3] : d).ToList(),
                RequiresTrust = s.RequiresTrust,
            };
            foreach (var alias in s.Aliases)
                rules.Aliases[alias.EndsWith(".js", StringComparison.Ordinal) ? alias[..^3] : alias] = name;
        }
    }

    private static Dictionary<string, (string, byte[])> LoadRedirects(ExtractedResources? resources)
    {
        var map = new Dictionary<string, (string, byte[])>(StringComparer.Ordinal);
        var folder = Path.Combine(BlockerAssets.UboFolder, "war");
        if (!Directory.Exists(folder)) return map;
        foreach (var file in Directory.GetFiles(folder))
        {
            var name = Path.GetFileName(file);
            map[name] = (MimeOf(name), File.ReadAllBytes(file));
        }
        foreach (var r in resources?.Redirects ?? [])
            if (map.TryGetValue(BlockerAssets.SafeName(r.Name), out var resource))
                foreach (var alias in r.Aliases) map.TryAdd(alias, resource);
        // Old names some lists still use.
        if (map.TryGetValue("noop.js", out var noop)) { map.TryAdd("noopjs", noop); map.TryAdd("abp-resource:blank-js", noop); }
        if (map.TryGetValue("noop.txt", out var txt)) map.TryAdd("nooptext", txt);
        if (map.TryGetValue("noop.html", out var html)) map.TryAdd("noopframe", html);
        if (map.TryGetValue("1x1.gif", out var gif)) map.TryAdd("1x1-transparent.gif", gif);
        map.TryAdd("empty", ("text/plain", []));
        return map;
    }

    private static string MimeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".js" => "application/javascript",
        ".html" => "text/html",
        ".css" => "text/css",
        ".json" => "application/json",
        ".xml" => "text/xml",
        ".gif" => "image/gif",
        ".png" => "image/png",
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        _ => "text/plain",
    };


    // What a blocked request would have cost, on average (an estimate, like Brave's): the size by kind of request,
    // and the time at a typical connection plus what each request takes to start.
    private static long EstimatedBytes(RequestType type) => type switch
    {
        RequestType.Script => 60_000,
        RequestType.Image => 40_000,
        RequestType.Media => 400_000,
        RequestType.Subdocument => 80_000,
        RequestType.Document => 80_000,
        RequestType.Stylesheet => 20_000,
        RequestType.Font => 30_000,
        RequestType.Object => 100_000,
        RequestType.Popup => 0,
        _ => 1_500, // xhr, beacons, pings…
    };

    private static void Record(RequestType type, bool tracker)
    {
        var bytes = EstimatedBytes(type);
        Interlocked.Increment(ref tracker ? ref _trackers : ref _ads);
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Add(ref _milliseconds, 25 + bytes / 1_500); // ~1.5 MB/s effective + 25 ms of connection per request
        if (Interlocked.Exchange(ref _statsDirty, 1) == 0)
            _statsTimer ??= new Timer(_ => FlushStats(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    private static void LoadStats()
    {
        try
        {
            if (!File.Exists(BlockerAssets.StatsPath)) return;
            var stats = JsonSerializer.Deserialize(File.ReadAllText(BlockerAssets.StatsPath), BlockingJson.Default.BlockerStats);
            if (stats is null) return;
            Interlocked.Exchange(ref _ads, stats.Ads);
            Interlocked.Exchange(ref _trackers, stats.Trackers);
            Interlocked.Exchange(ref _bytes, stats.Bytes);
            Interlocked.Exchange(ref _milliseconds, stats.Milliseconds);
            _statsSince = stats.Since;
        }
        catch { }
    }

    /// <summary>Writes the lifetime total if it changed. Also called when the app closes.</summary>
    public static void FlushStats()
    {
        if (Interlocked.Exchange(ref _statsDirty, 0) == 0) return;
        lock (StatsLock)
        {
            try
            {
                Directory.CreateDirectory(BlockerAssets.Folder);
                var stats = new BlockerStats
                {
                    Ads = Interlocked.Read(ref _ads), Trackers = Interlocked.Read(ref _trackers),
                    Bytes = Interlocked.Read(ref _bytes), Milliseconds = Interlocked.Read(ref _milliseconds),
                    Since = _statsSince,
                };
                var temp = BlockerAssets.StatsPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(stats, BlockingJson.Default.BlockerStats));
                File.Move(temp, BlockerAssets.StatsPath, overwrite: true);
            }
            catch { Interlocked.Exchange(ref _statsDirty, 1); }
        }
    }

    private static void LoadState()
    {
        try
        {
            if (!File.Exists(BlockerAssets.StatePath)) return;
            var state = JsonSerializer.Deserialize(File.ReadAllText(BlockerAssets.StatePath), BlockingJson.Default.BlockerState);
            if (state is null) return;
            _enabled = state.Enabled;
            _allowed = new HashSet<string>(state.AllowedSites, StringComparer.Ordinal);
        }
        catch { }
    }

    /// <summary>Also read by every render process, so nothing is injected while the blocker is off.</summary>
    private static void SaveState()
    {
        try
        {
            Directory.CreateDirectory(BlockerAssets.Folder);
            var state = new BlockerState { Enabled = _enabled, AllowedSites = [.. _allowed] };
            var temp = BlockerAssets.StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, BlockingJson.Default.BlockerState));
            File.Move(temp, BlockerAssets.StatePath, overwrite: true);
        }
        catch { }
        RaiseStatus();
    }

    private static void RaiseStatus()
    {
        if (_ui is null) return;
        if (_ui.HasThreadAccess) StatusChanged?.Invoke(null, EventArgs.Empty);
        else _ui.TryEnqueue(() => StatusChanged?.Invoke(null, EventArgs.Empty));
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(BlockerAssets.Folder);
            File.AppendAllText(Path.Combine(BlockerAssets.Folder, "blocker.log"), $"{DateTime.Now:s} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
