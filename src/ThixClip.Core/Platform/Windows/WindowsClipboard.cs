using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ThixClip.Core.Infrastructure;

namespace ThixClip.Core.Platform.Windows;

/// <summary>
/// The Windows clipboard through Win32. Reading takes text (CF_UNICODETEXT) or an image ("PNG", else CF_DIBV5/CF_DIB);
/// files copied in Explorer are not recorded. Content that a password manager marks as confidential
/// ("ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore", "CanIncludeInClipboardHistory" = 0) is
/// reported as such and not read at all. Writing puts text, or an image as PNG and DIB.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsClipboard
{
    private static readonly uint PngFormat = Win32.RegisterClipboardFormat("PNG");
    private static readonly uint ExcludeFormat = Win32.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint ViewerIgnoreFormat = Win32.RegisterClipboardFormat("Clipboard Viewer Ignore");
    private static readonly uint HistoryFormat = Win32.RegisterClipboardFormat("CanIncludeInClipboardHistory");

    /// <summary>Changes whenever any program writes the clipboard.</summary>
    public static uint ChangeCount => Win32.GetClipboardSequenceNumber();

    public static ClipboardSnapshot Read(bool includeImages)
    {
        if (!Open(IntPtr.Zero))
        {
            return ClipboardSnapshot.Empty;
        }

        try
        {
            if (IsConfidential())
            {
                return new ClipboardSnapshot(null, null, false, IsConfidential: true);
            }

            var text = Win32.IsClipboardFormatAvailable(Win32.CfUnicodeText) ? ReadText() : null;
            if (!string.IsNullOrEmpty(text))
            {
                return new ClipboardSnapshot(text, null, false, false);
            }

            if (!includeImages || Win32.IsClipboardFormatAvailable(Win32.CfHdrop))
            {
                return ClipboardSnapshot.Empty;
            }

            if (PngFormat != 0 && Win32.IsClipboardFormatAvailable(PngFormat) && ReadBytes(PngFormat) is { Length: > 0 } png)
            {
                return new ClipboardSnapshot(null, png, false, false);
            }

            var dibFormat = Win32.IsClipboardFormatAvailable(Win32.CfDibV5) ? Win32.CfDibV5 : Win32.CfDib;
            if (Win32.IsClipboardFormatAvailable(dibFormat) && ReadBytes(dibFormat) is { Length: > 0 } dib)
            {
                return new ClipboardSnapshot(null, dib, ImageIsDib: true, false);
            }

            return ClipboardSnapshot.Empty;
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    public static void WriteText(string text)
    {
        var bytes = new byte[(text.Length + 1) * 2];
        System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        Write([(Win32.CfUnicodeText, bytes)]);
    }

    /// <summary>PNG for programs that read it (keeps transparency), DIB for all others.</summary>
    public static void WriteImage(byte[] png)
    {
        var formats = new List<(uint, byte[])>();
        if (PngFormat != 0)
        {
            formats.Add((PngFormat, png));
        }

        formats.Add((Win32.CfDib, ImageCodec.ToDib(png)));
        Write(formats);
    }

    private static void Write(IReadOnlyList<(uint Format, byte[] Data)> formats)
    {
        if (!Open(WindowsKeyboardHook.ClipboardWindow))
        {
            throw new IOException("Die Zwischenablage ist gerade von einem anderen Programm belegt.");
        }

        try
        {
            Win32.EmptyClipboard();
            foreach (var (format, data) in formats)
            {
                var memory = Win32.GlobalAlloc(Win32.GmemMoveable, (UIntPtr)data.Length);
                if (memory == IntPtr.Zero)
                {
                    throw new IOException("Kein Speicher für die Zwischenablage.");
                }

                var target = Win32.GlobalLock(memory);
                Marshal.Copy(data, 0, target, data.Length);
                Win32.GlobalUnlock(memory);
                if (Win32.SetClipboardData(format, memory) == IntPtr.Zero)
                {
                    Win32.GlobalFree(memory); // only when the clipboard did not take it
                    Log.Warning($"SetClipboardData({format}) failed: {Marshal.GetLastWin32Error()}");
                }
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    /// <summary>Another program may hold the clipboard for a moment: try a few times.</summary>
    private static bool Open(IntPtr owner)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (Win32.OpenClipboard(owner))
            {
                return true;
            }

            Thread.Sleep(15);
        }

        return false;
    }

    private static bool IsConfidential()
    {
        if ((ExcludeFormat != 0 && Win32.IsClipboardFormatAvailable(ExcludeFormat)) ||
            (ViewerIgnoreFormat != 0 && Win32.IsClipboardFormatAvailable(ViewerIgnoreFormat)))
        {
            return true;
        }

        // "CanIncludeInClipboardHistory" = 0 (a DWORD) means: not for any history.
        return HistoryFormat != 0 && Win32.IsClipboardFormatAvailable(HistoryFormat) &&
               ReadBytes(HistoryFormat) is { Length: >= 4 } value && BitConverter.ToInt32(value, 0) == 0;
    }

    private static string? ReadText()
    {
        var bytes = ReadBytes(Win32.CfUnicodeText);
        if (bytes is null)
        {
            return null;
        }

        var text = System.Text.Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    private static byte[]? ReadBytes(uint format)
    {
        var handle = Win32.GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var size = (long)(ulong)Win32.GlobalSize(handle);
        if (size <= 0 || size > MatCloud.Contract.Clip.ClipContract.MaxImageBytes * 4)
        {
            return null;
        }

        var pointer = Win32.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }
    }
}
