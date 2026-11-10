using System.Numerics;
using BeautyBrowser.Shell;
using BeautyKit;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BeautyBrowser.Controls;

/// <summary>
/// The tab switcher (Ctrl+Tab), like Windows' Alt+Tab: the space's tabs with their thumbnails and a frame around the
/// selected one. <see cref="MainWindow"/> opens it, moves it with every Tab and, when Ctrl is released, goes to the
/// selected tab (<see cref="Commit"/>). Esc or a click outside cancels.
/// </summary>
public sealed partial class TabSwitcher : UserControl
{
    private const double CardWidth = 248;
    private const double ThumbnailHeight = 152;
    private const double CardHeight = ThumbnailHeight + 32; // thumbnail + title
    private const double Gap = 12;      // between cards
    private const double RingPad = 7;   // how far the frame shows around the card
    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(130);

    private BrowserShell? _shell;
    private readonly List<BrowserTab> _tabs = [];
    private int _index;
    private int _version; // a close in progress must not hide a switcher that opened again

    public TabSwitcher()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
        Cards.Spacing = Gap;
        Cards.Margin = new Thickness(RingPad);
        Ring.Width = CardWidth + RingPad * 2;
        Ring.Height = CardHeight + RingPad * 2;
        Ring.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(170) };
        Card.SizeChanged += (_, e) =>
            ElementCompositionPreview.GetElementVisual(Card).CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
    }

    public bool IsOpen { get; private set; }

    /// <summary>A tab picked with the mouse; the window opens it as if Ctrl had been released.</summary>
    public event EventHandler? CommitRequested;

    public event EventHandler? CancelRequested;

    public void Attach(BrowserShell shell) => _shell = shell;

    /// <summary>
    /// Opens the switcher with the frame on the next (+1) or previous (-1) tab, or just moves the frame when it is
    /// already open. Returns false when there is nowhere to switch to.
    /// </summary>
    public bool Step(int delta)
    {
        if (_shell is null) return false;
        if (IsOpen)
        {
            Select(Wrap(_index + delta), animate: true);
            return true;
        }

        // Open favorite tabs first, in their order, then the space's tabs.
        _tabs.Clear();
        _tabs.AddRange(_shell.OpenFavoriteTabs());
        _tabs.AddRange(_shell.CurrentSpace.Tabs);
        if (_tabs.Count < 2) return false;

        var current = _shell.CurrentTab is { } tab ? _tabs.IndexOf(tab) : -1;
        if (current < 0) current = delta > 0 ? -1 : 0;
        Build();
        Open();
        Select(Wrap(current + delta), animate: false);
        return true;
    }

    /// <summary>Closes and returns the selected tab, or null when canceled or already closed.</summary>
    public BrowserTab? Commit()
    {
        if (!IsOpen) return null;
        var tab = _index >= 0 && _index < _tabs.Count ? _tabs[_index] : null;
        Close();
        return tab;
    }

    public void Cancel() => Close();

    private int Wrap(int i) => ((i % _tabs.Count) + _tabs.Count) % _tabs.Count;


    private void Build()
    {
        // Keep it inside the window: with many tabs the strip slides so the frame stays in view.
        Scroller.MaxWidth = Math.Max(CardWidth + RingPad * 2, ActualWidth - 140);

        var scale = XamlRoot?.RasterizationScale ?? 1;
        Cards.Children.Clear();
        for (var i = 0; i < _tabs.Count; i++) Cards.Children.Add(BuildCard(_tabs[i], i, scale));
    }

    private FrameworkElement BuildCard(BrowserTab tab, int index, double scale)
    {
        var thumbnail = tab.CaptureThumbnail((int)Math.Round(CardWidth * scale), (int)Math.Round(ThumbnailHeight * scale));
        FrameworkElement preview = thumbnail is not null
            ? new Image { Source = thumbnail, Stretch = Stretch.UniformToFill }
            : Favicon(tab, 32);

        var title = new Grid
        {
            Height = 24,
            ColumnSpacing = 8,
            Margin = new Thickness(2, 8, 2, 0),
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
        };
        var icon = Favicon(tab, 16);
        var text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("BeautyTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        Grid.SetColumn(text, 1);
        title.Children.Add(icon);
        title.Children.Add(text);

        var card = new StackPanel
        {
            Width = CardWidth,
            Height = CardHeight,
            Background = new SolidColorBrush(Colors.Transparent), // the whole card takes the click
            Children =
            {
                new Border
                {
                    Height = ThumbnailHeight,
                    CornerRadius = new CornerRadius(9),
                    Background = new SolidColorBrush(ColorHelper.FromArgb(255, 241, 241, 244)),
                    BorderBrush = Brush("BeautyInsetBorderBrush"),
                    BorderThickness = new Thickness(1),
                    Child = preview,
                },
                title,
            },
        };
        card.Tapped += (_, e) =>
        {
            e.Handled = true;
            Select(index, animate: true);
            CommitRequested?.Invoke(this, EventArgs.Empty);
        };
        return card;
    }

    private static FrameworkElement Favicon(BrowserTab tab, double size)
    {
        FrameworkElement icon = tab.Favicon is { } source
            ? new Image { Source = source, Width = size, Height = size }
            : new FontIcon { Glyph = "", FontSize = size, Foreground = Brush("BeautyTextSecondaryBrush") };
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }

    private void Select(int index, bool animate)
    {
        _index = index;
        var x = (float)(index * (CardWidth + Gap));
        if (!animate)
        {
            var transition = Ring.TranslationTransition;
            Ring.TranslationTransition = null;
            Ring.Translation = new Vector3(x, 0, 0);
            Ring.TranslationTransition = transition;
        }
        else
        {
            Ring.Translation = new Vector3(x, 0, 0);
        }

        for (var i = 0; i < Cards.Children.Count; i++)
            Cards.Children[i].Opacity = i == index ? 1 : 0.78; // the selected one stands out a little

        // Keep the frame in view.
        DispatcherQueue.TryEnqueue(() =>
        {
            var left = x;
            var right = x + CardWidth + RingPad * 2;
            var offset = Scroller.HorizontalOffset;
            var viewport = Scroller.ViewportWidth;
            if (viewport <= 0) return;
            if (left < offset) Scroller.ChangeView(left, null, null, !animate);
            else if (right > offset + viewport) Scroller.ChangeView(right - viewport, null, null, !animate);
        });
    }


    private void Open()
    {
        _version++;
        IsOpen = true;
        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        Scroller.ChangeView(0, null, null, true);

        var root = ElementCompositionPreview.GetElementVisual(this);
        var card = ElementCompositionPreview.GetElementVisual(Card);
        var c = root.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.25f, 1f));

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = OpenDuration;
        root.StartAnimation("Opacity", fade);

        var grow = c.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(0, new Vector3(0.94f, 0.94f, 1));
        grow.InsertKeyFrame(1, Vector3.One, ease);
        grow.Duration = OpenDuration;
        card.StartAnimation("Scale", grow);
    }

    private void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        IsHitTestVisible = false;
        var version = ++_version;

        var root = ElementCompositionPreview.GetElementVisual(this);
        var card = ElementCompositionPreview.GetElementVisual(Card);
        var c = root.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0, ease);
        fade.Duration = CloseDuration;
        root.StartAnimation("Opacity", fade);

        var shrink = c.CreateVector3KeyFrameAnimation();
        shrink.InsertKeyFrame(1, new Vector3(0.97f, 0.97f, 1), ease);
        shrink.Duration = CloseDuration;
        card.StartAnimation("Scale", shrink);

        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (version != _version) return; // reopened while closing
            Visibility = Visibility.Collapsed;
            Cards.Children.Clear(); // release the thumbnails
            _tabs.Clear();
        });
    }

    private void Scrim_Tapped(object sender, TappedRoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
    private void Card_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true; // a tap inside the card does not cancel

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
