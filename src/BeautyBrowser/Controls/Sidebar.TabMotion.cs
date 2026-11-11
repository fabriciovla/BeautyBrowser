using System.Collections.Specialized;
using System.Numerics;
using BeautyKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace BeautyBrowser.Controls;

/// <summary>
/// How the tab list moves when a tab closes or opens. The list's own theme animations are off (Sidebar.xaml): they ran
/// on their own timing while the active tab's card slid on another, so closing a tab looked out of step. Here the rows
/// below a closed tab slide up with the same duration and curve as the card, and a new tab fades in.
/// </summary>
public sealed partial class Sidebar
{
    private INotifyCollectionChanged? _motionTabs;
    private (int Index, double Height)? _pendingSlide;
    private int _pendingFadeIn = -1;

    private void WatchTabMotion(INotifyCollectionChanged tabs)
    {
        if (_motionTabs is not null) _motionTabs.CollectionChanged -= Tabs_CollectionChanged;
        _motionTabs = tabs;
        tabs.CollectionChanged += Tabs_CollectionChanged;
    }

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldStartingIndex >= 0)
        {
            var row = TabList.ContainerFromIndex(Math.Max(0, e.OldStartingIndex - 1)) as FrameworkElement
                      ?? TabList.ContainerFromIndex(e.OldStartingIndex) as FrameworkElement;
            _pendingSlide = (e.OldStartingIndex, row?.ActualHeight > 0 ? row.ActualHeight : 42);
        }
        else if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex >= 0)
        {
            _pendingFadeIn = e.NewStartingIndex;
        }
        else return;

        TabList.LayoutUpdated -= RunTabMotion;
        TabList.LayoutUpdated += RunTabMotion; // once the rows are in their new places
    }

    private void RunTabMotion(object? sender, object e)
    {
        TabList.LayoutUpdated -= RunTabMotion;
        if (_compositor is null) { _pendingSlide = null; _pendingFadeIn = -1; return; }

        if (_pendingSlide is { } slide)
        {
            _pendingSlide = null;
            for (var i = slide.Index; i < TabList.Items.Count; i++)
            {
                if (TabList.ContainerFromIndex(i) is not UIElement row) break; // past the visible ones
                ElementCompositionPreview.SetIsTranslationEnabled(row, true);
                var move = _compositor.CreateVector3KeyFrameAnimation();
                move.InsertKeyFrame(0, new Vector3(0, (float)slide.Height, 0));
                move.InsertKeyFrame(1, Vector3.Zero, _indicatorEase);
                move.Duration = IndicatorSlide; // in step with the card
                ElementCompositionPreview.GetElementVisual(row).StartAnimation("Translation", move);
            }
        }

        if (_pendingFadeIn >= 0)
        {
            if (TabList.ContainerFromIndex(_pendingFadeIn) is UIElement row)
            {
                var fade = _compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0, 0);
                fade.InsertKeyFrame(1, 1);
                fade.Duration = TimeSpan.FromMilliseconds(180);
                ElementCompositionPreview.GetElementVisual(row).StartAnimation("Opacity", fade);
            }
            _pendingFadeIn = -1;
        }
    }

    // Rows are reused for other tabs: the close X shown on hover must not travel to the tab that takes its place.
    private void TabList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer?.ContentTemplateRoot is Grid row) SetCloseVisible(row, false);
    }
}
