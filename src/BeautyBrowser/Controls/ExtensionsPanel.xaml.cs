using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using BeautyBrowser.Shell;
using BeautyKit;
using BeautyKit.Blocking;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace BeautyBrowser.Controls;

/// <summary>
/// Extensions: for now, the built-in ad blocker (uBlock Origin lists and scriptlets), with a global and a per-site
/// switch, how much it blocked and the lists with their last update.
/// </summary>
public sealed partial class ExtensionsPanel : UserControl
{
    private BrowserShell? _shell;
    private BrowserTab? _tab;
    private readonly DispatcherQueueTimer _clock;

    // Parts updated in place.
    private Action? _paintEnabled;
    private Action? _paintSite;
    private TextBlock _pageCount = null!;
    private TextBlock _sessionCount = null!;
    private TextBlock _siteTitle = null!;
    private TextBlock _siteHint = null!;
    private Button _siteRow = null!;
    private TextBlock _listsSummary = null!;
    private TextBlock _updated = null!;
    private TextBlock _updateAction = null!;
    private StackPanel _allowedHost = null!;
    private TextBlock _allowedLabel = null!;
    private Border _allowedGroup = null!;

    public ExtensionsPanel()
    {
        InitializeComponent();
        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => RefreshCounts();
        Loaded += (_, _) => { ContentBlocker.StatusChanged += Blocker_StatusChanged; Refresh(); };
        Unloaded += (_, _) => { ContentBlocker.StatusChanged -= Blocker_StatusChanged; _clock.Stop(); };
        WatchVisibility();
    }

    public void Attach(BrowserShell shell)
    {
        _shell = shell;
        Build();
        shell.CurrentTabChanged += (_, _) => WatchTab(shell.CurrentTab);
        WatchTab(shell.CurrentTab);
    }

    private void WatchVisibility() =>
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible) { Refresh(); _clock.Start(); }
            else _clock.Stop();
        });

    private void WatchTab(BrowserTab? tab)
    {
        if (_tab is not null) _tab.PropertyChanged -= Tab_PropertyChanged;
        _tab = tab;
        if (_tab is not null) _tab.PropertyChanged += Tab_PropertyChanged;
        Refresh();
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserTab.Url)) Refresh();
        else if (e.PropertyName is nameof(BrowserTab.BlockedCount)) RefreshCounts();
    }

    private void Blocker_StatusChanged(object? sender, EventArgs e) => Refresh();


    private void Build()
    {
        Host.Children.Clear();

        Host.Children.Add(Label(L.T("Installed", "Instaladas")));
        var card = new StackPanel();
        Host.Children.Add(Group(card));

        // Header: icon, name and the global switch.
        var (toggle, paint) = Toggle(() => ContentBlocker.Enabled);
        _paintEnabled = paint;
        var header = Row(new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children =
            {
                IconTile(""),
                At(Texts(L.T("Ad blocker", "Bloqueador de anuncios"), L.T("uBlock Origin lists and scriptlets · built in", "Listas y scriptlets de uBlock Origin · integrado"), bold: true), 1),
                At(toggle, 2),
            },
        }, minHeight: 60);
        header.Click += (_, _) =>
        {
            ContentBlocker.Enabled = !ContentBlocker.Enabled;
            Refresh();
        };
        card.Children.Add(header);
        card.Children.Add(Divider());

        _pageCount = Big();
        _sessionCount = Big();
        card.Children.Add(new Grid
        {
            Padding = new Thickness(14, 10, 14, 12),
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() },
            Children =
            {
                Stat(_pageCount, L.T("on this page", "en esta página")),
                At(Stat(_sessionCount, L.T("since the browser opened", "desde que abriste el navegador")), 1),
            },
        });
        card.Children.Add(Divider());

        _siteTitle = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        _siteHint = Hint("");
        var (siteToggle, paintSite) = Toggle(() => _tab is { } t && !ContentBlocker.IsSiteAllowed(t.Url));
        _paintSite = paintSite;
        _siteRow = Row(new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children =
            {
                new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { _siteTitle, _siteHint } },
                At(siteToggle, 1),
            },
        }, minHeight: 50);
        _siteRow.Click += (_, _) =>
        {
            if (_tab is null) return;
            ContentBlocker.SetSiteAllowed(_tab.Url, !ContentBlocker.IsSiteAllowed(_tab.Url));
            _tab.Reload(); // the change shows right away
            Refresh();
        };
        card.Children.Add(_siteRow);
        card.Children.Add(Divider());

        _listsSummary = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        _updated = Hint("");
        _updateAction = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("BeautyAccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var updateRow = Row(new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children =
            {
                new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { _listsSummary, _updated } },
                At(_updateAction, 1),
            },
        }, minHeight: 50);
        updateRow.Click += async (_, _) => await ContentBlocker.UpdateAsync();
        card.Children.Add(updateRow);

        Host.Children.Add(Label(L.T("Filter lists", "Listas de filtros")));
        var lists = new StackPanel { Padding = new Thickness(12, 8, 12, 10), Spacing = 6 };
        foreach (var title in ContentBlocker.ListTitles)
        {
            lists.Children.Add(new Grid
            {
                ColumnSpacing = 10,
                ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 11, Foreground = Brush("BeautyAccentBrush") },
                    At(new TextBlock { Text = title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis }, 1),
                },
            });
        }
        Host.Children.Add(Group(lists));

        _allowedLabel = Label(L.T("Sites without blocking", "Sitios sin bloqueo"));
        _allowedHost = new StackPanel();
        _allowedGroup = Group(_allowedHost);
        Host.Children.Add(_allowedLabel);
        Host.Children.Add(_allowedGroup);

        Host.Children.Add(new TextBlock
        {
            Margin = new Thickness(10, 16, 10, 0),
            FontSize = 11,
            Foreground = Brush("BeautyTextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Text = L.T("Blocks ads, trackers, ad popups and dangerous sites. The lists update themselves every few days.",
                "Bloquea anuncios, rastreadores, ventanas emergentes de publicidad y sitios peligrosos. Las listas se actualizan solas cada pocos días."),
        });

        Refresh();
    }


    private void Refresh()
    {
        if (_shell is null || _paintEnabled is null) return;
        _paintEnabled();
        _paintSite?.Invoke();

        var site = _tab is null ? "" : ContentBlocker.SiteOf(_tab.Url);
        var hasSite = site.Length > 0;
        _siteRow.IsEnabled = hasSite && ContentBlocker.Enabled;
        _siteRow.Opacity = _siteRow.IsEnabled ? 1 : 0.5;
        _siteTitle.Text = hasSite ? L.T($"Block on {site}", $"Bloquear en {site}") : L.T("Block on this site", "Bloquear en este sitio");
        _siteHint.Text = !hasSite ? L.T("Open a page to choose", "Abrí una página para elegir")
            : ContentBlocker.IsSiteAllowed(_tab!.Url) ? L.T("Off on this site: its ads show", "Apagado en este sitio: se ven sus anuncios")
            : L.T("If a site misbehaves, turn it off just there", "Si un sitio no anda bien, apagalo solo ahí");

        _listsSummary.Text = ContentBlocker.FilterCount > 0
            ? L.T($"{ContentBlocker.ListTitles.Count()} lists · {N(ContentBlocker.FilterCount)} filters · {N(ContentBlocker.ScriptletCount)} scriptlets",
                $"{ContentBlocker.ListTitles.Count()} listas · {N(ContentBlocker.FilterCount)} filtros · {N(ContentBlocker.ScriptletCount)} scriptlets")
            : L.T("The lists have not been downloaded yet", "Todavía no se descargaron las listas");
        _updated.Text = ContentBlocker.IsUpdating ? L.T("Downloading lists and scriptlets…", "Descargando listas y scriptlets…")
            : ContentBlocker.LastError is { } error ? L.T("Could not update: ", "No se pudo actualizar: ") + error
            : ContentBlocker.LastUpdated is { } when ? L.T("Updated ", "Actualizado ") + Ago(when)
            : L.T("They download themselves once you are online", "Se descargan solas al conectarte");
        _updateAction.Text = ContentBlocker.IsUpdating ? "" : L.T("Update", "Actualizar");

        _allowedHost.Children.Clear();
        foreach (var allowed in ContentBlocker.AllowedSites.OrderBy(s => s))
        {
            var row = Row(new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
                Children =
                {
                    new TextBlock { Text = allowed, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                    At(new TextBlock { Text = L.T("Block again", "Volver a bloquear"), FontSize = 12, Foreground = Brush("BeautyAccentBrush"), VerticalAlignment = VerticalAlignment.Center }, 1),
                },
            }, minHeight: 40);
            row.Click += (_, _) => { ContentBlocker.SetSiteAllowed("https://" + allowed + "/", false); Refresh(); };
            _allowedHost.Children.Add(row);
        }
        var anyAllowed = ContentBlocker.AllowedSites.Count > 0;
        _allowedLabel.Visibility = anyAllowed ? Visibility.Visible : Visibility.Collapsed;
        _allowedGroup.Visibility = anyAllowed ? Visibility.Visible : Visibility.Collapsed;

        RefreshCounts();
    }

    private void RefreshCounts()
    {
        if (_shell is null) return;
        _pageCount.Text = N(_tab?.BlockedCount ?? 0);
        _sessionCount.Text = N(ContentBlocker.BlockedTotal);
    }

    private static string N(long value) => value.ToString("#,0", CultureInfo.CurrentCulture);

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return L.T("just now", "recién");
        if (span.TotalHours < 1) return L.T($"{(int)span.TotalMinutes} min ago", $"hace {(int)span.TotalMinutes} min");
        if (span.TotalDays < 1) return L.T($"{(int)span.TotalHours} h ago", $"hace {(int)span.TotalHours} h");
        return span.TotalDays < 2 ? L.T("yesterday", "ayer") : L.T($"{(int)span.TotalDays} days ago", $"hace {(int)span.TotalDays} días");
    }


    private static TextBlock Label(string text) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["BeautySectionLabel"] };

    private static Border Group(UIElement child) => new()
    {
        Padding = new Thickness(2),
        Background = Brush("BeautySubtleFillBrush"),
        CornerRadius = new CornerRadius(10),
        Child = child,
    };

    private static Button Row(UIElement content, double minHeight) => new()
    {
        Style = (Style)Application.Current.Resources["BeautyListRowButton"],
        MinHeight = minHeight,
        Padding = new Thickness(12, 7, 12, 7),
        Content = content,
    };

    private static Rectangle Divider() =>
        new() { Height = 1, Margin = new Thickness(12, 0, 10, 0), Fill = Brush("BeautyDividerBrush") };

    private static StackPanel Texts(string title, string hint, bool bold) => new()
    {
        Spacing = 1,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new TextBlock { Text = title, FontSize = 13.5, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal },
            Hint(hint),
        },
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Brush("BeautyTextSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock Big() => new() { FontSize = 22, FontWeight = FontWeights.SemiBold };

    private static StackPanel Stat(TextBlock number, string caption) => new()
    {
        Spacing = 0,
        Children = { number, Hint(caption) },
    };

    private static Border IconTile(string glyph) => new()
    {
        Width = 36,
        Height = 36,
        CornerRadius = new CornerRadius(9),
        Background = Brush("BeautyAccentBrush"),
        Child = new FontIcon { Glyph = glyph, FontSize = 17, Foreground = new SolidColorBrush(Colors.White) },
    };

    private static T At<T>(T element, int column) where T : FrameworkElement
    {
        Grid.SetColumn(element, column);
        return element;
    }

    /// <summary>A custom switch, like the ones in settings: a pill with a sliding knob.</summary>
    private static (Border Track, Action Paint) Toggle(Func<bool> isOn)
    {
        var knob = new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(3, 0, 0, 0),
            TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) },
        };
        var track = new Border
        {
            Width = 36,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(150) },
            Child = knob,
        };
        void Paint()
        {
            var on = isOn();
            track.Background = on ? Brush("BeautyAccentBrush") : new SolidColorBrush(ColorHelper.FromArgb(0x33, 0, 0, 0));
            knob.Translation = new Vector3(on ? 16 : 0, 0, 0);
        }
        Paint();
        return (track, Paint);
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
