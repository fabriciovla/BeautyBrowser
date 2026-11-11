using System.Collections.ObjectModel;
using System.ComponentModel;
using BeautyBrowser.Services;
using BeautyKit;
using BeautyKit.DevTools;
using Microsoft.UI.Dispatching;

namespace BeautyBrowser.Shell;

/// <summary>
/// The browser's core: spaces, tabs, favorites, history and downloads. The UI (window, sidebar, palette) only reads
/// this state and calls its methods.
/// </summary>
public sealed class BrowserShell
{
    private readonly BrowserEngine _engine;

    public BrowserShell(BrowserEngine engine, DispatcherQueue ui, DatabaseService? database = null)
    {
        _engine = engine;
        Database = database ?? new DatabaseService();
        History = new HistoryService(ui, Database);
        Settings = new SettingsService(Database);
        Updates = new UpdateService(ui);

        // Sites where popups were allowed for good are read by Chromium's thread from here on.
        foreach (var origin in SitePermissions.OriginsAllowing(PagePermission.Popups)) BrowserTab.AllowPopupsFrom(origin);

        // The start page's most visited sites, always up to date.
        StartPage.Sites = History.TopSites(8);
        try
        {
            var logo = Path.Combine(AppContext.BaseDirectory, "BeautyBrowser.png");
            if (File.Exists(logo)) StartPage.LogoDataUri = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(logo));
        }
        catch
        {
            // without a logo the start page shows its sphere
        }
        History.HistoryChanged += (_, _) => StartPage.Sites = History.TopSites(8);

        CurrentSpace = new Space("Space");
        Spaces.Add(CurrentSpace);

        // Any tab or space change saves the session, debounced.
        _sessionTimer = ui.CreateTimer();
        _sessionTimer.Interval = TimeSpan.FromSeconds(1.5);
        _sessionTimer.IsRepeating = false;
        _sessionTimer.Tick += (_, _) => SaveSession();
        TrackSpace(CurrentSpace);
        Spaces.CollectionChanged += (_, e) =>
        {
            foreach (var space in e.NewItems?.OfType<Space>() ?? []) TrackSpace(space);
            ScheduleSessionSave();
        };
        CurrentTabChanged += (_, _) => ScheduleSessionSave();
        CurrentSpaceChanged += (_, _) => ScheduleSessionSave();
        SpaceRenamed += (_, _) => ScheduleSessionSave();
    }

    public ObservableCollection<Space> Spaces { get; } = [];
    public Space CurrentSpace { get; private set; }
    public BrowserTab? CurrentTab => CurrentSpace.ActiveTab;

    public DatabaseService Database { get; }
    public HistoryService History { get; }
    public SettingsService Settings { get; }
    public FavoritesService Favorites { get; } = new();
    public DownloadsService Downloads { get; } = new();
    public SitePermissionsService SitePermissions { get; } = new();

    /// <summary>A page asks for the microphone, camera, location… Without a listener the request is denied.</summary>
    public event EventHandler<(BrowserTab Tab, PermissionRequest Request)>? PermissionRequested;

    /// <summary>A tab blocked a popup the page opened without a click.</summary>
    public event EventHandler<(BrowserTab Tab, string Origin, string Url)>? PopupBlocked;

    /// <summary>A dangerous file is about to download; without a listener it downloads.</summary>
    public event EventHandler<(BrowserTab Tab, DownloadApproval Approval)>? DownloadApprovalRequested;

    /// <summary>New versions from GitHub Releases (see UpdateService).</summary>
    public UpdateService Updates { get; }

    /// <summary>The visible tab changed (selection, close or space switch).</summary>
    public event EventHandler? CurrentTabChanged;

    public event EventHandler? CurrentSpaceChanged;

    public event EventHandler<bool>? FullscreenChanged;

    /// <summary>A download started, for the animation toward the library.</summary>
    public event EventHandler<DownloadEntry>? DownloadStarted;

    public event EventHandler? DevToolsChanged;


    /// <summary>The tab shown on the media card: the one playing, or the last that played.</summary>
    public BrowserTab? NowPlaying { get; private set; }

    public event EventHandler? NowPlayingChanged;

    private void RefreshNowPlaying()
    {
        var best = Spaces.SelectMany(s => s.Tabs).Concat(_favoriteTabs.Values)
            .Where(t => t.Media is not null)
            .OrderByDescending(t => t.Media!.IsPlaying)
            .ThenByDescending(t => t.MediaStartedAt)
            .FirstOrDefault();
        if (best == NowPlaying) return;
        NowPlaying = best;
        NowPlayingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the tab (from the list or a favorite) where the media is playing.</summary>
    public void ShowTab(BrowserTab tab)
    {
        if (Spaces.Any(s => s.Tabs.Contains(tab))) SelectTab(tab);
        else if (IsFavoriteTab(tab)) Show(tab);
    }


    private readonly Dictionary<BrowserTab, DevToolsSession> _devTools = [];

    public DevToolsSession? CurrentDevTools =>
        CurrentTab is { } tab && _devTools.TryGetValue(tab, out var session) ? session : null;

    /// <summary>Opens DevTools for the tab, or reveals the element at (x, y) when they are already open.</summary>
    public void OpenDevTools(BrowserTab tab, int x = 0, int y = 0)
    {
        if (_devTools.TryGetValue(tab, out var existing))
        {
            existing.InspectAt(x, y);
            return;
        }

        var session = _engine.OpenDevTools(tab, x, y);
        session.Frontend.NewTabRequested += (_, url) => NewTab(url); // links inside DevTools
        _devTools[tab] = session;
        DevToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CloseDevTools(BrowserTab tab)
    {
        if (!_devTools.Remove(tab, out var session)) return;
        DevToolsChanged?.Invoke(this, EventArgs.Empty);
        session.Dispose();
    }

    public void ToggleDevTools()
    {
        if (CurrentTab is not { } tab) return;
        if (_devTools.ContainsKey(tab)) CloseDevTools(tab);
        else OpenDevTools(tab);
    }


    public BrowserTab NewTab(string? url = null, bool activate = true)
    {
        var tab = _engine.CreateTab(url);
        Wire(tab);

        // New tabs go to the top of the list, as in Arc.
        CurrentSpace.Tabs.Insert(0, tab);
        if (activate || CurrentSpace.ActiveTab is null) SelectTab(tab);
        return tab;
    }

    /// <summary>The start page (beauty://newtab). Opens when no tab is left, instead of loading a search engine.</summary>
    public BrowserTab OpenStartPage() => NewTab(StartPage.Url);

    public void SelectTab(BrowserTab tab)
    {
        var space = Spaces.FirstOrDefault(s => s.Tabs.Contains(tab));
        if (space is null) return;

        var previous = CurrentTab;
        if (space != CurrentSpace)
        {
            CurrentSpace = space;
            CurrentSpaceChanged?.Invoke(this, EventArgs.Empty);
        }

        if (previous == tab && space.ActiveTab == tab) return;
        if (previous is { IsFullscreen: true }) previous.ExitFullscreen();
        space.ActiveTab = tab;
        CurrentTabChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The next (+1) or previous (-1) tab of the current space, wrapping around (Ctrl+Tab).</summary>
    public void SelectRelativeTab(int delta)
    {
        var tabs = CurrentSpace.Tabs;
        if (tabs.Count < 2) return;
        var index = CurrentTab is { } tab ? tabs.IndexOf(tab) : 0;
        SelectTab(tabs[((index + delta) % tabs.Count + tabs.Count) % tabs.Count]);
    }

    /// <summary>Ctrl+1…8: tab N of the current space. Ctrl+9: the last one, as in Chrome.</summary>
    public void SelectTabNumber(int number)
    {
        var tabs = CurrentSpace.Tabs;
        if (tabs.Count == 0) return;
        SelectTab(number >= 9 ? tabs[^1] : tabs[Math.Min(number, tabs.Count) - 1]);
    }


    private const int MaxClosedTabs = 25;
    private readonly List<(string Url, Space Space, (List<string> Urls, int Index) History)> _closedTabs = [];

    private void RememberClosed(string url, Space space, (List<string> Urls, int Index) history)
    {
        if (string.IsNullOrEmpty(url)) return;
        _closedTabs.Add((url, space, history));
        if (_closedTabs.Count > MaxClosedTabs) _closedTabs.RemoveAt(0);
    }

    /// <summary>Reopens the last closed tab in its space, if the space still exists (Ctrl+Shift+T).</summary>
    public void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0) return;
        var (url, space, history) = _closedTabs[^1];
        _closedTabs.RemoveAt(_closedTabs.Count - 1);

        if (Spaces.Contains(space) && space != CurrentSpace)
        {
            CurrentSpace = space;
            CurrentSpaceChanged?.Invoke(this, EventArgs.Empty);
        }
        // When the only thing left is the start page (the last tab was closed), that same tab is reused.
        if (CurrentTab is { } current && StartPage.Is(current.Url) && CurrentSpace.Tabs.Contains(current))
        {
            current.Navigate(url);
            CurrentTabChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            NewTab(url).SetRestoredHistory(history.Urls, history.Index); // back/forward come back too, as in Chrome
        }
    }

    public void CloseTab(BrowserTab tab)
    {
        if (IsFavoriteTab(tab))
        {
            CloseFavoriteTab(tab);
            return;
        }

        var space = Spaces.FirstOrDefault(s => s.Tabs.Contains(tab));
        if (space is null) return;

        var index = space.Tabs.IndexOf(tab);
        var wasActive = space.ActiveTab == tab;
        if (!StartPage.Is(tab.Url)) RememberClosed(tab.Url, space, tab.GetHistory());
        space.Tabs.RemoveAt(index);
        if (wasActive) space.ActiveTab = null;

        if (space.Tabs.Count == 0)
        {
            if (space == CurrentSpace) OpenStartPage();
        }
        else if (wasActive)
        {
            var next = space.Tabs[Math.Min(index, space.Tabs.Count - 1)];
            if (space == CurrentSpace) SelectTab(next);
            else space.ActiveTab = next;
        }

        if (wasActive && space == CurrentSpace) CurrentTabChanged?.Invoke(this, EventArgs.Empty);
        CloseDevTools(tab);
        tab.Dispose();
        RefreshNowPlaying();
    }

    public void DuplicateTab(BrowserTab tab) => NewTab(tab.Url);

    /// <summary>"Clear": closes every tab in the space except the one in view.</summary>
    public void ClearSpace()
    {
        foreach (var tab in CurrentSpace.Tabs.Where(t => t != CurrentTab).ToList())
            CloseTab(tab);
    }

    /// <summary>Navigates the current tab, or opens a new one.</summary>
    public void Open(string input, bool inNewTab)
    {
        if (inNewTab || CurrentTab is null) NewTab(BrowserTab.InputToUrl(input));
        else CurrentTab.Navigate(input);
    }


    public Space AddSpace(string? name = null, string? icon = null)
    {
        name = name?.Trim();
        var space = new Space(string.IsNullOrEmpty(name) ? $"Space {Spaces.Count + 1}" : name,
            icon ?? SpaceIcons.FirstUnused(Spaces));
        Spaces.Add(space);
        SwitchSpace(space);
        return space;
    }

    public void SwitchSpace(Space space)
    {
        if (space == CurrentSpace) return;
        if (CurrentTab is { IsFullscreen: true } current) current.ExitFullscreen();

        CurrentSpace = space;
        CurrentSpaceChanged?.Invoke(this, EventArgs.Empty);

        if (space.Tabs.Count == 0) OpenStartPage();
        else CurrentTabChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A space's name or icon changed; the space list raises additions and removals itself.</summary>
    public event EventHandler? SpaceRenamed;

    public void SetSpaceIcon(Space space, string icon)
    {
        if (!SpaceIcons.IsValid(icon) || icon == space.Icon) return;
        space.Icon = icon;
        SpaceRenamed?.Invoke(this, EventArgs.Empty);
        if (space == CurrentSpace) CurrentSpaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RenameSpace(Space space, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == space.Name) return;
        space.Name = name;
        SpaceRenamed?.Invoke(this, EventArgs.Empty);
        if (space == CurrentSpace) CurrentSpaceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes a space and closes its tabs. At least one space always remains.</summary>
    public void RemoveSpace(Space space)
    {
        if (Spaces.Count <= 1 || !Spaces.Contains(space)) return;
        if (space == CurrentSpace) SwitchSpace(Spaces.First(s => s != space));

        foreach (var tab in space.Tabs.ToList())
        {
            CloseDevTools(tab);
            tab.Dispose();
        }
        space.Tabs.Clear();
        space.ActiveTab = null;
        Spaces.Remove(space);
        RefreshNowPlaying();
    }

    /// <summary>The default zoom changed: apply it to every open tab.</summary>
    public void ApplyDefaultZoom()
    {
        foreach (var tab in Spaces.SelectMany(s => s.Tabs).Concat(_favoriteTabs.Values)) tab.ApplyDefaultZoom();
    }

    // As in Arc, every favorite has its own tab living in the tile, not in the tab list. It is created the first time
    // the favorite is opened and then stays where it was left.
    private readonly Dictionary<FavoriteSite, BrowserTab> _favoriteTabs = [];


    /// <summary>A link asks to open in Peek: (the tab it came from, the address).</summary>
    public event EventHandler<(BrowserTab From, string Url)>? PeekRequested;

    /// <summary>The Peek tab. It belongs to no space until it is expanded.</summary>
    public BrowserTab CreatePeekTab(string url)
    {
        var tab = _engine.CreateTab(url);
        Wire(tab);
        return tab;
    }

    /// <summary>"Open as tab": the Peek tab moves to the space's list as it is.</summary>
    public void AdoptTab(BrowserTab tab)
    {
        if (Spaces.Any(s => s.Tabs.Contains(tab))) return;
        CurrentSpace.Tabs.Insert(0, tab);
        SelectTab(tab);
    }

    public void DisposePeekTab(BrowserTab tab)
    {
        if (Spaces.Any(s => s.Tabs.Contains(tab))) return; // already a regular tab
        CloseDevTools(tab);
        tab.Dispose();
    }

    /// <summary>Shows any tab, from a space's list or a favorite.</summary>
    public void ActivateTab(BrowserTab tab)
    {
        if (IsFavoriteTab(tab)) Show(tab);
        else SelectTab(tab);
    }

    /// <summary>The open favorite tabs, in grid order.</summary>
    public IEnumerable<BrowserTab> OpenFavoriteTabs() =>
        Favorites.Items.Select(FavoriteTabOf).OfType<BrowserTab>();

    public BrowserTab? FavoriteTabOf(FavoriteSite site) => _favoriteTabs.GetValueOrDefault(site);

    public bool IsFavoriteTab(BrowserTab tab) => _favoriteTabs.ContainsValue(tab);

    /// <summary>Shows the favorite in its own tab without adding anything to the list.</summary>
    public void OpenFavorite(FavoriteSite site)
    {
        if (!_favoriteTabs.TryGetValue(site, out var tab))
        {
            tab = _engine.CreateTab(site.Url);
            Wire(tab);
            _favoriteTabs[site] = tab;
        }
        Show(tab);
    }

    /// <summary>"Add to favorites": the tab moves from the list to the tile as it is.</summary>
    public void Pin(BrowserTab tab)
    {
        if (IsFavoriteTab(tab) || Favorites.Add(tab.Url, tab.Title, tab.FaviconUrl) is not { } site) return;
        _favoriteTabs[site] = tab;
        var space = Spaces.FirstOrDefault(s => s.Tabs.Contains(tab));
        space?.Tabs.Remove(tab); // still the active tab; it just leaves the list
        if (tab == CurrentTab) CurrentTabChanged?.Invoke(this, EventArgs.Empty); // highlight its tile
    }

    /// <summary>Ctrl+D: the tab becomes a favorite or, if it is one, stops being one.</summary>
    public void ToggleFavorite(BrowserTab tab)
    {
        var site = _favoriteTabs.FirstOrDefault(p => p.Value == tab).Key;
        if (site is not null) Unpin(site.Url);
        else if (!StartPage.Is(tab.Url)) Pin(tab);
    }

    /// <summary>"Remove from favorites": an open tab goes back to the list.</summary>
    public void Unpin(string url)
    {
        foreach (var site in Favorites.Items.Where(f => FavoritesService.SameSite(f.Url, url)).ToList())
        {
            if (_favoriteTabs.Remove(site, out var tab))
            {
                var space = Spaces.FirstOrDefault(s => s.ActiveTab == tab) ?? CurrentSpace;
                space.Tabs.Insert(0, tab);
                if (space == CurrentSpace && CurrentTab == tab) CurrentTabChanged?.Invoke(this, EventArgs.Empty);
            }
            Favorites.Items.Remove(site);
        }
    }

    /// <summary>Shows a tab that is not in the list (a favorite's).</summary>
    private void Show(BrowserTab tab)
    {
        if (CurrentTab == tab) return;
        if (CurrentTab is { IsFullscreen: true } previous) previous.ExitFullscreen();
        CurrentSpace.ActiveTab = tab;
        CurrentTabChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ctrl+W on a favorite closes its tab (the tile stays) and returns to the list.</summary>
    private void CloseFavoriteTab(BrowserTab tab)
    {
        var site = _favoriteTabs.First(p => p.Value == tab).Key;
        _favoriteTabs.Remove(site);
        foreach (var space in Spaces.Where(s => s.ActiveTab == tab))
            space.ActiveTab = space.Tabs.FirstOrDefault();

        if (CurrentSpace.ActiveTab is null) OpenStartPage();
        else CurrentTabChanged?.Invoke(this, EventArgs.Empty);
        CloseDevTools(tab);
        tab.Dispose();
        RefreshNowPlaying();
    }


    public void Shutdown()
    {
        SaveSession(); // before closing the tabs, so "continue where you left off" works
        foreach (var session in _devTools.Values.ToList()) session.Dispose();
        _devTools.Clear();
        foreach (var tab in Spaces.SelectMany(s => s.Tabs).Concat(_favoriteTabs.Values).ToList()) tab.Dispose();
        _favoriteTabs.Clear();
        History.Flush();
        Downloads.Save();
    }


    private const string SessionFile = "session.json";
    private readonly DispatcherQueueTimer _sessionTimer;
    private bool _restoring;

    public sealed class SessionData
    {
        public int CurrentSpace { get; set; }
        public List<SpaceData> Spaces { get; set; } = [];
    }

    public sealed class SpaceData
    {
        public string Name { get; set; } = "";
        public string? Icon { get; set; }
        public List<string> Tabs { get; set; } = [];
        /// <summary>Each tab's back/forward list, in the same order as Tabs (missing in older sessions).</summary>
        public List<TabHistoryData>? History { get; set; }
        public int Active { get; set; }
    }

    public sealed class TabHistoryData
    {
        public List<string> Urls { get; set; } = [];
        public int Index { get; set; }
    }

    /// <summary>Reopens last session's spaces and tabs. Returns false when nothing was saved.</summary>
    public bool TryRestoreSession()
    {
        var data = JsonStore.Load<SessionData>(SessionFile);
        if (data.Spaces.Count == 0 || data.Spaces.All(sp => sp.Tabs.Count == 0)) return false;

        _restoring = true;
        Spaces.Clear();
        foreach (var saved in data.Spaces)
        {
            var space = new Space(string.IsNullOrWhiteSpace(saved.Name) ? $"Space {Spaces.Count + 1}" : saved.Name, saved.Icon);
            // Tabs load the first time they are shown, as in Chrome: until then they show the title and icon history
            // remembers, without starting a page (or its audio) for each one.
            for (var i = 0; i < saved.Tabs.Count; i++)
            {
                var url = saved.Tabs[i];
                if (string.IsNullOrWhiteSpace(url)) continue;
                var tab = _engine.CreateTab(url, TabCreation.WhenShown);
                if (saved.History?.ElementAtOrDefault(i) is { } history) tab.SetRestoredHistory(history.Urls, history.Index);
                if (History.Lookup(url) is { } entry) tab.SetPlaceholder(entry.Title, entry.FaviconUrl);
                Wire(tab);
                space.Tabs.Add(tab);
            }
            if (space.Tabs.Count > 0) space.ActiveTab = space.Tabs[Math.Clamp(saved.Active, 0, space.Tabs.Count - 1)];
            Spaces.Add(space);
        }
        CurrentSpace = Spaces[Math.Clamp(data.CurrentSpace, 0, Spaces.Count - 1)];
        _restoring = false;

        CurrentSpaceChanged?.Invoke(this, EventArgs.Empty);
        if (CurrentSpace.Tabs.Count == 0) OpenStartPage();
        else CurrentTabChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void TrackSpace(Space space) => space.Tabs.CollectionChanged += (_, _) => ScheduleSessionSave();

    private void ScheduleSessionSave()
    {
        if (_restoring) return;
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    private void SaveSession()
    {
        _sessionTimer.Stop();
        JsonStore.Save(SessionFile, new SessionData
        {
            CurrentSpace = Math.Max(0, Spaces.IndexOf(CurrentSpace)),
            Spaces = Spaces.Select(space => new SpaceData
            {
                Name = space.Name,
                Icon = space.Icon,
                Tabs = space.Tabs.Select(t => t.Url).ToList(),
                History = space.Tabs.Select(t => t.GetHistory()).Select(h => new TabHistoryData { Urls = h.Urls, Index = h.Index }).ToList(),
                Active = space.ActiveTab is null ? 0 : Math.Max(0, space.Tabs.IndexOf(space.ActiveTab)),
            }).ToList(),
        });
    }


    /// <summary>
    /// A page opened a popup window (sign-in, payment): (the page, the popup). The app shows it; without a listener it
    /// becomes a regular tab.
    /// </summary>
    public event EventHandler<(BrowserTab From, BrowserTab Popup)>? PopupOpened;

    /// <summary>A page closed itself (window.close()). Without a listener its tab is closed.</summary>
    public event EventHandler<BrowserTab>? TabCloseRequested;

    private void Wire(BrowserTab tab)
    {
        tab.NewTabRequested += (_, url) => NewTab(url);
        tab.PopupOpened += (_, popup) =>
        {
            Wire(popup);
            if (PopupOpened is { } handler) handler(this, (tab, popup));
            else AdoptTab(popup);
        };
        tab.PermissionRequested += (_, request) =>
        {
            if (PermissionRequested is { } handler) handler(this, (tab, request));
            else request.Respond(false);
        };
        tab.CloseRequested += (_, _) =>
        {
            if (TabCloseRequested is { } handler) handler(this, tab);
            else CloseTab(tab);
        };
        tab.PeekRequested += (_, url) =>
        {
            if (PeekRequested is { } peek) peek(this, (tab, url));
            else NewTab(url);
        };
        tab.FullscreenChanged += (_, fullscreen) => { if (tab == CurrentTab) FullscreenChanged?.Invoke(this, fullscreen); };
        tab.DownloadStarted += (_, download) => DownloadStarted?.Invoke(this, Downloads.Start(download));
        tab.DownloadUpdated += (_, download) => Downloads.Update(download);
        tab.PopupBlocked += (_, blocked) => PopupBlocked?.Invoke(this, (tab, blocked.Origin, blocked.Url));
        tab.DownloadApprovalRequested += (_, approval) =>
        {
            if (DownloadApprovalRequested is { } handler) handler(this, (tab, approval));
            else approval.Decide(true);
        };
        tab.InspectRequested += (_, point) => OpenDevTools(tab, point.X, point.Y);
        tab.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(BrowserTab.Url):
                    History.Visit(tab.Url);
                    ScheduleSessionSave();
                    break;
                case nameof(BrowserTab.Title):
                    History.Describe(tab.Url, tab.Title, tab.FaviconUrl);
                    break;
                case nameof(BrowserTab.FaviconUrl):
                    History.Describe(tab.Url, tab.Title, tab.FaviconUrl);
                    if (_favoriteTabs.FirstOrDefault(p => p.Value == tab).Key is { } site) Favorites.UpdateIcon(site, tab.FaviconUrl);
                    break;
                case nameof(BrowserTab.Media):
                    RefreshNowPlaying();
                    break;
            }
        };
    }
}
