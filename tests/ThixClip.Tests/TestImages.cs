using SkiaSharp;

namespace ThixClip.Tests;

internal static class TestImages
{
    public static byte[] Png(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(width / 4f, height / 4f, width / 2f, height / 2f, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>A packed 32-bit DIB as Windows puts it on the clipboard: bottom-up, alpha bytes all zero.</summary>
    public static byte[] Dib32(int width, int height, byte blue, byte green, byte red)
    {
        var dib = new byte[40 + width * height * 4];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(width).CopyTo(dib, 4);
        BitConverter.GetBytes(height).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);
        for (var i = 40; i < dib.Length; i += 4)
        {
            dib[i] = blue;
            dib[i + 1] = green;
            dib[i + 2] = red;
            dib[i + 3] = 0;
        }

        return dib;
    }
}
