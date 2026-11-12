using System.Globalization;

namespace BeautyKit;

/// <summary>Which language the interface uses: follow Windows, or a fixed one.</summary>
public enum LanguageChoice { System, Spanish, English }

/// <summary>
/// User-facing text in English or Spanish. It follows the Windows display language unless the user picked one in
/// Settings. The choice lives in a small file because it has to be known before any UI or setting store exists
/// (static texts read <see cref="Spanish"/> while they initialize); a change applies on the next start.
/// </summary>
public static class L
{
    private static readonly string ChoicePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeautyBrowser", "language.txt");

    /// <summary>The saved choice, as read at startup.</summary>
    public static LanguageChoice Choice { get; } = ReadChoice();

    public static bool Spanish { get; } = Choice switch
    {
        LanguageChoice.Spanish => true,
        LanguageChoice.English => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es",
    };

    public static string T(string english, string spanish) => Spanish ? spanish : english;

    /// <summary>The choice stored for the next start (what <see cref="Choice"/> will be then).</summary>
    public static LanguageChoice SavedChoice => ReadChoice();

    public static void SaveChoice(LanguageChoice choice)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ChoicePath)!);
            File.WriteAllText(ChoicePath, choice.ToString());
        }
        catch
        {
            // Unwritable profile folder: the language stays as it is.
        }
    }

    private static LanguageChoice ReadChoice()
    {
        try
        {
            return Enum.TryParse<LanguageChoice>(File.ReadAllText(ChoicePath).Trim(), out var choice) ? choice : LanguageChoice.System;
        }
        catch
        {
            return LanguageChoice.System;
        }
    }
}
