#if WINDOWS
using System.Runtime.Versioning;
using ThixClip.Core.Infrastructure;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ThixClip.Core.Platform.Windows;

/// <summary>
/// Text recognition with the OCR engine built into Windows 10/11 (Windows.Media.Ocr), in the languages of the user's
/// profile (German and English on a German Windows). No download, nothing leaves the computer.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class WindowsOcr
{
    private static OcrEngine? _engine;
    private static bool _initialized;

    public static bool IsAvailable
    {
        get
        {
            Initialize();
            return _engine is not null;
        }
    }

    public static async Task<string?> RecognizeAsync(byte[] png, CancellationToken cancellationToken)
    {
        Initialize();
        if (_engine is null)
        {
            return null;
        }

        // The engine refuses images above its size limit.
        var fitted = ImageCodec.FitInto(png, (int)OcrEngine.MaxImageDimension);
        using var stream = new MemoryStream(fitted).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
        var result = await _engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        var lines = result.Lines.Select(line => line.Text.Trim()).Where(line => line.Length > 0).ToList();
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                      ?? OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"));
        }
        catch (Exception exception)
        {
            Log.Warning($"Windows OCR is not available: {exception.Message}");
        }

        if (_engine is null)
        {
            Log.Warning("Windows OCR: no recognizer language installed");
        }
    }
}
#endif
