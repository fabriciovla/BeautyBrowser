using CefSharp;

namespace BeautyKit;

// "Copy video frame": the frame currently on screen is drawn from the page's own <video> element.
public sealed partial class BrowserTab
{
    /// <summary>
    /// The current frame of the video under (x, y) as PNG bytes, or null when there is none or the page cannot hand it
    /// over (a cross-origin video without CORS, or protected content, taints the canvas).
    /// </summary>
    internal async Task<byte[]?> ReadVideoFrameAsync(int x, int y)
    {
        try
        {
            if (!_browser.IsBrowserInitialized || _browser.GetMainFrame() is not { IsValid: true } frame) return null;

            var script = AtPointScript(x, y,
                "if(!v||!v.videoWidth||!v.videoHeight)return null;" +
                "try{var c=document.createElement('canvas');c.width=v.videoWidth;c.height=v.videoHeight;" +
                "c.getContext('2d').drawImage(v,0,0);return c.toDataURL('image/png');}catch(err){return null;}");
            var response = await frame.EvaluateScriptAsync(script, timeout: TimeSpan.FromSeconds(5));
            const string prefix = "data:image/png;base64,";
            return response.Success && response.Result is string url && url.StartsWith(prefix, StringComparison.Ordinal)
                ? Convert.FromBase64String(url[prefix.Length..])
                : null;
        }
        catch
        {
            return null;
        }
    }
}
