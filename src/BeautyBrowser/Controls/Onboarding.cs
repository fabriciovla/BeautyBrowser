using System.Numerics;
using BeautyBrowser.Shell;
using BeautyKit;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace BeautyBrowser.Controls;

/// <summary>
/// Onboarding: the first-run setup inside the browser (the installer only installs). It covers the window with the
/// theme color and a two-pane card: a short title and the button on the left, the choice on the right, with a
/// miniature of our window whose sidebar changes live. Steps: default browser, favorites, theme (color and sand) and
/// how the sidebar appears.
/// </summary>
public sealed partial class Onboarding : UserControl
{
    private const int Steps = 4;
    private static readonly Color Ink = Color.FromArgb(255, 0x1F, 0x1F, 0x24);
    private static readonly Color Muted = Color.FromArgb(255, 0x8A, 0x8A, 0x94);
    private static readonly Color LogoViolet = Color.FromArgb(255, 0x6B, 0x4F, 0xD0);

    /// <summary>The sites usual browsers suggest.</summary>
    private static readonly (string Title, string Url, string Icon)[] Sites =
    [
        ("YouTube", "https://www.youtube.com/", "https://www.youtube.com/favicon.ico"),
        ("Gmail", "https://mail.google.com/", "https://ssl.gstatic.com/ui/v1/icons/mail/rfr/gmail.ico"),
        ("WhatsApp", "https://web.whatsapp.com/", "https://web.whatsapp.com/favicon.ico"),
        ("ChatGPT", "https://chatgpt.com/", "https://chatgpt.com/cdn/assets/favicon-48x48.7d42b2e65f.png"),
        ("Calendar", "https://calendar.google.com/", "https://ssl.gstatic.com/calendar/images/dynamiclogo_2020q4/calendar_31_2x.png"),
        ("Drive", "https://drive.google.com/", "https://ssl.gstatic.com/images/branding/product/2x/drive_2020q4_32dp.png"),
        ("Claude", "https://claude.ai/", "https://claude.ai/favicon.ico"),
        ("Notion", "https://www.notion.so/", "https://www.notion.so/images/favicon.ico"),
        ("Spotify", "https://open.spotify.com/", "https://open.spotify.com/favicon.ico"),
        ("Netflix", "https://www.netflix.com/", "https://assets.nflxext.com/us/ffe/siteui/common/icons/nficon2016.png"),
        ("Figma", "https://www.figma.com/", "https://static.figma.com/app/icon/1/favicon.png"),
        ("X", "https://x.com/", "https://abs.twimg.com/favicons/twitter.3.ico"),
        ("Reddit", "https://www.reddit.com/", "https://www.redditstatic.com/shreddit/assets/favicon/192x192.png"),
        ("GitHub", "https://github.com/", "https://github.githubassets.com/favicons/favicon.png"),
        ("Wikipedia", L.T("https://en.wikipedia.org/", "https://es.wikipedia.org/"), "https://es.wikipedia.org/static/apple-touch/wikipedia.png"),
    ];

    private readonly BrowserShell _shell;
    private readonly bool[] _chosen = new bool[Sites.Length];
    private readonly bool[] _already = new bool[Sites.Length];   // already among the favorites

    private readonly Grid _root = new();
    private readonly Border _backdrop = new();
    private readonly Canvas _blobs = new() { IsHitTestVisible = false };
    private readonly Grid _card = new();
    private readonly Button _back;
    private readonly TextBlock _title = new();
    private readonly TextBlock _body = new();
    private readonly Button _primary;
    private readonly TextBlock _primaryText = new();
    private readonly Button _skip;
    private readonly TextBlock _skipText = new();
    private readonly Canvas _dots = new() { Width = 3 * DotStep + 16, Height = 6 };
    private readonly Border _dotPill = new()
    {
        Width = 16,
        Height = 6,
        CornerRadius = new CornerRadius(3),
        Background = new SolidColorBrush(Ink),
        TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(360) },
    };
    private const double DotStep = 14;   // from one dot to the next
    private int _transition;             // a step change in progress goes stale when another arrives
    private readonly List<UIElement> _cascade = [];   // what comes in one by one (cards, colors)
    private readonly Grid _right = new();
    private readonly StackPanel _texts = new() { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

    private int _step = -1;
    private bool _defaultDone, _closing;
    private DispatcherQueueTimer? _defaultWatch;
    private Grid? _mockFavorites;      // the miniature's favorites grid (step 2)
    private Border? _mockWindow, _mockSidebar;   // the miniature that gets tinted (step 3)

    /// <summary>Finished (or skipped); the window removes it.</summary>
    public event EventHandler? Finished;

    public Onboarding(BrowserShell shell)
    {
        _shell = shell;
        var favorites = shell.Favorites.Items;
        for (var i = 0; i < Sites.Length; i++)
        {
            _already[i] = favorites.Any(f => SameHost(f.Url, Sites[i].Url));
            _chosen[i] = _already[i] || (favorites.Count == 0 && i < 4);
        }

        // Background: the theme color with light blobs drifting slowly.
        _root.Children.Add(_backdrop);
        _blobs.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(1100) };
        _root.Children.Add(_blobs);
        _root.Children.Add(new NoiseLayer());
        _root.SizeChanged += (_, _) => LayoutBlobs();

        _card.MaxWidth = 1000;
        _card.MaxHeight = 600;
        _card.Margin = new Thickness(48);
        _card.HorizontalAlignment = HorizontalAlignment.Stretch;
        _card.VerticalAlignment = VerticalAlignment.Stretch;
        _card.CornerRadius = new CornerRadius(24);
        _card.Background = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255));
        _card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 255, 255, 255));
        _card.BorderThickness = new Thickness(1);
        _card.SizeChanged += (_, e) => _card.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        _card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        _card.ColumnDefinitions.Add(new ColumnDefinition());

        // Left pane: a white card resting inside the glass.
        _back = TextButton(L.T("Back", "Atrás"), "OnboardingBack", out var backText, glyph: "");
        backText.FontSize = 12.5;
        _back.HorizontalAlignment = HorizontalAlignment.Left;
        _back.Margin = new Thickness(-8, 0, 0, 0);
        _back.Click += (_, _) => GoTo(_step - 1);

        _title.FontSize = 34;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = new SolidColorBrush(Ink);
        _title.TextWrapping = TextWrapping.Wrap;
        _title.LineHeight = 38;
        _title.CharacterSpacing = -18;
        _body.FontSize = 14;
        _body.Foreground = new SolidColorBrush(Muted);
        _body.TextWrapping = TextWrapping.Wrap;
        _texts.Children.Add(_title);
        _texts.Children.Add(_body);

        _primaryText.FontSize = 14;
        _primaryText.FontWeight = FontWeights.SemiBold;
        _primaryText.Foreground = new SolidColorBrush(Colors.White);
        _primary = new Button
        {
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = _primaryText,
        };
        AutomationProperties.SetAutomationId(_primary, "OnboardingNext");
        PaintPrimary();
        _primary.Click += (_, _) => Primary();

        _skip = TextButton("", "OnboardingSkip", out var skipText);
        _skipText = skipText;
        _skip.HorizontalAlignment = HorizontalAlignment.Center;
        _skip.Click += (_, _) => Skip();

        for (var i = 0; i < Steps; i++)
        {
            var dot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(0x2E, 0, 0, 0)),
            };
            Canvas.SetLeft(dot, 5 + i * DotStep);
            _dots.Children.Add(dot);
        }
        _dots.Children.Add(_dotPill);
        _dots.HorizontalAlignment = HorizontalAlignment.Left;
        _dots.VerticalAlignment = VerticalAlignment.Center;

        var top = new Grid { Height = 32 };
        top.Children.Add(_dots);
        _back.HorizontalAlignment = HorizontalAlignment.Right;
        _back.Margin = new Thickness(0, 0, -8, 0);
        top.Children.Add(_back);

        var bottom = new StackPanel { Spacing = 6 };
        bottom.Children.Add(_primary);
        bottom.Children.Add(_skip);

        var left = new Grid { RowSpacing = 0 };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition());
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.Children.Add(top);
        Grid.SetRow(_texts, 1);
        left.Children.Add(_texts);
        Grid.SetRow(bottom, 2);
        left.Children.Add(bottom);

        _card.Children.Add(new Border
        {
            Margin = new Thickness(10),
            Padding = new Thickness(28, 20, 28, 16),
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Colors.White),
            Child = left,
        });

        _right.Margin = new Thickness(22, 26, 30, 26);
        Grid.SetColumn(_right, 1);
        _card.Children.Add(_right);
        _root.Children.Add(_card);

        Content = _root;
        PaintBackdrop();
        AppTheme.Changed += OnThemeChanged;
        Loaded += (_, _) =>
        {
            LayoutBlobs();
            AnimateCardIn();
            GoTo(0);
        };
        Unloaded += (_, _) =>
        {
            AppTheme.Changed -= OnThemeChanged;
            _defaultWatch?.Stop();
        };
    }


    private void GoTo(int index)
    {
        if (_closing) return;
        if (index >= Steps)
        {
            Finish();
            return;
        }
        index = Math.Max(0, index);
        var forward = index >= _step;
        if (_step == 0 && index != 0) _defaultWatch?.Stop();
        _waitingDefault = false;
        _step = index;

        string title, body, primary, skip;
        FrameworkElement content;
        switch (index)
        {
            case 0:
                _defaultDone = DefaultBrowser.IsDefault;
                title = L.T("Your browser.", "Tu navegador.");
                body = _defaultDone ? L.T("It is already the default.", "Ya es el predeterminado.") : L.T("Open every link here.", "Que todos tus links se abran acá.");
                primary = _defaultDone ? L.T("Next", "Siguiente") : L.T("Make default", "Usar como predeterminado");
                skip = _defaultDone ? "" : L.T("Not now", "Ahora no");
                content = Centered(MockWindow(logoOnPage: true));
                break;
            case 1:
                title = L.T("Your sites.", "Tus sitios.");
                body = L.T("On top of everything, one click away.", "Arriba de todo, a un clic.");
                primary = L.T("Next", "Siguiente");
                skip = L.T("Skip", "Saltar");
                content = FavoritesContent();
                break;
            case 2:
                title = L.T("Your style.", "Tu estilo.");
                body = L.T("Color and sand.", "Color y arena.");
                primary = L.T("Next", "Siguiente");
                skip = "";
                content = ThemeContent();
                break;
            default:
                title = L.T("Ready.", "Listo.");
                body = L.T("Move the mouse to the left.", "Llevá el mouse a la izquierda.");
                primary = L.T("Start", "Empezar");
                skip = "";
                content = Centered(MockWindow(animateSidebar: true));
                break;
        }

        // The indicator slides and the background advances; the buttons update right away.
        _dotPill.Translation = new Vector3((float)(index * DotStep), 0, 0);
        _blobs.Translation = new Vector3(-90f * index, -24f * index, 0);
        _back.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;

        var direction = forward ? 1f : -1f;
        var version = ++_transition;
        var first = _right.Children.Count == 0;
        var cascade = _cascade.ToList();
        _cascade.Clear();

        if (!first)
        {
            // Out: the content goes to the opposite side shrinking, the text rises and fades.
            foreach (var old in _right.Children.ToList()) Leave(old, new Vector3(-direction * 40, 0, 0), 0.96f, 200);
            Leave(_texts, new Vector3(0, -10, 0), 1, 160);
            _primaryText.Opacity = 0;
            _skip.Opacity = 0;
        }

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            if (!first) await Task.Delay(190);
            if (version != _transition || _closing) return;

            _title.Text = title;
            _body.Text = body;
            _primaryText.Text = primary;
            _skipText.Text = skip;
            _skip.Visibility = skip.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _right.Children.Clear();
            _right.Children.Add(content);

            // In: the title rises, then the body; the content arrives from the side and its children one by one.
            _texts.Opacity = 1;
            _texts.Translation = Vector3.Zero;
            Arrive(_title, new Vector3(0, 18, 0), 1, 0, 460);
            Arrive(_body, new Vector3(0, 14, 0), 1, 70, 460);
            Arrive(_primaryText, Vector3.Zero, 1, 120, 300);
            Arrive(_skip, Vector3.Zero, 1, 160, 300);
            Arrive(content, new Vector3(direction * 56, 0, 0), 0.97f, 40, 520);
            for (var i = 0; i < cascade.Count; i++)
                Arrive(cascade[i], new Vector3(0, 12, 0), 0.86f, 120 + i * 22, 380);
        });
    }

    private void Primary()
    {
        if (_step == 0 && !_defaultDone)
        {
            MakeDefault();
            return;
        }
        GoTo(_step + 1);
    }

    private void Skip()
    {
        if (_step == 0 && _waitingDefault)
        {
            DefaultBrowser.OpenSettings();   // "Open again"
            return;
        }
        GoTo(_step + 1);
    }


    private void MakeDefault()
    {
        DefaultBrowser.OpenSettings();
        _body.Text = L.T("Choose it in Windows and come back.", "Elegilo en Windows y volvé.");
        _primaryText.Text = L.T("Next", "Siguiente");
        _skipText.Text = L.T("Open again", "Abrir de nuevo");
        _skip.Visibility = Visibility.Visible;
        _waitingDefault = true;

        // Windows does not notify, so check every now and then whether it was chosen.
        _defaultWatch?.Stop();
        _defaultWatch = DispatcherQueue.CreateTimer();
        _defaultWatch.Interval = TimeSpan.FromMilliseconds(800);
        _defaultWatch.Tick += (_, _) =>
        {
            if (_step != 0 || !DefaultBrowser.IsDefault) return;
            _defaultWatch?.Stop();
            _defaultDone = true;
            _waitingDefault = false;
            _body.Text = L.T("It is already the default.", "Ya es el predeterminado.");
            _skip.Visibility = Visibility.Collapsed;
        };
        _defaultWatch.Start();
        // The button turns into "Next": a second tap moves on even if it was not chosen.
        _defaultDone = true;
    }

    private bool _waitingDefault;


    private FrameworkElement FavoritesContent()
    {
        var tiles = new Grid { ColumnSpacing = 10, RowSpacing = 10, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < 3; i++) tiles.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < Sites.Length; i++)
        {
            if (i % 3 == 0) tiles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });
            var tile = SiteTile(i);
            _cascade.Add(tile);
            Grid.SetRow(tile, i / 3);
            Grid.SetColumn(tile, i % 3);
            tiles.Children.Add(tile);
        }

        // On the left, our sidebar: the picked sites appear in its favorites grid.
        var sidebar = SidebarMock(out _mockFavorites, width: 140, height: 440);
        sidebar.VerticalAlignment = VerticalAlignment.Center;
        RefreshMockFavorites();

        var grid = new Grid { ColumnSpacing = 18 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(sidebar);
        Grid.SetColumn(tiles, 1);
        grid.Children.Add(tiles);
        return grid;
    }

    private FrameworkElement SiteTile(int index)
    {
        var (title, _, iconUrl) = Sites[index];
        var icon = new Image { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        var letter = new TextBlock
        {
            Text = title[..1],
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Ink),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var iconHost = new Grid { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        iconHost.Children.Add(letter);
        iconHost.Children.Add(icon);
        _ = LoadIconAsync(iconUrl, icon, letter);

        var name = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // The check mark, in the corner.
        var check = new Border
        {
            Width = 18,
            Height = 18,
            Margin = new Thickness(0, 7, 7, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Ink),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            CenterPoint = new Vector3(9, 9, 0),
            ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) },
            Child = new FontIcon { Glyph = "\uE73E", FontSize = 10, Foreground = new SolidColorBrush(Colors.White) },
        };

        var column = new StackPanel { Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        iconHost.HorizontalAlignment = HorizontalAlignment.Center;
        column.Children.Add(iconHost);
        column.Children.Add(name);
        var layers = new Grid();
        layers.Children.Add(column);
        layers.Children.Add(check);

        var tile = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(140) },
            Child = layers,
        };
        AutomationProperties.SetAutomationId(tile, "Site_" + title);
        AutomationProperties.SetName(tile, title);

        var hovered = false;
        void Paint()
        {
            var on = _chosen[index];
            tile.Background = new SolidColorBrush(on ? Colors.White : Color.FromArgb((byte)(hovered ? 0x4D : 0x2E), 255, 255, 255));
            tile.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(on ? 0xFF : 0x4D), 255, 255, 255));
            name.Foreground = new SolidColorBrush(on ? Ink : Colors.White);
            check.Scale = on ? Vector3.One : Vector3.Zero;
        }
        Paint();
        tile.PointerEntered += (_, _) => { hovered = true; Paint(); };
        tile.PointerExited += (_, _) => { hovered = false; Paint(); };
        tile.Tapped += (_, e) =>
        {
            e.Handled = true;
            _chosen[index] = !_chosen[index];
            Paint();
            RefreshMockFavorites();
        };
        return tile;
    }

    private static async Task LoadIconAsync(string url, Image image, UIElement fallback)
    {
        if (await Favicons.LoadForAsync(url, image, image.Width) is not { } source) return;
        image.Source = source;
        fallback.Visibility = Visibility.Collapsed;
    }

    /// <summary>The picked sites in the miniature's favorites grid, three per row like the real one.</summary>
    private void RefreshMockFavorites()
    {
        if (_mockFavorites is not { } grid) return;
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        var picked = Enumerable.Range(0, Sites.Length).Where(i => _chosen[i]).Take(9).ToList();
        for (var n = 0; n < picked.Count; n++)
        {
            if (n % 3 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            var icon = new Image { Width = 12, Height = 12 };
            _ = LoadIconAsync(Sites[picked[n]].Icon, icon, new Border());
            var tile = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(0xD9, 255, 255, 255)),
                Child = icon,
            };
            Grid.SetRow(tile, n / 3);
            Grid.SetColumn(tile, n % 3);
            grid.Children.Add(tile);
        }
    }


    private FrameworkElement ThemeContent()
    {
        var settings = _shell.Settings;
        var mock = MockWindow(withNoise: true);

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        void FillSwatches() => ThemeSwatches.Fill(swatches, settings.ThemeKey, key =>
        {
            settings.ThemeKey = key;   // applies live (AppTheme.Changed repaints the background and the miniature)
            FillSwatches();
        }, size: 30, ring: Colors.White);
        FillSwatches();
        foreach (var swatch in swatches.Children) _cascade.Add(swatch);

        // Sand: how much grain the glass has.
        var slider = new BeautySlider { Width = 220, Value = settings.GlassGrain };
        slider.ValueChanged += (_, value) => NoiseLayer.Configure("sand", value / 100, settings.GlassSoftness / 100.0);
        slider.ValueCommitted += (_, value) =>
        {
            settings.GlassTexture = "sand";
            settings.GlassGrain = (int)value;
        };
        var sand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
        sand.Children.Add(new TextBlock
        {
            Text = L.T("Sand", "Arena"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
        });
        sand.Children.Add(slider);

        var panel = new StackPanel { Spacing = 20, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(mock);
        panel.Children.Add(swatches);
        panel.Children.Add(sand);
        return panel;
    }

    private void OnThemeChanged()
    {
        PaintBackdrop();
        PaintPrimary();
        PaintMock();
    }


    /// <summary>
    /// A small BeautyBrowser window: the glass frame, the sidebar with its favorites and tabs, and the page. With
    /// <paramref name="animateSidebar"/> the sidebar floats and slides in and out with the cursor, like the real one.
    /// </summary>
    private FrameworkElement MockWindow(bool logoOnPage = false, bool animateSidebar = false, bool withNoise = false)
    {
        const double width = 420, height = 264;

        var page = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Colors.White) };
        if (logoOnPage)
        {
            page.Child = new Image
            {
                Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "BeautyBrowser.png"))),
                Width = 72,
                Height = 72,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            var lines = new StackPanel { Margin = new Thickness(26, 26, 26, 0), Spacing = 10 };
            foreach (var (w, h, alpha) in new[] { (120.0, 12.0, 0x2E), (230.0, 7.0, 0x17), (200.0, 7.0, 0x17), (215.0, 7.0, 0x17), (150.0, 7.0, 0x17) })
                lines.Children.Add(new Border
                {
                    Width = w,
                    Height = h,
                    CornerRadius = new CornerRadius(h / 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = new SolidColorBrush(Color.FromArgb((byte)alpha, 0, 0, 0)),
                });
            page.Child = lines;
        }

        var sidebar = SidebarMock(out var favorites, width: 118, height: height - 12);
        FillDemoFavorites(favorites);

        var window = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var inner = new Grid { Padding = new Thickness(6) };
        if (animateSidebar)
        {
            // Floating: the page fills everything and the sidebar slides over it when the cursor touches the edge.
            inner.Children.Add(page);
            sidebar.HorizontalAlignment = HorizontalAlignment.Left;
            sidebar.Margin = new Thickness(-6, -6, 0, -6);
            sidebar.Height = height;
            inner.Children.Add(sidebar);
            var cursor = new FontIcon
            {
                Glyph = "",
                FontSize = 18,
                Foreground = new SolidColorBrush(Ink),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 120, 0, 0),
            };
            inner.Children.Add(cursor);
            window.Loaded += (_, _) => AnimateSidebarDemo(sidebar, cursor);
        }
        else
        {
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.ColumnDefinitions.Add(new ColumnDefinition());
            inner.ColumnSpacing = 6;
            sidebar.Margin = new Thickness(-6, -6, 0, -6);
            sidebar.Height = height;
            inner.Children.Add(sidebar);
            Grid.SetColumn(page, 1);
            inner.Children.Add(page);
        }
        window.Child = inner;

        _mockWindow = window;
        _mockSidebar = sidebar;
        if (withNoise)
        {
            var host = new Grid { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Center };
            host.Children.Add(window);
            host.Children.Add(new Border   // the sand, only over the miniature's sidebar
            {
                Width = 118,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(12, 0, 0, 12),
                Child = new NoiseLayer(),
            });
            PaintMock();
            return host;
        }
        PaintMock();
        return window;
    }

    private void PaintMock()
    {
        if (_mockWindow is null || _mockSidebar is null) return;
        var neutral = Color.FromArgb(255, 0xE6, 0xE7, 0xEE);
        var tint = AppTheme.Current.Color;
        _mockWindow.Background = new SolidColorBrush(tint is { } c ? AppTheme.Mix(neutral, c, 0.42) : neutral);
        var panel = Color.FromArgb(255, 0xF4, 0xF4, 0xF7);
        _mockSidebar.Background = new SolidColorBrush(tint is { } s ? AppTheme.Mix(panel, s, 0.26) : panel);
    }

    /// <summary>
    /// Our sidebar, small: the top buttons, the address, the favorites grid, the space and the tabs (the active one
    /// on its white card).
    /// </summary>
    private static Border SidebarMock(out Grid favorites, double width, double height)
    {
        static Border Bar(double w, double h, byte alpha, HorizontalAlignment align = HorizontalAlignment.Left) => new()
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(h / 2),
            HorizontalAlignment = align,
            Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)),
        };

        var content = new StackPanel { Margin = new Thickness(9, 10, 9, 0), Spacing = 7 };

        // Top: library and pin on the left; back, forward and reload on the right.
        var top = new Grid { Height = 8 };
        var topLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        topLeft.Children.Add(Bar(8, 8, 0x38));
        topLeft.Children.Add(Bar(8, 8, 0x38));
        var topRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < 3; i++) topRight.Children.Add(Bar(8, 8, 0x24));
        top.Children.Add(topLeft);
        top.Children.Add(topRight);
        content.Children.Add(top);

        content.Children.Add(new Border
        {
            Height = 18,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0, 0, 0)),
            Padding = new Thickness(7, 0, 0, 0),
            Child = Bar(48, 5, 0x40),
        });

        favorites = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (var i = 0; i < 3; i++) favorites.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(favorites);

        var space = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(2, 3, 0, 0) };
        space.Children.Add(new FontIcon { Glyph = "", FontSize = 8, Foreground = new SolidColorBrush(Muted) });
        space.Children.Add(Bar(30, 5, 0x40));
        content.Children.Add(space);
        content.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0)) });

        var tabs = new StackPanel { Spacing = 3 };
        for (var i = 0; i < 4; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(2.5),
                Background = new SolidColorBrush(Color.FromArgb((byte)(i == 0 ? 0x73 : 0x40), 0, 0, 0)),
            });
            row.Children.Add(Bar(i == 0 ? 56 : 44 + (i * 7 % 14), 5, (byte)(i == 0 ? 0x66 : 0x33)));
            tabs.Children.Add(new Border
            {
                Height = 20,
                Padding = new Thickness(7, 0, 0, 0),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(i == 0 ? Colors.White : Colors.Transparent),
                BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(i == 0 ? 0x14 : 0), 0, 0, 0)),
                BorderThickness = new Thickness(1),
                Child = row,
            });
        }
        content.Children.Add(tabs);

        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(12, 8, 8, 12),
            Background = new SolidColorBrush(Color.FromArgb(255, 0xF4, 0xF4, 0xF7)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = content,
        };
    }

    /// <summary>Sample favorites for the miniature: the user's when there are any, otherwise three tiles.</summary>
    private void FillDemoFavorites(Grid grid)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        var icons = _shell.Favorites.Items.Select(f => f.FaviconUrl).Take(3).ToList();
        for (var i = 0; i < 3; i++)
        {
            var tile = new Border { CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromArgb(0xD9, 255, 255, 255)) };
            if (i < icons.Count)
            {
                var icon = new Image { Width = 10, Height = 10 };
                _ = LoadIconAsync(icons[i] ?? "", icon, new Border());
                tile.Child = icon;
            }
            Grid.SetColumn(tile, i);
            grid.Children.Add(tile);
        }
    }

    /// <summary>The cursor goes to the edge and the sidebar comes in; it returns to the page and the sidebar leaves. Forever.</summary>
    private static void AnimateSidebarDemo(UIElement sidebar, UIElement cursor)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(sidebar, true);
        ElementCompositionPreview.SetIsTranslationEnabled(cursor, true);
        var side = ElementCompositionPreview.GetElementVisual(sidebar);
        var mouse = ElementCompositionPreview.GetElementVisual(cursor);
        var c = side.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.25f, 1f));
        var duration = TimeSpan.FromMilliseconds(4200);

        var slide = c.CreateScalarKeyFrameAnimation();
        slide.InsertKeyFrame(0, -130);
        slide.InsertKeyFrame(0.30f, -130);
        slide.InsertKeyFrame(0.40f, 0, ease);
        slide.InsertKeyFrame(0.70f, 0);
        slide.InsertKeyFrame(0.80f, -130, ease);
        slide.InsertKeyFrame(1, -130);
        slide.Duration = duration;
        slide.IterationBehavior = AnimationIterationBehavior.Forever;
        side.StartAnimation("Translation.X", slide);

        var move = c.CreateScalarKeyFrameAnimation();
        move.InsertKeyFrame(0, 250);
        move.InsertKeyFrame(0.15f, 250);
        move.InsertKeyFrame(0.30f, 2, ease);
        move.InsertKeyFrame(0.62f, 2);
        move.InsertKeyFrame(0.78f, 250, ease);
        move.InsertKeyFrame(1, 250);
        move.Duration = duration;
        move.IterationBehavior = AnimationIterationBehavior.Forever;
        mouse.StartAnimation("Translation.X", move);
    }

    private static FrameworkElement Centered(FrameworkElement element)
    {
        element.VerticalAlignment = VerticalAlignment.Center;
        element.HorizontalAlignment = HorizontalAlignment.Center;
        return element;
    }


    private void Finish()
    {
        if (_closing) return;
        _closing = true;
        _defaultWatch?.Stop();

        // The picked sites go to the favorites grid, skipping those already there.
        for (var i = 0; i < Sites.Length; i++)
        {
            if (_chosen[i] && !_already[i]) _shell.Favorites.Add(Sites[i].Url, Sites[i].Title, Sites[i].Icon);
            // It was a favorite and got unchecked: remove it (its open tab goes back to the list).
            if (!_chosen[i] && _already[i])
                foreach (var site in _shell.Favorites.Items.Where(f => SameHost(f.Url, Sites[i].Url)).ToList())
                    _shell.Unpin(site.Url);
        }
        _shell.Settings.OnboardingDone = true;

        IsHitTestVisible = false;
        _card.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(300) };
        OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(300) };
        _card.Scale = new Vector3(1.05f, 1.05f, 1);
        Opacity = 0;
        _ = Task.Delay(320).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => Finished?.Invoke(this, EventArgs.Empty)));
    }


    private Color ThemeColor => AppTheme.Current.Color ?? LogoViolet;

    private void PaintBackdrop()
    {
        var color = ThemeColor;
        _backdrop.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = AppTheme.Mix(color, Colors.Black, 0.18), Offset = 0 },
                new GradientStop { Color = color, Offset = 0.55 },
                new GradientStop { Color = AppTheme.Mix(color, Colors.White, 0.28), Offset = 1 },
            },
        };
    }

    private void PaintPrimary()
    {
        var brush = new SolidColorBrush(Ink);
        _primary.Background = brush;
        _primary.Resources["ButtonBackground"] = brush;
        _primary.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 0x38, 0x38, 0x40));
        _primary.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(255, 0x4A, 0x4A, 0x52));
    }

    /// <summary>Three huge, soft light blobs that drift slowly across the background.</summary>
    private void LayoutBlobs()
    {
        if (_root.ActualWidth <= 0 || _blobs.Children.Count > 0) return;
        var spots = new (double X, double Y, double Size, byte Alpha, float Dx, float Dy, double Seconds)[]
        {
            (0.10, 0.05, 620, 0x59, 90, 60, 13),
            (0.60, 0.45, 760, 0x40, -110, 50, 17),
            (0.30, 0.65, 520, 0x33, 70, -80, 15),
        };
        foreach (var (x, y, size, alpha, dx, dy, seconds) in spots)
        {
            var blob = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(alpha, 255, 255, 255), Offset = 0 },
                        new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 1 },
                    },
                },
            };
            Canvas.SetLeft(blob, _root.ActualWidth * x - size / 4);
            Canvas.SetTop(blob, _root.ActualHeight * y - size / 4);
            _blobs.Children.Add(blob);

            ElementCompositionPreview.SetIsTranslationEnabled(blob, true);
            var visual = ElementCompositionPreview.GetElementVisual(blob);   // the blob, not its container (which uses transitions)
            var drift = visual.Compositor.CreateVector3KeyFrameAnimation();
            drift.InsertKeyFrame(0, Vector3.Zero);
            drift.InsertKeyFrame(0.5f, new Vector3(dx, dy, 0));
            drift.InsertKeyFrame(1, Vector3.Zero);
            drift.Duration = TimeSpan.FromSeconds(seconds);
            drift.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Translation", drift);
        }
    }

    private void AnimateCardIn()
    {
        _card.CenterPoint = new Vector3((float)_card.ActualWidth / 2, (float)_card.ActualHeight / 2, 0);
        _card.Opacity = 0;
        _card.Scale = new Vector3(0.94f, 0.94f, 1);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _card.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(380) };
            _card.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(480) };
            _card.Opacity = 1;
            _card.Scale = Vector3.One;
        });
    }

    /// <summary>Comes in from <paramref name="from"/> (and <paramref name="scale"/>) while fading in.</summary>
    private void Arrive(UIElement element, Vector3 from, float scale, int delayMs, int durationMs)
    {
        element.OpacityTransition = null;
        element.TranslationTransition = null;
        element.ScaleTransition = null;
        element.Opacity = 0;
        element.Translation = from;
        if (scale != 1 && element is FrameworkElement fe)
        {
            CenterOn(fe);
            element.Scale = new Vector3(scale, scale, 1);
        }
        var version = _transition;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            if (version != _transition) return;
            element.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(durationMs * 0.7) };
            element.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(durationMs) };
            element.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(durationMs) };
            element.Opacity = 1;
            element.Translation = Vector3.Zero;
            element.Scale = Vector3.One;
        });
    }

    /// <summary>Leaves toward <paramref name="to"/>, shrinking to <paramref name="scale"/>, and fades out.</summary>
    private static void Leave(UIElement element, Vector3 to, float scale, int durationMs)
    {
        if (element is FrameworkElement fe) CenterOn(fe);
        element.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(durationMs) };
        element.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(durationMs) };
        element.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(durationMs) };
        element.Opacity = 0;
        element.Translation = to;
        element.Scale = new Vector3(scale, scale, 1);
    }

    /// <summary>Scales from the element's center, following it if its size changes.</summary>
    private static void CenterOn(FrameworkElement element)
    {
        void Set() => element.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        Set();
        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element)
            element.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
    }

    private static Button TextButton(string text, string automationId, out TextBlock label, string? glyph = null)
    {
        label = new TextBlock { Text = text, FontSize = 13, Foreground = new SolidColorBrush(Muted), VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (glyph is not null) content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 10, Foreground = new SolidColorBrush(Muted) });
        content.Children.Add(label);
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["BeautyIconButton"],
            Padding = new Thickness(8, 5, 8, 6),
            UseSystemFocusVisuals = false,
            Content = content,
        };
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(0x0F, 0, 0, 0));
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0));
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private static bool SameHost(string a, string b)
    {
        static string Host(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", "").ToLowerInvariant() : url;
        return Host(a) == Host(b);
    }
}
