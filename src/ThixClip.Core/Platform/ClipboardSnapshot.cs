namespace ThixClip.Core.Platform;

/// <summary>
/// What the clipboard held at one moment. <see cref="IsConfidential"/>: a password manager marked it as not to be
/// recorded. <see cref="Image"/> is raw image data (PNG or a Windows DIB, see <see cref="ImageIsDib"/>).
/// </summary>
internal sealed record ClipboardSnapshot(string? Text, byte[]? Image, bool ImageIsDib, bool IsConfidential)
{
    public static readonly ClipboardSnapshot Empty = new(null, null, false, false);

    public bool HasContent => !string.IsNullOrEmpty(Text) || Image is { Length: > 0 };
}
