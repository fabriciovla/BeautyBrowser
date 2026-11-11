using System.Globalization;
using System.Text.Json;
using CefSharp;
using Windows.ApplicationModel.DataTransfer;

namespace BeautyKit;

// What the context menu and the Esc key do on a tab that CEF has no menu command for: they run small scripts on the
// element under the cursor.
public sealed partial class BrowserTab
{
    /// <summary>Ctrl+Shift+V / "Paste as plain text": pastes the clipboard without the source's formatting.</summary>
    internal async void PasteAsPlainText()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            var text = await content.GetTextAsync();
            // CefSharp has no "paste and match style": the text goes in as typed, which also keeps Undo working.
            if (text.Length > 0 && _browser.IsBrowserInitialized && _browser.GetFocusedFrame() is { IsValid: true } frame)
                frame.ExecuteJavaScriptAsync($"document.execCommand('insertText',false,{JsonSerializer.Serialize(text)});");
        }
        catch
        {
            // The clipboard is in use by another app: nothing is pasted.
        }
    }

    /// <summary>Esc while loading: stops the page from loading, keeping what already arrived.</summary>
    public void Stop()
    {
        if (_browser.IsBrowserInitialized) _browser.Stop();
    }


    /// <summary>
    /// A script that runs with <c>e</c> (the element at the point, going into shadow roots) and <c>v</c> (the video
    /// under it, even behind a player's overlays) defined. (x, y) are DIPs in the view; CSS pixels are DIPs / zoom.
    /// </summary>
    private string AtPointScript(int x, int y, string body)
    {
        var zoom = Math.Pow(1.2, _zoomLevel);
        var cssX = (x / zoom).ToString(CultureInfo.InvariantCulture);
        var cssY = (y / zoom).ToString(CultureInfo.InvariantCulture);
        return
            $"(function(){{var x={cssX},y={cssY};var e=document.elementFromPoint(x,y);" +
            "while(e&&e.shadowRoot){var n=e.shadowRoot.elementFromPoint(x,y);if(!n||n===e)break;e=n;}" +
            "var v=e&&e.tagName==='VIDEO'?e:null;" +
            "if(!v){var all=document.elementsFromPoint(x,y);for(var i=0;i<all.length;i++){if(all[i].tagName==='VIDEO'){v=all[i];break;}}}" +
            body + "})()";
    }

    /// <summary>Link text and video state at (x, y), for the entries of the context menu. Unchanged when it cannot read.</summary>
    internal async Task<ContextMenuInfo> ReadPointAsync(ContextMenuInfo info)
    {
        try
        {
            if (!_browser.IsBrowserInitialized || _browser.GetMainFrame() is not { IsValid: true } frame) return info;
            var script = AtPointScript(info.X, info.Y,
                "var a=e&&e.closest?e.closest('a[href],[role=link]'):null;" +
                "var t=a?(a.innerText||a.textContent||a.getAttribute('aria-label')||''):'';" +
                "t=t.replace(/\\s+/g,' ').trim();" +
                "return JSON.stringify({text:t,sel:String(window.getSelection()||''),loop:!!(v&&v.loop),controls:!!(v&&v.controls)," +
                "pip:!!(v&&document.pictureInPictureEnabled&&!v.disablePictureInPicture)});");
            var response = await frame.EvaluateScriptAsync(script, timeout: TimeSpan.FromMilliseconds(500));
            if (!response.Success || response.Result is not string json) return info;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Text selected inside a link does not always reach CEF's parameters: the page knows it.
            var selection = info.HasSelection ? info.SelectionText : root.GetProperty("sel").GetString() ?? "";
            return info with
            {
                SelectionText = selection,
                LinkText = root.GetProperty("text").GetString() ?? "",
                VideoLoop = root.GetProperty("loop").GetBoolean(),
                VideoControls = root.GetProperty("controls").GetBoolean(),
                CanPictureInPicture = root.GetProperty("pip").GetBoolean(),
            };
        }
        catch
        {
            return info;
        }
    }

    internal void ToggleVideoLoop(int x, int y) => RunAtPoint(x, y, "if(v)v.loop=!v.loop;");

    internal void ToggleVideoControls(int x, int y) => RunAtPoint(x, y, "if(v)v.controls=!v.controls;");

    internal void TogglePictureInPicture(int x, int y) =>
        RunAtPoint(x, y,
            "if(v){var p=document.pictureInPictureElement===v?document.exitPictureInPicture():v.requestPictureInPicture();" +
            "if(p&&p.catch)p.catch(function(){});}");

    private void RunAtPoint(int x, int y, string body)
    {
        if (_browser.IsBrowserInitialized) _browser.ExecuteScriptAsync(AtPointScript(x, y, body));
    }
}
