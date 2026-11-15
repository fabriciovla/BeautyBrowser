using BeautyKit;
using Microsoft.UI.Xaml;

namespace BeautyBrowser;

/// <summary>Helpers for {x:Bind}.</summary>
public static class BindHelpers
{
    public static Visibility VisibleIfNull(object? value) => value is null ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfNotNull(object? value) => value is not null ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    // The speaker on a sidebar tab: only while it makes sound or is muted.
    public static Visibility SpeakerVisible(MediaInfo? media, bool muted) => media?.IsPlaying == true || muted ? Visibility.Visible : Visibility.Collapsed;
    public static string SpeakerGlyph(bool muted) => muted ? "" : "";
    public static string SpeakerTip(bool muted) => muted ? L.T("Unmute", "Activar sonido") : L.T("Mute tab", "Silenciar pestaña");
}
