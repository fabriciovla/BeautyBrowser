using System.ComponentModel;
using BeautyBrowser.Shell;
using BeautyKit;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace BeautyBrowser.Controls;

/// <summary>
/// The now playing card at the bottom of the sidebar, showing <see cref="BrowserShell.NowPlaying"/>. It appears when a
/// tab starts playing and goes away when the tab closes, navigates or the X is clicked.
/// </summary>
public sealed partial class MediaCard : UserControl
{
    private const double DetailsHeight = 96;
    private static readonly double[] BarPeaks = [0.9, 0.55, 1.0, 0.7];
    private static readonly int[] BarPeriodsMs = [430, 520, 380, 610];

    private BrowserShell? _shell;
    private BrowserTab? _tab;
    private string? _artworkUrl;
    private bool _expanded;
    private readonly Storyboard _bars = new();
    private readonly List<ScaleTransform> _barScales = [];
    private readonly DispatcherQueueTimer _clock;

    public MediaCard()
    {
        InitializeComponent();
        BuildBars();
        Visibility = Visibility.Collapsed;

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(500);
        _clock.Tick += (_, _) => RefreshProgress();
    }

    /// <summary>The card expanded or collapsed; the sidebar stays open while it is expanded.</summary>
    public bool IsExpanded => _expanded;

    public void Attach(BrowserShell shell)
    {
        _shell = shell;
        shell.NowPlayingChanged += (_, _) => Watch(shell.NowPlaying);
        Watch(shell.NowPlaying);
    }

    private void Watch(BrowserTab? tab)
    {
        if (_tab == tab) return;
        if (_tab is not null) _tab.PropertyChanged -= Tab_PropertyChanged;
        _tab = tab;
        if (_tab is not null) _tab.PropertyChanged += Tab_PropertyChanged;
        Refresh();
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserTab.Media) or nameof(BrowserTab.IsAudioMuted) or nameof(BrowserTab.Favicon)) Refresh();
    }

    private void Refresh()
    {
        var media = _tab?.Media;
        if (_tab is null || media is null)
        {
            Visibility = Visibility.Collapsed;
            _bars.Stop();
            _clock.Stop();
            SetExpanded(false, animate: false);
            return;
        }

        var appearing = Visibility == Visibility.Collapsed;
        Visibility = Visibility.Visible;
        TitleText.Text = media.Title;
        ArtistText.Text = media.Artist;
        ToolTipService.SetToolTip(Card, $"{media.Title}\n{media.Artist}");

        // The page's artwork (MediaSession), or the site icon when it has none.
        if (media.ArtworkUrl != _artworkUrl)
        {
            _artworkUrl = media.ArtworkUrl;
            Artwork.Source = Uri.TryCreate(media.ArtworkUrl, UriKind.Absolute, out var art) && art.Scheme is "http" or "https"
                ? new BitmapImage(art) { DecodePixelWidth = 96 }
                : null;
        }
        FallbackIcon.Source = _tab.Favicon;

        PlayIcon.Glyph = media.IsPlaying ? "" : "";
        ToolTipService.SetToolTip(PlayButton, media.IsPlaying ? L.T("Pause", "Pausa") : L.T("Play", "Reproducir"));
        MuteIcon.Glyph = _tab.IsAudioMuted ? "" : "";
        ToolTipService.SetToolTip(MuteButton, _tab.IsAudioMuted ? L.T("Unmute", "Activar sonido") : L.T("Mute tab", "Silenciar pestaña"));
        NextButton.IsEnabled = media.HasNext;
        NextButton.Opacity = media.HasNext ? 1 : 0.35;

        // Paused, the bars stay still and low (Stop releases the animated values).
        if (media.IsPlaying) { if (_bars.GetCurrentState() != ClockState.Active) _bars.Begin(); }
        else { _bars.Stop(); foreach (var s in _barScales) s.ScaleY = 0.3; }

        if (media.IsPlaying || _expanded) _clock.Start(); else _clock.Stop();
        RefreshProgress();

        if (appearing)
        {
            Opacity = 0;
            Motion.Animate("media-card-in", TimeSpan.FromMilliseconds(220), t =>
            {
                Opacity = t;
                Translation = new System.Numerics.Vector3(0, (float)Motion.Lerp(10, 0, t), 0);
            });
        }
    }

    private void RefreshProgress()
    {
        if (_tab?.Media is not { } media) return;
        if (media.IsLive)
        {
            ElapsedText.Text = L.T("LIVE", "EN VIVO");
            RemainingText.Text = "";
            TrackFill.Width = Track.ActualWidth;
            return;
        }
        var position = media.PositionNow;
        var fraction = media.Duration > 0 ? Math.Clamp(position / media.Duration, 0, 1) : 0;
        TrackFill.Width = Track.ActualWidth * fraction;
        ElapsedText.Text = Time(position);
        RemainingText.Text = media.Duration > 0 ? "-" + Time(media.Duration - position) : "";
    }

    private static string Time(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }


    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e) => SetExpanded(true, animate: true);
    private void Card_PointerExited(object sender, PointerRoutedEventArgs e) => SetExpanded(false, animate: true);

    private void SetExpanded(bool expanded, bool animate)
    {
        if (_expanded == expanded && animate) return;
        _expanded = expanded;
        CloseButton.IsHitTestVisible = expanded;
        if (expanded) { _clock.Start(); RefreshProgress(); }

        var fromHeight = Details.Height;
        var fromOpacity = Details.Opacity;
        double toHeight = expanded ? DetailsHeight : 0, toOpacity = expanded ? 1 : 0;
        if (!animate)
        {
            Motion.Cancel("media-card-expand");
            Details.Height = toHeight;
            Details.Opacity = toOpacity;
            Bars.Opacity = expanded ? 0 : 1;
            CloseButton.Opacity = expanded ? 1 : 0;
            return;
        }
        Motion.Animate("media-card-expand", TimeSpan.FromMilliseconds(expanded ? 220 : 180), t =>
        {
            Details.Height = Motion.Lerp(fromHeight, toHeight, t);
            Details.Opacity = Motion.Lerp(fromOpacity, toOpacity, expanded ? Math.Clamp(t * 1.4 - 0.4, 0, 1) : t);
            Bars.Opacity = 1 - Details.Opacity;
            CloseButton.Opacity = Details.Opacity;
        });
    }


    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Clicking the card (not a button or the bar) goes to the tab.
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != Card; element = VisualTreeHelper.GetParent(element))
            if (element is ButtonBase || element == Track) return;
        OpenTab();
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenTab();

    private void OpenTab()
    {
        if (_tab is not null) _shell?.ShowTab(_tab);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => _tab?.MediaPlayPause();
    private void Previous_Click(object sender, RoutedEventArgs e) => _tab?.MediaPrevious();
    private void Next_Click(object sender, RoutedEventArgs e) => _tab?.MediaNext();
    private void Mute_Click(object sender, RoutedEventArgs e) { if (_tab is not null) _tab.SetAudioMuted(!_tab.IsAudioMuted); }
    private void Close_Click(object sender, RoutedEventArgs e) => _tab?.MediaDismiss();

    private void Track_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_tab?.Media is not { Duration: > 0, IsLive: false } media || Track.ActualWidth <= 0) return;
        var fraction = Math.Clamp(e.GetCurrentPoint(Track).Position.X / Track.ActualWidth, 0, 1);
        _tab.MediaSeek(fraction * media.Duration);
        TrackFill.Width = Track.ActualWidth * fraction;
        e.Handled = true;
    }


    private void BuildBars()
    {
        var brush = (Brush)Application.Current.Resources["BeautyAccentBrush"];
        for (var i = 0; i < BarPeaks.Length; i++)
        {
            var scale = new ScaleTransform { ScaleY = 0.3, CenterY = 8 };
            _barScales.Add(scale);
            var bar = new Rectangle
            {
                Width = 2.5,
                Height = 16,
                RadiusX = 1.25,
                RadiusY = 1.25,
                Fill = brush,
                RenderTransform = scale,
            };
            Bars.Children.Add(bar);

            var animation = new DoubleAnimation
            {
                From = 0.25,
                To = BarPeaks[i],
                Duration = TimeSpan.FromMilliseconds(BarPeriodsMs[i]),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                EnableDependentAnimation = false,
            };
            Storyboard.SetTarget(animation, bar);
            Storyboard.SetTargetProperty(animation, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
            _bars.Children.Add(animation);
        }
    }
}
