using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ThixClip.Core.Platform.Mac;

/// <summary>
/// Paste into the app that was in front before ThixClip opened: remember it (NSWorkspace.frontmostApplication), bring
/// it back and press Cmd+V for the user (a CGEvent). Sending keys to other apps needs the accessibility permission
/// (System Settings > Privacy &amp; Security > Accessibility); without it ThixClip only copies.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacPaste
{
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const ushort KeyCodeV = 0x09;
    private const ulong CommandFlag = 0x00100000;
    private const int ActivateIgnoringOtherApps = 1 << 1;

    private static IntPtr _previousApplication;

    [DllImport(ApplicationServices)]
    private static extern IntPtr CGEventSourceCreate(int stateId);

    [DllImport(ApplicationServices)]
    private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort keyCode, [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [DllImport(ApplicationServices)]
    private static extern void CGEventSetFlags(IntPtr cgEvent, ulong flags);

    [DllImport(ApplicationServices)]
    private static extern void CGEventPost(uint tap, IntPtr cgEvent);

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrusted();

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);

    /// <summary>True when macOS allows ThixClip to press keys in other apps.</summary>
    public static bool IsAllowed => AXIsProcessTrusted();

    /// <summary>Asks macOS to show its accessibility prompt (once; afterwards the user has to switch it on in the settings).</summary>
    public static void RequestPermission()
    {
        using var pool = ObjC.Pool();
        var yes = ObjC.SendWithBool(ObjC.Class("NSNumber"), ObjC.Selector("numberWithBool:"), true);
        var options = ObjC.Send(ObjC.Class("NSDictionary"), ObjC.Selector("dictionaryWithObject:forKey:"), yes, ObjC.String("AXTrustedCheckOptionPrompt"));
        AXIsProcessTrustedWithOptions(options);
    }

    public static void RememberForeground()
    {
        using var pool = ObjC.Pool();
        ObjC.LoadFramework("AppKit");
        var workspace = ObjC.Send(ObjC.Class("NSWorkspace"), ObjC.Selector("sharedWorkspace"));
        var application = ObjC.Send(workspace, ObjC.Selector("frontmostApplication"));
        if (application == IntPtr.Zero)
        {
            return;
        }

        var processId = (int)ObjC.SendInteger(application, ObjC.Selector("processIdentifier"));
        if (processId == Environment.ProcessId)
        {
            return;
        }

        if (_previousApplication != IntPtr.Zero)
        {
            ObjC.Send(_previousApplication, ObjC.Selector("release"));
        }

        _previousApplication = ObjC.Send(application, ObjC.Selector("retain"));
    }

    public static bool HasTarget => _previousApplication != IntPtr.Zero;

    /// <summary>Brings the remembered app back (also when ThixClip only copies, so the user lands where they were).</summary>
    public static void ActivatePrevious()
    {
        if (_previousApplication == IntPtr.Zero)
        {
            return;
        }

        using var pool = ObjC.Pool();
        ObjC.SendWithInteger(_previousApplication, ObjC.Selector("activateWithOptions:"), ActivateIgnoringOtherApps);
    }

    public static async Task PasteAsync()
    {
        ActivatePrevious();
        if (!IsAllowed)
        {
            return;
        }

        await Task.Delay(120); // the app has to be in front before the keys arrive
        var source = CGEventSourceCreate(0); // kCGEventSourceStateCombinedSessionState
        var down = CGEventCreateKeyboardEvent(source, KeyCodeV, true);
        var up = CGEventCreateKeyboardEvent(source, KeyCodeV, false);
        CGEventSetFlags(down, CommandFlag);
        CGEventSetFlags(up, CommandFlag);
        CGEventPost(0, down); // kCGHIDEventTap
        CGEventPost(0, up);
        CFRelease(down);
        CFRelease(up);
        if (source != IntPtr.Zero)
        {
            CFRelease(source);
        }
    }
}
