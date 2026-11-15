using Avalonia.Controls;
using Avalonia.Platform;
using SkiaSharp;
using ThixClip.Core.Infrastructure;

namespace ThixClip.Tray;

/// <summary>
/// The paused tray icon: the normal one with a pause badge at the bottom right, drawn at start (no second asset to keep
/// in step). Windows: an amber badge with white bars. macOS: the menu bar colours a template image itself, so the badge is
/// a solid circle with the bars cut out.
/// </summary>
internal static class TrayIconImages
{
    private const int Size = 64;

    public static WindowIcon? WithPauseBadge(Uri asset, bool isTemplate)
    {
        try
        {
            using var source = AssetLoader.Open(asset);
            return PausedPng(source, isTemplate) is { } png ? new WindowIcon(new MemoryStream(png)) : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            Log.Warning($"Paused tray icon could not be drawn: {exception.Message}");
            return null;
        }
    }

    /// <summary>The paused icon as a 64 px PNG (null when the source cannot be decoded).</summary>
    internal static byte[]? PausedPng(Stream source, bool isTemplate)
    {
        {
            using var original = SKBitmap.Decode(source);
            if (original is null)
            {
                return null;
            }

            using var surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            using (var image = SKImage.FromBitmap(original))
            {
                using var smooth = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                canvas.DrawImage(image, new SKRect(0, 0, Size, Size), smooth);
            }

            var center = new SKPoint(Size * 0.71f, Size * 0.71f);
            var radius = Size * 0.29f;
            using var clear = new SKPaint { BlendMode = SKBlendMode.Clear, IsAntialias = true };
            canvas.DrawCircle(center, radius + Size * 0.06f, clear); // a gap around the badge

            using var badge = new SKPaint { Color = isTemplate ? SKColors.Black : new SKColor(0xF5, 0x9E, 0x0B), IsAntialias = true };
            canvas.DrawCircle(center, radius, badge);

            using var bars = isTemplate ? new SKPaint { BlendMode = SKBlendMode.Clear, IsAntialias = true } : new SKPaint { Color = SKColors.White, IsAntialias = true };
            var barWidth = radius * 0.32f;
            var barHeight = radius * 1.0f;
            var gap = radius * 0.26f;
            canvas.DrawRoundRect(new SKRect(center.X - gap / 2 - barWidth, center.Y - barHeight / 2, center.X - gap / 2, center.Y + barHeight / 2), 2, 2, bars);
            canvas.DrawRoundRect(new SKRect(center.X + gap / 2, center.Y - barHeight / 2, center.X + gap / 2 + barWidth, center.Y + barHeight / 2), 2, 2, bars);

            using var snapshot = surface.Snapshot();
            using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }
    }
}
