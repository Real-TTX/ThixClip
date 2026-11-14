using SkiaSharp;
using ThixClip.Core.Platform;

namespace ThixClip.Tests;

/// <summary>The OCR engine of the system really reads text (Windows.Media.Ocr / Apple Vision).</summary>
public sealed class OcrTests
{
    [Fact]
    public async Task The_system_engine_reads_the_text_of_a_screenshot()
    {
        // A build agent may have no OCR language; THIXCLIP_REQUIRE_OCR=1 makes a missing engine a failure (developer machines).
        if (!TextRecognition.IsAvailable)
        {
            Assert.Null(Environment.GetEnvironmentVariable("THIXCLIP_REQUIRE_OCR"));
            return;
        }

        using var bitmap = new SKBitmap(900, 300);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true, TextSize = 56, Typeface = SKTypeface.FromFamilyName("Arial") };
            canvas.DrawText("Konzept finalisieren", 40, 110, paint);
            canvas.DrawText("Feedback einholen", 40, 220, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        var text = await TextRecognition.RecognizeAsync(image.Encode(SKEncodedImageFormat.Png, 100).ToArray(), CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("Konzept", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Feedback", text, StringComparison.OrdinalIgnoreCase);
    }
}
