using System.Windows;

namespace BeautyInstaller;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Test mode, without a window: extracts the app to any folder, with no shortcuts or registry entries.
        //   BeautyBrowserSetup.exe --test-install C:\path\to\folder
        var index = Array.IndexOf(e.Args, "--test-install");
        if (index >= 0 && index + 1 < e.Args.Length)
        {
            var code = 0;
            try
            {
                await Installer.ExtractAsync(e.Args[index + 1], new Progress<double>());
            }
            catch
            {
                code = 1;
            }
            Shutdown(code);
            return;
        }

        // Preview, without a window: renders a screen to a PNG.
        //   BeautyBrowserSetup.exe --preview cover|installing C:\path\to\image.png
        index = Array.IndexOf(e.Args, "--preview");
        if (index >= 0 && index + 2 < e.Args.Length)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = 0;
            try
            {
                SetupWindow.RenderPreview(e.Args[index + 1], e.Args[index + 2]);
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(e.Args[index + 2] + ".error.txt", ex.ToString());
                code = 1;
            }
            Shutdown(code);
            return;
        }

        // Update started by the browser itself, without a window: it waits for the browser to close, replaces the app
        // and, with --relaunch, opens it again.
        //   BeautyBrowserSetup.exe --silent [--relaunch]
        if (e.Args.Contains("--silent"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var updated = await Installer.UpdateSilentlyAsync(relaunch: e.Args.Contains("--relaunch"));
            Shutdown(updated ? 0 : 1);
            return;
        }

        new SetupWindow().Show();
    }
}
