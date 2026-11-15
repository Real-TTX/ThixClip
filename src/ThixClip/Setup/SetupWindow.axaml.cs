using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using Avalonia.Interactivity;
using ThixClip.Controls;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;

namespace ThixClip.Setup;

/// <summary>
/// The Windows setup assistant. The work (InstallService, business layer) runs on a background task and reports its
/// steps into the progress page; the window only steers. It cannot be closed halfway through.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class SetupWindow : ThemedWindow
{
    private readonly bool _isUninstall;
    private readonly bool _isUpdate;
    private readonly string? _installedVersion;
    private readonly string _programFolder;
    private readonly ObservableCollection<string> _steps = [];
    private bool _isWorking;
    private bool _isFinished;
    private bool _succeeded;

    /// <summary>For the XAML previewer only.</summary>
    public SetupWindow()
        : this(isUninstall: false)
    {
    }

    private SetupWindow(bool isUninstall)
        : this(isUninstall, isUpdate: !isUninstall && InstallService.IsInstalled, InstallService.InstalledVersion, InstallService.ProgramFolder,
            InstallService.HasDesktopShortcut)
    {
    }

    /// <summary>
    /// What the assistant shows is decided here once: install or update (and the installed version), the program folder.
    /// The snapshots pass fixed values and never ask the system.
    /// </summary>
    private SetupWindow(bool isUninstall, bool isUpdate, string? installedVersion, string programFolder, bool hasDesktopShortcut)
    {
        InitializeComponent();
        var workArea = WorkArea();
        Width = Math.Min(Width, workArea.Width - 16);
        Height = Math.Min(Height, workArea.Height - 16);

        _isUninstall = isUninstall;
        _isUpdate = isUpdate;
        _installedVersion = installedVersion;
        _programFolder = programFolder;
        DesktopToggle.IsChecked = hasDesktopShortcut;
        StepList.ItemsSource = _steps;
        VersionText.Text = $"Version {(isUninstall ? installedVersion ?? AppInfo.Version : AppInfo.Version)}";

        if (isUninstall)
        {
            ShowConfirmPage();
        }
        else
        {
            ShowWelcomePage();
        }
    }

    /// <summary>True when the program folder has to be removed after this process ended (uninstall from the program folder).</summary>
    public bool RemoveProgramFolderOnExit { get; private set; }

    /// <summary>Shows the assistant; <paramref name="finished"/> gets <see cref="RemoveProgramFolderOnExit"/> when it was closed.</summary>
    public static void Run(bool isUninstall, Action<bool> finished)
    {
        var window = new SetupWindow(isUninstall);
        window.Closed += (_, _) => finished(window.RemoveProgramFolderOnExit);
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// A window for the snapshots (ThixClip --snapshot): one page of the assistant with fixed values, nothing installed,
    /// removed or asked from the system.
    /// </summary>
    internal static SetupWindow ForSnapshot(SetupSnapshotPage page, string installedVersion, string programFolder)
    {
        var window = new SetupWindow(isUninstall: page == SetupSnapshotPage.Uninstall, isUpdate: page == SetupSnapshotPage.Update,
            installedVersion, programFolder, hasDesktopShortcut: false);
        if (page == SetupSnapshotPage.Progress)
        {
            window.ShowProgressPage();

            // The running bar would show wherever its animation happens to be: a still picture shows it two thirds along.
            window.Progress.IsIndeterminate = false;
            window.Progress.Value = 65;
            window._steps.Add($"Installiere ThixClip nach {programFolder}");
            window._steps.Add("Programmdatei kopiert");
            window._steps.Add("Startmenü-Eintrag angelegt");
            window._steps.Add("Autostart bei der Windows-Anmeldung eingerichtet");
        }
        else if (page == SetupSnapshotPage.Finish)
        {
            window.ShowProgressPage();
            window.ShowFinishPage(error: null);
        }

        return window;
    }

    protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        // Half an installation is worse than a slow one.
        e.Cancel = _isWorking;
        base.OnClosing(e);
    }

    private void ShowWelcomePage()
    {
        Title = _isUpdate ? "ThixClip aktualisieren" : $"ThixClip {AppInfo.Version} Setup";
        ModeText.Text = _isUpdate ? "Update" : "Setup";
        if (_isUpdate)
        {
            // An update keeps autostart and shortcuts as they are: no options to ask for.
            WelcomeTitle.Text = "Update";
            WelcomeText.Text = $"Installiert ist {_installedVersion ?? "eine ältere Version"}, dieses Setup bringt {AppInfo.Version}. " +
                               "Ein laufendes ThixClip wird beendet und danach wieder gestartet. Dein Verlauf bleibt erhalten.";
            OptionsCard.IsVisible = false;
            PrimaryLabel.Text = "Aktualisieren";
        }
        else
        {
            AutostartToggle.IsChecked = true;
            AutoUpdateToggle.IsChecked = true;
            PrimaryLabel.Text = "Installieren";
        }

        // The folder on a line of its own: wrapped inside a sentence the path breaks after "C:".
        PlaceInfo.Show($"Wird für dein Benutzerkonto installiert – ohne Administratorrechte – nach\n{_programFolder}", InfoSeverity.Info);
    }

    private void ShowConfirmPage()
    {
        Title = "ThixClip deinstallieren";
        ModeText.Text = "Deinstallation";
        WelcomePage.IsVisible = false;
        ConfirmPage.IsVisible = true;
        PrimaryButton.Theme = Ui.Theme("DangerButton");
        PrimaryButton.IsDefault = false;
        PrimaryLabel.Icon = Ui.Icon("Icon.Delete");
        PrimaryLabel.Text = "Deinstallieren";
    }

    private async void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        if (_isFinished)
        {
            if (_succeeded && !_isUninstall && StartToggle.IsChecked == true)
            {
                InstallService.StartInstalled();
            }

            Close();
            return;
        }

        await RunAsync();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private async Task RunAsync()
    {
        _isWorking = true;
        ShowProgressPage();

        var source = App.ExecutablePath;
        var options = _isUpdate ? null : new InstallOptions(AutostartToggle.IsChecked == true, DesktopToggle.IsChecked == true, AutoUpdateToggle.IsChecked == true);
        var deleteData = DeleteDataToggle.IsChecked == true;
        void Report(string step) => Ui.Post(() => _steps.Add(step));

        string? error = null;
        try
        {
            await Task.Run(() =>
            {
                if (_isUninstall)
                {
                    RemoveProgramFolderOnExit = InstallService.Uninstall(deleteData, Report);
                }
                else
                {
                    InstallService.Install(source, options, Report);
                }
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                              or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
        {
            Log.Error(_isUninstall ? "Uninstall failed" : "Setup failed", exception);
            error = exception.Message;
        }

        _isWorking = false;
        ShowFinishPage(error);
    }

    private void ShowProgressPage()
    {
        WelcomePage.IsVisible = ConfirmPage.IsVisible = false;
        ProgressPage.IsVisible = true;
        ProgressTitle.Text = _isUninstall ? "Entferne …" : _isUpdate ? "Aktualisiere …" : "Installiere …";
        PrimaryButton.IsEnabled = CancelButton.IsEnabled = false;
    }

    private void ShowFinishPage(string? error)
    {
        _isFinished = true;
        _succeeded = error is null;
        ProgressPage.IsVisible = false;
        FinishPage.IsVisible = true;
        CancelButton.IsVisible = false;
        PrimaryButton.IsEnabled = true;
        PrimaryButton.IsDefault = true;
        PrimaryButton.Theme = Ui.Theme("PrimaryButton");
        PrimaryLabel.Icon = Ui.Icon("Icon.Check");
        PrimaryLabel.Text = "Fertig";
        StartCard.IsVisible = _succeeded && !_isUninstall;

        if (error is not null)
        {
            FinishTitle.Text = _isUninstall ? "Deinstallation fehlgeschlagen" : "Installation fehlgeschlagen";
            ResultBar.Show($"{error}\n\nDetails stehen im Protokoll ({AppPaths.LogDirectory}).", InfoSeverity.Error);
            return;
        }

        FinishTitle.Text = _isUninstall ? "ThixClip wurde entfernt" : "ThixClip ist bereit";
        ResultBar.Show(_isUninstall
            ? DeleteDataToggle.IsChecked == true ? "Programm und Verlauf wurden entfernt." : "Das Programm wurde entfernt. Dein Verlauf bleibt erhalten."
            : $"ThixClip {AppInfo.Version} ist installiert. Öffne es jederzeit mit {HotkeyService.Display(HotkeyService.DefaultHotkey)} oder über das Symbol im Infobereich.",
            InfoSeverity.Success);
    }
}

/// <summary>The pages of the assistant that "ThixClip --snapshot" draws.</summary>
internal enum SetupSnapshotPage
{
    Welcome,
    Progress,
    Finish,
    Update,
    Uninstall,
}
