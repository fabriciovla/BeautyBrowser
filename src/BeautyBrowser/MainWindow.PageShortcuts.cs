using BeautyBrowser.Controls;
using BeautyKit;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace BeautyBrowser;

// Which shortcuts the page gets first. As in Chrome, only a few are the browser's no matter what (new and close tab,
// switching tabs…); the rest go to the page first and the app acts only if the page does not take them, so Ctrl+S
// saves in Docs, Ctrl+D duplicates in Figma and Ctrl+Shift+P opens VS Code's palette.
public sealed partial class MainWindow
{
    private const VirtualKeyModifiers Ctrl = VirtualKeyModifiers.Control;
    private const VirtualKeyModifiers Shift = VirtualKeyModifiers.Shift;
    private const VirtualKeyModifiers NoModifiers = VirtualKeyModifiers.None;

    private readonly Dictionary<(VirtualKey, VirtualKeyModifiers), Action> _pageShortcuts = [];

    private void SetupPageShortcuts()
    {
        void Page(VirtualKey key, VirtualKeyModifiers modifiers, Action action) => _pageShortcuts[(key, modifiers)] = action;
        void OnTab(Action<BrowserTab> action) { if (_shell.CurrentTab is { } tab) action(tab); }

        Page(VirtualKey.H, Ctrl, ToggleHistory);
        Page(VirtualKey.J, Ctrl, ToggleDownloads);
        Page(VirtualKey.S, Ctrl, ToggleDock);
        Page(VirtualKey.D, Ctrl, () => OnTab(_shell.ToggleFavorite));
        Page(VirtualKey.P, Ctrl, () => OnTab(t => t.Print()));
        Page(VirtualKey.U, Ctrl, ViewSourceOfCurrent);
        Page(VirtualKey.K, Ctrl, () => OpenPalette(PaletteMode.Navigate, search: true));
        Page(VirtualKey.E, Ctrl, () => OpenPalette(PaletteMode.Navigate, search: true));
        Page(VirtualKey.F, Ctrl, OpenFindBar);
        Page(VirtualKey.G, Ctrl, () => FindNextOrOpen(forward: true));
        Page(VirtualKey.G, Ctrl | Shift, () => FindNextOrOpen(forward: false));
        Page(VirtualKey.F3, NoModifiers, () => FindNextOrOpen(forward: true));
        Page(VirtualKey.F3, Shift, () => FindNextOrOpen(forward: false));
        Page(VirtualKey.R, Ctrl, () => OnTab(t => t.Reload()));
        Page(VirtualKey.F5, NoModifiers, () => OnTab(t => t.Reload()));
        Page(VirtualKey.R, Ctrl | Shift, () => OnTab(t => t.Reload(ignoreCache: true)));
        Page(VirtualKey.F5, Ctrl, () => OnTab(t => t.Reload(ignoreCache: true)));
        Page(VirtualKey.F5, Shift, () => OnTab(t => t.Reload(ignoreCache: true)));
        // Zoom: + and - on the main keyboard (no name in VirtualKey) and on the keypad.
        Page((VirtualKey)187, Ctrl, () => OnTab(t => t.ZoomIn()));
        Page(VirtualKey.Add, Ctrl, () => OnTab(t => t.ZoomIn()));
        Page((VirtualKey)189, Ctrl, () => OnTab(t => t.ZoomOut()));
        Page(VirtualKey.Subtract, Ctrl, () => OnTab(t => t.ZoomOut()));
        Page(VirtualKey.Number0, Ctrl, () => OnTab(t => t.ResetZoom()));
        Page(VirtualKey.NumberPad0, Ctrl, () => OnTab(t => t.ResetZoom()));

        // Hard reload had no accelerator: add it for when the focus is outside the page.
        foreach (var (key, modifiers) in new[] { (VirtualKey.R, Ctrl | Shift), (VirtualKey.F5, Ctrl), (VirtualKey.F5, Shift) })
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                OnTab(t => t.Reload(ignoreCache: true));
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }

        // Esc goes to the page first too: if the page does not take it, the app closes what it has open or, with
        // nothing open, stops the page from loading (PageEscape).
        Page(VirtualKey.Escape, NoModifiers, () => PageEscape(stopLoading: true));

        // Everything else the app has an accelerator for is reserved. Esc is not: it reaches the page unless the app
        // has something open to close (see CloseWithEscape).
        var reserved = Root.KeyboardAccelerators
            .Select(a => (a.Key, a.Modifiers))
            .Where(k => k.Key != VirtualKey.Escape && !_pageShortcuts.ContainsKey(k))
            .ToList();

        foreach (var view in new[] { WebView, PeekView, DevToolsView })
        {
            view.ReservedShortcuts.UnionWith(reserved);
            view.PageShortcuts.UnionWith(_pageShortcuts.Keys);
            view.PageShortcutNotHandled += (sender, shortcut) =>
            {
                // Only a page in the main view is stopped by Esc: not Peek's or DevTools'.
                if (shortcut == (VirtualKey.Escape, NoModifiers)) PageEscape(stopLoading: ReferenceEquals(sender, WebView));
                else if (_pageShortcuts.TryGetValue(shortcut, out var action)) action();
            };
        }
    }
}
