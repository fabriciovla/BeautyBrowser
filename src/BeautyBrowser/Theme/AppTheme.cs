using BeautyKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BeautyBrowser;

/// <summary>
/// The browser color: it tints the window and sidebar glass and is the accent color (load bar, buttons). "Classic" is
/// the neutral glass with a blue accent.
/// </summary>
public static class AppTheme
{
    public sealed record Option(string Key, string Name, Color? Color);

    public static readonly IReadOnlyList<Option> Options =
    [
        new("classic", L.T("Classic", "Clásico"), null),
        new("violet", L.T("Violet", "Violeta"), Rgb(0x7B, 0x5B, 0xE0)),   // the logo's
        new("blue", L.T("Blue", "Azul"), Rgb(0x2F, 0x6F, 0xEB)),
        new("sky", L.T("Sky", "Cielo"), Rgb(0x1F, 0xA3, 0xD6)),
        new("green", L.T("Green", "Verde"), Rgb(0x2F, 0xA5, 0x6C)),
        new("amber", L.T("Amber", "Ámbar"), Rgb(0xE0, 0x9A, 0x2B)),
        new("coral", L.T("Coral", "Coral"), Rgb(0xE8, 0x70, 0x55)),
        new("pink", L.T("Pink", "Rosa"), Rgb(0xDD, 0x5C, 0x9B)),
    ];

    private static readonly Color ClassicAccent = Rgb(0x2F, 0x6F, 0xEB);
    private static readonly Color ClassicPanel = Rgb(0xF4, 0xF4, 0xF7);

    public static Option Find(string? key) => Options.FirstOrDefault(o => o.Key == key) ?? Options[0];

    public static Option Current { get; private set; } = Options[0];

    public static event Action? Changed;

    /// <summary>Applies the theme to the shared brushes the sidebar, the library and the rest use.</summary>
    public static void Apply(string? key)
    {
        var option = Find(key);
        if (option == Current && _applied) return;
        Current = option;
        _applied = true;

        var resources = Application.Current.Resources;
        var accent = option.Color ?? ClassicAccent;
        if (resources["BeautyAccentBrush"] is SolidColorBrush accentBrush) accentBrush.Color = accent;
        Retarget(animate: _modeApplied);
    }

    private static bool _applied;

    /// <summary>Dark mode (Settings → Appearance), or the light look.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>The private space is showing: always dark, a neutral gray, like other browsers' incognito windows.</summary>
    public static bool IsPrivate { get; private set; }

    /// <summary>The tint over the window glass. The window uses this brush; it changes color in place.</summary>
    public static SolidColorBrush WindowTintBrush { get; } = new(Color.FromArgb(0, 0, 0, 0));

    /// <summary>Raised halfway through a change between light and dark, when the glass under the tint can flip.</summary>
    public static event Action<bool>? GlassDarkChanged;

    private static readonly Color DarkPanel = Rgb(0x26, 0x26, 0x2C);
    private static readonly Color DarkGlass = Rgb(0x1C, 0x1C, 0x21);

    // The shared brushes in dark mode; their light colors are kept to go back.
    private static readonly (string Key, uint Dark)[] DarkColors =
    [
        ("BeautyTextBrush", 0xFFECECF1),
        ("BeautyTextSecondaryBrush", 0xFF9A9AA5),
        ("BeautySubtleFillBrush", 0x14FFFFFF),
        ("BeautySubtleFillHoverBrush", 0x22FFFFFF),
        ("BeautyTileBrush", 0x14FFFFFF),
        ("BeautyTileHoverBrush", 0x22FFFFFF),
        ("BeautyTileActiveBrush", 0xFF3A3A42),
        ("BeautyTileBorderBrush", 0x14FFFFFF),
        ("BeautyTileActiveBorderBrush", 0x33FFFFFF),
        ("BeautyDividerBrush", 0x1AFFFFFF),
        ("BeautyPanelBorderBrush", 0x26FFFFFF),
        ("BeautyCardBrush", 0xFF2C2C33),
        ("BeautyMediaCardBrush", 0xE02C2C33),
        ("BeautyInsetBrush", 0x33000000),
        ("BeautyInsetBorderBrush", 0x14FFFFFF),
        ("BeautyLibraryListBrush", 0x14FFFFFF),
    ];

    private static readonly Dictionary<string, Color> LightColors = [];

    /// <summary>Switches between light, dark and the private space's look, with a short crossfade.</summary>
    public static void SetMode(bool dark, bool isPrivate)
    {
        if (IsDark == dark && IsPrivate == isPrivate && _modeApplied) return;
        var animate = _modeApplied;
        _modeApplied = true;
        IsDark = dark;
        IsPrivate = isPrivate;
        Retarget(animate);
    }

    private static bool _modeApplied;
    private static bool _glassDark;

    private static bool LooksDark => IsDark || IsPrivate;

    // Moves every themed color (shared brushes, sidebar glass, window tint) to the current mode and color.
    private static void Retarget(bool animate)
    {
        var resources = Application.Current.Resources;
        var changes = new List<(Action<Color> Set, Color From, Color To)>();
        foreach (var (key, dark) in DarkColors)
        {
            if (resources[key] is not SolidColorBrush brush) continue;
            if (!LightColors.ContainsKey(key)) LightColors[key] = brush.Color;
            var to = LooksDark ? Argb(dark) : LightColors[key];
            changes.Add((c => brush.Color = c, brush.Color, to));
        }
        if (resources["BeautyPanelBrush"] is AcrylicBrush panel)
        {
            changes.Add((c => { panel.TintColor = c; panel.FallbackColor = c; }, panel.TintColor, PanelTint()));
        }
        changes.Add((c => WindowTintBrush.Color = c, WindowTintBrush.Color, WindowTint));

        var toDark = LooksDark;
        Motion.Cancel("app-theme");
        if (!animate)
        {
            foreach (var (set, _, to) in changes) set(to);
            SetGlassDark(toDark);
            Changed?.Invoke();
            return;
        }

        // The tint covers the glass while it flips, so the switch reads as one smooth fade.
        Motion.Animate("app-theme", TimeSpan.FromMilliseconds(420), t =>
        {
            foreach (var (set, from, to) in changes) set(Lerp(from, to, t));
            if (t >= 0.5) SetGlassDark(toDark);
        }, ease: EaseInOut, completed: () => Changed?.Invoke());
    }

    private static void SetGlassDark(bool dark)
    {
        if (_glassDark == dark) return;
        _glassDark = dark;
        GlassDarkChanged?.Invoke(dark);
    }

    private static Color PanelTint() =>
        IsPrivate ? DarkPanel
        : IsDark ? Current.Color is { } c ? Mix(DarkPanel, c, 0.12) : DarkPanel
        : Current.Color is { } color ? Mix(ClassicPanel, color, 0.2) : ClassicPanel;

    /// <summary>The tint over the window glass (transparent for light Classic).</summary>
    public static Color WindowTint =>
        IsPrivate ? Color.FromArgb(0xE0, 0x1E, 0x1E, 0x23)
        : IsDark ? WithAlpha(Current.Color is { } c ? Mix(DarkGlass, c, 0.14) : DarkGlass, 0xD8)
        : Current.Color is { } color ? Color.FromArgb(0x4D, color.R, color.G, color.B)
        : Color.FromArgb(0, 0, 0, 0);

    private static double EaseInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t),
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Argb(uint v) => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
}
