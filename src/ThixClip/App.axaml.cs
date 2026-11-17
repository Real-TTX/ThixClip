using System.Net.NetworkInformation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MatDesktop;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Core.Platform;
using TrayIcon = ThixClip.Tray.TrayIcon;

namespace ThixClip;

/// <summary>
/// Start-up and end of ThixClip.
///  - Windows: the published ThixClip.exe is also its setup (RunSetupIfRequested).
///  - The look is MatDesktop's theme with ThixClip's accent (DESK-02).
///  - One instance per user and data folder (MatSingleInstance, DESK-05): a second start opens the window of the first one.
///  - The main window is built once at start and only shown and hidden afterwards: the shortcut opens it at once.
///  - "--autostart" (sign-in) and "--tray" (after an update) start hidden in the tray / menu bar.
/// </summary>
public partial class App : Application
{
    public const string TrayArgument = "--tray";

    /// <summary>ThixClip's accent (IDENT-01): MatDesktop derives every accent token of light and dark from it.</summary>
    public const string Accent = "#2F6FED";

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MatSingleInstance? _instance;
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private DispatcherTimer? _retentionTimer;
    private bool _isExiting;
    private bool _isAskingPause;
    private PauseLength _lastPause = PauseLength.Minutes30;

    /// <summary>The program file; the autostart entry points to it.</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ThixClip");

    public static App Instance => (App)Current!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // ThixClip speaks German, the texts of MatDesktop (title bar, menu, tray, setup) too.
        MatText.Language = "de";
        // the icon of every window (MatWindow.DefaultIcon): the main window, the dialogs, the setup
        MatTheme.Install(this, new MatThemeOptions(Accent) { Icon = Ui.AppIcon });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        _desktop = desktop;
        var args = desktop.Args ?? [];

        var selfTest = Array.IndexOf(args, SelfTest.Argument);
        if (selfTest >= 0)
        {
            var report = selfTest + 1 < args.Length ? args[selfTest + 1] : Path.Combine(AppPaths.DataDirectory, "self-test.txt");
            Dispatcher.UIThread.Post(async () => EndProcess(await SelfTest.RunAsync(report)));
            return;
        }

        if (OperatingSystem.IsWindows() && RunSetupIfRequested(args))
        {
            return;
        }

        if (!TakeSingleInstance())
        {
            EndProcess(0);
            return;
        }

        Start(args);
    }

    /// <summary>The main window (null during setup).</summary>
    public MainWindow? MainWindow => _mainWindow;

    /// <summary>
    /// Ends the process through the main loop. Shutting down straight from OnFrameworkInitializationCompleted (update,
    /// quiet uninstall, second instance) throws "Dispatcher shut down" because the loop has not started yet.
    /// </summary>
    private void EndProcess(int exitCode) => Dispatcher.UIThread.Post(() => _desktop?.Shutdown(exitCode));

    /// <summary>Installed by ThixLauncher: its sign-in at MatCloud goes along (in the background; a cloud that does not answer changes nothing).</summary>
    private async Task ConnectFromLauncherAsync()
    {
        if (await Task.Run(() => SyncService.ConnectFromLauncherAsync()) is { } account)
        {
            Ui.Post(() => _trayIcon?.ShowNotification("Mit der MatCloud verbunden", $"ThixClip gleicht deinen Verlauf jetzt mit der MatCloud ab (Konto {account}, angemeldet von ThixLauncher)."));
        }
    }

    private void Start(string[] args)
    {
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        UiWatchdog.Start();
        Log.Info($"ThixClip {AppInfo.Version} starting on {UpdateService.PlatformKey} ({string.Join(' ', args)})");

        ClipService.Initialize();
        RecordingService.Initialize();
        ApplyTheme(SettingsService.Get().Theme);
        SettingsService.Changed += (_, settings) => Ui.Post(() => ApplyTheme(settings.Theme));

        _mainWindow = new MainWindow();
        _trayIcon = new TrayIcon(this);
        RegisterHotkey();

        ClipboardMonitor.Start();
        OcrService.Start();
        SyncService.Start();
        _ = ConnectFromLauncherAsync();

        var startHidden = args.Contains(TrayArgument) || args.Contains(AutostartService.StartupArgument);
        if (!startHidden)
        {
            _mainWindow.ShowFromTray(rememberForeground: false);
        }
        else
        {
            // Build the window's visuals once, off screen, so the first shortcut opens it as fast as every later one.
            _mainWindow.Prewarm();
        }

        // Crashed or hung last time: say so, with the report.
        Dispatcher.UIThread.Post(() => Dialogs.ProblemReportDialog.ShowUnseen(), DispatcherPriority.Background);

        // macOS: opening the app again (Finder, Spotlight) shows the window.
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, activation) =>
            {
                if (activation.Kind == ActivationKind.Reopen)
                {
                    ShowMainWindow();
                }
            };
        }

        UpdateCoordinator.Checked += (_, result) =>
        {
            _trayIcon?.SetUpdateAvailable(result.IsUpdateAvailable);
            if (result is { IsUpdateAvailable: true, Manifest: { } manifest } && !SettingsService.Get().InstallUpdatesAutomatically)
            {
                _trayIcon?.ShowNotification("Update verfügbar", $"ThixClip {manifest.Version} kann installiert werden (Einstellungen › Updates).");
            }
        };
        UpdateCoordinator.Start();

        // Retention: once an hour (and at start, ClipService.Initialize).
        _retentionTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _retentionTimer.Tick += (_, _) => Task.Run(ClipService.RemoveExpired);
        _retentionTimer.Start();

        // After sleep, a network change or a new Wi-Fi the cloud is asked at once.
        NetworkChange.NetworkAvailabilityChanged += (_, availability) =>
        {
            if (availability.IsAvailable)
            {
                SyncService.Kick();
            }
        };
        NetworkChange.NetworkAddressChanged += (_, _) => SyncService.Kick();

        SyncService.StatusChanged += (_, status) =>
        {
            if (status.State == CloudState.SignedOut)
            {
                Ui.Post(() => _trayIcon?.ShowNotification("MatCloud", status.Message, isWarning: true));
            }
        };
    }

    /// <summary>Registers the global shortcut of the settings (again after a change).</summary>
    public bool RegisterHotkey() => HotkeyService.Apply(SettingsService.Get().Hotkey, OnHotkeyPressed);

    /// <summary>
    /// The shortcut: remember the app in front right now (the window is not up yet), then show or hide ThixClip. On
    /// Windows this runs on a thread-pool thread, on macOS on the main thread.
    /// </summary>
    private void OnHotkeyPressed()
    {
        var window = _mainWindow;
        if (window is null || _isExiting)
        {
            return;
        }

        if (!window.IsActiveQuickWindow)
        {
            PasteTarget.RememberForeground(window.NativeHandle);
        }

        Ui.Post(() => window.ToggleFromHotkey());
    }

    /// <summary>Opens the main window (tray, second start, Dock) and optionally navigates.</summary>
    public void ShowMainWindow(Action<MainWindow>? navigate = null)
    {
        if (_mainWindow is null || _isExiting)
        {
            return;
        }

        _mainWindow.ShowFromTray(rememberForeground: true);
        navigate?.Invoke(_mainWindow);
    }

    /// <summary>
    /// The pause button (main window, tray, settings, Ctrl/Cmd+Shift+P): a running pause ends at once, otherwise the dialog
    /// asks how long. The last length is offered again while the app runs.
    /// </summary>
    public async Task TogglePauseAsync(Window? owner = null)
    {
        if (RecordingService.IsPaused)
        {
            RecordingService.Resume();
            return;
        }

        if (_isAskingPause)
        {
            return;
        }

        _isAskingPause = true;
        try
        {
            owner ??= _mainWindow is { IsVisible: true } ? _mainWindow : null;
            if (await Dialogs.PauseDialog.AskAsync(owner, _lastPause) is { } length)
            {
                _lastPause = length;
                RecordingService.Pause(length);
            }
        }
        finally
        {
            _isAskingPause = false;
        }
    }

    /// <summary>The setting "Erscheinungsbild": Wie das System, Hell, Dunkel (DESK-02).</summary>
    public static void ApplyTheme(ThemeChoice theme) => MatTheme.SetAppearance(theme switch
    {
        ThemeChoice.Light => MatAppearance.Light,
        ThemeChoice.Dark => MatAppearance.Dark,
        _ => MatAppearance.System,
    });

    /// <summary>"Beenden": ends the application.</summary>
    public void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        UiWatchdog.Stop();
        Log.Info("ThixClip exiting");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        void Step(string name, Action action)
        {
            var started = watch.ElapsedMilliseconds;
            action();
            if (watch.ElapsedMilliseconds - started > 300)
            {
                Log.Warning($"Exit step {name} took {watch.ElapsedMilliseconds - started} ms");
            }
        }

        Step("clipboard monitor", ClipboardMonitor.Stop);
        Step("OCR", OcrService.Stop);
        Step("sync", SyncService.Stop);
        Step("shortcut", HotkeyService.Unregister);
        _retentionTimer?.Stop();
        Step("tray icon", () => _trayIcon?.Dispose());
        if (_mainWindow is not null)
        {
            _mainWindow.IsExiting = true;
            Step("main window", _mainWindow.Close);
        }

        Step("history", ClipService.Close);
        _instance?.Dispose();
        Log.Info($"ThixClip ended after {watch.ElapsedMilliseconds} ms");
        _desktop?.Shutdown(0);
    }

    /// <summary>
    /// Windows: the published ThixClip.exe is its own setup (InstallService):
    ///  - "--uninstall" (from "Installierte Apps"): the uninstall assistant, or silently with "--quiet".
    ///  - "--update" (downloaded by the update check): installs itself silently and starts the installed ThixClip in the tray.
    ///  - started anywhere else than the program folder (e.g. from Downloads): the setup assistant.
    /// A development build (not single-file) always runs as the application.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool RunSetupIfRequested(string[] args)
    {
        if (args.Contains(InstallService.UninstallArgument))
        {
            if (args.Contains(InstallService.QuietArgument))
            {
                if (InstallService.Uninstall(deleteUserData: false, Log.Info))
                {
                    InstallService.ScheduleProgramFolderRemoval();
                }

                EndProcess(0);
                return true;
            }

            Setup.ThixClipSetup.Run(isUninstall: true, removeProgramFolder =>
            {
                if (removeProgramFolder)
                {
                    InstallService.ScheduleProgramFolderRemoval();
                }

                EndProcess(0);
            });
            return true;
        }

        if (args.Contains(InstallService.UpdateArgument))
        {
            try
            {
                InstallService.Install(ExecutablePath, options: null, Log.Info);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                                  or System.Runtime.InteropServices.COMException)
            {
                Log.Error("Update failed", exception);
            }

            if (File.Exists(InstallService.InstalledExecutable))
            {
                InstallService.StartInstalled(TrayArgument);
            }

            EndProcess(0);
            return true;
        }

#if SINGLE_FILE
        const bool isPublishedBuild = true;
#else
        const bool isPublishedBuild = false;
#endif
        if (isPublishedBuild && !InstallService.IsInstalledExecutable(ExecutablePath))
        {
            Setup.ThixClipSetup.Run(isUninstall: false, _ => EndProcess(0));
            return true;
        }

        return false;
    }

    /// <summary>
    /// One instance per user and data folder (MatSingleInstance, DESK-05: a named mutex and a pipe, on macOS a lock file
    /// and a socket): a second start hands its arguments to the first one, which shows its window, and ends. A development
    /// run with its own THIXCLIP_DATA_DIR does not meet the installed one. On Windows the setup may also ask the running
    /// instance to end (ListenForExitSignal).
    /// </summary>
    private bool TakeSingleInstance()
    {
        _instance = new MatSingleInstance(AppInfo.ProductName, AppPaths.HasOverride ? AppPaths.DataDirectory : null);
        _instance.SecondStart += (_, _) => Ui.Post(() => ShowMainWindow());
        if (!_instance.TryClaim(_desktop?.Args ?? []))
        {
            Log.Info("ThixClip runs already: its window was asked to show");
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            ListenForExitSignal();
        }

        return true;
    }

    /// <summary>The setup asks a running ThixClip to end (update, uninstall).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ListenForExitSignal()
    {
        if (AppPaths.HasOverride)
        {
            return; // a development run with its own data folder is none of the setup's business
        }

        var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstallService.ExitSignalName);
        var listener = new Thread(() =>
        {
            exitSignal.WaitOne();
            Ui.Post(() =>
            {
                Log.Info("Setup asked ThixClip to end");
                ExitApplication();
            });
        })
        {
            IsBackground = true,
            Name = "ThixClip exit signal listener",
        };
        listener.Start();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error", e.Exception);
        e.Handled = true;
        if (e.Exception is BusinessException business)
        {
            _trayIcon?.ShowNotification("ThixClip", business.Message, isWarning: true);
            return;
        }

        _trayIcon?.ShowNotification("Unerwarteter Fehler", $"{e.Exception.Message} – Details im Protokoll ({AppPaths.LogDirectory}).", isWarning: true);
    }
}
