using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using BeautyKit;

namespace BeautyBrowser.Services;

public sealed class DownloadEntry : INotifyPropertyChanged
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    private long _totalBytes;
    private long _receivedBytes;
    private bool _isComplete;
    private bool _isCancelled;
    private bool _isPaused;
    private bool _isFailed;

    public int Id { get; set; }
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public DateTime Started { get; set; }

    /// <summary>Started from a private tab: never written to downloads.json, and dropped when the private space closes.</summary>
    [JsonIgnore] public bool IsPrivate { get; init; }

    public long TotalBytes { get => _totalBytes; set => Set(ref _totalBytes, value); }
    public long ReceivedBytes { get => _receivedBytes; set => Set(ref _receivedBytes, value); }
    public bool IsComplete { get => _isComplete; set => Set(ref _isComplete, value); }
    public bool IsCancelled { get => _isCancelled; set => Set(ref _isCancelled, value); }

    /// <summary>Paused by the user; it continues where it was.</summary>
    [JsonIgnore] public bool IsPaused { get => _isPaused; set => Set(ref _isPaused, value); }

    /// <summary>It ended on its own (network or disk error) instead of being canceled.</summary>
    [JsonIgnore] public bool IsFailed { get => _isFailed; set => Set(ref _isFailed, value); }

    /// <summary>The tab downloading it, which is the one that can cancel or pause it.</summary>
    [JsonIgnore] internal BrowserTab? Tab { get; init; }

    /// <summary>The icon Windows uses for this file type; null (and <see cref="IconGlyph"/> shown) if the shell has none.</summary>
    [JsonIgnore] public Microsoft.UI.Xaml.Media.ImageSource? Icon => FileIcons.For(FileName);
    [JsonIgnore] public string IconGlyph => FileIcons.GlyphFor(FileName);

    [JsonIgnore] public bool IsInProgress => !IsComplete && !IsCancelled;

    /// <summary>Canceled or failed, and there is an address to ask for it again.</summary>
    [JsonIgnore] public bool CanRetry => IsCancelled && Url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsRunning => IsInProgress && !IsPaused;
    [JsonIgnore] public bool IsHeld => IsInProgress && IsPaused;
    [JsonIgnore] public double Progress => TotalBytes > 0 ? 100.0 * ReceivedBytes / TotalBytes : 0;
    [JsonIgnore] public bool IsIndeterminate => IsInProgress && TotalBytes <= 0;

    /// <summary>"7.6 MB — github.com", "3.1 of 7.6 MB — github.com", "Paused — 3.1 of 7.6 MB — github.com" or "Canceled".</summary>
    [JsonIgnore]
    public string Detail
    {
        get
        {
            var host = Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? HostName.WithoutWww(uri.Host) : "";
            var size = IsFailed ? L.T("Failed", "Falló")
                : IsCancelled ? L.T("Canceled", "Cancelada")
                : IsComplete ? FormatSize(Math.Max(TotalBytes, ReceivedBytes))
                : TotalBytes > 0 ? L.T($"{FormatSize(ReceivedBytes)} of {FormatSize(TotalBytes)}", $"{FormatSize(ReceivedBytes)} de {FormatSize(TotalBytes)}")
                : FormatSize(ReceivedBytes);
            if (IsHeld) size = L.T("Paused", "En pausa") + " — " + size;
            return string.IsNullOrEmpty(host) ? size : $"{size} — {host}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Apply(BrowserDownload download)
    {
        TotalBytes = download.TotalBytes;
        ReceivedBytes = download.ReceivedBytes;
        IsComplete = download.IsComplete;
        IsFailed = download.IsFailed;
        IsCancelled = download.IsCancelled;
        if (!IsInProgress) IsPaused = false;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.#", Culture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.#", Culture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", Culture) + " KB",
        _ => bytes + " B",
    };

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        // Derived properties change with any of the state properties.
        foreach (var derived in new[] { nameof(IsInProgress), nameof(Progress), nameof(IsIndeterminate), nameof(Detail), nameof(CanRetry), nameof(IsRunning), nameof(IsHeld) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(derived));
    }
}

/// <summary>Recent downloads, stored in downloads.json.</summary>
public sealed class DownloadsService
{
    private const string FileName = "downloads.json";
    private const int MaxEntries = 30;

    private readonly Dictionary<int, DownloadEntry> _active = [];

    public DownloadsService()
    {
        // Only finished downloads that still exist on disk are kept.
        foreach (var entry in JsonStore.Load<List<DownloadEntry>>(FileName).Where(e => e.IsComplete && File.Exists(e.FullPath)))
            Items.Add(entry);
    }

    /// <summary>Newest first.</summary>
    public ObservableCollection<DownloadEntry> Items { get; } = [];

    public DownloadEntry Start(BrowserDownload download, BrowserTab? tab = null)
    {
        var isPrivate = tab?.IsPrivate == true;
        var entry = new DownloadEntry
        {
            Id = download.Id,
            Url = download.Url,
            FileName = download.FileName,
            FullPath = download.FullPath,
            Started = DateTime.Now,
            IsPrivate = isPrivate,
            Tab = tab,
        };
        entry.Apply(download);

        _active[download.Id] = entry;
        Items.Insert(0, entry);
        while (Items.Count > MaxEntries) Items.RemoveAt(Items.Count - 1);
        return entry;
    }

    public void Update(BrowserDownload download)
    {
        if (!_active.TryGetValue(download.Id, out var entry)) return;
        entry.Apply(download);
        if (download.IsComplete || download.IsCancelled)
        {
            _active.Remove(download.Id);
            Save();
        }
    }

    /// <summary>Stops a running download; Chromium deletes what it had so far.</summary>
    public void Cancel(DownloadEntry entry)
    {
        if (entry.IsInProgress) entry.Tab?.CancelDownload(entry.Id);
    }

    public void Pause(DownloadEntry entry)
    {
        if (!entry.IsRunning || entry.Tab is not { } tab) return;
        if (tab.PauseDownload(entry.Id)) entry.IsPaused = true;
    }

    public void Resume(DownloadEntry entry)
    {
        if (!entry.IsHeld || entry.Tab is not { } tab) return;
        if (tab.ResumeDownload(entry.Id)) entry.IsPaused = false;
    }

    /// <summary>Asks again for a download that was canceled or failed: it leaves the list and a new one starts from zero.</summary>
    public void Retry(DownloadEntry entry)
    {
        if (!entry.CanRetry) return;
        Items.Remove(entry);
        RetryRequested?.Invoke(this, entry);
    }

    /// <summary>A download has to be started again (the shell picks the tab that asks for it).</summary>
    public event EventHandler<DownloadEntry>? RetryRequested;

    /// <summary>The private space closed: its downloads leave the list (the files stay on disk).</summary>
    public void RemovePrivate()
    {
        foreach (var entry in Items.Where(e => e.IsPrivate).ToList()) Items.Remove(entry);
        foreach (var id in _active.Where(kv => kv.Value.IsPrivate).Select(kv => kv.Key).ToList()) _active.Remove(id);
    }

    public void Save() => JsonStore.Save(FileName, Items.Where(e => e.IsComplete && !e.IsPrivate).ToList());
}
