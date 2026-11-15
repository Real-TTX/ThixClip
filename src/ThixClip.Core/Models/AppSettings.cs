using ThixClip.Shared;

namespace ThixClip.Core.Models;

public enum ThemeChoice
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>The settings of this computer (settings.json). Only <see cref="Business.SettingsService"/> writes them.</summary>
public sealed record AppSettings
{
    /// <summary>The global shortcut that opens ThixClip, e.g. "Ctrl+Win+V" (Windows) or "Ctrl+Alt+V" (macOS).</summary>
    public string Hotkey { get; init; } = Business.HotkeyService.DefaultHotkey;

    /// <summary>Choosing an entry pastes it into the app that was in front (otherwise it is only copied).</summary>
    public bool PasteOnSelect { get; init; } = true;

    /// <summary>The window hides as soon as another app comes to the front (like a picker).</summary>
    public bool HideOnDeactivate { get; init; } = true;

    /// <summary>Content marked as confidential by password managers is not recorded.</summary>
    public bool IgnoreConfidential { get; init; } = true;

    public bool RecordImages { get; init; } = true;

    /// <summary>Recognise the text of images (search finds it).</summary>
    public bool RecognizeText { get; init; } = true;

    /// <summary>Recording is paused (tray menu): copies are not added to the history.</summary>
    public bool IsRecordingPaused { get; init; }

    /// <summary>How long entries are kept (0 = forever). With the cloud it is the setting of the account.</summary>
    public int RetentionDays { get; init; } = SyncContract.DefaultRetentionDays;

    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Full auto update: install new versions without asking.</summary>
    public bool InstallUpdatesAutomatically { get; init; }

    /// <summary>Size of the main window (device-independent pixels).</summary>
    public double WindowWidth { get; init; } = 1180;

    public double WindowHeight { get; init; } = 760;

    /// <summary>The menu at the left of the main window is folded to its icons.</summary>
    public bool IsSidebarCollapsed { get; init; }

    /// <summary>The welcome hint (shortcut, tray) has been shown.</summary>
    public bool WelcomeShown { get; init; }

    public CloudSettings Cloud { get; init; } = new();
}

/// <summary>The connection to ThixClip.Cloud. The device token is stored protected (DPAPI / Keychain), not in here.</summary>
public sealed record CloudSettings
{
    /// <summary>The address of the cloud, e.g. https://thixclip.example.com. Empty = not connected.</summary>
    public string ServerUrl { get; init; } = "";

    public string UserName { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public string DeviceName { get; init; } = "";

    public long DeviceId { get; init; }

    /// <summary>The highest revision of the account this computer has applied.</summary>
    public long LastRevision { get; init; }

    /// <summary>The device token, protected for this user (Windows DPAPI); on macOS it lives in the Keychain.</summary>
    public string ProtectedToken { get; init; } = "";

    public bool IsConnected => ServerUrl.Length > 0 && DeviceId > 0;
}
