using BeautyKit;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BeautyBrowser;

// Boosts (as in Arc): a card at the bottom right of the page to restyle the current site. Font, forced dark mode,
// elements hidden with a click and CSS of one's own. Every change is saved and shown on the page right away.
public sealed partial class MainWindow
{
    private Border? _boostCard;
    private BrowserTab? _boostTab;
    private SiteBoost? _boost;
    private TextBlock? _boostHiddenText;
    private Button? _boostPickButton;
    private bool _picking;

    private void SetupBoosts()
    {
        _shell.BoostRequested += (_, tab) => OpenBoost(tab);
        // Another tab or another site: the card belongs to the page it was opened on.
        _shell.CurrentTabChanged += (_, _) => { if (_boostTab != _shell.CurrentTab) CloseBoost(); };
    }

    private void OpenBoost(BrowserTab tab)
    {
        var site = Boosts.SiteOf(tab.Url);
        if (site.Length == 0) return;
        CloseBoost();
        _boostTab = tab;
        _boost = Boosts.For(site);
        tab.BoostElementPicked += OnBoostPicked;
        tab.BoostPickerEnded += OnBoostPickerEnded;
        tab.PropertyChanged += BoostTab_PropertyChanged;
        _boostCard = BuildBoostCard(site);
        Root.Children.Add(_boostCard);
    }

    private void CloseBoost()
    {
        if (_boostTab is { } tab)
        {
            if (_picking) tab.StopBoostPicker();
            tab.BoostElementPicked -= OnBoostPicked;
            tab.BoostPickerEnded -= OnBoostPickerEnded;
            tab.PropertyChanged -= BoostTab_PropertyChanged;
        }
        if (_boostCard is not null) Root.Children.Remove(_boostCard);
        _boostCard = null;
        _boostTab = null;
        _boost = null;
        _picking = false;
    }

    private void BoostTab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.Url) && _boost is { } boost && Boosts.SiteOf(_boostTab?.Url) != boost.Site) CloseBoost();
    }

    private void SaveBoost()
    {
        if (_boost is null || _boostTab is null) return;
        Boosts.Save(_boost);
        _boostTab.ApplyBoost(_boost);
        if (_boostHiddenText is not null)
            _boostHiddenText.Text = _boost.Hidden.Count == 0
                ? L.T("Nothing hidden", "Nada oculto")
                : L.T($"{_boost.Hidden.Count} hidden", $"{_boost.Hidden.Count} ocultos");
    }

    private void OnBoostPicked(object? sender, string selector)
    {
        if (_boost is null || _boost.Hidden.Contains(selector)) return;
        _boost.Hidden.Add(selector);
        SaveBoost();
    }

    private void OnBoostPickerEnded(object? sender, EventArgs e) => SetPicking(false);

    private void SetPicking(bool picking)
    {
        _picking = picking;
        if (_boostPickButton is not null)
            _boostPickButton.Content = picking ? L.T("Done", "Listo") : L.T("Pick on the page", "Elegir en la página");
        if (picking) _boostTab?.StartBoostPicker();
        else _boostTab?.StopBoostPicker();
        if (picking) WebView.Focus(FocusState.Programmatic); // the clicks go to the page
    }

    private Border BuildBoostCard(string site)
    {
        var boost = _boost!;
        Brush Res(string key) => (Brush)Application.Current.Resources[key];
        TextBlock Label(string text) => new() { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) };

        var close = new Button { Style = (Style)Application.Current.Resources["BeautyIconButton"], Content = new FontIcon { Glyph = "", FontSize = 11 } };
        ToolTipService.SetToolTip(close, L.T("Close", "Cerrar"));
        close.Click += (_, _) => CloseBoost();
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new FontIcon { Glyph = "", FontSize = 16, Foreground = Res("BeautyAccentBrush"), VerticalAlignment = VerticalAlignment.Center });
        var title = new StackPanel { Spacing = 0 };
        title.Children.Add(new TextBlock { Text = "Boost", FontSize = 15, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = site, FontSize = 12, Foreground = Res("BeautyTextSecondaryBrush") });
        Grid.SetColumn(title, 1);
        Grid.SetColumn(close, 2);
        header.Children.Add(title);
        header.Children.Add(close);

        var enabled = new ToggleSwitch { IsOn = boost.Enabled, OnContent = L.T("On", "Activado"), OffContent = L.T("Off", "Apagado") };
        enabled.Toggled += (_, _) => { boost.Enabled = enabled.IsOn; SaveBoost(); };

        // Font: chips, the chosen one filled.
        var fonts = new Grid { ColumnSpacing = 6 };
        var fontChoices = new[] { ("", L.T("Original", "Original")), ("serif", "Serif"), ("sans", "Sans"), ("mono", "Mono") };
        var fontButtons = new List<(Button Button, string Key)>();
        void PaintFonts()
        {
            foreach (var (button, key) in fontButtons)
            {
                var selected = boost.Font == key;
                button.Background = Res(selected ? "BeautyTextBrush" : "BeautySubtleFillBrush");
                button.Foreground = Res(selected ? "BeautyCardBrush" : "BeautyTextBrush");
            }
        }
        for (var i = 0; i < fontChoices.Length; i++)
        {
            var (key, name) = fontChoices[i];
            fonts.ColumnDefinitions.Add(new ColumnDefinition());
            var chip = new Button { Style = (Style)Application.Current.Resources["BeautyChipButton"], Content = name, FontSize = 13, Padding = new Thickness(2, 0, 2, 0) };
            chip.Click += (_, _) => { boost.Font = key; PaintFonts(); SaveBoost(); };
            Grid.SetColumn(chip, i);
            fonts.Children.Add(chip);
            fontButtons.Add((chip, key));
        }
        PaintFonts();

        var dark = new ToggleSwitch { IsOn = boost.Dark, OnContent = L.T("Forced dark", "Oscuro forzado"), OffContent = L.T("Forced dark", "Oscuro forzado") };
        dark.Toggled += (_, _) => { boost.Dark = dark.IsOn; SaveBoost(); };

        _boostPickButton = new Button { Content = L.T("Pick on the page", "Elegir en la página"), Style = (Style)Application.Current.Resources["BeautyChipButton"], Background = Res("BeautySubtleFillBrush"), FontSize = 13, Padding = new Thickness(12, 0, 12, 0) };
        _boostPickButton.Click += (_, _) => SetPicking(!_picking);
        _boostHiddenText = new TextBlock { FontSize = 12, Foreground = Res("BeautyTextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };
        var showAll = new HyperlinkButton { Content = L.T("Show all", "Mostrar todo"), Padding = new Thickness(0), FontSize = 12 };
        showAll.Click += (_, _) => { boost.Hidden.Clear(); SaveBoost(); _boostTab?.Reload(); };
        var hideRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _boostPickButton, _boostHiddenText, showAll } };

        var css = new TextBox
        {
            Text = boost.Css,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 90,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            FontSize = 12,
            PlaceholderText = L.T("Your CSS for this site…", "Tu CSS para este sitio…"),
            IsSpellCheckEnabled = false,
        };
        var applyCss = new Button { Content = L.T("Apply", "Aplicar"), Style = (Style)Application.Current.Resources["BeautyChipButton"], Background = Res("BeautySubtleFillBrush"), FontSize = 13, Padding = new Thickness(12, 0, 12, 0) };
        applyCss.Click += (_, _) => { boost.Css = css.Text; SaveBoost(); };

        var reset = new HyperlinkButton { Content = L.T("Remove this site's Boost", "Quitar el Boost de este sitio"), Padding = new Thickness(0), FontSize = 12 };
        reset.Click += (_, _) =>
        {
            var tab = _boostTab;
            _boost = new SiteBoost { Site = site };
            Boosts.Save(_boost);
            tab?.ApplyBoost(_boost);
            CloseBoost();
            tab?.Reload(); // elements hidden on the page come back
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(header);
        content.Children.Add(enabled);
        content.Children.Add(Label(L.T("Font", "Fuente")));
        content.Children.Add(fonts);
        content.Children.Add(Label(L.T("Colors", "Colores")));
        content.Children.Add(dark);
        content.Children.Add(Label(L.T("Hide elements", "Ocultar elementos")));
        content.Children.Add(hideRow);
        content.Children.Add(Label(L.T("Custom CSS", "CSS propio")));
        content.Children.Add(css);
        content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { applyCss, reset } });

        var card = new Border
        {
            Width = 320,
            Margin = new Thickness(0, 0, 28, 28),
            Padding = new Thickness(16, 12, 12, 16),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Res("BeautyCardBrush"),
            BorderBrush = Res("BeautyPanelBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 32),
            RequestedTheme = AppTheme.IsDark ? ElementTheme.Dark : ElementTheme.Light,
            Child = content,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(card, "BoostCard");
        SaveBoostLabel();
        return card;

        void SaveBoostLabel() => _boostHiddenText.Text = boost.Hidden.Count == 0
            ? L.T("Nothing hidden", "Nada oculto")
            : L.T($"{boost.Hidden.Count} hidden", $"{boost.Hidden.Count} ocultos");
    }
}
