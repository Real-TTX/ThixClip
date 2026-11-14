using SkiaSharp;
using ThixClip.Shared;

namespace ThixClip.Cloud.Business;

/// <summary>An image ready to be stored: the PNG (as it came when it was a PNG), a preview, the size and the hash of the PNG.</summary>
public sealed record PreparedImage(byte[] Png, byte[] Thumbnail, int Width, int Height, string ContentHash);

/// <summary>
/// Images of clips (SkiaSharp): an upload is decoded (so only real images are kept), stored as PNG (other formats are converted) and gets a
/// preview whose longest side is <see cref="SyncContract.ThumbnailSize"/> px.
/// </summary>
public static class ImageTools
{
    /// <summary>The largest picture that is decoded (a guard against an image that is small as a file and huge in memory).</summary>
    public const int MaxSide = 20_000;
    public const long MaxPixels = 120_000_000;

    public static Outcome<PreparedImage> Prepare(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return Outcome<PreparedImage>.Fail("Die Datei ist leer.", "image");
        }

        if (bytes.Length > SyncContract.MaxImageBytes)
        {
            return Outcome<PreparedImage>.Fail($"Das Bild ist größer als {SyncContract.MaxImageBytes / 1024 / 1024} MB.", "image");
        }

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return Outcome<PreparedImage>.Fail("Die Datei ist kein Bild, das ThixClip lesen kann (PNG, JPEG, GIF, WebP, BMP).", "image");
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > MaxSide || info.Height > MaxSide || (long)info.Width * info.Height > MaxPixels)
        {
            return Outcome<PreparedImage>.Fail("Das Bild ist zu groß.", "image");
        }

        using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (bitmap is null)
        {
            return Outcome<PreparedImage>.Fail("Das Bild ist beschädigt.", "image");
        }

        var png = codec.EncodedFormat == SKEncodedImageFormat.Png ? bytes : Encode(bitmap);
        var thumbnail = Thumbnail(bitmap);
        return Outcome<PreparedImage>.Success(new PreparedImage(png, thumbnail, bitmap.Width, bitmap.Height, ClipContent.HashBytes(png)));
    }

    /// <summary>The preview of a picture: the longest side <see cref="SyncContract.ThumbnailSize"/> px (a smaller picture keeps its size).</summary>
    public static byte[] Thumbnail(SKBitmap bitmap)
    {
        var longest = Math.Max(bitmap.Width, bitmap.Height);
        if (longest <= SyncContract.ThumbnailSize)
        {
            return Encode(bitmap);
        }

        var scale = SyncContract.ThumbnailSize / (double)longest;
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        using var resized = bitmap.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), SKFilterQuality.High);
        return Encode(resized ?? bitmap);
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
