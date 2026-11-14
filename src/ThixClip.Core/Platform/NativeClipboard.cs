using ThixClip.Core.Platform.Mac;
using ThixClip.Core.Platform.Windows;

namespace ThixClip.Core.Platform;

/// <summary>
/// The system clipboard, the same on Windows and macOS: a change counter (cheap to poll), reading a snapshot and
/// writing text or an image. Tests replace it with <see cref="UseForTests"/>.
/// </summary>
internal static class NativeClipboard
{
    private static IClipboardBackend? _testBackend;

    public static bool IsSupported => _testBackend is not null || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static long ChangeCount
    {
        get
        {
            if (_testBackend is not null)
            {
                return _testBackend.ChangeCount;
            }

            if (OperatingSystem.IsWindows())
            {
                return WindowsClipboard.ChangeCount;
            }

            return OperatingSystem.IsMacOS() ? MacClipboard.ChangeCount : 0;
        }
    }

    public static ClipboardSnapshot Read(bool includeImages)
    {
        if (_testBackend is not null)
        {
            return _testBackend.Read();
        }

        if (OperatingSystem.IsWindows())
        {
            return WindowsClipboard.Read(includeImages);
        }

        return OperatingSystem.IsMacOS() ? MacClipboard.Read(includeImages) : ClipboardSnapshot.Empty;
    }

    public static void WriteText(string text)
    {
        if (_testBackend is not null)
        {
            _testBackend.Write(new ClipboardSnapshot(text, null, false, false));
        }
        else if (OperatingSystem.IsWindows())
        {
            WindowsClipboard.WriteText(text);
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacClipboard.WriteText(text);
        }
    }

    public static void WriteImage(byte[] png)
    {
        if (_testBackend is not null)
        {
            _testBackend.Write(new ClipboardSnapshot(null, png, false, false));
        }
        else if (OperatingSystem.IsWindows())
        {
            WindowsClipboard.WriteImage(png);
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacClipboard.WriteImage(png);
        }
    }

    internal static void UseForTests(IClipboardBackend? backend) => _testBackend = backend;
}

/// <summary>A clipboard in memory for tests.</summary>
internal interface IClipboardBackend
{
    long ChangeCount { get; }

    ClipboardSnapshot Read();

    void Write(ClipboardSnapshot snapshot);
}
