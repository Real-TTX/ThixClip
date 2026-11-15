using SkiaSharp;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;

namespace ThixClip.Core.Business;

/// <summary>
/// The history of the mockup – texts, a link and pictures drawn here, today and yesterday – for the UI tests and the
/// snapshots of "ThixClip --snapshot". Only ever written into a test or temporary data folder.
/// </summary>
internal static class SampleHistory
{
    /// <summary>The text of the newest entry (chosen when the window opens).</summary>
    public const string NewestText = "Produktmeeting morgen um 10 Uhr\nim Konferenzraum.";

    /// <summary>
    /// Seven entries: yesterday the release plan, the chart and the feedback; today the mountains, the link, the to-do
    /// picture (favourite, with recognised text) and the meeting, all within the last half hour.
    /// </summary>
    public static void Create()
    {
        // Every entry is recorded at its own moment: the clock stands there for it and runs on as before afterwards.
        var pinned = AppClock.Pinned;
        var now = AppClock.Now;
        var yesterday = new DateTimeOffset(AppClock.Today.AddDays(-1));
        try
        {
            ClipEntry? At(DateTimeOffset moment, Func<ClipEntry?> capture)
            {
                AppClock.Pin(moment);
                return capture();
            }

            At(yesterday.AddHours(9.2), () => ClipService.CaptureText("Release-Plan Q2: Features, Tests, Rollout"));
            At(yesterday.AddHours(15.35), () => ClipService.CaptureImage(ImageCodec.Normalize(Chart())!));
            At(yesterday.AddHours(17.9), () => ClipService.CaptureText("Design-Feedback:\nClean, einfach, sehr stimmig."));
            At(now.AddMinutes(-26), () => ClipService.CaptureImage(ImageCodec.Normalize(Mountains())!));
            At(now.AddMinutes(-19), () => ClipService.CaptureText("https://www.example.com/produkt"));
            var todo = At(now.AddMinutes(-11), () => ClipService.CaptureImage(ImageCodec.Normalize(TodoNote())!))!;
            ClipService.SetOcrResult(todo.Id, "To-do:\nKonzept finalisieren\nFeedback einholen\nLaunch vorbereiten", OcrState.Done);
            ClipService.ToggleFavorite(todo.Id);
            At(now.AddMinutes(-3), () => ClipService.CaptureText(NewestText));
        }
        finally
        {
            AppClock.Pin(pinned);
        }
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
