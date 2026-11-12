using CefSharp;
using CefSharp.Internals;
using CefSharp.OffScreen;

namespace BeautyKit;

/// <summary>A page's message (CefSharp.PostMessage) with the frame that sent it, read while that frame is still valid.</summary>
internal readonly record struct PageMessage(object? Message, bool FromMainFrame, string? FrameUrl);

/// <summary>
/// The tab's Chromium browser. CefSharp raises <see cref="ChromiumWebBrowser.JavascriptMessageReceived"/> later on
/// another thread, once the sending frame was released, so a message could not tell whether the page or one of its
/// iframes sent it. This takes the message as it arrives, on Chromium's thread, and reads the frame first.
/// </summary>
internal sealed class TabBrowser(string address, IBrowserSettings settings, IRequestContext? context, Action<PageMessage> received)
    : ChromiumWebBrowser(address, settings, context, automaticallyCreateBrowser: false), IWebBrowserInternal
{
    void IWebBrowserInternal.SetJavascriptMessageReceived(JavascriptMessageReceivedEventArgs args)
    {
        bool main;
        string? url;
        try
        {
            main = args.Frame is { IsValid: true, IsMain: true };
            url = args.Frame?.IsValid == true ? args.Frame.Url : null;
        }
        catch
        {
            return; // the frame went away: nobody to answer
        }
        received(new PageMessage(args.Message, main, url));
    }
}
