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
using Windows.Foundation;

namespace BeautyBrowser.Controls;

/// <summary>
/// The "tabs on top" layout (Settings → Sidebar and tabs): a star that opens the favorites in a panel, the open
/// favorites as small icon tabs, then the space's tabs, which share the strip's width like Chrome's, and a toolbar
/// with the address below. Tabs grow in when they open, shrink away when they close and the rest slide to make room.
/// </summary>
public sealed partial class TopBar : UserControl
{
    private const double MaxTabWidth = 220;
    private const double MinTabWidth = 28;
    private const double CompactBelow = 84;   // narrower than this, a tab shows its icon only
    private const double FreeDragWidth = 40;  // always left free before the window buttons, to drag the window by
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
        // Too many tabs never spill over the + and the window buttons: what does not fit is cut.
        TabsViewport.SizeChanged += (_, e) => TabsViewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
    }

    public event EventHandler<PaletteMode>? PaletteRequested;
    public event EventHandler<LibrarySection>? LibraryRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? NewSpaceRequested;

    /// <summary>The free part of the tab strip, which drags the window.</summary>
    public FrameworkElement DragArea => DragZone;

    /// <summary>The address bar's place relative to <paramref name="root"/>, for the palette to open over it.</summary>
    public Rect AddressBounds(UIElement root)
    {
        var origin = AddressBar.TransformToVisual(root).TransformPoint(new Point(0, 0));
        return new Rect(origin.X, origin.Y, AddressBar.ActualWidth, AddressBar.ActualHeight);
    }

    /// <summary>The address bar's box, which the palette searches from.</summary>
    public TextBox AddressInput => AddressBox;

    /// <summary>Typing in the address bar started (it got the focus) or ended.</summary>
    public event EventHandler? AddressFocused;
    public event EventHandler? AddressBlurred;

    private string? _addressText; // what to put in the box when it gets the focus (null: the current address)
    private bool _editing;

    /// <summary>Puts the cursor in the address bar with this text (Ctrl+T empty, Ctrl+L the address, selected).</summary>
    public void FocusAddress(string text)
    {
        _addressText = text;
        if (AddressHasFocus)
        {
            ShowEditingText();
            AddressFocused?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _editing = false; // whatever a late LostFocus left behind: the box gets the focus from scratch
            AddressBox.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Whether the cursor is in the address bar right now.</summary>
    public bool AddressHasFocus => XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) == AddressBox;

    private void AddressBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_editing) return;
        _editing = true;
        AddressBar.BorderBrush = Res("BeautyAccentBrush");
        ShowEditingText();
        AddressFocused?.Invoke(this, EventArgs.Empty);
    }

    private void AddressBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _editing = false;
        AddressBar.BorderBrush = Res("BeautyTileBorderBrush");
        _addressText = null;
        AddressBlurred?.Invoke(this, EventArgs.Empty);
        RefreshNavigation(); // back to the short address
    }

    /// <summary>While typing: the full address, readable and selected, so typing replaces it.</summary>
    private void ShowEditingText()
    {
        var url = _watchedTab?.Url;
        var text = _addressText ?? (StartPage.Is(url) || StartPage.IsSettings(url) ? "" : AddressDisplay.ToDisplay(url ?? ""));
        _addressText = null;
        AddressBox.Text = text;
        AddressBox.SelectAll();
        UrlSearchIcon.Visibility = Visibility.Visible;
        UrlSecureIcon.Visibility = UrlInsecureIcon.Visibility = Visibility.Collapsed;
    }

    /// <summary>Where the download animation lands.</summary>
    public FrameworkElement LibraryTarget => DownloadsButton;

    public void Attach(BrowserShell shell)
    {
        _shell = shell;
        SitePanel.Attach(shell);
        shell.CurrentSpaceChanged += (_, _) => QueueSync(reset: true);
        shell.CurrentTabChanged += (_, _) => { QueueSync(); WatchCurrentTab(); };
        shell.Favorites.Items.CollectionChanged += (_, _) => { QueueSync(); RefreshFavorite(); };
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
        // The space keeps its newest tab first (the sidebar shows it on top); here it goes last, on the right, as in
        // Chrome.
        wanted.AddRange(_space.Reverse());

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
        if (_flash is { } flash && _items.TryGetValue(flash, out var flashed))
        {
            _flash = null;
            Flash(flashed);
        }
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
                       + NewTabButton.Width + NewTabButton.Margin.Left + Strip.ColumnDefinitions[4].Width.Value + FreeDragWidth;
        var viewport = Math.Max(0, Strip.ActualWidth - reserved);
        TabsViewport.MaxWidth = viewport;
        var available = Strip.ActualWidth - reserved - pinned * (PinnedTabWidth + TabGap);
        var tabWidth = regular == 0 ? MaxTabWidth : Math.Clamp(available / regular - TabGap, MinTabWidth, MaxTabWidth);
        foreach (var item in _items.Values) SetCompact(item, tabWidth < CompactBelow);

        var plan = Tabs.Children.OfType<Grid>().Select(item =>
        {
            var closing = _closing.Contains(item);
            var from = double.IsNaN(item.Width) ? item.ActualWidth : item.Width;
            var to = closing ? 0 : item.Tag is FavoriteSite ? PinnedTabWidth : tabWidth;
            return (Item: item, From: from, To: to, FromOpacity: item.Opacity, ToOpacity: closing ? 0.0 : 1.0);
        }).ToList();

        KeepCurrentInView(plan.Select(step => (step.Item, step.To)).ToList(), viewport);

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

    private double _scroll; // how far the tabs are slid left inside their window

    /// <summary>
    /// When the tabs are wider than the window they run in, they slide so the current one is whole in view (and not
    /// further than the last one's edge).
    /// </summary>
    private void KeepCurrentInView(List<(Grid Item, double Width)> plan, double viewport)
    {
        var total = plan.Sum(step => step.Width) + Math.Max(0, plan.Count - 1) * TabGap;
        if (total <= viewport) _scroll = 0;
        else
        {
            var x = 0.0;
            foreach (var (item, width) in plan)
            {
                if (IsCurrent(item))
                {
                    if (x + width > _scroll + viewport) _scroll = x + width - viewport;
                    if (x < _scroll) _scroll = x;
                    break;
                }
                x += width + TabGap;
            }
            _scroll = Math.Clamp(_scroll, 0, total - viewport);
        }
        Tabs.Margin = new Thickness(-_scroll, 0, 0, 0);
    }

    /// <summary>Narrow tabs (many open) show only the icon, centered; the close button waits for a wider tab.</summary>
    private BrowserTab? _flash;

    /// <summary>A tab opened in the background: it glows for a moment once it is in the strip.</summary>
    public void FlashTab(BrowserTab tab)
    {
        _flash = tab;
        QueueSync();
    }

    private void Flash(Grid item)
    {
        var card = CardOf(item);
        var accent = Res("BeautyAccentBrush") is SolidColorBrush brush ? brush.Color : Windows.UI.Color.FromArgb(255, 60, 120, 240);
        var transition = card.BackgroundTransition;
        card.BackgroundTransition = null; // the glow is drawn frame by frame
        Motion.Animate("topbar-flash-" + item.GetHashCode(), TimeSpan.FromMilliseconds(1200),
            t => card.Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(120 * Math.Abs(Math.Sin(2 * Math.PI * t))), accent.R, accent.G, accent.B)),
            ease: t => t,
            completed: () =>
            {
                card.BackgroundTransition = transition;
                Style(item);
            });
    }

    private readonly HashSet<Grid> _compact = [];

    private void UpdateAudio(Grid item, BrowserTab tab)
    {
        var content = ContentOf(item);
        if (content.Children.Count < 4 || content.Children[2] is not Button speaker) return;
        var show = tab.Media?.IsPlaying == true || tab.IsAudioMuted;
        speaker.Visibility = show && !_compact.Contains(item) ? Visibility.Visible : Visibility.Collapsed;
        ((FontIcon)speaker.Content).Glyph = tab.IsAudioMuted ? "\uE74F" : "\uE767";
        ToolTipService.SetToolTip(speaker, tab.IsAudioMuted ? L.T("Unmute", "Activar sonido") : L.T("Mute tab", "Silenciar pestaña"));
    }

    private void SetCompact(Grid item, bool compact)
    {
        if (item.Tag is not BrowserTab tab) return;
        if (compact) _compact.Add(item); else _compact.Remove(item);
        var content = ContentOf(item);
        content.ColumnSpacing = compact ? 0 : 8;
        content.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        CardOf(item).Padding = compact ? new Thickness(0) : new Thickness(10, 0, 4, 0);
        content.Children[1].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        content.Children[^1].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        UpdateAudio(item, tab);
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
        if (e.PropertyName is nameof(BrowserTab.Media) or nameof(BrowserTab.IsAudioMuted))
        {
            if (sender is BrowserTab playing && _items.TryGetValue(playing, out var tabItem)) UpdateAudio(tabItem, playing);
            return;
        }
        if (e.PropertyName is not (nameof(BrowserTab.Title) or nameof(BrowserTab.Favicon) or nameof(BrowserTab.IsLoading))) return;
        if (sender is BrowserTab tab && _items.TryGetValue(tab, out var item)) FillTab(item, tab);
    }

    private bool IsCurrent(Grid item) => _shell?.CurrentTab is { } current && item.Tag switch
    {
        BrowserTab tab => tab == current,
        FavoriteSite site => _shell.FavoriteTabOf(site) == current,
        _ => false,
    };

    // An item is a transparent hit target as tall as the strip (so the top edge of the window still lands on it)
    // holding the visible tab: a card whose content is a grid (icon, title, close).
    private static Border CardOf(Grid item) => (Border)item.Children[0];
    private static Grid ContentOf(Grid item) => (Grid)CardOf(item).Child;

    /// <summary>The current tab is a white card; the rest are plain, with a light fill under the mouse.</summary>
    private void Style(Grid item)
    {
        var current = IsCurrent(item);
        var hovered = item == _hovered;
        var card = CardOf(item);
        card.Background = current ? Res("BeautyCardBrush")
            : hovered ? Res("BeautySubtleFillBrush")
            : Res("BeautyTransparentBrush");
        card.BorderBrush = current ? Res("BeautyFillLowBrush") : Res("BeautyTransparentBrush");
        if (item.Tag is BrowserTab && ContentOf(item).Children[^1] is Button close) close.Opacity = current || hovered ? 1 : 0;
    }

    private Grid NewItem(object key, double padLeft)
    {
        var content = new Grid { ColumnSpacing = 8 };
        var card = new Border
        {
            Height = 32,
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(padLeft, 0, 4, 0),
            CornerRadius = (CornerRadius)Application.Current.Resources["BeautyItemCornerRadius"],
            BorderThickness = new Thickness(1),
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(140) },
            Child = content,
        };
        var item = new Grid
        {
            Tag = key,
            Height = 40,
            Background = Res("BeautyTransparentBrush"),
            Children = { card },
        };
        item.PointerEntered += (_, _) => { _hovered = item; Style(item); };
        item.PointerExited += (_, _) => { if (_hovered == item) _hovered = null; Style(item); };
        return item;
    }

    private Grid TabItem(BrowserTab tab)
    {
        var item = NewItem(tab, 10);
        var content = ContentOf(item);
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // sound
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // close

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
        Grid.SetColumn(close, 3);
        // Sound: a small speaker next to the title while the tab plays (or is muted); a tap mutes or unmutes it.
        var speaker = new Button
        {
            Style = (Style)Application.Current.Resources["BeautyTabCloseButton"],
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Content = new FontIcon { Glyph = "\uE767", FontSize = 11 },
        };
        speaker.Click += (_, _) => tab.SetAudioMuted(!tab.IsAudioMuted);
        Grid.SetColumn(speaker, 2);
        content.Children.Add(new Grid { VerticalAlignment = VerticalAlignment.Center }); // icon (FillTab)
        content.Children.Add(title);
        content.Children.Add(speaker);
        content.Children.Add(close);
        FillTab(item, tab);
        UpdateAudio(item, tab);

        item.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(item).Properties;
            if (point.IsMiddleButtonPressed) { e.Handled = true; _middlePressed = tab; }
            else if (point.IsLeftButtonPressed)
            {
                _shell?.SelectTab(tab);
                _drag = new TabDrag(item, tab, e.GetCurrentPoint(Tabs).Position.X);
            }
        };
        item.PointerMoved += (_, e) => DragMove(item, e);
        item.PointerCaptureLost += (_, _) => DragEnd(item);
        item.PointerReleased += (_, e) =>
        {
            if (_drag?.Item == item) { DragEnd(item); return; }
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

    // ---- dragging a tab ----

    private sealed class TabDrag(Grid item, BrowserTab tab, double startX)
    {
        public Grid Item { get; } = item;
        public BrowserTab Tab { get; } = tab;
        public double StartX { get; } = startX;  // where it was pressed, in strip coordinates
        public double Grab { get; set; }         // pointer to the tab's left edge
        public bool Moving { get; set; }
    }

    private const double DragThreshold = 6;
    private TabDrag? _drag;

    /// <summary>
    /// Past a few pixels the tab follows the mouse along the strip; crossing a neighbor's middle swaps them, and the
    /// neighbor slides into the freed place.
    /// </summary>
    private void DragMove(Grid item, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || drag.Item != item) return;
        if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) { DragEnd(item); return; } // released out of sight
        var x = e.GetCurrentPoint(Tabs).Position.X;
        if (!drag.Moving)
        {
            if (Math.Abs(x - drag.StartX) < DragThreshold) return;
            if (!item.CapturePointer(e.Pointer)) return;
            drag.Moving = true;
            drag.Grab = drag.StartX - item.ActualOffset.X;
            Canvas.SetZIndex(item, 1);
        }

        var left = x - drag.Grab;
        var regular = Tabs.Children.OfType<Grid>().Where(c => c.Tag is BrowserTab && !_closing.Contains(c)).ToList();
        // It stays within the run of normal tabs: never over the favorites, the + or the window buttons.
        if (regular.Count > 0)
        {
            var min = regular[0].ActualOffset.X;
            var max = Math.Min(regular[^1].ActualOffset.X + regular[^1].ActualWidth, Tabs.ActualWidth) - item.ActualWidth;
            left = Math.Clamp(left, min, Math.Max(min, max));
        }
        // Crossing a neighbor's middle swaps; a fast move can cross several.
        for (var guard = 0; guard < regular.Count; guard++)
        {
            regular = Tabs.Children.OfType<Grid>().Where(c => c.Tag is BrowserTab && !_closing.Contains(c)).ToList();
            var index = regular.IndexOf(item);
            var center = left + item.ActualWidth / 2;
            if (index > 0 && center < regular[index - 1].ActualOffset.X + regular[index - 1].ActualWidth / 2)
                Swap(item, regular[index - 1]);
            else if (index < regular.Count - 1 && center > regular[index + 1].ActualOffset.X + regular[index + 1].ActualWidth / 2)
                Swap(item, regular[index + 1]);
            else break;
        }

        item.Translation = new System.Numerics.Vector3((float)(left - item.ActualOffset.X), 0, 0);
    }

    /// <summary>Swaps the dragged tab with a neighbor; the neighbor glides from where it was.</summary>
    private void Swap(Grid item, Grid neighbor)
    {
        var neighborBefore = neighbor.ActualOffset.X;
        var from = Tabs.Children.IndexOf(item);
        var to = Tabs.Children.IndexOf(neighbor);
        Tabs.Children.Move((uint)from, (uint)to);
        Tabs.UpdateLayout();
        var shift = (float)(neighborBefore - neighbor.ActualOffset.X);
        Motion.Animate("topbar-swap-" + neighbor.GetHashCode(), TimeSpan.FromMilliseconds(160),
            t => neighbor.Translation = new System.Numerics.Vector3((float)Motion.Lerp(shift, 0, t), 0, 0));
    }

    /// <summary>Dropped: the tab settles in its slot and the space takes the new order.</summary>
    private void DragEnd(Grid item)
    {
        if (_drag is not { } drag || drag.Item != item) return;
        _drag = null;
        if (!drag.Moving) return;
        item.ReleasePointerCaptures();
        var offset = item.Translation.X;
        Motion.Animate("topbar-drop", TimeSpan.FromMilliseconds(160),
            t => item.Translation = new System.Numerics.Vector3((float)Motion.Lerp(offset, 0, t), 0, 0),
            completed: () => Canvas.SetZIndex(item, 0));

        if (_space is null) return;
        // The strip shows the space backwards (newest on the right).
        var shown = Tabs.Children.OfType<Grid>().Where(c => c.Tag is BrowserTab && !_closing.Contains(c))
            .Select(c => (BrowserTab)c.Tag).ToList();
        var target = shown.Count - 1 - shown.IndexOf(drag.Tab);
        var current = _space.IndexOf(drag.Tab);
        if (current >= 0 && target >= 0 && target < _space.Count && current != target) _space.Move(current, target);
    }

    private static void FillTab(Grid item, BrowserTab tab)
    {
        var slot = (Grid)ContentOf(item).Children[0];
        slot.Children.Clear();
        if (tab.IsLoading && tab.Favicon is null)
            slot.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true });
        else if (tab.Favicon is { } icon)
            slot.Children.Add(new Image { Width = 16, Height = 16, Source = icon });
        else
            slot.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = Res("BeautyTextSecondaryBrush") });
        // Internal pages by their name (a restored tab that has not loaded yet only knows its address).
        var text = StartPage.Is(tab.Url) || StartPage.IsSettings(tab.Url) || string.IsNullOrEmpty(tab.Title)
            ? Sidebar.ShortUrl(tab.Url)
            : tab.Title;
        ((TextBlock)ContentOf(item).Children[1]).Text = text;
        ToolTipService.SetToolTip(item, text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, text);
    }

    /// <summary>An open favorite: a small icon-only tab, like Chrome's pinned tabs. Middle click closes it.</summary>
    private Grid FavoriteTab(FavoriteSite site)
    {
        var item = NewItem(site, 0);
        CardOf(item).Padding = new Thickness(0);
        ContentOf(item).Children.Add(FavoriteIcon(site, 16));
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
        Add(L.T("Reload", "Volver a cargar"), () => tab.Reload());
        Add(tab.IsAudioMuted ? L.T("Unmute tab", "Activar sonido") : L.T("Mute tab", "Silenciar pestaña"), () => tab.SetAudioMuted(!tab.IsAudioMuted));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(L.T("Close tab", "Cerrar pestaña"), () => shell.CloseTab(tab));
        // "Right" is the strip's right (the visual order), not the space's.
        var shown = Tabs.Children.OfType<Grid>().Where(c => c.Tag is BrowserTab && !_closing.Contains(c)).Select(c => (BrowserTab)c.Tag).ToList();
        var others = shown.Where(t => t != tab).ToList();
        var toTheRight = shown.Skip(shown.IndexOf(tab) + 1).ToList();
        if (others.Count > 0) Add(L.T("Close other tabs", "Cerrar las demás pestañas"), () => others.ForEach(shell.CloseTab));
        if (toTheRight.Count > 0) Add(L.T("Close tabs to the right", "Cerrar las pestañas de la derecha"), () => toTheRight.ForEach(shell.CloseTab));
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
        RefreshZoomBadge();
        RefreshFavorite();
        if (_editing) return; // the user is typing: the box is theirs
        var url = tab?.Url;
        AddressBox.Text = StartPage.Is(url) ? "" : Sidebar.ShortUrl(url);
        UrlSearchIcon.Visibility = StartPage.Is(url) ? Visibility.Visible : Visibility.Collapsed;
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

    private void RefreshZoomBadge()
    {
        var percent = _watchedTab?.ZoomPercent ?? BrowserTab.DefaultZoomPercent;
        ZoomBadgeText.Text = $"{percent} %";
        ZoomBadge.Visibility = percent == BrowserTab.DefaultZoomPercent ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The star: filled when the page is a favorite; hidden on internal pages, which are not worth keeping.</summary>
    private void RefreshFavorite()
    {
        var url = _watchedTab?.Url;
        var usable = !string.IsNullOrEmpty(url) && !StartPage.Is(url) && !StartPage.IsSettings(url);
        FavoriteButton.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        var favorite = usable && _shell?.Favorites.Contains(url!) == true;
        FavoriteStarIcon.Glyph = favorite ? "\uE735" : "\uE734";
        FavoriteStarIcon.Foreground = favorite ? Res("BeautyAccentBrush") : Res("BeautyTextBrush");
        ToolTipService.SetToolTip(FavoriteButton, favorite
            ? L.T("Remove from favorites", "Quitar de favoritos")
            : L.T("Add to favorites", "Agregar a favoritos"));
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_shell is not { } shell || shell.CurrentTab is not { } tab) return;
        if (shell.Favorites.Contains(tab.Url)) shell.Unpin(tab.Url);
        else shell.Pin(tab);
        RefreshFavorite();
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => _shell?.CurrentTab?.ResetZoom();

    private void SiteFlyout_Opening(object sender, object e) => SitePanel.Refresh();

    private void Back_Click(object sender, RoutedEventArgs e) => _shell?.CurrentTab?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _shell?.CurrentTab?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => _shell?.CurrentTab?.Reload();
    private void NewTab_Click(object sender, RoutedEventArgs e) => PaletteRequested?.Invoke(this, PaletteMode.NewTab);
    private void Library_Click(object sender, RoutedEventArgs e) => LibraryRequested?.Invoke(this, LibrarySection.History);
    private void Downloads_Click(object sender, RoutedEventArgs e) => LibraryRequested?.Invoke(this, LibrarySection.Downloads);
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private static Brush Res(string key) =>
        key == "BeautyTransparentBrush" ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : (Brush)Application.Current.Resources[key];
}
