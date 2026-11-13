using System.IO.Pipes;
using System.Text;

namespace BeautyBrowser;

/// <summary>
/// Links from other apps. As the default browser, Windows starts <c>BeautyBrowser.exe "--beauty-open=https://…"</c>
/// (registered by the installer); it is an option rather than a bare argument so Chromium does not try to open it.
/// If a BeautyBrowser is already running, the new one hands the link over a per-user pipe and exits, and the link
/// opens as a tab in the existing window.
/// </summary>
internal static class ExternalLinks
{
    private const string OpenSwitch = "--beauty-open=";
    private static readonly string PipeName = "BeautyBrowser.Open." + Environment.UserName;

    /// <summary>The link the app was opened with, if any (also a path to a local .html file).</summary>
    public static string? FromCommandLine()
    {
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            var value = arg.StartsWith(OpenSwitch, StringComparison.OrdinalIgnoreCase) ? arg[OpenSwitch.Length..]
                : arg.StartsWith('-') ? null
                : arg;
            if (Normalize(value) is { } url) return url;
        }
        return null;
    }

    private static string? Normalize(string? value)
    {
        value = value?.Trim().Trim('"');
        if (string.IsNullOrEmpty(value)) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file")
            return uri.AbsoluteUri;
        try
        {
            if (File.Exists(value)) return new Uri(Path.GetFullPath(value)).AbsoluteUri; // a local .html file
        }
        catch
        {
            // Invalid path: ignored.
        }
        return null;
    }

    /// <summary>Hands the link to the running BeautyBrowser. Returns false when that fails.</summary>
    public static bool SendToRunning(string url)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(url);
            pipe.Write(bytes);
            pipe.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Messages that are not links: open an installed app, or just show the browser.</summary>
    public const string AppMessage = "app:";
    public const string ShowMessage = "show:";

    /// <summary>Listens in the background for links (and app or show requests) sent by other instances.</summary>
    public static void Listen(Action<string> open, Action<string>? command = null)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    pipe.WaitForConnection();
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var message = reader.ReadToEnd();
                    if (message.StartsWith(AppMessage, StringComparison.Ordinal) || message.StartsWith(ShowMessage, StringComparison.Ordinal))
                        command?.Invoke(message);
                    else if (Normalize(message) is { } url) open(url);
                }
                catch
                {
                    Thread.Sleep(500); // the pipe failed (rare): open it again
                }
            }
        })
        {
            IsBackground = true,
            Name = "BeautyBrowser links",
        };
        thread.Start();
    }
}
