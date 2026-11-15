using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Shared;

namespace ThixClip.Core.Business;

/// <summary>
/// Reading and changing the settings of this computer. Every change is checked here (values, policy) before it is
/// saved; the UI only offers. <see cref="Changed"/> tells the app (hotkey, theme, monitor) what is new.
/// </summary>
public static class SettingsService
{
    /// <summary>Raised after every saved change (any thread).</summary>
    public static event EventHandler<AppSettings>? Changed;

    public static AppSettings Get() => SettingsStore.Read();

    /// <summary>Changes general settings (not the cloud connection: <see cref="SyncService"/>).</summary>
    public static AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var current = SettingsStore.Read();
        var next = change(current) with { Cloud = current.Cloud };
        Validate(next);
        return Save(_ => next);
    }

    public static AppSettings SetHotkey(string text)
    {
        var hotkey = HotkeyService.Parse(text) ?? throw new BusinessException("Dieses Tastenkürzel wird nicht unterstützt.", nameof(AppSettings.Hotkey));
        HotkeyService.Validate(hotkey);
        return Update(settings => settings with { Hotkey = HotkeyService.Format(hotkey) });
    }

    /// <summary>
    /// Changes how long entries are kept. With the cloud it is the setting of the whole account: the cloud is told and
    /// cleans up on every device.
    /// </summary>
    public static async Task<AppSettings> SetRetentionAsync(int days)
    {
        if (!SyncContract.IsValidRetention(days))
        {
            throw new BusinessException("Diese Speicherdauer gibt es nicht.", nameof(AppSettings.RetentionDays));
        }

        var settings = Update(current => current with { RetentionDays = days });
        if (settings.Cloud.IsConnected)
        {
            await SyncService.PushRetentionAsync(days);
        }

        ClipService.RemoveExpired();
        return settings;
    }

    /// <summary>The stored part of a pause (only <see cref="RecordingService"/>): no configuration lock, it is an everyday switch.</summary>
    internal static AppSettings SetRecordingPaused(bool paused, DateTimeOffset? until) =>
        Save(settings => settings with { IsRecordingPaused = paused, RecordingPausedUntil = paused ? until : null });

    /// <summary>Things the app remembers by itself (window size, folded menu, welcome shown): no configuration lock.</summary>
    public static AppSettings Remember(Func<AppSettings, AppSettings> change)
    {
        var current = SettingsStore.Read();
        var next = change(current);
        return Save(_ => current with
        {
            WindowWidth = Math.Clamp(next.WindowWidth, 640, 4000),
            WindowHeight = Math.Clamp(next.WindowHeight, 420, 3000),
            IsSidebarCollapsed = next.IsSidebarCollapsed,
            WelcomeShown = next.WelcomeShown,
        });
    }

    /// <summary>The cloud part (only <see cref="SyncService"/>).</summary>
    internal static AppSettings UpdateCloud(Func<CloudSettings, CloudSettings> change) =>
        Save(settings => settings with { Cloud = change(settings.Cloud) });

    /// <summary>The account's retention as the cloud reports it (no lock: it comes from the account).</summary>
    internal static void ApplyRetentionFromCloud(int days)
    {
        if (!SyncContract.IsValidRetention(days) || Get().RetentionDays == days)
        {
            return;
        }

        Save(settings => settings with { RetentionDays = days });
        ClipService.RemoveExpired();
    }

    private static AppSettings Save(Func<AppSettings, AppSettings> change)
    {
        var saved = SettingsStore.Update(change);
        Changed?.Invoke(null, saved);
        return saved;
    }

    private static void Validate(AppSettings settings)
    {
        if (!SyncContract.IsValidRetention(settings.RetentionDays))
        {
            throw new BusinessException("Diese Speicherdauer gibt es nicht.", nameof(AppSettings.RetentionDays));
        }

        var hotkey = HotkeyService.Parse(settings.Hotkey) ?? throw new BusinessException("Dieses Tastenkürzel wird nicht unterstützt.", nameof(AppSettings.Hotkey));
        HotkeyService.Validate(hotkey);
    }
}
