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
    {
        InitializeComponent();
        var workArea = WorkArea();
        Width = Math.Min(Width, workArea.Width - 16);
        Height = Math.Min(Height, workArea.Height - 16);

        _isUninstall = isUninstall;
        _isUpdate = !isUninstall && InstallService.IsInstalled;
        StepList.ItemsSource = _steps;
        VersionText.Text = $"Version {(isUninstall ? InstallService.InstalledVersion ?? AppInfo.Version : AppInfo.Version)}";

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
            WelcomeText.Text = $"Installiert ist {InstallService.InstalledVersion ?? "eine ältere Version"}, dieses Setup bringt {AppInfo.Version}. " +
                               "Ein laufendes ThixClip wird beendet und danach wieder gestartet. Dein Verlauf bleibt erhalten.";
            OptionsCard.IsVisible = false;
            PrimaryLabel.Text = "Aktualisieren";
        }
        else
        {
            AutostartToggle.IsChecked = true;
            DesktopToggle.IsChecked = InstallService.HasDesktopShortcut;
            PrimaryLabel.Text = "Installieren";
        }

        PlaceInfo.Show($"Wird für dein Benutzerkonto installiert – ohne Administratorrechte – nach {InstallService.ProgramFolder}.", InfoSeverity.Info);
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
        WelcomePage.IsVisible = ConfirmPage.IsVisible = false;
        ProgressPage.IsVisible = true;
        ProgressTitle.Text = _isUninstall ? "Entferne …" : _isUpdate ? "Aktualisiere …" : "Installiere …";
        PrimaryButton.IsEnabled = CancelButton.IsEnabled = false;

        var source = App.ExecutablePath;
        var options = _isUpdate ? null : new InstallOptions(AutostartToggle.IsChecked == true, DesktopToggle.IsChecked == true);
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
