using MatCloud.Contract.Clip;

namespace ThixClip.Core.Models;

/// <summary>Whether the text of an image has been recognised.</summary>
public enum OcrState
{
    /// <summary>Not tried yet (queued).</summary>
    Pending = 0,

    /// <summary>Text recognised (<see cref="ClipEntry.OcrText"/>).</summary>
    Done = 1,

    /// <summary>Tried, the image holds no text.</summary>
    NoText = 2,

    /// <summary>No OCR engine on this computer, or OCR is switched off.</summary>
    Unavailable = 3,
}

/// <summary>What still has to go to the cloud for an entry.</summary>
public enum SyncState
{
    Synced = 0,

    /// <summary>The entry (or its deletion) has to be pushed.</summary>
    Pending = 1,

    /// <summary>The entry is pushed, its PNG still has to be uploaded.</summary>
    ImagePending = 2,
}

/// <summary>
/// One entry of the clipboard history as the app shows it. Immutable: the business layer replaces an entry with a
/// changed copy (<c>with</c>) and tells the UI through <see cref="Business.ClipService.Changed"/>. The PNG and the
/// thumbnail of an image are not part of it (they are read on demand).
/// </summary>
public sealed record ClipEntry
{
    public long Id { get; init; }

    public Guid SyncId { get; init; }

    public ClipKind Kind { get; init; }

    /// <summary>The text of a text or link entry; null for an image.</summary>
    public string? Text { get; init; }

    public string? OcrText { get; init; }

    public OcrState OcrState { get; init; }

    public string ContentHash { get; init; } = "";

    public bool IsFavorite { get; init; }

    public bool IsPinned { get; init; }

    /// <summary>When the content was copied first.</summary>
    public DateTimeOffset CreateDate { get; init; }

    /// <summary>When the content was copied last (the list is ordered by it).</summary>
    public DateTimeOffset CopiedDate { get; init; }

    /// <summary>The last change (favourite, pin, text, OCR): last writer wins in the cloud.</summary>
    public DateTimeOffset UpdateDate { get; init; }

    /// <summary>Where it was copied: this computer, another device or "Web".</summary>
    public string? Source { get; init; }

    public int? ImageWidth { get; init; }

    public int? ImageHeight { get; init; }

    public long? ImageBytes { get; init; }

    /// <summary>True when the PNG is on this computer (an image from another device may still be downloading).</summary>
    public bool HasImage { get; init; }

    public SyncState SyncState { get; init; }

    /// <summary>The cloud revision this entry was last seen with (0 = never synced).</summary>
    public long Revision { get; init; }

    public bool IsImage => Kind == ClipKind.Image;

    /// <summary>The text that search looks at: the text, or the recognised text of an image.</summary>
    public string SearchText => Kind == ClipKind.Image ? OcrText ?? "" : Text ?? "";

    /// <summary>Kept by the retention clean-up no matter how old.</summary>
    public bool IsKeptForever => IsFavorite || IsPinned;
}
