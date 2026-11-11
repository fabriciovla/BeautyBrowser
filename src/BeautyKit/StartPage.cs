using System.Text;
using System.Text.Json;
using CefSharp;

namespace BeautyKit;

public sealed record StartPageSite(string Title, string Url, string? Icon);

/// <summary>
/// The new tab page (beauty://newtab): logo, greeting, a large search box and the most visited sites. It is served
/// by the browser itself, so it works offline.
/// </summary>
public static class StartPage
{
    public const string Scheme = "beauty";
    public const string Url = "beauty://newtab/";

    /// <summary>Sites shown under the search box. Set on the UI thread, read from Chromium's threads.</summary>
    public static IReadOnlyList<StartPageSite> Sites { get; set; } = [];

    public static bool Is(string? url) => url is not null && url.StartsWith("beauty://newtab", StringComparison.OrdinalIgnoreCase);

    /// <summary>Registers beauty:// with Chromium. Must run before the engine starts.</summary>
    internal static void Register(CefSettingsBase settings) =>
        settings.RegisterScheme(new CefCustomScheme
        {
            SchemeName = Scheme,
            DomainName = "newtab",
            IsStandard = true,
            IsSecure = true,
            IsLocal = false,
            SchemeHandlerFactory = new Factory(),
        });

    private sealed class Factory : ISchemeHandlerFactory
    {
        public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request) =>
            ResourceHandler.FromString(Html(), Encoding.UTF8, mimeType: "text/html");
    }

    /// <summary>Name of the search engine the user chose in settings ("Google", "DuckDuckGo"…).</summary>
    public static string SearchEngineName => EngineName(BrowserTab.SearchUrlTemplate);

    private static string EngineName(string template) =>
        template.Contains("duckduckgo", StringComparison.OrdinalIgnoreCase) ? "DuckDuckGo"
        : template.Contains("bing.", StringComparison.OrdinalIgnoreCase) ? "Bing"
        : template.Contains("ecosia", StringComparison.OrdinalIgnoreCase) ? "Ecosia"
        : "Google";

    /// <summary>The app logo as a PNG data URI. Without it the page shows a colored sphere.</summary>
    public static string? LogoDataUri { get; set; }

    private static string Json(object value) => JsonSerializer.Serialize(value).Replace("</", "<\\/");

    private static string Html()
    {
        var template = BrowserTab.SearchUrlTemplate;
        var engine = EngineName(template);
        var sites = Sites.Take(8).Select(s => new { title = s.Title, url = s.Url, icon = s.Icon ?? "" }).ToArray();
        var page = Page;
        if (LogoDataUri is { Length: > 0 } logo)
            page = page.Replace("<div class=\"mark in\"></div>", $"<div class=\"in\"><img class=\"logo\" src=\"{logo}\" alt=\"\"></div>");
        return page
            .Replace("__LANG__", L.T("en", "es"))
            .Replace("__TITLE__", L.T("New tab", "Nueva pestaña"))
            .Replace("__PLACEHOLDER__", L.T($"Search {engine} or type an address", $"Buscá en {engine} o escribí una dirección"))
            .Replace("__ENTER__", L.T("to search with", "para buscar con"))
            .Replace("__SEARCH_LABEL__", L.T("Search", "Buscar"))
            .Replace("__GREETINGS__", Json(L.Spanish
                ? new[] { "Buenos días", "Buenas tardes", "Buenas noches", " · ¿qué buscamos?" }
                : new[] { "Good morning", "Good afternoon", "Good evening", " · what are we looking for?" }))
            .Replace("__ENGINE__", engine)
            .Replace("__SEARCH__", Json(template))
            .Replace("__SITES__", Json(sites));
    }

    private const string Page = """
        <!doctype html>
        <html lang="__LANG__">
        <head>
        <meta charset="utf-8">
        <title>__TITLE__</title>
        <style>
          :root { color-scheme: light; --text: #1f1f24; --muted: #8a8a94; --accent: #2f6feb; }
          * { box-sizing: border-box; }
          html, body { height: 100%; margin: 0; }
          body {
            font-family: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
            color: var(--text);
            background: radial-gradient(1200px 760px at 50% -12%, #ffffff 0%, #f4f4f7 46%, #e8e9ef 100%);
            display: flex; align-items: center; justify-content: center; overflow: hidden;
            -webkit-font-smoothing: antialiased;
          }
          body::before {
            content: ""; position: fixed; inset: 0; pointer-events: none; opacity: .045;
            background-image: url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='180' height='180'><filter id='n'><feTurbulence type='fractalNoise' baseFrequency='.85' numOctaves='2' stitchTiles='stitch'/></filter><rect width='100%' height='100%' filter='url(%23n)'/></svg>");
          }
          main { width: min(660px, 88vw); display: flex; flex-direction: column; align-items: center; margin-top: -7vh; }

          .mark {
            width: 66px; height: 66px; border-radius: 50%; position: relative;
            background: conic-gradient(from 210deg, #2f6feb, #7c5cf6, #e2559b, #f2a33a, #2f6feb);
            box-shadow: 0 14px 34px rgba(47, 111, 235, .22), inset 0 0 0 1px rgba(255, 255, 255, .45);
            animation: float 7s ease-in-out infinite;
          }
          .logo { width: 78px; height: 78px; display: block; animation: float 7s ease-in-out infinite; filter: drop-shadow(0 12px 22px rgba(70, 50, 160, .25)); }
          .mark::after {
            content: ""; position: absolute; inset: 6px; border-radius: 50%;
            background: radial-gradient(circle at 34% 28%, rgba(255,255,255,.95), rgba(255,255,255,.5) 42%, rgba(255,255,255,0) 72%);
          }

          h1 { margin: 22px 0 20px; font-size: 27px; font-weight: 600; letter-spacing: -.02em; text-align: center; }
          h1 span { color: var(--muted); font-weight: 500; }

          form {
            width: 100%; background: #fff; border-radius: 18px; padding: 16px 14px 12px 18px;
            border: 1px solid rgba(0, 0, 0, .06);
            box-shadow: 0 1px 2px rgba(0, 0, 0, .04), 0 14px 44px rgba(22, 22, 44, .08);
            transition: box-shadow .25s, border-color .25s;
          }
          form:focus-within {
            border-color: rgba(47, 111, 235, .32);
            box-shadow: 0 1px 2px rgba(0, 0, 0, .04), 0 18px 54px rgba(47, 111, 235, .14);
          }
          .row { display: flex; align-items: center; gap: 12px; }
          .row svg { flex: none; color: #9a9aa3; }
          input { flex: 1; min-width: 0; border: 0; outline: 0; background: transparent; font: inherit; font-size: 17px; color: var(--text); }
          input::placeholder { color: #9a9aa3; }
          .foot { display: flex; align-items: center; justify-content: space-between; margin-top: 14px; font-size: 12.5px; color: #9a9aa3; }
          .foot b { color: #6b6b75; font-weight: 600; }
          kbd { font: inherit; font-size: 11px; padding: 1px 6px; border-radius: 6px; background: #f1f1f4; color: #6b6b75; border: 1px solid rgba(0,0,0,.05); }
          button {
            width: 32px; height: 32px; border: 0; border-radius: 10px; background: var(--text); color: #fff;
            display: grid; place-items: center; cursor: pointer; opacity: .18; transition: opacity .15s, transform .15s;
          }
          button.on { opacity: 1; }
          button:active { transform: scale(.94); }

          nav { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 10px; width: 100%; margin-top: 26px; }
          nav:empty { display: none; }
          nav a {
            display: flex; align-items: center; gap: 10px; min-width: 0; padding: 10px 12px; border-radius: 12px;
            background: rgba(255, 255, 255, .58); border: 1px solid rgba(0, 0, 0, .05);
            color: var(--text); text-decoration: none; font-size: 13px;
            transition: background .15s, transform .15s, box-shadow .15s;
          }
          nav a:hover { background: #fff; transform: translateY(-1px); box-shadow: 0 6px 18px rgba(22, 22, 44, .06); }
          nav img, nav i { width: 20px; height: 20px; border-radius: 5px; flex: none; }
          nav i { display: grid; place-items: center; background: #e7e7ec; color: #55555e; font-style: normal; font-size: 11px; font-weight: 600; }
          nav span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

          .in { animation: in .55s cubic-bezier(.2, .8, .2, 1) both; }
          @keyframes in { from { opacity: 0; transform: translateY(10px); } to { opacity: 1; transform: none; } }
          @keyframes float { 50% { transform: translateY(-3px); } }
        </style>
        </head>
        <body>
        <main>
          <div class="mark in"></div>
          <h1 class="in" style="animation-delay:.05s"><span id="hello"></span></h1>
          <form class="in" style="animation-delay:.1s" autocomplete="off">
            <div class="row">
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>
              <input id="q" placeholder="__PLACEHOLDER__" spellcheck="false" autofocus>
            </div>
            <div class="foot">
              <div><kbd>Enter</kbd> __ENTER__ <b>__ENGINE__</b></div>
              <button id="go" type="submit" aria-label="__SEARCH_LABEL__">
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19V5M5 12l7-7 7 7"/></svg>
              </button>
            </div>
          </form>
          <nav class="in" style="animation-delay:.15s"></nav>
        </main>
        <script>
          const SEARCH = __SEARCH__;
          const SITES = __SITES__;
          const TEXT = __GREETINGS__;

          const hour = new Date().getHours();
          const hello = hour >= 5 && hour < 12 ? TEXT[0] : hour >= 12 && hour < 20 ? TEXT[1] : TEXT[2];
          document.getElementById('hello').textContent = hello;
          const span = document.createElement('span');
          span.textContent = TEXT[3];
          document.querySelector('h1').appendChild(span);

          const q = document.getElementById('q');
          const go = document.getElementById('go');
          q.addEventListener('input', () => go.classList.toggle('on', q.value.trim().length > 0));
          setTimeout(() => q.focus(), 0);

          const looksLikeAddress = (v) =>
            /^[a-z][a-z0-9+.-]*:\/\//i.test(v) ||
            /^localhost(:\d+)?(\/|$)/i.test(v) ||
            (!/\s/.test(v) && /^[^\s/]+\.[a-z]{2,}(:\d+)?(\/.*)?$/i.test(v));

          document.querySelector('form').addEventListener('submit', (e) => {
            e.preventDefault();
            const v = q.value.trim();
            if (!v) return;
            location.href = looksLikeAddress(v)
              ? (/^[a-z][a-z0-9+.-]*:\/\//i.test(v) ? v : 'https://' + v)
              : SEARCH.replace('{0}', encodeURIComponent(v));
          });

          const nav = document.querySelector('nav');
          for (const site of SITES) {
            const a = document.createElement('a');
            a.href = site.url;
            const letter = () => { const i = document.createElement('i'); i.textContent = (site.title[0] || '?').toUpperCase(); return i; };
            if (site.icon) {
              const img = document.createElement('img');
              img.src = site.icon;
              img.alt = '';
              img.onerror = () => img.replaceWith(letter());
              a.appendChild(img);
            } else {
              a.appendChild(letter());
            }
            const label = document.createElement('span');
            label.textContent = site.title;
            a.appendChild(label);
            nav.appendChild(a);
          }
        </script>
        </body>
        </html>
        """;
}
