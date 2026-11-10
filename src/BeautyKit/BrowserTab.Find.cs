using CefSharp;
using CefSharp.Structs;

namespace BeautyKit;

public sealed partial class BrowserTab
{
    /// <summary>Match count and the 1-based index of the highlighted match (0 when there is none).</summary>
    public event EventHandler<(int Count, int Active)>? FindResult;

    private string _findText = "";

    /// <summary>Searching the same text again moves to the next (or previous) match; new text starts over.</summary>
    public void Find(string text, bool forward = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            StopFinding();
            return;
        }
        var next = text == _findText;
        _findText = text;
        Host()?.Find(text, forward, matchCase: false, findNext: next);
    }

    public void StopFinding()
    {
        _findText = "";
        Host()?.StopFinding(clearSelection: true);
        FindResult?.Invoke(this, (0, 0));
    }

    private sealed class TabFindHandler(BrowserTab tab) : IFindHandler
    {
        public void OnFindResult(IWebBrowser chromiumWebBrowser, IBrowser browser, int identifier, int count,
            Rect selectionRect, int activeMatchOrdinal, bool finalUpdate)
        {
            if (!finalUpdate) return;
            tab.OnUi(() => tab.FindResult?.Invoke(tab, (count, activeMatchOrdinal)));
        }
    }
}
