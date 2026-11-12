using System.Text;
using System.Text.Json;
using CefSharp;

namespace BeautyKit;

public sealed record StartPageSite(string Title, string Url, string? Icon);

/// <summary>
/// The new tab page (beauty://newtab), laid out like Dia's: the ⌘ mark, a large search box with the chosen engine's icon
/// and a greeting. It is served by the browser itself, so it works offline.
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

    /// <summary>
    /// The scheme is registered for the global context only; a private tab has its own context, which needs the
    /// factory too or beauty://newtab fails with ERR_UNKNOWN_URL_SCHEME.
    /// </summary>
    internal static void Register(IRequestContext context) =>
        context.RegisterSchemeHandlerFactory(Scheme, "newtab", new Factory(isPrivate: true));

    /// <param name="isPrivate">The private space's page: dark and says what is not kept.</param>
    private sealed class Factory(bool isPrivate = false) : ISchemeHandlerFactory
    {
        public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request) =>
            ResourceHandler.FromString(Html(isPrivate), Encoding.UTF8, mimeType: "text/html");
    }

    /// <summary>The app is in dark mode: new tabs open with the dark page.</summary>
    public static bool Dark { get; set; }

    /// <summary>Name of the search engine the user chose in settings ("Google", "DuckDuckGo"…).</summary>
    public static string SearchEngineName => EngineName(BrowserTab.SearchUrlTemplate);

    private static string EngineName(string template) =>
        template.Contains("duckduckgo", StringComparison.OrdinalIgnoreCase) ? "DuckDuckGo"
        : template.Contains("bing.", StringComparison.OrdinalIgnoreCase) ? "Bing"
        : template.Contains("ecosia", StringComparison.OrdinalIgnoreCase) ? "Ecosia"
        : template.Contains("search.brave", StringComparison.OrdinalIgnoreCase) ? "Brave"
        : "Google";

    private static string Json(object value) => JsonSerializer.Serialize(value).Replace("</", "<\\/");

    // The search box shows the chosen engine's icon, embedded so nothing is downloaded. Other engines get the magnifier.
    private const string Magnifier =
        "<svg width=\"17\" height=\"17\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\"><circle cx=\"11\" cy=\"11\" r=\"7\"/><path d=\"m20 20-3.5-3.5\"/></svg>";

    private static string EngineIcon(string engine) => engine switch
    {
        "Google" => "<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path fill=\"#4285F4\" d=\"M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92a5.06 5.06 0 0 1-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z\"/><path fill=\"#34A853\" d=\"M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z\"/><path fill=\"#FBBC05\" d=\"M5.84 14.09a6.6 6.6 0 0 1 0-4.18V7.07H2.18a11 11 0 0 0 0 9.86l3.66-2.84z\"/><path fill=\"#EA4335\" d=\"M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84C6.71 7.31 9.14 5.38 12 5.38z\"/></svg>",
        "Bing" => "<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path fill=\"#008373\" d=\"M5 2l4 1.4v13.5l5.6-3.3-2.7-1.3-1.8-4.4L19 11v4.3L9 21.1 5 18.8z\"/></svg>",
        "DuckDuckGo" => "<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" aria-hidden=\"true\"><circle cx=\"12\" cy=\"12\" r=\"11\" fill=\"#DE5833\"/><ellipse cx=\"12\" cy=\"11.5\" rx=\"6.6\" ry=\"7\" fill=\"#fff\"/><circle cx=\"9.6\" cy=\"9.8\" r=\"1.3\" fill=\"#2d4f8e\"/><circle cx=\"14.4\" cy=\"9.8\" r=\"1.3\" fill=\"#2d4f8e\"/><path fill=\"#fdd20a\" d=\"M10.2 12.6h3.6l-1.8 3.2z\"/></svg>",
        "Ecosia" => "<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" aria-hidden=\"true\"><circle cx=\"12\" cy=\"12\" r=\"11\" fill=\"#3C8C3A\"/><circle cx=\"12\" cy=\"9.6\" r=\"4.8\" fill=\"#fff\"/><path fill=\"#fff\" d=\"M11.2 13h1.6v6.6h-1.6z\"/></svg>",
        "Brave" => "<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path fill=\"#FB542B\" d=\"M12 1.8l5.2 1.6 3.1.1.7 1.2-1.9 2.4.7 2.3-.1 4.4-1.9 3.2L12 22.2l-5.8-5.2-1.9-3.2-.1-4.4.7-2.3L3 4.7l.7-1.2 3.1-.1z\"/><path fill=\"#fff\" d=\"M12 8.2l2.6.9v3.2L12 15.8 9.4 12.3V9.1z\"/></svg>",
        _ => Magnifier,
    };

    private static string Html(bool isPrivate)
    {
        var template = BrowserTab.SearchUrlTemplate;
        var engine = EngineName(template);
        return Page
            .Replace("__LANG__", L.T("en", "es"))
            .Replace("__CLASS__", isPrivate ? "private" : Dark ? "dark" : "")
            .Replace("__PRIVATE_TITLE__", L.T("You're browsing privately", "Estás navegando en privado"))
            .Replace("__PRIVATE_NOTE__", L.T(
                "History, cookies, site data and what you type in forms are not kept. They are erased when you close the last private tab. Downloads stay on your PC.",
                "No se guardan el historial, las cookies, los datos de los sitios ni lo que escribís en formularios: se borran al cerrar la última pestaña privada. Las descargas sí quedan en tu PC."))
            .Replace("__TITLE__", isPrivate ? L.T("Private tab", "Pestaña privada") : L.T("New tab", "Nueva pestaña"))
            .Replace("__PLACEHOLDER__", L.T($"Search {engine} or type an address", $"Buscá en {engine} o escribí una dirección"))
            .Replace("__SEARCH_LABEL__", L.T("Search", "Buscar"))
            .Replace("__GREETINGS__", Json(L.Spanish
                ? new[] { "Buenos días.", "Buenas tardes.", "Buenas noches.", "¿Qué buscamos hoy?" }
                : new[] { "Good morning.", "Good afternoon.", "Good evening.", "What are we looking for today?" }))
            .Replace("__ENGINE_ICON__", EngineIcon(engine))
            .Replace("__SEARCH__", Json(template));
    }

    private const string Page = """
        <!doctype html>
        <html lang="__LANG__" class="__CLASS__">
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
            width: 100%; background: #fff; border-radius: 12px; padding: 14px 14px 14px 20px;
            border: 1px solid rgba(0, 0, 0, .06);
            box-shadow: 0 1px 2px rgba(0, 0, 0, .04), 0 10px 32px rgba(20, 20, 30, .06);
            transition: box-shadow .25s, border-color .25s;
          }
          form:focus-within { border-color: rgba(0, 0, 0, .1); box-shadow: 0 1px 2px rgba(0, 0, 0, .05), 0 14px 40px rgba(20, 20, 30, .09); }
          .row { display: flex; align-items: center; gap: 10px; }
          .row > svg { flex: none; color: var(--soft); }
          input { flex: 1; min-width: 0; border: 0; outline: 0; background: transparent; font: inherit; font-size: 16.5px; color: var(--text); }
          input::placeholder { color: var(--soft); }
          button {
            flex: none; width: 32px; height: 32px; border: 1px solid rgba(0, 0, 0, .06); border-radius: 8px; background: #f2f2f4; color: #a3a3ab;
            display: grid; place-items: center; cursor: pointer; transition: background .15s, color .15s, transform .15s;
          }
          button.on { background: var(--text); color: #fff; border-color: transparent; }
          button:active { transform: scale(.94); }

          h1 { margin: 44px 0 0; font-size: 29px; line-height: 1.3; font-weight: 600; letter-spacing: -.015em; text-align: center; }

          .in { animation: in .55s cubic-bezier(.2, .8, .2, 1) both; }
          @keyframes in { from { opacity: 0; transform: translateY(10px); } to { opacity: 1; transform: none; } }
          @keyframes float { 50% { transform: translateY(-3px); } }

          /* Dark mode, and the private space (always dark, like other browsers' incognito windows). */
          .note { display: none; }
          :is(.private, .dark) { color-scheme: dark; --text: #ececf1; --soft: #8e8e99; }
          html::after {
            content: ""; position: fixed; inset: 0; z-index: 0; pointer-events: none; opacity: 0; transition: opacity .3s cubic-bezier(.4, 0, .2, 1);
            background: radial-gradient(1400px 900px at 50% 0%, #2a2a31 0%, #202026 55%, #1a1a1f 100%);
          }
          :is(.private, .dark)::after { opacity: 1; }
          body, form, input, button { transition: color .3s, background-color .3s, border-color .3s, box-shadow .3s; transition-timing-function: cubic-bezier(.4, 0, .2, 1); }
          :is(.private, .dark) body::before { opacity: .05; }
          :is(.private, .dark) form { background: #2c2c33; border-color: rgba(255, 255, 255, .08); box-shadow: 0 10px 32px rgba(0, 0, 0, .35); }
          :is(.private, .dark) form:focus-within { border-color: rgba(255, 255, 255, .16); box-shadow: 0 14px 40px rgba(0, 0, 0, .45); }
          :is(.private, .dark) button { background: #38383f; border-color: rgba(255, 255, 255, .06); color: #8e8e99; }
          :is(.private, .dark) button.on { background: #ececf1; color: #1d1d20; }
          .private .note { display: block; max-width: 470px; margin: 12px 0 0; font-size: 14.5px; line-height: 1.55; color: var(--soft); text-align: center; }
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
              __ENGINE_ICON__
              <input id="q" placeholder="__PLACEHOLDER__" spellcheck="false" autofocus>
              <button id="go" type="submit" aria-label="__SEARCH_LABEL__">
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19V5M5 12l7-7 7 7"/></svg>
              </button>
            </div>
          </form>
          <h1 class="in" style="animation-delay:.1s"></h1>
          <p class="note in" style="animation-delay:.15s">__PRIVATE_NOTE__</p>
        </main>
        <script>
          const SEARCH = __SEARCH__;
          const TEXT = __GREETINGS__;

          const hour = new Date().getHours();
          const hello = hour >= 5 && hour < 12 ? TEXT[0] : hour >= 12 && hour < 20 ? TEXT[1] : TEXT[2];
          document.querySelector('h1').textContent = document.documentElement.classList.contains('private')
            ? '__PRIVATE_TITLE__' : hello + ' ' + TEXT[3];

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
        </script>
        </body>
        </html>
        """;
}
