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
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace BeautyBrowser.Controls;

/// <summary>
/// The "tabs on top" layout (Settings → Sidebar and tabs): a star that opens the favorites in a panel, the open
/// favorites as small icon tabs, then the space's tabs, which share the strip's width like Chrome's, and a toolbar
/// with the address below. Tabs grow in when they open, shrink away when they close and the rest slide to make room.
/// </summary>
public sealed partial class TopBar : UserControl
{
    private const double MaxTabWidth = 220;
    private const double MinTabWidth = 64;
    private const double PinnedTabWidth = 40;
    private const double TabGap = 2;
    private static readonly TimeSpan TabMotion = TimeSpan.FromMilliseconds(220);

    private BrowserShell? _shell;
    private BrowserTab? _watchedTab;
    private ObservableCollection<BrowserTab>? _space;

    // One element per open tab (key: the BrowserTab) or open favorite (key: the FavoriteSite). Closing ones stay in
    // the strip, shrinking, until their animation ends.
    private readonly Dictionary<object, Grid> _items = [];
    private readonly HashSet<Grid> _closing = [];
    private Grid? _hovered;
    private bool _syncQueued;
    private bool _resetQueued;

    public TopBar()
    {
        InitializeComponent();
        Strip.SizeChanged += (_, _) => Layout(animate: false);
    }

    public event EventHandler<PaletteMode>? PaletteRequested;
    public event EventHandler<LibrarySection>? LibraryRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? NewSpaceRequested;

    /// <summary>The free part of the tab strip, which drags the window.</summary>
    public FrameworkElement DragArea => DragZone;

    /// <summary>Rounded top corners like the window's; square when maximized.</summary>
    public bool Rounded
    {
        set => Panel.CornerRadius = value ? new CornerRadius(8, 8, 0, 0) : new CornerRadius(0);
    }

    /// <summary>Where the download animation lands.</summary>
    public FrameworkElement LibraryTarget => DownloadsButton;

    public void Attach(BrowserShell shell)
    {
        _shell = shell;
        shell.CurrentSpaceChanged += (_, _) => QueueSync(reset: true);
        shell.CurrentTabChanged += (_, _) => { QueueSync(); WatchCurrentTab(); };
        shell.Favorites.Items.CollectionChanged += (_, _) => QueueSync();
        shell.SpaceRenamed += (_, _) => RefreshSpace();
        QueueSync(reset: true);
        WatchCurrentTab();
    }

    /// <summary>Bounce of the downloads icon when a download arrives.</summary>
    public void PlayLibraryBounce()
    {
        DownloadsIcon.Foreground = Res("BeautyAccentBrush");
        Motion.Animate("topbar-library-bounce", TimeSpan.FromMilliseconds(420),
            t => DownloadsScale.ScaleX = DownloadsScale.ScaleY = 1 + 0.32 * Math.Sin(Math.PI * t),
            ease: t => t,
            completed: () => DownloadsIcon.ClearValue(FontIcon.ForegroundProperty));
    }

    // ---- the strip ----

    /// <summary>Once per burst of changes (closing a tab also changes the current one), on the next tick.</summary>
    private void QueueSync(bool reset = false)
    {
        _resetQueued |= reset;
        if (_syncQueued) return;
        _syncQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _syncQueued = false;
            var fresh = _resetQueued;
            _resetQueued = false;
            Sync(animate: !fresh);
        });
    }

    /// <summary>Brings the strip in line with the shell: new tabs grow in, closed ones shrink away, order follows.</summary>
    private void Sync(bool animate)
    {
        if (_shell is null) return;

        if (!animate || _space != _shell.CurrentSpace.Tabs)
        {
            // Another space: its tabs are just there, with no animation.
            if (_space is not null) _space.CollectionChanged -= Space_CollectionChanged;
            _space = _shell.CurrentSpace.Tabs;
            _space.CollectionChanged += Space_CollectionChanged;
            foreach (var key in _items.Keys) Unobserve(key);
            _items.Clear();
            _closing.Clear();
            Motion.Cancel("topbar-tabs");
            Tabs.Children.Clear();
            animate = false;
            RefreshSpace();
        }

        var wanted = new List<object>();
        foreach (var site in _shell.Favorites.Items)
            if (_shell.FavoriteTabOf(site) is not null) wanted.Add(site);
        wanted.AddRange(_space);

        // Gone: they shrink away (or leave at once).
        foreach (var (key, item) in _items.ToList())
        {
            if (wanted.Contains(key)) continue;
            _items.Remove(key);
            Unobserve(key);
            item.IsHitTestVisible = false;
            if (animate) _closing.Add(item);
            else Tabs.Children.Remove(item);
        }

        // New ones go right after the previous wanted item; existing ones move if the order changed.
        Grid? previous = null;
        foreach (var key in wanted)
        {
            if (!_items.TryGetValue(key, out var item))
            {
                item = key is BrowserTab tab ? TabItem(tab) : FavoriteTab((FavoriteSite)key);
                _items[key] = item;
                Observe(key);
                if (animate) { item.Width = 0; item.Opacity = 0; }
                Tabs.Children.Insert(previous is null ? 0 : Tabs.Children.IndexOf(previous) + 1, item);
            }
            else if (previous is not null)
            {
                var at = Tabs.Children.IndexOf(item);
                var after = Tabs.Children.IndexOf(previous);
                if (at < after) Tabs.Children.Move((uint)at, (uint)after);
            }
            previous = item;
        }

        foreach (var item in _items.Values) Style(item);
        Layout(animate);
    }

    private void Space_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueSync();

    /// <summary>
    /// Widths: favorites are fixed, tabs share what is left. Animated, every tab glides from its width to the new one
    /// (closing ones to zero, then out of the strip).
    /// </summary>
    private void Layout(bool animate)
    {
        if (_shell is null) return;
        var pinned = _items.Keys.Count(k => k is FavoriteSite);
        var regular = _items.Count - pinned;
        var reserved = Strip.Padding.Left + FavoritesButton.Width + FavoritesButton.Margin.Right
                       + NewTabButton.Width + NewTabButton.Margin.Left + Strip.ColumnDefinitions[4].Width.Value + 24;
        var available = Strip.ActualWidth - reserved - pinned * (PinnedTabWidth + TabGap);
        var tabWidth = regular == 0 ? MaxTabWidth : Math.Clamp(available / regular - TabGap, MinTabWidth, MaxTabWidth);

        var plan = Tabs.Children.OfType<Grid>().Select(item =>
        {
            var closing = _closing.Contains(item);
            var from = double.IsNaN(item.Width) ? item.ActualWidth : item.Width;
            var to = closing ? 0 : item.Tag is FavoriteSite ? PinnedTabWidth : tabWidth;
            return (Item: item, From: from, To: to, FromOpacity: item.Opacity, ToOpacity: closing ? 0.0 : 1.0);
        }).ToList();

        if (!animate)
        {
            if (_closing.Count > 0) return; // a resize mid-animation: the running one lands on the right widths
            Motion.Cancel("topbar-tabs");
            foreach (var step in plan) { step.Item.Width = step.To; step.Item.Opacity = step.ToOpacity; }
            return;
        }

        Motion.Animate("topbar-tabs", TabMotion, t =>
        {
            foreach (var step in plan)
            {
                step.Item.Width = Math.Max(0, Motion.Lerp(step.From, step.To, t));
                step.Item.Opacity = Motion.Lerp(step.FromOpacity, step.ToOpacity, t);
            }
        }, completed: () =>
        {
            foreach (var item in _closing) Tabs.Children.Remove(item);
            _closing.Clear();
        });
    }

    private void Observe(object key)
    {
        if (key is BrowserTab tab) tab.PropertyChanged += Tab_PropertyChanged;
    }

    private void Unobserve(object key)
    {
        if (key is BrowserTab tab) tab.PropertyChanged -= Tab_PropertyChanged;
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(BrowserTab.Title) or nameof(BrowserTab.Favicon) or nameof(BrowserTab.IsLoading))) return;
        if (sender is BrowserTab tab && _items.TryGetValue(tab, out var item)) FillTab(item, tab);
    }

    private bool IsCurrent(Grid item) => _shell?.CurrentTab is { } current && item.Tag switch
    {
        BrowserTab tab => tab == current,
        FavoriteSite site => _shell.FavoriteTabOf(site) == current,
        _ => false,
    };

    /// <summary>The current tab is a white card; the rest are plain, with a light fill under the mouse.</summary>
    private void Style(Grid item)
    {
        var current = IsCurrent(item);
        item.Background = current ? Res("BeautyCardBrush")
            : item == _hovered ? Res("BeautySubtleFillBrush")
            : Res("BeautyTransparentBrush");
        item.BorderBrush = current ? Res("BeautyFillLowBrush") : Res("BeautyTransparentBrush");
        if (item.Tag is BrowserTab && item.Children[^1] is Button close) close.Opacity = current || item == _hovered ? 1 : 0;
    }

    private Grid NewItem(object key, double padLeft)
    {
        var item = new Grid
        {
            Tag = key,
            Height = 32,
            Padding = new Thickness(padLeft, 0, 4, 0),
            ColumnSpacing = 8,
            CornerRadius = (CornerRadius)Application.Current.Resources["BeautyItemCornerRadius"],
            BorderThickness = new Thickness(1),
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(140) },
        };
        item.PointerEntered += (_, _) => { _hovered = item; Style(item); };
        item.PointerExited += (_, _) => { if (_hovered == item) _hovered = null; Style(item); };
        return item;
    }

    private Grid TabItem(BrowserTab tab)
    {
        var item = NewItem(tab, 10);
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        item.ColumnDefinitions.Add(new ColumnDefinition());
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(title, 1);
        var close = new Button
        {
            Style = (Style)Application.Current.Resources["BeautyTabCloseButton"],
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            Content = new FontIcon { Glyph = "", FontSize = 8 },
        };
        ToolTipService.SetToolTip(close, L.T("Close tab (Ctrl+W)", "Cerrar pestaña (Ctrl+W)"));
        close.Click += (_, _) => _shell?.CloseTab(tab);
        Grid.SetColumn(close, 2);
        item.Children.Add(new Grid()); // icon (FillTab)
        item.Children.Add(title);
        item.Children.Add(close);
        FillTab(item, tab);

        item.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(item).Properties;
            if (point.IsMiddleButtonPressed) { e.Handled = true; _middlePressed = tab; }
            else if (point.IsLeftButtonPressed) _shell?.SelectTab(tab);
        };
        item.PointerReleased += (_, e) =>
        {
            if (e.GetCurrentPoint(item).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonReleased) return;
            e.Handled = true;
            var pressed = _middlePressed;
            _middlePressed = null;
            // After the event: removing the element while WinUI still handles the press crashes (see Sidebar).
            if (pressed == tab && _shell is { } shell) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => shell.CloseTab(tab));
        };
        item.RightTapped += (_, e) => ShowTabMenu(tab, item, e);
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
        var text = string.IsNullOrEmpty(tab.Title) ? Sidebar.ShortUrl(tab.Url) : tab.Title;
        ((TextBlock)item.Children[1]).Text = text;
        ToolTipService.SetToolTip(item, text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, text);
    }

    /// <summary>An open favorite: a small icon-only tab, like Chrome's pinned tabs. Middle click closes it.</summary>
    private Grid FavoriteTab(FavoriteSite site)
    {
        var item = NewItem(site, 0);
        item.Padding = new Thickness(0);
        item.Children.Add(FavoriteIcon(site, 16));
        var name = string.IsNullOrWhiteSpace(site.Title) ? site.Host : site.Title;
        ToolTipService.SetToolTip(item, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, name);
        item.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(item).Properties;
            if (point.IsMiddleButtonPressed) e.Handled = true;
            else if (point.IsLeftButtonPressed) _shell?.OpenFavorite(site);
        };
        item.PointerReleased += (_, e) =>
        {
            if (e.GetCurrentPoint(item).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonReleased) return;
            e.Handled = true;
            if (_shell is { } shell && shell.FavoriteTabOf(site) is { } open)
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => shell.CloseTab(open));
        };
        return item;
    }

    /// <summary>The site's icon, with a globe until it loads.</summary>
    private static Grid FavoriteIcon(FavoriteSite site, double size)
    {
        var image = new Image { Width = size, Height = size };
        var placeholder = new FontIcon { Glyph = "", FontSize = size - 2, Foreground = Res("BeautyTextSecondaryBrush") };
        _ = LoadIconAsync(site.FaviconUrl, image, placeholder);
        return new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { placeholder, image } };
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

    // ---- favorites panel ----

    private Flyout? _favorites;
    private DispatcherQueueTimer? _favoritesHover;

    // The mouse resting on the star opens the panel; it closes by itself when the mouse moves away from both.
    private void Favorites_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _favoritesHover ??= CreateHoverTimer();
        _favoritesHover.Start();
    }

    private void Favorites_PointerExited(object sender, PointerRoutedEventArgs e) => _favoritesHover?.Stop();

    private void Favorites_Click(object sender, RoutedEventArgs e) => ShowFavorites(FlyoutShowMode.Standard);

    private DispatcherQueueTimer CreateHoverTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(160);
        timer.Tick += (_, _) => ShowFavorites(FlyoutShowMode.TransientWithDismissOnPointerMoveAway);
        return timer;
    }

    private void ShowFavorites(FlyoutShowMode mode)
    {
        if (_shell is not { } shell) return;
        if (_favorites?.IsOpen == true) return;

        const int columns = 4;
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        var sites = shell.Favorites.Items.ToList();
        for (var i = 0; i < sites.Count; i++)
        {
            if (i % columns == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tile = FavoriteTile(sites[i]);
            Grid.SetRow(tile, i / columns);
            Grid.SetColumn(tile, i % columns);
            grid.Children.Add(tile);
        }

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = L.T("Favorites", "Favoritos"), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 0) });
        panel.Children.Add(sites.Count > 0
            ? grid
            : new TextBlock
            {
                Width = 260,
                Margin = new Thickness(4, 0, 4, 4),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = Res("BeautyTextSecondaryBrush"),
                Text = L.T("Right-click a tab and choose \"Add to favorites\".", "Hacé clic derecho en una pestaña y elegí \"Agregar a favoritos\"."),
            });

        _favorites = new Flyout { Content = panel, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        _favorites.ShowAt(FavoritesButton, new FlyoutShowOptions { ShowMode = mode, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    private Button FavoriteTile(FavoriteSite site)
    {
        var name = string.IsNullOrWhiteSpace(site.Title) ? site.Host : site.Title;
        var open = _shell?.FavoriteTabOf(site) is not null;
        var content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        var icon = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(10),
            Background = Res("BeautyTileBrush"),
            BorderBrush = Res(open ? "BeautyAccentBrush" : "BeautyTileBorderBrush"),
            BorderThickness = new Thickness(open ? 1.5 : 1),
            Child = FavoriteIcon(site, 20),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        content.Children.Add(icon);
        content.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12,
            MaxWidth = 74,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var tile = new Button
        {
            Style = (Style)Application.Current.Resources["BeautyListRowButton"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(2, 8, 2, 6),
            CornerRadius = new CornerRadius(10),
            Content = content,
        };
        ToolTipService.SetToolTip(tile, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, name);
        tile.Click += (_, _) =>
        {
            _favorites?.Hide();
            _shell?.OpenFavorite(site);
        };
        tile.RightTapped += (_, e) =>
        {
            var menu = new MenuFlyout();
            var remove = new MenuFlyoutItem { Text = L.T("Remove from favorites", "Quitar de favoritos") };
            remove.Click += (_, _) => { _favorites?.Hide(); _shell?.Unpin(site.Url); };
            menu.Items.Add(remove);
            menu.ShowAt(tile, e.GetPosition(tile));
            e.Handled = true;
        };
        return tile;
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

    private static Brush Res(string key) =>
        key == "BeautyTransparentBrush" ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : (Brush)Application.Current.Resources[key];
}
