namespace BeautyBrowser.Services;

/// <summary>A tab that was archived for not being used: kept to be found and opened again.</summary>
public sealed class ArchivedTab
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string? FaviconUrl { get; set; }
    public string Space { get; set; } = "";
    public DateTime LastActive { get; set; }
    public DateTime ArchivedAt { get; set; }
}

/// <summary>The tabs the browser archived on its own, in the database.</summary>
public sealed class ArchivedTabsService(DatabaseService database)
{
    public event EventHandler? Changed;

    public int Count => database.CountArchivedTabs();

    public void Add(string url, string title, string? faviconUrl, string space, DateTime lastActiveUtc)
    {
        database.AddArchivedTab(url, title, faviconUrl, space, lastActiveUtc);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The newest first; with a <paramref name="query"/>, only the ones whose title or address contain it.</summary>
    public List<ArchivedTab> Search(string query, int limit = 300) => database.GetArchivedTabs(query, limit);

    public void Remove(long id)
    {
        database.DeleteArchivedTab(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        database.ClearArchivedTabs();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
