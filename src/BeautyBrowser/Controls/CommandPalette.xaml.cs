using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;
using BeautyBrowser.Shell;
using BeautyKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;

namespace BeautyBrowser.Controls;

public enum PaletteMode
{
    /// <summary>Ctrl+T: whatever is picked opens in a new tab.</summary>
    NewTab,

    /// <summary>Ctrl+L or a click on the URL: navigates the current tab.</summary>
    Navigate,
}

public enum PaletteResultKind { OpenTab, Url, Search, History }

public sealed class PaletteResult
{
    public PaletteResultKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Glyph { get; init; } = ""; // globe
    public ImageSource? Icon { get; init; }
    public string? Url { get; init; }
    public BrowserTab? Tab { get; init; }

    public bool IsOpenTab => Kind == PaletteResultKind.OpenTab;
    public string SubtitleWithDash => string.IsNullOrEmpty(Subtitle) ? "" : "  —  " + Subtitle;
}

/// <summary>
/// The central search box (Ctrl+T): what was typed, the best match from history, live Google suggestions, open tabs
/// and history.
/// </summary>
public sealed partial class CommandPalette : UserControl
{
    private const int MaxResults = 8;
    private const int MaxTopPages = 5;
    private const int MaxSuggestions = 4;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly ObservableCollection<PaletteResult> _items = [];
    private BrowserShell? _shell;
    private PaletteMode _mode;
    private bool _userMovedSelection;

    // Latest Google suggestions, reused filtered while new ones arrive so the list does not jump.
    private IReadOnlyList<string> _suggestions = [];
    private CancellationTokenSource? _suggestionsRequest;

    public CommandPalette()
    {
        InitializeComponent();
        Results.ItemsSource = _items;
        Card.Shadow = new ThemeShadow();
        Card.Translation = new Vector3(0, 0, 48);
        SizeChanged += (_, _) => PlaceCard();
    }

    // With tabs on top the address bar itself is the search box: the palette only shows the results, as a list
    // dropping from it (OpenAttached).
    private TextBox? _external;
    private Rect _anchor;

    /// <summary>The box being typed in: the palette's own, or the address bar's.</summary>
    private TextBox Input => _external ?? Query;

    private void PlaceCard()
    {
        if (_external is not null)
        {
            Card.HorizontalAlignment = HorizontalAlignment.Left;
            Card.MaxWidth = double.PositiveInfinity;
            Card.Width = _anchor.Width;
            Card.Margin = new Thickness(_anchor.X, _anchor.Bottom + 6, 0, 0);
            Card.RenderTransformOrigin = new Point(0.5, 0);
        }
        else
        {
            Card.HorizontalAlignment = HorizontalAlignment.Stretch;
            Card.MaxWidth = 760;
            Card.Width = double.NaN;
            Card.Margin = new Thickness(40, ActualHeight * 0.26, 40, 0);
            Card.RenderTransformOrigin = new Point(0.5, 0.5);
        }
    }

    /// <summary>
    /// Results for a box outside the palette (the address bar with tabs on top): the list drops right below it, the
    /// box keeps the focus and the keys (arrows, Enter, Tab, Esc) still move through the results. The page stays
    /// clickable; the box losing focus closes it.
    /// </summary>
    public void OpenAttached(BrowserShell shell, PaletteMode mode, TextBox input, Rect anchor)
    {
        Detach();
        _shell = shell;
        _mode = mode;
        _suggestions = [];
        _userMovedSelection = false;
        _external = input;
        _anchor = anchor;
        input.TextChanged += Query_TextChanged;
        input.PreviewKeyDown += Query_PreviewKeyDown;
        SearchRow.Visibility = Visibility.Collapsed;
        Divider.Visibility = Visibility.Collapsed;
        Backdrop.Background = null; // clicks reach the page and the address bar

        Visibility = Visibility.Visible;
        PlaceCard();
        Refresh();
        RequestSuggestions(input.Text.Trim());
        Motion.Animate("palette", TimeSpan.FromMilliseconds(120), t =>
        {
            Card.Opacity = t;
            CardScale.ScaleX = CardScale.ScaleY = Motion.Lerp(0.985, 1, t);
        });
    }

    private void Detach()
    {
        if (_external is not { } input) return;
        input.TextChanged -= Query_TextChanged;
        input.PreviewKeyDown -= Query_PreviewKeyDown;
        _external = null;
        SearchRow.Visibility = Visibility.Visible;
        Divider.Visibility = Visibility.Visible;
        Backdrop.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0));
    }

    public bool IsAttached => _external is not null;

    public bool IsOpen => Visibility == Visibility.Visible;

    public event EventHandler? Closed;

    public void Open(BrowserShell shell, PaletteMode mode, string initialText = "")
    {
        Detach();
        _shell = shell;
        _mode = mode;
        _suggestions = [];
        _userMovedSelection = false;

        Visibility = Visibility.Visible;
        PlaceCard();
        // The address is shown readable ("España", not "Espa%C3%B1a") without changing where it goes.
        Query.Text = mode == PaletteMode.Navigate ? AddressDisplay.ToDisplay(initialText) : initialText;
        Query.SelectAll();
        Refresh();
        Query.Focus(FocusState.Programmatic);

        Motion.Animate("palette", TimeSpan.FromMilliseconds(140), t =>
        {
            Card.Opacity = t;
            CardScale.ScaleX = CardScale.ScaleY = Motion.Lerp(0.97, 1, t);
        });
    }

    public void Close()
    {
        if (!IsOpen) return;
        if (_external is { } input)
        {
            // The address bar gets its text back right away; that must not search again while the list fades.
            input.TextChanged -= Query_TextChanged;
            input.PreviewKeyDown -= Query_PreviewKeyDown;
        }
        Motion.Animate("palette", TimeSpan.FromMilliseconds(100), t =>
        {
            Card.Opacity = 1 - t;
            CardScale.ScaleX = CardScale.ScaleY = Motion.Lerp(1, 0.98, t);
        }, completed: () =>
        {
            Visibility = Visibility.Collapsed;
            Detach();
            Closed?.Invoke(this, EventArgs.Empty);
        });
    }


    private void Refresh()
    {
        if (_shell is null) return;
        var query = Input.Text.Trim();
        var results = new List<PaletteResult>();

        // The private space neither shows the history nor mixes in the other spaces' tabs, and the other spaces do
        // not show its tabs.
        var isPrivate = _shell.CurrentSpace?.IsPrivate == true;

        // 1) The best history site whose address starts with what was typed ("you" → youtube.com).
        var bestSite = query.Length > 0 && !isPrivate
            ? _shell.History.Search(query, 20).FirstOrDefault(h => HostStartsWith(h.Url, query))
            : null;
        if (bestSite is not null)
        {
            results.Add(new PaletteResult
            {
                Kind = PaletteResultKind.Url,
                Title = ShortUrl(bestSite.Url),
                Subtitle = bestSite.Title,
                Icon = IconFor(bestSite.FaviconUrl),
                Url = new Uri(bestSite.Url).GetLeftPart(UriPartial.Authority), // scheme + host + port
            });
        }

        // 2) What was typed: go to the site or search for it.
        if (query.Length > 0)
        {
            results.Add(LooksLikeUrl(query)
                ? new PaletteResult { Kind = PaletteResultKind.Url, Title = query, Subtitle = L.T("Go to site", "Ir al sitio"), Glyph = "", Url = query }
                : new PaletteResult { Kind = PaletteResultKind.Search, Title = query, Subtitle = L.T($"Search with {_shell.Settings.SearchEngine}", $"Buscar con {_shell.Settings.SearchEngine}"), Glyph = "", Url = query });

            // 3) Google suggestions: the latest that arrived, filtered by what still matches.
            foreach (var suggestion in _suggestions
                         .Where(s => s.StartsWith(query, StringComparison.OrdinalIgnoreCase) &&
                                     !s.Equals(query, StringComparison.OrdinalIgnoreCase))
                         .Take(MaxSuggestions))
            {
                results.Add(new PaletteResult { Kind = PaletteResultKind.Search, Title = suggestion, Glyph = "", Url = suggestion });
            }
        }

        // 4) Open tabs that match (or, without text, the current space's).
        var tabs = _shell.Spaces.Where(s => s.IsPrivate == isPrivate).SelectMany(s => s.Tabs)
            .Where(t => t != _shell.CurrentTab || _mode == PaletteMode.NewTab)
            .Where(t => query.Length == 0 || Matches(t.Title, t.Url, query))
            .Take(query.Length == 0 ? 3 : 4);
        foreach (var tab in tabs)
        {
            results.Add(new PaletteResult
            {
                Kind = PaletteResultKind.OpenTab,
                Title = tab.Title,
                Subtitle = query.Length > 0 ? ShortUrl(tab.Url) : "",
                Icon = tab.Favicon,
                Tab = tab,
            });
        }

        // 5) Without text, the 5 exact pages visited most often (twitch.tv/somestreamer), skipping open tabs.
        if (query.Length == 0)
        {
            var open = _shell.Spaces.SelectMany(s => s.Tabs).Select(t => t.Url).ToHashSet();
            foreach (var page in isPrivate ? [] : _shell.History.TopPages(MaxTopPages + open.Count).Where(p => !open.Contains(p.Url)).Take(MaxTopPages))
            {
                results.Add(new PaletteResult
                {
                    Kind = PaletteResultKind.Url,
                    Title = string.IsNullOrWhiteSpace(page.Title) ? ShortUrl(page.Url, keepPath: true) : page.Title,
                    Subtitle = ShortUrl(page.Url, keepPath: true),
                    Icon = IconFor(page.FaviconUrl),
                    Url = page.Url,
                });
            }
            Show(results.ToList());
            return;
        }

        // 6) History, without repeating open tabs or the best site.
        var openUrls = _shell.Spaces.SelectMany(s => s.Tabs).Select(t => t.Url).ToHashSet();
        var history = isPrivate ? [] : _shell.History.Search(query, 12);
        foreach (var entry in history.Where(h => !openUrls.Contains(h.Url) && h != bestSite))
        {
            if (results.Count >= MaxResults) break;
            results.Add(new PaletteResult
            {
                Kind = PaletteResultKind.History,
                Title = entry.Title,
                Subtitle = ShortUrl(entry.Url, keepPath: true),
                Icon = IconFor(entry.FaviconUrl),
                Url = entry.Url,
            });
        }

        Show(results.Take(MaxResults).ToList());
    }

    /// <summary>Updates the list in place, changing only the rows that differ, so it neither flickers nor rebuilds on every key.</summary>
    private void Show(List<PaletteResult> results)
    {
        var selected = _userMovedSelection ? Results.SelectedItem as PaletteResult : null;

        for (var i = 0; i < results.Count; i++)
        {
            if (i >= _items.Count) _items.Add(results[i]);
            else if (!SameRow(_items[i], results[i])) _items[i] = results[i];
        }
        while (_items.Count > results.Count) _items.RemoveAt(_items.Count - 1);

        var keep = selected is null ? -1 : _items.ToList().FindIndex(r => SameRow(r, selected));
        Results.SelectedIndex = keep >= 0 ? keep : _items.Count > 0 ? 0 : -1;

        var any = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Results.Visibility = any;
        Divider.Visibility = any;
    }

    private static bool SameRow(PaletteResult a, PaletteResult b) =>
        a.Kind == b.Kind && a.Title == b.Title && a.Subtitle == b.Subtitle && a.Url == b.Url && a.Tab == b.Tab;

    /// <summary>Asks Google for suggestions (with a short pause between keystrokes) and refreshes when they arrive.</summary>
    private async void RequestSuggestions(string query)
    {
        _suggestionsRequest?.Cancel();
        if (query.Length == 0 || LooksLikeUrl(query)) return;

        var request = _suggestionsRequest = new CancellationTokenSource();
        try
        {
            await Task.Delay(60, request.Token);
            var url = "https://suggestqueries.google.com/complete/search?client=firefox&hl=" + L.T("en", "es") + "&ie=utf-8&oe=utf-8&q=" +
                      Uri.EscapeDataString(query);
            var json = await Http.GetStringAsync(url, request.Token);

            // Format: ["query", ["suggestion 1", "suggestion 2", …]]
            using var doc = JsonDocument.Parse(json);
            var list = doc.RootElement[1].EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();

            if (request.IsCancellationRequested || Input.Text.Trim() != query) return;
            _suggestions = list;
            Refresh();
        }
        catch
        {
            // Offline or canceled: keep going with local results.
        }
    }

    private void Execute(PaletteResult? result)
    {
        if (_shell is null) return;
        var query = Input.Text.Trim();

        if (result?.Tab is { } tab) _shell.SelectTab(tab);
        else if (result?.Url is { } url) _shell.Open(url, inNewTab: _mode == PaletteMode.NewTab);
        else if (query.Length > 0) _shell.Open(query, inNewTab: _mode == PaletteMode.NewTab);
        else return;

        Close();
    }


    private static bool CtrlDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// Ctrl+Enter, as in Chrome: "google" → https://www.google.com. Only a bare name (no scheme, dot, port, path or
    /// spaces) is completed; null means Enter works as usual.
    /// </summary>
    internal static string? CompleteDotCom(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))) return null;
        return "https://www." + text + ".com";
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        _userMovedSelection = false;
        Refresh();
        RequestSuggestions(Input.Text.Trim());
    }

    private void Query_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var count = _items.Count;
        switch (e.Key)
        {
            case VirtualKey.Down when count > 0:
                _userMovedSelection = true;
                Results.SelectedIndex = (Results.SelectedIndex + 1) % count;
                Results.ScrollIntoView(Results.SelectedItem);
                break;
            case VirtualKey.Up when count > 0:
                _userMovedSelection = true;
                Results.SelectedIndex = (Results.SelectedIndex - 1 + count) % count;
                Results.ScrollIntoView(Results.SelectedItem);
                break;
            case VirtualKey.Enter when CtrlDown() && CompleteDotCom(Input.Text) is { } completed:
                _shell?.Open(completed, inNewTab: _mode == PaletteMode.NewTab);
                Close();
                break;
            case VirtualKey.Enter:
                Execute(Results.SelectedItem as PaletteResult);
                break;
            case VirtualKey.Escape:
                Close();
                break;
            // Tab completes the box with the highlighted result ("you" → "youtube.com"), to keep typing after it.
            case VirtualKey.Tab when Completion(Results.SelectedItem as PaletteResult) is { } completion
                                     && !completion.Equals(Input.Text.Trim(), StringComparison.OrdinalIgnoreCase):
                Input.Text = completion;
                Input.SelectionStart = completion.Length;
                break;
            case VirtualKey.Tab:
                break; // focus stays in the box
            default:
                return;
        }
        e.Handled = true;
    }

    // What Tab writes in the box for a result: the address without "https://www." for pages, the text for searches.
    private static string? Completion(PaletteResult? result) => result switch
    {
        null => null,
        { Kind: PaletteResultKind.Search } => result.Title,
        { Kind: PaletteResultKind.OpenTab, Tab: { } tab } => ShortUrl(tab.Url, keepPath: true),
        { Url: { Length: > 0 } url } when Uri.TryCreate(url, UriKind.Absolute, out _) => ShortUrl(url, keepPath: true),
        { Url: { Length: > 0 } url } => url,
        _ => null,
    };

    private void Results_ItemClick(object sender, ItemClickEventArgs e) => Execute(e.ClickedItem as PaletteResult);

    private void Backdrop_Tapped(object sender, TappedRoutedEventArgs e) => Close();

    private void Card_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true; // keep it from reaching the backdrop


    private static bool Matches(string title, string url, string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term =>
            title.Contains(term, StringComparison.OrdinalIgnoreCase) || url.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool HostStartsWith(string url, string query) =>
        !query.Contains(' ') &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        HostName.WithoutWww(uri.Host).StartsWith(query, StringComparison.OrdinalIgnoreCase);

    private static ImageSource? IconFor(string? faviconUrl) => Favicons.FromUrl(faviconUrl);

    private static bool LooksLikeUrl(string text) =>
        !text.Contains(' ') && (text.Contains('.') || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("://", StringComparison.Ordinal));

    private static string ShortUrl(string url, bool keepPath = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return url;
        var host = HostName.WithoutWww(uri.Host);
        if (!uri.IsDefaultPort) host += ":" + uri.Port;
        return keepPath && uri.AbsolutePath != "/" ? host + uri.AbsolutePath : host;
    }
}
