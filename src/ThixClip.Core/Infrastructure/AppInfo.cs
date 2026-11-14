using System.Reflection;
using System.Runtime.InteropServices;

namespace ThixClip.Core.Infrastructure;

/// <summary>Version, channel and build number baked in at build time (Directory.Build.props).</summary>
public static class AppInfo
{
    private static readonly Assembly ThisAssembly = typeof(AppInfo).Assembly;

    public const string ProductName = "ThixClip";

    /// <summary>e.g. 0.1.42-20261114, nightly-43-20261114 or local-20261114.</summary>
    public static string Version { get; } =
        ThisAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "local";

    /// <summary>release, nightly or local.</summary>
    public static string Channel { get; } = ReadMetadata("ThixClipChannel") ?? "local";

    public static int BuildNumber { get; } = int.TryParse(ReadMetadata("ThixClipBuildNumber"), out var number) ? number : 0;

    /// <summary>owner/name of the GitHub repository that publishes the builds.</summary>
    public static string Repository { get; } = ReadMetadata("ThixClipRepository") ?? "Real-TTX/ThixClip";

    public static bool IsLocalBuild => Channel == "local";

    /// <summary>"Windows" or "macOS" (sent to the cloud with the device).</summary>
    public static string PlatformName => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : RuntimeInformation.OSDescription;

    /// <summary>The name of this computer, offered as the device name for the cloud.</summary>
    public static string MachineName
    {
        get
        {
            try
            {
                return Environment.MachineName;
            }
            catch (InvalidOperationException)
            {
                return "Computer";
            }
        }
    }

    private static string? ReadMetadata(string key) =>
        ThisAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value;
}
