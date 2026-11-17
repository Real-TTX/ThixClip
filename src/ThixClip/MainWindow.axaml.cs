using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MatCloud.Contract.Clip;
using MatDesktop;
using ThixClip.Controls;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Core.Platform;
using ThixClip.Pages;

namespace ThixClip;

/// <summary>
/// The main window. Built once; the shortcut shows it centred on the screen the user works on, with the search field
/// focused and the newest entry chosen, and hides it again. Everything works with the keyboard:
///   typing = search, ↑ ↓ PageUp PageDown = choose, Enter = paste (or copy), Esc = clear search / close,
///   Cmd/Ctrl+C copy, +D favourite, +P pin, +Delete (macOS Cmd+Backspace) delete, +F search, +1/2/3 sections,
///   +, settings, +B fold the menu, +O open the entry, +Shift+P pause / resume recording, +Shift+Delete (macOS Cmd+Shift+Backspace) "Verlauf löschen" as in a browser,
///   Alt+1 ... Alt+9 paste the n-th entry at once.
/// The chrome is MatDesktop's: MatTitleBar (DESK-03) and MatNav (DESK-04, folds to a rail of 68 px).
/// </summary>
public partial class MainWindow : MatWindow
{
    private readonly HistoryPage _history = new();
    private readonly Lazy<SettingsPage> _settings = new(() => new SettingsPage());
    private readonly Lazy<AboutPage> _about = new(() => new AboutPage());
    private readonly DispatcherTimer _sizeTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly MatNavItem _navHistory;
    private readonly MatNavItem _navImages;
    private readonly MatNavItem _navFavorites;
    private readonly MatNavItem _navSettings;
    private bool _isPasting;

    public MainWindow()
    {
        InitializeComponent();
        UseOwnTitleBar();
        var settings = SettingsService.Get();
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;

        // The title bar: the X (and Esc, Alt+F4) only hides, ThixClip keeps running in the tray / menu bar (DESK-06).
        TitleBar.Logo = Ui.Logo;
        TitleBar.CloseRequested += (_, e) =>
        {
            e.Handled = true;
            HideWindow(returnToPrevious: true);
        };

        // The menu: the sections, Settings below the line, the version at the foot (= About), folded or open as last time.
        var command = Ui.CommandKey.Name;
        _navHistory = new MatNavItem("clock", "Verlauf", $"{command}+1") { IsCurrent = true };
        _navImages = new MatNavItem("image", "Bilder", $"{command}+2");
        _navFavorites = new MatNavItem("star", "Favoriten", $"{command}+3");
        _navSettings = new MatNavItem("settings", "Einstellungen", $"{command}+,");
        Sidebar.Items.AddRange([_navHistory, _navImages, _navFavorites]);
        Sidebar.Settings = _navSettings;
        Sidebar.Version = AppInfo.Version;
        Sidebar.Channel = AppInfo.Channel;
        Sidebar.TitleBar = TitleBar;
        Sidebar.IsFolded = settings.IsSidebarCollapsed;
        Sidebar.FoldShortcut = $"{command}+B"; // DESK-07: named in the tooltip of the fold button
        Sidebar.ItemInvoked += (_, item) => OnNavInvoked(item);
        Sidebar.AboutRequested += (_, _) => ShowAbout();
        Sidebar.UpdateRequested += (_, _) => ShowAbout(); // About has the way to install it
        Sidebar.FoldChanged += (_, folded) =>
        {
            SettingsService.Remember(current => current with { IsSidebarCollapsed = folded });
            ApplySidebar();
        };
        AddToMenuFoot();

        _history.PasteRequested += async (_, row) => await PasteAsync(row);
        _history.Copied += (_, _) =>
        {
            if (SettingsService.Get().CloseAfterCopy)
            {
                HideWindow(returnToPrevious: true);
            }
        };
        PageHost.Content = _history;

        // Searching is a filter over the history in memory: it runs on every key, there is nothing to wait for.
        SearchBox.TextChanged += (_, _) =>
        {
            if (PageHost.Content != _history && !string.IsNullOrEmpty(SearchBox.Text))
            {
                ShowSection(_history.Section);
            }

            _history.SetSearch(SearchBox.Text ?? "");
        };

        _sizeTimer.Tick += (_, _) =>
        {
            _sizeTimer.Stop();
            if (WindowState == WindowState.Normal)
            {
                SettingsService.Remember(current => current with { WindowWidth = Width, WindowHeight = Height });
            }
        };
        Resized += (_, _) =>
        {
            _sizeTimer.Stop();
            _sizeTimer.Start();
        };

        // A low window: the retention card shrinks to its choice, so the sections never hide behind it.
        SizeChanged += (_, e) => RetentionDescription.IsVisible = RetentionHint.IsVisible = e.NewSize.Height >= 640;

        RecordingService.Changed += (_, _) => Ui.Post(RefreshPause);
        UpdateCoordinator.Checked += (_, _) => Ui.Post(ApplySidebar);
        RefreshPause();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => HideWhenAnotherAppComesFirst();

        InitializeRetention();
        ApplySidebar();
        RefreshSidebar();
        SettingsService.Changed += (_, _) => Ui.Post(RefreshSidebar);
        SyncService.StatusChanged += (_, _) => Ui.Post(RefreshCloudStatus);
    }

    /// <summary>Set by App when the application ends: then the window really closes.</summary>
    public bool IsExiting { get; set; }

    /// <summary>The native window handle (Windows), needed to tell "ThixClip is in front" from another app.</summary>
    public IntPtr NativeHandle { get; private set; }

    /// <summary>True while the window is shown and in front (the shortcut then hides it).</summary>
    public bool IsActiveQuickWindow { get; private set; }

    /// <summary>The menu at the left is folded to its icons (Ctrl/Cmd+B, the button at its foot); remembered across starts.</summary>
    public bool IsSidebarCollapsed => Sidebar.IsFolded;

    /// <summary>The button at the foot of the menu that folds and opens it.</summary>
    public Button SidebarToggle => Sidebar.FoldButton;

    /// <summary>The version at the foot of the menu (<c>Version 1.0.0 · release</c>, SETUP-07): it opens About.</summary>
    public Button VersionButton => Sidebar.VersionButton;

    /// <summary>The retention at the foot of the menu (hidden while the menu is folded).</summary>
    public Border RetentionCard { get; private set; } = null!;

    /// <summary>The state of the cloud at the foot of the menu (its words hidden while the menu is folded).</summary>
    public TextBlock CloudText { get; private set; } = null!;

    private ComboBox RetentionChoice { get; set; } = null!;

    private TextBlock RetentionDescription { get; set; } = null!;

    private TextBlock RetentionHint { get; set; } = null!;

    private Button CloudStatus { get; set; } = null!;

    private Ellipse CloudDot { get; set; } = null!;

    public void ToggleSidebar() => Sidebar.IsFolded = !Sidebar.IsFolded;

    /// <summary>"Verlauf löschen" (Ctrl/Cmd+Shift+Delete as in a browser): the history page asks for the range.</summary>
    public async Task ClearHistoryAsync()
    {
        if (PageHost.Content != _history)
        {
            ShowSection(_history.Section);
        }

        await _history.AskToClearAsync();
        SearchBox.Focus();
    }

    /// <summary>The shortcut was pressed (UI thread).</summary>
    public void ToggleFromHotkey()
    {
        if (IsVisible && IsActive)
        {
            HideWindow(returnToPrevious: true);
            return;
        }

        OpenForPicking();
    }

    /// <summary>Tray, second start, Dock: show the window (the app in front is remembered for pasting).</summary>
    public void ShowFromTray(bool rememberForeground)
    {
        if (rememberForeground && !IsVisible)
        {
            PasteTarget.RememberForeground(NativeHandle);
        }

        OpenForPicking();
        if (!SettingsService.Get().WelcomeShown)
        {
            SettingsService.Remember(settings => settings with { WelcomeShown = true });
        }
    }

    /// <summary>
    /// Creates the native window and its visuals once, invisibly, so that the first shortcut is as fast as every later
    /// one (start at sign-in with "--autostart").
    /// </summary>
    public void Prewarm()
    {
        Opacity = 0;
        ShowInTaskbar = false;
        Position = new PixelPoint(-32000, -32000);
        Show();
        NativeHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Dispatcher.UIThread.Post(() =>
        {
            Hide();
            Opacity = 1;
            ShowInTaskbar = true;
        }, DispatcherPriority.Background);
    }

    public void ShowSettings(string? tab = null)
    {
        Sidebar.Select(_navSettings);
        PageHost.Content = _settings.Value;
        if (tab is not null)
        {
            _settings.Value.SelectTab(tab);
        }
    }

    public void ShowAbout()
    {
        Sidebar.Select(null);
        PageHost.Content = _about.Value;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NativeHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The X (and Alt+F4) only hides: ThixClip keeps running in the tray / menu bar.
        if (!IsExiting)
        {
            e.Cancel = true;
            HideWindow(returnToPrevious: true);
        }

        base.OnClosing(e);
    }

    private void OpenForPicking()
    {
        if (!IsVisible)
        {
            PlaceOnActiveScreen();
            SearchBox.Text = "";
            if (PageHost.Content != _history)
            {
                ShowSection(_history.Section);
            }

            _history.ResetForOpen();
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        WindowActivation.BringToFront(NativeHandle);
        IsActiveQuickWindow = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void HideWindow(bool returnToPrevious)
    {
        IsActiveQuickWindow = false;
        Hide();
        if (returnToPrevious)
        {
            PasteTarget.ReturnToPrevious();
        }
    }

    /// <summary>The sign-in in the browser waits: the window stays next to the browser, so the code can be compared there.</summary>
    private bool IsSigningInInBrowser => _settings.IsValueCreated && _settings.Value.IsSigningInInBrowser;

    private void HideWhenAnotherAppComesFirst()
    {
        IsActiveQuickWindow = false;
        if (!IsVisible || _isPasting || OwnedWindows.Count > 0 || !SettingsService.Get().HideOnDeactivate || IsSigningInInBrowser)
        {
            return;
        }

        // A popup of this window (combo box, tool tip) does not count; give the system a moment to settle.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsActive && IsVisible && OwnedWindows.Count == 0)
            {
                Hide();
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>Copies the entry, hides ThixClip and pastes it into the app that was in front.</summary>
    private async Task PasteAsync(ClipRowViewModel row)
    {
        try
        {
            ClipService.CopyToClipboard(row.Id);
        }
        catch (Exception exception) when (exception is BusinessException or IOException)
        {
            _history.ShowMessage(exception.Message, isError: true);
            return;
        }

        var settings = SettingsService.Get();
        _isPasting = true;
        try
        {
            HideWindow(returnToPrevious: false);
            if (settings.PasteOnSelect && PasteTarget.CanSendKeys)
            {
                await PasteTarget.PasteAsync();
            }
            else
            {
                PasteTarget.ReturnToPrevious();
            }
        }
        finally
        {
            _isPasting = false;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var command = Ui.CommandKey.Modifier;
        var withCommand = e.KeyModifiers.HasFlag(command);
        var onHistory = PageHost.Content == _history;
        var inSearch = SearchBox.IsFocused;
        // The list keys work wherever the focus is on the history page except in a field or a drop-down: after a deletion
        // the focused row is gone (focus on nothing), after a click it is on a button of the detail pane.
        var focused = FocusManager?.GetFocusedElement();
        var inList = focused is not (TextBox or ComboBox or ComboBoxItem or ToggleSwitch);

        switch (e.Key)
        {
            case Key.Escape when OwnedWindows.Count == 0:
                e.Handled = true;
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Text = "";
                }
                else if (!onHistory)
                {
                    ShowSection(_history.Section);
                }
                else
                {
                    HideWindow(returnToPrevious: true);
                }

                return;

            case Key.F when withCommand:
                e.Handled = true;
                SearchBox.Focus();
                SearchBox.SelectAll();
                return;

            case Key.D1 or Key.D2 or Key.D3 when withCommand:
                e.Handled = true;
                ShowSection(e.Key == Key.D1 ? ClipSection.History : e.Key == Key.D2 ? ClipSection.Images : ClipSection.Favorites);
                SearchBox.Focus();
                return;

            case Key.OemComma when withCommand:
                e.Handled = true;
                ShowSettings();
                return;

            case Key.B when withCommand:
                e.Handled = true;
                ToggleSidebar();
                return;

            case Key.P when withCommand && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;
                _ = App.Instance.TogglePauseAsync(this);
                return;

            case Key.Delete when withCommand && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.Back when withCommand && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && OperatingSystem.IsMacOS():
                e.Handled = true;
                _ = ClearHistoryAsync();
                return;
        }

        if (!onHistory || (!inSearch && !inList))
        {
            return;
        }

        // Alt+1 ... Alt+9: paste the n-th entry straight away.
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is >= Key.D1 and <= Key.D9)
        {
            e.Handled = true;
            if (_history.RowAt(e.Key - Key.D1) is { } row)
            {
                _ = PasteAsync(row);
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                _history.MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                _history.MoveSelection(-1);
                break;
            case Key.PageDown:
                e.Handled = true;
                _history.MoveSelection(8);
                break;
            case Key.PageUp:
                e.Handled = true;
                _history.MoveSelection(-8);
                break;
            case Key.Home when withCommand || !inSearch:
                e.Handled = true;
                _history.SelectFirst();
                break;
            case Key.End when withCommand || !inSearch:
                e.Handled = true;
                _history.SelectLast();
                break;
            case Key.Enter when _history.Selected is { } selected:
                e.Handled = true;
                _ = PasteAsync(selected);
                break;
            case Key.C when withCommand && (!inSearch || SearchBox.SelectionStart == SearchBox.SelectionEnd):
                e.Handled = true;
                _history.CopySelected();
                break;
            case Key.D when withCommand:
                e.Handled = true;
                _history.ToggleFavoriteOfSelected();
                break;
            case Key.P when withCommand:
                e.Handled = true;
                _history.TogglePinOfSelected();
                break;
            case Key.O when withCommand:
                e.Handled = true;
                _history.OpenSelected();
                break;
            case Key.Delete when withCommand:
            case Key.Back when withCommand && OperatingSystem.IsMacOS():
            case Key.Delete when e.KeyModifiers == KeyModifiers.None && (!inSearch || string.IsNullOrEmpty(SearchBox.Text)):
                e.Handled = true;
                _history.DeleteSelected(keepListFocus: !inSearch);
                break;
        }
    }

    public void ShowSection(ClipSection section)
    {
        Sidebar.Select(section switch
        {
            ClipSection.Images => _navImages,
            ClipSection.Favorites => _navFavorites,
            _ => _navHistory,
        });
        PageHost.Content = _history;
        _history.SetSection(section);
    }

    private void OnNavInvoked(MatNavItem item)
    {
        if (item == _navSettings)
        {
            ShowSettings();
        }
        else
        {
            ShowSection(item == _navImages ? ClipSection.Images : item == _navFavorites ? ClipSection.Favorites : ClipSection.History);
        }
    }

    /// <summary>
    /// What ThixClip shows at the foot of its menu above the version (MatNav.Foot): the retention (mockup "Speicherdauer")
    /// and the state of the cloud. A waiting update is MatNav's own link above the version.
    /// </summary>
    private void AddToMenuFoot()
    {
        RetentionDescription = new TextBlock { Text = "Wie lange sollen Zwischenablage-Einträge gespeichert werden?" }.Themed("MatText.Muted");
        RetentionChoice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(RetentionChoice, "Speicherdauer");
        RetentionHint = new TextBlock { Text = "Favoriten und angeheftete Einträge bleiben immer." }.Themed("MatText.Faint");
        RetentionCard = new Border
        {
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Speicherdauer", FontWeight = FontWeight.SemiBold },
                    RetentionDescription,
                    RetentionChoice,
                    RetentionHint,
                },
            },
        }.Themed("MatCard");

        CloudDot = new Ellipse { Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -1) };
        CloudText = new TextBlock { FontSize = MatTokens.Type.FsSm, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 };
        CloudStatus = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                Children = { new Panel { Children = { new MatIcon { Icon = "cloud", Size = 18 }, CloudDot } }, CloudText },
            },
        }.Themed("MatButton.Ghost");
        CloudStatus.Click += (_, _) => ShowSettings(SettingsPage.CloudTab);

        Sidebar.Foot = new StackPanel { Spacing = 10, Margin = new Thickness(0, 12, 0, 10), Children = { RetentionCard, CloudStatus } };
    }

    private void InitializeRetention()
    {
        RetentionChoice.ItemsSource = ClipContract.RetentionChoices.Select(Ui.RetentionText).ToList();
        RetentionChoice.SelectionChanged += async (_, _) =>
        {
            if (RetentionChoice.SelectedIndex < 0)
            {
                return;
            }

            var days = ClipContract.RetentionChoices[RetentionChoice.SelectedIndex];
            if (days == SettingsService.Get().RetentionDays)
            {
                return;
            }

            try
            {
                await SettingsService.SetRetentionAsync(days);
            }
            catch (BusinessException exception)
            {
                _history.ShowMessage(exception.Message, isError: true);
                RefreshSidebar();
            }
        };
    }

    private void RefreshSidebar()
    {
        var settings = SettingsService.Get();
        var index = ClipContract.RetentionChoices.ToList().IndexOf(settings.RetentionDays);
        if (RetentionChoice.SelectedIndex != index)
        {
            RetentionChoice.SelectedIndex = index;
        }

        RetentionChoice.IsEnabled = !AccessPolicy.IsConfigurationLocked;
        RetentionHint.Text = AccessPolicy.IsConfigurationLocked
            ? "Von deiner Organisation festgelegt."
            : settings.Cloud.IsConnected
                ? "Gilt für alle Geräte des Cloud-Kontos. Favoriten und angeheftete Einträge bleiben immer."
                : "Favoriten und angeheftete Einträge bleiben immer.";
        HotkeyCaps.Keys = settings.Hotkey;
        RefreshCloudStatus();
    }

    /// <summary>
    /// Full menu: the retention, the cloud state in words and a waiting update (MatNav's link above the version). Folded
    /// (MatNav shows the icons, the update as an icon and an info icon for About): the cloud icon with its dot.
    /// </summary>
    private void ApplySidebar()
    {
        var folded = IsSidebarCollapsed;
        RetentionCard.IsVisible = !folded;
        CloudText.IsVisible = !folded;
        CloudStatus.HorizontalContentAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        // An update waiting: MatNav's link above the version (an icon when folded); a click shows About with the way to install it.
        Sidebar.UpdateVersion = UpdateService.LastResult is { IsUpdateAvailable: true, Manifest: { } manifest } ? manifest.Version : null;
        RefreshCloudStatus();
    }

    private void RefreshCloudStatus()
    {
        var cloud = SettingsService.Get().Cloud;
        var status = SyncService.Status;
        (CloudText.Text, var dot) = status.State switch
        {
            CloudState.Online => ($"Synchronisiert · {cloud.UserName}", "Mat.Success"),
            CloudState.Syncing => ("Synchronisiere …", "Mat.Accent"),
            CloudState.Connecting => ("Verbinde …", "Mat.Accent"),
            CloudState.Offline => ("Cloud nicht erreichbar", "Mat.Warning"),
            CloudState.SignedOut => ("Abgemeldet – neu anmelden", "Mat.Danger"),
            _ => cloud.IsConnected ? ("Cloud verbunden", "Mat.Success") : ("Mit Cloud verbinden", (string?)null),
        };
        CloudDot.IsVisible = dot is not null;
        if (dot is not null)
        {
            CloudDot.Use(Shape.FillProperty, dot);
        }

        var tip = status.LastSync is { } last ? $"{status.Message} (zuletzt {last:HH:mm:ss})" : status.Message;
        ToolTip.SetTip(CloudStatus, IsSidebarCollapsed ? $"{CloudText.Text}\n{tip}" : tip);
        AutomationProperties.SetName(CloudStatus, CloudText.Text);
    }

    /// <summary>Centred on the screen with the mouse (Windows) or the current one, never larger than its work area.</summary>
    private void PlaceOnActiveScreen()
    {
        var screen = WindowActivation.CursorPosition() is { } cursor
            ? Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y))
            : null;
        screen ??= Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var settings = SettingsService.Get();
        Width = Math.Min(settings.WindowWidth, area.Width / scaling - 32);
        Height = Math.Min(settings.WindowHeight, area.Height / scaling - 32);
        Position = new PixelPoint(
            area.X + (int)((area.Width - Width * scaling) / 2),
            area.Y + (int)((area.Height - Height * scaling) / 2.4));
    }

    /// <summary>
    /// The pause button of the header: recording shows "Pausieren" (quiet), a pause "Fortsetzen" with its end in the
    /// colours of a warning, so nobody forgets that copies are not recorded right now.
    /// </summary>
    private void RefreshPause()
    {
        var paused = RecordingService.IsPaused;
        if (paused)
        {
            PauseButton.Themed("MatButton.Warning");
        }
        else
        {
            PauseButton.ClearValue(ThemeProperty); // the normal framed button, always in sight
        }

        PauseLabel.Icon = paused ? "play" : "pause";
        var until = RecordingService.PausedUntil;
        var state = RecordingService.Describe();
        PauseLabel.Text = !paused ? "Pausieren"
            : until is { } end ? $"Fortsetzen · pausiert bis {end.ToLocalTime():HH:mm}"
            : $"Fortsetzen · {(state.Length > 0 ? char.ToLowerInvariant(state[0]) + state[1..] : state)}";
        ToolTip.SetTip(PauseButton, paused ? $"Aufzeichnung fortsetzen ({Ui.PauseShortcut})" : $"Aufzeichnung pausieren … ({Ui.PauseShortcut})");
        AutomationProperties.SetName(PauseButton, paused ? "Aufzeichnung fortsetzen" : "Aufzeichnung pausieren");
    }

    private async void OnPauseClick(object? sender, RoutedEventArgs e) => await App.Instance.TogglePauseAsync(this);

    private void OnHotkeyBadgeClick(object? sender, RoutedEventArgs e) => ShowSettings(SettingsPage.GeneralTab);
}
