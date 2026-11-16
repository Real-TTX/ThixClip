using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MatDesktop;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Shared;

namespace ThixClip.Tray;

/// <summary>
/// The icon in the notification area (Windows) or the menu bar (macOS), with MatDesktop's tray (TRAY-01 … TRAY-04):
/// the menu in the order of every Mat tray – "ThixClip 1.0.0", the state in words, "ThixClip öffnen", ThixClip's own items
/// (the five latest entries to copy again, pause / resume, sync now), "Einstellungen …", "Log-Ordner öffnen",
/// "Update installieren …" while one waits, "Beenden". The state is the dot of the icon: running green, paused amber
/// with a pause sign (on macOS a template image with the pause shape). Windows: a left click opens the window, the right
/// button the menu; macOS: every click opens the menu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int RecentCount = 5;

    private readonly App _app;
    private readonly MatTrayMenu _menu;
    private readonly MatTray _tray;
    private bool _updateAvailable;
    private bool _refreshPosted;

    public TrayIcon(App app)
    {
        _app = app;
        _menu = new MatTrayMenu(AppInfo.ProductName, AppInfo.Version, () => _app.ExitApplication())
        {
            Open = () => _app.ShowMainWindow(),
            Settings = () => _app.ShowMainWindow(window => window.ShowSettings()),
            LogFolder = () => Ui.OpenExternal(AppPaths.LogDirectory),
        };
        Update();
        _tray = new MatTray(app, _menu, Mark());
        _tray.MenuOpening += (_, _) => Update(); // macOS and Linux say when the menu opens: the last entries are fresh then
        SettingsService.Changed += OnChanged;
        RecordingService.Changed += OnChanged;
        ClipService.Changed += OnChanged;
    }

    /// <summary>The model of the menu (MatTray builds the menu from it).</summary>
    public MatTrayMenu Menu => _menu;

    /// <summary>The state of the tray: running, or paused (a state of ThixClip's own, TRAY-02) with its end in words.</summary>
    public static MatState State => RecordingService.IsPaused
        ? MatState.Custom(RecordingService.Describe(), "--mat-warning", MatStateMark.Pause)
        : MatState.Running;

    /// <summary>The mark the tray icons are drawn from: the logo on Windows, the template image (black on transparent) on macOS.</summary>
    public static Bitmap Mark(bool? template = null) =>
        new(AssetLoader.Open(new Uri((template ?? OperatingSystem.IsMacOS()) ? "avares://ThixClip/Assets/tray-macos.png" : "avares://ThixClip/Assets/logo-64.png")));

    public void SetUpdateAvailable(bool available)
    {
        _updateAvailable = available;
        Refresh();
    }

    /// <summary>A notification of ThixClip's own (TRAY-04: only for what the user did not cause); a click opens the window.</summary>
    public void ShowNotification(string title, string text, bool isWarning = false) =>
        MatNotification.Show(title, text, isWarning, () => _app.ShowMainWindow());

    public void Dispose()
    {
        SettingsService.Changed -= OnChanged;
        RecordingService.Changed -= OnChanged;
        ClipService.Changed -= OnChanged;
        _tray.Dispose();
    }

    /// <summary>
    /// The state, the last entries or a setting changed: the menu, the tooltip and (only when the state looks different) the
    /// icon follow. Windows does not say when the tray menu opens, so the model is kept up to date (coalesced: a burst of
    /// changes is one refresh; MatTray draws the icon again only when the colour or the mark of the state changed).
    /// </summary>
    private void OnChanged(object? sender, object? e)
    {
        if (_refreshPosted)
        {
            return;
        }

        _refreshPosted = true;
        Ui.Post(() =>
        {
            _refreshPosted = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        Update();
        _tray.Refresh();
    }

    /// <summary>The model: the state, ThixClip's own items and whether an update waits.</summary>
    private void Update()
    {
        var settings = SettingsService.Get();
        _menu.State = State;
        _menu.OpenShortcut = settings.Hotkey; // at the right of "ThixClip öffnen" (DESK-07)
        _menu.TooltipText = $"Öffnen mit {HotkeyService.Display(settings.Hotkey)}";
        _menu.InstallUpdate = _updateAvailable ? () => _app.ShowMainWindow(window => window.ShowSettings(Pages.SettingsPage.UpdatesTab)) : null;
        _menu.ClearOwn();

        var recent = ClipService.Query(new ClipQuery()).Where(entry => !entry.IsPinned).Take(RecentCount).ToList();
        _menu.AddOwn("Zuletzt kopiert", null);
        if (recent.Count == 0)
        {
            _menu.AddOwn("Noch nichts kopiert", null);
        }

        foreach (var entry in recent)
        {
            var id = entry.Id;
            _menu.AddOwn(MenuText(entry), () => Copy(id));
        }

        // pause and sync in a group of their own, below the last entries (still in the place of TRAY-01)
        _menu.AddOwnSeparator();
        if (RecordingService.IsPaused)
        {
            _menu.AddOwn($"Aufzeichnung fortsetzen ({RecordingService.Describe()})", RecordingService.Resume);
        }
        else
        {
            _menu.AddOwn("Aufzeichnung pausieren …", () => _ = _app.TogglePauseAsync());
        }

        if (settings.Cloud.IsConnected)
        {
            _menu.AddOwn("Jetzt synchronisieren", SyncService.Kick);
        }
    }

    private void Copy(long id)
    {
        try
        {
            ClipService.CopyToClipboard(id);
        }
        catch (BusinessException exception)
        {
            ShowNotification("Kopieren nicht möglich", exception.Message, isWarning: true);
        }
    }

    private static string MenuText(ClipEntry entry)
    {
        if (entry.Kind == ClipKind.Image)
        {
            return $"Bild ({entry.ImageWidth} × {entry.ImageHeight})";
        }

        var text = string.Join(' ', (entry.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 48 ? text : text[..47] + "…";
    }
}
