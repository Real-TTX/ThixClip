# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What ThixClip is

A clipboard history for **Windows and macOS** (desktop app, tray / menu bar) plus **ThixClip.Cloud** (server, Docker, port
**4950**) that keeps the history the same on every device and in the browser (PWA). The desktop app records text, links
and images (with OCR), opens with a global shortcut (default `Ctrl+Shift+V` on Windows, `Ctrl+Option+V` on macOS, it takes
precedence over other apps) and pastes the chosen entry into the app that was in front. **It must open instantly and be
fully usable with the keyboard** (owner's requirement): the main window is built once and only shown/hidden, search
filters in memory on every key. UI language German; code, comments and identifiers English. The mockup of the main
window is `docs/mockups/hauptfenster.png`.

Stack: **.NET 10**, C#. Desktop: **Avalonia 11.3** with **MatDesktop** (the desktop toolkit of the sister project MatDesign:
Fluent in the colours of the tokens, title bar, menu, tray, setup window), SQLite (Microsoft.Data.Sqlite), SkiaSharp.
Cloud: ASP.NET Core **Razor Pages**, EF Core + **PostgreSQL**, **MatUi** of MatDesign. The desktop app follows MatDesign's
rules DESK, TRAY and SETUP (MatDesign docs/DESKTOP.md).

## Build / run / test

- `dotnet build ThixClip.slnx` – expected: 0 warnings, 0 errors. On Windows the desktop projects target
  `net10.0-windows10.0.19041.0` (Windows OCR), elsewhere `net10.0` (`DesktopTargetFramework` in `Directory.Build.props`).
- `dotnet test tests/ThixClip.Tests` – business layer, sync against `FakeCloud` (in memory), image codec, hotkeys, setup
  (test locations only), and the **UI rendered headless** with Avalonia + Skia (`UiTests`: keyboard flows; with
  `THIXCLIP_SCREENSHOTS=<folder>` screenshots of every page in light and dark – look at them). Tests never touch the real
  history, settings, clipboard, Keychain, registry values or LaunchAgents (`TestData`).
  `THIXCLIP_CLOUD_URL` / `_USER` / `_PASSWORD` enable `CloudEndToEndTests` (two devices against a real cloud, e.g. the local stack).
- `dotnet test tests/ThixClip.Cloud.Tests` – see docs/CLOUD.md (with `THIXCLIP_TEST_DB` also against PostgreSQL);
  browser checks: `cd tests/e2e && npm install && node run.js http://localhost:4950`.
- **Run the cloud: always rebuild and redeploy the stack:** `./scripts/redeploy.ps1` (`-Clean` drops the volumes).
- Run the desktop app with sample data: `THIXCLIP_DATA_DIR=<folder>` (own instance, never meets the installed one).
  Windows screenshots of the real window need a real screen; a disconnected RDP session has a 640x480 display and
  rejects `SendInput` – use the headless UI tests there.
- Icons: `cd tools && npm install && node render-icons.js` renders `branding/*.svg` (installed Edge, headless) into
  `branding/png`, `src/ThixClip/Assets`, `src/ThixClip.Cloud/wwwroot/img`. The line icons of the app are MatDesktop's
  (every symbol of MatDesign's sprite, `<mat:MatIcon Icon="copy"/>`); an icon the sprite lacks is added to MatDesign first.
- **MatDesktop** (`src/ThixClip/MatDesktop/`, compiled into the app) is a COPY from MatDesign: never edit it here. Change it in
  MatDesign and copy it again there with `node tools/matdesktop-copy.js <path>/ThixClip/src/ThixClip`; the copy writes
  `matdesktop.version` (the hash of every file) and `UiTests.The_copy_of_MatDesktop_is_unchanged` holds the files to it. Its
  README (MatDesign `src/MatDesktop/README.md`) lists the resource keys and control themes.
- The Windows publish (win-x64) is a **trimmed** single file (`TrimMode=partial`: only trimmable libraries shrink, our own
  assemblies stay whole; about 38 MB). No `dynamic`, no built-in COM: COM goes through `[GeneratedComInterface]`
  (`Platform/Windows/ShellLink`). `ThixClip.exe --self-test <report>` checks the published program (JSON, SQLite, Skia,
  token store, OCR, COM, every window) in a temporary data folder; CI runs it after every Windows publish.
- **Snapshots:** `dotnet run --project src/ThixClip -- --snapshot docs/images` (or `ThixClip.exe --snapshot <folder>`) draws
  the windows headless (Avalonia.Headless + Skia, fonts of the machine) into PNGs, light and dark, 1x, natural size:
  `setup-{welcome,options,progress,finish,update,uninstall}`, `main-{history,settings,about,folded,paused}`,
  `dialog-{pause,clear-history,viewer}`, `toast` (`-light`/`-dark`) and `tray-icon-{windows,macos}[-paused]` (64 px). Temporary data folder with `SampleHistory`, pinned
  `AppClock` (12.11.2026 14:32), `AppInfo.Pin` (1.0.0, release, ANNA-PC), clipboard/tokens/policies/autostart in memory; no
  tray, hotkey, network or update check. Same output every run; MatDesign shows them and the README uses them. `UiTests` runs
  it in-process. A new window or page: add it to `Snapshot.cs` (and `FileNames`), look at the pictures. Pictures for the
  README or MatDesign come from here only: the screenshots of the UI tests show the real computer name and today's date.
- `scripts/package-macos.sh` builds `ThixClip.app` (Info.plist with `LSUIElement`, icns, ad-hoc signature), the update zip and the dmg.

## Map

```
src/ThixClip.Shared   the sync contract (SyncContract.cs): routes, DTOs, limits, JSON options. Only backwards-compatible changes.
                      ClearRange.cs: the ranges of "Verlauf löschen" (letzte Stunde … gesamte Zeit), the same on desktop and in the browser
src/ThixClip.Core     (no UI) models, business layer (static classes), infrastructure, native code
  Models/             ClipEntry (immutable record; OcrState, SyncState), AppSettings (+ CloudSettings), ClipQuery, Hotkey, BusinessException
  Business/           ClipService (the history in memory + HistoryStore: capture, dedupe by content hash, query/search incl. OCR,
                      favourite/pin/delete, ClearHistory(ClearRange) by last copy time, BrowserLink / ReadImageToView for the viewer (AppPaths.OpenDirectory: files of old versions, emptied at start), retention, the sync hooks), ClipboardMonitor (polls the change counter, 250 ms),
                      OcrService (queue), HotkeyService (parse/format/display/validate, registration), SettingsService,
                      SyncService (connect/disconnect, the loop: push -> upload images -> pull -> download images -> long poll),
                      AccessPolicy (LockConfiguration, DisableCloud, DisableUpdates), AutostartService (HKCU Run / LaunchAgent),
                      RecordingService (pause: 15/30 min, 1 h stored with its end, until restart in memory, until resumed),
                      InstallService (Windows self-installing exe), UpdateService + MacAppBundle (OTA from GitHub releases),
                      SampleHistory (the mockup's history, drawn with Skia: UI tests and snapshots, only into a test/temp folder)
  Infrastructure/     HistoryStore (SQLite history.db: ClipEntry, ClipImage), SettingsStore (settings.json), ImageCodec (SkiaSharp:
                      normalise to PNG, DIB in/out, thumbnails), CloudClient (HTTP of the contract), TokenStore (DPAPI / Keychain),
                      AppPaths, AppInfo (Pin: fixed version for snapshots), AppClock (the time of history and UI; Pin for snapshots), Log, ProblemReports (unhandled exceptions -> logs/crash-*.txt + message box on Windows,
                      hangs -> logs/hang-*.txt + .dmp on Windows; shown once at the next start)
  Platform/           NativeClipboard (+ Windows/WindowsClipboard, Mac/MacClipboard), PasteTarget (+ WindowsPaste, MacPaste:
                      remember the app in front, bring it back, Ctrl+V / Cmd+V), TextRecognition (WindowsOcr / MacOcr = Vision),
                      WindowActivation (foreground on Windows), Windows/WindowsKeyboardHook (low-level hook: the global shortcut,
                      swallows it, masks the Start menu; message-only window = clipboard owner), Windows/ShellLink (.lnk, generated COM),
                      Mac/MacHotkey (Carbon),
                      Mac/ObjC (objc_msgSend helpers)
src/ThixClip          (Avalonia app)
  Program / App       start-up: ProblemReports first, self test, Windows setup mode, single instance (MatSingleInstance), services, tray,
                      hotkey, prewarm, timers, TogglePauseAsync (the pause dialog for header, tray, settings, Ctrl/Cmd+Shift+P);
                      Initialize installs MatDesktop's theme with ThixClip's accent (App.Accent, #2F6FED) and the app icon of every window (MatThemeOptions.Icon), MatText in German
  MatDesktop/         COPY of MatDesign's desktop toolkit (never edited here, see Build / run / test): MatTheme, MatWindow, MatTitleBar, MatNav,
                      MatIcon, MatSettingRow/MatFormField, MatTray/MatTrayMenu/MatTrayIcons, MatNotification, MatSetupWindow, MatVersion ...
  Ui                  helpers: app icon and logo, Theme(key) for object initializers, the UI thread, opening addresses, texts of times
  UiWatchdog          knocks on the UI thread every second; 8 s without an answer = hang report (ProblemReports.WriteHang)
  SelfTest            --self-test of the published program (CI)
  Snapshot            --snapshot <folder>: every window as PNG, headless (see Build / run / test)
  MainWindow          a MatWindow: MatTitleBar (logo and word mark; search, pause button: framed "Pausieren" / warning "Fortsetzen · pausiert bis …",
                      shortcut chip; caption buttons on Windows / traffic lights on macOS), MatNav (sections, Settings; at its foot the
                      retention card and the cloud state in MatNav.Foot, a waiting update as MatNav's link (UpdateVersion -> About),
                      "Version x · channel" = about; folds to a 68 px rail with Ctrl/Cmd+B or the button at its foot, remembered in
                      AppSettings.IsSidebarCollapsed), page host; ALL keyboard handling (OnPreviewKeyDown)
  Pages/              HistoryPage (title and filters in one line; list grouped by day + detail pane with ONE row of icon buttons
                      Einfügen, Kopieren, Öffnen, Anheften, Favorit … Löschen; Ctrl/Cmd+click, +O or a click on the image opens the viewer; Delete takes the chosen
                      entry and keeps the focus; count / notes bottom right; the chosen row is scrolled into view only after
                      layout (BringIntoView), else the virtualizing panel leaves a growing gap; adaptive below 820 px), ClipRowViewModel, SettingsPage
                      (tabs Allgemein | Aufzeichnung | Cloud | Updates | Erweitert), AboutPage (update card -> Einstellungen › Updates)
  Controls/           ThixClip's own, ONE code base per control: IconLabel (a sprite icon and a text; MatIcon.IsFilled for a chosen star or pin),
                      ActionBar (left, positive -> negative, Separated gap before delete), FormErrors (FormErrors.Field on a MatFormField:
                      shows BusinessException.Field), InfoBar (like mat-alert), KeyCaps (MatKeyCap), HotkeyRecorder
  Tray/               TrayIcon: MatTrayMenu + MatTray (TRAY-01 order: "ThixClip 1.0.0", the state, "ThixClip öffnen", own items = last five
                      entries, then a group pause …/resume, sync; Open shows the shortcut, the tooltip "Öffnen mit …"; filled again in MatTray.MenuOpening; Einstellungen, Log-Ordner, update, Beenden); state Läuft (green dot) or paused (amber
                      dot with a pause sign, MatState.Custom); notifications are MatNotification
  Setup/              ThixClipSetup: MatSetupWindow for install (Welcome → Options → Progress → Finish), update and uninstall; the work is
                      InstallService on a background task
  Dialogs/            ConfirmDialog, ClearHistoryDialog ("Verlauf löschen" as in a browser: range list with counts, Enter deletes;
                      Ctrl/Cmd+Shift+Delete or the button below the list), PauseDialog (how long), ViewerDialog ("Öffnen" inside ThixClip: image fitted / 100 %, text selectable; Enter pastes, Ctrl/Cmd+C copies, Esc closes – no other program, no file), ProblemReportDialog (crash / hang
                      of the last run: the report to read and copy, the log folder)
src/ThixClip.Cloud    the server – map, pages, API, data model: docs/CLOUD.md
tests/ThixClip.Tests  desktop tests (see above); tests/ThixClip.Cloud.Tests, tests/e2e: the cloud
```

## Conventions

- **Microsoft C# coding conventions**, file-scoped namespaces, `_camelCase` fields, human-readable names, early returns.
- **Business layer = static classes.** UI never touches `HistoryStore`, `SettingsStore`, files, registry or HTTP; page models
  never query. Validation **and permission checks** (AccessPolicy, connection state; in the cloud: the acting user) live in
  the business layer. Errors are `BusinessException(message, field)`; the UI shows them at the `MatFormField` whose
  `controls:FormErrors.Field` is that `Field`.
- **Relational rules** (Postgres and the local SQLite): PascalCase tables/columns, key `Id` (64 bit), `CreateDate`,
  `CreateUserId`, `UpdateDate`, `UpdateUserId`, `UpdateState` (0 deleted, 1 created, 2 updated) for soft deletes / tombstones,
  a `Token` uuid where a key is a secret (UserSession, Device).
- **Sync rules** (both sides): one revision sequence per server; last writer wins by `UpdatedAt`; deleting wins and leaves a
  tombstone without content; the same content (SHA-256 of the UTF-8 text / the normalised PNG) under another key is merged
  (`MergedInto`); images go up after their entry (`NeedsImage`); retention is an account setting. Never log clipboard
  contents, passwords or tokens.
- **UI rules:** toolbar above lists (search, filter, sort), actions below, left-aligned, positive to negative, delete with a
  gap; buttons in one row (`Einfügen`, `Kopieren`, `Anheften` … gap … `Löschen`); icons where they help; tabs for areas of one
  item; hide what does not apply (OCR section only for images); cards keep their distance; the about page opens from the
  version bottom left. **Colours only from MatDesktop's theme** (DESK-02): `{DynamicResource Mat.Surface}`, `Mat.TextMuted`,
  `Mat.AccentText` … in XAML, `control.Use(property, "Mat.TextMuted")` in code (a brush looked up once would not follow a
  switch of light and dark); named looks are MatDesktop's control themes (`MatButton.Primary`, `.Danger`, `.Icon`, `.Link`,
  `MatText.PageTitle`, `MatCard` …), `Themed(key)` in code. No literal colour (`UiTests.No_window_writes_a_colour_of_its_own`):
  the only value ThixClip gives is its accent. Selected is a tint with the accent as its line, never a bar (COLOR-08). The
  version reads `Version x · channel` everywhere (MatVersion, SETUP-07). Icon-only buttons get `AutomationProperties.Name`. Several instances of a control on one page must work (no static state in controls).
- **Avalonia:** ControlThemes cannot contain descendant selectors (`^ Border.row` throws at runtime) – put those styles on
  the page. No `DispatcherTimer` debouncing on hot paths (search runs per key; refreshes coalesce through `Dispatcher.Post`).
- **Platforms:** one code base. Differences live in Core behind `OperatingSystem.IsWindows()/IsMacOS()` or Windows/macOS-only
  types; Windows OCR is under `#if WINDOWS`. macOS cannot be checked from the Windows machine: CI builds and tests it, the
  owner tries it on a Mac (clipboard, Carbon hot key, accessibility prompt, Vision).
- Edit text files with the editor tools or `[IO.File]` with UTF-8: Windows PowerShell 5.1 `Get-Content`/`Set-Content`
  double-encode umlauts.

## Versioning, branches, CI

- `Directory.Build.props` holds major/minor. CI sets `Channel`, `BuildNumber` (GitHub run number of the workflow),
  `BuildDate`: release `0.1.42-20261115` (main), nightly `nightly-43-20261115` (dev), local `local-20261115` (everything else).
- Branches: `main` = release, `dev` = nightly. **Commits are authored by Matthias Schmoldt only – no co-author trailers, no
  other names, no "generated with" lines.**
- `.github/workflows/desktop.yml`: Windows (test, publish win-x64 single file = `ThixClipSetup.exe`), macOS (test, publish
  osx-arm64 + osx-x64, `package-macos.sh` → dmg/zip), publish (manifest `thixclip-update.json`, rolling releases
  `release-channel` / `nightly-channel`, versioned release `v<version>` on main).
- `.github/workflows/cloud.yml`: tests (with a Postgres service) and the multi-arch image `ghcr.io/real-ttx/thixclip`
  (`:latest`/`:release`/`:<version>` from main, `:nightly`/`:<version>` from dev). Compose files: `docker-compose.yml`
  (release), `docker-compose.dev.yml` (nightly + pgAdmin), `docker-compose.local.yml` (build + pgAdmin + Postgres on 4952),
  no variables, one stack each, names without repetition (services `web`, `db`, volumes `web-data`, `db-data`).
