using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using BeautyKit.Blocking;

namespace BeautyKit;

/// <summary>Turns what the user typed in the address bar into a URL to load or a search.</summary>
public static partial class AddressInput
{
    /// <summary>
    /// Decides between navigating and searching:
    /// explicit schemes (http, https, file, about, chrome, view-source, data and the start page) navigate;
    /// IPs and <c>host:port</c> go over plain http (routers and dev servers rarely have https);
    /// "name.tld" goes over https only when the last label is a real TLD ("node.js" and "archivo.txt" are searches);
    /// anything else, including <c>javascript:</c>, is searched.
    /// </summary>
    public static string ToUrl(string input, string searchTemplate, PublicSuffix psl, string startScheme)
    {
        input = input.Trim();

        if (input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
            return "http://" + input;

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && IsNavigableScheme(uri.Scheme, startScheme))
            return uri.AbsoluteUri; // Not ToString(), which un-escapes %20 and %2F.

        if (TryAddress(input, psl) is { } url) return url;

        return string.Format(searchTemplate, Uri.EscapeDataString(input));
    }

    private static bool IsNavigableScheme(string scheme, string startScheme) =>
        scheme is "http" or "https" or "file" or "about" or "chrome" or "view-source" or "data" ||
        string.Equals(scheme, startScheme, StringComparison.Ordinal);

    private static string? TryAddress(string input, PublicSuffix psl)
    {
        if (input.Length == 0 || input.Any(char.IsWhiteSpace)) return null;
        // Never turn script URLs into something that loads, whatever follows them ("javascript:1" parses as host:port).
        if (input.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return null;

        // authority = host[:port]; the path, query and fragment follow it. Without an explicit scheme an "@" before the
        // host ("usuario@gmail.com", an email) is a search, as in Chrome, never user info.
        var authorityEnd = input.IndexOfAny(['/', '?', '#']);
        var authority = authorityEnd < 0 ? input : input[..authorityEnd];
        var rest = authorityEnd < 0 ? "" : input[authorityEnd..];
        if (authority.Contains('@')) return null;
        var hostPort = authority;
        if (hostPort.Length == 0) return null;

        string host, port = "";
        var isIp = false;

        if (hostPort[0] == '[')
        {
            // [::1] or [::1]:8080
            var close = hostPort.IndexOf(']');
            if (close < 0 || !IsIPv6(hostPort[1..close])) return null;
            host = hostPort[..(close + 1)];
            var tail = hostPort[(close + 1)..];
            if (tail.Length > 0 && !TryPort(tail, out port)) return null;
            isIp = true;
        }
        else if (hostPort.Count(c => c == ':') > 1)
        {
            // Bare IPv6 ("::1", "fe80::1"): it has no room for a port, so it has to be the whole address.
            if (!IsIPv6(hostPort)) return null;
            host = "[" + hostPort + "]";
            isIp = true;
        }
        else
        {
            var colon = hostPort.IndexOf(':');
            host = colon < 0 ? hostPort : hostPort[..colon];
            if (colon >= 0 && !TryPort(hostPort[colon..], out port)) return null;
            if (!IsHostName(host)) return null;
            isIp = IPv4().IsMatch(host) && host.Split('.').All(octet => int.Parse(octet) <= 255);
        }

        string scheme;
        if (isIp || port.Length > 0)
        {
            scheme = "http://"; // IPs and host:port are routers, local servers, dev servers…
        }
        else
        {
            // A dotted name only counts as a site when its last label is a real TLD.
            var name = host.TrimEnd('.');
            var lastDot = name.LastIndexOf('.');
            if (lastDot <= 0) return null;
            var tld = name[(lastDot + 1)..];
            if (!tld.All(c => char.IsLetter(c) || c == '-') || !psl.IsKnownTld(tld)) return null;
            scheme = "https://";
        }

        var candidate = scheme + host + (port.Length > 0 ? ":" + port : "") + rest;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : null;
    }

    private static bool IsIPv6(string text) =>
        IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>":8080" → "8080" (1 to 65535).</summary>
    private static bool TryPort(string text, out string port)
    {
        port = text.Length > 1 ? text[1..] : "";
        return text[0] == ':' && port.Length is >= 1 and <= 5 && port.All(char.IsAsciiDigit) && int.Parse(port) is >= 1 and <= 65535;
    }

    /// <summary>Letters (any alphabet), digits, "-", "_" and dots between non-empty labels; one trailing dot is allowed.</summary>
    private static bool IsHostName(string host)
    {
        var name = host.EndsWith('.') ? host[..^1] : host;
        if (name.Length == 0) return false;
        return name.Split('.').All(label => label.Length > 0 && label.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
    }

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$")]
    private static partial Regex IPv4();
}
