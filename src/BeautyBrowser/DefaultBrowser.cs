using System.Diagnostics;
using Microsoft.Win32;

namespace BeautyBrowser;

/// <summary>
/// The installer registers BeautyBrowser with Windows. Apps cannot make themselves the default browser, so this only
/// checks whether the user picked it and opens the Settings page where that is done.
/// </summary>
internal static class DefaultBrowser
{
    private const string ProgId = "BeautyBrowserHTML";   // the one the installer registers
    private const string AppName = "BeautyBrowser";

    public static bool IsDefault
    {
        get
        {
            try
            {
                foreach (var choice in new[] { "UserChoiceLatest", "UserChoice" })
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\{choice}");
                    if (key?.GetValue("ProgId") is string progId) return progId == ProgId;
                }
            }
            catch
            {
                // No registry access: treat it as not default.
            }
            return false;
        }
    }

    public static void OpenSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={AppName}") { UseShellExecute = true });
        }
        catch
        {
            // Settings could not be opened; nothing else to do.
        }
    }
}
