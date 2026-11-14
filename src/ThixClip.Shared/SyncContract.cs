using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThixClip.Shared;

/// <summary>
/// The sync protocol of ThixClip.Cloud. Every route below /api/v1 except <see cref="DeviceLogin"/> needs the device token as
/// "Authorization: Bearer &lt;token&gt;"; an unknown or removed device answers 401 (the desktop then signs out).
///
/// How a device stays in sync:
/// 1. Local changes (new entry, favourite, pin, delete) are sent with <see cref="Push"/> right away. An image entry is pushed
///    first, then its PNG is uploaded with PUT <see cref="Image"/>.
/// 2. <see cref="Wait"/> is a long poll: it answers as soon as the account has a revision above "since" (or after the timeout).
/// 3. <see cref="Changes"/> returns everything above "since" in revision order (deleted entries included, as tombstones);
///    the device stores the last revision it has seen and asks again while <see cref="ChangeSet.HasMore"/> is true.
/// Every change on the server (push, image upload, a change in the browser, the retention clean-up) gets a new revision
/// from one sequence, so nothing is ever missed.
/// </summary>
public static class SyncContract
{
    /// <summary>Raised when a change is not backwards compatible. Sent as header "X-ThixClip-Protocol".</summary>
    public const int ProtocolVersion = 1;

    public const string ProtocolHeader = "X-ThixClip-Protocol";

    public const string DeviceLogin = "/api/v1/devices/login";
    public const string DeviceLogout = "/api/v1/devices/logout";
    public const string Account = "/api/v1/account";
    public const string Changes = "/api/v1/clips/changes";
    public const string Wait = "/api/v1/clips/wait";
    public const string Push = "/api/v1/clips/push";

    /// <summary>GET: the PNG of an image entry; PUT (body image/png): uploads it.</summary>
    public static string Image(Guid syncId) => $"/api/v1/clips/{syncId:D}/image";

    /// <summary>GET: a small PNG preview (longest side <see cref="ThumbnailSize"/> px).</summary>
    public static string Thumbnail(Guid syncId) => $"/api/v1/clips/{syncId:D}/thumbnail";

    /// <summary>Longest side of a thumbnail in pixels.</summary>
    public const int ThumbnailSize = 320;

    /// <summary>The largest text of one entry (characters). Longer copies are not recorded.</summary>
    public const int MaxTextLength = 1_000_000;

    /// <summary>The largest image of one entry (bytes of the PNG).</summary>
    public const long MaxImageBytes = 25L * 1024 * 1024;

    /// <summary>The most entries one push or one change set carries.</summary>
    public const int MaxBatchSize = 200;

    /// <summary>Longest wait of a long poll in seconds.</summary>
    public const int MaxWaitSeconds = 30;

    /// <summary>The retention choices in days (0 = keep forever). Favourites and pinned entries are always kept.</summary>
    public static readonly IReadOnlyList<int> RetentionChoices = [1, 7, 30, 90, 365, 0];

    public const int DefaultRetentionDays = 30;

    /// <summary>The JSON settings of every request and response (camelCase, enums as text).</summary>
    public static readonly JsonSerializerOptions Json = CreateJsonOptions();

    public static bool IsValidRetention(int days) => RetentionChoices.Contains(days);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>What an entry holds. A link is a text that is one http(s) address.</summary>
public enum ClipKind
{
    Text = 1,
    Link = 2,
    Image = 3,
}

/// <summary>POST <see cref="SyncContract.DeviceLogin"/>: signs a device in with the account's user name and password.</summary>
public sealed record DeviceLoginRequest(
    string UserName,
    string Password,
    string DeviceName,
    string Platform,
    string AppVersion);

/// <summary>The answer of a successful device login: the token is shown once and identifies the device from now on.</summary>
public sealed record DeviceLoginResponse(
    string Token,
    long DeviceId,
    AccountInfo Account);

/// <summary>GET <see cref="SyncContract.Account"/>: the account as a device sees it.</summary>
public sealed record AccountInfo(
    string UserName,
    string DisplayName,
    int RetentionDays,
    string DeviceName,
    string ServerVersion);

/// <summary>PUT <see cref="SyncContract.Account"/>: settings a device may change for the whole account.</summary>
public sealed record AccountSettingsUpdate(int RetentionDays);

/// <summary>
/// One entry as it travels between device and cloud. <see cref="SyncId"/> is created by whoever created the entry (device or
/// browser) and never changes. <see cref="Revision"/> is set by the server (0 in a push).
/// </summary>
public sealed record ClipDto
{
    public required Guid SyncId { get; init; }

    public required ClipKind Kind { get; init; }

    /// <summary>The text of a text or link entry; null for an image.</summary>
    public string? Text { get; init; }

    /// <summary>Text recognised in an image (OCR); null when none was recognised (yet).</summary>
    public string? OcrText { get; init; }

    /// <summary>SHA-256 (hex, upper case) of the text (UTF-8) or of the PNG: the same content is one entry.</summary>
    public required string ContentHash { get; init; }

    public bool IsFavorite { get; init; }

    public bool IsPinned { get; init; }

    /// <summary>When the content was copied first.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the content was copied last: the list is ordered by it.</summary>
    public required DateTimeOffset CopiedAt { get; init; }

    /// <summary>When the entry was changed last on the device or in the browser (last writer wins).</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    public int? ImageWidth { get; init; }

    public int? ImageHeight { get; init; }

    /// <summary>Size of the PNG in bytes.</summary>
    public long? ImageBytes { get; init; }

    /// <summary>True when the server has the PNG of an image entry (it can be downloaded).</summary>
    public bool HasImage { get; init; }

    /// <summary>Where it was copied: the device name, or "Web".</summary>
    public string? Source { get; init; }

    /// <summary>A deleted entry (tombstone). Deleting wins over every other change.</summary>
    public bool IsDeleted { get; init; }

    public long Revision { get; init; }
}

/// <summary>GET <see cref="SyncContract.Changes"/>?since=&amp;limit=: the changes above a revision, oldest first.</summary>
public sealed record ChangeSet(
    IReadOnlyList<ClipDto> Clips,
    long Latest,
    bool HasMore,
    int RetentionDays);

/// <summary>GET <see cref="SyncContract.Wait"/>?since=&amp;timeout=: the latest revision of the account.</summary>
public sealed record WaitResult(long Latest);

/// <summary>POST <see cref="SyncContract.Push"/>: local changes of a device (Revision and HasImage are ignored).</summary>
public sealed record PushRequest(IReadOnlyList<ClipDto> Clips);

/// <summary>
/// What the server did with one pushed entry. <see cref="MergedInto"/> is set when the account already had the same content
/// under another SyncId: the server moved that entry to the top instead of keeping a second one, and the device re-keys its
/// local entry to that SyncId.
/// </summary>
public sealed record PushResult(Guid SyncId, long Revision, Guid? MergedInto, bool IsDeleted, bool NeedsImage);

public sealed record PushResponse(IReadOnlyList<PushResult> Results, long Latest);

/// <summary>The body of every error of the API (4xx/5xx), with a German message for the user.</summary>
public sealed record ApiError(string Message, string? Field = null);
