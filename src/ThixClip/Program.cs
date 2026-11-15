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
        var isSnapshot = args.Contains(Snapshot.Argument);
        ThixClip.Core.Infrastructure.ProblemReports.Install(showMessageBox: !args.Contains(SelfTest.Argument) && !isSnapshot);

        // The self test and the snapshots work in a fresh folder of their own (before anything reads the data folder).
        if (args.Contains(SelfTest.Argument) || isSnapshot)
        {
            Environment.SetEnvironmentVariable(ThixClip.Core.Infrastructure.AppPaths.DataDirectoryVariable,
                Path.Combine(Path.GetTempPath(), isSnapshot ? "thixclip-snapshot" : "thixclip-self-test", Guid.NewGuid().ToString("N")));
        }

        // The snapshots draw the windows headless: no screen, no tray, no shortcut, no single-instance check.
        if (isSnapshot)
        {
            return Snapshot.Run(args);
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
