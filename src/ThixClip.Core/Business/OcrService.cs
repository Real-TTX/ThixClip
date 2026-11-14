using System.Threading.Channels;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Core.Platform;

namespace ThixClip.Core.Business;

/// <summary>
/// Recognises the text of images in the background, one after the other (Windows OCR / Apple Vision, both offline).
/// The text makes images searchable and is shown in the detail pane; with the cloud it travels to the other devices,
/// so each image is read only once.
/// </summary>
public static class OcrService
{
    private static readonly Channel<long> Queue = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
    private static CancellationTokenSource? _stop;

    public static bool IsAvailable => TextRecognition.IsAvailable;

    public static void Start()
    {
        if (_stop is not null)
        {
            return;
        }

        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _ = Task.Run(() => RunAsync(token), token);
        foreach (var id in ClipService.WaitingForOcr())
        {
            Enqueue(id);
        }
    }

    public static void Stop()
    {
        _stop?.Cancel();
        _stop = null;
    }

    internal static void Enqueue(long id) => Queue.Writer.TryWrite(id);

    private static async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var id in Queue.Reader.ReadAllAsync(cancellationToken))
        {
            var entry = ClipService.Get(id);
            if (entry is not { OcrState: OcrState.Pending, HasImage: true })
            {
                continue;
            }

            if (!SettingsService.Get().RecognizeText || !TextRecognition.IsAvailable)
            {
                ClipService.SetOcrResult(id, null, OcrState.Unavailable);
                continue;
            }

            try
            {
                var png = ClipService.ReadImage(id);
                if (png is null)
                {
                    continue;
                }

                var text = await TextRecognition.RecognizeAsync(png, cancellationToken);
                ClipService.SetOcrResult(id, text, string.IsNullOrWhiteSpace(text) ? OcrState.NoText : OcrState.Done);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                Log.Warning($"OCR failed for entry {id}: {exception.GetType().Name}: {exception.Message}");
                ClipService.SetOcrResult(id, null, OcrState.Unavailable);
            }
        }
    }
}
