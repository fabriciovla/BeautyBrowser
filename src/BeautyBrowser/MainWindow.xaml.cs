using System.Numerics;
using System.Runtime.InteropServices;
using BeautyBrowser.Controls;
using BeautyBrowser.Shell;
using BeautyKit;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace BeautyBrowser;

public sealed partial class MainWindow : Window
{
    // Layout sizes (see Theme/Beauty.xaml).
    private const double Frame = 8;
    private const double SidebarWidth = 264;
    private const float SidebarHiddenOffset = -(264 + 32);
    private const double LibraryListEdge = 112 + 320; // rail + list; the page is to the right
    private const float LibraryHiddenOffset = -(float)LibraryListEdge; // hidden just outside the window
    private static readonly TimeSpan LibrarySlide = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan SidebarSlide = TimeSpan.FromMilliseconds(170);
    private readonly ScalarTransition _sidebarFade = new() { Duration = TimeSpan.FromMilliseconds(140) };
    private const float ShadowDepth = 32;

    // The sidebar and caption buttons open when the mouse moves toward an edge and crosses a line slightly inside the
    // window (EdgeMargin), or leaves through that edge. Coming in from outside opens nothing.
    private const double EdgeMargin = 6;        // the trigger line, inside the glass frame
    private const double OutsideZone = 48;      // margin outside the top/bottom (sidebar) or the sides (caption buttons)
    private const double StripHeight = 31;      // glass shown on top when the caption buttons appear
    private const double DragZoneHeight = Frame; // the top frame; the page drags separately (PageDragZone)
    private const double PageDragZone = 56;     // draggable strip at the top of the page, as tall as YouTube's bar
    private const int LeaveDelayMs = 350;       // after moving away
    private const int IdleHideMs = 3000;        // mouse at rest: they hide themselves

    private const double DevToolsMinWidth = 320;
    private const double PageMinWidth = 360;

    private const int PreviewDelayMs = 450;      // mouse resting on a tab
    private const int PreviewHideMs = 200;       // on leave, giving time to reach the card
    private const double PreviewGap = 10;        // between the sidebar and the card
    private const float PreviewHiddenOffset = -8;

    private readonly DispatcherQueueTimer _previewShowTimer;
    private readonly DispatcherQueueTimer _previewHideTimer;
    private BrowserTab? _previewTab;
    private FrameworkElement? _previewRow;
    private bool _previewShown;
    private bool _previewHovered;

    private readonly nint _hwnd;
    private readonly BrowserEngine _engine;
    private readonly BrowserShell _shell;
    private readonly DispatcherQueueTimer _hoverTimer;
    private readonly DispatcherQueueTimer _floatingLibraryTimer;

    private bool _sidebarShown;
    private bool _sidebarDocked;
    private bool _libraryShown;
    private bool _captionShown;
    private bool _fullscreen;
    private bool _userFullscreen; // F11, as opposed to the page asking for it (a video)
    private double _devToolsWidth = 560;

    private Point _lastCursor;
    private long _lastMoveAt;
    private long _sidebarHideAt;
    private long _captionHideAt;
    private bool _sidebarArmed = true;  // after an idle hide, wait for the mouse to move
    private bool _captionArmed = true;

    public MainWindow()
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // What Chromium needs before starting (hardware acceleration, page languages). It runs before
        // InitializeComponent so BrowserView already knows BrowserTab.UseGpu when it is created.
        var database = new Services.DatabaseService();
        var startupSettings = new Services.SettingsService(database);
        _engine = BrowserEngine.Initialize(_hwnd, DispatcherQueue,
            hardwareAcceleration: startupSettings.HardwareAcceleration,
            pageLanguages: startupSettings.PageLanguages);

        InitializeComponent();
        SetupWindowChrome();
        SetupOverlays();

        _shell = new BrowserShell(_engine, DispatcherQueue, database);
        _updates = _shell.Updates;
        Title = "Beauty Browser";

        Sidebar.Attach(_shell);
        Sidebar.TabHovered += (_, hover) => OnTabHovered(hover.Tab, hover.Row);
        Preview.Attach(_shell);
        Preview.ActionInvoked += (_, _) => HidePreview();
        Sidebar.PaletteRequested += (_, mode) => OpenPalette(mode);
        Sidebar.DockToggled += (_, _) => _shell.Settings.SidebarDocked = !_sidebarDocked; // saved; SettingsChanged applies it
        Sidebar.LibraryRequested += (_, section) => OpenLibrary(section);
        Sidebar.NewSpaceRequested += (_, _) =>
        {
            OpenLibrary(LibrarySection.Spaces);
            Library.StartNewSpace(); // the new space form on top, existing spaces below
        };
        AddZoomAccelerators();
        SetupPageShortcuts();
        SetupPermissions();
        SetupNotices();
        SetupUpdates();
        // Links other apps send while BeautyBrowser is open: a new tab, with the window brought to the front.
        ExternalLinks.Listen(url => DispatcherQueue.TryEnqueue(() =>
        {
            if (_fullscreen) _shell.CurrentTab?.ExitFullscreen();
            _shell.NewTab(url);
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } window) window.Restore();
            Activate();
        }));
        SetupPeek();
        Switcher.Attach(_shell);
        Switcher.CommitRequested += (_, _) => CommitSwitcher();
        Switcher.CancelRequested += (_, _) => CancelSwitcher();
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated && Switcher.IsOpen) CancelSwitcher(); };
        Library.Attach(_shell);
        Library.CloseRequested += (_, _) => CloseLibrary(returnToSidebar: true);
        Palette.Closed += (_, _) => WebView.Focus(FocusState.Programmatic);
        DevToolsSplitter.Dragged += (_, dx) => ResizeDevTools(_devToolsWidth - dx);

        _shell.CurrentTabChanged += (_, _) =>
        {
            if (_findTab is not null && _findTab != _shell.CurrentTab) CloseFindBar(focusPage: false); // another tab
            if (PeekOpen) ClosePeek(); // the tab changed: Peek closes
            WebView.Tab = _shell.CurrentTab;
            RefreshDevTools();
            WatchLoading(_shell.CurrentTab);
            // Start page: focus goes to the page so its search box can be typed into right away.
            if (StartPage.Is(_shell.CurrentTab?.Url)) WebView.Focus(FocusState.Programmatic);
        };
        PageArea.SizeChanged += (_, _) => LayoutLoadBar();

        // Full screen: the page settles once the window has changed size (see SetFullscreen).
        _fullscreenLayoutTimer = DispatcherQueue.CreateTimer();
        _fullscreenLayoutTimer.IsRepeating = false;
        _fullscreenLayoutTimer.Interval = TimeSpan.FromMilliseconds(300);
        _fullscreenLayoutTimer.Tick += (_, _) => ApplyFullscreenLayout();
        AppWindow.Changed += (_, change) => { if (change.DidSizeChange) ApplyFullscreenLayout(); };
        // Maximized: the page reaches the screen edges, without the glass frame.
        AppWindow.Changed += (_, change) => { if (change.DidPresenterChange || change.DidSizeChange) UpdateMaximized(); };

        // Drag the window from the top of the page (a strip as tall as YouTube's bar) without opening the caption
        // buttons, only from empty spots so links, buttons and text keep working.
        WebView.WindowDragZone = PageDragZone;
        // The window is moved here, following the cursor while the button is down: Windows' own drag
        // (WM_SYSCOMMAND/SC_MOVE) does not see the button held through WinUI's pointer and kept following the mouse
        // after release.
        WebView.WindowDragMoved += (_, starting) => MoveWindowWithCursor(starting);
        WebView.WindowMaximizeToggleRequested += (_, _) =>
        {
            if (AppWindow.Presenter is OverlappedPresenter window)
            {
                if (window.State == OverlappedPresenterState.Maximized) window.Restore();
                else window.Maximize();
            }
        };
        _shell.DevToolsChanged += (_, _) => RefreshDevTools();
        _shell.FullscreenChanged += (_, fullscreen) =>
        {
            if (!_userFullscreen) SetFullscreen(fullscreen); // with F11 the window stays full screen when a video leaves it
        };
        _shell.DownloadStarted += (_, _) => PlayDownloadAnimation();

        // The mouse position is checked ~40 times per second, also over the page and outside the window.
        _hoverTimer = DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(25);
        _hoverTimer.Tick += (_, _) => OnHoverTick();
        _hoverTimer.Start();

        _floatingLibraryTimer = DispatcherQueue.CreateTimer();
        _floatingLibraryTimer.IsRepeating = false;
        _floatingLibraryTimer.Interval = TimeSpan.FromSeconds(2.5);
        _floatingLibraryTimer.Tick += (_, _) => HideFloatingLibrary();

        _previewShowTimer = DispatcherQueue.CreateTimer();
        _previewShowTimer.IsRepeating = false;
        _previewShowTimer.Interval = TimeSpan.FromMilliseconds(PreviewDelayMs);
        _previewShowTimer.Tick += (_, _) => ShowPreview();
        _previewHideTimer = DispatcherQueue.CreateTimer();
        _previewHideTimer.IsRepeating = false;
        _previewHideTimer.Interval = TimeSpan.FromMilliseconds(PreviewHideMs);
        _previewHideTimer.Tick += (_, _) => { if (!_previewHovered) HidePreview(); };

        // Glass texture and sidebar options from settings, applied live when they change.
        ReadShellSettings();
        _shell.Settings.SettingsChanged += (_, _) =>
        {
            ReadShellSettings();
            if (_shell.Settings.SidebarDocked != _sidebarDocked) SetDocked(_shell.Settings.SidebarDocked);
            if (!_tabPreviews) HidePreview();
            if (!_showLoadBar) LoadBar.Opacity = 0;
        };

        Root.Loaded += (_, _) =>
        {
            // On startup: continue where the user left off (if chosen and saved) or the start page.
            var restored = _shell.Settings.Startup == Services.StartupMode.RestoreTabs && _shell.TryRestoreSession();
            // Opened with a link from another app (as the default browser): that link in a new tab.
            var link = App.PendingLink;
            App.PendingLink = null;
            if (link is not null) _shell.NewTab(link);
            else if (!restored && _shell.CurrentTab is null) _shell.NewTab();
            // First run (or --welcome): onboarding on top of everything.
            if (!_shell.Settings.OnboardingDone || Environment.GetCommandLineArgs().Contains("--welcome")) ShowOnboarding();
            if (_shell.Settings.SidebarDocked) SetDocked(true, animate: false);
            UpdateMaximized();
            ApplyLayout();
        };
        Closed += (_, _) => Shutdown();
    }

    // Settings read often (the mouse is checked 40 times per second), cached once per change.
    private long _sidebarIdleMs = IdleHideMs;
    private bool _tabPreviews = true;
    private bool _showLoadBar = true;

    private void ReadShellSettings()
    {
        var s = _shell.Settings;
        _sidebarIdleMs = s.SidebarIdleSeconds * 1000L;
        _tabPreviews = s.TabPreviews;
        _showLoadBar = s.ShowLoadBar;
        Controls.NoiseLayer.Configure(s.GlassTexture, s.GlassGrain / 100.0, s.GlassSoftness / 100.0);
        AppTheme.Apply(s.ThemeKey);
        ThemeTint.Background = new SolidColorBrush(AppTheme.WindowTint);
    }

    /// <summary>Windows glass, without a title bar or system buttons.</summary>
    private void SetupWindowChrome()
    {
        SystemBackdrop = MicaController.IsSupported()
            ? new MicaBackdrop { Kind = MicaKind.BaseAlt }
            : new DesktopAcrylicBackdrop();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Caption buttons (minimize, maximize, close) with a transparent background, drawn over the page. They start
        // hidden and appear when the mouse nears the top (SetCaptionShown).
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;

        AppWindow.Resize(new SizeInt32(1400, 900));
        var icon = Path.Combine(AppContext.BaseDirectory, "BeautyBrowser.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon); // taskbar and Alt+Tab
    }

    /// <summary>
    /// The sidebar moves with composition transitions: the GPU slides what is already drawn, so text and icons stay
    /// sharp during the animation.
    /// </summary>
    private void SetupOverlays()
    {
        Sidebar.Translation = new Vector3(SidebarHiddenOffset, 0, ShadowDepth);
        Sidebar.Shadow = new ThemeShadow();
        Sidebar.OpacityTransition = _sidebarFade;
        Sidebar.TranslationTransition = new Vector3Transition { Duration = SidebarSlide };

        Preview.Translation = new Vector3(PreviewHiddenOffset, 0, ShadowDepth);
        Preview.Shadow = new ThemeShadow();
        FindBar.Shadow = new ThemeShadow();
        FindBar.Translation = new Vector3(0, 0, 24);
        Preview.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) };
        Preview.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };

        // Library and page move together: same transition, same duration and (without a pinned sidebar) the same
        // distance. The library comes in from outside the window pushing the page, with no fades, and its shadow
        // (Z depth) grows on the way. The page never changes width, so it never relayouts.
        Library.Shadow = new ThemeShadow();
        Library.Translation = Vector3.Zero;
        Library.TranslationTransition = new Vector3Transition { Duration = LibrarySlide };
        SlideX(LibraryHost, LibraryHiddenOffset, animate: false);

        // Page clip on the GPU: when the caption buttons appear the top of the page is hidden to show the window
        // glass. The page neither moves nor shrinks.
        var pageVisual = ElementCompositionPreview.GetElementVisual(PageArea);
        var radius = new Vector2((float)ContentRadius);
        _pageClip = pageVisual.Compositor.CreateRectangleClip(0, 0, 10_000, 10_000, radius, radius, radius, radius);
        pageVisual.Clip = _pageClip;
        PageArea.SizeChanged += (_, e) =>
        {
            _pageClip.Right = (float)e.NewSize.Width;
            _pageClip.Bottom = (float)e.NewSize.Height;
        };
    }

    private const double ContentRadius = 8; // BeautyContentCornerRadius
    private RectangleClip _pageClip = null!;

    /// <summary>Animates how much of the top of the page is hidden (0 = none).</summary>
    private void AnimatePageTopClip(double top)
    {
        var compositor = _pageClip.Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, (float)top, compositor.CreateCubicBezierEasingFunction(new(0.2f, 0.8f), new(0.2f, 1f)));
        animation.Duration = TimeSpan.FromMilliseconds(180);
        _pageClip.StartAnimation(nameof(RectangleClip.Top), animation);
    }

    private void Shutdown()
    {
        _hoverTimer.Stop();
        WebView.Tab = null;
        DevToolsView.Tab = null;
        _shell.Shutdown();
        _engine.Dispose();

        // A downloaded update installs now that Chromium is done with the files; on a restart the installer opens the
        // browser again when it finishes.
        if (_updates.ApplyOnExit(relaunch: App.RestartRequested)) return;

        // A restart was requested: only now, with Chromium done with the profile, the app starts again.
        if (App.RestartRequested && Environment.ProcessPath is { } exe)
        {
            App.ReleaseSingleInstance(); // otherwise the new instance would see one running and exit
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        }
    }


    private double _pageLeft = Frame;
    private bool _maximized;

    /// <summary>The glass frame around the page; none while maximized.</summary>
    private double Edge => _maximized ? 0 : Frame;

    /// <summary>Where the page starts: at the frame or after the pinned sidebar.</summary>
    private double TargetPageLeft() => _sidebarDocked ? SidebarWidth + Edge : Edge;

    /// <summary>
    /// Maximized, the page reaches the screen edges with square corners, like a full screen app. Restored, the glass
    /// frame and rounded corners come back.
    /// </summary>
    private void UpdateMaximized()
    {
        var maximized = !_fullscreen && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        if (maximized == _maximized) return;
        _maximized = maximized;
        ApplyPageCorners();
        ApplyLayout();
        if (_captionShown) AnimatePageTopClip(StripHeight - Edge);
        UpdateDragZone();
        LayoutLoadBar();
    }

    /// <summary>Page corners: rounded with the frame, square when maximized or in full screen.</summary>
    private void ApplyPageCorners()
    {
        var square = _fullscreen || _maximized;
        var radius = new Vector2(square ? 0 : (float)ContentRadius);
        _pageClip.TopLeftRadius = _pageClip.TopRightRadius = _pageClip.BottomLeftRadius = _pageClip.BottomRightRadius = radius;
        WebView.CornerRadius = square ? new CornerRadius(0) : (CornerRadius)Application.Current.Resources["BeautyContentCornerRadius"];
    }

    /// <summary>
    /// Lays out the page. With the library open the page does not shrink: it slides right as a whole (out past the
    /// window edge) and comes back when it closes, so the site never relayouts and the GPU does the animation. Only
    /// pinning the sidebar changes the width, once.
    /// </summary>
    private void ApplyLayout(bool animateWidth = false)
    {
        if (_fullscreen) return;

        var left = TargetPageLeft();
        if (animateWidth && !_libraryShown && Math.Abs(left - _pageLeft) > 0.5)
        {
            AnimatePageLeft(left);
        }
        else
        {
            StopPageLeftAnimation();
            SetPageLeft(left);
        }

        var shift = _libraryShown ? (float)(LibraryListEdge + Edge - left) : 0;
        if (Math.Abs(shift - _pageShift) < 0.5) return;

        // The final position is set right away, so the mouse targets the page correctly from the start, and the GPU
        // brings it there from where it was shown, with the library's curve and duration.
        var from = _pageShift - shift;
        _pageShift = shift;
        PageSlider.Translation = new Vector3(shift, 0, 0);
        LayoutLoadBar();
        SlideX(PageArea, 0, from: from);
        SlideX(LoadBar, 0, from: from);
    }

    private float _pageShift;
    private CompositionEasingFunction? _slideEase;
    private int _pageLeftAnimation; // the current animation; a new one invalidates the previous one's ending
    private bool _pageLeftAnimating;

    /// <summary>
    /// Pin or unpin the sidebar. Resizing the page every frame leaves empty edges (Chromium takes time to render each
    /// size), so the page slides on the GPU and resizes once, when the part still to be drawn is outside the window.
    /// Shrinking: it slides with its old size (the excess leaves through the right edge) and shrinks on arrival.
    /// Growing: it takes the new size first and slides from where it was, so the part Chromium has not drawn yet
    /// starts outside the window.
    /// </summary>
    private void AnimatePageLeft(double left)
    {
        var id = ++_pageLeftAnimation;
        var delta = (float)(left - _pageLeft);
        var compositor = ElementCompositionPreview.GetElementVisual(PageArea).Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _pageLeftAnimating = true;

        if (delta > 0)
        {
            SlideX(PageArea, delta, from: 0);
            SlideX(LoadBar, delta, from: 0);
        }
        else
        {
            SetPageLeft(left);
            SlideX(PageArea, 0, from: -delta);
            SlideX(LoadBar, 0, from: -delta);
        }

        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (id != _pageLeftAnimation) return;
            _pageLeftAnimating = false;
            if (delta <= 0) return;
            SetPageLeft(left); // in place now, so apply the new size
            SlideX(PageArea, 0, animate: false);
            SlideX(LoadBar, 0, animate: false);
        });
    }

    private void StopPageLeftAnimation()
    {
        _pageLeftAnimation++;
        if (!_pageLeftAnimating) return;
        _pageLeftAnimating = false;
        SlideX(PageArea, 0, animate: false);
        SlideX(LoadBar, 0, animate: false);
    }

    /// <summary>Slides an element in X on the composition thread with an ease-out curve.</summary>
    private void SlideX(UIElement element, float to, float? from = null, bool animate = true)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        if (!animate)
        {
            visual.StopAnimation("Translation.X");
            visual.Properties.InsertVector3("Translation", new Vector3(to, 0, 0));
            return;
        }
        _slideEase ??= compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.9f), new Vector2(0.3f, 1f));
        var slide = compositor.CreateScalarKeyFrameAnimation();
        if (from is { } start) slide.InsertKeyFrame(0, start);
        slide.InsertKeyFrame(1, to, _slideEase);
        slide.Duration = LibrarySlide;
        visual.StartAnimation("Translation.X", slide);
    }

    private void SetPageLeft(double left)
    {
        var margin = new Thickness(left, Edge, Edge, Edge);
        if (Math.Abs(left - _pageLeft) < 0.5 && PageArea.Margin == margin) return;
        _pageLeft = left;
        PageArea.Margin = margin;
        LayoutLoadBar();
    }


    private BrowserTab? _loadingTab;
    private double _loadValue;   // 0 to 1, what the bar shows
    private bool _loadVisible;

    private void WatchLoading(BrowserTab? tab)
    {
        if (_loadingTab == tab) return;
        if (_loadingTab is not null) _loadingTab.PropertyChanged -= LoadingTab_PropertyChanged;
        _loadingTab = tab;
        if (tab is not null) tab.PropertyChanged += LoadingTab_PropertyChanged;

        // Another tab: the bar starts from its state.
        Motion.Cancel("load-bar");
        Motion.Cancel("load-bar-fade");
        _loadVisible = false;
        _loadValue = 0;
        LoadBar.Opacity = 0;
        LayoutLoadBar();
        UpdateLoadBar();
    }

    private void LoadingTab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserTab.IsLoading) or nameof(BrowserTab.LoadProgress)) UpdateLoadBar();
    }

    private void UpdateLoadBar()
    {
        var tab = _loadingTab;
        if (tab is null || _fullscreen || !_showLoadBar) return;

        if (tab.IsLoading)
        {
            Motion.Cancel("load-bar-fade");
            if (!_loadVisible || _loadValue >= 1)
            {
                _loadVisible = true;
                _loadValue = 0;
            }
            LoadBar.Opacity = 1;
            AnimateLoadTo(Math.Max(0.08, tab.LoadProgress)); // a little shows as soon as it starts
        }
        else if (_loadVisible)
        {
            // Done: it completes and fades out.
            AnimateLoadTo(1, () => Motion.Animate("load-bar-fade", TimeSpan.FromMilliseconds(260),
                t => LoadBar.Opacity = 1 - t,
                completed: () =>
                {
                    _loadVisible = false;
                    _loadValue = 0;
                    LayoutLoadBar();
                }));
        }
    }

    private void AnimateLoadTo(double target, Action? completed = null)
    {
        var from = _loadValue;
        target = Math.Max(target, from); // within one load the bar never goes back
        Motion.Animate("load-bar", TimeSpan.FromMilliseconds(220), t =>
        {
            _loadValue = Motion.Lerp(from, target, t);
            LayoutLoadBar();
        }, completed: completed);
    }

    /// <summary>Length of the full load bar relative to the page width. It grows from the center.</summary>
    private const double LoadBarTrack = 0.5;

    private void LayoutLoadBar()
    {
        var track = Math.Max(0, PageArea.ActualWidth * LoadBarTrack);
        var width = track * _loadValue;
        // On the top frame; when maximized (no frame), over the top edge of the page.
        LoadBar.Margin = new Thickness(_pageLeft + _pageShift + (PageArea.ActualWidth - width) / 2, _maximized ? 0 : 2.5, 0, 0);
        LoadBar.Width = width;
    }

    /// <summary>
    /// Caption buttons appear over the window glass. The page does not move: its top is hidden so the glass shows
    /// behind the buttons, and while they are visible that whole strip drags the window.
    /// </summary>
    private void SetCaptionShown(bool shown)
    {
        if (_captionShown == shown) return;
        _captionShown = shown;
        _captionHideAt = 0;
        AppWindow.TitleBar.PreferredHeightOption = shown ? TitleBarHeightOption.Standard : TitleBarHeightOption.Collapsed;
        AnimatePageTopClip(shown ? StripHeight - Edge : 0);
        UpdateDragZone();
    }

    /// <summary>
    /// The drag zone: the frame plus the top edge of the page (or the whole strip while it is visible). It stays clear
    /// of the sidebar so its buttons keep responding.
    /// </summary>
    private void UpdateDragZone()
    {
        // Maximized there is no top frame, so the strip does not cover the page (it drags from the page, PageDragZone).
        AppTitleBar.Height = _captionShown ? StripHeight : _maximized ? 0 : DragZoneHeight;
        var left = _libraryShown ? LibraryListEdge
            : _sidebarShown || _sidebarDocked ? SidebarWidth
            : 0;
        AppTitleBar.Margin = new Thickness(left, 0, 0, 0);
    }


    /// <summary>
    /// The library comes in from the left, where the sidebar is (same glass, same edge). It closes with the arrow,
    /// Esc, opening a history page or a click on the page.
    /// </summary>
    private void OpenLibrary(LibrarySection section)
    {
        HidePreview();
        if (_fullscreen) _shell.CurrentTab?.ExitFullscreen();
        Library.Show(section);
        if (_libraryShown) return;

        _libraryShown = true;
        Library.IsHitTestVisible = true;
        Library.Translation = new Vector3(0, 0, ShadowDepth); // the shadow grows as it comes in
        SlideX(LibraryHost, 0);
        if (!_sidebarDocked) SetSidebarShown(false);
        UpdateDragZone();
        ApplyLayout();
    }

    private void CloseLibrary(bool returnToSidebar)
    {
        if (!_libraryShown) return;

        _libraryShown = false;
        Library.IsHitTestVisible = false;
        Library.Translation = Vector3.Zero;
        SlideX(LibraryHost, LibraryHiddenOffset);
        if (returnToSidebar && !_sidebarDocked)
        {
            _sidebarArmed = true;
            SetSidebarShown(true); // back to the tabs; it hides if the mouse moves away
        }
        UpdateDragZone();
        ApplyLayout(); // the page slides back into place
        WebView.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// The sidebar's height over the page: floating it casts a shadow; pinned it rests (no shadow) to show it became
    /// part of the window. The Translation transition animates this depth too.
    /// </summary>
    private float SidebarDepth => _sidebarDocked ? 0 : ShadowDepth;

    /// <summary>The floating sidebar slides in from the left, over the page.</summary>
    private void SetSidebarShown(bool shown)
    {
        if (_sidebarShown == shown) return;
        _sidebarShown = shown;
        _sidebarHideAt = 0;
        if (!shown && !_sidebarDocked) HidePreview();
        Sidebar.IsHitTestVisible = shown;
        // It appears whole at once on open (no fade, so it never "arrives late") and fades on close.
        Sidebar.OpacityTransition = shown ? null : _sidebarFade;
        Sidebar.Opacity = shown ? 1 : 0;
        Sidebar.Translation = new Vector3(shown ? 0 : SidebarHiddenOffset, 0, SidebarDepth);
        UpdateDragZone();
    }

    /// <summary>
    /// Pinned, the sidebar lands (its shadow goes away) and the page shifts right once. Unpinned it floats again and
    /// the page grows underneath.
    /// </summary>
    private void SetDocked(bool docked, bool animate = true)
    {
        if (_sidebarDocked == docked && _sidebarShown) return;
        _sidebarDocked = docked;
        Sidebar.IsDocked = docked;
        SetSidebarShown(true);
        Sidebar.Translation = new Vector3(0, 0, SidebarDepth);
        _sidebarArmed = true;
        _lastMoveAt = Environment.TickCount64; // just unpinned: do not hide right away for an idle mouse
        UpdateDragZone();
        ApplyLayout(animateWidth: animate);
    }


    /// <summary>Where the mouse is relative to the window, in DIPs.</summary>
    private readonly record struct CursorState(
        Point Point,          // negative when the mouse is outside, left or above
        bool OverWindow,      // over this window
        bool AtScreenLeft,    // touching the monitor's left edge (maximized window)
        bool AtScreenTop);    // touching the monitor's top edge

    private CursorState _previous;

    private void OnHoverTick()
    {
        if (_fullscreen || Root.XamlRoot is null) return;
        if (!TryGetCursor(out var cursor)) return;

        var point = cursor.Point;
        var now = Environment.TickCount64;
        if (Math.Abs(point.X - _lastCursor.X) > 3 || Math.Abs(point.Y - _lastCursor.Y) > 3)
        {
            _lastCursor = point;
            _lastMoveAt = now;
            _sidebarArmed = _captionArmed = true; // once it moves they can show again
        }
        var idle = now - _lastMoveAt > IdleHideMs;
        var sidebarIdle = _sidebarIdleMs > 0 && now - _lastMoveAt > _sidebarIdleMs; // 0 = never hide for an idle mouse
        var previous = _previous;
        _previous = cursor;

        UpdateSidebarHover(cursor, previous, sidebarIdle, now);
        UpdateCaptionHover(cursor, previous, idle, now);
    }

    /// <summary>
    /// Whether the mouse moved toward the left edge and crossed the trigger line (EdgeMargin, slightly inside the
    /// window) since the last reading. It is computed over the path between both readings, so it works even when the
    /// mouse moves very fast or jumps out of the window. Coming in from outside does not count.
    /// </summary>
    private bool ReachedLeftEdge(CursorState now, CursorState before)
    {
        if (!before.OverWindow) return false;
        if (now.AtScreenLeft && !before.AtScreenLeft) return true; // maximized: the monitor edge

        var (a, b) = (before.Point, now.Point);
        if (!(a.X >= EdgeMargin && b.X < EdgeMargin)) return false;
        var yAtLine = a.Y + (b.Y - a.Y) * ((a.X - EdgeMargin) / (a.X - b.X));
        return yAtLine >= 0 && yAtLine <= Root.ActualHeight;
    }

    /// <summary>Like <see cref="ReachedLeftEdge"/>, toward the top edge.</summary>
    private bool ReachedTopEdge(CursorState now, CursorState before)
    {
        if (!before.OverWindow) return false;
        if (now.AtScreenTop && !before.AtScreenTop) return true;

        var (a, b) = (before.Point, now.Point);
        if (!(a.Y >= EdgeMargin && b.Y < EdgeMargin)) return false;
        var xAtLine = a.X + (b.X - a.X) * ((a.Y - EdgeMargin) / (a.Y - b.Y));
        return xAtLine >= 0 && xAtLine <= Root.ActualWidth;
    }

    private void UpdateSidebarHover(CursorState cursor, CursorState previous, bool idle, long now)
    {
        if (_sidebarDocked || _libraryShown || _onboardingOpen) return;

        if (!_sidebarShown)
        {
            if (_sidebarArmed && !Palette.IsOpen && ReachedLeftEdge(cursor, previous)) SetSidebarShown(true);
            return;
        }

        // Open, it stays while the mouse is outside to the left or over the sidebar. It goes away when the mouse returns
        // to the page (past the sidebar), goes far up or down, or rests.
        var p = cursor.Point;
        var height = Root.ActualHeight;
        var away = p.X > SidebarWidth + 24 || p.Y < -OutsideZone || p.Y > height + OutsideZone;
        var overSidebar = cursor.OverWindow && p.X >= 0 && p.X <= SidebarWidth;
        if (Sidebar.IsBusy || _previewHovered || _previewShown || overSidebar)
        {
            _sidebarHideAt = 0; // with the mouse over it, it stays even when idle
        }
        else if (idle)
        {
            SetSidebarShown(false);
            _sidebarArmed = false;
        }
        else if (away)
        {
            if (_sidebarHideAt == 0) _sidebarHideAt = now + LeaveDelayMs;
            else if (now >= _sidebarHideAt) SetSidebarShown(false);
        }
        else
        {
            _sidebarHideAt = 0;
        }
    }

    private void UpdateCaptionHover(CursorState cursor, CursorState previous, bool idle, long now)
    {
        if (!_captionShown)
        {
            if (_captionArmed && ReachedTopEdge(cursor, previous)) SetCaptionShown(true);
            return;
        }

        // Open, it stays while the mouse is outside at the top or over the strip.
        var p = cursor.Point;
        var width = Root.ActualWidth;
        var onStrip = p.Y <= StripHeight && p.X >= 0 && p.X <= width;
        var away = p.Y > StripHeight + 24 || p.X < -OutsideZone || p.X > width + OutsideZone;
        if (idle && !onStrip)
        {
            SetCaptionShown(false);
            _captionArmed = false;
        }
        else if (away)
        {
            if (_captionHideAt == 0) _captionHideAt = now + LeaveDelayMs;
            else if (now >= _captionHideAt) SetCaptionShown(false);
        }
        else
        {
            _captionHideAt = 0;
        }
    }

    private bool TryGetCursor(out CursorState cursor)
    {
        cursor = default;
        if (!GetCursorPos(out var screen)) return false;

        var under = GetAncestor(WindowFromPoint(screen), GA_ROOT);
        var client = screen;
        ScreenToClient(_hwnd, ref client);

        var scale = Root.XamlRoot?.RasterizationScale ?? 1;
        var point = new Point(client.X / scale, client.Y / scale);
        var overWindow = under == _hwnd &&
                         point.X >= 0 && point.Y >= 0 && point.X <= Root.ActualWidth && point.Y <= Root.ActualHeight;

        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var hasMonitor = GetMonitorInfo(MonitorFromPoint(screen, MONITOR_DEFAULTTONEAREST), ref monitor);

        cursor = new CursorState(
            point, overWindow,
            hasMonitor && screen.X <= monitor.Monitor.Left + 1,
            hasMonitor && screen.Y <= monitor.Monitor.Top + 1);
        return true;
    }


    private void RefreshDevTools()
    {
        var session = _fullscreen ? null : _shell.CurrentDevTools;
        DevToolsView.Tab = session?.Frontend;

        var visible = session is not null ? Visibility.Visible : Visibility.Collapsed;
        DevToolsPanel.Visibility = visible;
        DevToolsSplitter.Visibility = visible;
        SplitterColumn.Width = new GridLength(session is not null ? DevToolsSplitter.Width : 0);
        if (session is not null) ResizeDevTools(_devToolsWidth);
        else DevToolsColumn.Width = new GridLength(0);
    }

    private void ResizeDevTools(double width)
    {
        var max = Math.Max(DevToolsMinWidth, PageArea.ActualWidth - PageMinWidth);
        _devToolsWidth = Math.Clamp(width, DevToolsMinWidth, max);
        DevToolsColumn.Width = new GridLength(_devToolsWidth);
    }

    private void CloseDevTools_Click(object sender, RoutedEventArgs e)
    {
        if (_shell.CurrentTab is { } tab) _shell.CloseDevTools(tab);
    }


    private void SetFullscreen(bool fullscreen)
    {
        if (_fullscreen == fullscreen) return;
        _fullscreen = fullscreen;

        // Like Chrome, the window changes first and the page settles once it has its new size. When the margins
        // changed earlier, Chromium rendered an intermediate size and videos grew in steps.
        _fullscreenLayoutPending = true;
        _fullscreenLayoutTimer.Stop();
        _fullscreenLayoutTimer.Start(); // in case the window never reports its new size
        AppWindow.SetPresenter(fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        if (fullscreen)
        {
            CloseLibrary(returnToSidebar: false);
            SetCaptionShown(false);
            SetSidebarShown(false);
            LoadBar.Opacity = 0;
        }
    }

    private bool _fullscreenLayoutPending;
    private DispatcherQueueTimer _fullscreenLayoutTimer = null!;

    /// <summary>The window entered or left full screen; now the page takes its place.</summary>
    private void ApplyFullscreenLayout()
    {
        if (!_fullscreenLayoutPending) return;
        _fullscreenLayoutPending = false;
        _fullscreenLayoutTimer.Stop();
        var fullscreen = _fullscreen;

        // No rounded corners in full screen, neither on the page nor its clip.
        UpdateMaximized();
        ApplyPageCorners();

        if (fullscreen)
        {
            PageArea.Margin = new Thickness(0);
            PageSlider.Translation = Vector3.Zero;
            _pageShift = 0;
            SlideX(PageArea, 0, animate: false);
        }
        else
        {
            if (_sidebarDocked) SetSidebarShown(true);
            ApplyLayout();
            UpdateLoadBar();
        }
        RefreshDevTools();
    }


    /// <summary>
    /// The mouse entered or left a sidebar tab. If it rests a moment the preview appears; if the preview was already
    /// open it moves to the new tab right away, as in Dia and Arc.
    /// </summary>
    private void OnTabHovered(BrowserTab? tab, FrameworkElement? row)
    {
        _previewShowTimer.Stop();
        if (!_tabPreviews) return;
        if (tab is null || row is null)
        {
            if (_previewShown) { _previewHideTimer.Stop(); _previewHideTimer.Start(); }
            return;
        }

        _previewHideTimer.Stop();
        _previewTab = tab;
        _previewRow = row;
        if (_previewShown) ShowPreview();
        else _previewShowTimer.Start();
    }

    private void ShowPreview()
    {
        if (_previewTab is not { } tab || _previewRow is not { } row) return;
        if (_fullscreen || _libraryShown || Palette.IsOpen || !(_sidebarShown || _sidebarDocked) || !row.IsLoaded) return;

        Preview.Show(tab);
        Preview.Measure(new Size(TabPreview.CardWidth, double.PositiveInfinity));
        var height = Preview.DesiredSize.Height;

        // Next to the sidebar, level with the tab (a bit higher, as in Dia), without leaving the window.
        var rowTop = row.TransformToVisual(Root).TransformPoint(new Point(0, 0)).Y;
        var top = rowTop + row.ActualHeight / 2 - height * 0.62;
        top = Math.Clamp(top, Frame, Math.Max(Frame, Root.ActualHeight - height - Frame));
        var left = Sidebar.ActualWidth + PreviewGap;

        if (_previewShown)
        {
            var from = Preview.Margin.Top;
            Motion.Animate("preview-move", TimeSpan.FromMilliseconds(160),
                t => Preview.Margin = new Thickness(left, Motion.Lerp(from, top, t), 0, 0));
        }
        else
        {
            Motion.Cancel("preview-move");
            Preview.Margin = new Thickness(left, top, 0, 0);
        }

        _previewShown = true;
        Preview.IsHitTestVisible = true;
        Preview.Opacity = 1;
        Preview.Translation = new Vector3(0, 0, ShadowDepth);
    }

    private void HidePreview()
    {
        _previewShowTimer.Stop();
        _previewHideTimer.Stop();
        _previewTab = null;
        _previewRow = null;
        _previewHovered = false;
        if (!_previewShown) return;
        _previewShown = false;
        Preview.IsHitTestVisible = false;
        Preview.Opacity = 0;
        Preview.Translation = new Vector3(PreviewHiddenOffset, 0, ShadowDepth);
    }

    private void Preview_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _previewHovered = true;
        _previewHideTimer.Stop();
    }

    private void Preview_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _previewHovered = false;
        _previewHideTimer.Stop();
        _previewHideTimer.Start();
    }


    /// <param name="search">Starts empty, ready to search (Ctrl+K, Ctrl+E), instead of showing the current address.</param>
    private void OpenPalette(PaletteMode mode, bool search = false)
    {
        HidePreview();
        if (_fullscreen) _shell.CurrentTab?.ExitFullscreen();
        CloseLibrary(returnToSidebar: false);
        if (!_sidebarDocked) SetSidebarShown(false);
        var current = _shell.CurrentTab?.Url;
        var initial = mode == PaletteMode.Navigate && !search && !StartPage.Is(current) ? current ?? "" : "";
        Palette.Open(_shell, mode, initial);
    }


    private void PlayDownloadAnimation()
    {
        if (_fullscreen) return;

        var start = TryGetCursor(out var cursor) && cursor.OverWindow
            ? cursor.Point
            : new Point(Root.ActualWidth / 2, Root.ActualHeight / 2);

        FrameworkElement target;
        Action arrive;
        if (_libraryShown)
        {
            target = Library.DownloadsTarget;
            arrive = Library.PlayDownloadsBounce;
        }
        else if (_sidebarShown || _sidebarDocked)
        {
            target = Sidebar.LibraryTarget;
            arrive = Sidebar.PlayLibraryBounce;
        }
        else
        {
            ShowFloatingLibrary();
            target = FloatingLibrary;
            arrive = BounceFloatingLibrary;
        }

        var end = target.TransformToVisual(Root).TransformPoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
        var control = new Point((start.X + end.X) / 2, Math.Min(start.Y, end.Y) - 140); // arc upward

        const double size = 38;
        var scale = new ScaleTransform();
        var dot = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = (Brush)Application.Current.Resources["BeautyAccentBrush"],
            BorderBrush = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(2.5),
            RenderTransform = scale,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Child = new FontIcon { Glyph = "", FontSize = 15, Foreground = new SolidColorBrush(Colors.White) },
            Shadow = new ThemeShadow(),
            Translation = new Vector3(0, 0, 16),
        };
        EffectsLayer.Children.Add(dot);

        Motion.Animate($"download-{Guid.NewGuid()}", TimeSpan.FromMilliseconds(850), t =>
        {
            var u = 1 - t;
            var x = u * u * start.X + 2 * u * t * control.X + t * t * end.X;
            var y = u * u * start.Y + 2 * u * t * control.Y + t * t * end.Y;
            Canvas.SetLeft(dot, x - size / 2);
            Canvas.SetTop(dot, y - size / 2);

            // It pops in, travels and shrinks on arrival.
            var s = t < 0.15 ? Motion.Lerp(0.4, 1, t / 0.15) : Motion.Lerp(1, 0.5, (t - 0.15) / 0.85);
            scale.ScaleX = scale.ScaleY = s;
            dot.Opacity = t > 0.88 ? (1 - t) / 0.12 : 1;
        }, Motion.EaseInOutCubic, () =>
        {
            EffectsLayer.Children.Remove(dot);
            arrive();
        });
    }

    private void ShowFloatingLibrary()
    {
        _floatingLibraryTimer.Stop();
        FloatingLibrary.IsHitTestVisible = true;
        var from = FloatingLibrary.Opacity;
        Motion.Animate("floating-library", TimeSpan.FromMilliseconds(180), t => FloatingLibrary.Opacity = Motion.Lerp(from, 1, t));
    }

    private void BounceFloatingLibrary()
    {
        Motion.Animate("floating-library-bounce", TimeSpan.FromMilliseconds(420),
            t => FloatingLibraryScale.ScaleX = FloatingLibraryScale.ScaleY = 1 + 0.25 * Math.Sin(Math.PI * t),
            ease: t => t);
        _floatingLibraryTimer.Start();
    }

    private void HideFloatingLibrary()
    {
        _floatingLibraryTimer.Stop();
        FloatingLibrary.IsHitTestVisible = false;
        var from = FloatingLibrary.Opacity;
        Motion.Animate("floating-library", TimeSpan.FromMilliseconds(250), t => FloatingLibrary.Opacity = Motion.Lerp(from, 0, t));
    }

    /// <summary>The floating button goes straight to the downloads on the side.</summary>
    private void FloatingLibrary_Click(object sender, RoutedEventArgs e)
    {
        HideFloatingLibrary();
        OpenLibrary(LibrarySection.Downloads);
    }

    // Keyboard shortcuts, which also work with focus in the page.

    private void NewTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenPalette(PaletteMode.NewTab);
    }

    private void CloseTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (PeekOpen)
        {
            args.Handled = true;
            ClosePeek();
            return;
        }
        args.Handled = true;
        if (_shell.CurrentTab is { } tab) _shell.CloseTab(tab);
    }

    private void FocusAddress_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenPalette(PaletteMode.Navigate);
    }

    private void History_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleHistory();
    }

    private void ToggleHistory()
    {
        if (_libraryShown && Library.Section == LibrarySection.History) CloseLibrary(returnToSidebar: false);
        else OpenLibrary(LibrarySection.History);
    }

    // Esc with focus outside the page (sidebar, find bar…): closes whatever is open.
    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (CloseWithEscape()) args.Handled = true;
    }

    /// <summary>Esc closes the topmost thing the app has open. False when there was nothing to close.</summary>
    private bool CloseWithEscape()
    {
        if (_userFullscreen)
        {
            SetWindowFullscreen(false);
            return true;
        }
        if (PeekOpen)
        {
            ClosePeek();
            return true;
        }
        if (Switcher.IsOpen)
        {
            CancelSwitcher();
            return true;
        }
        if (FindBar.Visibility == Visibility.Visible)
        {
            CloseFindBar();
            return true;
        }
        if (!_libraryShown) return false; // without the library open, Esc reaches the page
        CloseLibrary(returnToSidebar: true);
        return true;
    }

    /// <summary>
    /// Esc went through the page and the page did not take it: the app closes what it has open and, if there is
    /// nothing, stops the page from loading, like Chrome.
    /// </summary>
    private void PageEscape(bool stopLoading)
    {
        if (CloseWithEscape()) return;
        if (stopLoading && _shell.CurrentTab is { IsLoading: true } tab) tab.Stop();
    }

    // F11: the window goes full screen (and F11 or Esc brings it back). An HTML full screen in progress just ends.
    private void Fullscreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_fullscreen && !_userFullscreen) _shell.CurrentTab?.ExitFullscreen();
        else SetWindowFullscreen(!_userFullscreen);
    }

    private void SetWindowFullscreen(bool fullscreen)
    {
        _userFullscreen = fullscreen;
        // The page may still be in HTML full screen (a video): it stays until the page leaves it.
        if (fullscreen || _shell.CurrentTab is not { IsFullscreen: true }) SetFullscreen(fullscreen);
    }

    // Ctrl+K and Ctrl+E: the palette with nothing in it, ready to search.
    private void Search_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenPalette(PaletteMode.Navigate, search: true);
    }

    private void Reload_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _shell.CurrentTab?.Reload();
    }

    private void DevTools_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _shell.ToggleDevTools();
    }

    // Ctrl+Tab opens the switcher instead of switching blind; Ctrl+PgUp/PgDn switch directly, as in Chrome.
    private void NextTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (sender.Key == VirtualKey.Tab) StepSwitcher(+1);
        else _shell.SelectRelativeTab(+1);
    }

    private void PreviousTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (sender.Key == VirtualKey.Tab) StepSwitcher(-1);
        else _shell.SelectRelativeTab(-1);
    }


    private DispatcherQueueTimer? _switcherTimer;

    private void StepSwitcher(int delta)
    {
        if (_fullscreen || Palette.IsOpen) return;
        if (!Switcher.IsOpen) HidePreview();
        if (!Switcher.Step(delta)) return;

        // While Ctrl stays down the switcher stays open; on release, go to the selected tab.
        if (_switcherTimer is null)
        {
            _switcherTimer = DispatcherQueue.CreateTimer();
            _switcherTimer.Interval = TimeSpan.FromMilliseconds(15);
            _switcherTimer.Tick += (_, _) =>
            {
                if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0) CommitSwitcher();
            };
        }
        _switcherTimer.Start();
    }

    private void CommitSwitcher()
    {
        _switcherTimer?.Stop();
        if (Switcher.Commit() is { } tab) _shell.ActivateTab(tab);
        WebView.Focus(FocusState.Programmatic);
    }

    private void CancelSwitcher()
    {
        _switcherTimer?.Stop();
        Switcher.Cancel();
    }

    private void TabNumber_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _shell.SelectTabNumber(sender.Key - Windows.System.VirtualKey.Number0);
    }


    private bool _onboardingOpen;

    private void ShowOnboarding()
    {
        if (_onboardingOpen) return;
        _onboardingOpen = true;
        SetSidebarShown(_sidebarDocked);
        var onboarding = new Onboarding(_shell);
        onboarding.Finished += (_, _) =>
        {
            OnboardingHost.Children.Clear();
            OnboardingHost.Visibility = Visibility.Collapsed;
            _onboardingOpen = false;
            WebView.Focus(FocusState.Programmatic);
        };
        OnboardingHost.Children.Add(onboarding);
        OnboardingHost.Visibility = Visibility.Visible;
    }


    private static readonly TimeSpan PeekOpenDuration = TimeSpan.FromMilliseconds(360);
    private static readonly TimeSpan PeekCloseDuration = TimeSpan.FromMilliseconds(170);
    private BrowserTab? _peekTab;
    private int _peekVersion; // a close in progress must not hide a Peek that opened again

    private bool PeekOpen => _peekTab is not null;

    private void SetupPeek()
    {
        _shell.PeekRequested += (_, request) =>
        {
            // A link from inside Peek opens right there.
            if (_peekTab is not null && request.From == _peekTab) _peekTab.Navigate(request.Url);
            else OpenPeek(request.From, request.Url);
        };
        // A sign-in or payment popup shows in Peek, as a real popup of the page; when Peek is busy, as a tab.
        _shell.PopupOpened += (_, request) =>
        {
            if (_peekTab is not null || _fullscreen) _shell.AdoptTab(request.Popup);
            else OpenPeek(request.From, popup: request.Popup);
        };
        // The page closed itself (the popup finished signing in).
        _shell.TabCloseRequested += (_, tab) =>
        {
            if (tab == _peekTab) ClosePeek();
            else _shell.CloseTab(tab);
        };
        PageArea.SizeChanged += (_, _) => { if (PeekOpen) LayoutPeek(); };
        PeekCard.Shadow = new ThemeShadow();
        PeekCard.Translation = new Vector3(0, 0, 48);
    }

    /// <summary>Where the card goes: centered over the page, almost as tall, leaving room for the buttons.</summary>
    private Rect PeekBounds()
    {
        var origin = PageArea.TransformToVisual(Root).TransformPoint(default);
        var w = PageArea.ActualWidth;
        var h = PageArea.ActualHeight;
        var width = Math.Clamp(w * 0.82, Math.Min(w - 24, 480), Math.Max(480, w - 120));
        var height = Math.Max(240, h - 24);
        var left = origin.X + (w - width) / 2 - 24; // slightly left, since the buttons go on the right
        var top = origin.Y + (h - height) / 2;
        return new Rect(Math.Max(origin.X + 8, left), top, width, height);
    }

    private void LayoutPeek()
    {
        var bounds = PeekBounds();
        PeekHost.Margin = new Thickness(bounds.X, bounds.Y, 0, 0);
        PeekHost.Width = bounds.Width;
        PeekHost.Height = bounds.Height;
        PeekActions.Margin = new Thickness(bounds.Right + 12, bounds.Y + 6, 0, 0);
        ElementCompositionPreview.GetElementVisual(PeekHost).CenterPoint =
            new Vector3((float)bounds.Width / 2, (float)bounds.Height / 2, 0);
    }

    private void OpenPeek(BrowserTab from, string? url = null, BrowserTab? popup = null)
    {
        url ??= popup?.Url ?? "";
        if (_fullscreen) { _shell.NewTab(url); return; }
        HidePreview();
        CloseFindBar(focusPage: false);
        if (Switcher.IsOpen) CancelSwitcher();

        // One was already open: replace the page without animating again.
        if (_peekTab is { } current && popup is null)
        {
            current.Navigate(url);
            return;
        }

        _peekVersion++;
        _peekTab = popup ?? _shell.CreatePeekTab(url, from.IsPrivate);
        PeekLayer.Visibility = Visibility.Visible;
        LayoutPeek();
        PeekView.Tab = _peekTab;
        PeekView.Focus(FocusState.Programmatic);

        // It grows from the clicked link, or from the center when that is unknown.
        var bounds = PeekBounds();
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var click = from == _shell.CurrentTab && WebView.LastPressPosition is { } press
            ? WebView.TransformToVisual(Root).TransformPoint(press)
            : center;
        AnimatePeekIn(new Vector3((float)(click.X - center.X), (float)(click.Y - center.Y), 0));
    }

    private void AnimatePeekIn(Vector3 offset)
    {
        var card = ElementCompositionPreview.GetElementVisual(PeekHost);
        var scrim = ElementCompositionPreview.GetElementVisual(PeekScrim);
        var actions = ElementCompositionPreview.GetElementVisual(PeekActions);
        ElementCompositionPreview.SetIsTranslationEnabled(PeekHost, true);
        ElementCompositionPreview.SetIsTranslationEnabled(PeekActions, true);
        var c = card.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

        var grow = c.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(0, new Vector3(0.08f, 0.08f, 1));
        grow.InsertKeyFrame(1, Vector3.One, ease);
        grow.Duration = PeekOpenDuration;
        card.StartAnimation("Scale", grow);

        var move = c.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, offset);
        move.InsertKeyFrame(1, Vector3.Zero, ease);
        move.Duration = PeekOpenDuration;
        card.StartAnimation("Translation", move);

        var appear = c.CreateScalarKeyFrameAnimation();
        appear.InsertKeyFrame(0, 0);
        appear.InsertKeyFrame(1, 1);
        appear.Duration = TimeSpan.FromMilliseconds(140);
        card.StartAnimation("Opacity", appear);

        var dim = c.CreateScalarKeyFrameAnimation();
        dim.InsertKeyFrame(0, 0);
        dim.InsertKeyFrame(1, 1, ease);
        dim.Duration = TimeSpan.FromMilliseconds(260);
        scrim.StartAnimation("Opacity", dim);

        // The buttons arrive a moment later, sliding out from the card.
        var show = c.CreateScalarKeyFrameAnimation();
        show.InsertKeyFrame(0, 0);
        show.InsertKeyFrame(1, 1, ease);
        show.DelayTime = TimeSpan.FromMilliseconds(180);
        show.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        show.Duration = TimeSpan.FromMilliseconds(220);
        actions.StartAnimation("Opacity", show);
        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0, new Vector3(-10, 0, 0));
        slide.InsertKeyFrame(1, Vector3.Zero, ease);
        slide.DelayTime = TimeSpan.FromMilliseconds(180);
        slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        slide.Duration = TimeSpan.FromMilliseconds(220);
        actions.StartAnimation("Translation", slide);
    }

    /// <summary>Closes Peek. With <paramref name="keepTab"/> the tab lives on as a regular one.</summary>
    private void ClosePeek(bool keepTab = false)
    {
        if (_peekTab is not { } tab) return;
        _peekTab = null;
        var version = ++_peekVersion;

        var card = ElementCompositionPreview.GetElementVisual(PeekHost);
        var scrim = ElementCompositionPreview.GetElementVisual(PeekScrim);
        var actions = ElementCompositionPreview.GetElementVisual(PeekActions);
        var c = card.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);

        var shrink = c.CreateVector3KeyFrameAnimation();
        shrink.InsertKeyFrame(1, keepTab ? new Vector3(1.03f, 1.03f, 1) : new Vector3(0.94f, 0.94f, 1), ease);
        shrink.Duration = PeekCloseDuration;
        card.StartAnimation("Scale", shrink);
        foreach (var visual in new[] { card, scrim, actions })
        {
            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1, 0, ease);
            fade.Duration = PeekCloseDuration;
            visual.StartAnimation("Opacity", fade);
        }
        batch.End();

        PeekView.Tab = null;
        if (keepTab)
        {
            _shell.AdoptTab(tab); // the page behind shows this tab while the card fades
        }
        else
        {
            _shell.DisposePeekTab(tab);
            WebView.Focus(FocusState.Programmatic);
        }

        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (version == _peekVersion) PeekLayer.Visibility = Visibility.Collapsed;
        });
    }

    private void PeekClose_Click(object sender, RoutedEventArgs e) => ClosePeek();
    private void PeekExpand_Click(object sender, RoutedEventArgs e) => ClosePeek(keepTab: true);
    private void PeekScrim_Tapped(object sender, TappedRoutedEventArgs e) => ClosePeek();


    private void Downloads_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleDownloads();
    }

    private void ToggleDownloads()
    {
        if (_libraryShown && Library.Section == LibrarySection.Downloads) CloseLibrary(returnToSidebar: false);
        else OpenLibrary(LibrarySection.Downloads);
    }

    /// <summary>Ctrl+S pins or unpins the sidebar (saved, and SettingsChanged applies it, like the button).</summary>
    private void ToggleDock_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleDock();
    }

    private void ToggleDock()
    {
        if (_fullscreen) return;
        _shell.Settings.SidebarDocked = !_sidebarDocked;
    }

    private void PrivateTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_fullscreen) _shell.CurrentTab?.ExitFullscreen();
        _shell.OpenPrivateTab();
    }

    private void ReopenTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _shell.ReopenClosedTab();
    }

    private void Favorite_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_shell.CurrentTab is { } tab) _shell.ToggleFavorite(tab);
    }

    private void Print_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _shell.CurrentTab?.Print();
    }

    private void ViewSource_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewSourceOfCurrent();
    }

    private void ViewSourceOfCurrent()
    {
        if (_shell.CurrentTab?.Url is { Length: > 0 } url && !StartPage.Is(url) && !url.StartsWith("view-source:"))
            _shell.NewTab("view-source:" + url);
    }

    private void Back_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _shell.CurrentTab?.GoBack();
    }

    private void Forward_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _shell.CurrentTab?.GoForward();
    }

    /// <summary>Alt+Home: the start page in this tab.</summary>
    private void Home_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_shell.CurrentTab is { } tab) tab.Navigate(StartPage.Url);
        else _shell.OpenStartPage();
    }

    /// <summary>Zoom with Ctrl +, Ctrl - and Ctrl 0 (main and numeric keypad). These keys have no name in XAML.</summary>
    private void AddZoomAccelerators()
    {
        void Add(VirtualKey key, Action<BrowserTab> action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                if (_shell.CurrentTab is { } tab) action(tab);
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }
        Add((VirtualKey)187, t => t.ZoomIn());   // +
        Add(VirtualKey.Add, t => t.ZoomIn());
        Add((VirtualKey)189, t => t.ZoomOut());  // -
        Add(VirtualKey.Subtract, t => t.ZoomOut());
        Add(VirtualKey.Number0, t => t.ResetZoom());
        Add(VirtualKey.NumberPad0, t => t.ResetZoom());
    }


    private BrowserTab? _findTab;

    private void Find_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenFindBar();
    }

    private void FindNext_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FindNextOrOpen(forward: true);
    }

    private void FindNextOrOpen(bool forward)
    {
        if (FindBar.Visibility == Visibility.Visible) FindStep(forward);
        else OpenFindBar();
    }

    private void FindPrevious_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FindNextOrOpen(forward: false);
    }

    private void OpenFindBar()
    {
        if (_shell.CurrentTab is not { } tab || _fullscreen) return;
        WatchFind(tab);
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
        if (FindBox.Text.Length > 0) tab.Find(FindBox.Text); // the previous search, highlighted again
    }

    private void CloseFindBar(bool focusPage = true)
    {
        if (FindBar.Visibility != Visibility.Visible) return;
        FindBar.Visibility = Visibility.Collapsed;
        _findTab?.StopFinding();
        WatchFind(null);
        FindCount.Text = "";
        if (focusPage) WebView.Focus(FocusState.Programmatic);
    }

    private void WatchFind(BrowserTab? tab)
    {
        if (_findTab == tab) return;
        if (_findTab is not null) _findTab.FindResult -= FindTab_FindResult;
        _findTab = tab;
        if (tab is not null) tab.FindResult += FindTab_FindResult;
    }

    private void FindTab_FindResult(object? sender, (int Count, int Active) result)
    {
        FindCount.Text = FindBox.Text.Length == 0 ? "" : result.Count == 0 ? L.T("No results", "Sin resultados") : $"{result.Active}/{result.Count}";
    }

    private void FindStep(bool forward)
    {
        if (FindBox.Text.Length > 0) _findTab?.Find(FindBox.Text, forward);
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_findTab is null) return;
        if (FindBox.Text.Length == 0)
        {
            _findTab.StopFinding();
            FindCount.Text = "";
        }
        else
        {
            _findTab.Find(FindBox.Text);
        }
    }

    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            FindStep(forward: !shift);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseFindBar();
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindStep(forward: true);
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindStep(forward: false);
    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFindBar();


    private NativePoint _dragCursorStart;
    private PointInt32 _dragWindowStart;

    private void MoveWindowWithCursor(bool starting)
    {
        if (!GetCursorPos(out var cursor)) return;
        if (starting)
        {
            // Maximized: like Windows, it restores under the mouse at the same proportion of the width.
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } window)
            {
                var proportion = (cursor.X - AppWindow.Position.X) / (double)Math.Max(1, AppWindow.Size.Width);
                var fromTop = cursor.Y - AppWindow.Position.Y;
                window.Restore();
                AppWindow.Move(new PointInt32(cursor.X - (int)(AppWindow.Size.Width * proportion), cursor.Y - fromTop));
            }
            _dragCursorStart = cursor;
            _dragWindowStart = AppWindow.Position;
            return;
        }
        AppWindow.Move(new PointInt32(
            _dragWindowStart.X + cursor.X - _dragCursorStart.X,
            _dragWindowStart.Y + cursor.Y - _dragCursorStart.Y));
    }


    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint hWnd, ref NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(NativePoint point);

    private const int VK_CONTROL = 0x11;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hWnd, uint flags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(NativePoint point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
