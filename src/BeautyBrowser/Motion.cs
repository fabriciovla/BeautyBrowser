using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace BeautyBrowser;

/// <summary>
/// Simple per-frame animations (CompositionTarget.Rendering) for what XAML does not animate on its own, such as
/// margins or curved paths. Starting an animation with the same key cancels the previous one.
/// </summary>
internal static class Motion
{
    private static readonly Dictionary<string, Action> Running = [];

    public static void Animate(string key, TimeSpan duration, Action<double> apply,
        Func<double, double>? ease = null, Action? completed = null)
    {
        Cancel(key);
        ease ??= EaseOutCubic;

        var clock = Stopwatch.StartNew();
        EventHandler<object>? tick = null;
        tick = (_, _) =>
        {
            var t = Math.Clamp(clock.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            apply(ease(t));
            if (t < 1) return;
            Stop();
            completed?.Invoke();
        };

        void Stop()
        {
            CompositionTarget.Rendering -= tick;
            Running.Remove(key);
        }

        Running[key] = Stop;
        CompositionTarget.Rendering += tick;
    }

    public static void Cancel(string key)
    {
        if (Running.TryGetValue(key, out var stop)) stop();
    }

    public static double Lerp(double from, double to, double t) => from + (to - from) * t;

    public static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    public static double EaseInOutCubic(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    /// <summary>Overshoots a little and settles, for the library's bounce.</summary>
    public static double EaseOutBack(double t)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }
}
