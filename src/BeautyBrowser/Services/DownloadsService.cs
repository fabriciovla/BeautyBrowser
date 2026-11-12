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

    [JsonIgnore] public bool IsInProgress => !IsComplete && !IsCancelled;
    [JsonIgnore] public double Progress => TotalBytes > 0 ? 100.0 * ReceivedBytes / TotalBytes : 0;
    [JsonIgnore] public bool IsIndeterminate => IsInProgress && TotalBytes <= 0;

    /// <summary>"7.6 MB — github.com", "3.1 of 7.6 MB — github.com" or "Canceled".</summary>
    [JsonIgnore]
    public string Detail
    {
        get
        {
            var host = Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? HostName.WithoutWww(uri.Host) : "";
            var size = IsCancelled ? L.T("Canceled", "Cancelada")
                : IsComplete ? FormatSize(Math.Max(TotalBytes, ReceivedBytes))
                : TotalBytes > 0 ? L.T($"{FormatSize(ReceivedBytes)} of {FormatSize(TotalBytes)}", $"{FormatSize(ReceivedBytes)} de {FormatSize(TotalBytes)}")
                : FormatSize(ReceivedBytes);
            return string.IsNullOrEmpty(host) ? size : $"{size} — {host}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Apply(BrowserDownload download)
    {
        TotalBytes = download.TotalBytes;
        ReceivedBytes = download.ReceivedBytes;
        IsComplete = download.IsComplete;
        IsCancelled = download.IsCancelled;
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
        foreach (var derived in new[] { nameof(IsInProgress), nameof(Progress), nameof(IsIndeterminate), nameof(Detail) })
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

    public DownloadEntry Start(BrowserDownload download, bool isPrivate = false)
    {
        var entry = new DownloadEntry
        {
            Id = download.Id,
            Url = download.Url,
            FileName = download.FileName,
            FullPath = download.FullPath,
            Started = DateTime.Now,
            IsPrivate = isPrivate,
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

    /// <summary>The private space closed: its downloads leave the list (the files stay on disk).</summary>
    public void RemovePrivate()
    {
        foreach (var entry in Items.Where(e => e.IsPrivate).ToList()) Items.Remove(entry);
        foreach (var id in _active.Where(kv => kv.Value.IsPrivate).Select(kv => kv.Key).ToList()) _active.Remove(id);
    }

    public void Save() => JsonStore.Save(FileName, Items.Where(e => e.IsComplete && !e.IsPrivate).ToList());
}
