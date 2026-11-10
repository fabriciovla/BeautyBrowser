using System.ComponentModel;
using CefSharp;

namespace BeautyKit;

/// <summary>What is playing in a tab, as reported by the detector injected into the page.</summary>
public sealed record MediaInfo(
    string Title,
    string Artist,
    string? ArtworkUrl,
    bool IsPlaying,
    double Position,
    double Duration,      // seconds; 0 when unknown
    bool IsLive,
    bool HasNext,
    bool HasPrevious,
    bool IsVideo,
    DateTime ReceivedAt)
{
    /// <summary>Estimated position now, between the page's reports (one per second).</summary>
    public double PositionNow =>
        IsPlaying && Duration > 0
            ? Math.Min(Duration, Position + (DateTime.UtcNow - ReceivedAt).TotalSeconds)
            : Position;
}

public sealed partial class BrowserTab
{
    private MediaInfo? _media;
    private string? _mediaFrame; // id of the playing document, set by the page detector
    private bool _audioMuted;
    private int _blocked;
    private int _blockedPublishScheduled;

    public MediaInfo? Media { get => _media; private set => Set(ref _media, value); }

    /// <summary>When it last started playing, to pick one when several tabs play.</summary>
    public DateTime MediaStartedAt { get; private set; }

    public bool IsAudioMuted { get => _audioMuted; private set => Set(ref _audioMuted, value); }

    public int BlockedCount => Volatile.Read(ref _blocked);

    public void SetAudioMuted(bool muted)
    {
        Host()?.SetAudioMuted(muted);
        IsAudioMuted = muted;
    }

    public void MediaPlayPause() => MediaCommand("toggle");
    public void MediaNext() => MediaCommand("next");
    public void MediaPrevious() => MediaCommand("previous");
    public void MediaSeek(double seconds) => MediaCommand("seek", seconds);

    public void MediaDismiss()
    {
        MediaCommand("pause");
        Media = null;
        _mediaFrame = null;
    }

    private void MediaCommand(string command, double value = 0)
    {
        if (_mediaFrame is null || !_browser.IsBrowserInitialized || _disposed) return;
        // Sent to every frame; only the document with this id (the playing one) acts on it.
        var arg = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var script = $"window.__bbMedia && window.__bbMedia('{command}', {arg}, '{_mediaFrame}');";
        try
        {
            foreach (var frame in _browser.GetBrowser().GetAllFrames())
            {
                using (frame)
                {
                    if (frame.IsValid) frame.ExecuteJavaScriptAsync(script);
                }
            }
        }
        catch { }
    }

    private void ClearMedia()
    {
        Media = null;
        _mediaFrame = null;
    }

    private void OnJavascriptMessage(object? sender, JavascriptMessageReceivedEventArgs e)
    {
        // CefSharp raises this on another thread with the frame already released, so e.Frame must not be touched.
        if (e.Message is not IDictionary<string, object?> message) return;
        if (!(message.TryGetValue("type", out var type) && type as string == "bb-media")) return;
        if (!(message.TryGetValue("token", out var token) && token is string { Length: > 0 and < 64 } frameId)) return;
        if (frameId.Any(c => !char.IsAsciiLetterOrDigit(c))) return; // it is embedded in a script: letters and digits only
        OnUi(() => ReceiveMedia(message, frameId));
    }

    private void ReceiveMedia(IDictionary<string, object?> m, string? frameId)
    {
        if (frameId is null) return;
        string Text(string key) => m.TryGetValue(key, out var v) && v is string s ? s : "";
        bool Flag(string key) => m.TryGetValue(key, out var v) && v is bool b && b;
        double Number(string key) => m.TryGetValue(key, out var v) ? v switch
        {
            int i => i, double d => d, long l => l, float f => f, _ => 0,
        } : 0;

        // Another frame reports: take it only if it plays sound, so a muted video elsewhere does not take over.
        if (Flag("gone"))
        {
            if (frameId == _mediaFrame) ClearMedia();
            return;
        }

        var playing = Flag("playing");
        var duration = Number("duration");
        var audible = !Flag("muted") && Flag("hasAudio");
        var title = Text("title");

        if (frameId != _mediaFrame || Media is null)
        {
            // Something new has to really play: not a muted background video or a one-second beep.
            var meaningful = playing && audible && (title.Length > 0 || duration < 0 || duration >= 8);
            if (!meaningful) return;
            _mediaFrame = frameId;
        }

        var previous = Media;
        if (playing && previous is not { IsPlaying: true }) MediaStartedAt = DateTime.UtcNow;

        Media = new MediaInfo(
            Title: title.Length > 0 ? title : Title,
            Artist: Text("artist") is { Length: > 0 } artist ? artist : HostOf(Url),
            ArtworkUrl: Text("artwork") is { Length: > 0 } art ? art : null,
            IsPlaying: playing,
            Position: Number("position"),
            Duration: Math.Max(0, duration),
            IsLive: duration < 0,
            HasNext: Flag("hasNext"),
            HasPrevious: true, // without a page "previous", restart from the beginning
            IsVideo: Flag("video"),
            ReceivedAt: DateTime.UtcNow);
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", "") : "";


    internal void CountBlocked()
    {
        Interlocked.Increment(ref _blocked);
        // Published a few times per second at most.
        if (Interlocked.Exchange(ref _blockedPublishScheduled, 1) == 1) return;
        _ui.TryEnqueue(async () =>
        {
            await Task.Delay(250);
            Volatile.Write(ref _blockedPublishScheduled, 0);
            if (!_disposed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BlockedCount)));
        });
    }

    private void ResetBlockedCount()
    {
        Interlocked.Exchange(ref _blocked, 0);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BlockedCount)));
    }
}
