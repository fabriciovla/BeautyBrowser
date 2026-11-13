using BeautyBrowser.Services;
using BeautyBrowser.Shell;
using BeautyKit;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using Windows.System;

namespace BeautyBrowser.Controls;

/// <summary>
/// The settings tab: categories on the left, search and content on the right. Built from custom controls (options with
/// a check mark, chips and switches).
/// </summary>
public sealed partial class SettingsPanel : UserControl
{
    private const int ConfirmMs = 4000;

    private BrowserShell? _shell;

    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public SettingsPanel()
    {
        InitializeComponent();
        BuildNavigation();
        // The blocker's total moves while the page is open, so it is read again every couple of seconds.
        _statsTimer.Tick += (_, _) => RefreshBlockerStats();
        Loaded += (_, _) => { RefreshBlockerStats(); _statsTimer.Start(); };
        Unloaded += (_, _) => _statsTimer.Stop();
    }

    private void RefreshBlockerStats()
    {
        var totals = BeautyKit.Blocking.ContentBlocker.Totals;
        StatAds.Text = totals.Ads.ToString("#,0");
        StatTrackers.Text = totals.Trackers.ToString("#,0");
        StatData.Text = totals.Bytes switch
        {
            >= 1_000_000_000 => $"{totals.Bytes / 1e9:0.0} GB",
            >= 1_000_000 => $"{totals.Bytes / 1e6:0.#} MB",
            _ => $"{totals.Bytes / 1e3:0} KB",
        };
        StatTime.Text = totals.TimeSaved.TotalHours >= 1 ? $"{(int)totals.TimeSaved.TotalHours} h {totals.TimeSaved.Minutes} min"
            : totals.TimeSaved.TotalMinutes >= 1 ? $"{(int)totals.TimeSaved.TotalMinutes} min {totals.TimeSaved.Seconds} s"
            : $"{totals.TimeSaved.Seconds} s";
        StatsHint.Text = L.T($"Since {totals.Since.ToLocalTime():d}. Data and time are estimates.",
            $"Desde el {totals.Since.ToLocalTime():d}. Los datos y el tiempo son estimados.");
    }

    private SettingsService Settings => _shell!.Settings;

    public void Attach(BrowserShell shell)
    {
        _shell = shell;
        VersionText.Text = $"Beauty Browser {UpdateService.CurrentVersion} · Chromium {BrowserEngine.ChromiumVersion}\n" + L.T("Your data stays on this PC.", "Tus datos se guardan solo en esta PC.");
        shell.Updates.StateChanged += (_, _) => ShowUpdateState();
        ShowUpdateState();
        _hardwareAtStart = shell.Settings.HardwareAcceleration; // what Chromium started with
        Build();
        shell.Passwords.Changed += (_, _) => BuildPasswordRows();
        BuildPasswordRows();
        shell.Settings.SettingsChanged += (_, _) =>
        {
            foreach (var refresh in _toggleRefreshers) refresh();
        };
    }

    /// <summary>
    /// Builds every row with the current values. Runs once (and on reset); afterwards each row updates in place, so
    /// nothing flickers when an option is touched.
    /// </summary>
    private void Build()
    {
        if (_shell is null) return;
        var s = Settings;

        OptionRows(StartupRows,
            [(L.T("Continue where you left off", "Continuar donde lo dejaste"), L.T("Your spaces and tabs come back", "Vuelven tus spaces y pestañas"),
                 () => s.Startup == StartupMode.RestoreTabs, () => s.Startup = StartupMode.RestoreTabs),
             (L.T("Open the home page", "Abrir la página de inicio"), null,
                 () => s.Startup == StartupMode.HomePage, () => s.Startup = StartupMode.HomePage)]);

        OptionRows(EngineRows, SettingsService.AvailableSearchEngines
            .Select(engine => (engine.Name, (string?)null,
                (Func<bool>)(() => string.Equals(engine.Name, s.SearchEngine, StringComparison.OrdinalIgnoreCase)),
                (Action)(() => s.SearchEngine = engine.Name)))
            .ToList());

        HomePageText.Text = s.HomePage;

        void FillTheme() => ThemeSwatches.Fill(ThemeRow, s.ThemeKey, key => s.ThemeKey = key);
        FillTheme();
        Chips(ModeChips,
            [(L.T("Light", "Claro"), () => s.ColorMode == "light", () => s.ColorMode = "light"),
             (L.T("Dark", "Oscuro"), () => s.ColorMode == "dark", () => s.ColorMode = "dark"),
             (L.T("System", "Sistema"), () => s.ColorMode == "system", () => s.ColorMode = "system")]);
        Chips(ZoomChips, SettingsService.ZoomOptions
            .Select(zoom => ($"{zoom} %", (Func<bool>)(() => zoom == s.DefaultZoom),
                (Action)(() => { s.DefaultZoom = zoom; _shell.ApplyDefaultZoom(); })))
            .ToList());
        Chips(FontChips, SettingsService.FontSizeOptions
            .Select(font => (font.Name, (Func<bool>)(() => font.Size == s.FontSize), (Action)(() => s.FontSize = font.Size)))
            .ToList());

        BuildGlass();

        _toggleRefreshers.Clear();
        _toggleRefreshers.Add(FillTheme); // the ring follows the theme, also when it changes from onboarding
        PositionChoice(s);
        ToggleRows(ExternalLinkToggles,
            [(L.T("Open them in the quick view", "Abrirlos en la vista rápida"),
                L.T("A small window over what you are doing; \"Open as tab\" keeps the page", "Una ventanita encima de lo que estás haciendo; \"Abrir como pestaña\" la deja"),
                () => s.ExternalLinksInPeek, v => s.ExternalLinksInPeek = v)]);
        ToggleRows(SidebarToggles,
            [(L.T("Keep the sidebar pinned", "Mantener la barra fija"), L.T("The page shifts over and the sidebar stays in view", "La página se corre y la barra queda siempre a la vista"),
                 () => s.SidebarDocked, v => s.SidebarDocked = v),
             (L.T("Tab previews", "Vista previa de pestañas"), L.T("When the mouse rests on a tab", "Al dejar el mouse sobre una pestaña"),
                 () => s.TabPreviews, v => s.TabPreviews = v),
             (L.T("Load bar", "Barra de carga"), L.T("The blue line at the top while a page loads", "La línea azul arriba mientras carga una página"),
                 () => s.ShowLoadBar, v => s.ShowLoadBar = v)]);
        Chips(IdleChips, SettingsService.SidebarIdleOptions
            .Select(option => (option.Name, (Func<bool>)(() => option.Seconds == s.SidebarIdleSeconds),
                (Action)(() => s.SidebarIdleSeconds = option.Seconds)))
            .ToList());
        // With the tabs on top there is no sidebar: pinning it, its previews and hiding it make no sense.
        void SidebarOnly()
        {
            var top = s.SidebarPosition == "top";
            for (var i = 0; i < 2 && i < SidebarToggles.Children.Count; i++)
                SidebarToggles.Children[i].Visibility = top ? Visibility.Collapsed : Visibility.Visible;
            // The page and the search show and hide whole groups: this one stays out while it does not apply.
            if (top) _hiddenGroups.Add(IdleGroup); else _hiddenGroups.Remove(IdleGroup);
            IdleGroup.Visibility = top ? Visibility.Collapsed : Visibility.Visible;
        }
        _toggleRefreshers.Add(SidebarOnly);
        SidebarOnly();

        FolderText.Text = s.DownloadsFolder;
        ToggleRows(DownloadToggles,
            [(L.T("Ask where to save each file", "Preguntar dónde guardar cada archivo"), null, () => s.AskWhereToSave, v => s.AskWhereToSave = v)]);

        ToggleRows(PrivacyToggles,
            [(L.T("Block popups", "Bloquear ventanas emergentes"), L.T("The ones a page opens on its own, without a click", "Las que una página abre sola, sin que hagas clic"),
                 () => s.BlockPopups, v => s.BlockPopups = v),
             (L.T("Block third-party cookies", "Bloquear cookies de terceros"), L.T("Keeps other sites from following you across pages", "Evita que otros sitios te sigan entre páginas"),
                 () => s.BlockThirdPartyCookies, v => s.BlockThirdPartyCookies = v),
             (L.T("Ask sites not to track you", "Pedir a los sitios que no te rastreen"), L.T("Sends the \"Do Not Track\" signal", "Envía la señal \"No rastrear\""),
                 () => s.DoNotTrack, v => s.DoNotTrack = v)]);

        OptionRows(InterfaceLanguageRows,
        [
            (L.T("System", "Sistema"), null, () => s.Language == LanguageChoice.System, () => { s.Language = LanguageChoice.System; UpdateLanguageRestart(); }),
            ("Español", null, () => s.Language == LanguageChoice.Spanish, () => { s.Language = LanguageChoice.Spanish; UpdateLanguageRestart(); }),
            ("English", null, () => s.Language == LanguageChoice.English, () => { s.Language = LanguageChoice.English; UpdateLanguageRestart(); }),
        ]);
        UpdateLanguageRestart();

        OptionRows(LanguageRows, SettingsService.AvailablePageLanguages
            .Select(lang => (lang.Name, (string?)null, (Func<bool>)(() => lang.Languages == s.PageLanguages),
                (Action)(() => s.PageLanguages = lang.Languages)))
            .ToList());

        // Hardware acceleration applies on restart; when it changed, "Restart now" shows up.
        ToggleRows(SystemToggles,
            [(L.T("Hardware acceleration", "Aceleración por hardware"), L.T("Uses the graphics card to render pages", "Usa la placa de video para dibujar las páginas"),
                 () => s.HardwareAcceleration, v => { s.HardwareAcceleration = v; UpdateRestartRow(); })]);
        UpdateRestartRow();
    }


    private bool _glassWired;

    /// <summary>Builds the texture card from the saved values. Sliders change the glass live and save on release.</summary>
    private void BuildGlass()
    {
        var s = Settings;
        TextureName.Text = SettingsService.GlassTextures.First(t => t.Key == s.GlassTexture).Name;
        GrainSlider.Value = s.GlassGrain;
        SoftnessSlider.Value = s.GlassSoftness;
        GrainText.Text = $"{s.GlassGrain} %";
        SoftnessText.Text = $"{s.GlassSoftness} %";

        // Texture list inside the dropdown, the selected one with a check mark.
        TextureRows.Children.Clear();
        foreach (var (key, name) in SettingsService.GlassTextures)
        {
            var row = new Button
            {
                Style = (Style)Application.Current.Resources["BeautyListRowButton"],
                MinHeight = 32,
                Padding = new Thickness(8, 4, 10, 4),
                Content = new Grid
                {
                    ColumnSpacing = 8,
                    ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(14) }, new ColumnDefinition() },
                    Children =
                    {
                        new FontIcon
                        {
                            Glyph = "",
                            FontSize = 11,
                            Foreground = Brush("BeautyAccentBrush"),
                            Visibility = key == s.GlassTexture ? Visibility.Visible : Visibility.Collapsed,
                        },
                        Column(new TextBlock
                        {
                            Text = name,
                            FontSize = 14,
                            FontWeight = key == s.GlassTexture ? FontWeights.SemiBold : FontWeights.Normal,
                        }, 1),
                    },
                },
            };
            row.Click += (_, _) =>
            {
                TextureFlyout.Hide();
                Settings.GlassTexture = key; // MainWindow pushes it to the glass; the card rebuilds here
                BuildGlass();
            };
            TextureRows.Children.Add(row);
        }

        if (_glassWired) return;
        _glassWired = true;
        GrainSlider.ValueChanged += (_, value) => { GrainText.Text = $"{value} %"; PreviewGlass(); };
        SoftnessSlider.ValueChanged += (_, value) => { SoftnessText.Text = $"{value} %"; PreviewGlass(); };
        GrainSlider.ValueCommitted += (_, value) => Settings.GlassGrain = (int)value;
        SoftnessSlider.ValueCommitted += (_, value) => Settings.GlassSoftness = (int)value;
    }

    /// <summary>While dragging, the glass changes live without saving yet.</summary>
    private void PreviewGlass() =>
        NoiseLayer.Configure(Settings.GlassTexture, GrainSlider.Value / 100, SoftnessSlider.Value / 100);

    private void ResetGlass_Click(object sender, RoutedEventArgs e)
    {
        Settings.ResetGlass();
        BuildGlass();
    }

    private static T Column<T>(T element, int column) where T : FrameworkElement
    {
        Grid.SetColumn(element, column);
        return element;
    }

    private bool _hardwareAtStart;

    private void UpdateLanguageRestart() =>
        LanguageRestartRow.Visibility = Settings.Language != L.Choice ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateRestartRow() =>
        RestartRow.Visibility = _shell is not null && Settings.HardwareAcceleration != _hardwareAtStart
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void Restart_Click(object sender, RoutedEventArgs e) => App.Restart();


    /// <summary>Exclusive options inside a bordered box: one row per option, split by lines, the selected one bold with a check mark.</summary>
    private static void OptionRows(StackPanel host,
        IReadOnlyList<(string Title, string? Hint, Func<bool> IsSelected, Action Choose)> options)
    {
        host.Children.Clear();
        var refreshers = new List<Action>();
        void RefreshGroup()
        {
            foreach (var refresh in refreshers) refresh();
        }

        foreach (var (title, hint, isSelected, choose) in options)
        {
            var check = new FontIcon
            {
                Glyph = "",
                FontSize = 12,
                Foreground = Brush("BeautyAccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(check, 1);
            var texts = Texts(title, hint, bold: false);
            var titleBlock = (TextBlock)texts.Children[0];

            refreshers.Add(() =>
            {
                var selected = isSelected();
                check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                titleBlock.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            });

            if (host.Children.Count > 0) host.Children.Add(new Rectangle { Height = 1, Fill = Brush("BeautyDividerBrush") });
            var row = new Button
            {
                Style = (Style)Application.Current.Resources["BeautyListRowButton"],
                MinHeight = hint is null ? 44 : 56,
                Padding = new Thickness(14, 6, 14, 6),
                Content = new Grid
                {
                    ColumnSpacing = 10,
                    ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
                    Children = { texts, check },
                },
            };
            row.Click += (_, _) =>
            {
                choose();
                RefreshGroup();
            };
            host.Children.Add(row);
        }
        RefreshGroup();
    }

    /// <summary>Chips in a row; the selected one is filled with the text color (dark in light mode, light in dark mode).</summary>
    private static void Chips(Grid host, IReadOnlyList<(string Text, Func<bool> IsSelected, Action Choose)> chips)
    {
        host.Children.Clear();
        host.ColumnDefinitions.Clear();
        var buttons = new List<(Button Chip, Func<bool> IsSelected)>();
        void RefreshGroup()
        {
            foreach (var (chip, isSelected) in buttons)
            {
                var selected = isSelected();
                chip.Background = Brush(selected ? "BeautyTextBrush" : "BeautySubtleFillBrush");
                chip.Foreground = Brush(selected ? "BeautyCardBrush" : "BeautyTextBrush");
            }
        }

        for (var i = 0; i < chips.Count; i++)
        {
            var (text, isSelected, choose) = chips[i];
            host.ColumnDefinitions.Add(new ColumnDefinition());
            var chip = new Button
            {
                Style = (Style)Application.Current.Resources["BeautyChipButton"],
                Content = text,
                FontSize = 14,
                Padding = new Thickness(4, 0, 4, 0),
            };
            Grid.SetColumn(chip, i);
            chip.Click += (_, _) =>
            {
                choose();
                RefreshGroup();
            };
            buttons.Add((chip, isSelected));
            host.Children.Add(chip);
        }
        RefreshGroup();
    }

    /// <summary>
    /// Three cards with a small drawing of each layout (left sidebar, right sidebar, tabs on top); the chosen one gets
    /// an accent border.
    /// </summary>
    private void PositionChoice(SettingsService s)
    {
        PositionCards.Children.Clear();
        PositionCards.ColumnDefinitions.Clear();
        var options = new[]
        {
            ("left", L.T("Left", "Izquierda")),
            ("right", L.T("Right", "Derecha")),
            ("top", L.T("Top", "Arriba")),
        };
        var cards = new List<(Button Card, Border Frame, string Key)>();
        void Refresh()
        {
            foreach (var (_, frame, key) in cards)
            {
                var selected = s.SidebarPosition == key;
                frame.BorderBrush = Brush(selected ? "BeautyAccentBrush" : "BeautyPanelBorderBrush");
                frame.BorderThickness = new Thickness(selected ? 2 : 1);
            }
        }

        for (var i = 0; i < options.Length; i++)
        {
            var (key, name) = options[i];
            PositionCards.ColumnDefinitions.Add(new ColumnDefinition());
            var frame = new Border
            {
                Height = 92,
                CornerRadius = new CornerRadius(10),
                Background = Brush("BeautySubtleFillBrush"),
                Padding = new Thickness(8),
                Child = LayoutSketch(key),
            };
            var card = new Button
            {
                Style = (Style)Application.Current.Resources["BeautyListRowButton"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(12),
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children = { frame, new TextBlock { Text = name, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center } },
                },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(card, "Position" + key);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, name);
            Grid.SetColumn(card, i);
            card.Click += (_, _) =>
            {
                s.SidebarPosition = key;
                Refresh();
            };
            cards.Add((card, frame, key));
            PositionCards.Children.Add(card);
        }
        _toggleRefreshers.Add(Refresh);
        Refresh();
    }

    /// <summary>A miniature window: the page as a white card and the tabs as a darker bar where they live.</summary>
    private static Grid LayoutSketch(string position)
    {
        var bar = Brush("BeautyFillHoverBrush");
        var page = Brush("BeautyCardBrush");
        var grid = new Grid { ColumnSpacing = 5, RowSpacing = 5 };
        Border Block(Brush fill) => new() { Background = fill, CornerRadius = new CornerRadius(4) };

        if (position == "top")
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            grid.RowDefinitions.Add(new RowDefinition());
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            strip.Children.Add(new Border { Width = 22, Background = page, CornerRadius = new CornerRadius(3) });
            strip.Children.Add(new Border { Width = 22, Background = bar, CornerRadius = new CornerRadius(3) });
            strip.Children.Add(new Border { Width = 22, Background = bar, CornerRadius = new CornerRadius(3) });
            grid.Children.Add(strip);
            var body = Block(page);
            Grid.SetRow(body, 1);
            grid.Children.Add(body);
            return grid;
        }

        var side = new ColumnDefinition { Width = new GridLength(30) };
        var rest = new ColumnDefinition();
        if (position == "right") { grid.ColumnDefinitions.Add(rest); grid.ColumnDefinitions.Add(side); }
        else { grid.ColumnDefinitions.Add(side); grid.ColumnDefinitions.Add(rest); }
        var tabs = new StackPanel { Spacing = 3 };
        for (var i = 0; i < 4; i++) tabs.Children.Add(new Border { Height = 8, Background = i == 0 ? page : bar, CornerRadius = new CornerRadius(2) });
        var pageBlock = Block(page);
        Grid.SetColumn(tabs, position == "right" ? 1 : 0);
        Grid.SetColumn(pageBlock, position == "right" ? 0 : 1);
        grid.Children.Add(tabs);
        grid.Children.Add(pageBlock);
        return grid;
    }

    // Every switch can repaint itself from the saved value, so it follows changes made elsewhere (the sidebar pin
    // button, for instance).
    private readonly List<Action> _toggleRefreshers = [];
    private readonly HashSet<UIElement> _hiddenGroups = [];

    /// <summary>Rows with our own check box on the left: a rounded square that fills with the accent and a check mark.</summary>
    private void ToggleRows(StackPanel host, IReadOnlyList<(string Title, string? Hint, Func<bool> Get, Action<bool> Set)> toggles)
    {
        host.Children.Clear();
        foreach (var (title, hint, get, set) in toggles)
        {
            var mark = new FontIcon { Glyph = "", FontSize = 11, Foreground = new SolidColorBrush(Colors.White) };
            var box = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = hint is null ? VerticalAlignment.Center : VerticalAlignment.Top,
                Margin = new Thickness(0, hint is null ? 0 : 1, 0, 0),
                BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(120) },
                Child = mark,
            };

            var state = get();
            void Paint()
            {
                box.Background = state ? Brush("BeautyAccentBrush") : null;
                box.BorderBrush = state ? null : Brush("BeautyTextSecondaryBrush");
                box.BorderThickness = new Thickness(state ? 0 : 1.5);
                mark.Opacity = state ? 1 : 0;
            }
            Paint();
            _toggleRefreshers.Add(() =>
            {
                if (state == get()) return;
                state = !state;
                Paint();
            });

            var row = new Button
            {
                Style = (Style)Application.Current.Resources["BeautyListRowButton"],
                MinHeight = 36,
                Margin = new Thickness(-8, 0, -8, 0), // the hover fill reaches past the text, the box lines up with the titles
                Padding = new Thickness(8, 6, 8, 6),
                Content = new Grid
                {
                    ColumnSpacing = 12,
                    ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
                    Children = { box, Column(Texts(title, hint, bold: false), 1) },
                },
            };
            row.Click += (_, _) =>
            {
                state = !state;
                set(state);
                Paint();
            };
            host.Children.Add(row);
        }
    }

    private static StackPanel Texts(string title, string? hint, bool bold)
    {
        var panel = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
        });
        if (hint is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 13,
                Foreground = Brush("BeautyTextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        return panel;
    }


    private void HomePageText_LostFocus(object sender, RoutedEventArgs e) => SaveHomePage();

    private void HomePageText_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        SaveHomePage();
        Focus(FocusState.Programmatic); // leave the field, as a "done"
    }

    private void SaveHomePage()
    {
        if (_shell is null) return;
        var text = HomePageText.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) text = SettingsService.DefaultHomePage;
        else if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        HomePageText.Text = text;
        if (text != Settings.HomePage) Settings.HomePage = text;
    }


    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_shell is null || XamlRoot is null) return;
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        var hwnd = Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        Settings.DownloadsFolder = folder.Path;
        FolderText.Text = folder.Path;
    }

    /// <summary>Brings the "clear history" and "clear cookies" rows into view (Ctrl+Shift+Delete).</summary>
    public void RevealClearData()
    {
        SearchBox.Text = "";
        ShowPage(SectionPrivacy);
        // After the page is laid out: the tab may have just opened.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            ClearHistoryRow.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.3 }));
    }


    // Navigation: each category is its own page; the row of the one showing is marked.

    private readonly List<(StackPanel Section, Button Row, TextBlock Text, FontIcon Icon)> _nav = [];
    private StackPanel? _page;

    private static StackPanel Header(StackPanel section) => (StackPanel)section.Children[0];
    private static string TitleOf(StackPanel section) => Header(section).Children.OfType<TextBlock>().First().Text;
    private static UIElementCollection GroupsOf(StackPanel section) => ((StackPanel)((Border)section.Children[1]).Child).Children;

    private void BuildNavigation()
    {
        foreach (var section in Sections.Children.OfType<StackPanel>())
        {
            var icon = new FontIcon { Glyph = Header(section).Children.OfType<FontIcon>().First().Glyph, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock { Text = TitleOf(section), FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
            var row = new Button
            {
                Style = (Style)Application.Current.Resources["BeautyListRowButton"],
                MinHeight = 40,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(8),
                Content = new Grid
                {
                    ColumnSpacing = 12,
                    ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
                    Children = { icon, Column(text, 1) },
                },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, text.Text);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(row, "Nav" + section.Name);
            row.Click += (_, _) =>
            {
                SearchBox.Text = "";
                ShowPage(section);
            };
            _nav.Add((section, row, text, icon));
            NavRows.Children.Add(row);
        }
        ShowPage(SectionGeneral);
    }

    /// <summary>Shows one category alone, from the top.</summary>
    private void ShowPage(StackPanel page)
    {
        _page = page;
        foreach (var (section, row, text, icon) in _nav)
        {
            var on = section == page;
            section.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            foreach (var group in GroupsOf(section)) group.Visibility = Visibility.Visible;
            row.Visibility = Visibility.Visible;
            row.Background = on ? Brush("BeautyFillHoverBrush") : null;
            text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            text.Foreground = icon.Foreground = Brush(on ? "BeautyTextBrush" : "BeautyTextSecondaryBrush");
        }
        foreach (var hidden in _hiddenGroups) hidden.Visibility = Visibility.Collapsed;
        NoResults.Visibility = Visibility.Collapsed;
        Scroller.ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
    }


    // Search: across every category. A group stays when its texts (titles, options, hints) contain what is typed; a
    // category whose name matches keeps all its groups. Clearing it goes back to the page that was open.

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = Fold(SearchBox.Text.Trim());
        if (query.Length == 0)
        {
            ShowPage(_page ?? SectionGeneral);
            return;
        }

        var any = false;
        foreach (var (section, row, text, icon) in _nav)
        {
            var all = Fold(TitleOf(section)).Contains(query, StringComparison.Ordinal);
            var shown = false;
            foreach (var group in GroupsOf(section))
            {
                var match = all || TextsOf(group).Any(t => Fold(t).Contains(query, StringComparison.Ordinal));
                group.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                shown |= match;
            }
            section.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            row.Background = null;
            text.FontWeight = FontWeights.Normal;
            text.Foreground = icon.Foreground = Brush(shown ? "BeautyTextBrush" : "BeautyTextSecondaryBrush");
            any |= shown;
        }
        foreach (var hidden in _hiddenGroups) hidden.Visibility = Visibility.Collapsed;
        NoResults.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Scroller.ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
    }

    /// <summary>
    /// The texts inside an element, walking its content rather than the visual tree: the other pages were never laid
    /// out, so their buttons have no template yet.
    /// </summary>
    private static IEnumerable<string> TextsOf(object? element)
    {
        switch (element)
        {
            case TextBlock { Text.Length: > 0 } block:
                yield return block.Text;
                break;
            case string { Length: > 0 } text:
                yield return text;
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                    foreach (var text in TextsOf(child)) yield return text;
                break;
            case Border border:
                foreach (var text in TextsOf(border.Child)) yield return text;
                break;
            case ContentControl control:
                foreach (var text in TextsOf(control.Content)) yield return text;
                break;
        }
    }

    /// <summary>Lowercase without accents: "busqueda" finds "Búsqueda".</summary>
    private static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    // Clear and reset: the first tap asks, the second one acts.

    private long _historyConfirmUntil;
    private long _cookiesConfirmUntil;
    private long _resetConfirmUntil;

    private void ClearHistory_Click(object sender, RoutedEventArgs e) =>
        ConfirmThen(ref _historyConfirmUntil, ClearHistoryHint, L.T("The pages you visited on this PC", "Las páginas que visitaste en esta PC"), () =>
            _shell?.Settings.ClearBrowsingData(clearHistory: true, clearCookies: false, clearCache: false, _shell.History));

    /// <summary>Saved accounts, one row each with a button to delete it. Passwords are never shown here.</summary>
    private void BuildPasswordRows()
    {
        PasswordRows.Children.Clear();
        var logins = _shell!.Passwords.All;
        if (logins.Count == 0)
        {
            PasswordRows.Children.Add(new TextBlock
            {
                Margin = new Thickness(14, 12, 14, 12),
                FontSize = 14,
                Foreground = Brush("BeautyTextSecondaryBrush"),
                Text = L.T("When you sign in to a site, Beauty Browser offers to save the password here.",
                    "Cuando inicies sesión en un sitio, Beauty Browser te ofrece guardar la contraseña acá."),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var login in logins)
        {
            if (PasswordRows.Children.Count > 0) PasswordRows.Children.Add(new Rectangle { Height = 1, Fill = Brush("BeautyDividerBrush") });
            var row = new Grid { ColumnSpacing = 14, Padding = new Thickness(14, 8, 8, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { FontSize = 15, Glyph = "", VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { FontSize = 15, Text = login.Site, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                FontSize = 13,
                Foreground = Brush("BeautyTextSecondaryBrush"),
                Text = login.User.Length > 0 ? login.User : L.T("(no user name)", "(sin usuario)"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var delete = new Button
            {
                Content = new FontIcon { FontSize = 13, Glyph = "" },
                Style = (Style)Application.Current.Resources["BeautyIconButton"],
            };
            ToolTipService.SetToolTip(delete, L.T("Delete", "Borrar"));
            delete.Click += (_, _) => _shell.Passwords.Remove(login);
            Grid.SetColumn(delete, 2);
            row.Children.Add(delete);
            PasswordRows.Children.Add(row);
        }
    }

    private void ClearCookies_Click(object sender, RoutedEventArgs e) =>
        ConfirmThen(ref _cookiesConfirmUntil, ClearCookiesHint, L.T("You will have to sign in to sites again", "Vas a tener que volver a iniciar sesión en los sitios"), () =>
            _shell?.Settings.ClearBrowsingData(clearHistory: false, clearCookies: true, clearCache: true, _shell.History));

    private void ShowUpdateState()
    {
        var updates = _shell!.Updates;
        var (title, hint, glyph) = updates.State switch
        {
            UpdateState.Checking => (L.T("Checking for updates…", "Buscando actualizaciones…"), "", "\uE895"),
            UpdateState.Downloading => (L.T("Downloading the new version…", "Descargando la versión nueva…"),
                L.T("It installs when you close the browser.", "Se instala cuando cerrás el navegador."), "\uE896"),
            UpdateState.Ready => (L.T($"Restart to update to {updates.ReadyVersion}", $"Reiniciar para actualizar a la {updates.ReadyVersion}"),
                L.T("Your tabs come back as they were.", "Tus pestañas vuelven como estaban."), "\uE72C"),
            UpdateState.UpToDate => (L.T("Check for updates", "Buscar actualizaciones"),
                L.T($"You have the latest version ({UpdateService.CurrentVersion}).", $"Tenés la última versión ({UpdateService.CurrentVersion})."), "\uE895"),
            UpdateState.Failed => (L.T("Check for updates", "Buscar actualizaciones"),
                L.T("Could not check. Try again later.", "No se pudo buscar. Probá más tarde."), "\uE895"),
            UpdateState.NotInstalled => (L.T("Check for updates", "Buscar actualizaciones"),
                L.T("Only the installed browser updates itself.", "Solo se actualiza el navegador instalado."), "\uE895"),
            _ => (L.T("Check for updates", "Buscar actualizaciones"),
                L.T("It updates by itself, like Chrome.", "Se actualiza sola, como Chrome."), "\uE895"),
        };
        UpdateTitle.Text = title;
        UpdateHint.Text = hint;
        UpdateIcon.Glyph = glyph;
        UpdateRow.IsEnabled = updates.State is not (UpdateState.Checking or UpdateState.Downloading or UpdateState.NotInstalled);
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_shell!.Updates.State == UpdateState.Ready) App.Restart(); // the installer runs and opens the browser again
        else _ = _shell.Updates.CheckNowAsync();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) =>
        ConfirmThen(ref _resetConfirmUntil, ResetHint, L.T("Restores the defaults. History and favorites stay.", "Vuelve todo a como venía. No borra historial ni favoritos."), () =>
        {
            Settings.ResetToDefaults();
            Build();
        }, done: L.T("Done, everything is back to the defaults.", "Listo, volvió todo a como venía."));

    private void ConfirmThen(ref long confirmUntil, TextBlock hint, string normalText, Action action, string? done = null)
    {
        var now = Environment.TickCount64;
        if (now < confirmUntil)
        {
            confirmUntil = 0;
            action();
            ShowHint(hint, done ?? L.T("Done, it was cleared.", "Listo, se borró."), "BeautyAccentBrush", normalText);
            return;
        }

        confirmUntil = now + ConfirmMs;
        ShowHint(hint, L.T("Tap again to confirm", "Tocá otra vez para confirmar"), "BeautyDangerBrush", normalText);
    }

    private async void ShowHint(TextBlock hint, string text, string brushKey, string normalText)
    {
        hint.Text = text;
        hint.Foreground = Brush(brushKey);
        var shown = text;
        await Task.Delay(ConfirmMs);
        if (hint.Text != shown) return; // another tap changed it meanwhile
        hint.Text = normalText;
        hint.Foreground = Brush("BeautyTextSecondaryBrush");
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
