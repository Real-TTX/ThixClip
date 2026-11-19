using Microsoft.Win32;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;

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
        AutostartService.UseInMemoryEntry(null);
        InstallerOptions.UseConfigForTests(null);
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

        AutostartService.UseInMemoryEntry(false);
        var steps = new List<string>();
        InstallService.Install(source, new InstallChoices(DesktopShortcut: true, Autostart: false), steps.Add);

        Assert.True(File.Exists(InstallService.InstalledExecutable));
        Assert.True(File.Exists(InstallService.StartMenuShortcut));
        Assert.True(File.Exists(InstallService.DesktopShortcut));
        Assert.True(InstallService.IsInstalled);
        Assert.Equal(AppInfo.Version, InstallService.InstalledVersion);
        Assert.Contains("Installation abgeschlossen", steps);
        Assert.False(AutostartService.IsEnabled());
        Assert.True(SettingsService.Get().InstallUpdatesAutomatically, "not given: the default of installer.json");

        // An update keeps the shortcuts as they are; a given option changes it.
        InstallService.Install(source, null, _ => { });
        Assert.True(File.Exists(InstallService.DesktopShortcut));
        Assert.False(AutostartService.IsEnabled());
        InstallService.Install(source, new InstallChoices(Autostart: true, AutomaticUpdates: false), _ => { });
        Assert.True(AutostartService.IsEnabled());
        Assert.False(SettingsService.Get().InstallUpdatesAutomatically);
        Assert.True(File.Exists(InstallService.DesktopShortcut));
        Assert.Equal((true, true, false), (InstallerOptions.Current().DesktopShortcut, InstallerOptions.Current().Autostart, InstallerOptions.Current().AutomaticUpdates));

        var removeLater = InstallService.Uninstall(deleteUserData: false, _ => { });
        Assert.False(removeLater);
        Assert.False(InstallService.IsInstalled);
        Assert.False(File.Exists(InstallService.StartMenuShortcut));
        Assert.False(Directory.Exists(program));
        Assert.True(Directory.Exists(AppPaths.DataDirectory), "the history stays unless the user deletes it");
        Assert.False(AutostartService.IsEnabled());
    }

    [Fact]
    public void The_command_line_carries_the_install_options_and_refuses_what_it_does_not_know()
    {
        var request = InstallerOptions.Parse(["--quiet", "--desktop-shortcut=yes", "--autostart=no", "--automatic-updates=Nein", "--start"]);
        Assert.Equal(SetupAction.Install, request.Action);
        Assert.True(request.Start);
        Assert.Equal((true, false, false), (request.Choices.DesktopShortcut, request.Choices.Autostart, request.Choices.AutomaticUpdates));
        Assert.Null(request.Choices.FileTypes);
        Assert.Equal(["--desktop-shortcut=yes", "--autostart=no", "--automatic-updates=no"], InstallerOptions.ToArguments(request.Choices));
        Assert.Equal(SetupAction.Update, InstallerOptions.Parse(["--update", "--autostart=yes"]).Action);
        Assert.Empty(InstallerOptions.Parse(["--configure", "--file-types=none"]).Choices.FileTypes!);
        Assert.Empty(InstallerOptions.Parse(["--quiet", "--file-types=default"]).Choices.FileTypes!);
        Assert.Equal(SetupAction.Uninstall, InstallerOptions.Parse(["--uninstall", "--quiet"]).Action);

        // the start at sign-in ("--autostart" without a value) and the other arguments of the app are no setup
        Assert.Equal(SetupAction.None, InstallerOptions.Parse([AutostartService.StartupArgument]).Action);
        Assert.Equal(SetupAction.None, InstallerOptions.Parse(["--tray"]).Action);

        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--autostart=vielleicht"]));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--file-types=.txt"]));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--color=blue"]));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--tray", "--autostart=yes"]));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--uninstall", "--desktop-shortcut=no"]));

        InstallerOptions.UseConfigForTests(InstallerOptions.Config with { DesktopShortcut = null });
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--desktop-shortcut=yes"]));
    }

    [Fact]
    public void The_embedded_installer_json_is_what_the_manifest_publishes()
    {
        var config = InstallerOptions.Config;
        Assert.NotNull(config.Autostart);
        Assert.NotNull(config.DesktopShortcut);
        Assert.NotNull(config.AutomaticUpdates);
        Assert.Empty(config.FileTypes);
        Assert.Equal((false, true, true), (InstallerOptions.Defaults.DesktopShortcut, InstallerOptions.Defaults.Autostart, InstallerOptions.Defaults.AutomaticUpdates));
    }

    [Fact]
    public void The_setup_assistant_asks_what_installer_json_offers_with_its_defaults()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var options = ThixClip.Setup.ThixClipSetup.Options().ToDictionary(option => option.Key, option => option.IsOn);
        Assert.Equal(new Dictionary<string, bool> { ["autostart"] = true, ["shortcut"] = false, ["updates"] = true }, options);
        Assert.Equal(new InstallChoices(false, true, true), ThixClip.Setup.ThixClipSetup.Choices(options));
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
    public async Task A_local_build_or_the_policy_stops_updates()
    {
        // CI builds are release or nightly builds; a local build never updates itself.
        if (AppInfo.IsLocalBuild)
        {
            Assert.False(UpdateService.CanUpdate);
        }

        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableUpdates");
        Assert.False(UpdateService.CanUpdate);
        await Assert.ThrowsAsync<BusinessException>(() => UpdateService.CheckAsync());
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
