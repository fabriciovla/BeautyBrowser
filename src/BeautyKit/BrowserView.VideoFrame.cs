using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace BeautyKit;

public sealed partial class BrowserView
{
    private static async void CopyVideoFrame(BrowserTab tab, ContextMenuInfo info)
    {
        try
        {
            if (await tab.ReadVideoFrameAsync(info.X, info.Y) is not { } png) return;
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
        }
        catch
        {
            // The clipboard is in use by another app: nothing is copied.
        }
    }
}
