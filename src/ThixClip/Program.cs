using Avalonia;
using Avalonia.Controls;

namespace ThixClip;

internal static class Program
{
    /// <summary>
    /// One program for everything (App decides): the application and - on Windows - its setup.
    /// The application ends only through "Beenden" (tray / menu bar), not when its window closes.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // First of all: no error ends ThixClip without a report (and on Windows a message box).
        ThixClip.Core.Infrastructure.ProblemReports.Install(showMessageBox: !args.Contains(SelfTest.Argument));

        // The self test works in a fresh folder of its own (before anything reads the data folder).
        if (args.Contains(SelfTest.Argument))
        {
            Environment.SetEnvironmentVariable(ThixClip.Core.Infrastructure.AppPaths.DataDirectoryVariable,
                Path.Combine(Path.GetTempPath(), "thixclip-self-test", Guid.NewGuid().ToString("N")));
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>Also used by the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // A clipboard tool lives in the menu bar: no Dock icon, the window comes and goes with the shortcut.
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .With(new Win32PlatformOptions { OverlayPopups = true })
            .LogToTrace();
}
