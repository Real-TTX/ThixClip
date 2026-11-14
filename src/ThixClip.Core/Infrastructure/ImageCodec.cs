using System.Buffers.Binary;
using SkiaSharp;

namespace ThixClip.Core.Infrastructure;

/// <summary>A decoded and normalised image: the PNG ThixClip stores, its size and a thumbnail for the list.</summary>
internal sealed record NormalizedImage(byte[] Png, int Width, int Height, byte[] Thumbnail);

/// <summary>
/// Image work with SkiaSharp (the engine Avalonia draws with): every image is decoded and stored as one PNG encoded the
/// same way, so the same picture copied twice has the same bytes (and the same content hash). Windows bitmaps (DIB)
/// come from the clipboard and go back to it.
/// </summary>
internal static class ImageCodec
{
    /// <summary>Longest side of a thumbnail (the list shows them at about 140 x 70, twice that for sharp screens).</summary>
    public const int ThumbnailSize = 320;

    /// <summary>Decodes PNG, JPEG, BMP, GIF or WebP and returns the normalised PNG; null when it is no image.</summary>
    public static NormalizedImage? Normalize(byte[] data)
    {
        using var bitmap = Decode(data);
        return bitmap is null ? null : FromBitmap(bitmap);
    }

    /// <summary>Decodes an image; null for data that is no image (SkiaSharp throws for those).</summary>
    public static SKBitmap? Decode(byte[]? data)
    {
        if (data is not { Length: > 0 })
        {
            return null;
        }

        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData);
        return codec is null ? null : SKBitmap.Decode(codec);
    }

    /// <summary>A packed Windows DIB (CF_DIB / CF_DIBV5: header + pixels) as normalised PNG.</summary>
    public static NormalizedImage? FromDib(byte[] dib)
    {
        using var bitmap = DecodeDib(dib);
        return bitmap is null ? null : FromBitmap(bitmap);
    }

    /// <summary>A small PNG of the image, longest side <paramref name="maxSize"/> (never larger than the image).</summary>
    public static byte[] Thumbnail(byte[] png, int maxSize = ThumbnailSize)
    {
        using var bitmap = Decode(png) ?? throw new InvalidDataException("No image.");
        return ThumbnailOf(bitmap, maxSize);
    }

    /// <summary>A copy that fits into <paramref name="maxSide"/> (for OCR engines with a size limit); the PNG itself when it fits.</summary>
    public static byte[] FitInto(byte[] png, int maxSide)
    {
        using var bitmap = Decode(png) ?? throw new InvalidDataException("No image.");
        if (Math.Max(bitmap.Width, bitmap.Height) <= maxSide)
        {
            return png;
        }

        return ThumbnailOf(bitmap, maxSide);
    }

    /// <summary>
    /// The image as a packed DIB (BITMAPINFOHEADER, 32 bit, bottom-up) for programs that do not read PNG from the
    /// clipboard. Transparent parts are laid on white: many programs ignore the alpha of a DIB and would show black.
    /// </summary>
    public static byte[] ToDib(byte[] png)
    {
        using var source = Decode(png) ?? throw new InvalidDataException("No image.");
        var width = source.Width;
        var height = source.Height;
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var flattened = new SKBitmap(info);
        using (var canvas = new SKCanvas(flattened))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, 0, 0);
        }

        const int headerSize = 40;
        var stride = width * 4;
        var dib = new byte[headerSize + stride * height];
        var span = dib.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span[0..], headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], height); // positive = bottom-up
        BinaryPrimitives.WriteInt16LittleEndian(span[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 0); // BI_RGB
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], stride * height);

        var pixels = flattened.GetPixelSpan();
        for (var row = 0; row < height; row++)
        {
            var sourceRow = pixels.Slice(row * flattened.RowBytes, stride);
            sourceRow.CopyTo(span.Slice(headerSize + (height - 1 - row) * stride, stride));
        }

        return dib;
    }

    private static NormalizedImage FromBitmap(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return new NormalizedImage(encoded.ToArray(), bitmap.Width, bitmap.Height, ThumbnailOf(bitmap, ThumbnailSize));
    }

    private static byte[] ThumbnailOf(SKBitmap bitmap, int maxSize)
    {
        var scale = Math.Min(1.0, (double)maxSize / Math.Max(bitmap.Width, bitmap.Height));
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        using var resized = scale < 1.0 ? bitmap.Resize(new SKImageInfo(width, height, bitmap.ColorType, bitmap.AlphaType), SKFilterQuality.High) : null;
        using var image = SKImage.FromBitmap(resized ?? bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
        return encoded.ToArray();
    }

    /// <summary>
    /// 24 and 32 bit DIBs (the usual screenshots) are read here: a 32 bit DIB whose alpha bytes are all zero is opaque
    /// (Windows writes them like that), a decoder would make it invisible. Everything else goes through the BMP decoder.
    /// </summary>
    private static SKBitmap? DecodeDib(byte[] dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8));
        var bitCount = BinaryPrimitives.ReadInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16));
        var height = Math.Abs(rawHeight);
        var bottomUp = rawHeight > 0;
        const int biRgb = 0, biBitfields = 3;

        if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (bitCount != 24 && bitCount != 32) ||
            (compression != biRgb && compression != biBitfields))
        {
            return DecodeAsBmpFile(dib, headerSize, bitCount);
        }

        // BI_BITFIELDS with a 40 byte header has three masks after it; V4/V5 headers contain them.
        var pixelOffset = headerSize + (compression == biBitfields && headerSize == 40 ? 12 : 0);
        var stride = ((width * bitCount + 31) / 32) * 4;
        if (dib.Length < pixelOffset + (long)stride * height)
        {
            return null;
        }

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        var target = new byte[width * height * 4];
        var anyAlpha = false;
        for (var row = 0; row < height; row++)
        {
            var sourceRow = pixelOffset + (bottomUp ? height - 1 - row : row) * stride;
            var targetRow = row * width * 4;
            for (var x = 0; x < width; x++)
            {
                var s = sourceRow + x * (bitCount / 8);
                var t = targetRow + x * 4;
                target[t] = dib[s];
                target[t + 1] = dib[s + 1];
                target[t + 2] = dib[s + 2];
                var alpha = bitCount == 32 ? dib[s + 3] : (byte)255;
                target[t + 3] = alpha;
                anyAlpha |= alpha != 0;
            }
        }

        if (!anyAlpha)
        {
            for (var i = 3; i < target.Length; i += 4)
            {
                target[i] = 255;
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(target, 0, bitmap.GetPixels(), target.Length);
        return bitmap;
    }

    private static SKBitmap? DecodeAsBmpFile(byte[] dib, int headerSize, int bitCount)
    {
        var colors = bitCount <= 8 ? BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32)) : 0;
        if (bitCount <= 8 && colors == 0)
        {
            colors = 1 << bitCount;
        }

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), 14 + headerSize + colors * 4);
        dib.CopyTo(file, 14);
        return Decode(file);
    }
}
