using System.Collections.ObjectModel;
using System.Globalization;
using BeautyBrowser.Services;
using BeautyKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace BeautyBrowser.Controls;

/// <summary>A history group header ("Today", "Yesterday", "Monday", "November 3").</summary>
public sealed class HistoryDay(string label)
{
    public string Label { get; } = label;
}

public sealed partial class HistoryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DayTemplate { get; set; }
    public DataTemplate? EntryTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is HistoryDay ? DayTemplate : EntryTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}

/// <summary>History inside the library: a search box on top and visits grouped by day.</summary>
public sealed partial class HistoryPanel : UserControl
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo(L.T("en-US", "es-AR"));
    private const int MaxRows = 300;

    private HistoryService? _history;
    private long _confirmUntil;

    // Rows are updated in place (see SyncRows). Rebuilding the whole list every time history changes, which happens
    // often while a page loads, would destroy the row being clicked between press and release and lose the click.
    private readonly ObservableCollection<object> _rows = [];
    private readonly Dictionary<HistoryEntry, (string Title, string? Favicon)> _shown =
        new(ReferenceEqualityComparer.Instance);
    private int _rangeDays; // 0 = all, 1 = today, 7 or 30 = last days

    public event EventHandler<string>? UrlSelected;

    public HistoryPanel()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;
        UpdateRangeChips();
    }

    public void Attach(HistoryService history)
    {
        if (_history is not null) _history.HistoryChanged -= History_HistoryChanged;
        _history = history;
        _history.HistoryChanged += History_HistoryChanged;
        Refresh();
    }

    public void FocusSearch()
    {
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private void History_HistoryChanged(object? sender, EventArgs e) => Refresh();

    public void Refresh()
    {
        if (_history is null) return;
        var query = SearchBox.Text.Trim();
        var since = _rangeDays switch
        {
            0 => DateTime.MinValue,
            1 => DateTime.Today,
            _ => DateTime.Today.AddDays(-(_rangeDays - 1)),
        };
        var results = _history.Search(query, MaxRows * 4)
            .Where(e => e.LastVisit >= since)
            .Take(MaxRows)
            .ToList();

        // Search results are sorted by relevance, without day headers that would break the order.
        var rows = new List<object>(results.Count + 8);
        if (query.Length == 0)
        {
            DateTime? day = null;
            foreach (var entry in results)
            {
                if (day != entry.LastVisit.Date)
                {
                    day = entry.LastVisit.Date;
                    rows.Add(new HistoryDay(DayLabel(day.Value)));
                }
                rows.Add(entry);
            }
        }
        else
        {
            rows.AddRange(results);
        }

        SyncRows(rows);
        var empty = results.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = query.Length > 0 ? L.T($"Nothing matches “{query}”.", $"Nada coincide con “{query}”.")
            : _rangeDays > 0 ? L.T("Nothing visited in that period.", "No visitaste nada en ese período.")
            : L.T("Nothing in your history yet.", "Todavía no hay nada en el historial.");
    }

    /// <summary>Brings the list to <paramref name="next"/> touching only the rows that changed.</summary>
    private void SyncRows(List<object> next)
    {
        for (var i = 0; i < next.Count; i++)
        {
            var item = next[i];
            if (i < _rows.Count && SameRow(_rows[i], item))
            {
                if (item is HistoryEntry entry && ContentChanged(entry)) _rows[i] = item; // new title or icon
                continue;
            }

            if (i < _rows.Count && i + 1 < next.Count && SameRow(_rows[i], next[i + 1]))
            {
                _rows.Insert(i, item); // a row was added here; the rest stays put
            }
            else if (i + 1 < _rows.Count && SameRow(_rows[i + 1], item))
            {
                _rows.RemoveAt(i); // the row here is gone
            }
            else if (i < _rows.Count)
            {
                _rows[i] = item;
            }
            else
            {
                _rows.Add(item);
            }
            Remember(item);
        }
        while (_rows.Count > next.Count) _rows.RemoveAt(_rows.Count - 1);
    }

    private static bool SameRow(object a, object b) => (a, b) switch
    {
        (HistoryEntry x, HistoryEntry y) => ReferenceEquals(x, y),
        (HistoryDay x, HistoryDay y) => x.Label == y.Label,
        _ => false,
    };

    private bool ContentChanged(HistoryEntry entry)
    {
        if (_shown.TryGetValue(entry, out var shown) && shown.Title == entry.Title && shown.Favicon == entry.FaviconUrl)
            return false;
        Remember(entry);
        return true;
    }

    private void Remember(object item)
    {
        if (item is HistoryEntry entry) _shown[entry] = (entry.Title, entry.FaviconUrl);
    }

    private static string DayLabel(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return L.T("Today", "Hoy");
        if (day == today.AddDays(-1)) return L.T("Yesterday", "Ayer");
        if (day > today.AddDays(-7)) return Capitalize(day.ToString("dddd", Culture));
        return day.Year == today.Year
            ? day.ToString(L.T("MMMM d", "d 'de' MMMM"), Culture)
            : day.ToString(L.T("MMMM d, yyyy", "d 'de' MMMM 'de' yyyy"), Culture);
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], Culture) + text[1..];

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Entry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryEntry entry) UrlSelected?.Invoke(this, entry.Url);
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryEntry entry) _history?.Delete(entry.Url);
    }


    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        var open = FilterChips.Visibility != Visibility.Visible;
        FilterChips.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            Motion.Animate("history-filter", TimeSpan.FromMilliseconds(160), t =>
            {
                FilterChips.Opacity = t;
                FilterChips.Translation = new System.Numerics.Vector3(0, (float)Motion.Lerp(-6, 0, t), 0);
            });
        }
    }

    private void Range_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out var days)) return;
        _rangeDays = days;
        UpdateRangeChips();
        Refresh();
    }

    /// <summary>The selected chip is a white card; the Filter button shows the active range.</summary>
    private void UpdateRangeChips()
    {
        foreach (var chip in RangeChips.Children.OfType<Button>())
        {
            var selected = chip.Tag is string tag && tag == _rangeDays.ToString(CultureInfo.InvariantCulture);
            chip.Background = selected ? Brush("BeautyCardBrush") : Brush("BeautySubtleFillBrush");
            chip.Foreground = Brush(selected ? "BeautyTextBrush" : "BeautyTextSecondaryBrush");
        }
        FilterLabel.Text = _rangeDays switch { 1 => L.T("Today", "Hoy"), 7 => L.T("7 days", "7 días"), 30 => L.T("30 days", "30 días"), _ => L.T("Filter", "Filtro") };
    }

    /// <summary>The first tap asks for confirmation on the same button; a second tap within 4 s clears everything.</summary>
    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        var now = Environment.TickCount64;
        if (now < _confirmUntil)
        {
            _confirmUntil = 0;
            SetConfirming(false);
            _history?.ClearAll();
            return;
        }

        _confirmUntil = now + 4000;
        SetConfirming(true);
        await Task.Delay(4000);
        if (Environment.TickCount64 >= _confirmUntil)
        {
            _confirmUntil = 0;
            SetConfirming(false);
        }
    }

    private void SetConfirming(bool confirming)
    {
        ClearAllText.Text = confirming ? L.T("Tap again to clear everything", "Tocá otra vez para borrar todo") : L.T("Clear all history", "Borrar todo el historial");
        ClearAllButton.Foreground = Brush(confirming ? "BeautyDangerBrush" : "BeautyTextSecondaryBrush");
    }

    private static Microsoft.UI.Xaml.Media.Brush Brush(string key) =>
        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];

    // Hovering a row swaps the time for the remove button.
    private void Entry_PointerEntered(object sender, PointerRoutedEventArgs e) => SetRowHover(sender, true);
    private void Entry_PointerExited(object sender, PointerRoutedEventArgs e) => SetRowHover(sender, false);

    private static void SetRowHover(object sender, bool hover)
    {
        if (sender is Button { Content: Grid { Children.Count: > 3 } grid } &&
            grid.Children[3] is Grid { Children.Count: 2 } trailing)
        {
            trailing.Children[0].Opacity = hover ? 0 : 1;
            trailing.Children[1].Opacity = hover ? 1 : 0;
        }
    }
}
