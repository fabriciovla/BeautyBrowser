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

    private static int NextLabel(string host, int pos)
    {
        var dot = host.IndexOf('.', pos);
        return dot < 0 ? -1 : dot + 1;
    }

    public static bool IsIp(string host) =>
        host.Length > 0 && (host[0] == '[' || host.Contains(':') ||
                            (char.IsDigit(host[^1]) && System.Net.IPAddress.TryParse(host, out _)));
}
