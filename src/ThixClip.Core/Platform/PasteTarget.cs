using ThixClip.Core.Platform.Mac;
using ThixClip.Core.Platform.Windows;

namespace ThixClip.Core.Platform;

/// <summary>
/// The app the user worked in before the shortcut opened ThixClip: remembered when the shortcut is pressed (before
/// ThixClip's window takes the focus), brought back after a choice, and given a Ctrl+V / Cmd+V.
/// </summary>
public static class PasteTarget
{
    /// <summary>Remember the app in front. <paramref name="ownWindow"/> is ThixClip's own window handle (Windows).</summary>
    public static void RememberForeground(IntPtr ownWindow)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsPaste.RememberForeground(ownWindow);
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacPaste.RememberForeground();
        }
    }

    /// <summary>False on macOS until the user allows ThixClip to control the computer (accessibility).</summary>
    public static bool CanSendKeys => !OperatingSystem.IsMacOS() || MacPaste.IsAllowed;

    /// <summary>macOS: show the system prompt for the accessibility permission.</summary>
    public static void RequestKeyPermission()
    {
        if (OperatingSystem.IsMacOS())
        {
            MacPaste.RequestPermission();
        }
    }

    /// <summary>Brings the remembered app back and presses Ctrl+V / Cmd+V in it.</summary>
    public static Task PasteAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsPaste.PasteAsync();
        }

        return OperatingSystem.IsMacOS() ? MacPaste.PasteAsync() : Task.CompletedTask;
    }

    /// <summary>Only brings the remembered app back (macOS; Windows does that by itself when ThixClip hides).</summary>
    public static void ReturnToPrevious()
    {
        if (OperatingSystem.IsMacOS())
        {
            MacPaste.ActivatePrevious();
        }
    }
}
