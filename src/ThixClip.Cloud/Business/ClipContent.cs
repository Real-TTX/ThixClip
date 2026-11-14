using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ThixClip.Shared;

namespace ThixClip.Cloud.Business;

/// <summary>What a clip is made of: the hash that makes the same content one clip, the kind of a text, the short form of a text for lists.</summary>
public static partial class ClipContent
{
    [GeneratedRegex("^[0-9A-F]{64}$")]
    private static partial Regex HashRegex();

    /// <summary>SHA-256 of the text as UTF-8, hex upper case (exactly as the desktop computes it; the text is not changed first).</summary>
    public static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>SHA-256 of bytes (the PNG of an image), hex upper case.</summary>
    public static string HashBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>A content hash as the contract writes it (upper case), or null when it is none.</summary>
    public static string? NormalizeHash(string? hash)
    {
        var upper = (hash ?? string.Empty).Trim().ToUpperInvariant();
        return HashRegex().IsMatch(upper) ? upper : null;
    }

    /// <summary>A link is a text that is exactly one http(s) address (spaces around it do not count).</summary>
    public static bool IsLink(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 4000 || trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0;
    }

    public static ClipKind KindOfText(string text) => IsLink(text) ? ClipKind.Link : ClipKind.Text;

    /// <summary>The start of a text for a list: one line, at most <paramref name="max"/> characters.</summary>
    public static string Preview(string? text, int max = 160)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var span = text.Length > max * 4 ? text[..(max * 4)] : text;
        var oneLine = string.Join(' ', span.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return oneLine.Length <= max ? oneLine : oneLine[..max].TrimEnd() + "…";
    }

    public static string KindName(ClipKind kind) => kind switch
    {
        ClipKind.Link => "Link",
        ClipKind.Image => "Bild",
        _ => "Text",
    };

    public static string KindIcon(ClipKind kind) => kind switch
    {
        ClipKind.Link => "link",
        ClipKind.Image => "image",
        _ => "file-text",
    };
}
