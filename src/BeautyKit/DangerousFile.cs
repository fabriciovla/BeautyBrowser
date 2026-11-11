namespace BeautyKit;

/// <summary>Files that run code when opened on Windows. Downloading one asks first, as Chrome does.</summary>
public static class DangerousFile
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Programs and installers
        "exe", "msi", "msp", "com", "scr", "pif", "dll", "cpl", "application", "gadget", "appref-ms", "appx", "msix",
        "appxbundle", "msixbundle", "jar",
        // Scripts
        "bat", "cmd", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "hta", "msc", "scf", "reg", "inf",
        // Shortcuts and help files that can start programs
        "lnk", "url", "chm",
        // Office documents with macros
        "docm", "xlsm", "pptm", "dotm", "xlam",
        // Disk images (mounted with a double click), OneNote and Excel add-ins, remote desktop and Windows shell files
        "iso", "img", "vhd", "vhdx", "one", "xll", "rdp", "settingcontent-ms", "library-ms", "wsc", "sct", "ws", "cab",
        "diagcab", "appinstaller", "mht", "mhtml",
    };

    /// <summary>Whether a file with this name can run code once opened. Looks at the last extension only.</summary>
    public static bool IsDangerous(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        // Windows ignores trailing dots and spaces ("setup.exe. " is setup.exe).
        var name = fileName.Trim().TrimEnd('.', ' ');
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 && Extensions.Contains(name[(dot + 1)..]);
    }
}
