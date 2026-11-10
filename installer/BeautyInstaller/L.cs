using System.Globalization;

namespace BeautyInstaller;

/// <summary>User-facing text in English or Spanish, following the Windows display language.</summary>
internal static class L
{
    public static bool Spanish { get; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";

    public static string T(string english, string spanish) => Spanish ? spanish : english;
}
