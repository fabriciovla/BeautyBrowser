using System.Text;
using System.Text.Json;
using CefSharp;
using CefSharp.Callback;

namespace BeautyKit;

// "Save as…" for a page: the whole page (images, styles) in one MHTML file, as Chrome does, not just its HTML.
public sealed partial class BrowserTab
{
    // Protocol message ids are shared with every other client of the tab (the DevTools panel counts up from small
    // numbers), so this one counts from far away from them.
    private static int _nextCaptureId = 1_000_000_000;

    /// <summary>A suggested file name for saving this page, without extension.</summary>
    internal string SuggestedPageFileName()
    {
        var name = string.IsNullOrWhiteSpace(Title) ? "page" : Title.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim(' ', '.');
        return clean.Length == 0 ? "page" : clean.Length > 100 ? clean[..100].TrimEnd(' ', '.') : clean;
    }

    /// <summary>Writes the page, with everything it needs, to <paramref name="path"/> as MHTML. False if it could not.</summary>
    internal async Task<bool> SavePageAsMhtmlAsync(string path)
    {
        try
        {
            if (Host() is not { } host) return false;

            var id = Interlocked.Increment(ref _nextCaptureId);
            var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = host.AddDevToolsMessageObserver(new CaptureObserver(id, done));
            if (await _browser.GetBrowser().ExecuteDevToolsMethodAsync(id, "Page.captureSnapshot", new Dictionary<string, object> { ["format"] = "mhtml" }) != id) return false;

            var winner = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            if (winner != done.Task || await done.Task is not { } mhtml) return false;

            await File.WriteAllTextAsync(path, mhtml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch
        {
            return false; // the file could not be written, or the page went away meanwhile
        }
    }

    private sealed class CaptureObserver(int id, TaskCompletionSource<string?> done) : IDevToolsMessageObserver
    {
        public bool OnDevToolsMessage(IBrowser browser, Stream message) => false;

        public void OnDevToolsMethodResult(IBrowser browser, int messageId, bool success, Stream result)
        {
            if (messageId != id) return;
            try
            {
                using var doc = JsonDocument.Parse(result);
                done.TrySetResult(success && doc.RootElement.TryGetProperty("data", out var data) ? data.GetString() : null);
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
