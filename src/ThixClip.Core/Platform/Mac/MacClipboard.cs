using System.Runtime.Versioning;

namespace ThixClip.Core.Platform.Mac;

/// <summary>
/// The general pasteboard of macOS (NSPasteboard). Text is "public.utf8-plain-text", images "public.png" or
/// "public.tiff" (turned into PNG). Files copied in the Finder are not recorded. Content that apps mark after
/// nspasteboard.org ("org.nspasteboard.ConcealedType" from password managers, "TransientType" for temporary data) is
/// reported as confidential and not read.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacClipboard
{
    private const string TextType = "public.utf8-plain-text";
    private const string PngType = "public.png";
    private const string TiffType = "public.tiff";
    private const int PngFileType = 4; // NSBitmapImageFileTypePNG

    public static long ChangeCount
    {
        get
        {
            using var pool = ObjC.Pool();
            return ObjC.SendInteger(Pasteboard, ObjC.Selector("changeCount"));
        }
    }

    public static ClipboardSnapshot Read(bool includeImages)
    {
        using var pool = ObjC.Pool();
        var pasteboard = Pasteboard;
        var types = ObjC.Send(pasteboard, ObjC.Selector("types"));
        if (ObjC.Contains(types, "org.nspasteboard.ConcealedType") || ObjC.Contains(types, "org.nspasteboard.TransientType"))
        {
            return new ClipboardSnapshot(null, null, false, IsConfidential: true);
        }

        if (ObjC.Contains(types, "public.file-url"))
        {
            return ClipboardSnapshot.Empty;
        }

        var text = ObjC.ToManagedString(ObjC.Send(pasteboard, ObjC.Selector("stringForType:"), ObjC.String(TextType)));
        if (!string.IsNullOrEmpty(text))
        {
            return new ClipboardSnapshot(text, null, false, false);
        }

        if (!includeImages)
        {
            return ClipboardSnapshot.Empty;
        }

        var png = ObjC.ToBytes(ObjC.Send(pasteboard, ObjC.Selector("dataForType:"), ObjC.String(PngType)));
        if (png is { Length: > 0 })
        {
            return new ClipboardSnapshot(null, png, false, false);
        }

        var tiff = ObjC.Send(pasteboard, ObjC.Selector("dataForType:"), ObjC.String(TiffType));
        return tiff == IntPtr.Zero ? ClipboardSnapshot.Empty : new ClipboardSnapshot(null, TiffToPng(tiff), false, false);
    }

    public static void WriteText(string text)
    {
        using var pool = ObjC.Pool();
        var pasteboard = Pasteboard;
        ObjC.SendInteger(pasteboard, ObjC.Selector("clearContents"));
        ObjC.SendBool(pasteboard, ObjC.Selector("setString:forType:"), ObjC.String(text), ObjC.String(TextType));
    }

    /// <summary>PNG plus TIFF: older apps only read TIFF.</summary>
    public static void WriteImage(byte[] png)
    {
        using var pool = ObjC.Pool();
        var pasteboard = Pasteboard;
        var data = ObjC.Data(png);
        ObjC.SendInteger(pasteboard, ObjC.Selector("clearContents"));
        ObjC.SendBool(pasteboard, ObjC.Selector("setData:forType:"), data, ObjC.String(PngType));
        var representation = ObjC.Send(ObjC.Class("NSBitmapImageRep"), ObjC.Selector("imageRepWithData:"), data);
        if (representation != IntPtr.Zero)
        {
            var tiff = ObjC.Send(representation, ObjC.Selector("TIFFRepresentation"));
            if (tiff != IntPtr.Zero)
            {
                ObjC.SendBool(pasteboard, ObjC.Selector("setData:forType:"), tiff, ObjC.String(TiffType));
            }
        }
    }

    private static IntPtr Pasteboard
    {
        get
        {
            ObjC.LoadFramework("AppKit");
            return ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Selector("generalPasteboard"));
        }
    }

    private static byte[]? TiffToPng(IntPtr tiff)
    {
        var representation = ObjC.Send(ObjC.Class("NSBitmapImageRep"), ObjC.Selector("imageRepWithData:"), tiff);
        if (representation == IntPtr.Zero)
        {
            return null;
        }

        var properties = ObjC.Send(ObjC.Class("NSDictionary"), ObjC.Selector("dictionary"));
        return ObjC.ToBytes(ObjC.SendWithIntegerAndObject(representation, ObjC.Selector("representationUsingType:properties:"), PngFileType, properties));
    }
}
