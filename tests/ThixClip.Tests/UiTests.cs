using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Pages;
using ThixClip.Shared;

[assembly: AvaloniaTestApplication(typeof(ThixClip.Tests.UiTestApp))]

namespace ThixClip.Tests;

/// <summary>The real app (App.axaml, themes, pages) rendered headless with Skia: real pixels, no screen needed.</summary>
public static class UiTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// The main window driven by the keyboard like a user would, and screenshots of every page in light and dark
/// (set THIXCLIP_SCREENSHOTS to a folder to keep them; docs/images has the ones of the README).
/// </summary>
public sealed class UiTests
{
    private static readonly string? ShotFolder = Environment.GetEnvironmentVariable("THIXCLIP_SCREENSHOTS");

    [AvaloniaFact]
    public void Typing_searches_arrows_choose_and_shortcuts_act()
    {
        using var data = new TestData();
        SampleHistory.Create();
        var window = Open();

        // Typing goes into the search field (it has the focus) and filters at once.
        Assert.True(window.SearchBox.IsFocused, "the search field has the focus when the window opens");
        window.KeyTextInput("konzept");
        var history = (HistoryPage)window.PageHost.Content!;
        WaitUntil(window, () => history.Selected?.Entry.Kind == ClipKind.Image);
        Assert.Equal("konzept", window.SearchBox.Text);
        Assert.True(history.Selected!.Entry.Kind == ClipKind.Image,
            $"{history.SubtitleText.Text} | {string.Join(" / ", history.ClipList.ItemsSource!.OfType<ClipRowViewModel>().Select(row => row.PreviewText))}");

        // Esc clears the search, arrows walk the list (skipping the headings).
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle(window);
        Assert.Equal("", window.SearchBox.Text);
        var first = history.Selected!.Id;
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Settle(window);
        Assert.NotEqual(first, history.Selected!.Id);

        // Ctrl/Cmd+D marks a favourite (the link, third entry), the Favoriten section shows it.
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Settle(window);
        var chosen = history.Selected!.Id;
        Assert.Equal(ClipKind.Link, history.Selected.Entry.Kind);
        window.KeyPressQwerty(PhysicalKey.D, CommandModifier);
        Settle(window);
        Assert.True(ClipService.Get(chosen)!.IsFavorite);
        window.KeyPressQwerty(PhysicalKey.Digit3, CommandModifier);
        Settle(window);
        Assert.Equal(ClipSection.Favorites, history.Section);
        Assert.Contains(chosen, history.ClipList.ItemsSource!.OfType<ClipRowViewModel>().Select(row => row.Id));
        Assert.True(history.Selected!.IsFavorite);

        // Ctrl/Cmd+P pins, Ctrl/Cmd+1 goes back to the history: pinned entries lead.
        window.KeyPressQwerty(PhysicalKey.Digit1, CommandModifier);
        Settle(window);
        window.KeyPressQwerty(PhysicalKey.End, CommandModifier);
        Settle(window);
        var last = history.Selected!.Id;
        window.KeyPressQwerty(PhysicalKey.P, CommandModifier);
        Settle(window);
        Assert.Equal(last, ClipService.Query(new ClipQuery())[0].Id);

        window.Close();
    }

    [AvaloniaFact]
    public void Screenshots_of_every_page_in_light_and_dark()
    {
        using var data = new TestData();
        SampleHistory.Create();
        foreach (var (variant, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var window = Open();
            var history = (HistoryPage)window.PageHost.Content!;
            history.MoveSelection(1); // the To-do picture, like the mockup
            Settle(window, waitForImages: true);
            Save(window, $"history-{suffix}");

            window.SearchBox.Text = "feedback";
            Settle(window, waitForImages: true);
            Save(window, $"search-{suffix}");
            window.SearchBox.Text = "";

            window.ShowSettings();
            Settle(window);
            Save(window, $"settings-{suffix}");
            window.ShowSettings(SettingsPage.CloudTab);
            Settle(window);
            Save(window, $"cloud-{suffix}");
            window.ShowAbout();
            Settle(window);
            Save(window, $"about-{suffix}");
            window.Close();
        }

        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    [AvaloniaFact]
    public void The_menu_folds_to_its_icons_and_stays_folded()
    {
        using var data = new TestData();
        SampleHistory.Create();
        var window = Open();
        var history = (HistoryPage)window.PageHost.Content!;
        var full = window.Sidebar.Bounds.Width;
        var listWidth = history.ClipList.Bounds.Width;

        // Ctrl/Cmd+B folds the menu: icons only, the page gets the room, the search field keeps the focus.
        window.KeyPressQwerty(PhysicalKey.B, CommandModifier);
        Settle(window);
        Assert.True(window.IsSidebarCollapsed);
        Assert.Equal(68, window.Sidebar.Bounds.Width);
        Assert.False(window.RetentionCard.IsVisible);
        Assert.False(window.CloudText.IsVisible);
        Assert.True(history.ClipList.Bounds.Width > listWidth + 150, $"{listWidth} -> {history.ClipList.Bounds.Width}");
        Assert.True(window.SearchBox.IsFocused);
        Assert.True(SettingsService.Get().IsSidebarCollapsed, "the choice is remembered");
        Save(window, "menu-folded");
        window.Close();

        // A new start comes folded; the button at the foot of the menu unfolds it.
        var again = Open();
        Assert.True(again.IsSidebarCollapsed);
        again.SidebarToggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(again);
        Assert.False(again.IsSidebarCollapsed);
        Assert.Equal(full, again.Sidebar.Bounds.Width);
        Assert.True(again.RetentionCard.IsVisible);
        Assert.False(SettingsService.Get().IsSidebarCollapsed);
        again.Close();
    }

    [AvaloniaFact]
    public void Ctrl_shift_delete_deletes_a_range_as_in_a_browser()
    {
        using var data = new TestData();
        SampleHistory.Create(); // today: three entries and the To-do favourite; yesterday: three more
        var window = Open();
        var history = (HistoryPage)window.PageHost.Content!;
        var clearKeys = OperatingSystem.IsMacOS() ? PhysicalKey.Backspace : PhysicalKey.Delete;

        // The dialog offers the shortest range first; Esc leaves everything as it was.
        window.KeyPressQwerty(clearKeys, CommandModifier | RawInputModifiers.Shift);
        Settle(window);
        var dialog = Assert.IsType<Dialogs.ClearHistoryDialog>(Assert.Single(window.OwnedWindows));
        Settle(dialog);
        Save(dialog, "clear-history");
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle(window);
        Assert.Empty(window.OwnedWindows);
        Assert.Equal(7, ClipService.Count);

        // Again: ↓ ↑ walk the ranges, Enter deletes the last hour – the favourite and yesterday stay.
        window.KeyPressQwerty(clearKeys, CommandModifier | RawInputModifiers.Shift);
        Settle(window);
        dialog = Assert.IsType<Dialogs.ClearHistoryDialog>(Assert.Single(window.OwnedWindows));
        Settle(dialog);
        dialog.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        dialog.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Settle(dialog);
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(window);
        Assert.Empty(window.OwnedWindows);
        Assert.Equal(4, ClipService.Count);
        Assert.Equal("3 Einträge gelöscht.", history.SubtitleText.Text);
        Assert.True(history.Selected!.IsFavorite, "the newest entry left is chosen");
        Assert.True(window.SearchBox.IsFocused, "the keyboard goes on in the search field");
        window.Close();
    }

    [AvaloniaFact]
    public void Delete_takes_the_chosen_entry_again_and_again()
    {
        using var data = new TestData();
        SampleHistory.Create();
        var window = Open();
        var history = (HistoryPage)window.PageHost.Content!;

        // From the (empty) search field: Delete takes the chosen entry, the next one moves up and goes with the next Delete.
        for (var round = 0; round < 2; round++)
        {
            var chosen = history.Selected!.Id;
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Settle(window);
            Assert.Null(ClipService.Get(chosen));
            Assert.NotNull(history.Selected);
        }

        // From the list (the row had the focus and is gone after the deletion): the same, again and again.
        history.ClipList.ContainerFromItem(history.Selected!)!.Focus();
        Settle(window);
        for (var round = 0; round < 3; round++)
        {
            var chosen = history.Selected!.Id;
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Settle(window);
            Assert.Null(ClipService.Get(chosen));
        }

        Assert.Equal(2, ClipService.Count);

        // A search text keeps Delete for the text.
        window.SearchBox.Focus();
        window.SearchBox.Text = "x";
        window.SearchBox.CaretIndex = 0;
        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Settle(window);
        Assert.Equal(2, ClipService.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void The_detail_actions_fit_in_one_line_as_icons()
    {
        using var data = new TestData();
        SampleHistory.Create();
        foreach (var width in new[] { 1360.0, 900.0 })
        {
            var window = Open(width: width);
            var history = (HistoryPage)window.PageHost.Content!;
            Settle(window);
            Assert.True(history.DetailActions.Bounds.Height < 50, $"{width}: {history.DetailActions.Bounds.Height}");
            Assert.True(history.DetailActions.Bounds.Width <= history.DetailPane.Bounds.Width - 30);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_pause_asks_how_long_and_shows_in_the_header_until_resumed()
    {
        using var data = new TestData();
        SampleHistory.Create();
        var window = Open();
        Assert.Equal("Pausieren", window.PauseLabel.Text);

        // Ctrl/Cmd+Shift+P asks how long; 30 minutes are offered, ↓ takes 1 hour, Enter pauses.
        window.KeyPressQwerty(PhysicalKey.P, CommandModifier | RawInputModifiers.Shift);
        Settle(window);
        var dialog = Assert.IsType<Dialogs.PauseDialog>(Assert.Single(window.OwnedWindows));
        Settle(dialog);
        Save(dialog, "pause");
        dialog.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Settle(dialog);
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(window);
        Assert.Empty(window.OwnedWindows);
        Assert.True(RecordingService.IsPaused);
        Assert.InRange(RecordingService.PausedUntil!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(60.1));
        Assert.StartsWith("Fortsetzen · pausiert bis ", window.PauseLabel.Text);
        Save(window, "paused");

        // The tray icon gets its pause badge (drawn from the normal icon).
        foreach (var (asset, template) in new[] { ("tray-windows.ico", false), ("tray-macos.png", true) })
        {
            using var source = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://ThixClip/Assets/{asset}"));
            var png = Tray.TrayIconImages.PausedPng(source, template);
            Assert.NotNull(png);
            Assert.NotNull(Tray.TrayIconImages.WithPauseBadge(new Uri($"avares://ThixClip/Assets/{asset}"), template));
            if (ShotFolder is not null)
            {
                File.WriteAllBytes(Path.Combine(ShotFolder, $"tray-paused-{(template ? "macos" : "windows")}.png"), png!);
            }
        }

        // The same keys (or the button) resume at once.
        window.KeyPressQwerty(PhysicalKey.P, CommandModifier | RawInputModifiers.Shift);
        Settle(window);
        Assert.False(RecordingService.IsPaused);
        Assert.Equal("Pausieren", window.PauseLabel.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void A_hang_of_the_last_run_is_shown_at_start()
    {
        using var data = new TestData();
        ProblemReports.WriteHang(TimeSpan.FromSeconds(14), withDump: false);
        var dialog = Dialogs.ProblemReportDialog.ShowUnseen();
        Assert.NotNull(dialog);
        Settle(dialog!);
        Assert.Equal("ThixClip hat nicht reagiert", dialog!.Title);
        Save(dialog, "problem-report");
        dialog.Close();
        Assert.Null(Dialogs.ProblemReportDialog.ShowUnseen());
    }

    [AvaloniaFact]
    public void Copying_again_and_again_keeps_the_list_at_the_top()
    {
        using var data = new TestData();
        SampleHistory.Create();
        SettingsService.Update(settings => settings with { CloseAfterCopy = false });
        var window = Open();
        var history = (HistoryPage)window.PageHost.Content!;
        var top = FirstItemTop(history);
        for (var round = 0; round < 6; round++)
        {
            history.MoveSelection(3);
            Settle(window);
            window.KeyPressQwerty(PhysicalKey.C, CommandModifier);
            Settle(window);
            ClipboardMonitor.CheckOnce(); // what the monitor does: the copied entry moves to the top
            Settle(window);
            Assert.True(Math.Abs(FirstItemTop(history) - top) < 1, $"round {round}: {top} -> {FirstItemTop(history)}");
        }

        window.Close();
    }

    private static double FirstItemTop(HistoryPage history)
    {
        var first = history.ClipList.ContainerFromIndex(0);
        return first is null ? double.NaN : first.TranslatePoint(new Point(0, 0), history.ClipList)!.Value.Y;
    }

    [AvaloniaFact]
    public void Copying_closes_the_window_when_the_settings_say_so()
    {
        using var data = new TestData();
        SampleHistory.Create();
        var window = Open();
        Assert.True(SettingsService.Get().CloseAfterCopy, "on by default");
        window.KeyPressQwerty(PhysicalKey.C, CommandModifier);
        Settle(window);
        Assert.False(window.IsVisible);
        Assert.Equal("Produktmeeting morgen um 10 Uhr\nim Konferenzraum.", data.Clipboard.Read().Text);

        SettingsService.Update(settings => settings with { CloseAfterCopy = false });
        window.ShowFromTray(rememberForeground: false);
        Settle(window);
        window.KeyPressQwerty(PhysicalKey.C, CommandModifier);
        Settle(window);
        Assert.True(window.IsVisible);
        Assert.Equal("In die Zwischenablage kopiert.", ((HistoryPage)window.PageHost.Content!).SubtitleText.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Opening_shows_the_entry_in_the_viewer_dialog()
    {
        using var data = new TestData();
        SampleHistory.Create();
        var window = Open();
        var history = (HistoryPage)window.PageHost.Content!;

        // A text: whole and selectable; Ctrl/Cmd+C copies the entry, closes the viewer and (by default) ThixClip.
        window.KeyPressQwerty(PhysicalKey.O, CommandModifier);
        Settle(window);
        var viewer = Assert.IsType<Dialogs.ViewerDialog>(Assert.Single(window.OwnedWindows));
        Assert.Equal("Produktmeeting morgen um 10 Uhr\nim Konferenzraum.", viewer.ShownText);
        Save(viewer, "viewer-text");
        viewer.KeyPressQwerty(PhysicalKey.C, CommandModifier);
        Settle(window);
        Assert.Empty(window.OwnedWindows);
        Assert.False(window.IsVisible);
        Assert.Equal("Produktmeeting morgen um 10 Uhr\nim Konferenzraum.", data.Clipboard.Read().Text);

        // An image: fitted, Space shows it at 100 %, Esc closes and ThixClip stays.
        window.ShowFromTray(rememberForeground: false);
        Settle(window);
        history.MoveSelection(1);
        Settle(window);
        viewer = history.OpenSelected()!;
        Settle(window, waitForImages: true);
        Assert.True(viewer.ShowsImage, viewer.Title);
        Save(viewer, "viewer-image");
        viewer.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Assert.True(viewer.IsActualSize);
        viewer.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle(window);
        Assert.Empty(window.OwnedWindows);
        Assert.True(window.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void The_about_page_leads_to_the_updates()
    {
        using var data = new TestData();
        var window = Open();
        window.VersionButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        var about = Assert.IsType<AboutPage>(window.PageHost.Content);
        Assert.Equal("Nach Updates suchen …", about.UpdateLabel.Text);
        about.UpdateButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.IsType<SettingsPage>(window.PageHost.Content);
        window.Close();
    }

    [AvaloniaFact]
    public void A_narrow_window_keeps_the_list()
    {
        using var data = new TestData();
        SampleHistory.Create();
        var window = Open(width: 780, height: 560);
        var history = (HistoryPage)window.PageHost.Content!;
        Settle(window);
        Assert.False(history.DetailPane.IsVisible);
        Assert.True(history.ClipList.IsVisible);
        Save(window, "narrow");
        window.Close();
    }

    [AvaloniaFact]
    public void The_snapshot_draws_every_window_in_light_and_dark_at_its_natural_size()
    {
        using var data = new TestData();
        var folder = Path.Combine(data.Folder, "snapshot");
        var report = Snapshot.Render(folder);

        // Every picture is written – only the setup assistant is left out where it does not exist (macOS).
        Assert.Equal(Snapshot.FileNames.Order(), report.Written.Concat(report.Skipped.Select(skipped => skipped.File)).Order());
        Assert.All(report.Skipped, skipped => Assert.StartsWith("setup-", skipped.File));
        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(report.Skipped);
        }

        foreach (var name in report.Written)
        {
            Assert.True(new FileInfo(Path.Combine(folder, name)).Length > 0, name);
        }

        // 1x, the size the windows have on screen.
        Assert.Equal((1180, 760), PictureSize(folder, "main-history-dark.png"));
        Assert.Equal(400, PictureSize(folder, "dialog-pause-light.png").Width);
        Assert.Equal(360, PictureSize(folder, "toast-dark.png").Width);
        Assert.Equal((64, 64), PictureSize(folder, "tray-icon-windows-paused.png"));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal((600, 560), PictureSize(folder, "setup-welcome-light.png"));
        }

        // Clock, version and computer name run and read as before; the history in the data folder is the sample's.
        Assert.Null(AppClock.Pinned);
        Assert.NotEqual(Snapshot.Version, AppInfo.Version);
        Assert.Equal(7, ClipService.Count);
    }

    private static (int Width, int Height) PictureSize(string folder, string name)
    {
        using var codec = SkiaSharp.SKCodec.Create(Path.Combine(folder, name));
        return (codec.Info.Width, codec.Info.Height);
    }

    private static RawInputModifiers CommandModifier => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static MainWindow Open(double width = 1360, double height = 860)
    {
        SettingsService.Remember(settings => settings with { WindowWidth = width, WindowHeight = height });
        var window = new MainWindow { Width = width, Height = height };
        window.ShowFromTray(rememberForeground: false);
        window.Width = width;
        window.Height = height;
        Settle(window);
        return window;
    }

    /// <summary>Runs the dispatcher until the condition holds (debounce timers, background work), at most two seconds.</summary>
    private static void WaitUntil(Window window, Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            Settle(window);
        }
    }

    /// <summary>Lets the dispatcher, the debounce timers and (optionally) the background image decoding finish.</summary>
    private static void Settle(Window window, bool waitForImages = false)
    {
        for (var round = 0; round < (waitForImages ? 40 : 8); round++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(waitForImages ? 40 : 15);
        }

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Save(Window window, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (ShotFolder is null)
        {
            return;
        }

        Directory.CreateDirectory(ShotFolder);
        frame.Save(Path.Combine(ShotFolder, $"{name}.png"));
    }
}
