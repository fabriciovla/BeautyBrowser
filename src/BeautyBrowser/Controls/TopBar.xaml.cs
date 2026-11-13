using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using BeautyBrowser.Services;
using BeautyBrowser.Shell;
using BeautyKit;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace BeautyBrowser.Controls;

/// <summary>
/// The "tabs on top" layout (Settings → Sidebar and tabs): favorites as small pinned tabs, then the space's tabs, which
/// share the strip's width like Chrome's (narrower as more open), and a toolbar with the address below.
/// </summary>
public sealed partial class TopBar : UserControl
{
    private const double MaxTabWidth = 220;
    private const double MinTabWidth = 64;
    private const double PinnedTabWidth = 40;
    private const double TabGap = 2;

    private BrowserShell? _shell;
    private BrowserTab? _watchedTab;
    private readonly List<BrowserTab> _observedTabs = [];
    private bool _rebuildQueued;

    public TopBar()
    {
        InitializeComponent();
        Strip.SizeChanged += (_, _) => SizeTabs();
    }

    public event EventHandler<PaletteMode>? PaletteRequested;
    public event EventHandler<LibrarySection>? LibraryRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? NewSpaceRequested;

    /// <summary>The free part of the tab strip, which drags the window.</summary>
    public FrameworkElement DragArea => DragZone;

    /// <summary>Where the download animation lands.</summary>
    public FrameworkElement LibraryTarget => DownloadsButton;

    public void Attach(BrowserShell shell)
    {
        _shell = shell;
        shell.CurrentSpaceChanged += (_, _) => Rebuild();
        shell.CurrentTabChanged += (_, _) => { Rebuild(); WatchCurrentTab(); };
        shell.Favorites.Items.CollectionChanged += (_, _) => Rebuild();
        shell.SpaceRenamed += (_, _) => RefreshSpace();
        Rebuild();
        WatchCurrentTab();
    }

    /// <summary>Bounce of the downloads icon when a download arrives.</summary>
    public void PlayLibraryBounce()
    {
        DownloadsIcon.Foreground = (Brush)Application.Current.Resources["BeautyAccentBrush"];
        Motion.Animate("topbar-library-bounce", TimeSpan.FromMilliseconds(420),
            t => DownloadsScale.ScaleX = DownloadsScale.ScaleY = 1 + 0.32 * Math.Sin(Math.PI * t),
            ease: t => t,
            completed: () => DownloadsIcon.ClearValue(FontIcon.ForegroundProperty));
    }

    // ---- tabs ----

    /// <summary>Rebuilds the strip on the next tick, once per burst of changes (a space switch fires several).</summary>
    private void Rebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _rebuildQueued = false;
            BuildTabs();
        });
    }

    private ObservableCollection<BrowserTab>? _space;

    private void BuildTabs()
    {
        if (_shell is null) return;

        if (_space != _shell.CurrentSpace.Tabs)
        {
            if (_space is not null) _space.CollectionChanged -= Space_CollectionChanged;
            _space = _shell.CurrentSpace.Tabs;
            _space.CollectionChanged += Space_CollectionChanged;
        }
        foreach (var tab in _observedTabs) tab.PropertyChanged -= Tab_PropertyChanged;
        _observedTabs.Clear();

        Tabs.Children.Clear();
        foreach (var site in _shell.Favorites.Items) Tabs.Children.Add(FavoriteTab(site));
        foreach (var tab in _space)
        {
            tab.PropertyChanged += Tab_PropertyChanged;
            _observedTabs.Add(tab);
            Tabs.Children.Add(TabItem(tab));
        }
        RefreshSpace();
        SizeTabs();
    }

    private void Space_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(BrowserTab.Title) or nameof(BrowserTab.Favicon) or nameof(BrowserTab.IsLoading))) return;
        if (sender is not BrowserTab tab) return;
        foreach (var child in Tabs.Children)
            if (child is Grid { Tag: BrowserTab owner } item && owner == tab) FillTab(item, tab);
    }

    /// <summary>Tabs share what the strip has left, between a minimum and Chrome's usual maximum.</summary>
    private void SizeTabs()
    {
        if (_shell is null) return;
        var pinned = Tabs.Children.OfType<FrameworkElement>().Count(c => c.Tag is FavoriteSite);
        var regular = Tabs.Children.Count - pinned;
        if (regular == 0) return;
        var reserved = Strip.Padding.Left + Strip.ColumnDefinitions[3].Width.Value + NewTabButton.Width + NewTabButton.Margin.Left + 24;
        var available = Strip.ActualWidth - reserved - pinned * (PinnedTabWidth + TabGap);
        var width = Math.Clamp(available / regular - TabGap, MinTabWidth, MaxTabWidth);
        foreach (var child in Tabs.Children.OfType<FrameworkElement>())
            if (child.Tag is BrowserTab) child.Width = width;
    }

    private Grid TabItem(BrowserTab tab)
    {
        var current = _shell?.CurrentTab == tab;
        var item = new Grid
        {
            Tag = tab,
            Height = 32,
            Padding = new Thickness(10, 0, 4, 0),
            ColumnSpacing = 8,
            CornerRadius = (CornerRadius)Application.Current.Resources["BeautyItemCornerRadius"],
            Background = current ? Res("BeautyCardBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = current ? Res("BeautyFillLowBrush") : null,
            BorderThickness = new Thickness(current ? 1 : 0),
        };
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        item.ColumnDefinitions.Add(new ColumnDefinition());
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var close = new Button
        {
            Style = (Style)Application.Current.Resources["BeautyTabCloseButton"],
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = current ? 1 : 0,
            Content = new FontIcon { Glyph = "", FontSize = 8 },
        };
        ToolTipService.SetToolTip(close, L.T("Close tab (Ctrl+W)", "Cerrar pestaña (Ctrl+W)"));
        close.Click += (_, _) => _shell?.CloseTab(tab);
        Grid.SetColumn(close, 2);
        item.Children.Add(new Grid()); // icon slot (FillTab)
        item.Children.Add(new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(item.Children[1] as FrameworkElement, 1);
        item.Children.Add(close);
        FillTab(item, tab);

        item.PointerEntered += (_, _) =>
        {
            close.Opacity = 1;
            if (_shell?.CurrentTab != tab) item.Background = Res("BeautySubtleFillBrush");
        };
        item.PointerExited += (_, _) =>
        {
            if (_shell?.CurrentTab != tab) { close.Opacity = 0; item.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); }
        };
        item.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(item).Properties;
            if (point.IsMiddleButtonPressed) { e.Handled = true; _middlePressed = tab; }
            else if (point.IsLeftButtonPressed && e.OriginalSource is not Button) _shell?.SelectTab(tab);
        };
        item.PointerReleased += (_, e) =>
        {
            if (e.GetCurrentPoint(item).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonReleased) return;
            e.Handled = true;
            var pressed = _middlePressed;
            _middlePressed = null;
            // Closing after the event: removing the element while WinUI still handles the press crashes (see Sidebar).
            if (pressed == tab && _shell is { } shell) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => shell.CloseTab(tab));
        };
        item.RightTapped += (_, e) => ShowTabMenu(tab, item, e);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, tab.Title);
        return item;
    }

    private BrowserTab? _middlePressed;

    private static void FillTab(Grid item, BrowserTab tab)
    {
        var slot = (Grid)item.Children[0];
        slot.Children.Clear();
        if (tab.IsLoading && tab.Favicon is null)
            slot.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true });
        else if (tab.Favicon is { } icon)
            slot.Children.Add(new Image { Width = 16, Height = 16, Source = icon });
        else
            slot.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = Res("BeautyTextSecondaryBrush") });
        ((TextBlock)item.Children[1]).Text = string.IsNullOrEmpty(tab.Title) ? Sidebar.ShortUrl(tab.Url) : tab.Title;
        ToolTipService.SetToolTip(item, tab.Title);
    }

    /// <summary>A favorite as a small icon-only tab, like Chrome's pinned tabs.</summary>
    private Grid FavoriteTab(FavoriteSite site)
    {
        var open = _shell?.FavoriteTabOf(site);
        var current = open is not null && open == _shell?.CurrentTab;
        var image = new Image { Width = 16, Height = 16 };
        var placeholder = new FontIcon { Glyph = "", FontSize = 14, Foreground = Res("BeautyTextSecondaryBrush") };
        var item = new Grid
        {
            Tag = site,
            Width = PinnedTabWidth,
            Height = 32,
            CornerRadius = (CornerRadius)Application.Current.Resources["BeautyItemCornerRadius"],
            Background = current ? Res("BeautyCardBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = current ? Res("BeautyFillLowBrush") : null,
            BorderThickness = new Thickness(current ? 1 : 0),
            Children = { placeholder, image },
        };
        _ = LoadIconAsync(site.FaviconUrl, image, placeholder);
        var name = string.IsNullOrWhiteSpace(site.Title) ? site.Host : site.Title;
        ToolTipService.SetToolTip(item, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, name);
        item.PointerEntered += (_, _) => { if (!current) item.Background = Res("BeautySubtleFillBrush"); };
        item.PointerExited += (_, _) => { if (!current) item.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); };
        item.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) _shell?.OpenFavorite(site);
        };
        return item;
    }

    private static async Task LoadIconAsync(string? url, Image image, FrameworkElement placeholder)
    {
        if (await Favicons.LoadForAsync(url, image, image.Width) is not { } source) return;
        image.Source = source;
        placeholder.Visibility = Visibility.Collapsed;
    }

    private void ShowTabMenu(BrowserTab tab, FrameworkElement target, RightTappedRoutedEventArgs e)
    {
        if (_shell is not { } shell) return;
        var menu = new MenuFlyout();
        void Add(string text, Action action)
        {
            var entry = new MenuFlyoutItem { Text = text };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }

        if (shell.Favorites.Contains(tab.Url)) Add(L.T("Remove from favorites", "Quitar de favoritos"), () => shell.Unpin(tab.Url));
        else Add(L.T("Add to favorites", "Agregar a favoritos"), () => shell.Pin(tab));
        Add(L.T("Duplicate tab", "Duplicar pestaña"), () => shell.DuplicateTab(tab));
        Add(L.T("Copy link", "Copiar enlace"), () =>
        {
            var package = new DataPackage();
            package.SetText(tab.Url);
            Clipboard.SetContent(package);
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(L.T("Close tab", "Cerrar pestaña"), () => shell.CloseTab(tab));
        menu.ShowAt(target, e.GetPosition(target));
        e.Handled = true;
    }

    // ---- toolbar ----

    private void WatchCurrentTab()
    {
        if (_watchedTab is not null) _watchedTab.PropertyChanged -= Watched_PropertyChanged;
        _watchedTab = _shell?.CurrentTab;
        if (_watchedTab is not null) _watchedTab.PropertyChanged += Watched_PropertyChanged;
        RefreshNavigation();
    }

    private void Watched_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshNavigation();

    private void RefreshNavigation()
    {
        var tab = _watchedTab;
        BackButton.IsEnabled = tab?.CanGoBack ?? false;
        ForwardButton.IsEnabled = tab?.CanGoForward ?? false;
        UrlText.Text = Sidebar.ShortUrl(tab?.Url);
        var scheme = Uri.TryCreate(tab?.Url, UriKind.Absolute, out var uri) ? uri.Scheme : "";
        UrlInsecureIcon.Visibility = scheme == "http" ? Visibility.Visible : Visibility.Collapsed;
        UrlSecureIcon.Visibility = scheme == "https" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshSpace()
    {
        if (_shell is null) return;
        SpaceIcon.Glyph = _shell.CurrentSpace.Icon;
        ToolTipService.SetToolTip(SpaceButton, _shell.CurrentSpace.Name);
    }

    private void SpaceMenu_Opening(object sender, object e)
    {
        SpaceMenu.Items.Clear();
        if (_shell is not { } shell) return;
        foreach (var space in shell.Spaces)
        {
            var entry = new MenuFlyoutItem
            {
                Text = space.Name,
                Icon = new FontIcon { Glyph = space.Icon },
                FontWeight = space == shell.CurrentSpace ? FontWeights.SemiBold : FontWeights.Normal,
            };
            entry.Click += (_, _) => shell.SwitchSpace(space);
            SpaceMenu.Items.Add(entry);
        }
        SpaceMenu.Items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = L.T("New space", "Nuevo space"), Icon = new FontIcon { Glyph = "" } };
        add.Click += (_, _) => NewSpaceRequested?.Invoke(this, EventArgs.Empty);
        SpaceMenu.Items.Add(add);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _shell?.CurrentTab?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _shell?.CurrentTab?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => _shell?.CurrentTab?.Reload();
    private void Url_Click(object sender, RoutedEventArgs e) => PaletteRequested?.Invoke(this, PaletteMode.Navigate);
    private void NewTab_Click(object sender, RoutedEventArgs e) => PaletteRequested?.Invoke(this, PaletteMode.NewTab);
    private void Library_Click(object sender, RoutedEventArgs e) => LibraryRequested?.Invoke(this, LibrarySection.History);
    private void Downloads_Click(object sender, RoutedEventArgs e) => LibraryRequested?.Invoke(this, LibrarySection.Downloads);
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}
