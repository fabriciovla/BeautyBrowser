using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeautyKit;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace BeautyBrowser.Services;

public sealed class HistoryEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string? FaviconUrl { get; set; }
    public DateTime LastVisit { get; set; }
    public int VisitCount { get; set; }

    public ImageSource? Favicon => Favicons.FromUrl(FaviconUrl);

    public string DisplayTime
    {
        get
        {
            var span = DateTime.Now - LastVisit;
            if (span.TotalMinutes < 1) return L.T("Just now", "Recién");
            if (span.TotalMinutes < 60) return L.T($"{(int)span.TotalMinutes} min ago", $"Hace {(int)span.TotalMinutes} min");
            if (span.TotalHours < 24 && LastVisit.Date == DateTime.Today) return LastVisit.ToString("HH:mm");
            if (LastVisit.Date == DateTime.Today.AddDays(-1)) return L.T($"Yesterday {LastVisit:HH:mm}", $"Ayer {LastVisit:HH:mm}");
            return LastVisit.ToString("dd/MM/yyyy HH:mm");
        }
    }

    public string Subtitle => $"{DisplayTime} · {Url}";

    /// <summary>"https://www.twitch.tv/x" → "twitch.tv".</summary>
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? HostName.WithoutWww(uri.Host) : Url;

    /// <summary>"https://www.spaceship.com/domain-search/x" → "spaceship.com/domain-search/x".</summary>
    public string DisplayUrl
    {
        get
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)) return Url;
            var host = HostName.WithoutWww(uri.Host);
            if (!uri.IsDefaultPort) host += ":" + uri.Port;
            var rest = uri.PathAndQuery == "/" ? "" : uri.PathAndQuery;
            return host + rest;
        }
    }

    /// <summary>Time of the visit ("23:17"); the group header tells the day.</summary>
    public string TimeLabel => LastVisit.ToString("HH:mm");
}

/// <summary>Browsing history stored in the local SQLite database (%LOCALAPPDATA%\BeautyBrowser\browser.db).</summary>
public sealed class HistoryService
{
    private readonly DatabaseService _db;
    private readonly List<HistoryEntry> _cache = [];
    private readonly DispatcherQueue _ui;

    public event EventHandler? HistoryChanged;

    // Database writes run in order on a background thread: a page that changes its title often (WhatsApp with each
    // unread message) must not make the UI thread, which also runs Chromium, wait on the disk.
    private readonly object _writeLock = new();
    private Task _writes = Task.CompletedTask;

    // HistoryChanged is raised at most twice per second (the start page and the history panel rebuild on it).
    private readonly DispatcherQueueTimer _notifyTimer;

    public HistoryService(DispatcherQueue ui, DatabaseService db)
    {
        _ui = ui;
        _db = db;
        _notifyTimer = ui.CreateTimer();
        _notifyTimer.Interval = TimeSpan.FromMilliseconds(500);
        _notifyTimer.IsRepeating = false;
        _notifyTimer.Tick += (_, _) => HistoryChanged?.Invoke(this, EventArgs.Empty);

        MigrateFromJsonIfPresent();
        ReloadCache();
    }

    private void MigrateFromJsonIfPresent()
    {
        try
        {
            var jsonPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BeautyBrowser", "history.json");

            if (File.Exists(jsonPath))
            {
                var list = JsonStore.Load<List<HistoryEntry>>("history.json");
                if (list != null && list.Count > 0)
                {
                    foreach (var item in list)
                    {
                        if (IsRecordable(item.Url))
                        {
                            _db.RecordVisit(item.Url, item.Title, item.FaviconUrl);
                        }
                    }
                }
                File.Move(jsonPath, jsonPath + ".migrated.bak", overwrite: true);
            }
        }
        catch
        {
            // Non-blocking migration
        }
    }

    private void ReloadCache()
    {
        _cache.Clear();
        _cache.AddRange(_db.GetRecentHistory(1500));
    }

    /// <summary>A URL was visited (a tab's address changed).</summary>
    public void Visit(string url)
    {
        if (!IsRecordable(url)) return;

        var existing = Find(url);
        var title = existing?.Title ?? url;
        var favicon = existing?.FaviconUrl;

        Write(() => _db.RecordVisit(url, title, favicon));

        if (existing is null)
        {
            existing = new HistoryEntry
            {
                Url = url,
                Title = title,
                FaviconUrl = favicon,
                LastVisit = DateTime.Now,
                VisitCount = 1
            };
            _cache.Insert(0, existing);
        }
        else
        {
            existing.LastVisit = DateTime.Now;
            existing.VisitCount++;
        }

        NotifyChanged();
    }

    /// <summary>Updates the title and favicon of an already visited URL.</summary>
    public void Describe(string url, string title, string? faviconUrl)
    {
        if (!IsRecordable(url)) return;

        var existing = Find(url);
        if (existing is not null)
        {
            var sameTitle = string.IsNullOrWhiteSpace(title) || existing.Title == title;
            var sameIcon = faviconUrl is null || existing.FaviconUrl == faviconUrl;
            if (sameTitle && sameIcon) return; // nothing new
            if (!string.IsNullOrWhiteSpace(title)) existing.Title = title;
            if (faviconUrl is not null) existing.FaviconUrl = faviconUrl;
        }

        Write(() => _db.UpdateDescription(url, title, faviconUrl));
        NotifyChanged();
    }

    public void Delete(string url)
    {
        Write(() => _db.DeleteHistoryEntry(url));
        _cache.RemoveAll(e => e.Url == url);
        NotifyChanged(immediate: true);
    }

    public void ClearAll()
    {
        Write(_db.ClearAllHistory);
        _cache.Clear();
        NotifyChanged(immediate: true);
    }

    public IEnumerable<HistoryEntry> Recent(int max) =>
        _cache.OrderByDescending(e => e.LastVisit).Take(max);

    /// <summary>Searches titles and URLs, favoring the most visited and recent.</summary>
    public IEnumerable<HistoryEntry> Search(string query, int max)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Recent(max);

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _cache
            .Where(e => terms.All(t =>
                e.Title.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                e.Url.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => Score(e, query))
            .Take(max);
    }

    /// <summary>
    /// The most visited sites (one per site) for the start page: visits to every page of a site add up and ties go to
    /// the most recent. They show by name ("Spotify", not "open.spotify.com") and sign-in pages are skipped.
    /// </summary>
    public IReadOnlyList<StartPageSite> TopSites(int max) =>
        _cache
            .Select(e => (Entry: e, Uri: Uri.TryCreate(e.Url, UriKind.Absolute, out var u) ? u : null))
            .Where(x => x.Uri is not null && !IsUtilityHost(x.Uri.Host))
            .GroupBy(x => SiteName(x.Uri!.Host), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(x => x.Entry.VisitCount))
            .ThenByDescending(g => g.Max(x => x.Entry.LastVisit))
            .Take(max)
            .Select(g =>
            {
                // The address of the site's most visited host (google.com before mail.google.com).
                var top = g.GroupBy(x => x.Uri!.Host, StringComparer.OrdinalIgnoreCase)
                           .OrderByDescending(h => h.Sum(x => x.Entry.VisitCount))
                           .First().First().Uri!;
                var icon = g.Select(x => x.Entry.FaviconUrl).FirstOrDefault(f => !string.IsNullOrEmpty(f));
                return new StartPageSite(g.Key, $"{top.Scheme}://{top.Host}/", icon);
            })
            .ToList();

    // Sign-in and account subdomains (accounts.google.com, login.microsoftonline.com…).
    private static readonly string[] UtilitySubdomains = ["accounts", "account", "login", "signin", "auth", "sso", "oauth", "myaccount"];

    // Internal service hosts that show up while passing through a sign-in and are not sites to visit.
    private static readonly HashSet<string> UtilityHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "gds.google.com", "consent.google.com", "consent.youtube.com",
    };

    private static bool IsUtilityHost(string host) =>
        UtilityHosts.Contains(host) ||
        UtilitySubdomains.Any(s => host.StartsWith(s + ".", StringComparison.OrdinalIgnoreCase));

    // Generic second level of country domains: mercadolibre.com.ar, bbc.co.uk…
    private static readonly HashSet<string> SecondLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "co", "org", "net", "gov", "gob", "edu", "ac", "or", "ne", "go",
    };

    /// <summary>The site name from its host: "open.spotify.com" → "Spotify", "claude.ai" → "Claude".</summary>
    private static string SiteName(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out _)) return host;

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (labels.Count > 1) labels.RemoveAt(labels.Count - 1);                                // the TLD (.com, .ai)
        if (labels.Count > 1 && SecondLevel.Contains(labels[^1])) labels.RemoveAt(labels.Count - 1); // .com.ar
        var name = labels.Count > 0 ? labels[^1] : host;
        return name.Length == 0 ? host : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
    }

    /// <summary>Waits (briefly) for pending writes, on exit.</summary>
    public void Flush()
    {
        Task pending;
        lock (_writeLock) pending = _writes;
        pending.Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>The history entry of an address, if it was visited (title and icon for restored tabs).</summary>
    public HistoryEntry? Lookup(string url) => Find(url);

    private void Write(Action write)
    {
        lock (_writeLock)
        {
            _writes = _writes.ContinueWith(_ =>
            {
                try { write(); }
                catch { /* losing one history write must never break browsing */ }
            }, TaskScheduler.Default);
        }
    }

    private void NotifyChanged(bool immediate = false)
    {
        if (!_ui.HasThreadAccess)
        {
            _ui.TryEnqueue(() => NotifyChanged(immediate));
            return;
        }
        _notifyTimer.Stop();
        if (immediate) HistoryChanged?.Invoke(this, EventArgs.Empty);
        else _notifyTimer.Start();
    }

    private static double Score(HistoryEntry entry, string query)
    {
        var host = Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) ? HostName.WithoutWww(uri.Host) : "";
        var hostBonus = host.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 50 : 0;
        var recency = 20 / (1 + (DateTime.Now - entry.LastVisit).TotalDays);
        return hostBonus + Math.Log(1 + entry.VisitCount) * 10 + recency;
    }

    private HistoryEntry? Find(string url) => _cache.Find(e => e.Url == url);

    private static bool IsRecordable(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
