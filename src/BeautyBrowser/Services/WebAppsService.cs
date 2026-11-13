using System.Collections.ObjectModel;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeautyKit;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BeautyBrowser.Services;

/// <summary>A site installed as an app: it opens in its own window with its own taskbar icon.</summary>
public sealed class WebApp
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string StartUrl { get; set; } = "";
    /// <summary>Pages under this address belong to the app; others open in the browser.</summary>
    public string Scope { get; set; } = "";
    /// <summary>"#rrggbb" from the site (manifest or theme-color), or null for ours.</summary>
    public string? ThemeColor { get; set; }
    public string IconPath { get; set; } = "";

    /// <summary>Windows groups taskbar buttons by this: one per app, apart from the browser.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string AppUserModelId => "BeautyBrowser.App." + Id;

    public bool InScope(string? url) =>
        url is not null && url.StartsWith(Scope, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// "Install as app", as Chrome and Edge do: reads the site's manifest (or its title, theme color and icons), saves an
/// icon, and creates Start menu and desktop shortcuts that run <c>BeautyBrowser.exe --beauty-app=ID</c>. The list lives
/// in apps.json; uninstalling removes the shortcuts and the icon. Apps share the browser's profile (sessions, cookies).
/// </summary>
public sealed class WebAppsService
{
    private const string FileName = "apps.json";
    public const string AppSwitch = "--beauty-app=";

    private static readonly string IconsFolder = Path.Combine(JsonStore.Folder, "Apps");
    private static readonly string StartMenuFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "BeautyBrowser Apps");

    public ObservableCollection<WebApp> Apps { get; }

    public WebAppsService() => Apps = new ObservableCollection<WebApp>(JsonStore.Load<List<WebApp>>(FileName));

    public WebApp? Find(string id) => Apps.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The installed app this page belongs to, if any (so the menu can say "Open in app").</summary>
    public WebApp? ForUrl(string? url) => Apps.FirstOrDefault(a => a.InScope(url));

    /// <summary>The app id passed on the command line (<c>--beauty-app=ID</c>), if any.</summary>
    public static string? IdFromCommandLine() =>
        Environment.GetCommandLineArgs().Skip(1)
            .FirstOrDefault(a => a.StartsWith(AppSwitch, StringComparison.OrdinalIgnoreCase))?[AppSwitch.Length..].Trim('"');

    /// <summary>
    /// Installs the tab's site. <paramref name="confirm"/> gets the proposed name and icon and returns the name the user
    /// chose, or null to cancel. Returns the app; null when cancelled or when the page cannot be installed (Failed).
    /// </summary>
    public async Task<(WebApp? App, bool Failed)> InstallAsync(BrowserTab tab, Func<string, string, Task<string?>> confirm)
    {
        if (tab.IsPrivate || !Uri.TryCreate(tab.Url, UriKind.Absolute, out var pageUri) || pageUri.Scheme != "https" && pageUri.Scheme != "http")
            return (null, true);

        var hints = await tab.ReadWebAppHintsAsync();
        var page = hints?.Url ?? tab.Url;
        var name = hints?.AppName is { Length: > 0 and <= 40 } siteName ? siteName.Trim() : CleanTitle(hints?.Title ?? tab.Title, pageUri);
        var startUrl = page;
        var scope = pageUri.GetLeftPart(UriPartial.Authority) + "/";
        var theme = ParseColor(hints?.ThemeColor);
        var iconCandidates = new List<(string Url, int Size)>();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BeautyBrowser");

        // The manifest, when the site has one: its name, start page, scope, color and icons win.
        if (hints?.ManifestUrl is { } manifestUrl && Uri.TryCreate(manifestUrl, UriKind.Absolute, out var manifestUri)
            && manifestUri.Scheme == "https")
        {
            try
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync(manifestUri));
                var root = doc.RootElement;
                string? Text(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                name = Text("short_name") is { Length: > 0 and <= 30 } shortName ? shortName : Text("name") ?? name;
                if (Resolve(manifestUri, Text("start_url")) is { } start && SameSite(start, pageUri)) startUrl = start.AbsoluteUri;
                if (Resolve(manifestUri, Text("scope")) is { } s && SameSite(s, pageUri)) scope = s.AbsoluteUri;
                theme = ParseColor(Text("theme_color")) ?? theme;
                if (root.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Array)
                {
                    foreach (var icon in icons.EnumerateArray())
                    {
                        var purpose = icon.TryGetProperty("purpose", out var p) ? p.GetString() ?? "" : "";
                        if (purpose.Length > 0 && !purpose.Contains("any")) continue; // maskable icons are cropped
                        var src = icon.TryGetProperty("src", out var v) ? v.GetString() : null;
                        var type = icon.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                        if (type.Contains("svg") || Resolve(manifestUri, src) is not { } url) continue;
                        iconCandidates.Add((url.AbsoluteUri, LargestSize(icon.TryGetProperty("sizes", out var z) ? z.GetString() : null)));
                    }
                }
            }
            catch
            {
                // No manifest after all (or unreadable): the page's own title and icons are used.
            }
        }

        foreach (var icon in hints?.Icons ?? [])
        {
            if (icon.Href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
            var size = LargestSize(icon.Sizes);
            if (size == 0) size = icon.Rel.Contains("apple") ? 180 : 32;
            iconCandidates.Add((icon.Href, size));
        }
        if (tab.FaviconUrl is { } favicon) iconCandidates.Add((favicon, 16));
        iconCandidates.Add((pageUri.GetLeftPart(UriPartial.Authority) + "/favicon.ico", 16));

        var id = MakeId(scope);
        Directory.CreateDirectory(IconsFolder);
        var iconPath = Path.Combine(IconsFolder, id + ".ico");
        var previewPath = Path.Combine(IconsFolder, id + ".new.ico"); // a reinstalled app keeps its icon until confirmed
        if (!await SaveIconAsync(http, iconCandidates.OrderByDescending(c => c.Size).Select(c => c.Url).Distinct(), previewPath))
            return (null, true);

        var chosen = (await confirm(name, previewPath))?.Trim();
        if (string.IsNullOrEmpty(chosen))
        {
            try { File.Delete(previewPath); } catch { }
            return (null, false);
        }
        name = chosen.Length > 60 ? chosen[..60] : chosen;
        File.Move(previewPath, iconPath, overwrite: true);

        var app = Find(id) ?? new WebApp { Id = id };
        app.Name = name;
        app.StartUrl = startUrl;
        app.Scope = scope;
        app.ThemeColor = theme;
        app.IconPath = iconPath;
        if (!Apps.Contains(app)) Apps.Add(app);
        Save();
        CreateShortcuts(app);
        return (app, false);
    }

    public void Uninstall(WebApp app)
    {
        foreach (var path in ShortcutPaths(app))
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
        try { if (File.Exists(app.IconPath)) File.Delete(app.IconPath); } catch { }
        Apps.Remove(app);
        Save();
    }

    public void Rename(WebApp app, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == app.Name) return;
        foreach (var path in ShortcutPaths(app)) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        app.Name = name;
        Save();
        CreateShortcuts(app);
    }

    private void Save() => JsonStore.Save(FileName, Apps.ToList());

    private static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BeautyBrowser.exe");

    public static string LaunchArguments(WebApp app) => AppSwitch + app.Id;

    private static void CreateShortcuts(WebApp app)
    {
        foreach (var path in ShortcutPaths(app))
        {
            try
            {
                ShellShortcuts.Create(path, Exe, LaunchArguments(app), app.IconPath,
                    L.T($"{app.Name} (BeautyBrowser app)", $"{app.Name} (app de BeautyBrowser)"), app.AppUserModelId);
            }
            catch
            {
                // A folder Windows will not let us write to: the other shortcut and the app list still work.
            }
        }
    }

    private static IEnumerable<string> ShortcutPaths(WebApp app)
    {
        var file = SafeFileName(app.Name) + ".lnk";
        yield return Path.Combine(StartMenuFolder, file);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), file);
    }

    private static string SafeFileName(string name)
    {
        var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c).ToArray()).Trim();
        return clean.Length == 0 ? "App" : clean.Length > 60 ? clean[..60] : clean;
    }

    /// <summary>"web.whatsapp.com/" → "web-whatsapp-com-1a2b3c": readable and unique per scope.</summary>
    private static string MakeId(string scope)
    {
        var host = Uri.TryCreate(scope, UriKind.Absolute, out var uri) ? uri.Host : scope;
        var slug = new string(host.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope.ToLowerInvariant())))[..6].ToLowerInvariant();
        return $"{slug}-{hash}";
    }

    /// <summary>"(3) WhatsApp" → "WhatsApp"; "Inbox - Gmail" stays; no title → the site's name.</summary>
    private static string CleanTitle(string? title, Uri page)
    {
        title = System.Text.RegularExpressions.Regex.Replace(title ?? "", @"^\(\d+\+?\)\s*", "").Trim();
        if (title.Length == 0 || title.StartsWith("http", StringComparison.OrdinalIgnoreCase)) title = HostName.WithoutWww(page.Host);
        return title.Length > 40 ? title[..40].Trim() : title;
    }

    private static Uri? Resolve(Uri baseUri, string? relative) =>
        string.IsNullOrWhiteSpace(relative) ? null : Uri.TryCreate(baseUri, relative, out var uri) && uri.Scheme is "https" or "http" ? uri : null;

    private static bool SameSite(Uri a, Uri b) => a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase);

    private static int LargestSize(string? sizes) =>
        (sizes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Equals("any", StringComparison.OrdinalIgnoreCase) ? 512 : int.TryParse(s.Split('x', 'X')[0], out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();

    /// <summary>"#25d366", "#fff" → "#25D366"; anything else (names, rgb(), transparent) → null.</summary>
    internal static string? ParseColor(string? color)
    {
        color = color?.Trim();
        if (color is null || !color.StartsWith('#')) return null;
        var hex = color[1..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? "#" + hex.ToUpperInvariant() : null;
    }

    /// <summary>Downloads the first icon that decodes and saves it as a .ico (PNG frames of 256, 48, 32 and 16 px).</summary>
    private static async Task<bool> SaveIconAsync(HttpClient http, IEnumerable<string> urls, string path)
    {
        foreach (var url in urls)
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(url);
                if (bytes.Length == 0 || bytes.Length > 4_000_000) continue;
                var frames = new List<byte[]>();
                foreach (var size in new uint[] { 256, 48, 32, 16 }) frames.Add(await ResizeToPngAsync(bytes, size));
                await File.WriteAllBytesAsync(path, BuildIco(frames, [256, 48, 32, 16]));
                return true;
            }
            catch
            {
                // Not an image Windows can read (SVG, broken): try the next one.
            }
        }
        return false;
    }

    private static async Task<byte[]> ResizeToPngAsync(byte[] image, uint size)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(image.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        var frame = await decoder.GetFrameAsync(decoder.FrameCount > 1 ? await LargestFrameAsync(decoder) : 0);
        var pixels = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = size, ScaledHeight = size, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, size, size, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();
        var result = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(result.AsBuffer(), (uint)result.Length, InputStreamOptions.None);
        return result;
    }

    // .ico files often hold several sizes: decode the biggest one.
    private static async Task<uint> LargestFrameAsync(BitmapDecoder decoder)
    {
        uint best = 0, bestWidth = 0;
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var width = (await decoder.GetFrameAsync(i)).PixelWidth;
            if (width > bestWidth) (best, bestWidth) = (i, width);
        }
        return best;
    }

    /// <summary>An .ico whose frames are PNGs (supported since Windows Vista).</summary>
    private static byte[] BuildIco(IReadOnlyList<byte[]> pngs, IReadOnlyList<int> sizes)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)pngs.Count);
        var offset = 6 + 16 * pngs.Count;
        for (var i = 0; i < pngs.Count; i++)
        {
            var s = sizes[i] >= 256 ? 0 : sizes[i];
            w.Write((byte)s); w.Write((byte)s); w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) w.Write(png);
        return ms.ToArray();
    }
}
