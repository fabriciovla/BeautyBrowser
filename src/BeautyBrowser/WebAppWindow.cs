using BeautyBrowser.Services;
using BeautyBrowser.Shell;
using BeautyKit;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI;

namespace BeautyBrowser;

/// <summary>
/// An installed site in its own window, as Chrome's and Edge's apps: a thin bar in the site's color (icon, title, back,
/// reload, open in the browser) over the page. No tabs or address bar; it has its own taskbar button and icon
/// (AppUserModelID) and shares the browser's profile. Links that leave the app open in the browser.
/// </summary>
internal sealed class WebAppWindow : Window
{
    private const double BarHeight = 36;

    /// <summary>The app windows open now; the browser stays alive (hidden if closed) while any is open.</summary>
    public static List<WebAppWindow> Open { get; } = [];

    public static event EventHandler? LastClosed;

    private readonly BrowserShell _shell;
    private readonly WebApp _app;
    private readonly BrowserTab _tab;
    private readonly BrowserView _view;
    private readonly Grid _bar;
    private readonly TextBlock _title;
    private readonly TextBlock _outside;
    private readonly Button _back;
    private readonly Border _permissionBar;
    private readonly TextBlock _permissionText;
    private PermissionRequest? _permission;
    private readonly Queue<PermissionRequest> _permissions = [];
    private readonly Brush _foreground;

    public WebApp App => _app;

    public WebAppWindow(BrowserShell shell, WebApp app, string? url = null)
    {
        _shell = shell;
        _app = app;
        Title = app.Name;

        var tint = ColorOf(app.ThemeColor);
        var dark = tint is { } c && Luminance(c) < 0.55;
        _foreground = new SolidColorBrush(dark ? Colors.White : Color.FromArgb(255, 0x1D, 0x1D, 0x20));
        var barBrush = tint is { } t ? new SolidColorBrush(t) : (Brush)Application.Current.Resources["BeautyTopBarBrush"];

        _tab = shell.CreateAppTab(app, url ?? app.StartUrl);
        _view = new BrowserView { Tab = _tab };

        // The bar: icon, page title (and the site when the page left the app), back, reload, open in the browser.
        var icon = new Image { Width = 16, Height = 16, Source = new BitmapImage(new Uri(app.IconPath)), VerticalAlignment = VerticalAlignment.Center };
        _title = new TextBlock { FontSize = 13, Foreground = _foreground, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _outside = new TextBlock { FontSize = 12, Foreground = _foreground, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _back = BarButton("", L.T("Back (Alt+Left)", "Atrás (Alt+Izquierda)"), () => _tab.GoBack());
        var reload = BarButton("", L.T("Reload (Ctrl+R)", "Volver a cargar (Ctrl+R)"), () => _tab.Reload());
        var browser = BarButton("", L.T("Open in BeautyBrowser", "Abrir en BeautyBrowser"), OpenInBrowser);

        var drag = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _title, _outside } };
        drag.Children.Add(titleRow);

        _bar = new Grid { Height = BarHeight, Background = barBrush, Padding = new Thickness(10, 0, 0, 0), ColumnSpacing = 6 };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(140) })
            _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        Place(_bar, _back, 0);
        Place(_bar, reload, 1);
        Place(_bar, new Border { Width = 4 }, 2);
        var titleArea = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon } };
        var middle = new Grid { ColumnSpacing = 8 };
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        middle.ColumnDefinitions.Add(new ColumnDefinition());
        middle.Children.Add(titleArea);
        Place(middle, drag, 1);
        Place(_bar, middle, 3);
        Place(_bar, browser, 4);

        // Permissions (notifications, microphone…): a bar under the title, answered once per site as in the browser.
        _permissionText = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var allow = new Button { Content = L.T("Allow", "Permitir"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var block = new Button { Content = L.T("Block", "Bloquear") };
        allow.Click += (_, _) => AnswerPermission(true);
        block.Click += (_, _) => AnswerPermission(false);
        var permissionRow = new Grid { ColumnSpacing = 8 };
        permissionRow.ColumnDefinitions.Add(new ColumnDefinition());
        permissionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        permissionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        permissionRow.Children.Add(_permissionText);
        Place(permissionRow, block, 1);
        Place(permissionRow, allow, 2);
        _permissionBar = new Border
        {
            Padding = new Thickness(14, 8, 10, 8),
            Background = (Brush)Application.Current.Resources["BeautyCardBrush"],
            BorderBrush = (Brush)Application.Current.Resources["BeautyDividerBrush"],
            BorderThickness = new Thickness(0, 0, 0, 1),
            Visibility = Visibility.Collapsed,
            Child = permissionRow,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(_bar);
        Grid.SetRow(_permissionBar, 1);
        root.Children.Add(_permissionBar);
        Grid.SetRow(_view, 2);
        root.Children.Add(_view);
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.R, VirtualKeyModifiers.Control, () => _tab.Reload()));
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.F5, VirtualKeyModifiers.None, () => _tab.Reload()));
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _tab.GoBack()));
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.Right, VirtualKeyModifiers.Menu, () => _tab.GoForward()));
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.W, VirtualKeyModifiers.Control, Close));
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.F11, VirtualKeyModifiers.None, ToggleFullscreen));
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        root.RequestedTheme = AppTheme.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        Content = root;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(drag);
        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        var glyph = ((SolidColorBrush)_foreground).Color;
        titleBar.ButtonForegroundColor = glyph;
        titleBar.ButtonHoverForegroundColor = glyph;
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x22, glyph.R, glyph.G, glyph.B);
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x38, glyph.R, glyph.G, glyph.B);
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();

        AppWindow.SetIcon(app.IconPath);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 820));
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShellShortcuts.SetWindowAppId(hwnd, app.AppUserModelId,
            $"\"{Environment.ProcessPath}\" {WebAppsService.LaunchArguments(app)}", app.Name, app.IconPath);

        _tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BrowserTab.Title) or nameof(BrowserTab.Url) or nameof(BrowserTab.CanGoBack)) RefreshBar();
        };
        _tab.PermissionRequested += (_, request) => OnPermission(request);
        _tab.FullscreenChanged += (_, fullscreen) => SetFullscreen(fullscreen);
        _tab.CloseRequested += (_, _) => Close();
        RefreshBar();

        Open.Add(this);
        Closed += (_, _) =>
        {
            Open.Remove(this);
            _view.Tab = null;
            _tab.Dispose();
            if (Open.Count == 0) LastClosed?.Invoke(null, EventArgs.Empty);
        };
        Activated += (_, e) => { if (e.WindowActivationState != WindowActivationState.Deactivated) _view.Focus(FocusState.Programmatic); };
    }

    /// <summary>Brings this app's window forward (it is already open).</summary>
    public void Bring()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }

    private void RefreshBar()
    {
        _back.IsEnabled = _tab.CanGoBack;
        _title.Text = string.IsNullOrWhiteSpace(_tab.Title) ? _app.Name : _tab.Title;
        Title = _title.Text;
        // Outside the app (a login page, another site): its address shows, as Chrome does.
        var outside = !_app.InScope(_tab.Url) && Uri.TryCreate(_tab.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        _outside.Text = outside ? HostName.WithoutWww(new Uri(_tab.Url).Host) : "";
        _outside.Visibility = outside ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenInBrowser()
    {
        _shell.OpenInBrowserFromApp(_tab.Url);
        Close();
    }

    private void OnPermission(PermissionRequest request)
    {
        if (_shell.SitePermissions.Decided(request.Origin, request.Permissions) is { } allowed)
        {
            request.Respond(allowed);
            return;
        }
        request.Dismissed += (_, _) => { if (_permission == request) ShowNextPermission(); };
        _permissions.Enqueue(request);
        if (_permission is null) ShowNextPermission();
    }

    private void ShowNextPermission()
    {
        _permission = null;
        while (_permissions.TryDequeue(out var request))
        {
            if (request.IsAnswered) continue;
            _permission = request;
            _permissionText.Text = MainWindow.PermissionMessage(request);
            _permissionBar.Visibility = Visibility.Visible;
            return;
        }
        _permissionBar.Visibility = Visibility.Collapsed;
    }

    private void AnswerPermission(bool allow)
    {
        if (_permission is { } request)
        {
            _shell.SitePermissions.Remember(request.Origin, request.Permissions, allow);
            request.Respond(allow);
        }
        ShowNextPermission();
    }

    private bool _fullscreen;

    private void ToggleFullscreen() => SetFullscreen(!_fullscreen);

    private void SetFullscreen(bool fullscreen)
    {
        if (_fullscreen == fullscreen) return;
        _fullscreen = fullscreen;
        AppWindow.SetPresenter(fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        _bar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        if (!fullscreen && _tab.IsFullscreen) _tab.ExitFullscreen();
    }

    private Button BarButton(string glyph, string tip, Action click)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["BeautyIconButton"],
            Width = 32,
            Height = 28,
            Foreground = _foreground,
            Content = new FontIcon { Glyph = glyph, FontSize = 13 },
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static KeyboardAccelerator Accelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) => { e.Handled = true; action(); };
        return accelerator;
    }

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static Color? ColorOf(string? hex)
    {
        if (WebAppsService.ParseColor(hex) is not { } clean) return null;
        var value = Convert.ToUInt32(clean[1..], 16);
        return Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
}
