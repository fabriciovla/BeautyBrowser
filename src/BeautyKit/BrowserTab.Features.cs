using System.ComponentModel;
using CefSharp;
using CefSharp.Enums;

namespace BeautyKit;

public sealed record BrowserDownload(
    int Id, string Url, string FileName, string FullPath,
    long TotalBytes, long ReceivedBytes, bool IsComplete, bool IsCancelled);

public sealed partial class BrowserTab
{
    private bool _isFullscreen;

    // Drag and drop that started inside the page (text, links, draggable elements).
    private bool _internalDrag;
    private DragOperationsMask _dragAllowed;
    private DragOperationsMask _dragOperation;

    /// <summary>The page entered or left full screen (a YouTube video, for instance).</summary>
    public event EventHandler<bool>? FullscreenChanged;

    internal event Action<ContextMenuInfo>? ContextMenuRequested;

    /// <summary>A download started; it already has a path in the Downloads folder.</summary>
    public event EventHandler<BrowserDownload>? DownloadStarted;

    public event EventHandler<BrowserDownload>? DownloadUpdated;


    private double _zoomLevel;

    /// <summary>Current zoom in percent. Each Chromium zoom level is ×1.2.</summary>
    public int ZoomPercent => (int)Math.Round(Math.Pow(1.2, _zoomLevel) * 100);

    public void ZoomIn() => SetZoomLevel(_zoomLevel + 0.5);
    public void ZoomOut() => SetZoomLevel(_zoomLevel - 0.5);
    public void ResetZoom() => SetZoomLevel(ZoomLevelFor(DefaultZoomPercent));

    private void SetZoomLevel(double level)
    {
        _zoomLevel = Math.Clamp(level, -4, 6);
        Host()?.SetZoomLevel(_zoomLevel);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ZoomPercent)));
    }

    public bool IsFullscreen
    {
        get => _isFullscreen;
        private set
        {
            if (_isFullscreen == value) return;
            _isFullscreen = value;
            FullscreenChanged?.Invoke(this, value);
        }
    }

    public void ExitFullscreen()
    {
        if (_isFullscreen && _browser.IsBrowserInitialized)
            _browser.ExecuteScriptAsync("if (document.fullscreenElement) document.exitFullscreen();");
    }


    internal void OpenInNewTab(string url) => NewTabRequested?.Invoke(this, url);
    internal void ViewSource() => OpenInNewTab("view-source:" + Url);
    /// <summary>Downloads <paramref name="url"/>. With <paramref name="askWhere"/> the Save As dialog shows even if the setting is off.</summary>
    internal void Download(string url, bool askWhere = false)
    {
        if (Host() is not { } host) return;
        if (askWhere) Interlocked.Increment(ref _askNext);
        host.StartDownload(url);
    }

    // Downloads started from a "Save … as" entry that still have to show the Save As dialog.
    private int _askNext;

    private bool TakeAskNext()
    {
        int n;
        do
        {
            n = Volatile.Read(ref _askNext);
            if (n == 0) return false;
        } while (Interlocked.CompareExchange(ref _askNext, n - 1, n) != n);
        return true;
    }
    public void Print() => Host()?.Print();
    internal void ReplaceMisspelling(string word) => Host()?.ReplaceMisspelling(word);
    internal void AddToDictionary(string word) => Host()?.AddWordToDictionary(word);

    internal void Undo() => _browser.Undo();
    internal void Redo() => _browser.Redo();
    internal void Cut() => _browser.Cut();
    internal void Copy() => _browser.Copy();
    internal void Paste() => _browser.Paste();
    internal void Delete() => _browser.Delete();
    internal void SelectAll() => _browser.SelectAll();

    /// <summary>Asks the app to open DevTools docked in the window, inspecting the element at (x, y).</summary>
    public event EventHandler<(int X, int Y)>? InspectRequested;

    internal void Inspect(int x, int y) => InspectRequested?.Invoke(this, (x, y));

    internal IBrowserHost? DevToolsHost() => Host();


    private bool BeginInternalDrag(DragOperationsMask allowed, IDragData data, int x, int y)
    {
        var host = Host();
        if (host is null) return false;

        _internalDrag = true;
        _dragAllowed = allowed;
        _dragOperation = DragOperationsMask.None;

        var mouse = new MouseEvent(x, y, CefEventFlags.LeftMouseButton);
        host.DragTargetDragEnter(data, mouse, allowed);
        host.DragTargetDragOver(mouse, allowed);
        return true;
    }

    /// <summary>A drag that started in the page (an image, a link, a draggable card) is in progress.</summary>
    internal bool IsDraggingInPage => _internalDrag;

    // Elements that react to the mouse on their own: pressing on them is not pressing on empty page.
    private const string InteractiveSelector =
        "a,button,input,textarea,select,option,label,summary,img,picture,video,audio,canvas,svg,iframe,embed,object," +
        "[draggable=true],[contenteditable=''],[contenteditable=true],[onclick],[onmousedown],[onpointerdown]," +
        "[role=button],[role=link],[role=slider],[role=tab],[role=menuitem],[role=option],[role=checkbox]," +
        "[role=switch],[role=textbox],[role=combobox],[role=gridcell],[role=treeitem],[role=listitem]";

    /// <summary>
    /// Whether (x, y) in the view (DIPs) is empty page: no image, button, link, draggable or other interactive element
    /// there. Used before dragging the window from the top of the page. False when it cannot tell.
    /// </summary>
    internal async Task<bool> IsEmptySpotAsync(double x, double y)
    {
        try
        {
            if (!_browser.IsBrowserInitialized || _browser.GetMainFrame() is not { IsValid: true } frame) return false;
            var zoom = Math.Pow(1.2, _zoomLevel); // CSS pixels are DIPs divided by the zoom
            var cssX = (x / zoom).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var cssY = (y / zoom).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var script =
                $"(function(){{var e=document.elementFromPoint({cssX},{cssY});if(!e)return true;" +
                $"return !e.closest(\"{InteractiveSelector}\");}})()";
            var response = await frame.EvaluateScriptAsync(script, timeout: TimeSpan.FromMilliseconds(500));
            return response.Success && response.Result is true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>While an internal drag is active, mouse moves are reported as drag-over.</summary>
    private bool TryContinueInternalDrag(int x, int y, CefEventFlags flags)
    {
        if (!_internalDrag) return false;
        Host()?.DragTargetDragOver(new MouseEvent(x, y, flags), _dragAllowed);
        return true;
    }

    /// <summary>Releasing the button ends the internal drag with a drop on the page.</summary>
    private bool TryEndInternalDrag(int x, int y, CefEventFlags flags, bool cancelled = false)
    {
        if (!_internalDrag) return false;
        _internalDrag = false;

        var host = Host();
        if (host is null) return true;

        if (cancelled)
        {
            host.DragTargetDragLeave();
            host.DragSourceEndedAt(x, y, DragOperationsMask.None);
        }
        else
        {
            host.DragTargetDragDrop(new MouseEvent(x, y, flags));
            host.DragSourceEndedAt(x, y, _dragOperation);
        }
        host.DragSourceSystemDragEnded();
        return true;
    }


    internal void ExternalDragEnter(IDragData data, int x, int y)
    {
        _dragOperation = DragOperationsMask.None;
        Host()?.DragTargetDragEnter(data, new MouseEvent(x, y, CefEventFlags.None), DragOperationsMask.Every);
    }

    /// <summary>The operation the page accepts at this point, according to CEF's last answer.</summary>
    internal DragOperationsMask ExternalDragOver(int x, int y)
    {
        Host()?.DragTargetDragOver(new MouseEvent(x, y, CefEventFlags.None), DragOperationsMask.Every);
        return _dragOperation;
    }

    internal void ExternalDragLeave() => Host()?.DragTargetDragLeave();

    internal void ExternalDrop(int x, int y) => Host()?.DragTargetDragDrop(new MouseEvent(x, y, CefEventFlags.None));


    private sealed class TabContextMenuHandler(BrowserTab tab) : IContextMenuHandler
    {
        public void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            IContextMenuParams parameters, IMenuModel model) { }

        public bool OnContextMenuCommand(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            IContextMenuParams parameters, CefMenuCommand commandId, CefEventFlags eventFlags) => false;

        public void OnContextMenuDismissed(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame) { }

        // CEF's menu is replaced by our own WinUI menu with Chrome's entries.
        public bool RunContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            IContextMenuParams parameters, IMenuModel model, IRunContextMenuCallback callback)
        {
            var info = new ContextMenuInfo(
                parameters.XCoord, parameters.YCoord,
                parameters.LinkUrl ?? "", parameters.SourceUrl ?? "",
                parameters.MediaType, parameters.HasImageContents,
                parameters.SelectionText ?? "",
                parameters.IsEditable, parameters.EditStateFlags,
                parameters.MisspelledWord ?? "",
                parameters.DictionarySuggestions?.ToArray() ?? []);

            callback.Cancel(); // CEF's menu is not shown; the actions run on our side
            tab.OnUi(() => tab.ContextMenuRequested?.Invoke(info));
            return true;
        }
    }

    private sealed class TabDownloadHandler(BrowserTab tab) : IDownloadHandler
    {
        // Paths assigned per download id: CEF sometimes reports FullPath only after some progress.
        // Dangerous files answered "keep" are let through when their callback comes around again.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _paths = [];
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _approved = [];

        public bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod) => true;

        // Downloads go straight to the Downloads folder without a dialog; existing names get a number.
        public bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem,
            IBeforeDownloadCallback callback)
        {
            // A file that can run code waits for the user: keep it and this method runs again, or discard it.
            if (DangerousFile.IsDangerous(downloadItem.SuggestedFileName) && !_approved.TryRemove(downloadItem.Id, out _) &&
                tab.AskAboutDownload(downloadItem.SuggestedFileName, downloadItem.Url ?? "", keep =>
                {
                    if (keep)
                    {
                        _approved[downloadItem.Id] = true;
                        OnBeforeDownload(chromiumWebBrowser, browser, downloadItem, callback);
                    }
                    else
                    {
                        callback.Dispose(); // not continuing cancels the download
                    }
                }))
                return true;

            Directory.CreateDirectory(DownloadsDirectory);
            var name = string.IsNullOrWhiteSpace(downloadItem.SuggestedFileName) ? L.T("download", "descarga") : downloadItem.SuggestedFileName;
            var path = UniquePath(Path.Combine(DownloadsDirectory, name));
            _paths[downloadItem.Id] = path;

            // With "ask where to save", Chromium shows the Save As dialog starting at this path.
            callback.Continue(path, showDialog: tab.TakeAskNext() || AskWhereToSave);

            var info = ToInfo(downloadItem, path);
            tab.OnUi(() => tab.DownloadStarted?.Invoke(tab, info));
            return true;
        }

        public void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem,
            IDownloadItemCallback callback)
        {
            if (!_paths.TryGetValue(downloadItem.Id, out var path)) return;
            // If another path was picked in Save As, Chromium reports the real one.
            if (!string.IsNullOrEmpty(downloadItem.FullPath)) path = _paths[downloadItem.Id] = downloadItem.FullPath;
            var info = ToInfo(downloadItem, path);
            if (info.IsComplete || info.IsCancelled) _paths.TryRemove(downloadItem.Id, out _);
            tab.OnUi(() => tab.DownloadUpdated?.Invoke(tab, info));
        }

        private static BrowserDownload ToInfo(DownloadItem item, string path) => new(
            item.Id, item.Url ?? "", Path.GetFileName(path), path,
            item.TotalBytes, item.ReceivedBytes, item.IsComplete, item.IsCancelled);

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (var i = 1; ; i++)
            {
                var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }
    }
}
