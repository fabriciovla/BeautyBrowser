#nullable disable
// From Beauty Screenshot (src/Ui.cs): fonts, curves and animation helpers.
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BeautyInstaller
{
    /// <summary>Small shared helpers for brushes, fonts and animations.</summary>
    static class Ui
    {
        public static readonly FontFamily TextFont = new FontFamily("Segoe UI");
        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
        public static readonly IEasingFunction EaseInOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });
        public static readonly IEasingFunction EaseIn = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });

        /// <summary>Starts gently, covers most of the way quickly and settles softly: for things landing in place.</summary>
        public static readonly IEasingFunction Glide = Freeze(new GlideEase());

        /// <summary>Picks up speed and leaves: for things exiting the screen.</summary>
        public static readonly IEasingFunction Launch = Freeze(new LaunchEase());

        static IEasingFunction Freeze(EasingFunctionBase e) { e.Freeze(); return e; }

        public static SolidColorBrush Brush(uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }

        public static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

        /// <summary>Animates from the current value.</summary>
        public static DoubleAnimation To(double to, double ms, double delay = 0, IEasingFunction ease = null)
        {
            return new DoubleAnimation(to, new Duration(Ms(ms))) { BeginTime = Ms(delay), EasingFunction = ease ?? EaseOut };
        }

        public static DoubleAnimation Tween(double from, double to, double ms, double delay = 0, IEasingFunction ease = null)
        {
            var a = To(to, ms, delay, ease);
            a.From = from;
            return a;
        }

        public static void Fade(UIElement e, double to, double ms, double delay = 0)
        {
            e.BeginAnimation(UIElement.OpacityProperty, To(to, ms, delay));
        }

        public static void After(double ms, Action action)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = Ms(ms) };
            timer.Tick += (s, e) => { timer.Stop(); action(); };
            timer.Start();
        }
    }

    /// <summary>
    /// A CSS-style cubic-bézier curve. The control points are fixed per subclass (not fields), so they survive the
    /// copies WPF makes of easing functions.
    /// </summary>
    abstract class BezierEase : EasingFunctionBase
    {
        protected BezierEase() { EasingMode = EasingMode.EaseIn; }   // EaseInCore is then the curve exactly as given

        protected abstract double X1 { get; }
        protected abstract double Y1 { get; }
        protected abstract double X2 { get; }
        protected abstract double Y2 { get; }

        protected override double EaseInCore(double time)
        {
            // Find the curve parameter whose x is the time (Newton, then bisection if it stalls), and return its y.
            double u = time;
            for (int i = 0; i < 8; i++)
            {
                double error = Curve(u, X1, X2) - time, slope = Slope(u, X1, X2);
                if (Math.Abs(error) < 1e-6) return Curve(u, Y1, Y2);
                if (Math.Abs(slope) < 1e-6) break;
                u = Math.Max(0, Math.Min(1, u - error / slope));
            }
            double lo = 0, hi = 1;
            for (int i = 0; i < 30; i++)
            {
                u = (lo + hi) / 2;
                if (Curve(u, X1, X2) < time) lo = u; else hi = u;
            }
            return Curve(u, Y1, Y2);
        }

        static double Curve(double u, double p1, double p2) => 3 * (1 - u) * (1 - u) * u * p1 + 3 * (1 - u) * u * u * p2 + u * u * u;
        static double Slope(double u, double p1, double p2) => 3 * (1 - u) * (1 - u) * p1 + 6 * (1 - u) * u * (p2 - p1) + 3 * u * u * (1 - p2);
    }

    sealed class GlideEase : BezierEase
    {
        protected override double X1 => 0.2;
        protected override double Y1 => 0.0;
        protected override double X2 => 0.0;
        protected override double Y2 => 1.0;
        protected override Freezable CreateInstanceCore() => new GlideEase();
    }

    sealed class LaunchEase : BezierEase
    {
        protected override double X1 => 0.4;
        protected override double Y1 => 0.0;
        protected override double X2 => 1.0;
        protected override double Y2 => 1.0;
        protected override Freezable CreateInstanceCore() => new LaunchEase();
    }
}
