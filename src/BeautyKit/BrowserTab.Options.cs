using CefSharp;

namespace BeautyKit;

// Navigation options shared by every tab. Several are read on Chromium's network thread, hence volatile.
public sealed partial class BrowserTab
{
    private static volatile bool _sendDoNotTrack;
    private static volatile bool _blockThirdPartyCookies;
    private static volatile bool _blockPopups = true;
    private static volatile bool _askWhereToSave;
    private static volatile string _downloadsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static string DownloadsDirectory
    {
        get => _downloadsDirectory;
        set => _downloadsDirectory = string.IsNullOrWhiteSpace(value) ? _downloadsDirectory : value;
    }

    public static bool AskWhereToSave { get => _askWhereToSave; set => _askWhereToSave = value; }

    /// <summary>Block popups the page opens without a user gesture.</summary>
    public static bool BlockPopups { get => _blockPopups; set => _blockPopups = value; }

    /// <summary>Send "Do Not Track" (DNT: 1) with every request.</summary>
    public static bool SendDoNotTrack { get => _sendDoNotTrack; set => _sendDoNotTrack = value; }

    public static bool BlockThirdPartyCookies { get => _blockThirdPartyCookies; set => _blockThirdPartyCookies = value; }

    /// <summary>Default zoom in percent, applied when each page loads.</summary>
    public static int DefaultZoomPercent { get; set; } = 100;

    public void ApplyDefaultZoom()
    {
        _zoomLevel = ZoomLevelFor(DefaultZoomPercent);
        Host()?.SetZoomLevel(_zoomLevel);
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ZoomPercent)));
    }

    private static double ZoomLevelFor(int percent) => Math.Log(Math.Max(percent, 25) / 100.0) / Math.Log(1.2);


    private sealed class TabRequestHandler(BrowserTab tab) : CefSharp.Handler.RequestHandler
    {
        // A link to an app (mailto:, zoommtg:…), in the page or in a frame: cancelled so the page stays, and offered to
        // the user (see BrowserTab.ExternalApps).
        protected override bool OnBeforeBrowse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            IRequest request, bool userGesture, bool isRedirect)
        {
            string? pageUrl = null;
            try { pageUrl = browser?.MainFrame?.Url; } catch { }
            return tab.TryHandleExternal(request.Url, pageUrl);
        }

        protected override IResourceRequestHandler? GetResourceRequestHandler(IWebBrowser chromiumWebBrowser,
            IBrowser browser, IFrame frame, IRequest request, bool isNavigation, bool isDownload,
            string requestInitiator, ref bool disableDefaultHandling)
        {
            // The page itself is never blocked, only its subresources.
            var block = Blocking.ContentBlocker.Enabled && !isDownload && request.ResourceType != ResourceType.MainFrame;
            if (!block && !_sendDoNotTrack && !_blockThirdPartyCookies) return null;

            string? topUrl = null;
            try { topUrl = browser?.MainFrame?.Url; } catch { }
            return new TabResourceHandler(tab, block, requestInitiator, topUrl);
        }
    }

    private sealed class TabResourceHandler(BrowserTab tab, bool block, string? initiator, string? topUrl)
        : CefSharp.Handler.ResourceRequestHandler
    {
        private static readonly ThirdPartyCookieFilter CookieFilter = new();
        private (string Mime, byte[] Data)? _replacement;

        protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser,
            IFrame frame, IRequest request, IRequestCallback callback)
        {
            if (_sendDoNotTrack) request.SetHeaderByName("DNT", "1", overwrite: true);
            if (!block) return CefReturnValue.Continue;

            var url = request.Url;
            var type = RequestTypeOf(request.ResourceType);
            var method = request.Method ?? "GET";

            // Right after startup the engine may still be loading (about a second); requests wait rather than slip through.
            if (!Blocking.ContentBlocker.IsReady)
            {
                Blocking.ContentBlocker.Ready.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
                {
                    using (callback) callback.Continue(Decide(url, type, method));
                });
                return CefReturnValue.ContinueAsync;
            }
            return Decide(url, type, method) ? CefReturnValue.Continue : CefReturnValue.Cancel;
        }

        /// <summary>True to continue (as is or redirected); false to cancel.</summary>
        private bool Decide(string url, Blocking.RequestType type, string method)
        {
            var decision = Blocking.ContentBlocker.Evaluate(url, initiator, topUrl, type, method);
            if (!decision.Block) return true;
            tab.CountBlocked();
            if (decision.Redirect is { } name && Blocking.ContentBlocker.Redirect(name) is { } resource)
            {
                _replacement = resource;
                return true;
            }
            return false;
        }

        protected override IResourceHandler? GetResourceHandler(IWebBrowser chromiumWebBrowser, IBrowser browser,
            IFrame frame, IRequest request)
        {
            if (_replacement is not { } resource) return null;
            var handler = new ResourceHandler(resource.Mime, new MemoryStream(resource.Data), autoDisposeStream: true);
            handler.Headers.Add("Access-Control-Allow-Origin", "*");
            return handler;
        }

        protected override ICookieAccessFilter? GetCookieAccessFilter(IWebBrowser chromiumWebBrowser,
            IBrowser browser, IFrame frame, IRequest request) =>
            _blockThirdPartyCookies ? CookieFilter : null;

        private static Blocking.RequestType RequestTypeOf(ResourceType type) => type switch
        {
            ResourceType.MainFrame => Blocking.RequestType.Document,
            ResourceType.SubFrame => Blocking.RequestType.Subdocument,
            ResourceType.Stylesheet => Blocking.RequestType.Stylesheet,
            ResourceType.Script or ResourceType.Worker or ResourceType.SharedWorker or ResourceType.ServiceWorker => Blocking.RequestType.Script,
            ResourceType.Image or ResourceType.Favicon => Blocking.RequestType.Image,
            ResourceType.FontResource => Blocking.RequestType.Font,
            ResourceType.Object or ResourceType.PluginResource => Blocking.RequestType.Object,
            ResourceType.Media => Blocking.RequestType.Media,
            ResourceType.Xhr => Blocking.RequestType.Xhr,
            ResourceType.Ping => Blocking.RequestType.Ping,
            _ => Blocking.RequestType.Other,
        };
    }

    private sealed class ThirdPartyCookieFilter : ICookieAccessFilter
    {
        public bool CanSendCookie(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, Cookie cookie) =>
            IsFirstParty(browser, request);

        public bool CanSaveCookie(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
            IResponse response, Cookie cookie) =>
            IsFirstParty(browser, request);

        private static bool IsFirstParty(IBrowser browser, IRequest request)
        {
            if (request.ResourceType == ResourceType.MainFrame) return true;
            var topLevel = browser?.MainFrame?.Url;
            if (string.IsNullOrEmpty(topLevel)) return true;
            return Site(request.Url) == Site(topLevel);
        }

        /// <summary>
        /// Registrable domain from the Public Suffix List the blocker loaded: "a.b.google.com" → "google.com", while
        /// a.vercel.app and b.vercel.app (or any *.github.io) stay different sites.
        /// </summary>
        private static string Site(string url)
        {
            var site = Blocking.ContentBlocker.SiteOf(url);
            return site.Length > 0 ? site : url; // Not an http(s) URL with a host: compare it as is.
        }
    }
}
