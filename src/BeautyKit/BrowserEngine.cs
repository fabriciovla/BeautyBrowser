using CefSharp;
using Microsoft.UI.Dispatching;

namespace BeautyKit;

/// <summary>
/// Entry point to the engine (Chromium through CEF). There is one per process and every tab shares its profile.
/// </summary>
/// <remarks>
/// Chromium renders offscreen and runs on the UI thread (external message pump), so frames arrive straight on the
/// UI thread. Child windows are not used because WinUI 3 captures input for its whole window and they would never
/// receive the mouse.
/// </remarks>
public sealed class BrowserEngine : IDisposable
{
    public const string HomeUrl = "https://www.google.com";

    private readonly nint _hostWindow;
    private readonly DispatcherQueue _ui;
    private readonly MessagePump _pump;

    private BrowserEngine(nint hostWindow, DispatcherQueue ui, MessagePump pump)
    {
        _hostWindow = hostWindow;
        _ui = ui;
        _pump = pump;
    }

    public static string DefaultProfileDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeautyBrowser", "Profiles", "Default");

    public static string ChromiumVersion => Cef.ChromiumVersion;

    /// <summary>Starts Chromium. Call it once, on the UI thread, before creating tabs.</summary>
    /// <param name="hostWindow">The main window; tabs render inside it.</param>
    /// <param name="ui">The UI thread's queue, where Chromium does its work.</param>
    /// <param name="hardwareAcceleration">Render pages on the GPU. Decided at startup.</param>
    /// <param name="pageLanguages">Preferred page languages, such as "en-US,en".</param>
    public static BrowserEngine Initialize(nint hostWindow, DispatcherQueue ui, string? profileDirectory = null,
        bool hardwareAcceleration = true, string? pageLanguages = null)
    {
        profileDirectory ??= DefaultProfileDirectory;
        Directory.CreateDirectory(profileDirectory);

        var settings = new EngineSettings
        {
            CachePath = profileDirectory,
            Locale = L.T("en-US", "es-419"),
            AcceptLanguageList = pageLanguages ?? L.T("en-US,en", "es-419,es,en"),
            PersistSessionCookies = true,
            MultiThreadedMessageLoop = false,
            ExternalMessagePump = true,
            WindowlessRenderingEnabled = true,
            // Opaque white background, like Chrome. Offscreen, Chromium otherwise leaves whatever the page does not
            // paint transparent, which showed up black (chrome://gpu) or as a thin line above YouTube videos.
            BackgroundColor = 0xFFFFFFFF,
            // Chromium's helper processes are this same executable (see RunSubprocessIfRequested), so they use the
            // .NET runtime bundled with the app.
            BrowserSubprocessPath = Environment.ProcessPath!,
        };

        StartPage.Register(settings); // beauty://newtab

        if (!hardwareAcceleration)
        {
            settings.CefCommandLineArgs.Add("disable-gpu");
            settings.CefCommandLineArgs.Add("disable-gpu-compositing");
        }

        // With hardware acceleration frames travel as shared GPU textures at the monitor's refresh rate; without it
        // they go through the CPU at 60 fps.
        BrowserTab.UseGpu = hardwareAcceleration && Gpu.TryInitialize();
        BrowserTab.FrameRate = BrowserTab.UseGpu ? Math.Clamp(DisplayRefreshRate(), 60, 240) : 60;
        Gpu.WriteLog(BrowserTab.FrameRate);

        // Windows wakes timers every ~15.6 ms, while Chromium asks for work every few ms (more at 144 Hz) and scrolling
        // stutters with that rounding. Like Chrome, request 1 ms timer resolution while running.
        TimeBeginPeriod(1);

        var pump = new MessagePump(ui);
        if (!Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: pump))
            throw new InvalidOperationException("Could not start Chromium (CEF).");

        pump.Start();
        Blocking.ContentBlocker.Initialize(ui); // loads the saved filter lists and updates them
        return new BrowserEngine(hostWindow, ui, pump);
    }

    /// <summary>
    /// When Chromium launched this executable as a helper process ("--type=renderer", "--type=gpu-process"…), runs it
    /// and returns its exit code; returns -1 for a normal browser start. Call it first thing in Main.
    /// </summary>
    public static int RunSubprocessIfRequested(string[] args)
    {
        var type = args.FirstOrDefault(a => a.StartsWith("--type=", StringComparison.Ordinal))?[7..];
        if (type is null) return -1;

        // Render processes carry our injector (blocker scriptlets, cosmetic filters and the media detector), which has
        // to act before the site's scripts. This is SelfHost.Main with a render process handler.
        if (type == "renderer")
        {
            try
            {
                var folder = Path.GetDirectoryName(typeof(CefSharp.Core.ObjectFactory).Assembly.Location)!;
                var core = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(
                    Path.Combine(folder, "CefSharp.BrowserSubprocess.Core.dll"));
                var executable = core.GetType("CefSharp.BrowserSubprocess.BrowserSubprocessExecutable", throwOnError: true)!;
                var main = executable.GetMethod("Main", [typeof(IEnumerable<string>), typeof(CefSharp.RenderProcess.IRenderProcessHandler)])!;
                var instance = main.IsStatic ? null : Activator.CreateInstance(executable);
                return (int)main.Invoke(instance, [args, new Blocking.PageInjector()])!;
            }
            catch (Exception ex)
            {
                Blocking.ContentBlocker.Log("Render process without injector: " + ex.Message);
            }
        }

        return CefSharp.BrowserSubprocess.SelfHost.Main(args);
    }

    /// <summary>A new tab; without a URL it opens the start page chosen in settings.</summary>
    public BrowserTab CreateTab(string? url = null) => new(_hostWindow, _ui, url ?? BrowserTab.DefaultHomePage);

    /// <summary>
    /// Preferred page languages (Accept-Language), such as "en-US,en". Applies right away to whatever loads next.
    /// Call it on the UI thread.
    /// </summary>
    public static bool SetPageLanguages(string languages)
    {
        if (!Cef.IsInitialized.GetValueOrDefault()) return false;
        return Cef.GetGlobalRequestContext().SetPreference("intl.accept_languages", languages, out _);
    }

    /// <summary>
    /// Page font sizes, live for every tab: the default (text without its own size) and the minimum, which enlarges
    /// small text and is what most sites notice. Call it on the UI thread.
    /// </summary>
    public static bool SetFontSizes(int defaultSize, int minimumSize)
    {
        if (!Cef.IsInitialized.GetValueOrDefault()) return false;
        var context = Cef.GetGlobalRequestContext();
        var ok = context.SetPreference("webkit.webprefs.default_font_size", defaultSize, out _);
        ok &= context.SetPreference("webkit.webprefs.minimum_font_size", minimumSize, out _);
        return ok;
    }

    private DevTools.DevToolsBridge? _devToolsBridge;

    /// <summary>
    /// Creates DevTools for a tab, shown docked inside the app and pointing at the element at (x, y). The frontend is
    /// another offscreen tab shown with a BrowserView.
    /// </summary>
    public DevTools.DevToolsSession OpenDevTools(BrowserTab tab, int x = 0, int y = 0)
    {
        _devToolsBridge ??= new DevTools.DevToolsBridge();
        return new DevTools.DevToolsSession(this, tab, _devToolsBridge, _ui, x, y);
    }

    /// <summary>Shuts Chromium down. Call it on the UI thread after closing every tab.</summary>
    public void Dispose()
    {
        _devToolsBridge?.Dispose();
        _pump.Stop();
        Gpu.WriteLog(BrowserTab.FrameRate);
        Cef.Shutdown();
        Gpu.Shutdown();
        TimeEndPeriod(1);
    }

    /// <summary>Chromium settings. CefSharp.OffScreen's own settings class is not used because it mutes audio.</summary>
    private sealed class EngineSettings : CefSettingsBase;

    private static int DisplayRefreshRate()
    {
        // DEVMODEW: dmSize at byte 68 and dmDisplayFrequency at byte 184; 220 bytes in total.
        var devMode = new byte[220];
        BitConverter.TryWriteBytes(devMode.AsSpan(68), (ushort)devMode.Length);
        return EnumDisplaySettingsW(null, -1 /* ENUM_CURRENT_SETTINGS */, devMode)
            ? Math.Max(1, BitConverter.ToInt32(devMode, 184))
            : 60;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, byte[] devMode);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    private sealed class MessagePump : CefSharp.Handler.BrowserProcessHandler
    {
        private const int MaxIntervalMs = 1000 / 60;

        private readonly DispatcherQueue _ui;
        private readonly DispatcherQueueTimer _timer;
        private readonly Timer _delayTimer;
        private bool _running;
        private bool _inWork;

        public MessagePump(DispatcherQueue ui)
        {
            _ui = ui;
            _timer = ui.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(MaxIntervalMs);
            _timer.Tick += (_, _) => DoWork();
            // High priority: every page and video frame comes out of this work, and video stutters if it waits
            // behind the UI.
            _delayTimer = new Timer(_ => _ui.TryEnqueue(DispatcherQueuePriority.High, DoWork));
        }

        public void Start()
        {
            _running = true;
            _timer.Start();
        }

        public void Stop()
        {
            _running = false;
            _timer.Stop();
            _delayTimer.Dispose();
        }

        // CEF calls this from any thread; each call replaces the previous request, as in cefclient.
        protected override void OnScheduleMessagePumpWork(long delay)
        {
            if (!_running) return;
            if (delay <= 0)
            {
                _delayTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _ui.TryEnqueue(DispatcherQueuePriority.High, DoWork);
            }
            else if (delay < MaxIntervalMs)
            {
                _delayTimer.Change((int)delay, Timeout.Infinite);
            }
            // Longer delays are covered by the timer.
        }

        private void DoWork()
        {
            if (!_running || _inWork) return; // DoMessageLoopWork is not reentrant
            _inWork = true;
            try { Cef.DoMessageLoopWork(); }
            finally { _inWork = false; }
        }
    }
}
