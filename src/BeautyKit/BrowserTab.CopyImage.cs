using System.Globalization;
using System.Text.Json;
using CefSharp;

namespace BeautyKit;

// "Copy image" reads the picture from the page that already has it, instead of downloading it again: that is the only
// way to get blob: and data: images, and images that need the page's cookies or referrer.
public sealed partial class BrowserTab
{
    /// <summary>
    /// The image under (x, y) in the view as PNG bytes, or null when the page cannot hand it over (a cross-origin
    /// image without CORS taints the canvas). <paramref name="source"/> finds the image if the point misses it.
    /// </summary>
    internal async Task<byte[]?> ReadImageAsync(int x, int y, string source)
    {
        try
        {
            if (!_browser.IsBrowserInitialized || _browser.GetMainFrame() is not { IsValid: true } frame) return null;

            var zoom = Math.Pow(1.2, _zoomLevel); // CSS pixels are DIPs divided by the zoom
            var cssX = (x / zoom).ToString(CultureInfo.InvariantCulture);
            var cssY = (y / zoom).ToString(CultureInfo.InvariantCulture);
            var script =
                $"(async function(){{var x={cssX},y={cssY},src={JsonSerializer.Serialize(source)};" +
                // An image at the point; else any image on the page with that address.
                "var all=document.elementsFromPoint(x,y),img=null;" +
                "for(var i=0;i<all.length&&!img;i++){if(all[i].tagName==='IMG')img=all[i];}" +
                "if(!img){img=Array.prototype.find.call(document.images,function(m){return m.currentSrc===src||m.src===src;})||null;}" +
                "function png(bitmap,w,h){var c=document.createElement('canvas');c.width=w;c.height=h;" +
                "c.getContext('2d').drawImage(bitmap,0,0);return c.toDataURL('image/png');}" +
                // 1) the picture as the page shows it; throws if it came from another origin without CORS
                "try{if(img&&img.complete&&img.naturalWidth>0)return png(img,img.naturalWidth,img.naturalHeight);}catch(e){}" +
                // 2) fetched with the page's cookies and referrer: works for blob:, data: and same-origin images
                "try{var r=await fetch(src,{credentials:'include'});var b=await createImageBitmap(await r.blob());" +
                "return png(b,b.width,b.height);}catch(e){}" +
                "return null;})()";

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
