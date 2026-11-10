using BeautyKit;

namespace BeautyBrowser;

/// <summary>
/// BeautyBrowser.exe has two jobs. When Chromium launches it as a helper process (renderer, GPU, network… with
/// "--type=…"), it runs that and exits without a window; otherwise it opens the browser. This way helper processes
/// use the .NET runtime bundled with the app.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var subprocessExit = BrowserEngine.RunSubprocessIfRequested(args);
        if (subprocessExit >= 0)
        {
            // A finished Chromium helper process exits immediately, as Chrome's do. Letting .NET and CefSharp run their
            // final cleanup sometimes crashed there and Windows showed "Exception Processing Message 0xc0000005" when
            // closing tabs. These processes keep no state, so there is nothing to clean up.
            TerminateProcess(GetCurrentProcess(), (uint)subprocessExit);
            return subprocessExit;
        }

        XamlGeneratedProgram.XamlGeneratedMain();
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint process, uint exitCode);
}
