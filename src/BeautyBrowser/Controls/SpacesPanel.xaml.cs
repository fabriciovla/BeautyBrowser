using BeautyBrowser.Shell;
using BeautyKit;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace BeautyBrowser.Controls;

/// <summary>
/// Spaces in the library. On top, while creating one: name and icon (Windows icons, not emojis). Below, every space as
/// a card: click to switch; on hover, change icon (the grid opens inside the card), rename and delete (the first tap
/// asks, the second deletes). The cards are custom controls with smooth transitions, since Windows buttons swap their
/// background abruptly on hover and flicker.
/// </summary>
public sealed partial class SpacesPanel : UserControl
{
    private const int ConfirmMs = 4000;
    private const int IconColumns = 6;
    private static readonly TimeSpan HoverFade = TimeSpan.FromMilliseconds(140);

    private static readonly Color Transparent = Colors.Transparent;
    private static readonly Color HoverWhite = Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
    private static readonly Color Ink = Color.FromArgb(0xFF, 0x1F, 0x1F, 0x24);
    private static readonly Color SoftFill = Color.FromArgb(0x0E, 0x00, 0x00, 0x00);
    private static readonly Color SoftFillHover = Color.FromArgb(0x1A, 0x00, 0x00, 0x00);

    private BrowserShell? _shell;
    private Space? _editing;
    private Space? _pickingIcon;
    private Space? _confirmingDelete;
    private long _confirmUntil;
    private string _composerIcon = SpaceIcons.Default;

    public SpacesPanel()
    {
        InitializeComponent();
    }

    public void Attach(BrowserShell shell)
    {
        _shell = shell;
        shell.Spaces.CollectionChanged += (_, _) => Refresh();
        shell.CurrentSpaceChanged += (_, _) => Refresh();
        shell.SpaceRenamed += (_, _) => Refresh();
        Refresh();
    }

    public void Refresh()
    {
        if (_shell is null) return;
        var count = _shell.Spaces.Count;
        Summary.Text = count == 1 ? L.T("1 space · each keeps its own tabs", "1 space · cada uno guarda sus pestañas") : L.T($"{count} spaces · each keeps its own tabs", $"{count} spaces · cada uno guarda sus pestañas");
        Rows.Children.Clear();
        foreach (var space in _shell.Spaces) Rows.Children.Add(BuildCard(space));
    }


    /// <summary>Opens the new space form (name and icon) above the existing spaces.</summary>
    public void StartNewSpace()
    {
        if (_shell is null) return;
        _editing = null;
        _pickingIcon = null;
        _composerIcon = SpaceIcons.FirstUnused(_shell.Spaces);
        ComposerName.Text = "";
        RefreshComposerIcons();
        Composer.Visibility = Visibility.Visible;
        Refresh();
        DispatcherQueue.TryEnqueue(() => ComposerName.Focus(FocusState.Programmatic));
    }

    private void CloseComposer() => Composer.Visibility = Visibility.Collapsed;

    private void RefreshComposerIcons()
    {
        ComposerIcon.Glyph = _composerIcon;
        FillIconGrid(ComposerIcons, _composerIcon, glyph =>
        {
            _composerIcon = glyph;
            RefreshComposerIcons();
        });
    }

    private void CreateSpace()
    {
        if (_shell is null) return;
        _shell.AddSpace(ComposerName.Text, _composerIcon);
        CloseComposer();
        Refresh();
    }

    private void NewSpace_Click(object sender, RoutedEventArgs e)
    {
        if (Composer.Visibility == Visibility.Visible) CloseComposer();
        else StartNewSpace();
    }

    private void CreateSpace_Click(object sender, RoutedEventArgs e) => CreateSpace();
    private void CancelNew_Click(object sender, RoutedEventArgs e) => CloseComposer();

    private void ComposerName_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { e.Handled = true; CreateSpace(); }
        else if (e.Key == VirtualKey.Escape) { e.Handled = true; CloseComposer(); }
    }


    private static void FillIconGrid(Grid grid, string selected, Action<string> pick)
    {
        grid.Children.Clear();
        grid.ColumnSpacing = 6;
        grid.RowSpacing = 6;
        if (grid.ColumnDefinitions.Count != IconColumns)
        {
            grid.ColumnDefinitions.Clear();
            for (var i = 0; i < IconColumns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        var rows = (SpaceIcons.All.Length + IconColumns - 1) / IconColumns;
        grid.RowDefinitions.Clear();
        for (var i = 0; i < rows; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });

        for (var i = 0; i < SpaceIcons.All.Length; i++)
        {
            var (glyph, name) = SpaceIcons.All[i];
            var isSelected = glyph == selected;
            // Selected: the same dark square with a white icon the space wears; the rest are soft.
            var cell = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(isSelected ? Ink : SoftFill),
                BackgroundTransition = new BrushTransition { Duration = HoverFade },
                Child = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 16,
                    Foreground = isSelected ? new SolidColorBrush(Colors.White) : Brush("BeautyTextBrush"),
                },
            };
            if (!isSelected) Hover(cell, SoftFill, SoftFillHover);
            ToolTipService.SetToolTip(cell, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, name);
            cell.Tapped += (_, e) => { e.Handled = true; pick(glyph); };
            Grid.SetRow(cell, i / IconColumns);
            Grid.SetColumn(cell, i % IconColumns);
            grid.Children.Add(cell);
        }
    }

    /// <summary>A background that changes smoothly on hover (BackgroundTransition).</summary>
    private static void Hover(Border element, Color normal, Color hover)
    {
        element.PointerEntered += (_, _) => element.Background = new SolidColorBrush(hover);
        element.PointerExited += (_, _) => element.Background = new SolidColorBrush(normal);
    }


    private FrameworkElement BuildCard(Space space)
    {
        var shell = _shell!;
        var current = space == shell.CurrentSpace;
        var editing = space == _editing;
        var picking = space == _pickingIcon;
        var confirming = space == _confirmingDelete && Environment.TickCount64 < _confirmUntil;

        // Icon: a dark square with a white glyph for the current space; soft for the rest.
        var icon = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(current ? Ink : SoftFill),
            Child = new FontIcon
            {
                Glyph = space.Icon,
                FontSize = 17,
                Foreground = current ? new SolidColorBrush(Colors.White) : Brush("BeautyTextBrush"),
            },
        };

        FrameworkElement title;
        if (editing)
        {
            var box = new TextBox
            {
                Text = space.Name,
                FontSize = 14,
                Padding = new Thickness(0, 4, 0, 4),
                VerticalAlignment = VerticalAlignment.Center,
                IsSpellCheckEnabled = false,
            };
            box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Enter) { e.Handled = true; FinishEditing(space, box.Text); }
                else if (e.Key == VirtualKey.Escape) { e.Handled = true; FinishEditing(space, null); }
            };
            box.LostFocus += (_, _) => FinishEditing(space, box.Text);
            title = box;
        }
        else
        {
            var count = space.Tabs.Count;
            title = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = space.Name,
                        FontSize = 14,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brush("BeautyTextBrush"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1,
                    },
                    new TextBlock
                    {
                        Text = confirming ? L.T("Tap the trash can again to delete it", "Tocá el tacho otra vez para borrarlo")
                            : count == 0 ? L.T("No tabs", "Sin pestañas")
                            : count == 1 ? L.T("1 tab", "1 pestaña") : L.T($"{count} tabs", $"{count} pestañas"),
                        FontSize = 12,
                        Foreground = Brush(confirming ? "BeautyDangerBrush" : "BeautyTextSecondaryBrush"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1,
                    },
                },
            };
        }
        Grid.SetColumn(title, 1);

        // On the right, "Current" at rest; on hover the actions cross-fade in.
        var changeIcon = SmallButton("", L.T("Change icon", "Cambiar ícono"), () => TogglePickingIcon(space));
        var rename = SmallButton("", L.T("Rename", "Renombrar"), () => StartEditing(space));
        var delete = SmallButton("", L.T("Delete space", "Borrar space"), () => ConfirmDelete(space));
        delete.Visibility = shell.Spaces.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (confirming) delete.Foreground = Brush("BeautyDangerBrush");
        var pinnedActions = confirming || picking || editing;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = pinnedActions ? 1 : 0,
            OpacityTransition = new ScalarTransition { Duration = HoverFade },
            Children = { changeIcon, rename, delete },
        };
        var badge = new Border
        {
            Padding = new Thickness(8, 2, 8, 3),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(SoftFill),
            Opacity = current && !pinnedActions ? 1 : 0,
            OpacityTransition = new ScalarTransition { Duration = HoverFade },
            IsHitTestVisible = false,
            Child = new TextBlock { Text = L.T("Current", "Actual"), FontSize = 11, Foreground = Brush("BeautyTextSecondaryBrush") },
        };
        var right = new Grid { Children = { badge, actions } };
        Grid.SetColumn(right, 2);

        var row = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Children = { icon, title, right },
        };

        var content = new StackPanel { Spacing = 12, Children = { row } };
        if (picking)
        {
            var grid = new Grid();
            FillIconGrid(grid, space.Icon, glyph =>
            {
                _pickingIcon = null;
                shell.SetSpaceIcon(space, glyph); // raises SpaceRenamed and the list rebuilds
                Refresh();
            });
            content.Children.Add(grid);
        }

        // The card: white for the current space; the rest appear on hover.
        var restColor = current ? Colors.White : Transparent;
        var card = new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(picking || editing ? Colors.White : restColor),
            BackgroundTransition = new BrushTransition { Duration = HoverFade },
            BorderBrush = current || picking || editing ? Brush("BeautyInsetBorderBrush") : new SolidColorBrush(Transparent),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        card.PointerEntered += (_, _) =>
        {
            if (!current && !picking && !editing) card.Background = new SolidColorBrush(HoverWhite);
            actions.Opacity = 1;
            badge.Opacity = 0;
        };
        card.PointerExited += (_, _) =>
        {
            if (!current && !picking && !editing) card.Background = new SolidColorBrush(restColor);
            if (space == _confirmingDelete || space == _pickingIcon || space == _editing) return;
            actions.Opacity = 0;
            badge.Opacity = current ? 1 : 0;
        };
        card.Tapped += (_, e) =>
        {
            if (editing || IsInsideButton(e.OriginalSource as DependencyObject, card)) return;
            shell.SwitchSpace(space); // the page on the right shows that space
        };
        return card;
    }

    private static bool IsInsideButton(DependencyObject? source, DependencyObject stop)
    {
        for (var node = source; node is not null && node != stop; node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase or TextBox) return true;
        return false;
    }

    private Button SmallButton(string glyph, string tooltip, Action action)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["BeautyIconButton"],
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Foreground = Brush("BeautyTextSecondaryBrush"),
            UseSystemFocusVisuals = false,
            Content = new FontIcon { Glyph = glyph, FontSize = 13 },
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private void TogglePickingIcon(Space space)
    {
        _pickingIcon = _pickingIcon == space ? null : space;
        _editing = null;
        Refresh();
    }


    private void StartEditing(Space space)
    {
        _editing = space;
        _pickingIcon = null;
        Refresh();
    }

    private void FinishEditing(Space space, string? name)
    {
        if (_editing != space) return;
        _editing = null;
        if (name is not null) _shell?.RenameSpace(space, name);
        Refresh();
    }


    private async void ConfirmDelete(Space space)
    {
        var now = Environment.TickCount64;
        if (_confirmingDelete == space && now < _confirmUntil)
        {
            _confirmingDelete = null;
            if (_pickingIcon == space) _pickingIcon = null;
            _shell?.RemoveSpace(space);
            Refresh();
            return;
        }

        _confirmingDelete = space;
        _confirmUntil = now + ConfirmMs;
        Refresh();
        await Task.Delay(ConfirmMs);
        if (_confirmingDelete == space && Environment.TickCount64 >= _confirmUntil)
        {
            _confirmingDelete = null;
            Refresh();
        }
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
