using MatCloud.Contract.Clip;
using SkiaSharp;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Core.Platform;

namespace ThixClip.Tests;

public sealed class ClipServiceTests : IDisposable
{
    private readonly TestData _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void A_copied_text_becomes_an_entry_and_survives_a_restart()
    {
        _data.Clipboard.Write(new ClipboardSnapshot("Produktmeeting morgen um 10 Uhr", null, false, false));
        ClipboardMonitor.CheckOnce();

        var entry = Assert.Single(ClipService.Query(new ClipQuery()));
        Assert.Equal(ClipKind.Text, entry.Kind);
        Assert.Equal("Produktmeeting morgen um 10 Uhr", entry.Text);

        // A new start reads it from disk.
        HistoryStore.Close();
        ClipService.ResetForTests();
        ClipService.Initialize();
        Assert.Equal("Produktmeeting morgen um 10 Uhr", Assert.Single(ClipService.Query(new ClipQuery())).Text);
    }

    [Fact]
    public void The_same_content_twice_is_one_entry_moved_to_the_top()
    {
        var first = ClipService.CaptureText("eins")!;
        ClipService.CaptureText("zwei");
        Thread.Sleep(5);
        var again = ClipService.CaptureText("eins")!;

        var list = ClipService.Query(new ClipQuery());
        Assert.Equal(2, list.Count);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal("eins", list[0].Text);
    }

    [Theory]
    [InlineData("https://www.example.com/produkt", ClipKind.Link)]
    [InlineData("http://localhost:4950", ClipKind.Link)]
    [InlineData("siehe https://example.com", ClipKind.Text)]
    [InlineData("ftp://example.com/file", ClipKind.Text)]
    [InlineData("www.example.com", ClipKind.Text)]
    public void A_single_web_address_is_a_link(string text, ClipKind kind) =>
        Assert.Equal(kind, ClipService.CaptureText(text)!.Kind);

    [Fact]
    public void Empty_whitespace_paused_and_confidential_content_is_not_recorded()
    {
        Assert.Null(ClipService.CaptureText("   \n "));
        Assert.Null(ClipService.Capture(new ClipboardSnapshot(null, null, false, IsConfidential: true)));

        RecordingService.Pause(PauseLength.UntilResumed);
        _data.Clipboard.Write(new ClipboardSnapshot("geheim?", null, false, false));
        ClipboardMonitor.CheckOnce();
        Assert.Empty(ClipService.Query(new ClipQuery()));
    }

    [Fact]
    public void A_pause_lasts_its_time_until_restart_or_until_resumed()
    {
        RecordingService.Pause(PauseLength.Minutes30);
        Assert.True(RecordingService.IsPaused);
        Assert.True(SettingsService.Get().IsRecordingPaused);
        var until = Assert.IsType<DateTimeOffset>(RecordingService.PausedUntil);
        Assert.InRange(until - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30.1));
        Assert.StartsWith("Pausiert bis ", RecordingService.Describe());

        // A timed pause that has ended is over at the next start, even if the timer never fired.
        SettingsStore.Update(settings => settings with { RecordingPausedUntil = DateTimeOffset.UtcNow.AddMinutes(-1) });
        Assert.False(RecordingService.IsPaused);
        RecordingService.Initialize();
        Assert.False(SettingsService.Get().IsRecordingPaused);

        // Until restart: in memory only, nothing stored.
        RecordingService.Pause(PauseLength.UntilRestart);
        Assert.True(RecordingService.IsPaused);
        Assert.False(SettingsService.Get().IsRecordingPaused);
        Assert.Equal("Pausiert bis zum Neustart", RecordingService.Describe());
        Assert.Null(ClipService.Capture(new ClipboardSnapshot("nicht aufnehmen", null, false, false)));

        RecordingService.Resume();
        Assert.False(RecordingService.IsPaused);
        Assert.NotNull(ClipService.Capture(new ClipboardSnapshot("wieder aufnehmen", null, false, false)));

        RecordingService.Pause(PauseLength.UntilResumed);
        Assert.Null(RecordingService.PausedUntil);
        Assert.Equal("Pausiert", RecordingService.Describe());
        Assert.Throws<BusinessException>(() => RecordingService.Pause((PauseLength)42));
    }

    [Fact]
    public void The_viewer_gets_the_whole_image_and_only_web_links_go_to_the_browser()
    {
        var link = ClipService.CaptureText("https://example.com/plan?x=1")!;
        Assert.Equal("https://example.com/plan?x=1", ClipService.BrowserLink(link.Id));
        var text = ClipService.CaptureText("Zeile 1\nÄnderung ü")!;
        Assert.Null(ClipService.BrowserLink(text.Id));

        var png = ImageCodec.Normalize(TestImages.Png(30, 20, SKColors.Teal))!;
        var image = ClipService.CaptureImage(png)!;
        Assert.Equal(png.Png, ClipService.ReadImageToView(image.Id));
        Assert.Throws<BusinessException>(() => ClipService.ReadImageToView(text.Id));
        Assert.Throws<BusinessException>(() => ClipService.BrowserLink(999_999));

        // Files earlier versions handed to other programs go at the next start (they hold clipboard contents).
        Directory.CreateDirectory(AppPaths.OpenDirectory);
        var leftover = Path.Combine(AppPaths.OpenDirectory, "Text 2026-11-15 10-00-00 (1).txt");
        File.WriteAllText(leftover, "geheim");
        HistoryStore.Close();
        ClipService.ResetForTests();
        ClipService.Initialize();
        Assert.False(File.Exists(leftover));
    }

    [Fact]
    public void A_hang_is_reported_once_with_its_text()
    {
        Assert.Empty(ProblemReports.TakeUnseen());
        var path = ProblemReports.WriteHang(TimeSpan.FromSeconds(12), withDump: false);
        Assert.NotNull(path);
        ProblemReports.Append(path!, "Danach reagierte ThixClip wieder.");

        var report = Assert.Single(ProblemReports.TakeUnseen());
        Assert.True(report.IsHang);
        Assert.Contains("12 Sekunden", report.Text);
        Assert.Contains(AppInfo.Version, report.Text);
        Assert.Contains("Danach reagierte", report.Text);
        Assert.Empty(ProblemReports.TakeUnseen()); // shown once
    }

    [Fact]
    public void Search_finds_every_word_in_texts_and_in_the_recognised_text_of_images()
    {
        ClipService.CaptureText("Release-Plan Q2: Features, Tests, Rollout");
        ClipService.CaptureText("Design-Feedback: Clean, einfach");
        var image = ClipService.CaptureImage(ImageCodec.Normalize(TestImages.Png(40, 20, SKColors.Coral))!)!;
        ClipService.SetOcrResult(image.Id, "To-do: Konzept finalisieren\nFeedback einholen", OcrState.Done);

        Assert.Single(ClipService.Query(new ClipQuery(Search: "rollout q2")));
        Assert.Equal(2, ClipService.Query(new ClipQuery(Search: "feedback")).Count);
        Assert.Equal(image.Id, Assert.Single(ClipService.Query(new ClipQuery(Search: "konzept"))).Id);
        Assert.Empty(ClipService.Query(new ClipQuery(Search: "konzept rollout")));
        Assert.Equal(image.Id, Assert.Single(ClipService.Query(new ClipQuery(Section: ClipSection.Images))).Id);
        Assert.Equal(2, ClipService.Query(new ClipQuery(Kind: ClipKind.Text)).Count);
    }

    [Fact]
    public void Pinned_entries_come_first_and_favourites_have_their_own_section()
    {
        var old = ClipService.CaptureText("alt")!;
        ClipService.CaptureText("neu");
        ClipService.TogglePin(old.Id);
        ClipService.ToggleFavorite(old.Id);

        Assert.Equal("alt", ClipService.Query(new ClipQuery())[0].Text);
        Assert.Equal("alt", Assert.Single(ClipService.Query(new ClipQuery(Section: ClipSection.Favorites))).Text);
        Assert.Equal("alt", ClipService.Query(new ClipQuery(Sort: ClipSort.OldestFirst))[0].Text);
    }

    [Fact]
    public void Clearing_keeps_favourites_and_pinned_entries()
    {
        var favorite = ClipService.CaptureText("Favorit")!;
        var pinned = ClipService.CaptureText("Angeheftet")!;
        ClipService.CaptureText("weg 1");
        ClipService.CaptureText("weg 2");
        ClipService.ToggleFavorite(favorite.Id);
        ClipService.TogglePin(pinned.Id);

        Assert.Equal(2, ClipService.CountToClear(ClearRange.All));
        Assert.Equal(2, ClipService.ClearHistory(ClearRange.All));
        Assert.Equal(["Angeheftet", "Favorit"], ClipService.Query(new ClipQuery()).Select(entry => entry.Text!).OrderBy(text => text));
    }

    [Fact]
    public void Clearing_a_range_takes_only_what_was_copied_within_it_as_in_a_browser()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (text, age) in new[]
                 {
                     ("eben", TimeSpan.FromMinutes(5)), ("heute früh", TimeSpan.FromHours(5)), ("vorgestern", TimeSpan.FromDays(2)),
                     ("letzte Woche", TimeSpan.FromDays(10)), ("Favorit von eben", TimeSpan.FromMinutes(1)),
                 })
        {
            var entry = ClipService.CaptureText(text)!;
            HistoryStore.Update(ClipService.Get(entry.Id)! with { CopiedDate = now - age });
        }

        ClipService.ResetForTests();
        ClipService.Initialize();
        ClipService.ToggleFavorite(ClipService.Query(new ClipQuery(Search: "Favorit")).Single().Id);

        Assert.Equal([1, 2, 3, 4, 4], ClearRanges.Choices.Select(ClipService.CountToClear));
        Assert.Equal(1, ClipService.ClearHistory(ClearRange.LastHour));
        Assert.Equal(1, ClipService.ClearHistory(ClearRange.Last24Hours));
        Assert.Equal(1, ClipService.ClearHistory(ClearRange.Last7Days));
        Assert.Equal(["Favorit von eben", "letzte Woche"], ClipService.Query(new ClipQuery()).Select(entry => entry.Text!));
        Assert.Throws<BusinessException>(() => ClipService.ClearHistory((ClearRange)42));
    }

    [Fact]
    public void Retention_removes_old_entries_but_never_favourites()
    {
        var old = ClipService.CaptureText("alt")!;
        var oldFavorite = ClipService.CaptureText("alter Favorit")!;
        ClipService.ToggleFavorite(oldFavorite.Id);
        foreach (var entry in new[] { old, oldFavorite })
        {
            var current = ClipService.Get(entry.Id)!;
            HistoryStore.Update(current with { CopiedDate = DateTimeOffset.UtcNow.AddDays(-40) });
        }

        ClipService.ResetForTests();
        ClipService.Initialize(); // removes expired entries at start (30 days by default)

        Assert.Equal("alter Favorit", Assert.Single(ClipService.Query(new ClipQuery())).Text);
    }

    [Fact]
    public void Copying_an_entry_puts_it_on_the_clipboard_and_moves_it_up()
    {
        var first = ClipService.CaptureText("zuerst")!;
        ClipService.CaptureText("danach");

        ClipService.CopyToClipboard(first.Id);
        Assert.Equal("zuerst", _data.Clipboard.Content.Text);
        ClipboardMonitor.CheckOnce();
        Assert.Equal("zuerst", ClipService.Query(new ClipQuery())[0].Text);
        Assert.Equal(2, ClipService.Count);
    }

    [Fact]
    public void An_image_is_stored_as_png_with_size_and_thumbnail()
    {
        _data.Clipboard.Write(new ClipboardSnapshot(null, TestImages.Png(1920, 1080, SKColors.SteelBlue), false, false));
        ClipboardMonitor.CheckOnce();

        var entry = Assert.Single(ClipService.Query(new ClipQuery()));
        Assert.Equal(ClipKind.Image, entry.Kind);
        Assert.Equal((1920, 1080), (entry.ImageWidth!.Value, entry.ImageHeight!.Value));
        using var thumbnail = SKBitmap.Decode(ClipService.ReadThumbnail(entry.Id));
        Assert.Equal(ImageCodec.ThumbnailSize, thumbnail.Width);

        ClipService.CopyToClipboard(entry.Id);
        using var copied = SKBitmap.Decode(_data.Clipboard.Content.Image);
        Assert.Equal(1920, copied.Width);
    }

    [Fact]
    public void Deleting_without_cloud_removes_the_entry_for_good()
    {
        var entry = ClipService.CaptureText("weg damit")!;
        ClipService.Delete(entry.Id);

        Assert.Empty(ClipService.Query(new ClipQuery()));
        Assert.Empty(HistoryStore.LoadAll());
    }

    [Fact]
    public void Editing_a_text_checks_it_and_keeps_links_links()
    {
        var entry = ClipService.CaptureText("alt")!;
        Assert.Equal(ClipKind.Link, ClipService.UpdateText(entry.Id, "https://example.com").Kind);
        Assert.Throws<BusinessException>(() => ClipService.UpdateText(entry.Id, " "));
    }

    [Fact]
    public async Task Settings_reject_unknown_retention_and_locked_configuration()
    {
        await Assert.ThrowsAsync<BusinessException>(() => SettingsService.SetRetentionAsync(12));
        AccessPolicy.UsePolicyReaderForTests(name => name == "LockConfiguration");
        var error = Assert.Throws<BusinessException>(() => SettingsService.Update(settings => settings with { PasteOnSelect = false }));
        Assert.Contains("LockConfiguration", error.Message);
    }
}
