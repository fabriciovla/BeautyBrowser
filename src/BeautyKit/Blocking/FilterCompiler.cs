using System.Text;
using System.Text.Json;

namespace BeautyKit.Blocking;

/// <summary>A downloaded filter list.</summary>
/// <param name="Trusted">uBlock's own lists may use "trusted-…" scriptlets; third-party lists may not.</param>
internal sealed record FilterListText(string Name, string Text, bool Trusted);

/// <summary>Parses filter lists (uBlock Origin / Adblock Plus syntax) into the network engine and the page rules.</summary>
internal sealed class FilterCompiler
{
    private readonly NetworkEngine _network;
    private readonly PageRules _rules = new();
    private readonly HashSet<string> _genericExceptions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenCosmetic = new(StringComparer.Ordinal);

    public int NetworkCount { get; private set; }
    public int CosmeticCount { get; private set; }
    public int ScriptletCount { get; private set; }
    public int Rejected { get; private set; }

    public FilterCompiler(PublicSuffix psl) => _network = new NetworkEngine(psl);

    /// <summary>The environment uBlock's "!#if" directives see: a desktop Chromium.</summary>
    private static readonly HashSet<string> TrueConditions = new(StringComparer.Ordinal)
    {
        "env_chromium", "cap_user_stylesheet", "true",
    };

    public void AddList(FilterListText list, Func<string, string?> include)
    {
        var stack = new Stack<(bool ParentActive, bool Condition)>();
        var active = true;
        foreach (var rawLine in list.Text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // uBlock preprocessor directives.
            if (line.StartsWith("!#", StringComparison.Ordinal))
            {
                if (line.StartsWith("!#if ", StringComparison.Ordinal))
                {
                    var condition = Evaluate(line[5..]);
                    stack.Push((active, condition));
                    active = active && condition;
                }
                else if (line.StartsWith("!#else", StringComparison.Ordinal) && stack.Count > 0)
                {
                    var (parent, condition) = stack.Peek();
                    active = parent && !condition;
                }
                else if (line.StartsWith("!#endif", StringComparison.Ordinal) && stack.Count > 0)
                {
                    active = stack.Pop().ParentActive;
                }
                else if (active && line.StartsWith("!#include ", StringComparison.Ordinal))
                {
                    var text = include(line[10..].Trim());
                    if (text is not null) AddList(list with { Text = text }, include);
                }
                continue;
            }
            if (!active) continue;
            AddLine(line, list.Trusted);
        }
    }

    private void AddLine(string line, bool trusted)
    {
        if (line[0] is '!' or '[') return; // comment or header

        // "hosts" format (0.0.0.0 domain).
        if (line.StartsWith("0.0.0.0 ", StringComparison.Ordinal) || line.StartsWith("127.0.0.1 ", StringComparison.Ordinal))
        {
            var host = line[(line.IndexOf(' ') + 1)..].Trim();
            var hash = host.IndexOf('#');
            if (hash >= 0) host = host[..hash].Trim();
            if (host.Length > 0 && !host.Contains(' ') && host != "localhost" && host != "0.0.0.0")
                AddNetwork("||" + host + "^");
            return;
        }

        if (FindCosmeticMarker(line, out var markerAt, out var marker))
        {
            AddCosmetic(line[..markerAt], marker, line[(markerAt + marker.Length)..], trusted);
            return;
        }

        if (line.Contains(' ') && !line.Contains("$", StringComparison.Ordinal)) { Rejected++; return; }
        AddNetwork(line);
    }

    private void AddNetwork(string line)
    {
        if (_network.Add(line)) NetworkCount++;
        else Rejected++;
    }


    private static readonly string[] Markers = ["#@?#", "#@$#", "#@%#", "#?#", "#$#", "#%#", "#@#", "##"];

    private static bool FindCosmeticMarker(string line, out int at, out string marker)
    {
        at = -1;
        marker = "";
        for (var i = line.IndexOf('#'); i >= 0; i = line.IndexOf('#', i + 1))
        {
            foreach (var m in Markers)
            {
                if (string.CompareOrdinal(line, i, m, 0, m.Length) != 0) continue;
                // What comes before are domains: no '/', '|', '^' or '$' (otherwise it is a network filter with a '#').
                if (line.AsSpan(0, i).IndexOfAny("/|^$?=&") >= 0) return false;
                at = i;
                marker = m;
                return true;
            }
        }
        return false;
    }

    private static readonly string[] ProceduralOperators =
    [
        ":has-text(", ":upward(", ":remove(", ":style(", ":matches-css(", ":matches-css-before(", ":matches-css-after(",
        ":min-text-length(", ":xpath(", ":matches-attr(", ":matches-prop(", ":watch-attr(", ":remove-attr(",
        ":remove-class(", ":matches-path(", ":matches-media(", ":others(", ":-abp-has(", ":-abp-contains(",
        ":contains(", ":if(", ":if-not(", ":spath(", ":nth-ancestor(",
    ];

    private void AddCosmetic(string domainsText, string marker, string body, bool trusted)
    {
        if (marker is "#$#" or "#@$#" or "#%#" or "#@%#") return; // AdGuard/ABP syntax: unsupported
        if (body.Length == 0) return;
        var exception = marker.Contains('@');

        var includes = new List<string>();
        var excludes = new List<string>();
        var everywhere = false; // "*##…": every site
        var skippedInclude = false;
        foreach (var part in domainsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var negated = part.StartsWith('~');
            var name = (negated ? part[1..] : part).ToLowerInvariant();
            if (name == "*") { everywhere |= !negated; continue; }
            // Regex domains and "inside an iframe of" (>>) are unsupported; that entry is ignored.
            if (name.Length == 0 || name.StartsWith('/') || name.EndsWith(">>", StringComparison.Ordinal))
            {
                skippedInclude |= !negated;
                continue;
            }
            (negated ? excludes : includes).Add(name);
        }
        // If it only had sites we do not understand, it must not turn generic.
        if (skippedInclude && includes.Count == 0 && !everywhere) return;

        // Scriptlets: ##+js(name, arg1, …)
        if (body.StartsWith("+js(", StringComparison.Ordinal))
        {
            if (!body.EndsWith(')')) return;
            if (everywhere) includes.Add("*");
            AddScriptlet(includes, excludes, body[4..^1], exception, trusted);
            return;
        }
        if (body[0] == '^') return; // HTML filters (Firefox only)
        if (body.Contains('{') || body.Contains('}')) return; // no arbitrary CSS

        if (!_seenCosmetic.Add(domainsText + marker + body)) return;
        CosmeticCount++;

        var procedural = marker.Contains('?') || IsProcedural(body);
        // "selector:style(…)" with nothing else procedural is a plain CSS rule.
        string? style = null;
        if (procedural && TryPlainStyle(body, out var styleSelector, out var declarations))
        {
            style = $"{styleSelector}{{{declarations}}}";
            procedural = false;
        }

        if (exception)
        {
            if (includes.Count == 0) _genericExceptions.Add(body);
            foreach (var host in includes) Add(_rules.Exceptions, host, body);
            return;
        }

        if (includes.Count == 0)
        {
            // Generic. Generic procedural and style filters are expensive and rare; uBlock skips them too.
            if (procedural || style is not null) return;
            _rules.Generic.Add(body);
            foreach (var host in excludes) Add(_rules.Exceptions, host, body);
            return;
        }

        foreach (var host in includes)
        {
            if (style is not null) Add(_rules.Styles, host, style);
            else if (procedural) Add(_rules.Procedural, host, body);
            else Add(_rules.Specific, host, body);
        }
        foreach (var host in excludes) Add(_rules.Exceptions, host, body);
    }

    private static bool IsProcedural(string selector)
    {
        foreach (var op in ProceduralOperators)
            if (selector.Contains(op, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>"div.ad:style(height: 0 !important)" → selector and declarations, when there is no other procedural operator.</summary>
    private static bool TryPlainStyle(string body, out string selector, out string declarations)
    {
        selector = declarations = "";
        if (!body.EndsWith(')')) return false;
        var at = body.LastIndexOf(":style(", StringComparison.Ordinal);
        if (at <= 0) return false;
        selector = body[..at];
        if (IsProcedural(selector)) return false;
        declarations = body[(at + 7)..^1];
        // For safety, nothing that loads resources.
        return !declarations.Contains("url(", StringComparison.OrdinalIgnoreCase) && !declarations.Contains('\\');
    }

    private void AddScriptlet(List<string> includes, List<string> excludes, string argsText, bool exception, bool trusted)
    {
        var args = ParseScriptletArgs(argsText);
        if (args.Count == 0)
        {
            // "#@#+js()": no scriptlets on that site.
            if (exception) foreach (var host in includes) Add(_rules.Scriptlets, host, "-[]");
            return;
        }
        var name = args[0];
        if (name.EndsWith(".js", StringComparison.Ordinal)) name = name[..^3];
        args[0] = name;
        if (!trusted && name.StartsWith("trusted-", StringComparison.Ordinal)) return;

        var json = JsonSerializer.Serialize(args, BlockingJson.Default.ListString);
        ScriptletCount++;

        if (exception)
        {
            // "#@#+js(x)" without domains: an exception everywhere.
            if (includes.Count == 0) Add(_rules.Scriptlets, "", "-" + json);
            foreach (var host in includes) Add(_rules.Scriptlets, host, "-" + json);
            return;
        }
        if (includes.Count == 0) return; // uBlock does not allow generic scriptlets
        foreach (var host in includes) Add(_rules.Scriptlets, host, "+" + json);
        foreach (var host in excludes) Add(_rules.Scriptlets, host, "-" + json);
    }

    /// <summary>Arguments of +js(…), with uBlock's rules (quotes, escaped "\,").</summary>
    public static List<string> ParseScriptletArgs(string s)
    {
        var args = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;

            var q = s[i];
            if (q is '"' or '\'' or '`')
            {
                var end = NextUnescaped(s, q, i + 1);
                if (end < s.Length)
                {
                    var after = end + 1;
                    while (after < s.Length && char.IsWhiteSpace(s[after])) after++;
                    if (after == s.Length || s[after] == ',')
                    {
                        args.Add(Unescape(s[(i + 1)..end], q));
                        i = after == s.Length ? s.Length : after + 1;
                        continue;
                    }
                }
            }

            var sep = NextUnescaped(s, ',', i);
            args.Add(Unescape(s[i..sep].TrimEnd(), ','));
            i = sep < s.Length ? sep + 1 : s.Length;
        }
        return args;
    }

    private static int NextUnescaped(string s, char c, int from)
    {
        for (var p = s.IndexOf(c, from); p >= 0; p = s.IndexOf(c, p + 1))
        {
            var slashes = 0;
            for (var k = p - 1; k >= 0 && s[k] == '\\'; k--) slashes++;
            if (slashes % 2 == 0) return p;
        }
        return s.Length;
    }

    private static string Unescape(string arg, char c) => arg.Replace("\\" + c, c.ToString(), StringComparison.Ordinal);

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }


    private static bool Evaluate(string expression)
    {
        var tokens = new List<string>();
        for (var i = 0; i < expression.Length;)
        {
            var c = expression[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '(' or ')' or '!') { tokens.Add(c.ToString()); i++; continue; }
            if (i + 1 < expression.Length && (expression.AsSpan(i, 2) is "&&" or "||")) { tokens.Add(expression.Substring(i, 2)); i += 2; continue; }
            var start = i;
            while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_')) i++;
            if (i == start) { i++; continue; }
            tokens.Add(expression[start..i]);
        }
        var pos = 0;
        return Or(tokens, ref pos);

        static bool Or(List<string> t, ref int p)
        {
            var value = And(t, ref p);
            while (p < t.Count && t[p] == "||") { p++; value |= And(t, ref p); }
            return value;
        }
        static bool And(List<string> t, ref int p)
        {
            var value = Not(t, ref p);
            while (p < t.Count && t[p] == "&&") { p++; value &= Not(t, ref p); }
            return value;
        }
        static bool Not(List<string> t, ref int p)
        {
            if (p < t.Count && t[p] == "!") { p++; return !Not(t, ref p); }
            if (p < t.Count && t[p] == "(")
            {
                p++;
                var value = Or(t, ref p);
                if (p < t.Count && t[p] == ")") p++;
                return value;
            }
            return p < t.Count && TrueConditions.Contains(t[p++]);
        }
    }


    public (NetworkEngine Network, PageRules Rules) Build()
    {
        _network.Freeze();
        if (_genericExceptions.Count > 0) _rules.Generic.RemoveAll(_genericExceptions.Contains);
        _rules.Generic = _rules.Generic.Distinct(StringComparer.Ordinal).ToList();
        _rules.NoGeneric = [.. _network.NoGenericCosmetic];
        _rules.NoCosmetic = [.. _network.NoCosmetic];
        _rules.NoSpecific = [.. _network.NoSpecificCosmetic];
        _rules.NoScriptlets = [.. _network.NoScriptlets];
        return (_network, _rules);
    }

    public string Summary() =>
        new StringBuilder()
            .Append($"network={NetworkCount} cosmetic={CosmeticCount} scriptlets={ScriptletCount} rejected={Rejected} ")
            .Append($"generic={_rules.Generic.Count} sites={_rules.Specific.Count}")
            .ToString();
}
