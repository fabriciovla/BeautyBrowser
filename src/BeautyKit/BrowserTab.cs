using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using CefSharp.Structs;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BeautyKit;

/// <summary>
/// A tab: a Chromium browser that renders offscreen. <see cref="BrowserView"/> shows its frames inside the UI and
/// forwards mouse and keyboard input, the same way WebView2 works inside WinUI. Observable state is always raised on
/// the UI thread.
/// </summary>
public sealed partial class BrowserTab : INotifyPropertyChanged, IDisposable
{
    private readonly ChromiumWebBrowser _browser;
    private readonly DispatcherQueue _ui;
    private readonly nint _hostWindow;
    private bool _disposed;

    // The size of the last shown tab: tabs that load in the background lay out at that size, not at 1×1 px, so the
    // page does not come up broken (or in its mobile layout) when it is shown.
    private static (int Width, int Height, double Scale, (int X, int Y) Offset) _lastViewSize = (1, 1, 1, (0, 0));

    private int _viewWidth = _lastViewSize.Width;
    private int _viewHeight = _lastViewSize.Height;
    private double _scale = _lastViewSize.Scale;
    private (int X, int Y) _viewOffsetPx = _lastViewSize.Offset; // the view's position inside the window, in pixels

    // Hidden until a view shows it: tabs opened in the background stay hidden for Chromium (visibilityState
    // "hidden", throttled timers, no painting), as in Chrome.
    private bool _hidden = true;

    // Restored tabs create their Chromium browser the first time they are shown (see EnsureCreated).
    private bool _created;
    private IWindowInfo? _pendingWindowInfo;
    private IBrowserSettings? _pendingSettings;
    private string? _pendingUrl;

    private string _title = L.T("New tab", "Nueva pestaña");
    private string _url;
    private ImageSource? _favicon;
    private string? _faviconUrl;
    private int _faviconRequest;
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;

    /// <param name="mode">
    /// <see cref="TabCreation.Now"/> creates the browser right away; <see cref="TabCreation.WhenShown"/> waits until it
    /// is first shown (restored tabs); <see cref="TabCreation.Popup"/> lets Chromium create it as a page's popup.
    /// </param>
    internal BrowserTab(nint hostWindow, DispatcherQueue ui, string url, TabCreation mode = TabCreation.Now)
    {
        _hostWindow = hostWindow;
        _ui = ui;
        _url = url;

        var browserSettings = CefSharp.Core.ObjectFactory.CreateBrowserSettings(autoDispose: true);
        browserSettings.WindowlessFrameRate = FrameRate;
        browserSettings.BackgroundColor = 0xFFFFFFFF; // opaque (see BrowserEngine)
        _zoomLevel = ZoomLevelFor(DefaultZoomPercent);

        _browser = new ChromiumWebBrowser(url, browserSettings, automaticallyCreateBrowser: false)
        {
            DisplayHandler = new TabDisplayHandler(this),
            LifeSpanHandler = new TabLifeSpanHandler(this),
            MenuHandler = new TabContextMenuHandler(this),
            DownloadHandler = new TabDownloadHandler(this),
            RequestHandler = new TabRequestHandler(this),
            FindHandler = new TabFindHandler(this),
            KeyboardHandler = new TabKeyboardHandler(this),
            PermissionHandler = new TabPermissionHandler(this),
            JsDialogHandler = new TabJsDialogHandler(this),
        };
        _browser.RenderHandler = new TabRenderHandler(this);
        _browser.JavascriptMessageReceived += OnJavascriptMessage; // playing media (BrowserTab.Media)

        _browser.TitleChanged += (_, e) => OnUi(() => Title = ReadableTitle(e.Title, Url));
        _browser.AddressChanged += (_, e) => OnUi(() => Url = e.Address);
        _browser.LoadingStateChanged += (_, e) => OnUi(() =>
        {
            IsLoading = e.IsLoading;
            CanGoBack = e.CanGoBack;
            CanGoForward = e.CanGoForward;
        });
        // Pages that declare no icon fall back to the site's /favicon.ico, as Chrome does.
        _browser.FrameLoadEnd += (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            OnUi(() =>
            {
                if (_faviconPending || !Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
                if (_faviconUrl is not null && _faviconHost == uri.Host) return; // it already has this site's icon
                _ = ResolveFaviconAsync([$"{uri.Scheme}://{uri.Authority}/favicon.ico"]);
            });
        };
        // Chromium remembers zoom per site, so the tab's zoom is applied again whenever a page starts loading.
        _browser.FrameLoadStart += (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            OnUi(() =>
            {
                Host()?.SetZoomLevel(_zoomLevel);
                ResetBlockedCount(); // another page: the blocked counter starts over
                ClearMedia();        // and whatever played on the previous one stopped
            });
        };

        // If the view focused the tab before Chromium finished creating the browser, that focus event was lost and
        // the page would never get keyboard input, so it is sent again now.
        // The same goes for the hidden state, the size the tab may have been given meanwhile and an address it was sent
        // to before it existed.
        _browser.BrowserInitialized += (_, _) => OnUi(() =>
        {
            if (Host() is not { } host) return;
            if (_hidden) host.WasHidden(true);
            host.NotifyScreenInfoChanged();
            host.WasResized();
            if (_focused) host.SendFocusEvent(true);
            if (_pendingUrl is { } pending)
            {
                _pendingUrl = null;
                _browser.LoadUrl(pending);
            }
        });

        if (mode == TabCreation.Popup)
        {
            _created = true; // Chromium creates it (see TabLifeSpanHandler.OnBeforePopup)
            return;
        }

        var windowInfo = CefSharp.Core.ObjectFactory.CreateWindowInfo();
        windowInfo.SetAsWindowless(hostWindow); // the main window owns native dialogs and menus
        windowInfo.SharedTextureEnabled = UseGpu; // frames stay on the GPU (OnAcceleratedPaint)
        if (mode == TabCreation.WhenShown)
        {
            _pendingWindowInfo = windowInfo;
            _pendingSettings = browserSettings;
            if (!StartPage.Is(url)) _title = ReadableTitle(null, url); // the start page keeps "New tab"
            return;
        }
        _created = true;
        _browser.CreateBrowser(windowInfo, browserSettings);
    }

    /// <summary>Creates the Chromium browser of a restored tab, the first time it is shown or used.</summary>
    private void EnsureCreated()
    {
        if (_created || _disposed || _pendingWindowInfo is null) return;
        _created = true;
        _browser.CreateBrowser(_pendingWindowInfo, _pendingSettings);
        _pendingWindowInfo = null;
        _pendingSettings = null;
    }

    /// <summary>Title and icon to show for a restored tab until its page loads (from history).</summary>
    public void SetPlaceholder(string? title, string? faviconUrl)
    {
        if (_created) return;
        if (!string.IsNullOrWhiteSpace(title)) Title = ReadableTitle(title, Url);
        if (!string.IsNullOrEmpty(faviconUrl)) _ = ResolveFaviconAsync([faviconUrl]);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The page opened a popup window (window.open with a size: "Sign in with Google", payments…). It is a real popup:
    /// it keeps window.opener, so it can hand its result back to the page and close itself.
    /// </summary>
    public event EventHandler<BrowserTab>? PopupOpened;

    /// <summary>
    /// The tab may close: the page closed itself (window.close(), usually a popup when it is done) or it let a
    /// <see cref="RequestClose"/> through. The app removes the tab.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>The page wants a new window (target=_blank, window.open…).</summary>
    public event EventHandler<string>? NewTabRequested;

    /// <summary>
    /// A plain click on a link that opens another tab. The app shows it in a mini window over the page, like Arc's
    /// Peek; without a listener it opens as a tab.
    /// </summary>
    public event EventHandler<string>? PeekRequested;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Url { get => _url; private set => Set(ref _url, value); }
    public ImageSource? Favicon { get => _favicon; private set => Set(ref _favicon, value); }
    public string? FaviconUrl { get => _faviconUrl; private set => Set(ref _faviconUrl, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    /// <summary>Real page load progress from 0 to 1, as reported by Chromium.</summary>
    public double LoadProgress { get => _loadProgress; private set => Set(ref _loadProgress, value); }
    private double _loadProgress = 1;

    public bool CanGoBack { get => _canGoBack; private set => Set(ref _canGoBack, value); }
    public bool CanGoForward { get => _canGoForward; private set => Set(ref _canGoForward, value); }


    /// <summary>Navigates to a URL, or searches the text when it does not look like one.</summary>
    public void Navigate(string input)
    {
        var url = InputToUrl(input);
        if (!_browser.IsBrowserInitialized)
        {
            // Not created yet (restored tab) or still starting: it loads once it exists.
            _pendingUrl = url;
            Url = url;
            EnsureCreated();
            return;
        }
        _browser.LoadUrl(url);
    }

    public void GoBack() { if (_browser.IsBrowserInitialized) _browser.Back(); }
    public void GoForward() { if (_browser.IsBrowserInitialized) _browser.Forward(); }
    /// <param name="ignoreCache">Ctrl+Shift+R, Ctrl+F5: fetch everything again instead of using the cache.</param>
    public void Reload(bool ignoreCache = false) { if (_browser.IsBrowserInitialized) _browser.Reload(ignoreCache); }

    public static string SearchUrlTemplate { get; set; } = "https://www.google.com/search?q={0}";
    public static string DefaultHomePage { get; set; } = "https://www.google.com";

    public static string InputToUrl(string input)
    {
        input = input.Trim();

        if (input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
            return "http://" + input;

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" or "file" or "about" or "chrome" or "view-source" or StartPage.Scheme)
            return uri.ToString();

        if (!input.Contains(' ') && input.Contains('.'))
            return "https://" + input;

        return string.Format(SearchUrlTemplate, Uri.EscapeDataString(input));
    }


    internal WriteableBitmap? Frame { get; private set; }

    /// <summary>Last frame of the popup widget (&lt;select&gt; dropdowns and the like).</summary>
    internal WriteableBitmap? PopupFrame { get; private set; }

    /// <summary>Popup position and size in DIPs relative to the view, or null when it is closed.</summary>
    internal Windows.Foundation.Rect? PopupBounds { get; private set; }

    /// <summary>The frame or popup bitmap was replaced (not just its content), or the popup opened or closed.</summary>
    internal event Action? VisualChanged;

    internal event Action<CursorType>? CursorChanged;

    internal void Resize(int widthDip, int heightDip, double scale, (int X, int Y) offsetPx)
    {
        _viewOffsetPx = offsetPx;
        widthDip = Math.Max(widthDip, 1);
        heightDip = Math.Max(heightDip, 1);

        var scaleChanged = Math.Abs(scale - _scale) > 0.001;
        var sizeChanged = widthDip != _viewWidth || heightDip != _viewHeight;
        _viewWidth = widthDip;
        _viewHeight = heightDip;
        _scale = scale;
        _lastViewSize = (widthDip, heightDip, scale, offsetPx);

        if (!_browser.IsBrowserInitialized) return;
        var host = _browser.GetBrowserHost();
        if (scaleChanged) host.NotifyScreenInfoChanged();
        if (sizeChanged || scaleChanged) host.WasResized();
    }

    internal void SetHidden(bool hidden)
    {
        _hidden = hidden;
        if (!hidden) EnsureCreated();
        if (_browser.IsBrowserInitialized) _browser.GetBrowserHost().WasHidden(hidden);
    }


    internal void SendMouseMove(int x, int y, CefEventFlags flags, bool leave)
    {
        if (!leave && TryContinueInternalDrag(x, y, flags)) return;
        Host()?.SendMouseMoveEvent(new MouseEvent(x, y, flags), leave);
    }

    internal void SendMouseClick(int x, int y, CefEventFlags flags, MouseButtonType button, bool up, int clickCount)
    {
        if (up && button == MouseButtonType.Left && TryEndInternalDrag(x, y, flags)) return;
        Host()?.SendMouseClickEvent(new MouseEvent(x, y, flags), button, up, clickCount);
    }

    internal void SendMouseWheel(int x, int y, CefEventFlags flags, int deltaX, int deltaY) =>
        Host()?.SendMouseWheelEvent(new MouseEvent(x, y, flags), deltaX, deltaY);

    internal void SendKey(KeyEvent keyEvent) => Host()?.SendKeyEvent(keyEvent);

    /// <summary>A key went through the page and the page did not take it (no preventDefault): code and modifiers.</summary>
    internal event Action<int, CefEventFlags>? KeyNotHandled;

    // CEF calls OnKeyEvent only for keys the page left unhandled: that is how Chrome gives web apps their shortcuts first.
    private sealed class TabKeyboardHandler(BrowserTab tab) : IKeyboardHandler
    {
        public bool OnPreKeyEvent(IWebBrowser chromiumWebBrowser, IBrowser browser, KeyType type, int windowsKeyCode,
            int nativeKeyCode, CefEventFlags modifiers, bool isSystemKey, ref bool isKeyboardShortcut) => false;

        public bool OnKeyEvent(IWebBrowser chromiumWebBrowser, IBrowser browser, KeyType type, int windowsKeyCode,
            int nativeKeyCode, CefEventFlags modifiers, bool isSystemKey)
        {
            if (type == KeyType.RawKeyDown) tab.OnUi(() => tab.KeyNotHandled?.Invoke(windowsKeyCode, modifiers));
            return false;
        }
    }

    private bool _focused; // what the view asked for, replayed once the browser is ready

    internal void SendFocus(bool focused)
    {
        _focused = focused;
        Host()?.SendFocusEvent(focused);
    }

    internal void SendCaptureLost()
    {
        TryEndInternalDrag(0, 0, CefEventFlags.None, cancelled: true);
        Host()?.SendCaptureLostEvent();
    }

    private IBrowserHost? Host() => _browser.IsBrowserInitialized && !_disposed ? _browser.GetBrowserHost() : null;


    private unsafe void Paint(bool popup, nint buffer, int width, int height)
    {
        if (_disposed || width <= 0 || height <= 0) return;

        var bitmap = popup ? PopupFrame : Frame;
        var replaced = false;
        if (bitmap is null || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
        {
            bitmap = new WriteableBitmap(width, height);
            if (popup) PopupFrame = bitmap; else Frame = bitmap;
            replaced = true;
        }

        // CEF delivers BGRA, the same format as WriteableBitmap.
        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(new ReadOnlySpan<byte>((void*)buffer, width * height * 4));
        bitmap.Invalidate();
        if (!popup) FrameChanged();

        if (replaced) VisualChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DismissPermissionRequests();
        _browser.Dispose();
        DisposeGpu();
    }


    private void OnUi(Action action)
    {
        if (_ui.HasThreadAccess) action();
        else _ui.TryEnqueue(() => { if (!_disposed) action(); });
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hWnd, ref NativePoint point);

    private struct NativePoint { public int X, Y; }

    private const int VkControl = 0x11;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    // With the external message pump CEF runs on the UI thread; OnUi still guards against that changing.

    private sealed class TabRenderHandler(BrowserTab tab) : IRenderHandler
    {
        public ScreenInfo? GetScreenInfo() => new ScreenInfo { DeviceScaleFactor = (float)tab._scale };

        public Rect GetViewRect() => new(0, 0, tab._viewWidth, tab._viewHeight);

        // Used to place context menus and native dialogs on screen.
        public bool GetScreenPoint(int viewX, int viewY, out int screenX, out int screenY)
        {
            var origin = new NativePoint();
            ClientToScreen(tab._hostWindow, ref origin);
            screenX = origin.X + tab._viewOffsetPx.X + (int)Math.Round(viewX * tab._scale);
            screenY = origin.Y + tab._viewOffsetPx.Y + (int)Math.Round(viewY * tab._scale);
            return true;
        }

        public void OnPaint(PaintElementType type, Rect dirtyRect, nint buffer, int width, int height) =>
            tab.Paint(type == PaintElementType.Popup, buffer, width, height);

        public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo acceleratedPaintInfo) =>
            tab.AcceleratedPaint(type == PaintElementType.Popup, acceleratedPaintInfo.SharedTextureHandle);

        public void OnCursorChange(nint cursor, CursorType type, CursorInfo customCursorInfo) =>
            tab.OnUi(() => tab.CursorChanged?.Invoke(type));

        // Drags that start in the page are handled inside the page (see BrowserTab.Features).
        public bool StartDragging(IDragData dragData, DragOperationsMask mask, int x, int y) =>
            tab.BeginInternalDrag(mask, dragData, x, y);

        // CEF reports which operation the page would accept at the current point.
        public void UpdateDragCursor(DragOperationsMask operation) => tab._dragOperation = operation;

        public void OnPopupShow(bool show) => tab.OnUi(() =>
        {
            if (show) return; // the size arrives in OnPopupSize
            tab.PopupBounds = null;
            tab.PopupFrame = null;
            tab._gpuPopup?.Dispose();
            tab._gpuPopup = null;
            tab.VisualChanged?.Invoke();
        });

        public void OnPopupSize(Rect rect) => tab.OnUi(() =>
        {
            tab.PopupBounds = new Windows.Foundation.Rect(rect.X, rect.Y, rect.Width, rect.Height);
            tab.VisualChanged?.Invoke();
        });

        public void OnImeCompositionRangeChanged(CefSharp.Structs.Range selectedRange, Rect[] characterBounds) { }

        public void OnVirtualKeyboardRequested(IBrowser browser, TextInputMode inputMode) { }

        public void Dispose() { }
    }

    private bool _faviconPending;
    private string? _faviconHost; // the site the shown favicon belongs to

    /// <summary>
    /// Picks the best favicon candidate and shows it only once it has been read: if the best one fails the rest are
    /// tried, then the site's /favicon.ico. When none works the tab keeps no favicon (the list shows a globe) rather
    /// than an empty square.
    /// </summary>
    private async Task ResolveFaviconAsync(IReadOnlyList<string> candidates)
    {
        var request = ++_faviconRequest;
        _faviconPending = true;
        try
        {
            var options = new List<string>();
            var best = await Favicons.PickBestAsync(candidates);
            // Windows may render an SVG incompletely (it skips text, for instance), so the larger apple-touch-icon,
            // a regular image, is tried first.
            if (best is not null && Favicons.IsSvgUrl(best))
            {
                var touch = await TouchIconsAsync();
                if (request != _faviconRequest || _disposed) return;
                options.AddRange(touch);
            }
            if (best is not null && !options.Contains(best)) options.Add(best);
            options.AddRange(candidates.Where(c => !options.Contains(c)));
            if (Uri.TryCreate(Url, UriKind.Absolute, out var page) && page.Scheme is "http" or "https")
            {
                var root = $"{page.Scheme}://{page.Authority}/favicon.ico";
                if (!options.Contains(root)) options.Add(root);
            }

            foreach (var url in options)
            {
                var image = await Favicons.LoadAsync(url);
                if (request != _faviconRequest || _disposed) return; // the page changed meanwhile
                if (image is null) continue;
                Favicon = image;
                FaviconUrl = url;
                _faviconHost = page?.Host;
                return;
            }
        }
        finally
        {
            if (request == _faviconRequest) _faviconPending = false;
        }
    }

    /// <summary>The page's apple-touch-icons, images of about 180 px.</summary>
    private async Task<List<string>> TouchIconsAsync()
    {
        try
        {
            if (!_browser.IsBrowserInitialized || _browser.GetMainFrame() is not { IsValid: true } frame) return [];
            const string script =
                "Array.from(document.querySelectorAll('link[rel~=\"apple-touch-icon\"],link[rel~=\"apple-touch-icon-precomposed\"]'))" +
                ".map(l => l.href).filter(h => h.startsWith('http')).join('\\n')";
            var response = await frame.EvaluateScriptAsync(script, timeout: TimeSpan.FromSeconds(2));
            return response.Success && response.Result is string { Length: > 0 } list
                ? list.Split('\n', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList()
                : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// The title to show. When the page has none (Chromium sends the address as the title), the site name:
    /// "youtube.com" rather than "https://www.youtube.com/watch?v=…".
    /// </summary>
    private static string ReadableTitle(string? title, string url)
    {
        var looksLikeUrl = string.IsNullOrWhiteSpace(title) || title == url ||
                           title.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                           title.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeUrl) return title!;
        return Uri.TryCreate(string.IsNullOrWhiteSpace(title) ? url : title, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host.Replace("www.", "")
            : url;
    }

    private sealed class TabDisplayHandler(BrowserTab tab) : CefSharp.Handler.DisplayHandler
    {
        protected override void OnFullscreenModeChange(IWebBrowser chromiumWebBrowser, IBrowser browser, bool fullscreen) =>
            tab.OnUi(() => tab.IsFullscreen = fullscreen);

        protected override void OnLoadingProgressChange(IWebBrowser chromiumWebBrowser, IBrowser browser, double progress) =>
            tab.OnUi(() => tab.LoadProgress = Math.Clamp(progress, 0, 1));

        protected override void OnFaviconUrlChange(IWebBrowser chromiumWebBrowser, IBrowser browser, IList<string> urls)
        {
            // A page may declare several icons (16 px, 32 px, SVG…): keep the best looking one, not the first.
            var candidates = urls.ToList();
            tab.OnUi(() => _ = tab.ResolveFaviconAsync(candidates));
        }
    }

    private sealed class TabLifeSpanHandler(BrowserTab tab) : CefSharp.Handler.LifeSpanHandler
    {
        protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            string targetUrl, string targetFrameName, WindowOpenDisposition targetDisposition, bool userGesture,
            IPopupFeatures popupFeatures, IWindowInfo windowInfo, IBrowserSettings browserSettings,
            ref bool noJavascriptAccess, out IWebBrowser? newBrowser)
        {
            newBrowser = null;
            // Popups the page opens without a user gesture are blocked when the setting is on.
            if (BlockPopups && !userGesture) return true;
            // Ads that open "on your click" (popunders on streaming sites) are closed by the blocker.
            if (Blocking.ContentBlocker.IsAdPopup(targetUrl, frame?.Url ?? browser?.MainFrame?.Url))
            {
                tab.CountBlocked();
                return true;
            }
            // window.open with a size is a popup window the page talks to (sign-in, payments): Chromium creates it in a
            // tab of ours so window.opener keeps working. Opening it as a plain tab instead would leave the sign-in hung.
            if (targetDisposition == WindowOpenDisposition.NewPopup)
            {
                var popup = new BrowserTab(tab._hostWindow, tab._ui, string.IsNullOrEmpty(targetUrl) ? "about:blank" : targetUrl, TabCreation.Popup);
                windowInfo.SetAsWindowless(tab._hostWindow);
                windowInfo.SharedTextureEnabled = UseGpu;
                browserSettings.WindowlessFrameRate = FrameRate;
                newBrowser = popup._browser;
                tab.OnUi(() => tab.PopupOpened?.Invoke(tab, popup));
                return false;
            }

            // Ctrl+click or middle click (background tab) opens a regular tab; a plain click opens the mini window.
            // Shift+click (new window) and Ctrl+Shift+click (foreground tab) open a regular tab too, as in Chrome.
            var peek = userGesture
                       && targetDisposition is not (WindowOpenDisposition.NewBackgroundTab or WindowOpenDisposition.NewWindow)
                       && (GetAsyncKeyState(VkControl) & 0x8000) == 0;
            tab.OnUi(() =>
            {
                if (peek && tab.PeekRequested is { } handler) handler(tab, targetUrl);
                else tab.NewTabRequested?.Invoke(tab, targetUrl);
            });
            return true; // cancel the popup: the app opens it
        }

        // window.close() from the page (a sign-in popup that finished) or a RequestClose the page let through. Closes
        // run through here too when the app disposes the tab; those are not reported back.
        protected override bool DoClose(IWebBrowser chromiumWebBrowser, IBrowser browser)
        {
            if (!tab._disposed) tab.OnUi(() => tab.CloseRequested?.Invoke(tab, EventArgs.Empty));
            return false;
        }
    }
}
