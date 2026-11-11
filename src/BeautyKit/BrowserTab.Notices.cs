using System.Collections.Concurrent;
using CefSharp;

namespace BeautyKit;

/// <summary>A download of a file that can run code (an .exe, a script…): waits for the user to keep or discard it.</summary>
public sealed class DownloadApproval
{
    private readonly Action<bool> _decide;
    private int _decided;

    internal DownloadApproval(string fileName, string url, Action<bool> decide)
    {
        FileName = fileName;
        Url = url;
        _decide = decide;
    }

    public string FileName { get; }

    public string Url { get; }

    /// <summary>Keeps the download (true) or cancels it (false). Only the first call counts.</summary>
    public void Decide(bool keep)
    {
        if (Interlocked.Exchange(ref _decided, 1) != 0) return;
        try
        {
            _decide(keep);
        }
        catch
        {
            // The tab was closed while the question was open: there is nothing left to continue or cancel.
        }
    }
}

public sealed partial class BrowserTab
{
    /// <summary>The page tried to open a popup without a click and it was blocked: (page origin, blocked URL).</summary>
    public event EventHandler<(string Origin, string Url)>? PopupBlocked;

    /// <summary>
    /// A dangerous file is about to be downloaded. The download waits for <see cref="DownloadApproval.Decide"/>; without
    /// a listener it goes ahead as before.
    /// </summary>
    public event EventHandler<DownloadApproval>? DownloadApprovalRequested;

    // Sites where the user chose to always allow popups. Read on Chromium's thread, written from the UI one.
    private static readonly ConcurrentDictionary<string, byte> PopupAllowedSites = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The scheme and host of a page ("https://example.com"), the key sites are remembered by.</summary>
    public static string OriginOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant()
            : "";

    public static void AllowPopupsFrom(string origin)
    {
        if (origin.Length > 0) PopupAllowedSites[origin] = 0;
    }

    public static bool ArePopupsAllowedFrom(string origin) => origin.Length > 0 && PopupAllowedSites.ContainsKey(origin);

    private void ReportBlockedPopup(string origin, string url) => OnUi(() => PopupBlocked?.Invoke(this, (origin, url)));

    /// <summary>
    /// Asks whether to keep a dangerous download. Returns false when nobody listens, so the caller goes on by itself.
    /// <paramref name="decide"/> runs on the UI thread with the answer.
    /// </summary>
    private bool AskAboutDownload(string fileName, string url, Action<bool> decide)
    {
        if (DownloadApprovalRequested is null) return false;
        OnUi(() =>
        {
            if (DownloadApprovalRequested is { } handler) handler(this, new DownloadApproval(fileName, url, decide));
            else decide(true);
        });
        return true;
    }
}
