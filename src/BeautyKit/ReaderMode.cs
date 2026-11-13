using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using CefSharp;

namespace BeautyKit;

/// <summary>An article taken out of a page, ready to be shown in the reader.</summary>
internal sealed record ReaderArticle(string Title, string Byline, string Site, string Lang, string Dir, string Html, int Words, string Original);

/// <summary>
/// Reading mode: the page's article (found by Mozilla's Readability.js, Apache 2.0) shown alone, with our typography,
/// in beauty://reader/… so no script of the site runs there. The address keeps the original page in "u"; if the
/// article is no longer in memory (the app restarted), the page sends the tab back to the original.
/// </summary>
public static class ReaderMode
{
    public const string Prefix = "beauty://reader/";
    public const int MinFontSize = 14, MaxFontSize = 28, DefaultFontSize = 19;

    private const int Capacity = 40;
    private static readonly ConcurrentDictionary<string, ReaderArticle> Articles = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> Order = new();
    private static int _fontSize = DefaultFontSize;

    public static bool Is(string? url) => url is not null && url.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Text size of the reader, in px. The app saves it (see <see cref="FontSizeChanged"/>).</summary>
    public static int FontSize
    {
        get => _fontSize;
        set => _fontSize = Math.Clamp(value, MinFontSize, MaxFontSize);
    }

    /// <summary>The size was changed from the reader's own buttons. Raised on Chromium's thread.</summary>
    public static event EventHandler<int>? FontSizeChanged;

    /// <summary>The address of the page the tab is really on: the article's, while the reader shows it.</summary>
    public static string? PageUrl(string? url) => TryOriginal(url, out var original) ? original : url;

    /// <summary>The page the reader shows ("https://site/article"), or false if <paramref name="url"/> is not a reader address.</summary>
    public static bool TryOriginal(string? url, out string original)
    {
        original = "";
        if (!Is(url)) return false;
        var query = Query(url!);
        if (!query.TryGetValue("u", out var u) || !Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        original = u;
        return true;
    }

    internal static string Add(ReaderArticle article)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        Articles[token] = article;
        Order.Enqueue(token);
        while (Order.Count > Capacity && Order.TryDequeue(out var old)) Articles.TryRemove(old, out _);
        return token;
    }

    internal static string UrlFor(string token, string original) =>
        $"{Prefix}{token}?u={Uri.EscapeDataString(original)}";

    private static Dictionary<string, string> Query(string url)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = url.IndexOf('?');
        if (at < 0) return result;
        foreach (var pair in url[(at + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }

    internal static IResourceHandler Serve(string url, bool dark)
    {
        var query = Query(url);
        var path = url[Prefix.Length..];
        var token = path.Split('?')[0].Trim('/');
        var original = query.GetValueOrDefault("u", "");

        // The text size changes with a link in the page (?s=): the reader remembers it for the next article.
        if (query.TryGetValue("s", out var s) && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
        {
            var clamped = Math.Clamp(size, MinFontSize, MaxFontSize);
            if (clamped != _fontSize)
            {
                _fontSize = clamped;
                FontSizeChanged?.Invoke(null, clamped);
            }
        }

        if (!Articles.TryGetValue(token, out var article))
        {
            // Gone (the app restarted, or too many were opened since): back to the page it came from.
            var safe = Uri.TryCreate(original, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : "about:blank";
            var html = $"<!doctype html><meta charset=utf-8><meta http-equiv=\"refresh\" content=\"0;url={WebUtility.HtmlEncode(safe)}\"><a href=\"{WebUtility.HtmlEncode(safe)}\">{WebUtility.HtmlEncode(safe)}</a>";
            return ResourceHandler.FromString(html, Encoding.UTF8, mimeType: "text/html");
        }
        return ResourceHandler.FromString(Html(token, article, _fontSize, dark), Encoding.UTF8, mimeType: "text/html");
    }

    private static string Html(string token, ReaderArticle a, int size, bool dark)
    {
        string E(string text) => WebUtility.HtmlEncode(text);
        string Size(int value) => $"{Prefix}{token}?u={Uri.EscapeDataString(a.Original)}&amp;s={Math.Clamp(value, MinFontSize, MaxFontSize)}";

        var minutes = Math.Max(1, (int)Math.Round(a.Words / 200.0));
        var meta = string.Join(" · ", new[]
        {
            a.Byline.Length > 0 ? E(a.Byline) : "",
            a.Site.Length > 0 ? E(a.Site) : "",
            L.T($"{minutes} min read", $"{minutes} min de lectura"),
        }.Where(part => part.Length > 0));

        var lang = a.Lang.Length > 0 && a.Lang.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? a.Lang : L.T("en", "es");
        var dir = a.Dir is "rtl" or "ltr" ? a.Dir : "ltr";
        var theme = dark
            ? "--bg:#1a1a1c;--fg:#e8e8ea;--muted:#9a9aa0;--line:#2c2c30;--link:#8ab4ff;--chip:#2a2a2e;--chiphover:#36363b;"
            : "--bg:#fbfbfa;--fg:#1d1d1f;--muted:#6b6b72;--line:#e6e6e3;--link:#2f5fd0;--chip:#efefec;--chiphover:#e3e3df;";

        return $$"""
            <!doctype html>
            <html lang="{{lang}}" dir="{{dir}}">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src https: http: data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{E(a.Title)}}</title>
            <style>
              :root { {{theme}} color-scheme: {{(dark ? "dark" : "light")}}; }
              * { box-sizing: border-box; }
              html { background: var(--bg); }
              body { margin: 0; color: var(--fg); background: var(--bg);
                     font: {{size}}px/1.7 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; -webkit-font-smoothing: antialiased; }
              .bar { position: sticky; top: 0; z-index: 2; display: flex; justify-content: flex-end; align-items: center; gap: 6px;
                     padding: 10px 18px; background: color-mix(in srgb, var(--bg) 88%, transparent); backdrop-filter: blur(10px); }
              .bar a { color: var(--fg); text-decoration: none; background: var(--chip); border-radius: 8px; padding: 5px 12px;
                       font: 600 13px "Segoe UI Variable Text", "Segoe UI", sans-serif; }
              .bar a:hover { background: var(--chiphover); }
              .bar .gap { flex: 1; }
              main { max-width: 42em; margin: 0 auto; padding: 18px 22px 120px; }
              h1.title { font-size: 1.85em; line-height: 1.25; margin: 12px 0 8px; letter-spacing: -0.01em; }
              .meta { color: var(--muted); font-size: 0.8em; margin: 0 0 28px; padding-bottom: 18px; border-bottom: 1px solid var(--line); }
              article h1, article h2, article h3, article h4 { line-height: 1.3; margin: 1.6em 0 0.5em; }
              article h1 { font-size: 1.5em; } article h2 { font-size: 1.3em; } article h3 { font-size: 1.12em; }
              article p, article ul, article ol, article blockquote, article pre, article figure, article table { margin: 0 0 1.1em; }
              article a { color: var(--link); }
              article img { max-width: 100%; height: auto; border-radius: 8px; display: block; margin: 1.2em auto; }
              article figure { margin: 1.4em 0; } article figcaption { color: var(--muted); font-size: 0.8em; text-align: center; margin-top: -0.6em; }
              article blockquote { border-inline-start: 3px solid var(--line); padding: 0 0 0 1em; color: var(--muted); }
              article pre { overflow: auto; background: var(--chip); padding: 12px 14px; border-radius: 8px; font-size: 0.85em; line-height: 1.5; }
              article code { font-family: "Cascadia Code", Consolas, monospace; font-size: 0.88em; }
              article table { border-collapse: collapse; display: block; overflow-x: auto; }
              article td, article th { border: 1px solid var(--line); padding: 6px 10px; }
              article hr { border: 0; border-top: 1px solid var(--line); margin: 2em 0; }
            </style>
            </head>
            <body>
            <div class="bar">
              <a href="{{E(a.Original)}}" title="{{L.T("Back to the page", "Volver a la página")}}">&#x2715;&ensp;{{L.T("Close reader", "Cerrar lector")}}</a>
              <span class="gap"></span>
              <a href="{{Size(size - 1)}}" title="{{L.T("Smaller text", "Texto más chico")}}" aria-label="{{L.T("Smaller text", "Texto más chico")}}">A&minus;</a>
              <a href="{{Size(size + 1)}}" title="{{L.T("Larger text", "Texto más grande")}}" aria-label="{{L.T("Larger text", "Texto más grande")}}">A+</a>
            </div>
            <main>
              <h1 class="title">{{E(a.Title)}}</h1>
              <p class="meta">{{meta}}</p>
              <article>{{a.Html}}</article>
            </main>
            </body>
            </html>
            """;
    }

    // ---- The scripts that run in the page ----

    private static string? _readability, _readerable;

    private static string Resource(string name)
    {
        using var stream = typeof(ReaderMode).Assembly.GetManifestResourceStream("BeautyKit.Reader." + name)
            ?? throw new InvalidOperationException("Missing resource " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Whether the page looks like an article (Readability's isProbablyReaderable).</summary>
    internal static string ReaderableScript => _readerable ??=
        "(function(){try{" + Resource("Readability-readerable.js") + "\nreturn isProbablyReaderable(document);}catch(e){return false;}})()";

    /// <summary>
    /// Extracts the article and cleans it: no scripts, frames, forms, styles, ids, classes or event handlers, and no
    /// "javascript:" addresses, so what the reader shows cannot do anything but read. Returns JSON, or null.
    /// </summary>
    internal static string ParseScript => _readability ??=
        "(function(){try{" + Resource("Readability.js") + """

        var art = new Readability(document.cloneNode(true)).parse();
        if (!art || !art.content) return null;
        var doc = new DOMParser().parseFromString(art.content, 'text/html');
        doc.querySelectorAll('script,style,iframe,frame,frameset,object,embed,form,input,button,select,textarea,link,meta,base,noscript,svg,canvas,audio,video,applet,template').forEach(function (e) { e.remove(); });
        doc.querySelectorAll('*').forEach(function (e) {
          Array.prototype.slice.call(e.attributes).forEach(function (a) {
            var n = a.name.toLowerCase();
            var bad = n.indexOf('on') === 0 || n === 'style' || n === 'class' || n === 'id' || n === 'srcdoc' || n === 'ping' || n === 'target'
              || ((n === 'href' || n === 'src' || n === 'xlink:href' || n === 'formaction') && /^\s*(javascript|vbscript|data:text)/i.test(a.value));
            if (bad) e.removeAttribute(a.name);
          });
        });
        var text = (art.textContent || '').trim();
        return JSON.stringify({
          title: art.title || document.title || '',
          byline: art.byline || '',
          site: art.siteName || location.hostname || '',
          lang: art.lang || document.documentElement.lang || '',
          dir: art.dir || '',
          html: doc.body.innerHTML,
          words: text.length ? text.split(/\s+/).length : 0,
          chars: text.length
        });
        }catch(e){return null;}})()
        """;
}
