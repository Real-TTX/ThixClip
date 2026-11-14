using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;

namespace ThixClip.Pages;

/// <summary>Version, channel, build and where the data lives.</summary>
public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version}";
        ChannelText.Text = AppInfo.Channel switch
        {
            "release" => "Release",
            "nightly" => "Nightly",
            _ => "Lokaler Build",
        };
        BuildText.Text = AppInfo.BuildNumber > 0 ? $"#{AppInfo.BuildNumber}" : "–";
        SystemText.Text = $"{RuntimeInformation.OSDescription} ({UpdateService.PlatformKey}), .NET {Environment.Version}";
        DataText.Text = AppPaths.DataDirectory;
    }

    private void OnGitHubClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal($"https://github.com/{AppInfo.Repository}");

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (Ui.WindowOf(this) is MainWindow window)
        {
            window.ShowSection(Core.Models.ClipSection.History);
        }
    }
}
