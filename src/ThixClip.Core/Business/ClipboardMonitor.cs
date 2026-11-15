using ThixClip.Core.Infrastructure;
using ThixClip.Core.Platform;

namespace ThixClip.Core.Business;

/// <summary>
/// Watches the system clipboard: a background thread compares the clipboard's change counter four times a second (a
/// single cheap system call; Windows: GetClipboardSequenceNumber, macOS: NSPasteboard.changeCount) and records a new
/// content through <see cref="ClipService"/>. Reading never happens on the UI thread.
/// </summary>
public static class ClipboardMonitor
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private static readonly AutoResetEvent Wake = new(false);
    private static Thread? _thread;
    private static volatile bool _running;
    private static long _lastChangeCount = -1;

    public static bool IsRunning => _running;

    public static void Start()
    {
        if (_running || !NativeClipboard.IsSupported)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "ThixClip clipboard monitor", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public static void Stop()
    {
        _running = false;
        Wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    /// <summary>ThixClip itself wrote the clipboard: look right away instead of waiting for the next round.</summary>
    public static void CheckSoon() => Wake.Set();

    /// <summary>One round (tests call it directly).</summary>
    internal static void CheckOnce()
    {
        var changeCount = NativeClipboard.ChangeCount;
        if (changeCount == _lastChangeCount)
        {
            return;
        }

        _lastChangeCount = changeCount;
        if (RecordingService.IsPaused)
        {
            return;
        }

        var settings = SettingsService.Get();
        var snapshot = NativeClipboard.Read(settings.RecordImages);
        ClipService.Capture(snapshot);
    }

    internal static void ResetForTests() => _lastChangeCount = -1;

    private static void Run()
    {
        while (_running)
        {
            try
            {
                CheckOnce();
            }
            catch (Exception exception)
            {
                // A program can leave the clipboard in a state that cannot be read: skip it, keep watching.
                Log.Warning($"Clipboard could not be read: {exception.GetType().Name}: {exception.Message}");
            }

            Wake.WaitOne(Interval);
        }
    }
}
