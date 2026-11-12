using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace BeautyBrowser.Controls;

/// <summary>The theme colors as circles to pick from (settings and onboarding); the selected one gets a ring.</summary>
public static class ThemeSwatches
{
    public static void Fill(Panel host, string selectedKey, Action<string> pick, double size = 26, Color? ring = null)
    {
        host.Children.Clear();
        var ringColor = ring ?? ((SolidColorBrush)Application.Current.Resources["BeautyTextBrush"]).Color; // follows dark mode
        foreach (var option in AppTheme.Options)
        {
            var selected = option.Key == selectedKey;
            Brush fill = option.Color is { } color
                ? new SolidColorBrush(color)
                : new LinearGradientBrush   // Classic: the neutral glass
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Colors.White, Offset = 0 },
                        new GradientStop { Color = Color.FromArgb(255, 0xC9, 0xCB, 0xD4), Offset = 1 },
                    },
                };

            var swatch = new Grid { Width = size + 8, Height = size + 8, Background = new SolidColorBrush(Colors.Transparent) };
            swatch.Children.Add(new Ellipse   // ring of the selected one
            {
                Stroke = new SolidColorBrush(ringColor),
                StrokeThickness = 1.5,
                Opacity = selected ? 1 : 0,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
            });
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = fill,
                Stroke = new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)),
                StrokeThickness = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Scale = selected ? new System.Numerics.Vector3(0.78f) : System.Numerics.Vector3.One,
                CenterPoint = new System.Numerics.Vector3((float)size / 2, (float)size / 2, 0),
                ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(160) },
            };
            swatch.Children.Add(dot);
            ToolTipService.SetToolTip(swatch, option.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, option.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(swatch, "Theme_" + option.Key);
            swatch.Tapped += (_, e) =>
            {
                e.Handled = true;
                pick(option.Key);
            };
            host.Children.Add(swatch);
        }
    }
}
