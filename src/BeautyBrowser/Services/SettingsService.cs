using System;
using System.Collections.Generic;
using System.Globalization;
using BeautyKit;
using CefSharp;

namespace BeautyBrowser.Services;

public record SearchEngineOption(string Name, string UrlTemplate);

public record PageLanguageOption(string Name, string Languages);

public enum StartupMode { HomePage, RestoreTabs }

/// <summary>
/// Browser settings, stored in SQLite. Every change applies to the tabs right away (through BrowserTab's options),
/// except hardware acceleration, which needs a restart.
/// </summary>
public sealed class SettingsService
{
    private readonly DatabaseService _db;

    public static readonly IReadOnlyList<SearchEngineOption> AvailableSearchEngines =
    [
        new("Google", "https://www.google.com/search?q={0}"),
        new("DuckDuckGo", "https://duckduckgo.com/?q={0}"),
        new("Bing", "https://www.bing.com/search?q={0}"),
        new("Ecosia", "https://www.ecosia.org/search?q={0}"),
    ];

    public static readonly IReadOnlyList<PageLanguageOption> AvailablePageLanguages =
    [
        new("Español (Latinoamérica)", "es-419,es,en"),
        new("Español (España)", "es-ES,es,en"),
        new("English", "en-US,en"),
        new("Português", "pt-BR,pt,en"),
    ];

    public static readonly IReadOnlyList<int> ZoomOptions = [80, 90, 100, 110, 125, 150];

    /// <summary>Page font size: the default and the minimum (the minimum is what nearly every site notices).</summary>
    public static readonly IReadOnlyList<(string Name, int Size, int Minimum)> FontSizeOptions =
    [
        (L.T("Small", "Chica"), 14, 0), (L.T("Normal", "Normal"), 16, 0), (L.T("Large", "Grande"), 18, 14), (L.T("Huge", "Enorme"), 22, 18),
    ];

    public const string DefaultHomePage = "https://www.google.com";

    public event EventHandler? SettingsChanged;

    public SettingsService(DatabaseService db)
    {
        _db = db;
        Apply();
        ApplyToEngine();
    }


    public StartupMode Startup
    {
        get => Enum.TryParse<StartupMode>(_db.GetSetting("startup_mode", nameof(StartupMode.RestoreTabs)), out var mode)
            ? mode : StartupMode.RestoreTabs;
        set => Save("startup_mode", value.ToString());
    }


    public string SearchEngine
    {
        get => _db.GetSetting("search_engine", "Google");
        set => Save("search_engine", value);
    }

    public string HomePage
    {
        get => _db.GetSetting("home_url", DefaultHomePage);
        set => Save("home_url", value);
    }


    public int DefaultZoom
    {
        get => GetInt("default_zoom", 100);
        set => Save("default_zoom", value.ToString(CultureInfo.InvariantCulture));
    }

    public int FontSize
    {
        get => GetInt("font_size", 16);
        set
        {
            Save("font_size", value.ToString(CultureInfo.InvariantCulture));
            ApplyToEngine();
        }
    }

    /// <summary>The browser color (see <see cref="AppTheme"/>): it tints the glass and is the accent.</summary>
    public string ThemeKey
    {
        get => AppTheme.Find(_db.GetSetting("theme", "classic")).Key;
        set => Save("theme", value);
    }

    /// <summary>Whether onboarding (the first-run setup inside the browser) is done.</summary>
    public bool OnboardingDone
    {
        get => GetBool("onboarding_done", false);
        set => Save("onboarding_done", value);
    }

    // Glass texture of the sidebar, the library and the window edges.

    /// <summary>Noise textures: the stored key and the shown name.</summary>
    public static readonly IReadOnlyList<(string Key, string Name)> GlassTextures =
    [
        ("grain", L.T("Grain", "Grano")), ("sand", L.T("Sand", "Arena")), ("fine", L.T("Fine", "Fino")), ("paper", L.T("Paper", "Papel")), ("tweed", "Tweed"), ("denim", "Denim"),
    ];

    public const string DefaultGlassTexture = "grain";

    public string GlassTexture
    {
        get
        {
            var key = _db.GetSetting("glass_texture", DefaultGlassTexture);
            foreach (var texture in GlassTextures)
                if (texture.Key == key) return key;
            return DefaultGlassTexture;
        }
        set => Save("glass_texture", value);
    }

    /// <summary>Grain amount from 0 to 100%. Starts from the old setting (None/Soft/Medium/Strong) when it exists.</summary>
    public int GlassGrain
    {
        get => Math.Clamp(GetInt("glass_grain", LegacyGlassGrain()), 0, 100);
        set => Save("glass_grain", Math.Clamp(value, 0, 100).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Grain softness: 0 is sharp, 100 very blurred.</summary>
    public int GlassSoftness
    {
        get => Math.Clamp(GetInt("glass_softness", 0), 0, 100);
        set => Save("glass_softness", Math.Clamp(value, 0, 100).ToString(CultureInfo.InvariantCulture));
    }

    private int LegacyGlassGrain() => GetInt("glass_noise", 0) switch { 1 => 20, 2 => 36, 3 => 60, _ => 0 };

    /// <summary>"Reset" on the texture card: grain, no noise, sharp.</summary>
    public void ResetGlass()
    {
        _db.SetSetting("glass_texture", DefaultGlassTexture);
        _db.SetSetting("glass_grain", "0");
        _db.SetSetting("glass_softness", "0");
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }


    /// <summary>Pinned sidebar (the page shifts over). Remembered between sessions.</summary>
    public bool SidebarDocked
    {
        get => GetBool("sidebar_docked", false);
        set => Save("sidebar_docked", value);
    }

    /// <summary>Seconds with the mouse still before the floating sidebar hides; 0 means never.</summary>
    public static readonly IReadOnlyList<(string Name, int Seconds)> SidebarIdleOptions =
    [
        ("2 s", 2), ("3 s", 3), ("5 s", 5), (L.T("Never", "Nunca"), 0),
    ];

    public int SidebarIdleSeconds
    {
        get => GetInt("sidebar_idle_seconds", 3);
        set => Save("sidebar_idle_seconds", value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Preview when hovering a tab.</summary>
    public bool TabPreviews
    {
        get => GetBool("tab_previews", true);
        set => Save("tab_previews", value);
    }

    /// <summary>The blue line at the top while a page loads.</summary>
    public bool ShowLoadBar
    {
        get => GetBool("show_load_bar", true);
        set => Save("show_load_bar", value);
    }


    public string DownloadsFolder
    {
        get => _db.GetSetting("downloads_folder", DefaultDownloadsFolder);
        set => Save("downloads_folder", value);
    }

    public static string DefaultDownloadsFolder { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public bool AskWhereToSave
    {
        get => GetBool("ask_where_to_save", false);
        set => Save("ask_where_to_save", value);
    }


    public bool BlockThirdPartyCookies
    {
        get => GetBool("block_third_party_cookies", false);
        set => Save("block_third_party_cookies", value);
    }

    public bool DoNotTrack
    {
        get => GetBool("do_not_track", false);
        set => Save("do_not_track", value);
    }

    public bool BlockPopups
    {
        get => GetBool("block_popups", true);
        set => Save("block_popups", value);
    }


    /// <summary>Position and size of the window when it was last closed (not maximized); null the first time.</summary>
    public Windows.Graphics.RectInt32? WindowBounds
    {
        get => GetInt("window_w", 0) > 0
            ? new Windows.Graphics.RectInt32(GetInt("window_x", 0), GetInt("window_y", 0), GetInt("window_w", 0), GetInt("window_h", 0))
            : null;
        set
        {
            if (value is not { } r) return;
            Save("window_x", r.X.ToString(CultureInfo.InvariantCulture));
            Save("window_y", r.Y.ToString(CultureInfo.InvariantCulture));
            Save("window_w", r.Width.ToString(CultureInfo.InvariantCulture));
            Save("window_h", r.Height.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The window was maximized when it was last closed.</summary>
    public bool WindowMaximized
    {
        get => GetBool("window_maximized", false);
        set => Save("window_maximized", value);
    }

    /// <summary>Interface language (system, Español or English). Applies after restarting BeautyBrowser.</summary>
    public LanguageChoice Language
    {
        get => L.SavedChoice;
        set => L.SaveChoice(value);
    }

    public string PageLanguages
    {
        get => _db.GetSetting("page_languages", AvailablePageLanguages[L.Spanish ? 0 : 2].Languages);
        set
        {
            Save("page_languages", value);
            BrowserEngine.SetPageLanguages(value);
        }
    }


    /// <summary>Render pages on the GPU. Applies after restarting BeautyBrowser.</summary>
    public bool HardwareAcceleration
    {
        get => GetBool("hardware_acceleration", true);
        set => Save("hardware_acceleration", value);
    }


    /// <summary>Resets every setting to its default; history and favorites stay.</summary>
    public void ResetToDefaults()
    {
        _db.ClearSettings();
        Apply();
        ApplyToEngine();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }


    public string GetCurrentSearchTemplate()
    {
        var current = SearchEngine;
        foreach (var opt in AvailableSearchEngines)
        {
            if (string.Equals(opt.Name, current, StringComparison.OrdinalIgnoreCase))
                return opt.UrlTemplate;
        }
        return AvailableSearchEngines[0].UrlTemplate;
    }

    public void ClearBrowsingData(bool clearHistory, bool clearCookies, bool clearCache, HistoryService historyService)
    {
        if (clearHistory) historyService.ClearAll();

        if (clearCookies)
        {
            try
            {
                Cef.GetGlobalCookieManager()?.DeleteCookies("", "");
            }
            catch
            {
                // Chromium is not ready yet, so there are no cookies to delete.
            }
        }
    }


    /// <summary>Pushes the settings to the tabs (BeautyKit).</summary>
    private void Apply()
    {
        BrowserTab.SearchUrlTemplate = GetCurrentSearchTemplate();
        BrowserTab.DefaultHomePage = HomePage;
        BrowserTab.DefaultZoomPercent = DefaultZoom;
        BrowserTab.DownloadsDirectory = DownloadsFolder;
        BrowserTab.AskWhereToSave = AskWhereToSave;
        BrowserTab.BlockThirdPartyCookies = BlockThirdPartyCookies;
        BrowserTab.SendDoNotTrack = DoNotTrack;
        BrowserTab.BlockPopups = BlockPopups;
    }

    /// <summary>What Chromium stores as a global preference, live. Does nothing before Chromium starts.</summary>
    private void ApplyToEngine()
    {
        var size = FontSize;
        var minimum = 0;
        foreach (var option in FontSizeOptions)
            if (option.Size == size) minimum = option.Minimum;
        BrowserEngine.SetFontSizes(size, minimum);
        BrowserEngine.SetPageLanguages(PageLanguages);
    }

    private void Save(string key, string value)
    {
        _db.SetSetting(key, value);
        Apply();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Save(string key, bool value) => Save(key, value ? "1" : "0");

    private bool GetBool(string key, bool fallback) => _db.GetSetting(key, fallback ? "1" : "0") == "1";

    private int GetInt(string key, int fallback) =>
        int.TryParse(_db.GetSetting(key, fallback.ToString(CultureInfo.InvariantCulture)),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
