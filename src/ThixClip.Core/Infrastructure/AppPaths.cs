namespace ThixClip.Core.Infrastructure;

/// <summary>
/// Where ThixClip keeps its files. Program files and user data are separate: updates and uninstalling never touch the
/// user data unless the user decides so. The clipboard history is machine-local on purpose (it can be large and holds
/// images); the cloud is the way to share it.
///   Windows: %LocalAppData%\ThixClip (history, settings, updates, logs)
///   macOS:   ~/Library/Application Support/ThixClip
/// </summary>
public static class AppPaths
{
    /// <summary>Developers can point ThixClip to a separate folder (sample data, screenshots) with this variable.</summary>
    public const string DataDirectoryVariable = "THIXCLIP_DATA_DIR";

    private static string? _dataDirectoryOverride = NullIfEmpty(Environment.GetEnvironmentVariable(DataDirectoryVariable));

    public static string DataDirectory => _dataDirectoryOverride ?? Path.Combine(UserFolder(), AppInfo.ProductName);

    /// <summary>The clipboard history (SQLite).</summary>
    public static string DatabaseFile => Path.Combine(DataDirectory, "history.db");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string UpdateDirectory => Path.Combine(DataDirectory, "updates");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Texts and images handed to another program ("Öffnen"); emptied at every start.</summary>
    public static string OpenDirectory => Path.Combine(DataDirectory, "open");

    /// <summary>True when a developer or a test pointed ThixClip to its own folder: it is then its own instance.</summary>
    public static bool HasOverride => _dataDirectoryOverride is not null;

    /// <summary>Tests use a temporary folder.</summary>
    internal static void UseDirectoryForTests(string? directory) => _dataDirectoryOverride = directory;

    private static string UserFolder() => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
