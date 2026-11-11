using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace BeautyBrowser.Services;

public enum UpdateState
{
    /// <summary>Nothing checked yet.</summary>
    Idle,
    Checking,
    Downloading,
    UpToDate,
    /// <summary>A newer version is downloaded and verified: it installs on close.</summary>
    Ready,
    /// <summary>The last check failed (offline, GitHub unavailable…).</summary>
    Failed,
    /// <summary>Run from the source code, not installed: it does not update itself.</summary>
    NotInstalled,
}

/// <summary>
/// Updates, the way Chrome does: every few hours it looks at the latest GitHub release; when there is a newer version
/// it downloads the installer in the background, checks its SHA-256 and announces it (<see cref="UpdateReady"/>). The
/// installer runs, without a window, when the browser closes (<see cref="ApplyOnExit"/>).
/// </summary>
public sealed class UpdateService
{
    private const string Repository = "fabriciovla/BeautyBrowser";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";
    private const string SetupAsset = "BeautyBrowserSetup.exe";
    private const string ChecksumAsset = SetupAsset + ".sha256";

    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30); // not while the browser is starting up
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    private static readonly string UpdatesFolder = Path.Combine(JsonStore.Folder, "Updates");

    private static readonly string InstallFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BeautyBrowser");

    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _timer;
    private bool _checking;
    private string? _installer;     // downloaded and verified
    private string? _installerHash; // checked again right before running it

    public UpdateService(DispatcherQueue ui)
    {
        _ui = ui;
        _timer = ui.CreateTimer();
        _timer.Interval = FirstCheck;
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckEvery;
            await CheckAsync();
        };
    }

    /// <summary>This version, "0.1.2" (version.props).</summary>
    public static string CurrentVersion { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    /// <summary>A newer version was downloaded and verified; it installs when the browser closes. The version, "0.1.3".</summary>
    public event EventHandler<string>? UpdateReady;

    public string? ReadyVersion { get; private set; }

    public UpdateState State { get; private set; } = IsInstalledCopy ? UpdateState.Idle : UpdateState.NotInstalled;

    /// <summary>Raised on the UI thread whenever <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    private void SetState(UpdateState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Check for updates" in settings: checks right away instead of waiting for the next round.</summary>
    public Task CheckNowAsync() => IsInstalledCopy ? CheckAsync() : Task.CompletedTask;

    /// <summary>
    /// Only the installed copy updates itself. One run from the source code (bin\Debug, bin\Release) would otherwise be
    /// replaced by the installed one's update.
    /// </summary>
    public static bool IsInstalledCopy =>
        Environment.ProcessPath is { } exe &&
        exe.StartsWith(InstallFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public void Start()
    {
        if (!IsInstalledCopy) return;
        CleanUpInstalled();
        _timer.Start();
    }

    private async Task CheckAsync()
    {
        if (_checking || _installer is not null) return;
        _checking = true;
        SetState(UpdateState.Checking);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BeautyBrowser-Updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var release = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseApi));
            var root = release.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!IsNewer(tag, CurrentVersion))
            {
                SetState(UpdateState.UpToDate);
                return;
            }
            var version = tag.TrimStart('v', 'V');

            string? setupUrl = null, checksumUrl = null, digest = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();
                if (url is null || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue; // only from this repository
                if (name == SetupAsset)
                {
                    setupUrl = url;
                    if (asset.TryGetProperty("digest", out var d) && d.GetString() is { } value &&
                        value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        digest = value[7..];
                }
                else if (name == ChecksumAsset)
                {
                    checksumUrl = url;
                }
            }
            if (setupUrl is null)
            {
                SetState(UpdateState.UpToDate); // a release without an installer yet
                return;
            }

            // The expected hash: the one GitHub computed for the file, or the .sha256 published next to it. Without one
            // nothing is installed.
            if (digest is null && checksumUrl is not null)
                digest = (await http.GetStringAsync(checksumUrl)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (digest is not { Length: 64 })
            {
                SetState(UpdateState.Failed);
                return;
            }
            digest = digest.ToLowerInvariant();

            Directory.CreateDirectory(UpdatesFolder);
            var target = Path.Combine(UpdatesFolder, $"BeautyBrowserSetup-{version}.exe");
            if (!File.Exists(target) || await HashOfAsync(target) != digest)
            {
                SetState(UpdateState.Downloading);
                var partial = target + ".download";
                using (var response = await http.GetAsync(setupUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync();
                    await using var output = File.Create(partial);
                    await input.CopyToAsync(output);
                }
                if (await HashOfAsync(partial) != digest)
                {
                    File.Delete(partial);
                    SetState(UpdateState.Failed);
                    return; // corrupted or tampered with: tried again at the next check
                }
                File.Move(partial, target, overwrite: true);
            }

            CleanUp(keep: target);
            _installer = target;
            _installerHash = digest;
            ReadyVersion = version;
            SetState(UpdateState.Ready);
            UpdateReady?.Invoke(this, version);
        }
        catch
        {
            // Offline, GitHub unavailable, rate limited…: tried again at the next check.
            SetState(UpdateState.Failed);
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>
    /// Called while the browser closes, once Chromium released the profile: runs the downloaded installer without a
    /// window. With <paramref name="relaunch"/> ("Restart now") the installer opens the browser again. False when there
    /// is no update to apply.
    /// </summary>
    public bool ApplyOnExit(bool relaunch)
    {
        if (_installer is not { } installer || _installerHash is not { } hash) return false;
        try
        {
            if (!File.Exists(installer) || HashOf(installer) != hash) return false;
            Process.Start(new ProcessStartInfo(installer, relaunch ? "--silent --relaunch" : "--silent")
            {
                UseShellExecute = false,
                WorkingDirectory = UpdatesFolder,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>"v0.1.3" is newer than "0.1.2".</summary>
    private static bool IsNewer(string tag, string current) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out var latest) &&
        Version.TryParse(current, out var running) &&
        latest > running;

    private static async Task<string> HashOfAsync(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file));
    }

    private static string HashOf(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    /// <summary>
    /// At startup: deletes unfinished downloads and installers of this version or older (already installed). A newer
    /// one downloaded last time stays, so it is not downloaded again.
    /// </summary>
    private static void CleanUpInstalled()
    {
        try
        {
            if (!Directory.Exists(UpdatesFolder)) return;
            foreach (var file in Directory.EnumerateFiles(UpdatesFolder))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var pending = Path.GetExtension(file) == ".exe" && name.StartsWith("BeautyBrowserSetup-", StringComparison.Ordinal) &&
                              IsNewer(name["BeautyBrowserSetup-".Length..], CurrentVersion);
                if (pending) continue;
                try { File.Delete(file); } catch { /* in use: next time */ }
            }
        }
        catch
        {
            // nothing to do
        }
    }

    /// <summary>Deletes older downloads (and unfinished ones), keeping <paramref name="keep"/>.</summary>
    private static void CleanUp(string? keep)
    {
        try
        {
            if (!Directory.Exists(UpdatesFolder)) return;
            foreach (var file in Directory.EnumerateFiles(UpdatesFolder))
            {
                if (keep is not null && string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); } catch { /* in use: next time */ }
            }
        }
        catch
        {
            // nothing to do
        }
    }
}
