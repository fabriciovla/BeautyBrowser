using CefSharp;
using CefSharp.Enums;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace BeautyKit;

/// <summary>
/// Shows the active tab inside the UI and forwards mouse, keyboard and focus to it. Used in XAML like any control:
/// &lt;kit:BrowserView CornerRadius="10" /&gt;.
/// </summary>
public sealed partial class BrowserView : ContentControl
{
    // Shortcuts the app handles (KeyboardAccelerators) instead of the page. AltGr arrives as Ctrl+Alt and is a
    // character (@, \…), not a shortcut.
    private static bool IsAppShortcut(VirtualKey key, bool ctrl, bool shift, bool alt) =>
        (ctrl && !alt && key is VirtualKey.T or VirtualKey.W or VirtualKey.L or VirtualKey.R or VirtualKey.H) ||
        (ctrl && !alt && key is VirtualKey.J or VirtualKey.S or VirtualKey.D or VirtualKey.F or VirtualKey.G
            or VirtualKey.P or VirtualKey.U or VirtualKey.Number0 or VirtualKey.NumberPad0 or VirtualKey.Add
            or VirtualKey.Subtract or (VirtualKey)187 or (VirtualKey)189) ||
        (alt && !ctrl && key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.D) || // back, forward, home, address bar
        (!ctrl && !alt && key is VirtualKey.F3 or VirtualKey.F6) ||
        (ctrl && !alt && key is VirtualKey.Tab or VirtualKey.PageUp or VirtualKey.PageDown) ||   // switch tabs
        (ctrl && !alt && !shift && key is >= VirtualKey.Number1 and <= VirtualKey.Number9) ||    // go to tab N
        (ctrl && shift && !alt && key == VirtualKey.I) ||   // DevTools
        (!ctrl && key is VirtualKey.F5 or VirtualKey.F12);

    private readonly Grid _layers;
    private readonly Border _clip;
    private readonly Image _page;
    private readonly Image _popup;
    private readonly ClickCounter _clicks = new();
    private BrowserTab? _tab;

    public BrowserView()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        // The frame is shown at its real size anchored to the top left (RefreshVisual sizes it). Stretched to the
        // control, the old frame would look distorted during a resize until Chromium delivers the new one.
        _page = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _popup = new Image { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
        var overlay = new Canvas();
        overlay.Children.Add(_popup);

        _layers = new Grid();
        _layers.Children.Add(_page);
        _layers.Children.Add(overlay);
        if (BrowserTab.UseGpu) CreateGpuPanel(_layers); // frames go through the GPU (see BrowserView.Gpu)

        // A white background looks right while loading and makes the whole area hit-testable.
        _clip = new Border { Child = _layers, Background = new SolidColorBrush(Colors.White) };
        Content = _clip;

        // Clip the CPU image; the GPU image rounds its own corners (GpuCorners).
        RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) =>
        {
            _clip.CornerRadius = CornerRadius;
            if (_gpuPanel is not null) PresentGpu(); // the GPU path rounds while drawing, so redraw with the new radius
        });

        SizeChanged += (_, _) => UpdateSize();
        Loaded += (_, _) =>
        {
            XamlRoot.Changed += (_, _) => UpdateSize(); // DPI, maximize…
            UpdateSize();
        };

        PointerMoved += OnPointerMoved;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        PointerExited += OnPointerExited;
        PointerCaptureLost += (_, _) =>
        {
            EndWindowDrag();
            _tab?.SendCaptureLost();
        };

        KeyDown += (_, e) => OnKey(e, up: false);
        KeyUp += (_, e) => OnKey(e, up: true);
        CharacterReceived += OnCharacterReceived;
        GotFocus += (_, _) => _tab?.SendFocus(true);
        LostFocus += (_, _) => _tab?.SendFocus(false);

        SetupDragAndDrop();
    }

    /// <summary>The tab being shown. The others stay hidden and Chromium stops rendering them.</summary>
    public BrowserTab? Tab
    {
        get => _tab;
        set
        {
            if (_tab == value) return;

            if (_tab is not null)
            {
                _tab.VisualChanged -= RefreshVisual;
                _tab.GpuFrameArrived -= PresentGpu;
                _tab.CursorChanged -= OnCursorChanged;
                _tab.ContextMenuRequested -= ShowContextMenu;
                _tab.SendFocus(false);
                _tab.SetHidden(true);
            }

            _tab = value;

            if (_tab is not null)
            {
                _tab.VisualChanged += RefreshVisual;
                _tab.GpuFrameArrived += PresentGpu;
                _tab.CursorChanged += OnCursorChanged;
                _tab.ContextMenuRequested += ShowContextMenu;
                _tab.SetHidden(false);
                UpdateSize();
                if (FocusState != FocusState.Unfocused) _tab.SendFocus(true);
            }

            RefreshVisual();
        }
    }

    private void UpdateSize()
    {
        if (_tab is null || XamlRoot is null) return;

        var scale = XamlRoot.RasterizationScale;
        var origin = TransformToVisual(null).TransformPoint(default);
        _tab.Resize(
            (int)Math.Round(ActualWidth), (int)Math.Round(ActualHeight), scale,
            ((int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale)));
        RefreshVisual(); // the display scale may have changed (frames are in physical pixels)
    }

    private void RefreshVisual()
    {
        if (_gpuPanel is null && BrowserTab.UseGpu) CreateGpuPanel(_layers);

        // GPU path, unless Chromium fell back to the CPU (if its GPU process crashed, for instance); then the bitmap is
        // shown as usual so the page does not go blank.
        if (_gpuPanel is not null)
        {
            if (_tab?.GpuFrame is not null || _tab?.Frame is null)
            {
                _page.Visibility = Visibility.Collapsed;
                PresentGpu();
                return;
            }
            _gpuPanel.Visibility = Visibility.Collapsed;
            _page.Visibility = Visibility.Visible;
        }

        var frame = _tab?.Frame;
        _page.Source = frame;

        // The frame's real size in DIPs; frames come in physical pixels.
        var scale = XamlRoot?.RasterizationScale ?? 1;
        _page.Width = frame is null ? double.NaN : frame.PixelWidth / scale;
        _page.Height = frame is null ? double.NaN : frame.PixelHeight / scale;

        if (_tab?.PopupBounds is { } bounds && _tab.PopupFrame is { } popupFrame)
        {
            _popup.Source = popupFrame;
            _popup.Width = bounds.Width;
            _popup.Height = bounds.Height;
            Canvas.SetLeft(_popup, bounds.X);
            Canvas.SetTop(_popup, bounds.Y);
            _popup.Visibility = Visibility.Visible;
        }
        else
        {
            _popup.Source = null;
            _popup.Visibility = Visibility.Collapsed;
        }
    }


    /// <summary>
    /// Height in DIPs of the strip at the top of the page that drags the window like a title bar: press on an empty
    /// spot (arrow cursor, not on a link or text) and move. Plain clicks still reach the page. 0 turns it off.
    /// </summary>
    public double WindowDragZone { get; set; }

    /// <summary>
    /// Dragging the window from the page: starts (true on the first move), continues with every move while pressed and
    /// ends on release (<see cref="WindowDragEnded"/>). The app moves the window.
    /// </summary>
    public event EventHandler<bool>? WindowDragMoved;

    public event EventHandler? WindowDragEnded;

    private bool _windowDragging;

    /// <summary>Double click on an empty spot of the drag strip: maximize or restore.</summary>
    public event EventHandler? WindowMaximizeToggleRequested;

    private const double WindowDragThreshold = 5;
    private bool _cursorIsArrow = true;
    private Windows.Foundation.Point? _windowDragStart;

    private void EndWindowDrag()
    {
        if (!_windowDragging) return;
        _windowDragging = false;
        WindowDragEnded?.Invoke(this, EventArgs.Empty);
    }

    private bool InWindowDragZone(Windows.Foundation.Point position) =>
        WindowDragZone > 0 && _cursorIsArrow && position.Y >= 0 && position.Y < WindowDragZone;


    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_tab is null) return;
        var point = e.GetCurrentPoint(this);

        if (_windowDragging)
        {
            if (point.Properties.IsLeftButtonPressed) WindowDragMoved?.Invoke(this, false);
            else EndWindowDrag();
            e.Handled = true;
            return;
        }

        if (_windowDragStart is { } start && point.Properties.IsLeftButtonPressed)
        {
            var p = point.Position;
            if (Math.Abs(p.X - start.X) >= WindowDragThreshold || Math.Abs(p.Y - start.Y) >= WindowDragThreshold)
            {
                // The page releases what it had pressed and from here on the mouse moves the window. Pointer capture is
                // kept so moves arrive even when the mouse leaves the view.
                _windowDragStart = null;
                _windowDragging = true;
                _tab.SendCaptureLost();
                WindowDragMoved?.Invoke(this, true);
                e.Handled = true;
                return;
            }
        }
        _tab.SendMouseMove((int)point.Position.X, (int)point.Position.Y, Flags(point.Properties), leave: false);
        e.Handled = true;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_tab is null) return;
        var point = e.GetCurrentPoint(this);
        _tab.SendMouseMove((int)point.Position.X, (int)point.Position.Y, Flags(point.Properties), leave: true);
    }

    /// <summary>Where the last click on the page happened (DIPs, relative to the view); the mini window grows from there.</summary>
    public Windows.Foundation.Point? LastPressPosition { get; private set; }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_tab is null) return;
        var point = e.GetCurrentPoint(this);

        switch (point.Properties.PointerUpdateKind)
        {
            case PointerUpdateKind.XButton1Pressed: _tab.GoBack(); e.Handled = true; return;
            case PointerUpdateKind.XButton2Pressed: _tab.GoForward(); e.Handled = true; return;
        }

        if (ButtonOf(point.Properties.PointerUpdateKind) is not { } button) return;

        LastPressPosition = point.Position;
        Focus(FocusState.Pointer);
        CapturePointer(e.Pointer);

        var clickCount = _clicks.Press(button, point.Position);
        var emptyTop = button == MouseButtonType.Left && InWindowDragZone(point.Position);
        _windowDragStart = emptyTop ? point.Position : null;
        if (emptyTop && clickCount == 2)
        {
            _windowDragStart = null;
            WindowMaximizeToggleRequested?.Invoke(this, EventArgs.Empty);
        }
        _tab.SendMouseClick((int)point.Position.X, (int)point.Position.Y, Flags(point.Properties), button, up: false, clickCount);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_tab is null) return;
        var point = e.GetCurrentPoint(this);
        if (ButtonOf(point.Properties.PointerUpdateKind) is not { } button) return;

        _windowDragStart = null;
        if (_windowDragging)
        {
            EndWindowDrag(); // the page already released the click (capture lost), so no mouse-up is sent
            ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }
        _tab.SendMouseClick((int)point.Position.X, (int)point.Position.Y, Flags(point.Properties), button, up: true, _clicks.Count);
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_tab is null) return;
        var point = e.GetCurrentPoint(this);
        var delta = point.Properties.MouseWheelDelta;
        var horizontal = point.Properties.IsHorizontalMouseWheel;
        _tab.SendMouseWheel((int)point.Position.X, (int)point.Position.Y, Flags(point.Properties),
            horizontal ? delta : 0, horizontal ? 0 : delta);
        e.Handled = true;
    }

    private static MouseButtonType? ButtonOf(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => MouseButtonType.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => MouseButtonType.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => MouseButtonType.Middle,
        _ => null,
    };

    private static CefEventFlags Flags(PointerPointProperties props)
    {
        var flags = KeyboardFlags();
        if (props.IsLeftButtonPressed) flags |= CefEventFlags.LeftMouseButton;
        if (props.IsRightButtonPressed) flags |= CefEventFlags.RightMouseButton;
        if (props.IsMiddleButtonPressed) flags |= CefEventFlags.MiddleMouseButton;
        return flags;
    }


    private void OnKey(KeyRoutedEventArgs e, bool up)
    {
        if (_tab is null) return;

        // Esc leaves full screen; in Chrome the browser handles it, not the page.
        if (!up && e.Key == VirtualKey.Escape && _tab.IsFullscreen)
        {
            _tab.ExitFullscreen();
            e.Handled = true;
            return;
        }

        if (IsAppShortcut(e.Key, IsDown(VirtualKey.Control), IsDown(VirtualKey.Shift), IsDown(VirtualKey.Menu))) return; // the app handles it

        var status = e.KeyStatus;
        // NativeKeyCode is the lParam of WM_KEYDOWN/WM_KEYUP.
        var native = 1 | ((int)status.ScanCode << 16) | (status.IsExtendedKey ? 1 << 24 : 0)
                     | (status.IsMenuKeyDown ? 1 << 29 : 0)
                     | (up ? (1 << 30) | (1 << 31) : status.WasKeyDown ? 1 << 30 : 0);

        var flags = KeyboardFlags();
        if (status.WasKeyDown && !up) flags |= CefEventFlags.IsRepeat;
        if (e.Key is >= VirtualKey.NumberPad0 and <= VirtualKey.Divide) flags |= CefEventFlags.IsKeyPad;

        _tab.SendKey(new KeyEvent
        {
            Type = up ? KeyEventType.KeyUp : KeyEventType.RawKeyDown,
            WindowsKeyCode = (int)e.Key,
            NativeKeyCode = native,
            Modifiers = flags,
            // With AltGr Windows sends WM_KEYDOWN, not WM_SYSKEYDOWN: it is not a system key.
            IsSystemKey = status.IsMenuKeyDown && !IsAltGr(),
        });

        // Only keys XAML uses to move focus (Tab, arrows) are marked handled. Marking every key stops WinUI from
        // raising CharacterReceived and text could not be typed.
        if (e.Key is VirtualKey.Tab or VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
            e.Handled = true;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_tab is null) return;

        // AltGr (@, \, #, [, ], {, }, ~, €… on Spanish keyboards) arrives as Ctrl+Alt. Chromium would take it as a
        // shortcut and type nothing, so it is sent as regular text flagged AltGr.
        var altGr = IsAltGr();
        var modifiers = KeyboardFlags();
        if (altGr) modifiers = (modifiers & ~(CefEventFlags.ControlDown | CefEventFlags.AltDown)) | CefEventFlags.AltGrDown;

        _tab.SendKey(new KeyEvent
        {
            Type = KeyEventType.Char,
            WindowsKeyCode = e.Character,
            NativeKeyCode = 1 | ((int)e.KeyStatus.ScanCode << 16),
            Modifiers = modifiers,
            IsSystemKey = e.KeyStatus.IsMenuKeyDown && !altGr,
        });
        e.Handled = true;
    }

    private static CefEventFlags KeyboardFlags()
    {
        var flags = CefEventFlags.None;
        if (IsDown(VirtualKey.Shift)) flags |= CefEventFlags.ShiftDown;
        if (IsDown(VirtualKey.Control)) flags |= CefEventFlags.ControlDown;
        if (IsDown(VirtualKey.Menu)) flags |= CefEventFlags.AltDown;
        return flags;
    }

    private static bool IsAltGr() => IsDown(VirtualKey.Control) && IsDown(VirtualKey.Menu);

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);


    private void OnCursorChanged(CursorType type)
    {
        var shape = type.ToString() switch
        {
            "Hand" => InputSystemCursorShape.Hand,
            "IBeam" or "VerticalText" => InputSystemCursorShape.IBeam,
            "Cross" or "Cell" => InputSystemCursorShape.Cross,
            "Wait" => InputSystemCursorShape.Wait,
            "Progress" => InputSystemCursorShape.AppStarting,
            "Help" => InputSystemCursorShape.Help,
            "Move" or "Grab" or "Grabbing" => InputSystemCursorShape.SizeAll,
            "NotAllowed" or "NoDrop" => InputSystemCursorShape.UniversalNo,
            "EastResize" or "WestResize" or "EastWestResize" or "ColumnResize" => InputSystemCursorShape.SizeWestEast,
            "NorthResize" or "SouthResize" or "NorthSouthResize" or "RowResize" => InputSystemCursorShape.SizeNorthSouth,
            "NortheastResize" or "SouthwestResize" or "NortheastSouthwestResize" => InputSystemCursorShape.SizeNortheastSouthwest,
            "NorthwestResize" or "SoutheastResize" or "NorthwestSoutheastResize" => InputSystemCursorShape.SizeNorthwestSoutheast,
            _ => InputSystemCursorShape.Arrow,
        };
        _cursorIsArrow = shape == InputSystemCursorShape.Arrow;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    /// <summary>Counts consecutive clicks (double, triple) the way Windows does.</summary>
    private sealed class ClickCounter
    {
        private const double MaxDistance = 4;
        private long _lastTime;
        private Windows.Foundation.Point _lastPoint;
        private MouseButtonType _lastButton;

        public int Count { get; private set; }

        public int Press(MouseButtonType button, Windows.Foundation.Point point)
        {
            var now = Environment.TickCount64;
            var isSequence = Count > 0 && button == _lastButton &&
                             now - _lastTime <= DoubleClickTimeMs &&
                             Math.Abs(point.X - _lastPoint.X) <= MaxDistance &&
                             Math.Abs(point.Y - _lastPoint.Y) <= MaxDistance;

            Count = isSequence ? Math.Min(Count + 1, 3) : 1;
            _lastTime = now;
            _lastPoint = point;
            _lastButton = button;
            return Count;
        }

        private static readonly uint DoubleClickTimeMs = GetDoubleClickTime();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();
    }
}
