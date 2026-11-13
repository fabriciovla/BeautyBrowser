# Changelog

What each Beauty Browser release changes, newest first. Every release gets its section here before it is published:
`tools/publish-release.ps1` uses it as the release notes and refuses to publish without it.

## [0.1.5] - 2026-11-13

### Added
- Choose where the tabs live: sidebar on the left, sidebar on the right, or tabs on top (Settings → Sidebar and tabs).
  With tabs on top the new tab opens on the right, tabs can be dragged to reorder, and the + button opens a tab.
  The address bar of the top bar is a real search box, the library opens under the bar, and the site settings, zoom
  badge and favorites star live inside the address bar. Tabs show a speaker while they play sound (click to mute) and
  have a menu with Reload, Mute, Close others and Close tabs to the right.
- Settings open as a tab, with categories, a search box and card pages.
- Middle click, Ctrl+click and "Open link in new tab" open the page in a background tab and tell you about it (in the
  top bar the new tab glows for a moment).
- The start page is simpler and shows the icon of your search engine. It also lists the people who contribute to
  Beauty Browser.

### Fixed
- Middle click and Ctrl+click on a normal link open a new tab.
- The minimum window size is 720 × 480, and the sidebar floats over the page when the window is narrower than 900.
- The right sidebar fills the whole window height.
- Tabs on top: dragging is limited to the bar, many tabs scroll instead of overflowing, Ctrl+T focuses the address
  bar, and Ctrl+1…9 and Ctrl+Tab follow the order you see.

## [0.1.4] - 2026-11-12

### Added
- Dark mode: Settings → Mode lets you pick Light, Dark or System. The window, panels, start page and the Windows
  caption buttons all follow it.
- Private spaces have a dark look, a fade when you enter them, and a start page that explains what is not saved. They
  no longer suggest your history or searches.
- The search palette shows the 5 pages you visit most, with the exact address (for example `twitch.tv/somechannel`).
- Interface language setting: System, Español or English, applied on restart.
- The window remembers its position, size and maximized state.
- The start page has a new design with cards for our apps.
- Plain http pages show a "not secure" icon in the address bar.
- The zoom indicator has a Reset button.
- Each download shows the Windows file-type icon.

### Fixed
- Updates are only installed when they are signed with the Beauty Browser release key.
- The cursor hides when a page asks for it (YouTube in full screen) and comes back when you leave the page.
- No 1 px line at the top of full-screen videos.
- The page shows the right cursor when you drag or pan, and when a drop is not allowed.
- Pages use Chrome's default fonts on Windows, so sites like Wikipedia look as they do in Chrome.
- The window can't be shrunk below a phone-like size (400 × 500).
- The tab preview has rounded corners and is centered on the tab.
- Scrolling in History, Downloads, Settings and Spaces is smooth.
- Links to apps (mailto:, Zoom, Teams…) ask to open the app instead of breaking the page.
- When a tab's page crashes, the browser says so and reloads it when you return to the tab.
- Downloads keep going when the tab that started them closes, and two downloads with the same name no longer collide.
- Private tabs: downloads are not saved to the list, site permission choices are not stored, and the new tab page loads.
- Safer browsing: the browser always asks before screen sharing, more dangerous file types get a warning, pages can
  only open web addresses in popups, links can't pass Chromium options to the browser, and pages and their frames
  can't trick the password manager.
- A corrupted settings file is kept aside instead of being overwritten.
- No Windows crash dialog when a Chromium helper process stops.

## [0.1.3] - 2026-11-11

### Fixed
- The browser crashed when a sign-in popup closed itself (for example "Sign in with Google" on Figma).
- Closing a tab now asks the page first, so sites with unsaved work can show "Leave site?".
- Back and forward history survives restarting the browser and reopening closed tabs (Ctrl+Shift+T).
- Mouse-wheel scrolling is smooth and follows the Windows "lines per notch" setting.
- Dropdowns near the bottom of the page are no longer cut off.
- Closing the browser is instant: the window hides at once while Chromium shuts down.
- Closing a tab in the sidebar animates smoothly.
- More site icons load (SVG icons, and icons that sites refuse to give to a direct download).
- The address bar tells addresses from searches with real rules (IP addresses, `localhost:8080`, `node.js`, emails).
- "Search Google for…" in the context menu uses the search engine chosen in settings.
- The third-party cookie setting recognizes sites correctly (Public Suffix List).
- Copying an image copies the image itself, including private and generated images.
- Addresses are shown readable instead of percent-encoded.
- The default download file name follows the browser's language.

### Added
- Private browsing: Ctrl+Shift+N opens a Private space. Nothing is saved to history or the session, and cookies and
  site data are dropped when its last tab closes.
- Password manager: offers to save a password after you sign in, lists saved accounts under the login field and fills
  them with a click. Passwords are encrypted with Windows. Manage them in Settings → Saved passwords.
- Zoom indicator: the zoom level shows over the page when it changes, and a badge in the address bar resets it.
- Ctrl+wheel zooms the page.
- Ctrl+T: Tab completes the selected suggestion, and the empty box shows your most visited sites.
- Warnings before keeping dangerous downloads, and a notice to allow a blocked popup.
- "Save as…" asks where to save, and pages are saved complete (MHTML).
- More of Chrome's context menu: loop, show controls, picture in picture and copy frame for videos; copy link text;
  paste as plain text; spelling suggestions and add to dictionary.
- Shortcuts: Esc stops loading, F11 full screen, Ctrl+K / Ctrl+E search, Ctrl+Enter, Ctrl+N new tab,
  Ctrl+Shift+Delete clear browsing data.
- Links dropped on the sidebar open as new tabs.
- Touch screens: scroll, tap, long-press and pinch like Chrome, and the touch keyboard opens on text fields.

## [0.1.2] - 2026-11-10

### Added
- Automatic updates from GitHub Releases: the browser checks every 6 hours and installs the new version when you close it.

### Fixed
- Browsing closer to Chrome: popups, site permissions, keyboard shortcuts, smooth scrolling on heavy sites
  (WhatsApp), background tabs, faster history, dragging the window from the top of the page, Shift+wheel.

## [0.1.1] and earlier

See [GitHub Releases](https://github.com/fabriciovla/BeautyBrowser/releases).
