using CefSharp;

namespace BeautyKit;

// Closing a tab the way Chrome does: the page runs its beforeunload handler first, so an unsaved draft or form can ask
// "Leave site?" instead of being lost.
public sealed partial class BrowserTab
{
    private bool _closing; // a close was asked and the page has not answered yet

    /// <summary>
    /// Asks the page whether it may close. <see cref="CloseRequested"/> fires once it may (right away when the page has
    /// no beforeunload handler); if the user chooses to stay, nothing happens. Chromium proceeds on its own when the page
    /// does not answer.
    /// </summary>
    public void RequestClose()
    {
        if (_disposed || _closing) return;
        if (Host() is not { } host)
        {
            // Not created yet (a restored tab never shown) or still starting: there is no page to ask.
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        _closing = true;
        host.CloseBrowser(forceClose: false); // DoClose runs if the page lets it close
    }

    private sealed class TabJsDialogHandler(BrowserTab tab) : CefSharp.Handler.JsDialogHandler
    {
        // The dialogs themselves are Chromium's; this only learns that "Leave site?" was answered. If the user left,
        // DoClose comes next; if they stayed, the tab must accept a new close request.
        protected override void OnDialogClosed(IWebBrowser chromiumWebBrowser, IBrowser browser) =>
            tab.OnUi(() => tab._closing = false);
    }
}
