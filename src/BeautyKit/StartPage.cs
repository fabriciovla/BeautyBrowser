using System.Text;
using System.Text.Json;
using CefSharp;

namespace BeautyKit;

public sealed record StartPageSite(string Title, string Url, string? Icon);

/// <summary>
/// The new tab page (beauty://newtab), laid out like Dia's: the ⌘ mark, a large search box, a greeting and, rising from
/// the bottom edge, tilted cards that preview our apps. It is served by the browser itself, so it works offline.
/// </summary>
public static class StartPage
{
    public const string Scheme = "beauty";
    public const string Url = "beauty://newtab/";

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

    private static string Json(object value) => JsonSerializer.Serialize(value).Replace("</", "<\\/");

    // The cards at the bottom, left to right: our apps for now (sponsored startups will take these places later).
    private static readonly (string Name, string Preview, string UrlEnglish, string UrlSpanish)[] Apps =
    [
        ("Beauty Screenshot", "screenshot-preview.webp", "https://beauty-shell.vercel.app/en/screenshot/", "https://beauty-shell.vercel.app/screenshot/"),
        ("Beauty Shell", "shell-preview.webp", "https://beauty-shell.vercel.app/en/shell/", "https://beauty-shell.vercel.app/shell/"),
        ("Conext", "conext-preview.webp", "https://www.conext.lat/en", "https://www.conext.lat/"),
    ];

    private static readonly Dictionary<string, string> Assets = [];

    /// <summary>An image embedded in BeautyKit (Assets\StartPage) as a data: address.</summary>
    private static string Asset(string file)
    {
        lock (Assets)
        {
            if (Assets.TryGetValue(file, out var cached)) return cached;
            using var stream = typeof(StartPage).Assembly.GetManifestResourceStream("BeautyKit.StartPage." + file);
            var uri = "";
            if (stream is not null)
            {
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                uri = "data:image/webp;base64," + Convert.ToBase64String(memory.ToArray());
            }
            return Assets[file] = uri;
        }
    }

    private static string Html()
    {
        var template = BrowserTab.SearchUrlTemplate;
        var engine = EngineName(template);
        var apps = Apps.Select(a => new { name = a.Name, image = Asset(a.Preview), url = L.T(a.UrlEnglish, a.UrlSpanish) }).ToArray();
        return Page
            .Replace("__LANG__", L.T("en", "es"))
            .Replace("__TITLE__", L.T("New tab", "Nueva pestaña"))
            .Replace("__PLACEHOLDER__", L.T($"Search {engine} or type an address", $"Buscá en {engine} o escribí una dirección"))
            .Replace("__ENTER__", L.T("to search with", "para buscar con"))
            .Replace("__SEARCH_LABEL__", L.T("Search", "Buscar"))
            .Replace("__GREETINGS__", Json(L.Spanish
                ? new[] { "Buenos días.", "Buenas tardes.", "Buenas noches.", "¿Qué buscamos hoy?" }
                : new[] { "Good morning.", "Good afternoon.", "Good evening.", "What are we looking for today?" }))
            .Replace("__ENGINE__", engine)
            .Replace("__SEARCH__", Json(template))
            .Replace("__APPS__", Json(apps));
    }

    private const string Page = """
        <!doctype html>
        <html lang="__LANG__">
        <head>
        <meta charset="utf-8">
        <title>__TITLE__</title>
        <style>
          :root { color-scheme: light; --text: #1d1d20; --soft: #9b9ba3; }
          * { box-sizing: border-box; }
          html, body { height: 100%; margin: 0; }
          body {
            font-family: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
            color: var(--text);
            background: radial-gradient(1400px 900px at 50% 0%, #f6f6f7 0%, #efeff1 55%, #e8e8eb 100%);
            display: flex; align-items: center; justify-content: center; overflow: hidden;
            -webkit-font-smoothing: antialiased;
          }
          body::before {
            content: ""; position: fixed; inset: 0; pointer-events: none; opacity: .035;
            background-image: url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='180' height='180'><filter id='n'><feTurbulence type='fractalNoise' baseFrequency='.85' numOctaves='2' stitchTiles='stitch'/></filter><rect width='100%' height='100%' filter='url(%23n)'/></svg>");
          }
          main { position: relative; z-index: 1; width: min(600px, 88vw); display: flex; flex-direction: column; align-items: center; margin-top: -18vh; }

          .mark { width: 78px; height: 78px; display: block; margin-bottom: 30px; overflow: visible; animation: float 7s ease-in-out infinite; }
          .mark path { filter: drop-shadow(0 4px 6px rgba(80, 56, 200, .25)); }

          form {
            width: 100%; background: #fff; border-radius: 12px; padding: 18px 14px 12px 20px;
            border: 1px solid rgba(0, 0, 0, .06);
            box-shadow: 0 1px 2px rgba(0, 0, 0, .04), 0 10px 32px rgba(20, 20, 30, .06);
            transition: box-shadow .25s, border-color .25s;
          }
          form:focus-within { border-color: rgba(0, 0, 0, .1); box-shadow: 0 1px 2px rgba(0, 0, 0, .05), 0 14px 40px rgba(20, 20, 30, .09); }
          .row { display: flex; align-items: center; gap: 10px; }
          .row svg { flex: none; color: var(--soft); }
          input { flex: 1; min-width: 0; border: 0; outline: 0; background: transparent; font: inherit; font-size: 16.5px; color: var(--text); }
          input::placeholder { color: var(--soft); }
          .foot { display: flex; align-items: center; justify-content: space-between; margin-top: 34px; font-size: 14px; color: var(--soft); }
          .foot b { color: #6e6e76; font-weight: 600; }
          kbd { font: inherit; font-size: 12px; padding: 1px 6px; border-radius: 5px; background: #f2f2f4; color: #6e6e76; border: 1px solid rgba(0,0,0,.05); }
          button {
            width: 32px; height: 32px; border: 1px solid rgba(0, 0, 0, .06); border-radius: 8px; background: #f2f2f4; color: #a3a3ab;
            display: grid; place-items: center; cursor: pointer; transition: background .15s, color .15s, transform .15s;
          }
          button.on { background: var(--text); color: #fff; border-color: transparent; }
          button:active { transform: scale(.94); }

          h1 { margin: 44px 0 0; font-size: 29px; line-height: 1.3; font-weight: 600; letter-spacing: -.015em; text-align: center; }

          /* The app cards rise from the bottom edge, tilted, and lift under the pointer. */
          .showcase { position: fixed; left: 50%; bottom: 0; width: min(920px, 100vw); height: 300px; transform: translateX(-50%); pointer-events: none; }
          .card {
            position: absolute; display: block; padding: 9px; background: #fff; border-radius: 22px; pointer-events: auto;
            box-shadow: 0 0 0 1px rgba(0, 0, 0, .05), 0 18px 50px rgba(20, 20, 35, .16);
            transition: transform .45s cubic-bezier(.2, .8, .2, 1), box-shadow .45s;
            animation: rise .9s cubic-bezier(.2, .8, .2, 1) both;
          }
          .card img { display: block; width: 100%; border-radius: 14px; }
          .card:hover { box-shadow: 0 0 0 1px rgba(0, 0, 0, .05), 0 26px 60px rgba(20, 20, 35, .22); }
          .c0 { left: 2%; width: 34%; top: 128px; transform: rotate(-9deg); animation-delay: .2s; }
          .c0:hover { transform: translateY(-16px) rotate(-9deg); }
          .c1 { left: 31%; width: 38%; top: 72px; transform: rotate(-4deg); z-index: 2; padding: 16px 12px 12px; border-radius: 26px; animation-delay: .1s; }
          .c1::before { content: ""; position: absolute; top: 5px; left: 50%; width: 6px; height: 6px; margin-left: -3px; border-radius: 50%; background: #1d1d20; }
          .c1:hover { transform: translateY(-16px) rotate(-4deg); }
          .c2 { right: 2%; width: 34%; top: 118px; transform: rotate(11deg); animation-delay: .3s; }
          .c2:hover { transform: translateY(-16px) rotate(11deg); }
          @keyframes rise { from { opacity: 0; translate: 0 90px; } to { opacity: 1; translate: 0 0; } }
          @media (max-height: 660px) { .showcase { display: none; } main { margin-top: 0; } }

          .in { animation: in .55s cubic-bezier(.2, .8, .2, 1) both; }
          @keyframes in { from { opacity: 0; transform: translateY(10px); } to { opacity: 1; transform: none; } }
          @keyframes float { 50% { transform: translateY(-3px); } }
        </style>
        </head>
        <body>
        <main>
          <svg class="mark in" viewBox="0 0 24 24" fill="none" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#5b3fe0"/><stop offset="1" stop-color="#9b7df5"/></linearGradient></defs>
            <path stroke="url(#g)" d="M15 6v12a3 3 0 1 0 3-3H6a3 3 0 1 0 3 3V6a3 3 0 1 0-3 3h12a3 3 0 1 0-3-3"/>
          </svg>
          <form class="in" style="animation-delay:.05s" autocomplete="off">
            <div class="row">
              <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>
              <input id="q" placeholder="__PLACEHOLDER__" spellcheck="false" autofocus>
            </div>
            <div class="foot">
              <div><kbd>Enter</kbd> __ENTER__ <b>__ENGINE__</b></div>
              <button id="go" type="submit" aria-label="__SEARCH_LABEL__">
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19V5M5 12l7-7 7 7"/></svg>
              </button>
            </div>
          </form>
          <h1 class="in" style="animation-delay:.1s"></h1>
        </main>
        <div class="showcase"></div>
        <script>
          const SEARCH = __SEARCH__;
          const APPS = __APPS__;
          const TEXT = __GREETINGS__;

          const hour = new Date().getHours();
          const hello = hour >= 5 && hour < 12 ? TEXT[0] : hour >= 12 && hour < 20 ? TEXT[1] : TEXT[2];
          document.querySelector('h1').textContent = hello + ' ' + TEXT[3];

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

          const showcase = document.querySelector('.showcase');
          APPS.forEach((app, i) => {
            const a = document.createElement('a');
            a.className = 'card c' + i;
            a.href = app.url;
            a.title = app.name;
            const img = document.createElement('img');
            img.src = app.image;
            img.alt = app.name;
            a.appendChild(img);
            showcase.appendChild(a);
          });
        </script>
        </body>
        </html>
        """;
}
