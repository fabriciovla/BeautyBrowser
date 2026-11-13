using System.Text.RegularExpressions;

namespace BeautyKit.Blocking;

/// <summary>Request type, as in filter lists ($script, $image…).</summary>
[Flags]
public enum RequestType
{
    None = 0,
    Other = 1 << 0,
    Script = 1 << 1,
    Image = 1 << 2,
    Stylesheet = 1 << 3,
    Object = 1 << 4,
    Subdocument = 1 << 5,
    Xhr = 1 << 6,
    Ping = 1 << 7,
    Media = 1 << 8,
    Font = 1 << 9,
    Websocket = 1 << 10,
    Document = 1 << 11,
    Popup = 1 << 12,

    /// <summary>What a filter without a type covers: everything but the document itself and popups.</summary>
    AnyRequest = Other | Script | Image | Stylesheet | Object | Subdocument | Xhr | Ping | Media | Font | Websocket,
}

/// <summary>The request being matched, already split up and lowercased except <see cref="RawUrl"/>.</summary>
public sealed class RequestContext
{
    public required string Url { get; init; }
    public required string RawUrl { get; init; }
    public required string Host { get; init; }
    public required int HostStart { get; init; }
    public required int HostEnd { get; init; }
    public required string Domain { get; init; }
    public required string DocHost { get; init; }
    public required string DocDomain { get; init; }
    public required string DocEntity { get; init; }
    public required RequestType Type { get; init; }
    public required string Method { get; init; }

    public bool ThirdParty => Domain != DocDomain;
    public bool StrictThirdParty => Host != DocHost;

    /// <summary>Returns null when the URL is not http(s) or ws(s).</summary>
    public static RequestContext? Create(string rawUrl, string? docUrlOrOrigin, RequestType type, string method, PublicSuffix psl)
    {
        var url = rawUrl.ToLowerInvariant();
        if (!TryHost(url, out var hostStart, out var hostEnd)) return null;
        var host = url[hostStart..hostEnd];

        var docHost = host;
        if (!string.IsNullOrEmpty(docUrlOrOrigin))
        {
            var doc = docUrlOrOrigin.ToLowerInvariant();
            if (TryHost(doc, out var ds, out var de)) docHost = doc[ds..de];
        }

        return new RequestContext
        {
            Url = url,
            RawUrl = rawUrl,
            Host = host,
            HostStart = hostStart,
            HostEnd = hostEnd,
            Domain = psl.Domain(host),
            DocHost = docHost,
            DocDomain = psl.Domain(docHost),
            DocEntity = psl.Entity(docHost),
            Type = type,
            Method = method.ToLowerInvariant(),
        };
    }

    /// <summary>Finds the host in a lowercased URL ("https://user@host:443/…").</summary>
    public static bool TryHost(string url, out int start, out int end)
    {
        start = end = 0;
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme <= 0) return false;
        var s = url.AsSpan(0, scheme);
        if (!(s is "http" or "https" or "ws" or "wss")) return false;

        start = scheme + 3;
        end = start;
        while (end < url.Length && url[end] is not ('/' or '?' or '#')) end++;
        var at = url.LastIndexOf('@', end - 1, end - start);
        if (at >= 0) start = at + 1;
        // Port, leaving bracketed IPv6 alone.
        if (start < end && url[start] == '[')
        {
            var close = url.IndexOf(']', start);
            if (close > 0 && close < end) end = close + 1;
        }
        else
        {
            var colon = url.IndexOf(':', start, end - start);
            if (colon >= 0) end = colon;
        }
        if (end > start && url[end - 1] == '.') end--; // "example.com." → "example.com"
        return end > start;
    }
}

/// <summary>Domains from $domain= or $to=, with "~" to exclude and "x.*" for any suffix.</summary>
internal sealed class DomainList
{
    public readonly HashSet<string> Include = new(StringComparer.Ordinal);
    public readonly HashSet<string> Exclude = new(StringComparer.Ordinal);

    public static DomainList? Parse(string value, char separator)
    {
        var list = new DomainList();
        foreach (var part in value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var negated = part.StartsWith('~');
            var name = (negated ? part[1..] : part).ToLowerInvariant();
            if (name.Length == 0 || name.StartsWith('/')) return null; // regex domains are not supported
            (negated ? list.Exclude : list.Include).Add(name);
        }
        return list.Include.Count + list.Exclude.Count == 0 ? null : list;
    }

    public bool Matches(string host, string entity)
    {
        if (Exclude.Count > 0 && Contains(Exclude, host, entity)) return false;
        return Include.Count == 0 || Contains(Include, host, entity);
    }

    /// <summary>Whether the host, one of its parent domains or its entity ("google.*") is in the set.</summary>
    public static bool Contains(HashSet<string> set, string host, string entity)
    {
        for (var pos = 0; pos >= 0 && pos < host.Length;)
        {
            if (set.Contains(pos == 0 ? host : host[pos..])) return true;
            var dot = host.IndexOf('.', pos);
            pos = dot < 0 ? -1 : dot + 1;
        }
        for (var pos = 0; pos >= 0 && pos < entity.Length;)
        {
            if (set.Contains(pos == 0 ? entity : entity[pos..])) return true;
            var dot = entity.IndexOf('.', pos);
            pos = dot < 0 || dot + 1 >= entity.Length - 1 ? -1 : dot + 1;
        }
        return false;
    }
}

/// <summary>A network filter, such as "||ads.example.com^$script,third-party".</summary>
internal sealed class NetworkFilter
{
    public const byte AnchorNone = 0, AnchorLeft = 1, AnchorHost = 2;

    public required string Raw;
    public bool Exception;
    public bool Important;
    public bool Tracker; // it comes from a privacy list
    public bool MatchCase;

    public byte Anchor;
    public bool RightAnchor;
    public string Pattern = "";
    public string[] Segments = [];   // the pattern split at '*'
    public Regex? Regex;

    public RequestType Types = RequestType.AnyRequest;
    public byte Party;          // 0 any, 1 first party, 2 third party
    public bool StrictParty;    // compare hosts rather than sites
    public DomainList? Domains; // $domain= (matched against the requesting page)
    public DomainList? To;      // $to= / $denyallow= (matched against the requested host)
    public string[]? Methods;

    // Exceptions only
    public bool DocumentException, GenericHide, ElemHide, SpecificHide;

    public string? Redirect;
    public int RedirectPriority;
    public bool RedirectRuleOnly;

    /// <summary>Whether the filter applies to this request, ignoring exceptions.</summary>
    public bool Matches(RequestContext ctx)
    {
        if ((Types & ctx.Type) == 0) return false;
        if (Party != 0)
        {
            var third = StrictParty ? ctx.StrictThirdParty : ctx.ThirdParty;
            if (third != (Party == 2)) return false;
        }
        if (Methods is not null && Array.IndexOf(Methods, ctx.Method) < 0) return false;
        if (Domains is not null && !Domains.Matches(ctx.DocHost, ctx.DocEntity)) return false;
        if (To is not null && !To.Matches(ctx.Host, "")) return false;
        return MatchesUrl(ctx);
    }

    public bool MatchesUrl(RequestContext ctx)
    {
        var url = MatchCase ? ctx.RawUrl : ctx.Url;
        if (Regex is not null)
        {
            try { return Regex.IsMatch(url); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        switch (Anchor)
        {
            case AnchorHost:
                // The pattern starts at the host or at any of its labels ("||example.com" matches "a.example.com").
                for (var p = ctx.HostStart; p < ctx.HostEnd; p++)
                {
                    if (p != ctx.HostStart && url[p - 1] != '.') continue;
                    if (Glob(url, p, anchored: true)) return true;
                }
                return false;
            case AnchorLeft:
                return Glob(url, 0, anchored: true);
            default:
                return Glob(url, 0, anchored: false);
        }
    }

    /// <summary>A pattern with '*' (anything) and '^' (separator or end of the URL).</summary>
    private bool Glob(string url, int pos, bool anchored)
    {
        var segments = Segments;
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            var last = i == segments.Length - 1;
            if (seg.Length == 0)
            {
                if (last && RightAnchor && segments.Length > 1) return true; // ends with '*'
                continue;
            }

            if (i == 0 && anchored)
            {
                var end = MatchAt(url, pos, seg);
                if (end < 0) return false;
                if (last && RightAnchor && end != url.Length) return false;
                pos = end;
                continue;
            }

            if (last && RightAnchor)
            {
                for (var p = pos; p <= url.Length; p++)
                    if (MatchAt(url, p, seg) == url.Length) return true;
                return false;
            }

            var found = -1;
            for (var p = pos; p <= url.Length - MinLength(seg); p++)
            {
                var end = MatchAt(url, p, seg);
                if (end >= 0) { found = end; break; }
            }
            if (found < 0) return false;
            pos = found;
        }
        return !RightAnchor || pos == url.Length || (segments.Length > 0 && segments[^1].Length == 0);
    }

    private static int MinLength(string seg) => seg.EndsWith('^') ? seg.Length - 1 : seg.Length;

    /// <summary>If <paramref name="seg"/> matches at <paramref name="pos"/>, returns where it ends; otherwise -1.</summary>
    private static int MatchAt(string url, int pos, string seg)
    {
        var p = pos;
        for (var i = 0; i < seg.Length; i++)
        {
            var c = seg[i];
            if (c == '^')
            {
                if (p == url.Length) { if (i == seg.Length - 1) return p; return -1; }
                if (!IsSeparator(url[p])) return -1;
                p++;
                continue;
            }
            if (p >= url.Length || url[p] != c) return -1;
            p++;
        }
        return p;
    }

    public static bool IsSeparator(char c) =>
        !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.' or '%') && c < 0x80;
}
