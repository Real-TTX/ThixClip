# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What ThixClip is

A clipboard history for **Windows and macOS** (desktop app, tray / menu bar) plus **ThixClip.Cloud** (server, Docker, port
**4950**) that keeps the history the same on every device and in the browser (PWA). The desktop app records text, links
and images (with OCR), opens with a global shortcut (default `Ctrl+Win+V` on Windows, `Ctrl+Option+V` on macOS, it takes
precedence over other apps) and pastes the chosen entry into the app that was in front. **It must open instantly and be
fully usable with the keyboard** (owner's requirement): the main window is built once and only shown/hidden, search
filters in memory on every key. UI language German; code, comments and identifiers English. The mockup of the main
window is `docs/mockups/hauptfenster.png`.

Stack: **.NET 10**, C#. Desktop: **Avalonia 11.3** (Fluent underneath our own look), SQLite (Microsoft.Data.Sqlite),
SkiaSharp. Cloud: ASP.NET Core **Razor Pages**, EF Core + **PostgreSQL**, **MatUi** of the sister project MatDesign.

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
  `branding/png`, `src/ThixClip/Assets`, `src/ThixClip.Cloud/wwwroot/img`. `node tools/avalonia-icons.js` turns MatDesign's
  icon sprite into `src/ThixClip/Themes/Icons.axaml` (never edit that file by hand).
- `scripts/package-macos.sh` builds `ThixClip.app` (Info.plist with `LSUIElement`, icns, ad-hoc signature), the update zip and the dmg.

## Map

```
src/ThixClip.Shared   the sync contract (SyncContract.cs): routes, DTOs, limits, JSON options. Only backwards-compatible changes.
src/ThixClip.Core     (no UI) models, business layer (static classes), infrastructure, native code
  Models/             ClipEntry (immutable record; OcrState, SyncState), AppSettings (+ CloudSettings), ClipQuery, Hotkey, BusinessException
  Business/           ClipService (the history in memory + HistoryStore: capture, dedupe by content hash, query/search incl. OCR,
                      favourite/pin/delete/clear, retention, the sync hooks), ClipboardMonitor (polls the change counter, 250 ms),
                      OcrService (queue), HotkeyService (parse/format/display/validate, registration), SettingsService,
                      SyncService (connect/disconnect, the loop: push -> upload images -> pull -> download images -> long poll),
                      AccessPolicy (LockConfiguration, DisableCloud, DisableUpdates), AutostartService (HKCU Run / LaunchAgent),
                      InstallService (Windows self-installing exe), UpdateService + MacAppBundle (OTA from GitHub releases)
  Infrastructure/     HistoryStore (SQLite history.db: ClipEntry, ClipImage), SettingsStore (settings.json), ImageCodec (SkiaSharp:
                      normalise to PNG, DIB in/out, thumbnails), CloudClient (HTTP of the contract), TokenStore (DPAPI / Keychain),
                      AppPaths, AppInfo, Log
  Platform/           NativeClipboard (+ Windows/WindowsClipboard, Mac/MacClipboard), PasteTarget (+ WindowsPaste, MacPaste:
                      remember the app in front, bring it back, Ctrl+V / Cmd+V), TextRecognition (WindowsOcr / MacOcr = Vision),
                      WindowActivation (foreground on Windows), Windows/WindowsKeyboardHook (low-level hook: the global shortcut,
                      swallows it, masks the Start menu; message-only window = clipboard owner), Mac/MacHotkey (Carbon),
                      Mac/ObjC (objc_msgSend helpers)
src/ThixClip          (Avalonia app)
  App.axaml(.cs)      start-up: Windows setup mode, single instance (mutex + pipe), services, tray, hotkey, prewarm, timers
  MainWindow          header (search, shortcut chip, own caption buttons on Windows / traffic lights on macOS), sidebar (sections,
                      retention card, cloud state, version = about), page host; ALL keyboard handling (OnPreviewKeyDown)
  Pages/              HistoryPage (list grouped by day + detail pane, adaptive below 820 px), ClipRowViewModel, SettingsPage
                      (tabs Allgemein | Aufzeichnung | Cloud | Updates | Erweitert), AboutPage
  Controls/           ONE code base per control: IconView (line icons), IconLabel, ActionBar (left, positive -> negative, Separated
                      gap before delete), SettingRow, FormField (shows BusinessException.Field), InfoBar, KeyCaps, HotkeyRecorder
  Themes/             Theme.axaml (light + dark theme dictionaries, ControlThemes), Icons.axaml (generated)
  Tray/               TrayIcon (menu: open, last five entries, pause, sync, settings, update, quit), NotificationToast
  Setup/              SetupWindow (Windows install / update / uninstall assistant)
  Dialogs/            ConfirmDialog
src/ThixClip.Cloud    the server – map, pages, API, data model: docs/CLOUD.md
tests/ThixClip.Tests  desktop tests (see above); tests/ThixClip.Cloud.Tests, tests/e2e: the cloud
```

## Conventions

- **Microsoft C# coding conventions**, file-scoped namespaces, `_camelCase` fields, human-readable names, early returns.
- **Business layer = static classes.** UI never touches `HistoryStore`, `SettingsStore`, files, registry or HTTP; page models
  never query. Validation **and permission checks** (AccessPolicy, connection state; in the cloud: the acting user) live in
  the business layer. Errors are `BusinessException(message, field)`; the UI shows them at the `FormField` with that `Field`.
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
  version bottom left. Colours only from `Themes/Theme.axaml` via `DynamicResource` (light and dark). Icon-only buttons get
  `AutomationProperties.Name`. Several instances of a control on one page must work (no static state in controls).
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
