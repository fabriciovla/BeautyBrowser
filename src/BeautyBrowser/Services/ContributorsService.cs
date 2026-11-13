using System.Net.Http;
using System.Text.Json;
using BeautyKit;

namespace BeautyBrowser.Services;

/// <summary>
/// The people who contributed to BeautyBrowser on GitHub, for the start page (under the search box). The list is kept in
/// contributors.json and refreshed once a day, so new tabs never wait for the network and work offline.
/// </summary>
internal static class ContributorsService
{
    private const string Api = "https://api.github.com/repos/fabriciovla/BeautyBrowser/contributors?per_page=40";
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromDays(1);
    private static readonly string CacheFile = Path.Combine(JsonStore.Folder, "contributors.json");

    private sealed record Entry(string Login, string Avatar, string Profile);

    /// <summary>Loads the saved list right away and, when it is a day old or missing, fetches a fresh one.</summary>
    public static async Task LoadAsync()
    {
        try
        {
            var cached = File.Exists(CacheFile) ? Read(await File.ReadAllTextAsync(CacheFile)) : null;
            if (cached is not null) Publish(cached);
            if (cached is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(CacheFile) < RefreshEvery) return;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BeautyBrowser");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var fresh = Parse(await http.GetStringAsync(Api));
            if (fresh.Count == 0) return;
            Publish(fresh);
            Directory.CreateDirectory(JsonStore.Folder);
            await File.WriteAllTextAsync(CacheFile, JsonSerializer.Serialize(fresh));
        }
        catch
        {
            // Offline or rate limited: the saved list (if any) stays; the start page simply shows nothing new.
        }
    }

    /// <summary>Real people only (no bots), with the avatar and profile links checked to point where they should.</summary>
    private static List<Entry> Parse(string json)
    {
        var list = new List<Entry>();
        using var doc = JsonDocument.Parse(json);
        foreach (var user in doc.RootElement.EnumerateArray())
        {
            if (user.TryGetProperty("type", out var type) && type.GetString() != "User") continue;
            var login = user.GetProperty("login").GetString() ?? "";
            var avatar = user.GetProperty("avatar_url").GetString() ?? "";
            var profile = user.GetProperty("html_url").GetString() ?? "";
            if (Valid(login, avatar, profile)) list.Add(new Entry(login, avatar, profile));
        }
        return list;
    }

    private static List<Entry>? Read(string json)
    {
        var list = JsonSerializer.Deserialize<List<Entry>>(json);
        return list?.Where(e => Valid(e.Login, e.Avatar, e.Profile)).ToList();
    }

    private static bool Valid(string login, string avatar, string profile) =>
        login.Length is > 0 and <= 39 && login.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && avatar.StartsWith("https://avatars.githubusercontent.com/", StringComparison.Ordinal)
        && profile.StartsWith("https://github.com/", StringComparison.Ordinal);

    private static void Publish(List<Entry> list) =>
        StartPage.Contributors = list.Select(e => new StartPageContributor(e.Login, e.Avatar, e.Profile)).ToList();
}
