using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace BeautyKit;

// "Copy image": the picture comes from the page, so blob:, data: and private images work; the address is only the
// last resort.
public sealed partial class BrowserView
{
    private static async void CopyImage(BrowserTab tab, ContextMenuInfo info)
    {
        try
        {
            if (await tab.ReadImageAsync(info.X, info.Y, info.SourceUrl) is { } png)
            {
                var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(png.AsBuffer());
                stream.Seek(0);
                var package = new DataPackage();
                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                Clipboard.SetContent(package);
            }
            else if (IsWebUrl(info.SourceUrl))
            {
                CopyImage(info.SourceUrl); // cross-origin without CORS: the page cannot read it, so it is downloaded
            }
        }
        catch
        {
            // The clipboard is in use by another app: nothing is copied.
        }
    }
}
