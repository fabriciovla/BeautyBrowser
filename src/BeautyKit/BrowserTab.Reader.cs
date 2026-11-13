using System.Text.Json;
using CefSharp;

namespace BeautyKit;

// Reading mode of the tab (see ReaderMode): whether the page looks like an article, and opening / closing the reader.
public sealed partial class BrowserTab
{
    private bool _isReadable;
    private int _readableCheck;

    /// <summary>The page looks like an article: the address bar offers the reader.</summary>
    public bool IsReadable { get => _isReadable; private set => Set(ref _isReadable, value); }

    /// <summary>The tab is showing the reader (beauty://reader/…).</summary>
    public bool InReader => ReaderMode.Is(Url);

    // Called when a page starts and finishes loading (see the constructor): the answer is only for the page that asked.
    private void ReaderPageStarted()
    {
        Interlocked.Increment(ref _readableCheck);
        IsReadable = false;
    }

    private async void ReaderPageLoaded()
    {
        var check = _readableCheck;
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        // Pages that fill themselves with scripts are looked at again a little later.
        foreach (var delay in new[] { 300, 2500 })
        {
            await Task.Delay(delay);
            if (_disposed || check != _readableCheck) return;
            if (await EvaluateAsync(ReaderMode.ReaderableScript) is true)
            {
                IsReadable = true;
                return;
            }
        }
    }

    /// <summary>
    /// Shows the page's article in the reader, or, if the reader is already open, goes back to the page. False if the
    /// page has no article to show.
    /// </summary>
    public async Task<bool> ToggleReaderAsync()
    {
        if (ReaderMode.TryOriginal(Url, out var original))
        {
            Navigate(original);
            return true;
        }
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;

        try
        {
            if (await EvaluateAsync(ReaderMode.ParseScript, TimeSpan.FromSeconds(15)) is not string json) return false;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var html = Text("html");
            if (root.TryGetProperty("chars", out var chars) && chars.GetInt32() < 250) return false; // too little: not an article
            if (html.Length == 0) return false;

            var title = Text("title");
            var token = ReaderMode.Add(new ReaderArticle(
                title.Length > 0 ? title : Title, Text("byline"), Text("site"), Text("lang"), Text("dir"), html,
                root.TryGetProperty("words", out var words) ? words.GetInt32() : 0, Url));
            Navigate(ReaderMode.UrlFor(token, Url));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<object?> EvaluateAsync(string script, TimeSpan? timeout = null)
    {
        try
        {
            if (!_browser.IsBrowserInitialized || _browser.GetMainFrame() is not { IsValid: true } frame) return null;
            var response = await frame.EvaluateScriptAsync(script, timeout: timeout ?? TimeSpan.FromSeconds(5));
            return response.Success ? response.Result : null;
        }
        catch
        {
            return null;
        }
    }
}
