using ThixClip.Shared;

namespace ThixClip.Cloud.Data;

/// <summary>What every record has: an Id (BIGINT) and who created and changed it, and when (the columns are filled by <see cref="AppDbContext"/>).</summary>
public abstract class Entity
{
    public long Id { get; set; }

    public DateTime CreateDate { get; set; }

    public long? CreateUserId { get; set; }

    public DateTime UpdateDate { get; set; }

    public long? UpdateUserId { get; set; }
}

/// <summary>A record that supports soft delete: 0 = deleted, 1 = created, 2 = updated.</summary>
public abstract class SoftEntity : Entity
{
    public const short Deleted = 0;
    public const short Created = 1;
    public const short Updated = 2;

    public short UpdateState { get; set; } = Created;
}

/// <summary>An account. Its clips are its own: nobody else sees them, an administrator neither.</summary>
public sealed class User : SoftEntity
{
    /// <summary>The name to sign in with (desktop and browser); unique, stored in lower case.</summary>
    public string UserName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public bool IsAdmin { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>A password set by an administrator: the person chooses their own at the next sign-in.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>How long clips are kept, in days (0 = forever). Favourites and pinned clips are always kept.</summary>
    public int RetentionDays { get; set; } = SyncContract.DefaultRetentionDays;

    /// <summary>system, light or dark.</summary>
    public string Theme { get; set; } = "system";

    public int FailedLogins { get; set; }

    public DateTime? LockoutUntil { get; set; }

    public DateTime? LastLoginDate { get; set; }
}

/// <summary>A signed-in browser. The cookie carries the <see cref="Token"/>; the session lives in the database.</summary>
public sealed class UserSession : Entity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    public User? User { get; set; }

    public DateTime LastSeenDate { get; set; }

    public DateTime ExpiresDate { get; set; }

    public bool IsRevoked { get; set; }

    public string? UserAgent { get; set; }

    public string? IpAddress { get; set; }
}

/// <summary>
/// A desktop app that signed in. It sends its <see cref="Token"/> as a bearer token with every request; removing the device (soft delete)
/// ends its access at once.
/// </summary>
public sealed class Device : SoftEntity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    public User? User { get; set; }

    /// <summary>The name the desktop gave itself (usually the computer name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>windows, macos ...</summary>
    public string Platform { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    public DateTime LastSeenDate { get; set; }

    public string? IpAddress { get; set; }
}

/// <summary>
/// One clip of an account. A deleted clip stays as a tombstone (<see cref="SoftEntity.UpdateState"/> 0, its <see cref="SyncId"/> and
/// <see cref="Revision"/> kept, its content wiped) so that every device learns about the deletion.
/// </summary>
public sealed class ClipEntry : SoftEntity
{
    public long UserId { get; set; }

    public User? User { get; set; }

    /// <summary>The id the creator (a device or the browser) gave the clip; unique per account, never changes.</summary>
    public Guid SyncId { get; set; }

    public ClipKind Kind { get; set; }

    public string? Text { get; set; }

    public string? OcrText { get; set; }

    /// <summary>SHA-256 of the text (UTF-8) or of the PNG, hex upper case: the same content is one clip.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public bool IsFavorite { get; set; }

    public bool IsPinned { get; set; }

    /// <summary>When the content was copied the last time (the order of the list).</summary>
    public DateTime CopiedDate { get; set; }

    /// <summary>When it was copied the first time (on the device that created it).</summary>
    public DateTime ClientCreateDate { get; set; }

    /// <summary>The last change on a device or in the browser: an older change does not overwrite a newer one.</summary>
    public DateTime ClientUpdateDate { get; set; }

    /// <summary>Where it was copied: the name of the device, or "Web".</summary>
    public string? Source { get; set; }

    public bool HasImage { get; set; }

    public int? ImageWidth { get; set; }

    public int? ImageHeight { get; set; }

    public long? ImageBytes { get; set; }

    /// <summary>The number of the last change, from the one sequence <c>ClipRevision</c>: devices ask for everything above the revision they know.</summary>
    public long Revision { get; set; }

    public ClipImage? Image { get; set; }
}

/// <summary>The picture of an image clip: the PNG and a small preview. A row of its own, so that lists never load the bytes.</summary>
public sealed class ClipImage : Entity
{
    public long ClipEntryId { get; set; }

    public ClipEntry? ClipEntry { get; set; }

    public byte[] Png { get; set; } = [];

    public byte[] Thumbnail { get; set; } = [];
}
