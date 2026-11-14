using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using SkiaSharp;
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

/// <summary>The history of the mockup: texts, a link and pictures, today and yesterday.</summary>
internal static class SampleHistory
{
    public static void Create()
    {
        var releasePlan = ClipService.CaptureText("Release-Plan Q2: Features, Tests, Rollout")!;
        var chart = ClipService.CaptureImage(ImageCodec.Normalize(Chart())!)!;
        var feedback = ClipService.CaptureText("Design-Feedback:\nClean, einfach, sehr stimmig.")!;
        ClipService.CaptureImage(ImageCodec.Normalize(Mountains())!);
        ClipService.CaptureText("https://www.example.com/produkt");
        var todo = ClipService.CaptureImage(ImageCodec.Normalize(TodoNote())!)!;
        ClipService.SetOcrResult(todo.Id, "To-do:\nKonzept finalisieren\nFeedback einholen\nLaunch vorbereiten", OcrState.Done);
        ClipService.ToggleFavorite(todo.Id);
        ClipService.CaptureText("Produktmeeting morgen um 10 Uhr\nim Konferenzraum.");

        var yesterday = DateTime.Today.AddDays(-1).ToUniversalTime();
        foreach (var (entry, hour) in new[] { (releasePlan, 9.2), (chart, 15.35), (feedback, 17.9) })
        {
            var current = ClipService.Get(entry.Id)!;
            HistoryStore.Update(current with { CopiedDate = new DateTimeOffset(yesterday.AddHours(hour), TimeSpan.Zero) });
        }

        ClipService.ResetForTests();
        ClipService.Initialize();
    }

    private static byte[] TodoNote() => Draw(900, 560, canvas =>
    {
        canvas.Clear(new SKColor(0xF6, 0xF4, 0xEF));
        using var leaf = new SKPaint { Color = new SKColor(0x5E, 0x8C, 0x4E), IsAntialias = true };
        canvas.DrawOval(70, 300, 60, 150, leaf);
        canvas.DrawOval(110, 200, 50, 130, leaf);
        using var board = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawRoundRect(200, 40, 660, 480, 16, 16, board);
        using var text = new SKPaint { Color = new SKColor(0x22, 0x22, 0x22), IsAntialias = true, TextSize = 64, Typeface = SKTypeface.FromFamilyName("Segoe Print") };
        canvas.DrawText("To-do:", 260, 140, text);
        text.TextSize = 46;
        using var box = new SKPaint { Color = new SKColor(0x22, 0x22, 0x22), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4 };
        var y = 240f;
        foreach (var line in new[] { "Konzept finalisieren", "Feedback einholen", "Launch vorbereiten" })
        {
            canvas.DrawRect(262, y - 36, 34, 34, box);
            canvas.DrawText(line, 320, y, text);
            y += 100;
        }
    });

    private static byte[] Mountains() => Draw(1920, 1080, canvas =>
    {
        using var sky = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 1080),
                [new SKColor(0x6F, 0xA8, 0xE8), new SKColor(0xDD, 0xEC, 0xFA)], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(0, 0, 1920, 1080, sky);
        using var far = new SKPaint { Color = new SKColor(0x7D, 0x8B, 0xA6), IsAntialias = true };
        using var near = new SKPaint { Color = new SKColor(0x3E, 0x4C, 0x66), IsAntialias = true };
        using var snow = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawPath(Triangle(200, 760, 820, 230, 1450, 760), far);
        canvas.DrawPath(Triangle(700, 820, 1200, 300, 1800, 820), near);
        canvas.DrawPath(Triangle(1110, 400, 1200, 300, 1290, 400), snow);
        using var lake = new SKPaint { Color = new SKColor(0x2F, 0x6F, 0xA8) };
        canvas.DrawRect(0, 800, 1920, 280, lake);
    });

    private static byte[] Chart() => Draw(800, 420, canvas =>
    {
        canvas.Clear(SKColors.White);
        using var bar = new SKPaint { Color = new SKColor(0x2F, 0x6F, 0xED), IsAntialias = true };
        var heights = new[] { 60, 90, 140, 210, 320 };
        for (var i = 0; i < heights.Length; i++)
        {
            canvas.DrawRoundRect(120 + i * 120, 380 - heights[i], 70, heights[i], 6, 6, bar);
        }
    });

    private static SKPath Triangle(float x1, float y1, float x2, float y2, float x3, float y3)
    {
        var path = new SKPath();
        path.MoveTo(x1, y1);
        path.LineTo(x2, y2);
        path.LineTo(x3, y3);
        path.Close();
        return path;
    }

    private static byte[] Draw(int width, int height, Action<SKCanvas> paint)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            paint(canvas);
        }

        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
}
