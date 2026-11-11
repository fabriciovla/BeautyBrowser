using Point = Windows.Foundation.Point;
using Size = Windows.Foundation.Size;
using System.ComponentModel;
using System.Diagnostics;
using BeautyBrowser.Services;
using BeautyBrowser.Shell;
using BeautyKit;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;

namespace BeautyBrowser.Controls;

/// <summary>
/// The sidebar: navigation, address, favorites, the space's tabs and the library. It only shows
/// <see cref="BrowserShell"/>'s state and asks it for actions.
/// </summary>
public sealed partial class Sidebar : UserControl
{
    private BrowserShell? _shell;
    private BrowserTab? _watchedTab;
    private bool _syncingSelection;
    private int _openFlyouts;

    public Sidebar()
    {
        InitializeComponent();
        TrackFlyout(SiteFlyout);
        SetupLinkDrop();
    }

    // As on Chrome's tab strip: a link (or text) dropped on the sidebar opens in a new tab; text is searched.
    private void SetupLinkDrop()
    {
        AllowDrop = true;
        var visual = new LinkDragVisual();
        DragEnter += async (_, e) =>
        {
            if (Content is not Panel root) return;
            var deferral = e.GetDeferral();
            try { await visual.PrepareAsync(e.DataView, root); }
            finally { deferral.Complete(); }
        };
        DragOver += (_, e) =>
        {
            if (_shell is null || !(e.DataView.Contains(StandardDataFormats.WebLink) || e.DataView.Contains(StandardDataFormats.Text))) return;
            e.AcceptedOperation = DataPackageOperation.Link | DataPackageOperation.Copy;
            e.DragUIOverride.Caption = L.T("Open in new tab", "Abrir en pestaña nueva");
            visual.Apply(e.DragUIOverride);
            e.Handled = true;
        };
        Drop += async (_, e) =>
        {
            if (_shell is not { } shell) return;
            var deferral = e.GetDeferral();
            try
            {
                var input = e.DataView.Contains(StandardDataFormats.WebLink) ? (await e.DataView.GetWebLinkAsync()).AbsoluteUri
                    : e.DataView.Contains(StandardDataFormats.Text) ? (await e.DataView.GetTextAsync()).Trim()
                    : "";
                if (input.Length > 0) shell.Open(input, inNewTab: true);
                e.AcceptedOperation = DataPackageOperation.Link;
            }
            finally
            {
                deferral.Complete();
            }
        };
    }

    /// <summary>Asks to open the command palette (Ctrl+T, Ctrl+L).</summary>
    public event EventHandler<PaletteMode>? PaletteRequested;

    /// <summary>Asks to open the library (history, downloads, settings) on this side.</summary>
    public event EventHandler<LibrarySection>? LibraryRequested;

    /// <summary>The + at the bottom: create a space (opens Spaces in the library with the form on top).</summary>
    public event EventHandler? NewSpaceRequested;

    public event EventHandler? DockToggled;

    /// <summary>A menu or flyout is open, so the window must not hide the sidebar.</summary>
    public bool IsBusy => _openFlyouts > 0 || _favoriteDrag is { Moving: true } || NowPlaying.IsExpanded;

    /// <summary>Where the download animation lands.</summary>
    public FrameworkElement LibraryTarget => LibraryButton;

    public bool IsDocked
    {
        set
        {
            DockIcon.Glyph = value ? "" : "";
            ToolTipService.SetToolTip(DockButton, value ? L.T("Unpin sidebar (Ctrl+S)", "Soltar barra lateral (Ctrl+S)") : L.T("Pin sidebar (Ctrl+S)", "Fijar barra lateral (Ctrl+S)"));
        }
    }

    public void Attach(BrowserShell shell)
    {
        _shell = shell;

        shell.Favorites.Items.CollectionChanged += (_, _) => BuildFavorites();
        shell.Favorites.IconChanged += (_, _) =>
        {
            if (_favoriteDrag is not null) return;
            _favoriteTiles.Clear(); // the icon changed: rebuild the tiles
            BuildFavorites();
        };
        BuildFavorites();

        shell.CurrentSpaceChanged += (_, _) => RefreshSpace();
        shell.CurrentTabChanged += (_, _) => RefreshCurrentTab();
        shell.Spaces.CollectionChanged += (_, _) => RefreshDots();
        shell.SpaceRenamed += (_, _) => RefreshDots(); // new name or icon
        NowPlaying.Attach(shell);

        RefreshSpace();
        RefreshCurrentTab();
    }

    /// <summary>Bounce of the folder icon when a download arrives.</summary>
    public void PlayLibraryBounce()
    {
        LibraryIcon.Foreground = (Brush)Application.Current.Resources["BeautyAccentBrush"];
        Motion.Animate("library-bounce", TimeSpan.FromMilliseconds(420),
            t => LibraryScale.ScaleX = LibraryScale.ScaleY = 1 + 0.32 * Math.Sin(Math.PI * t),
            ease: t => t,
            completed: () => LibraryIcon.ClearValue(FontIcon.ForegroundProperty));
    }


    private void RefreshSpace()
    {
        if (_shell is null) return;
        SpaceName.Text = _shell.CurrentSpace.Name;
        SpaceIcon.Glyph = _shell.CurrentSpace.Icon;
        _indicatorSnapNext = true; // another space: the card appears in place without traveling
        TabList.ItemsSource = _shell.CurrentSpace.Tabs;
        RefreshDots();
        RefreshCurrentTab();
    }

    private void RefreshCurrentTab()
    {
        if (_shell is null) return;

        // A favorite's tab is not in the list, so no tab is marked then.
        _syncingSelection = true;
        TabList.SelectedItem = _shell.CurrentTab is { } current && _shell.CurrentSpace.Tabs.Contains(current) ? current : null;
        _syncingSelection = false;
        PlaceTabIndicator(animate: true);

        if (_watchedTab is not null) _watchedTab.PropertyChanged -= WatchedTab_PropertyChanged;
        _watchedTab = _shell.CurrentTab;
        if (_watchedTab is not null) _watchedTab.PropertyChanged += WatchedTab_PropertyChanged;
        RefreshNavigation();
        RefreshActiveFavorite();
    }

    private void WatchedTab_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshNavigation();

    private void RefreshNavigation()
    {
        var tab = _watchedTab;
        BackButton.IsEnabled = tab?.CanGoBack ?? false;
        ForwardButton.IsEnabled = tab?.CanGoForward ?? false;
        UrlText.Text = ShortUrl(tab?.Url);
    }

    private void RefreshDots()
    {
        if (_shell is null) return;
        SpaceDots.Children.Clear();
        foreach (var space in _shell.Spaces)
        {
            // Every space with its icon: the current one on a white card, the rest dimmed.
            var current = space == _shell.CurrentSpace;
            var dot = new Button
            {
                Style = (Style)Application.Current.Resources["BeautyIconButton"],
                Width = 34,
                Height = 34,
                Padding = new Thickness(0),
                UseSystemFocusVisuals = false,
                Background = current ? (Brush)Application.Current.Resources["BeautyCardBrush"] : null,
                Foreground = (Brush)Application.Current.Resources[current ? "BeautyTextBrush" : "BeautyTextSecondaryBrush"],
                Content = new FontIcon { Glyph = space.Icon, FontSize = 16 },
            };
            ToolTipService.SetToolTip(dot, space.Name);
            dot.Click += (_, _) => _shell.SwitchSpace(space);
            SpaceDots.Children.Add(dot);
        }
    }


    /// <summary>"https://www.google.com/search?q=x" → "google.com".</summary>
    private static string ShortUrl(string? url) =>
        StartPage.Is(url) ? L.T("New tab", "Nueva pestaña")
        : Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.Host.Replace("www.", "")
            : url ?? "";

    private void TrackFlyout(FlyoutBase flyout)
    {
        flyout.Opened += (_, _) => _openFlyouts++;
        flyout.Closed += (_, _) => _openFlyouts = Math.Max(0, _openFlyouts - 1);
    }

    private void ShowMenu(MenuFlyout menu, FrameworkElement target, RightTappedRoutedEventArgs e)
    {
        TrackFlyout(menu);
        menu.ShowAt(target, e.GetPosition(target));
        e.Handled = true;
    }


    private void Back_Click(object sender, RoutedEventArgs e) => _shell?.CurrentTab?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _shell?.CurrentTab?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => _shell?.CurrentTab?.Reload();
    private void Url_Click(object sender, RoutedEventArgs e) => PaletteRequested?.Invoke(this, PaletteMode.Navigate);
    private void NewTab_Click(object sender, RoutedEventArgs e) => PaletteRequested?.Invoke(this, PaletteMode.NewTab);
    private void Dock_Click(object sender, RoutedEventArgs e) => DockToggled?.Invoke(this, EventArgs.Empty);

    private async void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (_shell?.CurrentTab is not { } tab) return;
        var package = new DataPackage();
        package.SetText(tab.Url);
        Clipboard.SetContent(package);

        CopyLinkIcon.Glyph = ""; // a check mark for a moment
        await Task.Delay(1200);
        CopyLinkIcon.Glyph = "";
    }


    private void SiteFlyout_Opening(object sender, object e)
    {
        RefreshZoom();
        RefreshSiteBlocker();
    }


    private void RefreshSiteBlocker()
    {
        var tab = _shell?.CurrentTab;
        var site = tab is null ? "" : BeautyKit.Blocking.ContentBlocker.SiteOf(tab.Url);
        var enabled = BeautyKit.Blocking.ContentBlocker.Enabled;
        var on = enabled && site.Length > 0 && !BeautyKit.Blocking.ContentBlocker.IsSiteAllowed(tab!.Url);

        SiteBlockerRow.IsEnabled = enabled && site.Length > 0;
        SiteBlockerHint.Text = !enabled ? L.T("Off on every site (Extensions)", "Apagado en todos los sitios (Extensiones)")
            : site.Length == 0 ? L.T("Not used on this page", "No se usa en esta página")
            : on ? L.T($"{tab!.BlockedCount} blocked on this page", $"{tab!.BlockedCount} bloqueados en esta página")
            : L.T($"Off on {site}", $"Apagado en {site}");
        SiteBlockerState.Text = on ? L.T("On", "Activado") : L.T("Off", "Apagado");
        SiteBlockerState.Foreground = (Brush)Application.Current.Resources[on ? "BeautyAccentBrush" : "BeautyTextSecondaryBrush"];
        SiteBlockerIcon.Foreground = SiteBlockerState.Foreground;
    }

    private void SiteBlocker_Click(object sender, RoutedEventArgs e)
    {
        if (_shell?.CurrentTab is not { } tab) return;
        var allowed = BeautyKit.Blocking.ContentBlocker.IsSiteAllowed(tab.Url);
        BeautyKit.Blocking.ContentBlocker.SetSiteAllowed(tab.Url, !allowed);
        RefreshSiteBlocker();
        tab.Reload(); // the change shows right away
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { _shell?.CurrentTab?.ZoomIn(); RefreshZoom(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { _shell?.CurrentTab?.ZoomOut(); RefreshZoom(); }
    private void ZoomReset_Click(object sender, RoutedEventArgs e) { _shell?.CurrentTab?.ResetZoom(); RefreshZoom(); }
    private void RefreshZoom() => ZoomText.Text = $"{_shell?.CurrentTab?.ZoomPercent ?? 100} %";


    private const double FavoriteHeight = 44;
    private const double FavoriteGap = 8;
    private const int FavoriteColumns = 6;   // a 6-column grid: a tile spans 2 (three per row), 3 (two per row) or 6
    private const double DragThreshold = 6;
    private const double SwapHysteresis = 10; // the new slot must be clearly closer, so it does not jitter
    private static readonly TimeSpan FavoriteMove = TimeSpan.FromMilliseconds(220);

    private readonly List<FavoriteTile> _favoriteTiles = [];
    private FavoriteDrag? _favoriteDrag;
    private ImplicitAnimationCollection? _favoriteMoves;

    // The favorite just dropped flies to its slot; grabbing another before it lands finishes it at once.
    private Border? _dropGhost;
    private FavoriteTile? _dropTile;

    private sealed class FavoriteTile
    {
        public required FavoriteSite Site;
        public required Border Tile;
        public required CompositeTransform Transform;
        public required Ellipse OpenDot;
        public bool Hovered;
    }

    private sealed class FavoriteDrag
    {
        public required FavoriteTile Item;
        public required Point Start;        // where it was pressed, in grid coordinates
        public required Point StartOrigin;  // corner of its slot when the drag started
        public required Size Size;          // size when grabbed; it stays while dragging
        public required List<FavoriteSite> Order;
        public Border? Ghost;               // the copy that follows the mouse
        public bool Moving;
    }

    /// <summary>
    /// Slot of every favorite: rows of three (two when there are four), and an incomplete last row shares the width,
    /// so two favorites take half each and one takes it all.
    /// </summary>
    private static (int Row, int Column, int Span) FavoriteSlot(int index, int count)
    {
        var perRow = count == 4 ? 2 : 3;
        var row = index / perRow;
        var inRow = row < count / perRow ? perRow : count % perRow;
        var span = FavoriteColumns / inRow;
        return (row, index % perRow * span, span);
    }

    /// <summary>
    /// Builds the favorite tiles. When only the order changed, the existing tiles slide to their slots; when one was
    /// added or removed, they are rebuilt.
    /// </summary>
    private void BuildFavorites()
    {
        if (_shell is null) return;
        var items = _shell.Favorites.Items.ToList();
        if (items.Count == _favoriteTiles.Count && items.All(site => _favoriteTiles.Any(t => t.Site == site)))
        {
            if (_favoriteDrag is null) PlaceFavorites(items);
            RefreshActiveFavorite();
            return;
        }

        CancelFavoriteDrag();
        FinishDrop();
        FavoritesGrid.Children.Clear();
        _favoriteTiles.Clear();
        if (FavoritesGrid.ColumnDefinitions.Count != FavoriteColumns)
        {
            FavoritesGrid.ColumnDefinitions.Clear();
            for (var i = 0; i < FavoriteColumns; i++) FavoritesGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        foreach (var site in items)
        {
            var item = FavoriteTileFor(site);
            FavoritesGrid.Children.Add(item.Tile);
            _favoriteTiles.Add(item);
        }
        PlaceFavorites(items);
        FavoritesGrid.Visibility = _favoriteTiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshActiveFavorite();
    }

    private void PlaceFavorites(List<FavoriteSite> order)
    {
        var rows = order.Count == 0 ? 0 : FavoriteSlot(order.Count - 1, order.Count).Row + 1;
        while (FavoritesGrid.RowDefinitions.Count < rows) FavoritesGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FavoriteHeight) });
        while (FavoritesGrid.RowDefinitions.Count > rows) FavoritesGrid.RowDefinitions.RemoveAt(FavoritesGrid.RowDefinitions.Count - 1);

        foreach (var item in _favoriteTiles)
        {
            var (row, column, span) = FavoriteSlot(order.IndexOf(item.Site), order.Count);
            Grid.SetRow(item.Tile, row);
            Grid.SetColumn(item.Tile, column);
            Grid.SetColumnSpan(item.Tile, span);
        }
    }

    /// <summary>Corner, size and center of a slot, computed before the grid has rearranged.</summary>
    private (Point Origin, Size Size, Point Center) SlotGeometry(int index, int count)
    {
        var cell = (FavoritesGrid.ActualWidth - FavoriteGap * (FavoriteColumns - 1)) / FavoriteColumns;
        var (row, column, span) = FavoriteSlot(index, count);
        var origin = new Point(column * (cell + FavoriteGap), row * (FavoriteHeight + FavoriteGap));
        var width = span * cell + (span - 1) * FavoriteGap;
        return (origin, new Size(width, FavoriteHeight), new Point(origin.X + width / 2, origin.Y + FavoriteHeight / 2));
    }

    /// <summary>The tile itself: the favicon and, below, a dot when its tab is open but not in view.</summary>
    private static (Border Tile, Ellipse OpenDot) FavoriteVisual(FavoriteSite site)
    {
        // While loading (or when the site icon cannot be read) it shows a globe, never an empty square.
        var placeholder = new FontIcon
        {
            Glyph = "\uE774",
            FontSize = 16,
            Foreground = TileBrush("BeautyTextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var image = new Image
        {
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = new Grid { Children = { placeholder, image } };
        _ = LoadFavoriteIconAsync(site.FaviconUrl, image, placeholder);

        var dot = new Ellipse
        {
            Width = 4,
            Height = 4,
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Fill = TileBrush("BeautyTextSecondaryBrush"),
            Visibility = Visibility.Collapsed,
        };

        var tile = new Border
        {
            Height = FavoriteHeight,
            CornerRadius = (CornerRadius)Application.Current.Resources["BeautyItemCornerRadius"],
            Background = TileBrush("BeautyTileBrush"),
            BorderBrush = TileBrush("BeautyTileBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = new Grid { Children = { icon, dot } },
        };
        return (tile, dot);
    }

    private static async Task LoadFavoriteIconAsync(string? url, Image image, FrameworkElement placeholder)
    {
        if (await Favicons.LoadForAsync(url, image, image.Width) is not { } source) return;
        image.Source = source;
        placeholder.Visibility = Visibility.Collapsed;
    }

    private FavoriteTile FavoriteTileFor(FavoriteSite site)
    {
        var (tile, dot) = FavoriteVisual(site);
        var transform = new CompositeTransform();
        tile.RenderTransformOrigin = new Point(0.5, 0.5);
        tile.RenderTransform = transform;
        var item = new FavoriteTile { Site = site, Tile = tile, Transform = transform, OpenDot = dot };

        var name = string.IsNullOrWhiteSpace(site.Title) ? site.Host : site.Title;
        ToolTipService.SetToolTip(tile, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, name);

        // When the grid rearranges (while another tile is dragged) this one slides to its new slot. It is enabled after
        // the first layout, or it would fly in from the corner when it appears.
        tile.Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => AnimateLayoutMoves(tile));

        void Press(bool down) => transform.ScaleX = transform.ScaleY = down ? 0.95 : 1;

        tile.PointerEntered += (_, _) => { item.Hovered = true; PaintFavorite(item); };
        tile.PointerExited += (_, _) => { item.Hovered = false; if (_favoriteDrag is null) Press(false); PaintFavorite(item); };

        // Tap opens, middle click closes its tab, press and drag moves it.
        tile.PointerPressed += (_, e) =>
        {
            if (_shell is null) return;
            var properties = e.GetCurrentPoint(tile).Properties;
            if (properties.IsMiddleButtonPressed)
            {
                e.Handled = true;
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => CloseFavoriteTab(site)); // after the event (see TabItem_PointerPressed)
                return;
            }
            if (!properties.IsLeftButtonPressed) return;

            FinishDrop();
            var order = _shell.Favorites.Items.ToList();
            _favoriteDrag = new FavoriteDrag
            {
                Item = item,
                Start = e.GetCurrentPoint(FavoritesGrid).Position,
                StartOrigin = SlotGeometry(order.IndexOf(site), order.Count).Origin,
                Size = new Size(tile.ActualWidth, tile.ActualHeight),
                Order = order,
            };
            tile.CapturePointer(e.Pointer);
            Press(true);
            e.Handled = true;
        };
        tile.PointerMoved += (_, e) =>
        {
            if (_favoriteDrag is not { } drag || drag.Item != item) return;
            var p = e.GetCurrentPoint(FavoritesGrid).Position;
            var dx = p.X - drag.Start.X;
            var dy = p.Y - drag.Start.Y;
            if (!drag.Moving)
            {
                if (Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;
                StartFavoriteDrag(drag);
            }

            // The copy follows the mouse at the size it had when grabbed.
            var left = drag.StartOrigin.X + dx;
            var top = drag.StartOrigin.Y + dy;
            Canvas.SetLeft(drag.Ghost, left);
            Canvas.SetTop(drag.Ghost, top);

            // Which slot is its center over? It only moves when that slot is clearly closer than the current one.
            var center = new Point(left + drag.Size.Width / 2, top + drag.Size.Height / 2);
            var count = drag.Order.Count;
            double Distance(int index)
            {
                var c = SlotGeometry(index, count).Center;
                return Math.Sqrt(Math.Pow(c.X - center.X, 2) + Math.Pow(c.Y - center.Y, 2));
            }
            var current = drag.Order.IndexOf(site);
            var target = Enumerable.Range(0, count).MinBy(Distance);
            if (target != current && Distance(target) + SwapHysteresis < Distance(current))
            {
                drag.Order.Remove(site);
                drag.Order.Insert(target, site);
                PlaceFavorites(drag.Order); // the others slide (implicit animation)
            }
        };
        tile.PointerReleased += (_, e) =>
        {
            if (_favoriteDrag is not { } drag || drag.Item != item) return;
            _favoriteDrag = null;
            tile.ReleasePointerCapture(e.Pointer);
            Press(false);
            e.Handled = true;

            if (!drag.Moving)
            {
                _shell?.OpenFavorite(site);
                return;
            }
            DropFavorite(drag);
            _shell?.Favorites.Move(site, drag.Order.IndexOf(site)); // save the order (the tiles are already there)
        };
        tile.PointerCaptureLost += (_, _) =>
        {
            if (_favoriteDrag?.Item != item) return;
            Press(false);
            CancelFavoriteDrag();
        };

        tile.RightTapped += (_, e) =>
        {
            var menu = new MenuFlyout();
            void Add(string text, Action action)
            {
                var menuItem = new MenuFlyoutItem { Text = text };
                menuItem.Click += (_, _) => action();
                menu.Items.Add(menuItem);
            }
            Add(L.T("Open in new tab", "Abrir en pestaña nueva"), () => _shell?.NewTab(site.Url));
            Add(L.T("Copy link", "Copiar enlace"), () =>
            {
                var package = new DataPackage();
                package.SetText(site.Url);
                Clipboard.SetContent(package);
            });
            if (_shell?.FavoriteTabOf(site) is not null) Add(L.T("Close its tab", "Cerrar su pestaña"), () => CloseFavoriteTab(site));
            menu.Items.Add(new MenuFlyoutSeparator());
            Add(L.T("Remove from favorites", "Quitar de favoritos"), () => _shell?.Unpin(site.Url));
            ShowMenu(menu, tile, e);
        };
        return item;
    }

    /// <summary>Starts a drag: the grid keeps the gap and a lifted copy follows the mouse.</summary>
    private void StartFavoriteDrag(FavoriteDrag drag)
    {
        drag.Moving = true;
        var (ghost, _) = FavoriteVisual(drag.Item.Site);
        ghost.Width = drag.Size.Width;
        ghost.Height = drag.Size.Height;
        ghost.Background = drag.Item.Tile.Background;
        ghost.BorderBrush = drag.Item.Tile.BorderBrush;
        ghost.BorderThickness = drag.Item.Tile.BorderThickness;
        ghost.RenderTransformOrigin = new Point(0.5, 0.5);
        ghost.RenderTransform = new ScaleTransform { ScaleX = 1.05, ScaleY = 1.05 };
        ghost.Shadow = new ThemeShadow();
        ghost.Translation = new System.Numerics.Vector3(0, 0, 16);
        Canvas.SetLeft(ghost, drag.StartOrigin.X);
        Canvas.SetTop(ghost, drag.StartOrigin.Y);
        FavoritesDragLayer.Children.Add(ghost);
        drag.Ghost = ghost;
        drag.Item.Tile.Opacity = 0;
    }

    /// <summary>On release the copy flies to its new slot, taking its size, and the tile reappears there.</summary>
    private void DropFavorite(FavoriteDrag drag)
    {
        if (drag.Ghost is not { } ghost) return;
        _dropGhost = ghost;
        _dropTile = drag.Item;

        var target = SlotGeometry(drag.Order.IndexOf(drag.Item.Site), drag.Order.Count);
        var (fromLeft, fromTop) = (Canvas.GetLeft(ghost), Canvas.GetTop(ghost));
        var (fromWidth, fromHeight) = (ghost.Width, ghost.Height);
        var scale = (ScaleTransform)ghost.RenderTransform;
        Motion.Animate("favorite-drop", TimeSpan.FromMilliseconds(200), t =>
        {
            Canvas.SetLeft(ghost, Motion.Lerp(fromLeft, target.Origin.X, t));
            Canvas.SetTop(ghost, Motion.Lerp(fromTop, target.Origin.Y, t));
            ghost.Width = Motion.Lerp(fromWidth, target.Size.Width, t);
            ghost.Height = Motion.Lerp(fromHeight, target.Size.Height, t);
            scale.ScaleX = scale.ScaleY = Motion.Lerp(1.05, 1, t);
        }, completed: FinishDrop);
    }

    /// <summary>Finishes the drop animation at once, if one is running.</summary>
    private void FinishDrop()
    {
        Motion.Cancel("favorite-drop");
        if (_dropGhost is not null) FavoritesDragLayer.Children.Remove(_dropGhost);
        if (_dropTile is not null) _dropTile.Tile.Opacity = 1;
        _dropGhost = null;
        _dropTile = null;
    }

    /// <summary>The mouse was lost mid-drag: everything goes back.</summary>
    private void CancelFavoriteDrag()
    {
        if (_favoriteDrag is not { } drag) return;
        _favoriteDrag = null;
        if (drag.Ghost is not null) FavoritesDragLayer.Children.Remove(drag.Ghost);
        drag.Item.Tile.Opacity = 1;
        drag.Item.Transform.ScaleX = drag.Item.Transform.ScaleY = 1;
        if (_shell is not null) PlaceFavorites(_shell.Favorites.Items.ToList());
    }

    /// <summary>Implicit animation: when layout moves the tile, the GPU slides it instead of jumping.</summary>
    private void AnimateLayoutMoves(Border tile)
    {
        var visual = ElementCompositionPreview.GetElementVisual(tile);
        if (_favoriteMoves is null)
        {
            var c = visual.Compositor;
            var offset = c.CreateVector3KeyFrameAnimation();
            offset.Target = "Offset";
            offset.InsertExpressionKeyFrame(1, "this.FinalValue",
                c.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.2f, 0.9f), new System.Numerics.Vector2(0.25f, 1f)));
            offset.Duration = FavoriteMove;
            _favoriteMoves = c.CreateImplicitAnimationCollection();
            _favoriteMoves["Offset"] = offset;
        }
        visual.ImplicitAnimations = _favoriteMoves;
    }

    /// <summary>Middle click (or the menu) on a favorite closes its tab; the favorite stays.</summary>
    private void CloseFavoriteTab(FavoriteSite site)
    {
        if (_shell?.FavoriteTabOf(site) is { } tab) _shell.CloseTab(tab);
    }

    /// <summary>
    /// The favorite in view is a white card with a border; those with their tab open in the background get a dot.
    /// </summary>
    private void RefreshActiveFavorite()
    {
        foreach (var item in _favoriteTiles) PaintFavorite(item);
    }

    private void PaintFavorite(FavoriteTile item)
    {
        var active = IsActive(item.Site);
        item.Tile.Background = TileBrush(active ? "BeautyTileActiveBrush" : item.Hovered ? "BeautyTileHoverBrush" : "BeautyTileBrush");
        item.Tile.BorderBrush = TileBrush(active ? "BeautyTileActiveBorderBrush" : "BeautyTileBorderBrush");
        // The border grows inward (Padding compensates) so the icon does not jump when selected.
        item.Tile.BorderThickness = new Thickness(active ? 1.5 : 1);
        item.Tile.Padding = new Thickness(active ? 0 : 0.5);
        item.OpenDot.Visibility = !active && _shell?.FavoriteTabOf(item.Site) is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsActive(FavoriteSite site) =>
        _shell?.CurrentTab is { } tab && _shell.FavoriteTabOf(site) == tab;

    private static Brush TileBrush(string key) => (Brush)Application.Current.Resources[key];



    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _shell is null) return;
        TabHovered?.Invoke(this, (null, null)); // once selected, the preview is no longer needed
        PlaceTabIndicator(animate: true);
        if (TabList.SelectedItem is BrowserTab tab) _shell.SelectTab(tab);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab) _shell?.CloseTab(tab);
    }

    /// <summary>
    /// The mouse entered a tab in the list (or left it: tab and row are null). The window shows the preview next to
    /// the sidebar.
    /// </summary>
    public event EventHandler<(BrowserTab? Tab, FrameworkElement? Row)>? TabHovered;

    // The close X appears while hovering the tab and, if the mouse stays, so does the preview.
    private void TabItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetCloseVisible(sender, true);
        if (sender is FrameworkElement { DataContext: BrowserTab tab } row) TabHovered?.Invoke(this, (tab, row));
    }

    private void TabItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetCloseVisible(sender, false);
        TabHovered?.Invoke(this, (null, null));
    }

    // Middle click closes on release (like Chrome) and after the event: removing the row while WinUI is still processing
    // the press crashes the app (0xc0000005).
    private BrowserTab? _middlePressedTab;

    private void TabItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrowserTab tab } row) return;
        if (!e.GetCurrentPoint(row).Properties.IsMiddleButtonPressed) return;
        e.Handled = true;
        _middlePressedTab = tab;
    }

    private void TabItem_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_shell is null || sender is not FrameworkElement { DataContext: BrowserTab tab } row) return;
        if (e.GetCurrentPoint(row).Properties.PointerUpdateKind != Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased) return;
        e.Handled = true;
        var pressed = _middlePressedTab;
        _middlePressedTab = null;
        if (pressed != tab) return; // pressed on one tab and released on another: close nothing
        TabHovered?.Invoke(this, (null, null));
        var shell = _shell;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => shell.CloseTab(tab));
    }

    private static void SetCloseVisible(object sender, bool visible)
    {
        if (sender is Grid { Children.Count: > 0 } grid && grid.Children[^1] is Button close)
            close.Opacity = visible ? 1 : 0;
    }

    private void TabItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_shell is null || (sender as FrameworkElement)?.DataContext is not BrowserTab tab) return;

        var menu = new MenuFlyout();
        void Add(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (_shell.Favorites.Contains(tab.Url)) Add(L.T("Remove from favorites", "Quitar de favoritos"), () => _shell.Unpin(tab.Url));
        else Add(L.T("Add to favorites", "Agregar a favoritos"), () => _shell.Pin(tab));
        Add(L.T("Duplicate tab", "Duplicar pestaña"), () => _shell.DuplicateTab(tab));
        Add(L.T("Copy link", "Copiar enlace"), () =>
        {
            var package = new DataPackage();
            package.SetText(tab.Url);
            Clipboard.SetContent(package);
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(L.T("Close tab", "Cerrar pestaña"), () => _shell.CloseTab(tab));

        ShowMenu(menu, (FrameworkElement)sender, e);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _shell?.ClearSpace();


    private void AddSpace_Click(object sender, RoutedEventArgs e) => NewSpaceRequested?.Invoke(this, EventArgs.Empty);
    private void Library_Click(object sender, RoutedEventArgs e) => LibraryRequested?.Invoke(this, LibrarySection.History);
    private void Downloads_Click(object sender, RoutedEventArgs e) => LibraryRequested?.Invoke(this, LibrarySection.Downloads);

    private void UrlBar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        UrlBarContainer.Background = (Brush)Application.Current.Resources["BeautySubtleFillHoverBrush"];
    }

    private void UrlBar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        UrlBarContainer.Background = (Brush)Application.Current.Resources["BeautySubtleFillBrush"];
    }
}
