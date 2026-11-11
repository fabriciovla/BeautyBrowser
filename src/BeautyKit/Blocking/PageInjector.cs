using System.Text;
using System.Text.Json;
using CefSharp;
using CefSharp.RenderProcess;

namespace BeautyKit.Blocking;

/// <summary>
/// Runs inside every page's render process. When a document is created, before any of the site's scripts run, it
/// injects the uBlock scriptlets for that site (which is how YouTube and streaming ads go away), the CSS and
/// procedural filters that hide ads, and the media detector for the sidebar card. The browser compiles the rules
/// (<see cref="ContentBlocker"/>) into page-rules.json.
/// </summary>
internal sealed class PageInjector : IRenderProcessHandler
{
    private const int MaxCache = 64;

    private readonly object _gate = new();
    private Task<PageRules?>? _loading;
    private PageRules? _rules;
    private DateTime _rulesStamp;
    private HashSet<string>? _genericSet;
    private string? _genericCss;
    private BlockerState _state = new();
    private DateTime _stateStamp;
    private readonly Dictionary<string, string> _codeByHost = new(StringComparer.Ordinal);

    public void OnWebKitInitialized() => StartLoading();

    public void OnContextCreated(IBrowser browser, IFrame frame, IV8Context context)
    {
        try
        {
            var url = frame.Url ?? "";
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;

            context.Execute(PageScripts.Media, "beautybrowser://media.js", 1, out var mediaError);
            if (frame.IsMain) context.Execute(PageScripts.Passwords, "beautybrowser://passwords.js", 1, out _);

            var code = CodeFor(url, frame.IsMain);
            V8Exception? blockerError = null;
            if (code.Length > 0) context.Execute(code, "beautybrowser://blocker.js", 1, out blockerError);

            if (DebugLog)
                Debug($"{(frame.IsMain ? "main " : "frame")} {url[..Math.Min(url.Length, 90)]} code={code.Length} " +
                      $"error={blockerError?.Message ?? mediaError?.Message ?? "-"}" +
                      (blockerError is null ? "" : $" line={blockerError.LineNumber} «{blockerError.SourceLine?[..Math.Min(blockerError.SourceLine.Length, 120)]}»"));
        }
        catch (Exception ex)
        {
            // The blocker must never break the page.
            if (DebugLog) Debug("exception: " + ex.Message);
        }
    }

    /// <summary>Diagnostics: with BEAUTYBROWSER_BLOCKER_LOG=1 every injection is logged to Blocker\renderer.log.</summary>
    private static readonly bool DebugLog = Environment.GetEnvironmentVariable("BEAUTYBROWSER_BLOCKER_LOG") == "1";

    private static void Debug(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(BlockerAssets.Folder, "renderer.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public void OnContextReleased(IBrowser browser, IFrame frame, IV8Context context) { }


    private void StartLoading()
    {
        lock (_gate)
        {
            if (_loading is { IsCompleted: false }) return;
            _loading = Task.Run(LoadRules);
        }
    }

    private PageRules? LoadRules()
    {
        try
        {
            var path = BlockerAssets.PageRulesPath;
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path);
            using var stream = File.OpenRead(path);
            var rules = JsonSerializer.Deserialize(stream, BlockingJson.Default.PageRules);
            lock (_gate)
            {
                _rules = rules;
                _rulesStamp = stamp;
                _genericSet = rules is null ? null : new HashSet<string>(rules.Generic, StringComparer.Ordinal);
                _genericCss = null;
                _codeByHost.Clear();
            }
            return rules;
        }
        catch { return null; }
    }

    private PageRules? Rules()
    {
        // Recompiled lists are reloaded; the previous rules apply meanwhile.
        try
        {
            var path = BlockerAssets.PageRulesPath;
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != _rulesStamp && _loading is { IsCompleted: true }) StartLoading();
        }
        catch { }

        if (_rules is null && _loading is { } loading) loading.Wait(TimeSpan.FromSeconds(3));
        return _rules;
    }

    private BlockerState State()
    {
        try
        {
            var path = BlockerAssets.StatePath;
            if (!File.Exists(path)) return _state;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (stamp == _stateStamp) return _state;
            _state = JsonSerializer.Deserialize(File.ReadAllText(path), BlockingJson.Default.BlockerState) ?? new BlockerState();
            _stateStamp = stamp;
            lock (_gate) _codeByHost.Clear();
        }
        catch { }
        return _state;
    }


    private string CodeFor(string url, bool mainFrame)
    {
        var state = State();
        if (!state.Enabled) return "";
        var lower = url.ToLowerInvariant();
        if (!RequestContext.TryHost(lower, out var start, out var end)) return "";
        var host = lower[start..end];

        var keys = HostKeys(host);
        if (state.AllowedSites.Count > 0 && keys.Any(state.AllowedSites.Contains)) return "";

        var rules = Rules();
        if (rules is null) return "";

        var cacheKey = (mainFrame ? "m|" : "f|") + host;
        lock (_gate)
        {
            if (_codeByHost.TryGetValue(cacheKey, out var cached)) return cached;
        }

        var code = new StringBuilder();
        AppendScriptlets(code, rules, keys);
        // Generic hiding (~14,000 selectors) only goes into the main frame; for iframes the site-specific rules are
        // enough, since ad iframes are already cut at the network level.
        AppendCosmetics(code, rules, keys, includeGeneric: mainFrame);
        var result = code.ToString();

        lock (_gate)
        {
            if (_codeByHost.Count > MaxCache) _codeByHost.Clear();
            _codeByHost[cacheKey] = result;
        }
        return result;
    }

    /// <summary>
    /// Keys used to look up rules for a host, like uBlock: the host, its parent domains and its entities, each without
    /// its last labels plus ".*" ("www.google.com.ar" → "www.google.com.*", "google.*"…).
    /// </summary>
    private static List<string> HostKeys(string host)
    {
        var hosts = new List<string> { host };
        for (var pos = host.IndexOf('.'); pos >= 0; pos = host.IndexOf('.', pos + 1)) hosts.Add(host[(pos + 1)..]);
        var keys = new List<string>(hosts);
        foreach (var h in hosts)
        {
            var s = h;
            for (var dot = s.LastIndexOf('.'); dot > 0; dot = s.LastIndexOf('.'))
            {
                s = s[..dot];
                keys.Add(s + ".*");
            }
        }
        return keys;
    }

    private static bool AnyIn(HashSet<string> set, List<string> keys)
    {
        if (set.Count == 0) return false;
        foreach (var key in keys) if (set.Contains(key)) return true;
        return false;
    }

    private static IEnumerable<string> Collect(Dictionary<string, List<string>> map, List<string> keys)
    {
        foreach (var key in keys)
            if (map.TryGetValue(key, out var list))
                foreach (var item in list) yield return item;
    }

    private static void AppendScriptlets(StringBuilder code, PageRules rules, List<string> keys)
    {
        if (rules.Resources.Count == 0 || AnyIn(rules.NoScriptlets, keys)) return;

        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Collect(rules.Scriptlets, [.. keys, "*"])) all.Add(item);
        if (rules.Scriptlets.TryGetValue("", out var global)) foreach (var item in global) all.Add(item);
        if (all.Count == 0 || all.Contains("-[]")) return;

        var functions = new Dictionary<string, string>(StringComparer.Ordinal);
        var calls = new StringBuilder();
        foreach (var item in all)
        {
            if (item[0] != '+' || all.Contains("-" + item[1..])) continue;
            List<string>? args;
            try { args = JsonSerializer.Deserialize(item[1..], BlockingJson.Default.ListString); }
            catch { continue; }
            if (args is not { Count: > 0 }) continue;

            var name = args[0];
            if (rules.Aliases.TryGetValue(name, out var real)) name = real;
            if (!rules.Resources.TryGetValue(name, out var resource)) continue;

            AddWithDependencies(rules, name, resource, functions);
            calls.Append("try{").Append(resource.Function).Append(".apply(null,")
                 .Append(JsonSerializer.Serialize(args.Skip(1).ToList(), BlockingJson.Default.ListString))
                 .Append(");}catch(e){}\n");
        }
        if (calls.Length == 0) return;

        code.Append("(function(){const scriptletGlobals={};\n");
        foreach (var fn in functions.Values) code.Append(fn).Append('\n');
        code.Append(calls).Append("})();\n");
    }

    private static void AddWithDependencies(PageRules rules, string name, ScriptletResource resource, Dictionary<string, string> functions)
    {
        if (functions.ContainsKey(name)) return;
        functions[name] = resource.Code;
        foreach (var dep in resource.Dependencies)
            if (rules.Resources.TryGetValue(dep, out var depResource))
                AddWithDependencies(rules, dep, depResource, functions);
    }

    private void AppendCosmetics(StringBuilder code, PageRules rules, List<string> keys, bool includeGeneric)
    {
        if (AnyIn(rules.NoCosmetic, keys)) return;

        var exceptions = new HashSet<string>(Collect(rules.Exceptions, keys), StringComparer.Ordinal);
        var css = new StringBuilder();

        if (includeGeneric && !AnyIn(rules.NoGeneric, keys))
        {
            if (exceptions.Count == 0 || _genericSet is null || !exceptions.Overlaps(_genericSet))
                css.Append(GenericCss(rules));
            else
                foreach (var selector in rules.Generic)
                    if (!exceptions.Contains(selector)) AppendHide(css, selector);
        }

        var procedural = new List<string>();
        if (!AnyIn(rules.NoSpecific, keys))
        {
            foreach (var selector in Collect(rules.Specific, keys))
                if (!exceptions.Contains(selector)) AppendHide(css, selector);
            foreach (var rule in Collect(rules.Styles, keys)) css.Append(rule).Append('\n');
            foreach (var selector in Collect(rules.Procedural, keys))
                if (!exceptions.Contains(selector)) procedural.Add(selector);
        }

        if (css.Length == 0 && procedural.Count == 0) return;
        code.Append("(").Append(PageScripts.Cosmetic).Append(")(")
            .Append(JsonSerializer.Serialize(css.ToString(), BlockingJson.Default.String)).Append(',')
            .Append(JsonSerializer.Serialize(procedural, BlockingJson.Default.ListString)).Append(");\n");
    }

    private string GenericCss(PageRules rules)
    {
        if (_genericCss is not null) return _genericCss;
        var css = new StringBuilder(rules.Generic.Count * 40);
        foreach (var selector in rules.Generic) AppendHide(css, selector);
        return _genericCss = css.ToString();
    }

    /// <summary>One rule per selector, so an invalid one only drops itself.</summary>
    private static void AppendHide(StringBuilder css, string selector) =>
        css.Append(selector).Append("{display:none!important}\n");
}
