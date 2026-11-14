using System.Runtime.Versioning;
using ThixClip.Core.Infrastructure;

namespace ThixClip.Core.Platform.Mac;

/// <summary>
/// Text recognition with Apple's Vision framework (VNRecognizeTextRequest, macOS 10.15+), German and English, on the
/// computer itself. performRequests:error: runs synchronously, so no completion block is needed.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacOcr
{
    private const nint AccurateLevel = 0; // VNRequestTextRecognitionLevelAccurate

    public static bool IsAvailable => OperatingSystem.IsMacOSVersionAtLeast(10, 15);

    public static string? Recognize(byte[] png)
    {
        if (!IsAvailable)
        {
            return null;
        }

        ObjC.LoadFramework("Vision");
        using var pool = ObjC.Pool();
        var data = ObjC.Data(ImageCodec.FitInto(png, 4096));
        var options = ObjC.Send(ObjC.Class("NSDictionary"), ObjC.Selector("dictionary"));
        var handler = ObjC.Send(ObjC.Send(ObjC.Class("VNImageRequestHandler"), ObjC.Selector("alloc")), ObjC.Selector("initWithData:options:"), data, options);
        var request = ObjC.Send(ObjC.Send(ObjC.Class("VNRecognizeTextRequest"), ObjC.Selector("alloc")), ObjC.Selector("init"));
        if (handler == IntPtr.Zero || request == IntPtr.Zero)
        {
            Log.Warning("Vision is not available");
            return null;
        }

        try
        {
            ObjC.SendVoidWithInteger(request, ObjC.Selector("setRecognitionLevel:"), AccurateLevel);
            ObjC.SendVoidWithBool(request, ObjC.Selector("setUsesLanguageCorrection:"), true);
            ObjC.SendVoid(request, ObjC.Selector("setRecognitionLanguages:"), LanguageArray());

            var requests = ObjC.Send(ObjC.Class("NSArray"), ObjC.Selector("arrayWithObject:"), request);
            if (!ObjC.SendBool(handler, ObjC.Selector("performRequests:error:"), requests, IntPtr.Zero))
            {
                return null;
            }

            var results = ObjC.Send(request, ObjC.Selector("results"));
            var lines = new List<string>();
            for (var index = 0; index < ObjC.Count(results); index++)
            {
                var candidates = ObjC.SendWithInteger(ObjC.At(results, index), ObjC.Selector("topCandidates:"), 1);
                if (ObjC.Count(candidates) > 0 && ObjC.ToManagedString(ObjC.Send(ObjC.At(candidates, 0), ObjC.Selector("string"))) is { Length: > 0 } line)
                {
                    lines.Add(line.Trim());
                }
            }

            return lines.Count == 0 ? null : string.Join('\n', lines);
        }
        finally
        {
            ObjC.Send(handler, ObjC.Selector("release"));
            ObjC.Send(request, ObjC.Selector("release"));
        }
    }

    private static IntPtr LanguageArray()
    {
        var array = ObjC.Send(ObjC.Class("NSMutableArray"), ObjC.Selector("array"));
        ObjC.SendVoid(array, ObjC.Selector("addObject:"), ObjC.String("de-DE"));
        ObjC.SendVoid(array, ObjC.Selector("addObject:"), ObjC.String("en-US"));
        return array;
    }
}
