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
        ApplyPanel();
        Changed?.Invoke();
    }

    private static bool _applied;

    /// <summary>The tint over the window glass (transparent for Classic; dark gray in the private space).</summary>
    public static Color WindowTint => IsPrivate ? Color.FromArgb(0xE0, 0x1E, 0x1E, 0x23)
        : Current.Color is { } color ? Color.FromArgb(0x4D, color.R, color.G, color.B)
        : Color.FromArgb(0, 0, 0, 0);

    /// <summary>The private space is showing: the app turns dark, like other browsers' incognito windows.</summary>
    public static bool IsPrivate { get; private set; }

    private static readonly Color PrivatePanel = Rgb(0x26, 0x26, 0x2C);

    // The shared brushes in the private space; their light colors are kept to restore them.
    private static readonly (string Key, uint Dark)[] PrivateColors =
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

    public static void SetPrivate(bool isPrivate)
    {
        if (IsPrivate == isPrivate) return;
        IsPrivate = isPrivate;

        var resources = Application.Current.Resources;
        foreach (var (key, dark) in PrivateColors)
        {
            if (resources[key] is not SolidColorBrush brush) continue;
            if (!LightColors.ContainsKey(key)) LightColors[key] = brush.Color;
            brush.Color = isPrivate
                ? Color.FromArgb((byte)(dark >> 24), (byte)(dark >> 16), (byte)(dark >> 8), (byte)dark)
                : LightColors[key];
        }
        ApplyPanel();
        Changed?.Invoke();
    }

    private static void ApplyPanel()
    {
        if (Application.Current.Resources["BeautyPanelBrush"] is not AcrylicBrush panel) return;
        var tint = IsPrivate ? PrivatePanel : Current.Color is { } color ? Mix(ClassicPanel, color, 0.2) : ClassicPanel;
        panel.TintColor = tint;
        panel.FallbackColor = tint;
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
}
