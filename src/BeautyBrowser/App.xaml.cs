using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace BeautyBrowser;

public partial class App : Application
{
    private static Window? _window;
    private static Mutex? _singleInstance;

    /// <summary>
    /// The link the app was opened with (as the default browser). The window opens it after restoring the session and
    /// clears it.
    /// </summary>
    public static string? PendingLink { get; set; }

    /// <summary>A restart was requested, for example to apply hardware acceleration.</summary>
    public static bool RestartRequested { get; private set; }

    public App()
    {
        CrashLog.Install(this);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance: Chromium cannot open the same profile twice. If a BeautyBrowser is already running,
        // bring it to the front and exit.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\BeautyBrowser.SingleInstance", out var firstInstance);
        var link = ExternalLinks.FromCommandLine();
        // The running one may be closing (its window already hidden while Chromium shuts down): wait for it to finish
        // and open normally, rather than bringing back a window that is going away.
        if (!firstInstance && !OtherWindowVisible()) firstInstance = WaitForPreviousToExit();
        if (!firstInstance)
        {
            // A link from another app: the running window opens it as a new tab.
            if (link is not null) ExternalLinks.SendToRunning(link);
            BringExistingToFront();
            Exit();
            return;
        }
        PendingLink = link;

        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnLaunched", ex);
            throw;
        }
    }

    /// <summary>
    /// Restarts BeautyBrowser: closes the window (saving the session and shutting Chromium down) and only then starts
    /// the app again (see MainWindow.Shutdown).
    /// </summary>
    public static void Restart()
    {
        RestartRequested = true;
        _window?.Close();
    }

    /// <summary>Releases the single-instance mark before relaunching on restart.</summary>
    public static void ReleaseSingleInstance()
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        _singleInstance = null;
    }

    private static void BringExistingToFront()
    {
        var current = (uint)Environment.ProcessId;
        var hWnd = nint.Zero;
        while ((hWnd = FindWindowEx(0, hWnd, "WinUIDesktopWin32WindowClass", null)) != 0)
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == current) continue;
            ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
            break;
        }
    }

    private static bool OtherWindowVisible()
    {
        var current = (uint)Environment.ProcessId;
        var hWnd = nint.Zero;
        while ((hWnd = FindWindowEx(0, hWnd, "WinUIDesktopWin32WindowClass", null)) != 0)
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid != current && IsWindowVisible(hWnd)) return true;
        }
        return false;
    }

    private static bool WaitForPreviousToExit()
    {
        try
        {
            return _singleInstance!.WaitOne(TimeSpan.FromSeconds(20));
        }
        catch (AbandonedMutexException)
        {
            return true; // it ended without releasing it: ours now all the same
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint hWndParent, nint hWndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
}
