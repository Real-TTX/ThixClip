using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ThixClip.Core.Platform.Windows;

/// <summary>
/// Paste into the program that was in front before ThixClip opened: remember its window when the shortcut is pressed,
/// give it the focus back and press Ctrl+V for the user. Modifier keys the user still holds are released first, otherwise
/// the program would see e.g. Ctrl+Alt+V.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsPaste
{
    private static IntPtr _previousWindow;

    public static void RememberForeground(IntPtr ownWindow)
    {
        var foreground = Win32.GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != ownWindow && !IsOwnProcess(foreground))
        {
            _previousWindow = foreground;
        }
    }

    public static bool HasTarget => _previousWindow != IntPtr.Zero && Win32.IsWindow(_previousWindow);

    public static async Task PasteAsync()
    {
        if (!HasTarget)
        {
            return;
        }

        Win32.SetForegroundWindow(_previousWindow);
        await Task.Delay(60); // the target has to be in front before the keys arrive

        var inputs = new List<Win32.Input>();
        foreach (var key in new[] { Win32.VkLWin, Win32.VkRWin, Win32.VkLMenu, Win32.VkRMenu, Win32.VkLShift, Win32.VkRShift })
        {
            if (Win32.IsKeyDown(key))
            {
                inputs.Add(Key(key, up: true));
            }
        }

        inputs.Add(Key(Win32.VkControl, up: false));
        inputs.Add(Key(Win32.VkV, up: false));
        inputs.Add(Key(Win32.VkV, up: true));
        inputs.Add(Key(Win32.VkControl, up: true));
        Win32.SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<Win32.Input>());
    }

    private static Win32.Input Key(int virtualKey, bool up) => new()
    {
        Type = Win32.InputKeyboard,
        Data = new Win32.InputUnion { Keyboard = new Win32.KeyboardInput { Vk = (ushort)virtualKey, Flags = up ? Win32.KeyEventFKeyUp : 0, ExtraInfo = Win32.OwnInputMarker } },
    };

    private static bool IsOwnProcess(IntPtr window)
    {
        Win32.GetWindowThreadProcessId(window, out var processId);
        return processId == Environment.ProcessId;
    }
}
