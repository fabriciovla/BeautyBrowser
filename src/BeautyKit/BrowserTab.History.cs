using CefSharp;

namespace BeautyKit;

/// <summary>
/// Back/forward that survives a restart or Ctrl+Shift+T. CEF cannot rebuild a browser's history, so the pages before
/// (and after) the restored one are kept here, and going to them replaces the current entry (location.replace): the
/// page then moves along the remembered list while Chromium's own history stays put underneath.
/// </summary>
public sealed partial class BrowserTab
{
    private const int MaxSavedHistory = 50;

    private readonly List<string> _restoredBack = [];    // oldest first; the last one is the page "Back" goes to
    private readonly List<string> _restoredForward = []; // next first; only while on Chromium's first entry
    private bool _cefCanGoBack, _cefCanGoForward;

    /// <summary>Gives a restored tab the pages around its own (its address is <paramref name="urls"/>[index]).</summary>
    public void SetRestoredHistory(IReadOnlyList<string> urls, int index)
    {
        if (index < 0 || index >= urls.Count) return;
        _restoredBack.Clear();
        _restoredBack.AddRange(urls.Take(index));
        _restoredForward.Clear();
        _restoredForward.AddRange(urls.Skip(index + 1));
        UpdateHistoryState();
    }

    /// <summary>The tab's back/forward list and the position of the current page, to save it.</summary>
    public (List<string> Urls, int Index) GetHistory()
    {
        var cef = new List<string>();
        var current = 0;
        if (Host() is { } host)
        {
            using var visitor = new HistoryVisitor();
            host.GetNavigationEntries(visitor, currentOnly: false); // visited right away, on this thread
            cef = visitor.Urls;
            current = Math.Max(0, visitor.Current);
        }
        if (cef.Count == 0) cef.Add(Url);

        // While the restored forward pages are pending the tab sits on Chromium's first entry; they come after it.
        var urls = new List<string>(_restoredBack) { cef[0] };
        urls.AddRange(_restoredForward);
        urls.AddRange(cef.Skip(1));
        var index = _restoredBack.Count + (_restoredForward.Count > 0 ? 0 : current);

        // Only the pages closest to the current one are kept.
        var start = Math.Clamp(index - MaxSavedHistory / 2, 0, Math.Max(0, urls.Count - MaxSavedHistory));
        var kept = urls.Skip(start).Take(MaxSavedHistory).ToList();
        return (kept, index - start);
    }

    public void GoBack()
    {
        if (_cefCanGoBack) { if (_browser.IsBrowserInitialized) _browser.Back(); return; }
        if (_restoredBack.Count == 0) return;
        var target = _restoredBack[^1];
        _restoredBack.RemoveAt(_restoredBack.Count - 1);
        _restoredForward.Insert(0, Url);
        ReplaceCurrent(target);
    }

    public void GoForward()
    {
        // Pending restored pages come before Chromium's own forward entries (see GetHistory).
        if (_restoredForward.Count == 0) { if (_browser.IsBrowserInitialized) _browser.Forward(); return; }
        var target = _restoredForward[0];
        _restoredForward.RemoveAt(0);
        _restoredBack.Add(Url);
        ReplaceCurrent(target);
    }

    private void ReplaceCurrent(string url)
    {
        UpdateHistoryState();
        if (!_created || !_browser.IsBrowserInitialized) { Navigate(url); return; }
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _browser.GetMainFrame().ExecuteJavaScriptAsync($"location.replace({System.Text.Json.JsonSerializer.Serialize(url)})");
        }
        else
        {
            _browser.LoadUrl(url); // pages without scripts (start page, PDFs, errors): a regular load
        }
    }

    // Called with Chromium's state after every load.
    private void OnHistoryChanged(bool canGoBack, bool canGoForward)
    {
        // A new page opened from the first entry: like in any browser, what was ahead is gone.
        if (canGoBack && !_cefCanGoBack) _restoredForward.Clear();
        _cefCanGoBack = canGoBack;
        _cefCanGoForward = canGoForward;
        UpdateHistoryState();
    }

    private void UpdateHistoryState()
    {
        CanGoBack = _cefCanGoBack || _restoredBack.Count > 0;
        CanGoForward = _cefCanGoForward || _restoredForward.Count > 0;
    }

    private sealed class HistoryVisitor : INavigationEntryVisitor
    {
        public List<string> Urls { get; } = [];
        public int Current { get; private set; } = -1;

        public bool Visit(NavigationEntry entry, bool current, int index, int total)
        {
            if (entry is { IsValid: true } && !string.IsNullOrEmpty(entry.Url))
            {
                if (current) Current = Urls.Count;
                Urls.Add(entry.Url);
            }
            return true;
        }

        public void Dispose() { }
    }
}
