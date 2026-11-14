using Microsoft.Win32;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;

namespace ThixClip.Tests;

/// <summary>Setup, autostart and updates. Nothing here touches the real program folder, registry values or LaunchAgents.</summary>
public sealed class PlatformTests : IDisposable
{
    private readonly TestData _data = new();

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            InstallService.UseLocationsForTests(null);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\ThixClipTests", throwOnMissingSubKey: false);
        }

        AutostartService.UseLaunchAgentFolderForTests(null);
        _data.Dispose();
    }

    [Fact]
    public void The_setup_installs_into_the_program_folder_and_uninstalls_everything()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var program = Path.Combine(_data.Folder, "Programs", "ThixClip");
        var shortcuts = Path.Combine(_data.Folder, "StartMenu");
        InstallService.UseLocationsForTests(program, shortcuts, @"Software\ThixClipTests\Uninstall");
        var source = Path.Combine(_data.Folder, "Downloads", "ThixClipSetup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, new byte[1024 * 8]);

        var steps = new List<string>();
        InstallService.Install(source, new InstallOptions(StartWithWindows: false, DesktopShortcut: true), steps.Add);

        Assert.True(File.Exists(InstallService.InstalledExecutable));
        Assert.True(File.Exists(InstallService.StartMenuShortcut));
        Assert.True(File.Exists(InstallService.DesktopShortcut));
        Assert.True(InstallService.IsInstalled);
        Assert.Equal(AppInfo.Version, InstallService.InstalledVersion);
        Assert.Contains("Installation abgeschlossen", steps);

        // An update keeps the shortcuts as they are.
        InstallService.Install(source, options: null, _ => { });
        Assert.True(File.Exists(InstallService.DesktopShortcut));

        var removeLater = InstallService.Uninstall(deleteUserData: false, _ => { });
        Assert.False(removeLater);
        Assert.False(InstallService.IsInstalled);
        Assert.False(File.Exists(InstallService.StartMenuShortcut));
        Assert.False(Directory.Exists(program));
        Assert.True(Directory.Exists(AppPaths.DataDirectory), "the history stays unless the user deletes it");
    }

    [Fact]
    public void The_launch_agent_starts_thixclip_hidden_at_sign_in()
    {
        var plist = AutostartService.LaunchAgent("/Applications/ThixClip.app/Contents/MacOS/ThixClip");
        Assert.Contains("<string>info.schmoldt.thixclip</string>", plist);
        Assert.Contains("<string>/Applications/ThixClip.app/Contents/MacOS/ThixClip</string>", plist);
        Assert.Contains($"<string>{AutostartService.StartupArgument}</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
    }

    [Fact]
    public void Autostart_on_macos_writes_and_removes_the_launch_agent()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        AutostartService.UseLaunchAgentFolderForTests(_data.Folder);
        AutostartService.Enable("/Applications/ThixClip.app/Contents/MacOS/ThixClip");
        Assert.True(AutostartService.IsEnabled());
        AutostartService.Disable();
        Assert.False(AutostartService.IsEnabled());
    }

    [Fact]
    public void Updates_only_come_from_the_own_channel_and_a_higher_build()
    {
        var manifest = new UpdateManifest("0.1.50-20261201", "release", 50, "ThixClipSetup.exe", "AB", DateTimeOffset.UtcNow,
            new Dictionary<string, UpdatePackage> { ["osx-arm64"] = new("ThixClip-macOS-arm64.zip", "CD") });

        Assert.True(UpdateService.IsNewer(manifest, "release", 49));
        Assert.False(UpdateService.IsNewer(manifest, "release", 50));
        Assert.False(UpdateService.IsNewer(manifest, "nightly", 1));
        Assert.Equal("ThixClipSetup.exe", UpdateService.PackageFor(manifest, "win-x64")!.File);
        Assert.Equal("ThixClip-macOS-arm64.zip", UpdateService.PackageFor(manifest, "osx-arm64")!.File);
        Assert.Null(UpdateService.PackageFor(manifest, "osx-x64"));
        Assert.True(UpdateService.IsSafeFileName("ThixClipSetup.exe"));
        Assert.False(UpdateService.IsSafeFileName("../evil.exe"));
        Assert.False(UpdateService.IsSafeFileName("setup.ps1"));
    }

    [Fact]
    public void A_local_build_never_updates_itself()
    {
        Assert.True(AppInfo.IsLocalBuild);
        Assert.False(UpdateService.CanUpdate);
    }

    [Fact]
    public void The_mac_update_script_swaps_the_bundle_and_restarts_in_the_menu_bar()
    {
        var script = MacAppBundle.ReplacementScript(42, "/Applications/ThixClip.app", "/tmp/new/ThixClip.app", "/tmp/update.log");
        Assert.Contains("while kill -0 42", script);
        Assert.Contains("BUNDLE='/Applications/ThixClip.app'", script);
        Assert.Contains("--args --tray", script);
        Assert.EndsWith("Applications" + Path.DirectorySeparatorChar + "ThixClip.app",
            MacAppBundle.BundleOf("/Applications/ThixClip.app/Contents/MacOS/ThixClip")!.Replace('/', Path.DirectorySeparatorChar));
        Assert.Null(MacAppBundle.BundleOf("/usr/local/bin/ThixClip"));
    }
}
