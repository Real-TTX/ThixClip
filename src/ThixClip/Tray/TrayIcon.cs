using Avalonia.Controls;
using Avalonia.Platform;
using ThixClip.Core.Business;
using ThixClip.Core.Models;
using ThixClip.Shared;
using AvaloniaTrayIcon = Avalonia.Controls.TrayIcon;

namespace ThixClip.Tray;

/// <summary>
/// The icon in the notification area (Windows) or the menu bar (macOS) and its menu: open ThixClip, the five latest
/// entries to copy again, pause recording, sync now, settings, updates, quit. Windows: a click opens the window, the
/// right button the menu. macOS: the glyph is a template image (the menu bar colours it), every click opens the menu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int RecentCount = 5;

    private readonly AvaloniaTrayIcon _trayIcon;
    private readonly NativeMenu _menu = new();
    private readonly App _app;
    private bool _updateAvailable;

    public TrayIcon(App app)
    {
        _app = app;
        _trayIcon = new AvaloniaTrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri(OperatingSystem.IsMacOS() ? "avares://ThixClip/Assets/tray-macos.png" : "avares://ThixClip/Assets/tray-windows.ico"))),
            ToolTipText = ToolTip(),
            Menu = _menu,
            IsVisible = true,
        };
        if (OperatingSystem.IsMacOS())
        {
            MacOSProperties.SetIsTemplateIcon(_trayIcon, true);
        }

        _trayIcon.Clicked += (_, _) => _app.ShowMainWindow();
        _menu.Opening += (_, _) => BuildMenu();
        AvaloniaTrayIcon.SetIcons(app, [_trayIcon]);
        SettingsService.Changed += OnSettingsChanged;
        BuildMenu();
    }

    public void SetUpdateAvailable(bool available)
    {
        _updateAvailable = available;
        BuildMenu();
    }

    public void ShowNotification(string title, string text, bool isWarning = false) =>
        NotificationToast.Show(title, text, isWarning, () => _app.ShowMainWindow());

    public void Dispose()
    {
        SettingsService.Changed -= OnSettingsChanged;
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => Ui.Post(() =>
    {
        _trayIcon.ToolTipText = ToolTip();
        BuildMenu();
    });

    private static string ToolTip()
    {
        var settings = SettingsService.Get();
        var hotkey = HotkeyService.Display(settings.Hotkey);
        return settings.IsRecordingPaused ? $"ThixClip – Aufzeichnung pausiert ({hotkey})" : $"ThixClip – {hotkey}";
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var settings = SettingsService.Get();

        _menu.Items.Add(Item($"ThixClip öffnen\t{HotkeyService.Display(settings.Hotkey)}", () => _app.ShowMainWindow()));
        _menu.Items.Add(new NativeMenuItemSeparator());

        var recent = ClipService.Query(new ClipQuery()).Where(entry => !entry.IsPinned).Take(RecentCount).ToList();
        _menu.Items.Add(new NativeMenuItem("Zuletzt kopiert") { IsEnabled = false });
        if (recent.Count == 0)
        {
            _menu.Items.Add(new NativeMenuItem("Noch nichts kopiert") { IsEnabled = false });
        }

        foreach (var entry in recent)
        {
            var id = entry.Id;
            _menu.Items.Add(Item(MenuText(entry), () => Copy(id)));
        }

        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(new NativeMenuItem("Aufzeichnung pausieren")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = settings.IsRecordingPaused,
            Command = new RelayCommand(() => SettingsService.SetRecordingPaused(!SettingsService.Get().IsRecordingPaused)),
        });
        if (settings.Cloud.IsConnected)
        {
            _menu.Items.Add(Item("Jetzt synchronisieren", SyncService.Kick));
        }

        _menu.Items.Add(Item("Einstellungen …", () => _app.ShowMainWindow(window => window.ShowSettings())));
        if (_updateAvailable)
        {
            _menu.Items.Add(Item("Update installieren …", () => _app.ShowMainWindow(window => window.ShowSettings(Pages.SettingsPage.UpdatesTab))));
        }

        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(Item("Beenden", () => _app.ExitApplication()));
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

    private static NativeMenuItem Item(string header, Action action) => new(header) { Command = new RelayCommand(action) };

    /// <summary>Menu items run their action through a command (works on Windows and in the macOS menu bar).</summary>
    private sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Ui.Post(action);
    }
}
