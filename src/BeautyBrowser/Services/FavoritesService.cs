using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BeautyBrowser.Services;

public sealed class FavoriteSite
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string? FaviconUrl { get; set; }

    [JsonIgnore]
    public ImageSource? Icon => BeautyKit.Favicons.FromUrl(FaviconUrl);

    [JsonIgnore]
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", "") : Url;
}

/// <summary>Sites pinned to the sidebar grid, stored in favorites.json.</summary>
public sealed class FavoritesService
{
    private const string FileName = "favorites.json";

    public FavoritesService()
    {
        foreach (var site in JsonStore.Load<List<FavoriteSite>>(FileName)) Items.Add(site);
        Items.CollectionChanged += (_, _) => JsonStore.Save(FileName, Items.ToList());
    }

    public ObservableCollection<FavoriteSite> Items { get; } = [];

    public bool Contains(string url) => Items.Any(f => SameSite(f.Url, url));

    public FavoriteSite? Add(string url, string title, string? faviconUrl)
    {
        if (string.IsNullOrWhiteSpace(url) || Contains(url)) return null;
        var site = new FavoriteSite { Url = url, Title = title, FaviconUrl = faviconUrl };
        Items.Add(site);
        return site;
    }

    public event EventHandler<FavoriteSite>? IconChanged;

    /// <summary>Stores the icon the favorite's tab brought, since icon URLs change over time.</summary>
    public void UpdateIcon(FavoriteSite site, string? faviconUrl)
    {
        if (string.IsNullOrEmpty(faviconUrl) || site.FaviconUrl == faviconUrl || !Items.Contains(site)) return;
        site.FaviconUrl = faviconUrl;
        JsonStore.Save(FileName, Items.ToList());
        IconChanged?.Invoke(this, site);
    }

    public void Move(FavoriteSite site, int newIndex)
    {
        var oldIndex = Items.IndexOf(site);
        newIndex = Math.Clamp(newIndex, 0, Items.Count - 1);
        if (oldIndex < 0 || oldIndex == newIndex) return;
        Items.Move(oldIndex, newIndex);
    }

    public void Remove(string url)
    {
        foreach (var site in Items.Where(f => SameSite(f.Url, url)).ToList()) Items.Remove(site);
    }

    /// <summary>Same page, ignoring "www.", the trailing slash and the fragment.</summary>
    public static bool SameSite(string a, string b) => Normalize(a) == Normalize(b);

    private static string Normalize(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? (uri.Host.Replace("www.", "") + uri.AbsolutePath.TrimEnd('/')).ToLowerInvariant()
            : url.ToLowerInvariant();
}
