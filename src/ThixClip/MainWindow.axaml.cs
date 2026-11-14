using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Core.Platform;
using ThixClip.Pages;
using ThixClip.Shared;

namespace ThixClip;

/// <summary>
/// The main window. Built once; the shortcut shows it centred on the screen the user works on, with the search field
/// focused and the newest entry chosen, and hides it again. Everything works with the keyboard:
///   typing = search, ↑ ↓ PageUp PageDown = choose, Enter = paste (or copy), Esc = clear search / close,
///   Cmd/Ctrl+C copy, +D favourite, +P pin, +Delete (macOS Cmd+Backspace) delete, +F search, +1/2/3 sections,
///   +, settings, Alt+1 ... Alt+9 paste the n-th entry at once.
/// </summary>
public partial class MainWindow : ThemedWindow
{
    private readonly HistoryPage _history = new();
    private readonly Lazy<SettingsPage> _settings = new(() => new SettingsPage());
    private readonly Lazy<AboutPage> _about = new(() => new AboutPage());
    private readonly DispatcherTimer _sizeTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _isPasting;

    public MainWindow()
    {
        InitializeComponent();
        var settings = SettingsService.Get();
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;

        if (OperatingSystem.IsMacOS())
        {
            // The traffic lights stay where macOS draws them; the logo moves right of them.
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = 68;
            CaptionButtons.IsVisible = false;
            HeaderGrid.Margin = new Thickness(84, 0, 18, 0);
            HeaderGrid.ColumnDefinitions[0].Width = new GridLength(186);
        }
        else
        {
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
            ExtendClientAreaTitleBarHeightHint = 0;
        }

        _history.PasteRequested += async (_, row) => await PasteAsync(row);
        PageHost.Content = _history;
        NavHistory.IsChecked = true;

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
            MaximizeIcon.Data = Ui.Icon(WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize");
        };

        // A low window: the retention card shrinks to its choice, so the sections never hide behind it.
        SizeChanged += (_, e) =>
        {
            RetentionDescription.IsVisible = RetentionHint.IsVisible = e.NewSize.Height >= 640;
            var sidebar = new GridLength(e.NewSize.Width < 900 ? 210 : 248);
            BodyGrid.ColumnDefinitions[0].Width = sidebar;
            if (!OperatingSystem.IsMacOS())
            {
                HeaderGrid.ColumnDefinitions[0].Width = sidebar;
            }
        };

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => HideWhenAnotherAppComesFirst();

        InitializeRetention();
        RefreshSidebar();
        SettingsService.Changed += (_, _) => Ui.Post(RefreshSidebar);
        SyncService.StatusChanged += (_, _) => Ui.Post(RefreshCloudStatus);
        var command = Ui.CommandKey.Name;
        ToolTip.SetTip(NavHistory, $"Verlauf ({command}+1)");
        ToolTip.SetTip(NavImages, $"Bilder ({command}+2)");
        ToolTip.SetTip(NavFavorites, $"Favoriten ({command}+3)");
        ToolTip.SetTip(NavSettings, $"Einstellungen ({command}+,)");
    }

    /// <summary>Set by App when the application ends: then the window really closes.</summary>
    public bool IsExiting { get; set; }

    /// <summary>The native window handle (Windows), needed to tell "ThixClip is in front" from another app.</summary>
    public IntPtr NativeHandle { get; private set; }

    /// <summary>True while the window is shown and in front (the shortcut then hides it).</summary>
    public bool IsActiveQuickWindow { get; private set; }

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
        SelectNav(NavSettings);
        PageHost.Content = _settings.Value;
        if (tab is not null)
        {
            _settings.Value.SelectTab(tab);
        }
    }

    public void ShowAbout()
    {
        SelectNav(null);
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

    private void HideWhenAnotherAppComesFirst()
    {
        IsActiveQuickWindow = false;
        if (!IsVisible || _isPasting || OwnedWindows.Count > 0 || !SettingsService.Get().HideOnDeactivate)
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
        var inList = FocusManager?.GetFocusedElement() is ListBoxItem or ListBox;

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
            case Key.Delete when withCommand:
            case Key.Back when withCommand && OperatingSystem.IsMacOS():
                e.Handled = true;
                _history.DeleteSelected();
                break;
        }
    }

    public void ShowSection(ClipSection section)
    {
        SelectNav(section switch
        {
            ClipSection.Images => NavImages,
            ClipSection.Favorites => NavFavorites,
            _ => NavHistory,
        });
        PageHost.Content = _history;
        _history.SetSection(section);
    }

    private void SelectNav(ToggleButton? selected)
    {
        foreach (var item in new[] { NavHistory, NavImages, NavFavorites, NavSettings })
        {
            item.IsChecked = item == selected;
        }
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        switch ((sender as Control)?.Tag as string)
        {
            case "Images":
                ShowSection(ClipSection.Images);
                break;
            case "Favorites":
                ShowSection(ClipSection.Favorites);
                break;
            case "Settings":
                ShowSettings();
                break;
            default:
                ShowSection(ClipSection.History);
                break;
        }
    }

    private void InitializeRetention()
    {
        RetentionChoice.ItemsSource = SyncContract.RetentionChoices.Select(Ui.RetentionText).ToList();
        RetentionChoice.SelectionChanged += async (_, _) =>
        {
            if (RetentionChoice.SelectedIndex < 0)
            {
                return;
            }

            var days = SyncContract.RetentionChoices[RetentionChoice.SelectedIndex];
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
        var index = SyncContract.RetentionChoices.ToList().IndexOf(settings.RetentionDays);
        if (RetentionChoice.SelectedIndex != index)
        {
            RetentionChoice.SelectedIndex = index;
        }

        RetentionChoice.IsEnabled = !AccessPolicy.IsConfigurationLocked;
        RetentionHint.Text = settings.Cloud.IsConnected
            ? "Gilt für alle Geräte des Cloud-Kontos. Favoriten und angeheftete Einträge bleiben immer."
            : "Favoriten und angeheftete Einträge bleiben immer.";
        HotkeyCaps.Keys = settings.Hotkey;
        VersionButton.Content = $"Version {AppInfo.Version}";
        RefreshCloudStatus();
    }

    private void RefreshCloudStatus()
    {
        var cloud = SettingsService.Get().Cloud;
        var status = SyncService.Status;
        (CloudText.Text, var dot) = status.State switch
        {
            CloudState.Online => ($"Synchronisiert · {cloud.UserName}", "SuccessBrush"),
            CloudState.Syncing => ("Synchronisiere …", "AccentBrush"),
            CloudState.Connecting => ("Verbinde …", "AccentBrush"),
            CloudState.Offline => ("Cloud nicht erreichbar", "WarningBrush"),
            CloudState.SignedOut => ("Abgemeldet – neu anmelden", "DangerBrush"),
            _ => cloud.IsConnected ? ("Cloud verbunden", "SuccessBrush") : ("Mit Cloud verbinden", (string?)null),
        };
        CloudDot.IsVisible = dot is not null;
        if (dot is not null)
        {
            CloudDot.Fill = Ui.Brush(dot);
        }

        ToolTip.SetTip(CloudStatus, status.LastSync is { } last ? $"{status.Message} (zuletzt {last:HH:mm:ss})" : status.Message);
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

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not TextBox && e.ClickCount == 1)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnHeaderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Border or Grid or StackPanel)
        {
            OnMaximizeClick(sender, e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => HideWindow(returnToPrevious: true);

    private void OnHotkeyBadgeClick(object? sender, RoutedEventArgs e) => ShowSettings(SettingsPage.GeneralTab);

    private void OnCloudStatusClick(object? sender, RoutedEventArgs e) => ShowSettings(SettingsPage.CloudTab);

    private void OnVersionClick(object? sender, RoutedEventArgs e) => ShowAbout();
}
