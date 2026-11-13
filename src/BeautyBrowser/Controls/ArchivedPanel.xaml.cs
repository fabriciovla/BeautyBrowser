using BeautyBrowser.Services;
using BeautyBrowser.Shell;
using BeautyKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BeautyBrowser.Controls;

/// <summary>
/// The library's "Archived" section: the tabs that were closed for not being used. Search them, tap one to open it
/// again (it leaves the archive) or remove it.
/// </summary>
public sealed partial class ArchivedPanel : UserControl
{
    private BrowserShell? _shell;

    public ArchivedPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Attach(BrowserShell shell)
    {
        _shell = shell;
        shell.Archive.Changed += (_, _) => DispatcherQueue.TryEnqueue(Refresh);
    }

    public void Refresh()
    {
        if (_shell is null) return;
        var query = SearchBox.Text.Trim();
        var items = _shell.Archive.Search(query);

        Rows.Children.Clear();
        foreach (var item in items) Rows.Children.Add(Row(item));

        ClearButton.IsEnabled = items.Count > 0;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = query.Length > 0
            ? L.T("No archived tab matches.", "Ninguna pestaña archivada coincide.")
            : L.T("Tabs you do not use for a while are archived here, so they do not pile up. You can change when in Settings.",
                  "Las pestañas que no usás por un tiempo se archivan acá, para que no se acumulen. Podés cambiar cuándo en Ajustes.");
    }

    public void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    private Button Row(ArchivedTab item)
    {
        var icon = Favicons.FromUrl(item.FaviconUrl);
        var leading = icon is null
            ? (UIElement)new FontIcon { FontSize = 14, Glyph = "", Foreground = Brush("BeautyTextSecondaryBrush") }
            : new Image { Width = 16, Height = 16, Source = icon };

        var remove = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["BeautyIconButton"],
            Foreground = Brush("BeautyTextSecondaryBrush"),
            UseSystemFocusVisuals = false,
            Content = new FontIcon { FontSize = 10, Glyph = "" },
        };
        ToolTipService.SetToolTip(remove, L.T("Remove from archive", "Quitar del archivo"));
        remove.Click += (_, e) => _shell?.Archive.Remove(item.Id);

        var when = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = Brush("BeautyTextSecondaryBrush"),
            Text = Ago(item.ArchivedAt),
        };
        var trailing = new Grid { Width = 52, Children = { when, remove } };

        var grid = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(18) },
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var texts = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = item.Title, FontSize = 13, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush("BeautyTextBrush") },
                new TextBlock { Text = DisplayUrl(item.Url), FontSize = 11, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush("BeautyTextSecondaryBrush") },
            },
        };
        grid.Children.Add(leading);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);

        var row = new Button
        {
            Height = 46,
            Style = (Style)Application.Current.Resources["BeautyListRowButton"],
            Content = grid,
        };
        ToolTipService.SetToolTip(row, item.Url);
        row.PointerEntered += (_, _) => { when.Opacity = 0; remove.Opacity = 1; };
        row.PointerExited += (_, _) => { when.Opacity = 1; remove.Opacity = 0; };
        row.Click += (_, _) => _shell?.ReopenArchived(item);
        return row;
    }

    private static string DisplayUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? HostName.WithoutWww(uri.Host) + uri.PathAndQuery.TrimEnd('/') : url;

    private static string Ago(DateTime local)
    {
        var span = DateTime.Now - local;
        if (span.TotalMinutes < 1) return L.T("just now", "recién");
        if (span.TotalHours < 1) return L.T($"{(int)span.TotalMinutes} min ago", $"hace {(int)span.TotalMinutes} min");
        if (span.TotalDays < 1) return L.T($"{(int)span.TotalHours} h ago", $"hace {(int)span.TotalHours} h");
        return span.TotalDays < 2 ? L.T("yesterday", "ayer") : L.T($"{(int)span.TotalDays} days ago", $"hace {(int)span.TotalDays} días");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ClearFlyout.Hide();
        _shell?.Archive.Clear();
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
