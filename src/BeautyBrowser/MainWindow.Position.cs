using BeautyBrowser.Controls;
using BeautyKit;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace BeautyBrowser;

/// <summary>
/// Where the tabs live (Settings → Sidebar and tabs): the sidebar on the left (the original layout), the same sidebar on
/// the right edge, or a Chrome-like tab strip and toolbar on top.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Tab strip (40) plus toolbar (46).</summary>
    private const double TopBarHeight = 86;

    private string _position = "left";
    private bool SidebarRight => _position == "right";
    private bool TabsOnTop => _position == "top";

    /// <summary>Where the floating sidebar waits: just outside its edge.</summary>
    private float SidebarHiddenX => SidebarRight ? -SidebarHiddenOffset : SidebarHiddenOffset;

    private void InitPosition()
    {
        TopBar.Attach(_shell);
        TopBar.PaletteRequested += (_, mode) => OpenPalette(mode);
        TopBar.LibraryRequested += (_, section) => OpenLibrary(section);
        TopBar.SettingsRequested += (_, _) => OpenSettings();
        TopBar.NewSpaceRequested += (_, _) =>
        {
            OpenLibrary(LibrarySection.Spaces);
            Library.StartNewSpace();
        };
        // The strip's free part drags the window; it moves as tabs open and close.
        TopBar.DragArea.SizeChanged += (_, _) => UpdateDragZone();
        // Tabs on top: the address bar is the search box and the results drop below it.
        TopBar.AddressFocused += (_, _) =>
        {
            var mode = _topAddressMode;
            _topAddressMode = PaletteMode.Navigate;
            HidePreview();
            CloseLibrary(returnToSidebar: false);
            Palette.OpenAttached(_shell, mode, TopBar.AddressInput, TopBar.AddressBounds(Root));
        };
        TopBar.AddressBlurred += (_, _) => { if (Palette.IsAttached) Palette.Close(); };
        _position = _shell.Settings.SidebarPosition;
        ApplyPosition(animate: false);
    }

    /// <summary>Settings changed: switch layouts if the position is a new one.</summary>
    private void RefreshPosition()
    {
        var position = _shell.Settings.SidebarPosition;
        if (position == _position) return;
        _position = position;
        ApplyPosition(animate: true);
    }

    private void ApplyPosition(bool animate)
    {
        HidePreview();
        var top = TabsOnTop;

        Sidebar.Visibility = top ? Visibility.Collapsed : Visibility.Visible;
        Sidebar.HorizontalAlignment = SidebarRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Sidebar.IsOnRight = SidebarRight; // its content starts below the caption buttons
        TopBar.Visibility = top && !_fullscreen ? Visibility.Visible : Visibility.Collapsed;

        // Tabs on top keep the caption buttons visible, at the strip's right end; the page is not dragged from.
        WebView.WindowDragZone = top ? 0 : PageDragZone;
        if (top)
        {
            if (_captionShown) { _captionShown = false; AnimatePageTopClip(0); }
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            if (_sidebarShown) { _sidebarShown = false; Sidebar.Opacity = 0; Sidebar.IsHitTestVisible = false; }
            _sidebarDocked = false;
        }
        else
        {
            AppWindow.TitleBar.PreferredHeightOption = _captionShown ? TitleBarHeightOption.Standard : TitleBarHeightOption.Collapsed;
            // Back to the hidden spot on the new side (no slide across the window).
            var transition = Sidebar.TranslationTransition;
            Sidebar.TranslationTransition = null;
            Sidebar.Translation = new System.Numerics.Vector3(_sidebarShown ? 0 : SidebarHiddenX, 0, SidebarDepth);
            Sidebar.TranslationTransition = transition;
            _narrow = Root.ActualWidth is > 0 and < DockMinWidth;
            if (DockWanted != _sidebarDocked || (DockWanted && !_sidebarShown)) SetDocked(DockWanted, animate);
        }

        ApplyPageCorners(); // square with tabs on top
        UpdateDragZone();
        ApplyLayout(animateWidth: animate);
        LayoutLoadBar();
    }

    private PaletteMode _topAddressMode = PaletteMode.Navigate;

    /// <summary>
    /// With tabs on top the search goes in the address bar: Ctrl+T leaves it empty (Enter opens a new tab), Ctrl+L
    /// selects the address. False when the bar is not there (another layout, full screen).
    /// </summary>
    private bool TryOpenTopAddress(PaletteMode mode, string initial)
    {
        if (!TabsOnTop || TopBar.Visibility != Visibility.Visible) return false;
        _topAddressMode = mode;
        TopBar.FocusAddress(initial.Length > 0 ? AddressDisplay.ToDisplay(initial) : "");
        return true;
    }

    /// <summary>With tabs on top, only the strip's free part drags the window (the tabs must stay clickable).</summary>
    private void PlaceTopDragZone()
    {
        if (TopBar.DragArea.ActualWidth < 1 || TopBar.Visibility != Visibility.Visible) return;
        var origin = TopBar.DragArea.TransformToVisual(Root).TransformPoint(new Point(0, 0));
        var margin = new Thickness(origin.X, 0, 0, 0);
        if (AppTitleBar.Margin == margin && Math.Abs(AppTitleBar.Width - TopBar.DragArea.ActualWidth) < 0.5) return;
        AppTitleBar.HorizontalAlignment = HorizontalAlignment.Left;
        AppTitleBar.Margin = margin;
        AppTitleBar.Width = TopBar.DragArea.ActualWidth;
        AppTitleBar.Height = origin.Y + TopBar.DragArea.ActualHeight;
    }

    /// <summary>The page's top edge: under the toolbar (with the glass frame between) with tabs on top, at the frame otherwise.</summary>
    private double PageTop => TabsOnTop ? TopBarHeight : Edge;

    /// <summary>The page's right edge: after a sidebar pinned on the right, at the frame otherwise.</summary>
    private double PageRight => _sidebarDocked && SidebarRight ? SidebarWidth + Edge : Edge;

    /// <summary>A point as if the sidebar were on the left: the edge logic is written once.</summary>
    private Point FromSidebarEdge(Point p) => SidebarRight ? new Point(Root.ActualWidth - p.X, p.Y) : p;
}
