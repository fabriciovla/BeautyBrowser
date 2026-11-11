namespace BeautyKit.Blocking;

/// <summary>
/// Registrable domain of a host ("a.b.google.com" → "google.com", "www.clarin.com.ar" → "clarin.com.ar"), using the
/// Public Suffix List when it has been downloaded and a heuristic otherwise. Immutable and thread-safe.
/// </summary>
public sealed class PublicSuffix
{
    private readonly HashSet<string> _rules;
    private readonly HashSet<string> _wildcards;   // "*.ck" → "ck"
    private readonly HashSet<string> _exceptions;  // "!www.ck" → "www.ck"

    private PublicSuffix(HashSet<string> rules, HashSet<string> wildcards, HashSet<string> exceptions)
    {
        _rules = rules;
        _wildcards = wildcards;
        _exceptions = exceptions;
    }

    /// <summary>Without the list: common two-part suffixes (com.ar, co.uk…) or else the last label.</summary>
    public static PublicSuffix Fallback { get; } = new([], [], []);

    public static PublicSuffix Parse(string text)
    {
        var rules = new HashSet<string>(StringComparer.Ordinal);
        var wildcards = new HashSet<string>(StringComparer.Ordinal);
        var exceptions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var space = line.IndexOf(' ');
            if (space > 0) line = line[..space];
            line = line.ToLowerInvariant();
            if (line.StartsWith('!')) exceptions.Add(line[1..]);
            else if (line.StartsWith("*.", StringComparison.Ordinal)) wildcards.Add(line[2..]);
            else rules.Add(line);
        }
        return new PublicSuffix(rules, wildcards, exceptions);
    }

    /// <summary>Length of the public suffix at the end of <paramref name="host"/> ("com.ar" → 6); 0 for IPs.</summary>
    public int SuffixLength(string host)
    {
        if (host.Length == 0 || IsIp(host)) return 0;

        if (_rules.Count == 0)
        {
            // "xx.yy" with a two-letter country code and a generic second level counts as one suffix.
            var last = host.LastIndexOf('.');
            if (last < 0) return host.Length;
            var prev = host.LastIndexOf('.', last - 1);
            var second = host[(prev + 1)..last];
            var twoPart = host.Length - last - 1 == 2 &&
                          second is "com" or "co" or "org" or "net" or "gov" or "gob" or "edu" or "ac" or "or" or "ne" or "mil";
            return twoPart ? host.Length - prev - 1 : host.Length - last - 1;
        }

        // Longest suffix first, so the first rule that matches is the longest one.
        for (var pos = 0; pos >= 0 && pos < host.Length; pos = NextLabel(host, pos))
        {
            var suffix = pos == 0 ? host : host[pos..];
            if (_exceptions.Contains(suffix))
            {
                var dot = suffix.IndexOf('.');
                return dot < 0 ? 0 : suffix.Length - dot - 1;
            }
            if (_rules.Contains(suffix)) return suffix.Length;
            var firstDot = suffix.IndexOf('.');
            if (firstDot > 0 && _wildcards.Contains(suffix[(firstDot + 1)..])) return suffix.Length;
        }
        // Implicit "*" rule: the last label.
        var lastDot = host.LastIndexOf('.');
        return lastDot < 0 ? host.Length : host.Length - lastDot - 1;
    }

    public string Domain(string host)
    {
        var suffix = SuffixLength(host);
        if (suffix == 0 || suffix >= host.Length) return host;
        var start = host.LastIndexOf('.', host.Length - suffix - 2);
        return start < 0 ? host : host[(start + 1)..];
    }

    /// <summary>uBlock "entity": the host with its public suffix replaced by "*" ("www.google.com.ar" → "www.google.*").</summary>
    public string Entity(string host)
    {
        var suffix = SuffixLength(host);
        if (suffix == 0 || suffix >= host.Length) return "";
        return host[..(host.Length - suffix)] + "*";
    }

    /// <summary>Country-code TLDs that exist (used when the list has not been downloaded yet).</summary>
    private static readonly HashSet<string> FallbackCountryCodes = new(
        ("ac ad ae af ag ai al am ao aq ar as at au aw ax az ba bb bd be bf bg bh bi bj bm bn bo br bs bt bw by bz " +
         "ca cc cd cf cg ch ci ck cl cm cn co cr cu cv cw cx cy cz de dj dk dm do dz ec ee eg er es et eu fi fj fk fm fo fr " +
         "ga gd ge gf gg gh gi gl gm gn gp gq gr gs gt gu gw gy hk hm hn hr ht hu id ie il im in io iq ir is it je jm jo jp " +
         "ke kg kh ki km kn kp kr kw ky kz la lb lc li lk lr ls lt lu lv ly ma mc md me mg mh mk ml mm mn mo mp mq mr ms mt " +
         "mu mv mw mx my mz na nc ne nf ng ni nl no np nr nu nz om pa pe pf pg ph pk pl pm pn pr ps pt pw py qa re ro rs ru " +
         "rw sa sb sc sd se sg sh si sk sl sm sn so sr ss st su sv sx sy sz tc td tf tg th tj tk tl tm tn to tr tt tv tw tz " +
         "ua ug uk us uy uz va vc ve vg vi vn vu wf ws ye yt za zm zw").Split(' '), StringComparer.Ordinal);

    /// <summary>Generic TLDs people type most (used when the list has not been downloaded yet).</summary>
    private static readonly HashSet<string> FallbackGenericTlds = new(
        ("com net org edu gov mil int info biz name pro mobi asia tel travel jobs museum coop aero cat xxx app dev page " +
         "blog shop store online site tech cloud xyz top club live news art wiki email link media agency studio design " +
         "games network digital world today zone space website fun life run").Split(' '), StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="label"/> is a real top-level domain ("com", "ar"), as opposed to a file extension or
    /// a name that merely follows a dot ("txt", "exe", "js"). Case-insensitive.
    /// </summary>
    public bool IsKnownTld(string label)
    {
        if (label.Length == 0) return false;
        label = label.ToLowerInvariant();
        if (label.StartsWith("xn--", StringComparison.Ordinal)) return true; // Punycode: only IDN TLDs look like this.
        if (_rules.Count == 0)
            return FallbackCountryCodes.Contains(label) || FallbackGenericTlds.Contains(label);
        return _rules.Contains(label) || _wildcards.Contains(label);
    }

    private static int NextLabel(string host, int pos)
    {
        var dot = host.IndexOf('.', pos);
        return dot < 0 ? -1 : dot + 1;
    }

    public static bool IsIp(string host) =>
        host.Length > 0 && (host[0] == '[' || host.Contains(':') ||
                            (char.IsDigit(host[^1]) && System.Net.IPAddress.TryParse(host, out _)));
}
