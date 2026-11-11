namespace BeautyKit;

// Closing a tab the way Chrome does: the page runs its beforeunload handler first, so an unsaved draft or form can ask
// "Leave site?" instead of being lost.
public sealed partial class BrowserTab
{
    /// <summary>
    /// Asks the page whether it may close. <see cref="CloseRequested"/> fires once it may (right away when the page has
    /// no beforeunload handler); if the user chooses to stay, nothing happens and it can be asked again. Chromium
    /// proceeds on its own when the page does not answer, and ignores a repeated request while one is pending.
    /// </summary>
    public void RequestClose()
    {
        if (_disposed) return;
        if (Host() is not { } host)
        {
            // Not created yet (a restored tab never shown) or still starting: there is no page to ask.
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        host.CloseBrowser(forceClose: false); // DoClose runs if the page lets it close
    }
}
