using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Point = Windows.Foundation.Point;

namespace BeautyBrowser.Controls;

/// <summary>
/// The active tab's white card (<c>TabIndicator</c> in Sidebar.xaml). The ListViewItem does not paint it: it sits behind
/// the list and slides to the selected tab. It moves on the composition thread, bound to the list's scroll, so it never
/// lags behind while scrolling.
/// </summary>
public sealed partial class Sidebar
{
    private static readonly TimeSpan IndicatorSlide = TimeSpan.FromMilliseconds(280);

    private Compositor? _compositor;
    private Visual? _indicatorVisual;
    private CompositionPropertySet? _indicatorProps;  // "Y": the card's top in content coordinates, without scroll
    private CompositionEasingFunction? _indicatorEase;
    private ScrollViewer? _tabScroll;

    private float _indicatorTarget = float.NaN;
    private bool _indicatorShown;
    private bool _indicatorSnapNext = true;          // place it without animating next time (on open or space change)

    private void TabList_Loaded(object sender, RoutedEventArgs e)
    {
        if (_indicatorVisual is not null) return;

        _tabScroll = FindDescendant<ScrollViewer>(TabList);

        // Whatever leaves the list area (a tab scrolled out of view) is not drawn.
        var hostVisual = ElementCompositionPreview.GetElementVisual(TabIndicatorHost);
        var c = _compositor = hostVisual.Compositor;
        hostVisual.Clip = c.CreateInsetClip(0, 0, 0, 0);

        ElementCompositionPreview.SetIsTranslationEnabled(TabIndicator, true);
        _indicatorVisual = ElementCompositionPreview.GetElementVisual(TabIndicator);
        _indicatorVisual.Opacity = 0;

        // Position = its own animatable Y + the list's scroll offset.
        _indicatorProps = c.CreatePropertySet();
        _indicatorProps.InsertScalar("Y", 0);
        var translation = c.CreateExpressionAnimation(_tabScroll is not null
            ? "Vector3(0, props.Y + scroll.Translation.Y, 0)"
            : "Vector3(0, props.Y, 0)");
        translation.SetReferenceParameter("props", _indicatorProps);
        if (_tabScroll is not null)
            translation.SetReferenceParameter("scroll", ElementCompositionPreview.GetScrollViewerManipulationPropertySet(_tabScroll));
        _indicatorVisual.StartAnimation("Translation", translation);

        _indicatorEase = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.25f, 1f));

        // Any layout change (new tab, closed tab, resize) moves the card.
        TabList.LayoutUpdated += (_, _) => PlaceTabIndicator(animate: true);
        PlaceTabIndicator(animate: false);
    }

    /// <summary>Moves the card to the selected tab, or hides it when the list has none.</summary>
    private void PlaceTabIndicator(bool animate)
    {
        if (_indicatorVisual is null || _indicatorProps is null || _compositor is null) return;

        if (TabList.SelectedItem is not { } item)
        {
            HideTabIndicator();
            return;
        }
        if (TabList.ContainerFromItem(item) is not ListViewItem container || container.ActualHeight <= 0)
            return; // not on screen yet: the next LayoutUpdated retries

        if (TabIndicator.Height != container.ActualHeight) TabIndicator.Height = container.ActualHeight;

        // The tab's top in content coordinates (the current scroll is added back to remove it).
        var top = container.TransformToVisual(TabIndicatorHost).TransformPoint(new Point(0, 0)).Y;
        var y = (float)(top + (_tabScroll?.VerticalOffset ?? 0));

        var appearing = !_indicatorShown;
        if (!appearing && Math.Abs(y - _indicatorTarget) < 0.5f) return;
        _indicatorTarget = y;

        if (animate && !appearing && !_indicatorSnapNext)
        {
            var slide = _compositor.CreateScalarKeyFrameAnimation();
            slide.InsertKeyFrame(1, y, _indicatorEase);
            slide.Duration = IndicatorSlide;
            _indicatorProps.StartAnimation("Y", slide);
        }
        else
        {
            _indicatorProps.StopAnimation("Y");
            _indicatorProps.InsertScalar("Y", y);
        }
        _indicatorSnapNext = false;

        if (appearing)
        {
            _indicatorShown = true;
            FadeTabIndicator(1);
        }
    }

    private void HideTabIndicator()
    {
        if (!_indicatorShown) return;
        _indicatorShown = false;
        _indicatorTarget = float.NaN;
        FadeTabIndicator(0);
    }

    private void FadeTabIndicator(float to)
    {
        if (_compositor is null || _indicatorVisual is null) return;
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, to);
        fade.Duration = TimeSpan.FromMilliseconds(140);
        _indicatorVisual.StartAnimation("Opacity", fade);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }
}
