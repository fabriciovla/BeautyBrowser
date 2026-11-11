using System.Numerics;
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

/// <summary>Settings, a library section built from custom controls (options with a check mark, chips and switches).</summary>
public sealed partial class SettingsPanel : UserControl
{
    private const int ConfirmMs = 4000;

    private BrowserShell? _shell;

    public SettingsPanel()
    {
        InitializeComponent();
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
                            FontSize = 13,
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

    private void UpdateRestartRow() =>
        RestartRow.Visibility = _shell is not null && Settings.HardwareAcceleration != _hardwareAtStart
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void Restart_Click(object sender, RoutedEventArgs e) => App.Restart();


    /// <summary>Exclusive options: one row per option, the selected one bold with a check mark.</summary>
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

            var row = new Button
            {
                Style = (Style)Application.Current.Resources["BeautyListRowButton"],
                MinHeight = hint is null ? 36 : 48,
                Padding = new Thickness(12, 6, 12, 6),
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

    /// <summary>Chips in a row; the selected one is a white card.</summary>
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
                chip.Background = Brush(selected ? "BeautyCardBrush" : "BeautySubtleFillBrush");
                chip.Foreground = Brush(selected ? "BeautyTextBrush" : "BeautyTextSecondaryBrush");
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
                FontSize = 12,
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

    // Every switch can repaint itself from the saved value, so it follows changes made elsewhere (the sidebar pin
    // button, for instance).
    private readonly List<Action> _toggleRefreshers = [];

    /// <summary>Rows with a custom switch: a pill with a sliding knob.</summary>
    private void ToggleRows(StackPanel host, IReadOnlyList<(string Title, string? Hint, Func<bool> Get, Action<bool> Set)> toggles)
    {
        host.Children.Clear();
        for (var i = 0; i < toggles.Count; i++)
        {
            var (title, hint, get, set) = toggles[i];
            var on = get();
            if (i > 0) host.Children.Add(new Rectangle { Height = 1, Margin = new Thickness(12, 0, 10, 0), Fill = Brush("BeautyDividerBrush") });

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
            Grid.SetColumn(track, 1);

            var state = on;
            void Paint()
            {
                track.Background = state ? Brush("BeautyAccentBrush") : new SolidColorBrush(ColorHelper.FromArgb(0x33, 0, 0, 0));
                knob.Translation = new Vector3(state ? 16 : 0, 0, 0);
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
                MinHeight = hint is null ? 40 : 50,
                Padding = new Thickness(12, 7, 12, 7),
                Content = new Grid
                {
                    ColumnSpacing = 12,
                    ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
                    Children = { Texts(title, hint, bold: false), track },
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
            FontSize = 13,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
        });
        if (hint is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
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
                Margin = new Thickness(12, 10, 12, 10),
                FontSize = 12,
                Foreground = Brush("BeautyTextSecondaryBrush"),
                Text = L.T("When you sign in to a site, Beauty Browser offers to save the password here.",
                    "Cuando inicies sesión en un sitio, Beauty Browser te ofrece guardar la contraseña acá."),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var login in logins)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 6, 6, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { FontSize = 15, Glyph = "", VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { FontSize = 13, Text = login.Site, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                FontSize = 11,
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
