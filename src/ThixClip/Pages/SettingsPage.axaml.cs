using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MatCloud.Contract;
using MatCloud.Contract.Clip;
using MatDesktop;
using ThixClip.Controls;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Core.Platform;
using ThixClip.Dialogs;

namespace ThixClip.Pages;

/// <summary>
/// The settings. Every change goes through the business layer (SettingsService, AutostartService, SyncService,
/// UpdateService), which checks values and policies; an error is shown at its field or above the tabs.
/// </summary>
public partial class SettingsPage : UserControl
{
    public const string GeneralTab = "general";
    public const string CloudTab = "cloud";
    public const string UpdatesTab = "updates";

    private bool _isLoading;
    private UpdateManifest? _availableUpdate;
    private CancellationTokenSource? _browserSignIn;
    private string? _browserAddress;

    public SettingsPage()
    {
        InitializeComponent();
        RetentionChoice.ItemsSource = ClipContract.RetentionChoices.Select(Ui.RetentionText).ToList();

        HotkeyRecorder.Recorded += (_, text) => Apply(() =>
        {
            SettingsService.SetHotkey(text);
            if (!App.Instance.RegisterHotkey())
            {
                throw new BusinessException("Das Tastenkürzel ist schon von einer anderen App belegt.", nameof(AppSettings.Hotkey));
            }
        });
        AutostartSwitch.IsCheckedChanged += (_, _) => Apply(() =>
        {
            if (AutostartSwitch.IsChecked == true)
            {
                AutostartService.Enable(App.ExecutablePath);
            }
            else
            {
                AutostartService.Disable();
            }
        });
        PasteSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { PasteOnSelect = PasteSwitch.IsChecked == true }));
        CloseAfterCopySwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { CloseAfterCopy = CloseAfterCopySwitch.IsChecked == true }));
        HideSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { HideOnDeactivate = HideSwitch.IsChecked == true }));
        ThemeChoice.SelectionChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { Theme = (ThemeChoice)Math.Max(0, ThemeChoice.SelectedIndex) }));
        RetentionChoice.SelectionChanged += async (_, _) =>
        {
            if (_isLoading || RetentionChoice.SelectedIndex < 0)
            {
                return;
            }

            await ApplyAsync(() => SettingsService.SetRetentionAsync(ClipContract.RetentionChoices[RetentionChoice.SelectedIndex]));
        };
        ImagesSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { RecordImages = ImagesSwitch.IsChecked == true }));
        OcrSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { RecognizeText = OcrSwitch.IsChecked == true }));
        ConfidentialSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { IgnoreConfidential = ConfidentialSwitch.IsChecked == true }));
        CheckUpdatesSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { CheckForUpdates = CheckUpdatesSwitch.IsChecked == true }));
        AutoInstallSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { InstallUpdatesAutomatically = AutoInstallSwitch.IsChecked == true }));

        SettingsService.Changed += (_, _) => Ui.Post(Load);
        RecordingService.Changed += (_, _) => Ui.Post(LoadPause);
        SyncService.StatusChanged += (_, _) => Ui.Post(LoadCloud);
        UpdateCoordinator.Checked += (_, result) => ShowUpdateResult(result);
        Load();
    }

    /// <summary>A sign-in in the browser is waiting for its confirmation (the main window stays open meanwhile).</summary>
    public bool IsSigningInInBrowser => _browserSignIn is not null;

    public void SelectTab(string tab) => Tabs.SelectedItem = tab switch
    {
        CloudTab => CloudTabItem,
        UpdatesTab => UpdatesTabItem,
        _ => GeneralTabItem,
    };

    /// <summary>Shows the stored settings (again after every change, also from the tray or the sidebar).</summary>
    private void Load()
    {
        _isLoading = true;
        try
        {
            var settings = SettingsService.Get();
            var locked = AccessPolicy.IsConfigurationLocked;
            PolicyNote.IsVisible = locked || AccessPolicy.IsCloudDisabled || AccessPolicy.AreUpdatesDisabled;

            HotkeyRecorder.Hotkey = settings.Hotkey;
            HotkeyRecorder.IsEnabled = HotkeyResetButton.IsEnabled = !locked;
            HotkeyResetButton.IsVisible = settings.Hotkey != HotkeyService.DefaultHotkey;
            AutostartSwitch.IsChecked = AutostartService.IsEnabled();
            AutostartSwitch.IsEnabled = AutostartService.IsSupported && !locked;
            PasteSwitch.IsChecked = settings.PasteOnSelect;
            HideSwitch.IsChecked = settings.HideOnDeactivate;
            CloseAfterCopySwitch.IsChecked = settings.CloseAfterCopy;
            ThemeChoice.SelectedIndex = (int)settings.Theme;
            RetentionChoice.SelectedIndex = ClipContract.RetentionChoices.ToList().IndexOf(settings.RetentionDays);
            foreach (var control in new Control[] { PasteSwitch, CloseAfterCopySwitch, HideSwitch, ThemeChoice, RetentionChoice, ImagesSwitch, OcrSwitch, ConfidentialSwitch, CheckUpdatesSwitch, AutoInstallSwitch })
            {
                control.IsEnabled = !locked;
            }

            ShowAccessibilityHint(settings.PasteOnSelect);

            LoadPause();
            ImagesSwitch.IsChecked = settings.RecordImages;
            OcrSwitch.IsChecked = settings.RecognizeText;
            ConfidentialSwitch.IsChecked = settings.IgnoreConfidential;
            OcrRow.Description = OcrService.IsAvailable
                ? "Die Suche findet auch Text in Screenshots. Läuft auf diesem Gerät, nichts verlässt den Computer."
                : "Auf diesem Gerät ist keine Texterkennung verfügbar (Windows: Sprachpaket mit Texterkennung installieren).";

            CheckUpdatesSwitch.IsChecked = settings.CheckForUpdates;
            AutoInstallSwitch.IsChecked = settings.InstallUpdatesAutomatically;
            InstalledVersionText.Text = MatVersion.Value(AppInfo.Version, AppInfo.Channel);
            ChannelText.Text = AppInfo.Channel switch
            {
                "release" => "Release (Branch main)",
                "nightly" => "Nightly (Branch dev)",
                _ => "Lokaler Build – keine Updates",
            };
            LastCheckText.Text = UpdateService.LastCheck is { } last ? Ui.FormatDateTime(last) : "–";
            CheckUpdateButton.IsEnabled = UpdateService.CanUpdate;
            CheckUpdatesSwitch.IsEnabled = AutoInstallSwitch.IsEnabled = UpdateService.CanUpdate && !locked;

            DataFolderRow.Description = AppPaths.DataDirectory;
            LogFolderRow.Description = AppPaths.LogDirectory;
            LoadCloud();
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>The pause: what runs now and the button that changes it (the dialog asks how long).</summary>
    private void LoadPause()
    {
        var paused = RecordingService.IsPaused;
        PauseRow.Description = paused
            ? $"{RecordingService.Describe()} – Kopiertes kommt gerade nicht in den Verlauf."
            : $"Für eine Weile nichts aufnehmen, z. B. bei vertraulicher Arbeit ({Ui.PauseShortcut}, auch im Menü des Symbols).";
        PauseSettingLabel.Icon = paused ? "play" : "pause";
        PauseSettingLabel.Text = paused ? "Fortsetzen" : "Pausieren …";
    }

    private async void OnPauseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await App.Instance.TogglePauseAsync(Ui.WindowOf(this));

    private void LoadCloud()
    {
        var cloud = SettingsService.Get().Cloud;
        var status = SyncService.Status;
        CloudLogin.IsVisible = !cloud.IsConnected;
        CloudConnected.IsVisible = cloud.IsConnected;
        CloudLogin.IsEnabled = !AccessPolicy.IsCloudDisabled;
        if (AccessPolicy.IsCloudDisabled)
        {
            CloudMessage.Show("Von deiner Organisation festgelegt: die Cloud-Synchronisierung ist ausgeschaltet (Richtlinie DisableCloud).", InfoSeverity.Warning);
        }
        else if (status.State == CloudState.SignedOut)
        {
            CloudMessage.Show(status.Message, InfoSeverity.Warning);
        }

        if (!cloud.IsConnected)
        {
            if (string.IsNullOrEmpty(ServerUrlBox.Text))
            {
                ServerUrlBox.Text = cloud.ServerUrl;
            }

            if (string.IsNullOrEmpty(UserNameBox.Text))
            {
                UserNameBox.Text = cloud.UserName;
            }

            if (string.IsNullOrEmpty(DeviceNameBox.Text))
            {
                DeviceNameBox.Text = cloud.DeviceName.Length > 0 ? cloud.DeviceName : AppInfo.MachineName;
            }

            return;
        }

        CloudServerText.Text = cloud.ServerUrl;
        CloudAccountText.Text = cloud.DisplayName.Length > 0 && cloud.DisplayName != cloud.UserName ? $"{cloud.DisplayName} ({cloud.UserName})" : cloud.UserName;
        CloudDeviceText.Text = cloud.DeviceName;
        CloudStateText.Text = status.Message;
        CloudLastSyncText.Text = status.LastSync is { } last ? Ui.FormatDateTime(last) : "–";
    }

    private void ShowAccessibilityHint(bool pastes)
    {
        var needsPermission = pastes && OperatingSystem.IsMacOS() && !PasteTarget.CanSendKeys;
        AccessibilityButton.IsVisible = needsPermission;
        if (needsPermission)
        {
            AccessibilityInfo.Show("Zum Einfügen braucht ThixClip unter macOS die Erlaubnis „Bedienungshilfen“ (Systemeinstellungen › Datenschutz & Sicherheit). Bis dahin wird nur kopiert.", InfoSeverity.Warning);
        }
        else
        {
            AccessibilityInfo.Hide();
        }
    }

    private void Apply(Action change)
    {
        if (_isLoading)
        {
            return;
        }

        try
        {
            FormErrors.ShowError(this, null, "");
            PageMessage.Hide();
            change();
        }
        catch (BusinessException exception)
        {
            ShowError(exception.Message, exception.Field);
            Load();
        }
    }

    private async Task ApplyAsync(Func<Task> change)
    {
        try
        {
            FormErrors.ShowError(this, null, "");
            PageMessage.Hide();
            await change();
        }
        catch (BusinessException exception)
        {
            ShowError(exception.Message, exception.Field);
            Load();
        }
    }

    private void ShowError(string message, string? field)
    {
        if (!FormErrors.ShowError(this, field, message))
        {
            PageMessage.Show(message, InfoSeverity.Error);
        }
    }

    private void OnHotkeyResetClick(object? sender, RoutedEventArgs e) => Apply(() =>
    {
        SettingsService.SetHotkey(HotkeyService.DefaultHotkey);
        App.Instance.RegisterHotkey();
    });

    private void OnAccessibilityClick(object? sender, RoutedEventArgs e)
    {
        PasteTarget.RequestKeyPermission();
        Ui.OpenExternal("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
    }

    /// <summary>The first way: MatCloud shows a code, the browser opens, the person confirms it there; ThixClip waits (Abbrechen ends it).</summary>
    private async void OnBrowserConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_browserSignIn is not null)
        {
            return;
        }

        CloudMessage.Hide();
        FormErrors.ShowError(this, null, "");
        _browserSignIn = new CancellationTokenSource();
        ServerUrlBox.IsEnabled = DeviceNameBox.IsEnabled = BrowserConnectButton.IsEnabled = ShowPasswordSignInButton.IsEnabled = false;
        try
        {
            if (await SyncService.ConnectInBrowserAsync(ServerUrlBox.Text ?? "", DeviceNameBox.Text ?? "", start => Ui.Post(() => ShowBrowserCode(start)),
                    Ui.OpenExternal, _browserSignIn.Token))
            {
                CloudMessage.Show("Verbunden. Dein Verlauf wird jetzt mit der Cloud abgeglichen.", InfoSeverity.Success);
            }
        }
        catch (BusinessException exception)
        {
            if (!FormErrors.ShowError(this, exception.Field, exception.Message))
            {
                CloudMessage.Show(exception.Message, InfoSeverity.Error);
            }
        }
        finally
        {
            _browserSignIn.Dispose();
            _browserSignIn = null;
            ServerUrlBox.IsEnabled = DeviceNameBox.IsEnabled = BrowserConnectButton.IsEnabled = ShowPasswordSignInButton.IsEnabled = true;
            ShowSignInWay(password: false);
            LoadCloud();
        }
    }

    /// <summary>The code of a started sign-in in the browser, big, with the hint to confirm it there.</summary>
    internal void ShowBrowserCode(DeviceLinkStart start)
    {
        _browserAddress = start.VerificationUrl;
        BrowserCodeText.Text = start.Code;
        BrowserSignIn.IsVisible = PasswordSignIn.IsVisible = false;
        BrowserCode.IsVisible = true;
        FocusAfterLayout(CancelLinkButton);
    }

    /// <summary>The way to sign in: in the browser (first) or with user name and password (second).</summary>
    internal void ShowSignInWay(bool password)
    {
        BrowserCode.IsVisible = false;
        BrowserSignIn.IsVisible = !password;
        PasswordSignIn.IsVisible = password;
        BrowserConnectButton.IsDefault = !password;
        ConnectButton.IsDefault = password;
    }

    private void OnShowPasswordSignInClick(object? sender, RoutedEventArgs e)
    {
        ShowSignInWay(password: true);
        FocusAfterLayout(string.IsNullOrEmpty(UserNameBox.Text) ? UserNameBox : PasswordBox);
    }

    private void OnShowBrowserSignInClick(object? sender, RoutedEventArgs e)
    {
        ShowSignInWay(password: false);
        FocusAfterLayout(BrowserConnectButton);
    }

    /// <summary>A control that has just become visible takes the focus once it is laid out (before that it cannot).</summary>
    private static void FocusAfterLayout(Control control) =>
        Dispatcher.UIThread.Post(() => control.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);

    private void OnOpenLinkClick(object? sender, RoutedEventArgs e)
    {
        if (_browserAddress is not null)
        {
            Ui.OpenExternal(_browserAddress);
        }
    }

    private void OnCancelLinkClick(object? sender, RoutedEventArgs e) => _browserSignIn?.Cancel();

    /// <summary>The second way: user name and password of a local account of MatCloud.</summary>
    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        CloudMessage.Hide();
        try
        {
            FormErrors.ShowError(this, null, "");
            await SyncService.ConnectAsync(ServerUrlBox.Text ?? "", UserNameBox.Text ?? "", PasswordBox.Text ?? "", DeviceNameBox.Text ?? "");
            PasswordBox.Text = "";
            CloudMessage.Show("Verbunden. Dein Verlauf wird jetzt mit der Cloud abgeglichen.", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            if (!FormErrors.ShowError(this, exception.Field, exception.Message))
            {
                CloudMessage.Show(exception.Message, InfoSeverity.Error);
            }
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            LoadCloud();
        }
    }

    private void OnSyncNowClick(object? sender, RoutedEventArgs e) => SyncService.Kick();

    private void OnOpenCloudClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(SettingsService.Get().Cloud.ServerUrl);

    private async void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(this), "Von der Cloud abmelden?",
                "Dieser Computer wird aus deinem Cloud-Konto entfernt und gleicht nicht mehr ab. Der Verlauf auf diesem Computer bleibt erhalten.",
                "Abmelden", isDestructive: true))
        {
            return;
        }

        await SyncService.DisconnectAsync();
        CloudMessage.Show("Abgemeldet.", InfoSeverity.Info);
        LoadCloud();
    }

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            ShowUpdateResult(await UpdateCoordinator.CheckAsync());
        }
        catch (BusinessException exception)
        {
            UpdateMessage.Show(exception.Message, InfoSeverity.Error);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = UpdateService.CanUpdate;
            LastCheckText.Text = UpdateService.LastCheck is { } last ? Ui.FormatDateTime(last) : "–";
        }
    }

    private void ShowUpdateResult(UpdateCheckResult result)
    {
        _availableUpdate = result.Manifest;
        InstallUpdateButton.IsVisible = result.IsUpdateAvailable;
        UpdateMessage.Show(result.Message, result.IsUpdateAvailable ? InfoSeverity.Success : InfoSeverity.Info);
    }

    private async void OnInstallUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
        {
            return;
        }

        InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = false;
        UpdateProgress.IsVisible = true;
        try
        {
            await UpdateCoordinator.InstallAsync(_availableUpdate, new Progress<double>(value => UpdateProgress.Value = value));
        }
        catch (Exception exception) when (exception is BusinessException or HttpRequestException or IOException)
        {
            UpdateMessage.Show($"Das Update ist fehlgeschlagen: {exception.Message}", InfoSeverity.Error);
            InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = true;
            UpdateProgress.IsVisible = false;
        }
    }

    private void OnReleasePageClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(UpdateService.ReleasePageUrl(AppInfo.IsLocalBuild ? "release" : AppInfo.Channel));

    private void OnOpenDataFolderClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(AppPaths.DataDirectory);

    private void OnOpenLogFolderClick(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Ui.OpenExternal(AppPaths.LogDirectory);
    }
}
