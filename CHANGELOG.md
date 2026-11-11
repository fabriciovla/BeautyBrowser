# Changelog

What each Beauty Browser release changes, newest first. Every release gets its section here before it is published:
`tools/publish-release.ps1` uses it as the release notes and refuses to publish without it.

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
