using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace BeautyInstaller;

/// <summary>
/// What the installer does, step by step: close BeautyBrowser if it is running, extract the app (it travels inside
/// the installer as payload.zip), create the Start menu shortcut and register it in Settings → Apps (and as a browser,
/// so it can be made the default). Everything is per user, without administrator rights. User data
/// (%LOCALAPPDATA%\BeautyBrowser) is never touched.
/// </summary>
public static class Installer
{
    public const string AppName = "BeautyBrowser";        // folders, files and registry keys (do not change)
    public const string DisplayName = "Beauty Browser";  // the display name
    public const string AppExe = "BeautyBrowser.exe";
    /// <summary>"0.1.2", from version.props.</summary>
    public static string Version { get; } =
        typeof(Installer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BeautyBrowser";

    // Registered as a browser, the way Chrome does for a per-user install.
    private const string ProgId = "BeautyBrowserHTML";
    private const string ClientKey = @"Software\Clients\StartMenuInternet\BeautyBrowser";
    private const string RegisteredApps = @"Software\RegisteredApplications";
    private static readonly string[] FileTypes = [".htm", ".html", ".shtml", ".xht", ".xhtml"];
    private static readonly string[] Protocols = ["http", "https"];

    /// <summary>Where the app keeps user data (history, settings, favorites).</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    private static string Exe => Path.Combine(InstallDir, AppExe);

    // Windows shows the shortcut's file name: "Beauty Browser".
    private static string StartMenuShortcut { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{DisplayName}.lnk");

    private static string DesktopShortcut { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{DisplayName}.lnk");

    // Shortcuts from older versions ("BeautyBrowser.lnk"), replaced by the ones above.
    private static string OldStartMenuShortcut { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{AppName}.lnk");

    private static string OldDesktopShortcut { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk");

    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo("payload.zip") is not null;

    /// <summary>Already installed: this run is an update and skips the welcome.</summary>
    public static bool IsUpdate => File.Exists(Exe);

    public static bool HasDesktopShortcut => File.Exists(DesktopShortcut) || File.Exists(OldDesktopShortcut);

    /// <summary>Size of the installed app, such as "596 MB".</summary>
    public static string InstalledSize
    {
        get
        {
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
            if (payload is null) return "";
            using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
            return $"{Math.Ceiling(zip.Entries.Sum(e => e.Length) / 1048576.0)} MB";
        }
    }

    /// <summary>Closes BeautyBrowser normally (so it saves its session) and waits for it to exit.</summary>
    public static async Task CloseRunningAsync()
    {
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe))
            .Where(p =>
            {
                try { return p.MainModule?.FileName.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase) == true; }
                catch { return false; }
            });
        foreach (var process in running)
        {
            CloseMainWindow(process);
            if (!await Task.Run(() => process.WaitForExit(8000))) process.Kill(entireProcessTree: true);
        }
    }

    private static void CloseMainWindow(Process process)
    {
        // WinUI 3: look for the real window; Process.MainWindowHandle may point at a helper window.
        var hWnd = nint.Zero;
        while ((hWnd = FindWindowEx(0, hWnd, "WinUIDesktopWin32WindowClass", null)) != 0)
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == (uint)process.Id) PostMessage(hWnd, 0x0010 /* WM_CLOSE */, 0, 0);
        }
        if (process.MainWindowHandle != 0) process.CloseMainWindow();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindowEx(nint hWndParent, nint hWndChildAfter, string lpszClass, string? lpszWindow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>Extracts the app into <paramref name="targetDir"/>, reporting progress from 0 to 1.</summary>
    public static async Task ExtractAsync(string targetDir, IProgress<double> progress, CancellationToken cancel = default)
    {
        await using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                                  ?? throw new InvalidOperationException("The installer does not carry the app.");
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);

        var total = Math.Max(1, zip.Entries.Sum(e => e.Length));
        long done = 0;
        var buffer = new byte[1 << 20];
        Directory.CreateDirectory(targetDir);
        var root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;

        foreach (var entry in zip.Entries)
        {
            cancel.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // nothing outside the folder

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = await OpenForWriteAsync(destination);
            int read;
            while ((read = await input.ReadAsync(buffer, cancel)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                progress.Report((double)done / total);
            }
        }
        progress.Report(1);
    }

    /// <summary>A file from an app that just closed can stay locked for a moment, so this retries.</summary>
    private static async Task<FileStream> OpenForWriteAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(300);
            }
        }
    }

    public static void CreateStartMenuShortcut()
    {
        if (File.Exists(OldStartMenuShortcut)) File.Delete(OldStartMenuShortcut);
        CreateShortcut(StartMenuShortcut);
    }

    public static void SetDesktopShortcut(bool on)
    {
        if (File.Exists(OldDesktopShortcut)) File.Delete(OldDesktopShortcut);
        if (on) CreateShortcut(DesktopShortcut);
        else if (File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut);
    }

    /// <summary>Uninstaller and the Settings → Apps entry.</summary>
    public static void Register()
    {
        var uninstaller = Path.Combine(InstallDir, "uninstall.cmd");
        File.WriteAllText(uninstaller, UninstallScript());

        var sizeKb = new DirectoryInfo(InstallDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", DisplayName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", DisplayName);
        key.SetValue("DisplayIcon", Exe + ",0");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"cmd.exe /c \"\"{uninstaller}\"\"");
        key.SetValue("EstimatedSize", (int)Math.Min(sizeKb, int.MaxValue), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    // ---------------------------------------------------------------- navegador predeterminado

    /// <summary>
    /// Registers BeautyBrowser as a browser so it shows in Settings → Default apps and in "Open with". Windows does not
    /// let an app make itself the default; the user chooses (see <see cref="OpenDefaultAppsSettings"/>). Links arrive as
    /// <c>--beauty-open=URL</c> (see ExternalLinks in the app).
    /// </summary>
    public static void RegisterBrowser()
    {
        var icon = Exe + ",0";
        var openLink = $"\"{Exe}\" \"--beauty-open=%1\"";

        using (var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            progId.SetValue("", L.T("Beauty Browser Document", "Documento de Beauty Browser"));
            progId.SetValue("FriendlyTypeName", L.T("Beauty Browser Document", "Documento de Beauty Browser"));
            using (var application = progId.CreateSubKey("Application"))
            {
                application.SetValue("ApplicationName", DisplayName);
                application.SetValue("ApplicationIcon", icon);
                application.SetValue("ApplicationCompany", DisplayName);
                application.SetValue("ApplicationDescription", L.T("Web browser built on Chromium", "Navegador web con Chromium"));
            }
            using (var defaultIcon = progId.CreateSubKey("DefaultIcon")) defaultIcon.SetValue("", icon);
            using (var command = progId.CreateSubKey(@"shell\open\command")) command.SetValue("", openLink);
        }

        using (var client = Registry.CurrentUser.CreateSubKey(ClientKey))
        {
            client.SetValue("", DisplayName);
            using (var capabilities = client.CreateSubKey("Capabilities"))
            {
                capabilities.SetValue("ApplicationName", DisplayName);
                capabilities.SetValue("ApplicationIcon", icon);
                capabilities.SetValue("ApplicationDescription", L.T("Web browser built on Chromium, designed your way.", "Navegador web con Chromium, con tu diseño."));
                using (var files = capabilities.CreateSubKey("FileAssociations"))
                    foreach (var type in FileTypes) files.SetValue(type, ProgId);
                using (var urls = capabilities.CreateSubKey("URLAssociations"))
                    foreach (var protocol in Protocols) urls.SetValue(protocol, ProgId);
                using (var startMenu = capabilities.CreateSubKey("StartMenu")) startMenu.SetValue("StartMenuInternet", AppName);
            }
            using (var defaultIcon = client.CreateSubKey("DefaultIcon")) defaultIcon.SetValue("", icon);
            using (var command = client.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{Exe}\"");
        }

        using (var registered = Registry.CurrentUser.CreateSubKey(RegisteredApps))
            registered.SetValue(AppName, ClientKey + @"\Capabilities");

        // "Open with" for standalone .html files.
        foreach (var type in FileTypes)
        {
            using var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{type}\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, 0, 0);
    }

    /// <summary>Whether it is already the default browser (the user's choice for https links).</summary>
    public static bool IsDefaultBrowser
    {
        get
        {
            foreach (var choice in new[] { "UserChoiceLatest", "UserChoice" })
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\{choice}");
                if (key?.GetValue("ProgId") is string progId) return progId == ProgId;
            }
            return false;
        }
    }

    /// <summary>Opens Settings → Default apps right at BeautyBrowser's page.</summary>
    public static void OpenDefaultAppsSettings() =>
        Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={AppName}") { UseShellExecute = true });

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    /// <summary>
    /// The update the browser downloads and runs when it closes: the same steps as the window, without one. The desktop
    /// shortcut stays as the user left it. Errors go to %LOCALAPPDATA%\BeautyBrowser\update.log.
    /// </summary>
    public static async Task<bool> UpdateSilentlyAsync(bool relaunch)
    {
        try
        {
            await CloseRunningAsync();
            await ExtractAsync(InstallDir, new Progress<double>());
            CreateStartMenuShortcut();
            Register();
            RegisterBrowser();
            if (relaunch) Launch();
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(Path.Combine(DataDir, "update.log"), $"{DateTime.Now:s} {Version}: {ex}\r\n");
            }
            catch
            {
                // nothing else to do
            }
            if (relaunch && File.Exists(Exe))
            {
                try { Launch(); } catch { /* the user opens it again */ }
            }
            return false;
        }
    }

    public static void Launch() =>
        Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true, WorkingDirectory = InstallDir });

    /// <summary>
    /// The uninstaller closes the app, removes shortcuts and registry entries, and deletes the folder from a separate
    /// process (the script lives inside it). User data in %LOCALAPPDATA%\BeautyBrowser is kept.
    /// </summary>
    private static string UninstallScript() => $"""
        @echo off
        rem Desinstala BeautyBrowser. Tus datos (historial, ajustes, sesion) en %LOCALAPPDATA%\BeautyBrowser se conservan.
        taskkill /im {AppExe} /f >nul 2>&1
        del "{StartMenuShortcut}" >nul 2>&1
        del "{DesktopShortcut}" >nul 2>&1
        del "{OldStartMenuShortcut}" >nul 2>&1
        del "{OldDesktopShortcut}" >nul 2>&1
        reg delete "HKCU\{UninstallKey}" /f >nul 2>&1
        reg delete "HKCU\{ClientKey}" /f >nul 2>&1
        reg delete "HKCU\Software\Classes\{ProgId}" /f >nul 2>&1
        reg delete "HKCU\{RegisteredApps}" /v {AppName} /f >nul 2>&1
        {string.Join("\r\n", FileTypes.Select(t => $"reg delete \"HKCU\\Software\\Classes\\{t}\\OpenWithProgids\" /v {ProgId} /f >nul 2>&1"))}
        start "" /min cmd /c timeout /t 2 /nobreak ^>nul ^& rmdir /s /q "{InstallDir}"
        """;

    private static void CreateShortcut(string path)
    {
        // WScript.Shell is the simplest way to write a .lnk without declaring our own COM interfaces.
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available");
        var shell = Activator.CreateInstance(shellType)!;
        try
        {
            var link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path])!;
            var linkType = link.GetType();
            void Set(string name, object value) => linkType.InvokeMember(name, BindingFlags.SetProperty, null, link, [value]);
            Set("TargetPath", Exe);
            Set("WorkingDirectory", InstallDir);
            Set("Description", L.T("Web browser", "Navegador web"));
            Set("IconLocation", Exe + ",0");
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
