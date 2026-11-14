using ThixClip.Core.Platform.Mac;

namespace ThixClip.Core.Platform;

/// <summary>The OCR engine of the system: Windows.Media.Ocr or Apple Vision. Both work offline.</summary>
internal static class TextRecognition
{
    public static bool IsAvailable
    {
        get
        {
#if WINDOWS
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            {
                return Windows.WindowsOcr.IsAvailable;
            }
#endif
            return OperatingSystem.IsMacOS() && MacOcr.IsAvailable;
        }
    }

    /// <summary>The recognised text (lines joined with \n), or null when there is none.</summary>
    public static async Task<string?> RecognizeAsync(byte[] png, CancellationToken cancellationToken)
    {
#if WINDOWS
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return await Windows.WindowsOcr.RecognizeAsync(png, cancellationToken);
        }
#endif
        if (OperatingSystem.IsMacOS())
        {
            return await RecognizeOnMacAsync(png, cancellationToken);
        }

        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static Task<string?> RecognizeOnMacAsync(byte[] png, CancellationToken cancellationToken) =>
        Task.Run(() => MacOcr.Recognize(png), cancellationToken);
}
