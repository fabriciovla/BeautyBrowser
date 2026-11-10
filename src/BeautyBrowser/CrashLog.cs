namespace BeautyBrowser;

/// <summary>Writes unhandled exceptions to %LOCALAPPDATA%\BeautyBrowser\crash.log.</summary>
internal static class CrashLog
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeautyBrowser", "crash.log");

    public static void Install(Microsoft.UI.Xaml.Application app)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Write("Task", e.Exception);
        app.UnhandledException += (_, e) => Write("XAML", e.Exception);
    }

    public static void Write(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n");
        }
        catch
        {
            // Nothing else to do if the log cannot be written.
        }
    }
}
