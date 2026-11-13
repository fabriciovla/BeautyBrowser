using System.Text.Json;
using CefSharp;
using CefSharp.Callback;

namespace BeautyKit;

/// <summary>One window-sized piece of a page's capture, taken with the page scrolled to <see cref="ScrollY"/> (CSS px).</summary>
public sealed record ScreenshotTile(byte[] Png, double ScrollY);

/// <summary>A whole page as pieces to join: each window-sized, the content <see cref="ContentHeight"/> CSS px tall.</summary>
public sealed record PageTiles(IReadOnlyList<ScreenshotTile> Tiles, double ViewHeight, double ContentHeight);

/// <summary>What the page's "Screenshot" menu asks for.</summary>
public enum ScreenshotMode
{
    /// <summary>The whole page, past the bottom of the window.</summary>
    FullPage,

    /// <summary>A rectangle the user drags over the page.</summary>
    Area,
}

// Screenshots through the DevTools protocol (Page.captureScreenshot).
public sealed partial class BrowserTab
{
    // Chromium cannot make a texture taller than this.
    private const int MaxCapturePixels = 16_000;

    /// <summary>The window's current contents as a PNG. Null if the page cannot be captured (a crashed tab, a page that went away).</summary>
    public async Task<byte[]?> CaptureScreenshotAsync()
    {
        try
        {
            return await TakeScreenshotAsync();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The whole page, as window-sized pieces taken while scrolling down (Page.captureScreenshot's captureBeyondViewport
    /// and a taller emulated window both repeat the visible part in offscreen mode). The page is scrolled back after.
    /// Null if the page cannot be captured.
    /// </summary>
    public async Task<PageTiles?> CaptureFullPageAsync()
    {
        double viewHeight, contentHeight, scrollX, scrollY;
        if (await CallDevToolsAsync("Page.getLayoutMetrics", null, TimeSpan.FromSeconds(10)) is not { } metrics) return null;
        using (metrics)
        {
            var root = metrics.RootElement;
            var content = root.TryGetProperty("cssContentSize", out var css) ? css
                : root.TryGetProperty("contentSize", out var plain) ? plain : default;
            var view = root.TryGetProperty("cssVisualViewport", out var visual) ? visual
                : root.TryGetProperty("visualViewport", out var oldVisual) ? oldVisual : default;
            if (content.ValueKind != JsonValueKind.Object || view.ValueKind != JsonValueKind.Object) return null;
            viewHeight = view.GetProperty("clientHeight").GetDouble();
            scrollX = view.TryGetProperty("pageX", out var x) ? x.GetDouble() : 0;
            scrollY = view.TryGetProperty("pageY", out var y) ? y.GetDouble() : 0;
            contentHeight = Math.Clamp(content.GetProperty("height").GetDouble(), viewHeight, MaxCapturePixels);
        }
        if (viewHeight < 1) return null;

        var tiles = new List<ScreenshotTile>();
        try
        {
            for (var top = 0.0; ; top += viewHeight)
            {
                // The last piece cannot scroll past the end: it reports where it really is, and overlaps the one before.
                var at = await ScrollToAsync(scrollX, top);
                await Task.Delay(220); // lazy images and sticky headers settle
                if (await TakeScreenshotAsync() is not { } png) return null;
                tiles.Add(new ScreenshotTile(png, at));
                if (top + viewHeight >= contentHeight || at + viewHeight >= contentHeight - 0.5) break;
            }
            return new PageTiles(tiles, viewHeight, contentHeight);
        }
        catch
        {
            return null;
        }
        finally
        {
            await ScrollToAsync(scrollX, scrollY); // the page stays where the user had it
        }
    }

    /// <summary>Scrolls the page without animation; returns where it really got to.</summary>
    private async Task<double> ScrollToAsync(double x, double y)
    {
        var script = FormattableString.Invariant(
            $"(function(){{window.scrollTo({{left:{x},top:{y},behavior:'instant'}});return window.scrollY;}})()");
        if (!_browser.IsBrowserInitialized || _browser.GetMainFrame() is not { IsValid: true } frame) return y;
        var response = await frame.EvaluateScriptAsync(script, timeout: TimeSpan.FromSeconds(5));
        return response.Success && response.Result is not null
            ? Convert.ToDouble(response.Result, System.Globalization.CultureInfo.InvariantCulture)
            : y;
    }

    private async Task<byte[]?> TakeScreenshotAsync()
    {
        var args = new Dictionary<string, object> { ["format"] = "png", ["fromSurface"] = true };
        if (await CallDevToolsAsync("Page.captureScreenshot", args, TimeSpan.FromSeconds(30)) is not { } shot) return null;
        using (shot)
        {
            return shot.RootElement.TryGetProperty("data", out var data) && data.GetString() is { Length: > 0 } base64
                ? Convert.FromBase64String(base64)
                : null;
        }
    }

    /// <summary>Runs a protocol method and returns its result, or null if it failed or took longer than <paramref name="timeout"/>.</summary>
    private async Task<JsonDocument?> CallDevToolsAsync(string method, Dictionary<string, object>? args, TimeSpan timeout)
    {
        if (Host() is not { } host) return null;

        var id = Interlocked.Increment(ref _nextCaptureId);
        var done = new TaskCompletionSource<JsonDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = host.AddDevToolsMessageObserver(new ResultObserver(id, done));
        if (await _browser.GetBrowser().ExecuteDevToolsMethodAsync(id, method, args) != id) return null;

        var winner = await Task.WhenAny(done.Task, Task.Delay(timeout));
        return winner == done.Task ? await done.Task : null;
    }

    private sealed class ResultObserver(int id, TaskCompletionSource<JsonDocument?> done) : IDevToolsMessageObserver
    {
        public bool OnDevToolsMessage(IBrowser browser, Stream message) => false;

        public void OnDevToolsMethodResult(IBrowser browser, int messageId, bool success, Stream result)
        {
            if (messageId != id) return;
            try
            {
                done.TrySetResult(success ? JsonDocument.Parse(result) : null);
            }
            catch
            {
                done.TrySetResult(null);
            }
        }

        public void OnDevToolsEvent(IBrowser browser, string method, Stream parameters) { }
        public void OnDevToolsAgentAttached(IBrowser browser) { }
        public void OnDevToolsAgentDetached(IBrowser browser) { }
        public void Dispose() { }
    }
}
