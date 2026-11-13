using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BeautyKit.Blocking;

/// <param name="Tracker">The filter that blocked it comes from a privacy list (trackers), not from the ad lists.</param>
public readonly record struct BlockDecision(bool Block, string? Redirect, bool Tracker = false)
{
    public static readonly BlockDecision Allow = new(false, null);
}

/// <summary>
/// Network filter engine (uBlock Origin / Adblock Plus syntax). Built once from the lists and read-only afterwards, so
/// Chromium's network thread uses it without locks.
/// </summary>
internal sealed class NetworkEngine
{
    private readonly PublicSuffix _psl;
    private readonly FilterBucket _block = new(indexHosts: true);
    private readonly FilterBucket _important = new(indexHosts: false);
    private readonly FilterBucket _exceptions = new(indexHosts: false);
    private readonly FilterBucket _redirects = new(indexHosts: false);
    private readonly FilterBucket _redirectExceptions = new(indexHosts: false);
    private readonly FilterBucket _popups = new(indexHosts: true);
    private readonly FilterBucket _popupExceptions = new(indexHosts: false);
    private readonly List<NetworkFilter> _documentExceptions = [];
    private readonly ConcurrentDictionary<string, bool> _documentAllowed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _badFilters = new(StringComparer.Ordinal);

    // Per-site cosmetic exceptions that come from network filters ($generichide, $elemhide…).
    public readonly HashSet<string> NoGenericCosmetic = new(StringComparer.Ordinal);
    public readonly HashSet<string> NoCosmetic = new(StringComparer.Ordinal);
    public readonly HashSet<string> NoSpecificCosmetic = new(StringComparer.Ordinal);
    public readonly HashSet<string> NoScriptlets = new(StringComparer.Ordinal);

    public int FilterCount { get; private set; }

    /// <summary>The compiler is adding lines of a privacy list: its filters block trackers, not ads.</summary>
    public bool AddingTrackers { get; set; }

    public NetworkEngine(PublicSuffix psl) => _psl = psl;

    public PublicSuffix Psl => _psl;


    /// <summary>Adds a network filter line. Returns false when it is not understood or not supported.</summary>
    public bool Add(string line)
    {
        var filter = Parse(line);
        if (filter is null) return false;
        FilterCount++;
        return true;
    }

    /// <summary>Applies $badfilter once loading is done.</summary>
    public void Freeze()
    {
        if (_badFilters.Count == 0) return;
        foreach (var bucket in new[] { _block, _important, _exceptions, _redirects, _redirectExceptions, _popups, _popupExceptions })
            bucket.RemoveWhere(f => _badFilters.Contains(f.Raw));
        _documentExceptions.RemoveAll(f => _badFilters.Contains(f.Raw));
    }

    private static readonly HashSet<string> IgnoredOptions = new(StringComparer.Ordinal)
    {
        "_", "__", "___", "genericblock", "reason", "cname", "network",
    };

    private static readonly HashSet<string> UnsupportedOptions = new(StringComparer.Ordinal)
    {
        // They modify the request or response (or are Firefox-only): not applied, but they must not over-block either.
        "csp", "removeparam", "queryprune", "permissions", "header", "responseheader", "replace", "uritransform",
        "urltransform", "urlskip", "ipaddress", "webrtc", "inline-script", "inline-font", "popunder", "rewrite",
        "object-subrequest", "elemhide-legacy", "unknown",
    };

    private NetworkFilter? Parse(string line)
    {
        var raw = line;
        var exception = line.StartsWith("@@", StringComparison.Ordinal);
        if (exception) line = line[2..];

        // Options: whatever follows the last '$', unless it belongs to a /regex/.
        string pattern = line, options = "";
        var dollar = line.LastIndexOf('$');
        if (dollar >= 0 && !(line.StartsWith('/') && line.EndsWith('/') && line.Length > 2))
        {
            // "$" inside a regex followed by options: /re$/$script, where the options '$' is the last one.
            pattern = line[..dollar];
            options = line[(dollar + 1)..];
            if (options.Length > 0 && !LooksLikeOptions(options))
            {
                pattern = line;
                options = "";
            }
        }

        var filter = new NetworkFilter { Raw = raw, Exception = exception, Tracker = AddingTrackers };

        RequestType include = RequestType.None, exclude = RequestType.None;
        var badfilter = false;
        var popupOnly = false;
        if (options.Length > 0)
        {
            foreach (var rawOption in SplitOptions(options))
            {
                var option = rawOption.Trim();
                if (option.Length == 0) continue;
                var negated = option.StartsWith('~');
                var name = negated ? option[1..] : option;
                string? value = null;
                var eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                }
                name = name.ToLowerInvariant();

                if (IgnoredOptions.Contains(name)) continue;
                if (UnsupportedOptions.Contains(name)) return null;

                if (TypeOf(name) is { } type)
                {
                    if (negated) exclude |= type; else include |= type;
                    continue;
                }

                switch (name)
                {
                    case "third-party" or "3p":
                        filter.Party = negated ? (byte)1 : (byte)2; break;
                    case "first-party" or "1p":
                        filter.Party = negated ? (byte)2 : (byte)1; break;
                    case "strict3p" or "strict-third-party":
                        filter.Party = 2; filter.StrictParty = true; break;
                    case "strict1p" or "strict-first-party":
                        filter.Party = 1; filter.StrictParty = true; break;
                    case "important":
                        filter.Important = true; break;
                    case "match-case":
                        filter.MatchCase = true; break;
                    case "badfilter":
                        badfilter = true; break;
                    case "domain" or "from":
                        if (value is null || (filter.Domains = DomainList.Parse(value, '|')) is null) return null;
                        break;
                    case "to":
                        if (value is null || (filter.To = DomainList.Parse(value, '|')) is null) return null;
                        break;
                    case "denyallow":
                        if (value is null) return null;
                        var allow = DomainList.Parse(value, '|');
                        if (allow is null) return null;
                        filter.To ??= new DomainList();
                        foreach (var d in allow.Include) filter.To.Exclude.Add(d);
                        break;
                    case "method":
                        if (value is null) return null;
                        if (value.Contains('~')) return null;
                        filter.Methods = value.ToLowerInvariant().Split('|', StringSplitOptions.RemoveEmptyEntries);
                        break;
                    case "redirect" or "redirect-rule":
                        if (value is null && !exception) return null;
                        filter.RedirectRuleOnly = name == "redirect-rule";
                        ParseRedirect(filter, value ?? "");
                        break;
                    case "empty":
                        filter.Redirect = "empty"; break;
                    case "mp4":
                        filter.Redirect = "noop-1s.mp4"; break;
                    case "generichide" or "ghide":
                        filter.GenericHide = true; break;
                    case "elemhide" or "ehide":
                        filter.ElemHide = true; break;
                    case "specifichide" or "shide":
                        filter.SpecificHide = true; break;
                    default:
                        return null; // unknown option: better not to use the filter
                }
            }
        }

        if (badfilter)
        {
            _badFilters.Add(RemoveOption(raw, "badfilter"));
            return null;
        }

        // $popup goes to its own list; $document on an exception allows the whole page.
        if (include != RequestType.None || exclude != RequestType.None)
        {
            var types = include != RequestType.None ? include : RequestType.AnyRequest | RequestType.Document | RequestType.Popup;
            types &= ~exclude;
            if (include == RequestType.None) types &= ~(RequestType.Document | RequestType.Popup) | include;
            filter.Types = types;
        }
        if ((filter.Types & RequestType.Popup) != 0 && (filter.Types & ~RequestType.Popup & ~RequestType.Document) == 0)
            popupOnly = true;

        if (exception && (filter.Types & RequestType.Document) != 0 && include.HasFlag(RequestType.Document))
            filter.DocumentException = true;

        if (!ParsePattern(filter, pattern)) return null;

        if (filter.Exception && (filter.GenericHide || filter.ElemHide || filter.SpecificHide || filter.DocumentException))
        {
            CollectCosmeticException(filter);
            if (filter.DocumentException) _documentExceptions.Add(filter);
            return filter;
        }
        if (filter.GenericHide || filter.ElemHide || filter.SpecificHide) return null; // only meaningful as exceptions

        if (filter.Redirect is not null)
        {
            if (filter.Exception) _redirectExceptions.Add(filter);
            else
            {
                _redirects.Add(filter);
                if (!filter.RedirectRuleOnly) _block.Add(filter);
            }
            return filter;
        }

        if ((filter.Types & RequestType.Popup) != 0)
        {
            (filter.Exception ? _popupExceptions : _popups).Add(filter);
            if (popupOnly) return filter;
        }

        if (filter.Exception) _exceptions.Add(filter);
        else if (filter.Important) _important.Add(filter);
        else _block.Add(filter);
        return filter;
    }

    private static bool LooksLikeOptions(string options) =>
        options.Length > 0 && !options.Contains('/') || options.Contains("domain=", StringComparison.OrdinalIgnoreCase)
                                                     || options.Contains("redirect", StringComparison.OrdinalIgnoreCase);

    /// <summary>Options are separated by ',' but $domain= may hold regexes with commas, so /…/ is respected.</summary>
    private static IEnumerable<string> SplitOptions(string options)
    {
        var start = 0;
        var inRegex = false;
        for (var i = 0; i < options.Length; i++)
        {
            var c = options[i];
            if (c == '/' && (i == 0 || options[i - 1] is '=' or '|' or '/')) inRegex = !inRegex || options[i - 1] == '/';
            if (c == ',' && !inRegex)
            {
                yield return options[start..i];
                start = i + 1;
            }
        }
        yield return options[start..];
    }

    private static string RemoveOption(string raw, string option)
    {
        var dollar = raw.LastIndexOf('$');
        if (dollar < 0) return raw;
        var kept = raw[(dollar + 1)..].Split(',').Where(o => !o.Trim().Equals(option, StringComparison.OrdinalIgnoreCase)).ToList();
        return kept.Count == 0 ? raw[..dollar] : raw[..(dollar + 1)] + string.Join(',', kept);
    }

    private static void ParseRedirect(NetworkFilter filter, string value)
    {
        var colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out var priority))
        {
            filter.RedirectPriority = priority;
            value = value[..colon];
        }
        filter.Redirect = value; // "" on an exception means any redirect
    }

    private static RequestType? TypeOf(string name) => name switch
    {
        "script" => RequestType.Script,
        "image" => RequestType.Image,
        "stylesheet" or "css" => RequestType.Stylesheet,
        "object" => RequestType.Object,
        "xmlhttprequest" or "xhr" => RequestType.Xhr,
        "subdocument" or "frame" => RequestType.Subdocument,
        "ping" or "beacon" => RequestType.Ping,
        "media" => RequestType.Media,
        "font" => RequestType.Font,
        "websocket" => RequestType.Websocket,
        "other" => RequestType.Other,
        "document" or "doc" => RequestType.Document,
        "popup" => RequestType.Popup,
        "all" => RequestType.AnyRequest | RequestType.Document | RequestType.Popup,
        _ => null,
    };

    private static bool ParsePattern(NetworkFilter filter, string pattern)
    {
        if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
        {
            try
            {
                var options = RegexOptions.CultureInvariant | (filter.MatchCase ? 0 : RegexOptions.IgnoreCase);
                filter.Regex = new Regex(pattern[1..^1], options, TimeSpan.FromMilliseconds(20));
                filter.Pattern = pattern;
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (pattern.StartsWith("||", StringComparison.Ordinal)) { filter.Anchor = NetworkFilter.AnchorHost; pattern = pattern[2..]; }
        else if (pattern.StartsWith('|')) { filter.Anchor = NetworkFilter.AnchorLeft; pattern = pattern[1..]; }
        if (pattern.EndsWith('|') && !pattern.EndsWith("\\|", StringComparison.Ordinal)) { filter.RightAnchor = true; pattern = pattern[..^1]; }

        // Redundant wildcards: "*ads*" is "ads".
        if (filter.Anchor == NetworkFilter.AnchorNone) pattern = pattern.TrimStart('*');
        if (!filter.RightAnchor) pattern = pattern.TrimEnd('*');
        if (filter.Anchor == NetworkFilter.AnchorHost && pattern.StartsWith('*'))
        {
            filter.Anchor = NetworkFilter.AnchorNone;
            pattern = pattern.TrimStart('*');
        }

        if (!filter.MatchCase) pattern = pattern.ToLowerInvariant();
        filter.Pattern = pattern;
        filter.Segments = pattern.Split('*');
        return true;
    }

    /// <summary>"@@||site.com^$generichide": the site goes to the list every render process reads.</summary>
    private void CollectCosmeticException(NetworkFilter filter)
    {
        var hosts = new List<string>();
        if (filter.Anchor == NetworkFilter.AnchorHost && filter.Regex is null)
        {
            var p = filter.Pattern.TrimEnd('^', '/');
            if (p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) hosts.Add(p);
        }
        else if (filter.Pattern.Length == 0 && filter.Domains is { Include.Count: > 0 } domains)
        {
            hosts.AddRange(domains.Include);
        }

        foreach (var host in hosts)
        {
            if (filter.DocumentException) { NoCosmetic.Add(host); NoScriptlets.Add(host); }
            if (filter.ElemHide) NoCosmetic.Add(host);
            if (filter.GenericHide) NoGenericCosmetic.Add(host);
            if (filter.SpecificHide) NoSpecificCosmetic.Add(host);
        }
    }


    /// <summary>Whether to block (or redirect) this request.</summary>
    public BlockDecision Evaluate(RequestContext ctx)
    {
        if (_important.FindMatch(ctx) is { } important) return Decide(ctx, important.Tracker);
        var block = _block.FindMatch(ctx);
        if (block is null) return BlockDecision.Allow;
        if (_exceptions.FindMatch(ctx) is not null) return BlockDecision.Allow;
        return Decide(ctx, block.Tracker);
    }

    private BlockDecision Decide(RequestContext ctx, bool tracker)
    {
        // Redirect: the highest priority one without an exception.
        string? best = null;
        var bestPriority = int.MinValue;
        foreach (var filter in _redirects.FindAll(ctx))
        {
            if (filter.Redirect is null || filter.RedirectPriority < bestPriority) continue;
            var excepted = false;
            foreach (var ex in _redirectExceptions.FindAll(ctx))
            {
                if (string.IsNullOrEmpty(ex.Redirect) || ex.Redirect == filter.Redirect) { excepted = true; break; }
            }
            if (excepted) continue;
            best = filter.Redirect;
            bestPriority = filter.RedirectPriority;
        }
        return new BlockDecision(true, best, tracker);
    }

    /// <summary>Whether this popup (popunder) should be closed.</summary>
    public bool BlockPopup(RequestContext ctx) =>
        _popups.FindMatch(ctx) is not null && _popupExceptions.FindMatch(ctx) is null;

    /// <summary>Whether the whole page has an exception ("@@||site^$document"). Cached per page URL.</summary>
    public bool IsDocumentAllowed(string topUrl)
    {
        if (_documentExceptions.Count == 0) return false;
        if (_documentAllowed.TryGetValue(topUrl, out var cached)) return cached;
        var ctx = RequestContext.Create(topUrl, topUrl, RequestType.Document, "get", _psl);
        var allowed = ctx is not null && _documentExceptions.Any(f => f.Matches(ctx));
        if (_documentAllowed.Count > 2000) _documentAllowed.Clear();
        _documentAllowed[topUrl] = allowed;
        return allowed;
    }
}

/// <summary>
/// Filters indexed by token (a word of the pattern that must appear in the URL), like uBlock: each request only looks
/// at the filters whose words appear in its URL, not at all 100,000.
/// </summary>
internal sealed class FilterBucket(bool indexHosts)
{
    private static readonly HashSet<string> BadTokens = new(StringComparer.Ordinal)
    {
        "com", "http", "https", "icon", "images", "img", "js", "net", "news", "www", "html", "php", "org", "api", "static",
    };

    private readonly Dictionary<int, List<NetworkFilter>> _byToken = [];
    private readonly Dictionary<string, List<NetworkFilter>> _byDomain = new(StringComparer.Ordinal);
    private readonly List<NetworkFilter> _noToken = [];
    private readonly HashSet<string> _hosts = new(StringComparer.Ordinal);  // "||host^" without options

    public void Add(NetworkFilter filter)
    {
        if (indexHosts && IsPlainHost(filter))
        {
            _hosts.Add(filter.Pattern.TrimEnd('^'));
            return;
        }

        if (filter.Regex is null && BestToken(filter) is { } token)
        {
            if (!_byToken.TryGetValue(token, out var list)) _byToken[token] = list = [];
            list.Add(filter);
            return;
        }

        // Without a token: filters limited to certain sites are stored per site.
        if (filter.Domains is { Include.Count: > 0, Exclude.Count: 0 } domains && domains.Include.All(d => !d.EndsWith(".*", StringComparison.Ordinal)))
        {
            foreach (var domain in domains.Include)
            {
                if (!_byDomain.TryGetValue(domain, out var list)) _byDomain[domain] = list = [];
                list.Add(filter);
            }
            return;
        }
        _noToken.Add(filter);
    }

    public void RemoveWhere(Predicate<NetworkFilter> predicate)
    {
        foreach (var list in _byToken.Values) list.RemoveAll(predicate);
        foreach (var list in _byDomain.Values) list.RemoveAll(predicate);
        _noToken.RemoveAll(predicate);
        _hosts.RemoveWhere(h => predicate(new NetworkFilter { Raw = "||" + h + "^" }));
    }

    private static bool IsPlainHost(NetworkFilter f) =>
        f.Anchor == NetworkFilter.AnchorHost && f.Regex is null && !f.Exception && !f.Important && !f.RightAnchor &&
        f.Types == RequestType.AnyRequest && f.Party == 0 && f.Domains is null && f.To is null && f.Methods is null &&
        f.Redirect is null && f.Pattern.EndsWith('^') && f.Pattern.Length > 1 &&
        f.Pattern.AsSpan(0, f.Pattern.Length - 1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789.-_") < 0 &&
        f.Raw.Equals("||" + f.Pattern, StringComparison.Ordinal);

    public NetworkFilter? FindMatch(RequestContext ctx)
    {
        if (_hosts.Count > 0 && MatchesHost(ctx.Host)) return HostMatch;
        NetworkFilter? found = null;
        Visit(ctx, f =>
        {
            if (!f.Matches(ctx)) return true;
            found = f;
            return false;
        });
        return found;
    }

    public List<NetworkFilter> FindAll(RequestContext ctx)
    {
        var all = new List<NetworkFilter>();
        Visit(ctx, f =>
        {
            if (f.Matches(ctx)) all.Add(f);
            return true;
        });
        return all;
    }

    private static readonly NetworkFilter HostMatch = new() { Raw = "||host^" };

    private bool MatchesHost(string host)
    {
        for (var pos = 0; pos >= 0 && pos < host.Length;)
        {
            if (_hosts.Contains(pos == 0 ? host : host[pos..])) return true;
            var dot = host.IndexOf('.', pos);
            pos = dot < 0 ? -1 : dot + 1;
        }
        return false;
    }

    /// <summary>Walks the candidate filters for the URL; <paramref name="visit"/> returns false to stop.</summary>
    private void Visit(RequestContext ctx, Func<NetworkFilter, bool> visit)
    {
        if (_byToken.Count > 0)
        {
            Span<int> seen = stackalloc int[128];
            var seenCount = 0;
            var url = ctx.Url;
            var i = 0;
            while (i < url.Length)
            {
                while (i < url.Length && !IsTokenChar(url[i])) i++;
                var start = i;
                while (i < url.Length && IsTokenChar(url[i])) i++;
                if (i == start) break;
                var hash = Hash(url.AsSpan(start, i - start));
                var dup = false;
                for (var k = 0; k < seenCount; k++) if (seen[k] == hash) { dup = true; break; }
                if (dup) continue;
                if (seenCount < seen.Length) seen[seenCount++] = hash;
                if (!_byToken.TryGetValue(hash, out var list)) continue;
                foreach (var f in list) if (!visit(f)) return;
            }
        }

        if (_byDomain.Count > 0)
        {
            var host = ctx.DocHost;
            for (var pos = 0; pos >= 0 && pos < host.Length;)
            {
                if (_byDomain.TryGetValue(pos == 0 ? host : host[pos..], out var list))
                    foreach (var f in list) if (!visit(f)) return;
                var dot = host.IndexOf('.', pos);
                pos = dot < 0 ? -1 : dot + 1;
            }
        }

        foreach (var f in _noToken) if (!visit(f)) return;
    }

    private static bool IsTokenChar(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '%';

    private static int Hash(ReadOnlySpan<char> token)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var c in token) h = (h ^ c) * 16777619;
            return h;
        }
    }

    /// <summary>
    /// The pattern word that makes the best index: whole (not cut by '*' or by an unanchored start or end), as long as
    /// possible and not one of the words every URL contains.
    /// </summary>
    private static int? BestToken(NetworkFilter f)
    {
        if (f.MatchCase) return null; // the index is lowercase
        var p = f.Pattern;
        string? best = null;
        var bestBad = true;
        var i = 0;
        while (i < p.Length)
        {
            while (i < p.Length && !IsTokenChar(p[i])) i++;
            var start = i;
            while (i < p.Length && IsTokenChar(p[i])) i++;
            if (i == start) break;

            var leftOk = start > 0 ? p[start - 1] != '*' : f.Anchor != NetworkFilter.AnchorNone;
            var rightOk = i < p.Length ? p[i] != '*' : f.RightAnchor;
            if (!leftOk || !rightOk) continue;

            var token = p[start..i];
            var bad = BadTokens.Contains(token) || token.Length < 2;
            if (best is null || (bestBad && !bad) || (bad == bestBad && token.Length > best.Length))
            {
                best = token;
                bestBad = bad;
            }
        }
        return best is null ? null : Hash(best);
    }
}
