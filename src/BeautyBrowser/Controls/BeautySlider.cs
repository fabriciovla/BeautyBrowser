using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace BeautyBrowser.Controls;

/// <summary>
/// A custom 0-100 slider: thin track, accent fill and a white thumb. It can be dragged from anywhere on the track and
/// moved with the arrow keys.
/// </summary>
public sealed partial class BeautySlider : UserControl
{
    private const double ThumbSize = 18;
    private const double TrackHeight = 4;

    private readonly Border _fill;
    private readonly Ellipse _thumb;
    private readonly TranslateTransform _thumbOffset = new();
    private double _value;
    private bool _dragging;

    public BeautySlider()
    {
        Height = 26;
        IsTabStop = true;
        UseSystemFocusVisuals = false;

        var track = new Border
        {
            Height = TrackHeight,
            Margin = new Thickness(ThumbSize / 2, 0, ThumbSize / 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            Background = (Brush)Application.Current.Resources["BeautyFillPressedBrush"], // follows dark mode
        };
        _fill = new Border
        {
            Height = TrackHeight,
            Margin = new Thickness(ThumbSize / 2, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            Background = (Brush)Application.Current.Resources["BeautyAccentBrush"],
        };
        _thumb = new Ellipse
        {
            Width = ThumbSize,
            Height = ThumbSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Colors.White),
            Stroke = new SolidColorBrush(ColorHelper.FromArgb(0x26, 0, 0, 0)),
            StrokeThickness = 1,
            RenderTransform = _thumbOffset,
        };

        Content = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent), // the whole strip is hit-testable
            Children = { track, _fill, _thumb },
        };

        SizeChanged += (_, _) => Layout();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndDrag();
        KeyDown += OnKeyDown;
    }

    /// <summary>Raised while moving too, during a drag.</summary>
    public event EventHandler<double>? ValueChanged;

    /// <summary>Released (or moved with the keyboard): a good moment to save.</summary>
    public event EventHandler<double>? ValueCommitted;

    public double Value
    {
        get => _value;
        set
        {
            value = Math.Clamp(Math.Round(value), 0, 100);
            if (Math.Abs(value - _value) < 0.5) return;
            _value = value;
            Layout();
        }
    }

    private double Usable => Math.Max(0, ActualWidth - ThumbSize);

    private void Layout()
    {
        var x = Usable * _value / 100;
        _thumbOffset.X = x;
        _fill.Width = Math.Max(0, x);
    }

    private void SetFromPointer(PointerRoutedEventArgs e)
    {
        if (Usable <= 0) return;
        var x = e.GetCurrentPoint(this).Position.X - ThumbSize / 2;
        var before = _value;
        Value = x / Usable * 100;
        if (Math.Abs(before - _value) >= 0.5) ValueChanged?.Invoke(this, _value);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = CapturePointer(e.Pointer);
        Focus(FocusState.Pointer);
        SetFromPointer(e);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        SetFromPointer(e);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        ValueCommitted?.Invoke(this, _value);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var step = e.Key switch
        {
            VirtualKey.Left or VirtualKey.Down => -1,
            VirtualKey.Right or VirtualKey.Up => 1,
            VirtualKey.PageDown => -10,
            VirtualKey.PageUp => 10,
            _ => 0,
        };
        if (step == 0) return;
        e.Handled = true;
        var before = _value;
        Value = _value + step;
        if (Math.Abs(before - _value) < 0.5) return;
        ValueChanged?.Invoke(this, _value);
        ValueCommitted?.Invoke(this, _value);
    }
}
