using System.Numerics;
using BeautyBrowser.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace BeautyBrowser.Controls;

public enum LibrarySection { History, Downloads, Settings, Spaces, Extensions }

/// <summary>
/// The library: a section rail (downloads, spaces, history, extensions and settings) and the section's list, docked to
/// the left edge while the page stays open on the right.
/// </summary>
public sealed partial class LibraryPanel : UserControl
{
    private BrowserShell? _shell;

    public LibraryPanel()
    {
        InitializeComponent();
        RailPill.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(220) };
        RailPill.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) };
        Loaded += (_, _) => Select(Section, animate: false); // measured by now: place the rail card
    }

    /// <summary>The user wants to close the library (arrow or Esc).</summary>
    public event EventHandler? CloseRequested;

    public LibrarySection Section { get; private set; } = LibrarySection.History;

    /// <summary>Where the download animation lands while the library is open.</summary>
    public FrameworkElement DownloadsTarget => DownloadsTab;

    public void Attach(BrowserShell shell)
    {
        _shell = shell;
        HistoryView.Attach(shell.History);
        // Whatever is picked opens in the page on the right; the library stays open.
        HistoryView.UrlSelected += (_, url) => _shell?.Open(url, inNewTab: false);
        DownloadsHost.Children.Add(new DownloadsPanel(shell.Downloads));
        SpacesView.Attach(shell);
        SettingsView.Attach(shell);
        ExtensionsView.Attach(shell);
    }

    public void Show(LibrarySection section)
    {
        if (section == LibrarySection.Spaces) SpacesView.Refresh(); // keep tab counts current
        Select(section, animate: IsLoaded && Section != section);
        if (section == LibrarySection.History)
            DispatcherQueue.TryEnqueue(() => HistoryView.FocusSearch()); // typing searches right away
        else
            Focus(FocusState.Programmatic);
    }

    /// <summary>Scrolls the settings to the rows that clear history, cookies and cache.</summary>
    public void RevealClearData() => SettingsView.RevealClearData();

    /// <summary>Spaces, with the new space form open above the existing ones.</summary>
    public void StartNewSpace()
    {
        Show(LibrarySection.Spaces);
        SpacesView.StartNewSpace();
    }

    /// <summary>Bounce of the downloads button when a download arrives.</summary>
    public void PlayDownloadsBounce()
    {
        DownloadsIcon.Foreground = Brush("BeautyAccentBrush");
        Motion.Animate("library-downloads-bounce", TimeSpan.FromMilliseconds(420),
            t => DownloadsScale.ScaleX = DownloadsScale.ScaleY = 1 + 0.25 * Math.Sin(Math.PI * t),
            ease: t => t,
            completed: () => DownloadsIcon.ClearValue(FontIcon.ForegroundProperty));
    }

    private void Select(LibrarySection section, bool animate)
    {
        Section = section;

        // The white card slides to the rail's section.
        var railTab = section switch
        {
            LibrarySection.Downloads => DownloadsTab,
            LibrarySection.Spaces => SpacesTab,
            LibrarySection.History => HistoryTab,
            LibrarySection.Extensions => ExtensionsTab,
            LibrarySection.Settings => SettingsTab,
            _ => null,
        };
        if (railTab is not null && railTab.ActualHeight > 0)
        {
            var y = railTab.TransformToVisual(RailButtons).TransformPoint(new Point(0, 0)).Y;
            var transition = RailPill.TranslationTransition;
            if (!animate) RailPill.TranslationTransition = null;
            RailPill.Translation = new Vector3(0, (float)y, 0);
            if (!animate) RailPill.TranslationTransition = transition;
        }
        RailPill.Opacity = railTab is null ? 0 : 1;

        foreach (var tab in new[] { DownloadsTab, SpacesTab, HistoryTab, ExtensionsTab, SettingsTab })
            tab.Foreground = Brush(tab == railTab ? "BeautyTextBrush" : "BeautyTextSecondaryBrush");

        // Only the selected section's list is visible.
        var view = section switch
        {
            LibrarySection.Downloads => (FrameworkElement)DownloadsHost,
            LibrarySection.Spaces => SpacesView,
            LibrarySection.Settings => SettingsView,
            LibrarySection.Extensions => ExtensionsView,
            _ => HistoryView,
        };
        foreach (var child in SectionHost.Children.OfType<FrameworkElement>())
            child.Visibility = child == view ? Visibility.Visible : Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(() => ScrollSnap.AttachAll(view)); // once its content is built

        if (animate) AnimateSectionIn(view);
    }

    /// <summary>
    /// Soft entrance of the content: it fades in and rises a few pixels, animated on the composition thread so it never
    /// stutters even when the new section has a lot to draw.
    /// </summary>
    private static void AnimateSectionIn(FrameworkElement view)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(view, true);
        var visual = ElementCompositionPreview.GetElementVisual(view);
        var c = visual.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.25f, 1f));
        var duration = TimeSpan.FromMilliseconds(200);

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = duration;
        visual.StartAnimation("Opacity", fade);

        var rise = c.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new Vector3(0, 10, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, ease);
        rise.Duration = duration;
        visual.StartAnimation("Translation", rise);
    }

    private void Rail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<LibrarySection>(tag, out var section))
            Show(section);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Library_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
