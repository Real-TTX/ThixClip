using System.Security.Cryptography;
using System.Text;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Core.Platform;
using ThixClip.Shared;

namespace ThixClip.Core.Business;

public enum ClipChangeKind
{
    Added,
    Updated,
    Removed,

    /// <summary>Many entries changed at once (clean-up, sync, clear): read the list again.</summary>
    Reset,
}

public sealed record ClipChange(ClipChangeKind Kind, ClipEntry? Entry = null);

/// <summary>
/// The clipboard history: what is recorded, how it is found, and every change to it. The history lives in memory
/// (opening and searching never wait for the disk) and in <see cref="HistoryStore"/>; the UI only calls this class and
/// listens to <see cref="Changed"/>.
///
/// Rules:
/// - The same content is one entry: copying it again moves it to the top (by content hash).
/// - A text that is one http(s) address is a link. Text longer than <see cref="SyncContract.MaxTextLength"/> and images
///   above <see cref="SyncContract.MaxImageBytes"/> are not recorded.
/// - Favourites and pinned entries are kept forever; everything else goes after the retention time.
/// - A deletion is kept as a tombstone until the cloud knows it (then it disappears on every device).
/// </summary>
public static class ClipService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<long, ClipEntry> Entries = [];
    private static readonly Dictionary<long, ClipEntry> Tombstones = [];
    private static bool _loaded;

    /// <summary>Raised after every change (any thread; the UI posts to its own thread).</summary>
    public static event EventHandler<ClipChange>? Changed;

    /// <summary>Loads the history (once, at start) and removes what has expired.</summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }

            foreach (var (entry, isDeleted) in HistoryStore.LoadAll())
            {
                (isDeleted ? Tombstones : Entries)[entry.Id] = entry;
            }

            _loaded = true;
        }

        RemoveOpenedFiles();
        RemoveExpired();
    }

    /// <summary>Number of entries in the history (all sections).</summary>
    public static int Count
    {
        get
        {
            EnsureLoaded();
            lock (Gate)
            {
                return Entries.Count;
            }
        }
    }

    public static ClipEntry? Get(long id)
    {
        EnsureLoaded();
        lock (Gate)
        {
            return Entries.GetValueOrDefault(id);
        }
    }

    /// <summary>
    /// The entries of a section, filtered by type and search text (every word must occur, case-insensitive, in the text or
    /// in the recognised text of an image). Pinned entries come first, then by the time of the last copy.
    /// </summary>
    public static IReadOnlyList<ClipEntry> Query(ClipQuery query)
    {
        EnsureLoaded();
        var words = query.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<ClipEntry> snapshot;
        lock (Gate)
        {
            snapshot = [.. Entries.Values];
        }

        IEnumerable<ClipEntry> result = snapshot.Where(entry => query.Section switch
        {
            ClipSection.Images => entry.Kind == ClipKind.Image,
            ClipSection.Favorites => entry.IsFavorite,
            _ => true,
        });

        if (query.Kind is { } kind)
        {
            result = result.Where(entry => entry.Kind == kind);
        }

        if (words.Length > 0)
        {
            result = result.Where(entry => words.All(word => entry.SearchText.Contains(word, StringComparison.CurrentCultureIgnoreCase)));
        }

        var ordered = query.Sort == ClipSort.OldestFirst
            ? result.OrderByDescending(entry => entry.IsPinned).ThenBy(entry => entry.CopiedDate).ThenBy(entry => entry.Id)
            : result.OrderByDescending(entry => entry.IsPinned).ThenByDescending(entry => entry.CopiedDate).ThenByDescending(entry => entry.Id);
        return ordered.ToList();
    }

    /// <summary>Records what the clipboard holds now (from <see cref="ClipboardMonitor"/>); null when nothing was recorded.</summary>
    internal static ClipEntry? Capture(ClipboardSnapshot snapshot)
    {
        var settings = SettingsService.Get();
        if (RecordingService.IsPaused || !snapshot.HasContent)
        {
            return null;
        }

        if (snapshot.IsConfidential)
        {
            if (settings.IgnoreConfidential)
            {
                Log.Info("Confidential clipboard content not recorded");
                return null;
            }

            return null; // nothing was read in the first place
        }

        if (!string.IsNullOrEmpty(snapshot.Text))
        {
            return CaptureText(snapshot.Text);
        }

        if (snapshot.Image is { } image && settings.RecordImages)
        {
            var normalized = snapshot.ImageIsDib ? ImageCodec.FromDib(image) : ImageCodec.Normalize(image);
            if (normalized is null)
            {
                return null;
            }

            return CaptureImage(normalized);
        }

        return null;
    }

    /// <summary>Records a text (also used by tests).</summary>
    internal static ClipEntry? CaptureText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > SyncContract.MaxTextLength)
        {
            return null;
        }

        var hash = HashOf(Encoding.UTF8.GetBytes(text));
        return MoveToTop(hash) ?? Add(new ClipEntry
        {
            SyncId = Guid.NewGuid(),
            Kind = IsLink(text) ? ClipKind.Link : ClipKind.Text,
            Text = text,
            ContentHash = hash,
            OcrState = OcrState.Unavailable,
        }, null, null);
    }

    internal static ClipEntry? CaptureImage(NormalizedImage image)
    {
        if (image.Png.Length > SyncContract.MaxImageBytes)
        {
            Log.Info($"Image of {image.Png.Length} bytes not recorded (limit {SyncContract.MaxImageBytes})");
            return null;
        }

        var hash = HashOf(image.Png);
        var entry = MoveToTop(hash) ?? Add(new ClipEntry
        {
            SyncId = Guid.NewGuid(),
            Kind = ClipKind.Image,
            ContentHash = hash,
            ImageWidth = image.Width,
            ImageHeight = image.Height,
            ImageBytes = image.Png.Length,
            HasImage = true,
            OcrState = SettingsService.Get().RecognizeText ? OcrState.Pending : OcrState.Unavailable,
        }, image.Png, image.Thumbnail);

        if (entry is { OcrState: OcrState.Pending })
        {
            OcrService.Enqueue(entry.Id);
        }

        return entry;
    }

    public static ClipEntry ToggleFavorite(long id) => Change(id, entry => entry with { IsFavorite = !entry.IsFavorite });

    public static ClipEntry TogglePin(long id) => Change(id, entry => entry with { IsPinned = !entry.IsPinned });

    /// <summary>Changes the text of a text or link entry (a link stays a link only while it is one address).</summary>
    public static ClipEntry UpdateText(long id, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new BusinessException("Der Text darf nicht leer sein.", nameof(ClipEntry.Text));
        }

        if (text.Length > SyncContract.MaxTextLength)
        {
            throw new BusinessException("Der Text ist zu lang.", nameof(ClipEntry.Text));
        }

        return Change(id, entry =>
        {
            if (entry.IsImage)
            {
                throw new BusinessException("Ein Bild hat keinen Text zum Bearbeiten.");
            }

            return entry with { Text = text, Kind = IsLink(text) ? ClipKind.Link : ClipKind.Text, ContentHash = HashOf(Encoding.UTF8.GetBytes(text)) };
        });
    }

    /// <summary>Deletes an entry here and, through the cloud, on every device.</summary>
    public static void Delete(long id)
    {
        ClipEntry? removed;
        lock (Gate)
        {
            if (!Entries.Remove(id, out removed))
            {
                return;
            }

            DeleteLocked(removed);
        }

        Changed?.Invoke(null, new ClipChange(ClipChangeKind.Removed, removed));
        SyncService.Kick();
    }

    /// <summary>
    /// "Verlauf löschen" as in a browser: deletes what was copied within the range (here and, through the cloud, on every
    /// device), except favourites and pinned entries; returns how many went.
    /// </summary>
    public static int ClearHistory(ClearRange range)
    {
        var start = StartOf(range);
        EnsureLoaded();
        int count;
        lock (Gate)
        {
            var doomed = Entries.Values.Where(entry => IsCleared(entry, start)).ToList();
            foreach (var entry in doomed)
            {
                Entries.Remove(entry.Id);
                DeleteLocked(entry);
            }

            count = doomed.Count;
        }

        if (count > 0)
        {
            Changed?.Invoke(null, new ClipChange(ClipChangeKind.Reset));
            SyncService.Kick();
        }

        return count;
    }

    /// <summary>How many entries <see cref="ClearHistory"/> would delete for the range (the dialog shows it before).</summary>
    public static int CountToClear(ClearRange range)
    {
        var start = StartOf(range);
        EnsureLoaded();
        lock (Gate)
        {
            return Entries.Values.Count(entry => IsCleared(entry, start));
        }
    }

    /// <summary>Puts an entry on the system clipboard (it then moves to the top, like any copy).</summary>
    public static void CopyToClipboard(long id)
    {
        var entry = Get(id) ?? throw new BusinessException("Den Eintrag gibt es nicht mehr.");
        if (entry.IsImage)
        {
            var png = entry.HasImage ? HistoryStore.ReadPng(id) : null;
            NativeClipboard.WriteImage(png ?? throw new BusinessException("Das Bild wird noch aus der Cloud geladen."));
        }
        else
        {
            NativeClipboard.WriteText(entry.Text ?? "");
        }

        ClipboardMonitor.CheckSoon();
    }

    /// <summary>Copies a text that is not (yet) an entry, e.g. the recognised text of an image.</summary>
    public static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        NativeClipboard.WriteText(text);
        ClipboardMonitor.CheckSoon();
    }

    /// <summary>
    /// The web address "Im Browser öffnen" of the viewer hands to the browser: only a link entry, only http and https;
    /// null for everything else.
    /// </summary>
    public static string? BrowserLink(long id)
    {
        var entry = Get(id) ?? throw new BusinessException("Den Eintrag gibt es nicht mehr.");
        return entry.Kind == ClipKind.Link && Uri.TryCreate(entry.Text?.Trim(), UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>The whole image of an entry for the viewer.</summary>
    public static byte[] ReadImageToView(long id)
    {
        var entry = Get(id) ?? throw new BusinessException("Den Eintrag gibt es nicht mehr.");
        if (!entry.IsImage)
        {
            throw new BusinessException("Der Eintrag ist kein Bild.");
        }

        return (entry.HasImage ? HistoryStore.ReadPng(id) : null) ?? throw new BusinessException("Das Bild wird noch aus der Cloud geladen.");
    }

    public static byte[]? ReadThumbnail(long id) => HistoryStore.ReadThumbnail(id);

    public static byte[]? ReadImage(long id) => HistoryStore.ReadPng(id);

    /// <summary>
    /// Versions up to 0.1.6 handed entries to other programs as files there; they hold clipboard contents and do not stay.
    /// </summary>
    private static void RemoveOpenedFiles()
    {
        if (!Directory.Exists(AppPaths.OpenDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(AppPaths.OpenDirectory))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // still open in another program: next time
            }
        }
    }

    /// <summary>Removes entries older than the retention time (not favourites, not pinned). Local only: the cloud applies the same rule.</summary>
    public static int RemoveExpired()
    {
        var days = SettingsService.Get().RetentionDays;
        if (days <= 0 || !_loaded)
        {
            return 0;
        }

        var cutoff = AppClock.UtcNow.AddDays(-days);
        List<ClipEntry> expired;
        lock (Gate)
        {
            expired = Entries.Values.Where(entry => !entry.IsKeptForever && entry.CopiedDate < cutoff).ToList();
            foreach (var entry in expired)
            {
                Entries.Remove(entry.Id);
            }
        }

        if (expired.Count == 0)
        {
            return 0;
        }

        HistoryStore.Remove(expired.Select(entry => entry.Id).ToList());
        Log.Info($"Retention: {expired.Count} entries older than {days} days removed");
        Changed?.Invoke(null, new ClipChange(ClipChangeKind.Reset));
        return expired.Count;
    }

    // ---- OCR ---------------------------------------------------------------------------------------------------

    internal static IReadOnlyList<long> WaitingForOcr()
    {
        EnsureLoaded();
        lock (Gate)
        {
            return Entries.Values.Where(entry => entry is { OcrState: OcrState.Pending, HasImage: true }).Select(entry => entry.Id).ToList();
        }
    }

    internal static void SetOcrResult(long id, string? text, OcrState state)
    {
        try
        {
            Change(id, entry => entry with { OcrText = text, OcrState = state });
        }
        catch (BusinessException)
        {
            // deleted meanwhile
        }
    }

    // ---- Sync (only SyncService) -----------------------------------------------------------------------------------

    /// <summary>Entries and tombstones whose change still has to go to the cloud.</summary>
    internal static List<(ClipEntry Entry, bool IsDeleted)> PendingChanges(int max)
    {
        EnsureLoaded();
        lock (Gate)
        {
            return Tombstones.Values.Where(entry => entry.SyncState == SyncState.Pending).Select(entry => (entry, true))
                .Concat(Entries.Values.Where(entry => entry.SyncState == SyncState.Pending).OrderBy(entry => entry.CopiedDate).Select(entry => (entry, false)))
                .Take(max)
                .ToList();
        }
    }

    internal static List<ClipEntry> PendingImageUploads()
    {
        lock (Gate)
        {
            return Entries.Values.Where(entry => entry is { SyncState: SyncState.ImagePending, HasImage: true }).ToList();
        }
    }

    /// <summary>After connecting: everything here goes up once.</summary>
    internal static void MarkAllForUpload()
    {
        EnsureLoaded();
        lock (Gate)
        {
            foreach (var entry in Entries.Values.ToList())
            {
                var pending = entry with { SyncState = SyncState.Pending, Revision = 0 };
                Entries[entry.Id] = pending;
                HistoryStore.Update(pending);
            }

            // Tombstones of a former connection mean nothing to a new account.
            HistoryStore.Remove(Tombstones.Keys.ToList());
            Tombstones.Clear();
        }
    }

    /// <summary>After disconnecting: tombstones are not needed any more.</summary>
    internal static void ForgetTombstones()
    {
        lock (Gate)
        {
            HistoryStore.Remove(Tombstones.Keys.ToList());
            Tombstones.Clear();
        }
    }

    /// <summary>
    /// The cloud accepted a pushed change. <paramref name="pushedUpdateDate"/> is the state that was sent: an entry
    /// that changed again meanwhile stays pending.
    /// </summary>
    internal static void ConfirmPushed(PushResult result, DateTimeOffset pushedUpdateDate)
    {
        var syncId = result.SyncId;
        ClipEntry? changed = null;
        var removed = false;
        lock (Gate)
        {
            var tombstone = Tombstones.Values.FirstOrDefault(entry => entry.SyncId == syncId);
            if (tombstone is not null)
            {
                Tombstones.Remove(tombstone.Id);
                HistoryStore.Remove([tombstone.Id]);
                return;
            }

            var entry = Entries.Values.FirstOrDefault(candidate => candidate.SyncId == syncId);
            if (entry is null)
            {
                return;
            }

            if (result.IsDeleted)
            {
                // Deleted on another device meanwhile: deleting wins.
                Entries.Remove(entry.Id);
                HistoryStore.Remove([entry.Id]);
                removed = true;
                changed = entry;
            }
            else if (result.MergedInto is { } target && target != syncId)
            {
                // The account had this content already: this entry becomes that one.
                var existing = Entries.Values.FirstOrDefault(candidate => candidate.SyncId == target);
                if (existing is not null)
                {
                    Entries.Remove(entry.Id);
                    HistoryStore.Remove([entry.Id]);
                    var merged = existing with
                    {
                        CopiedDate = Max(existing.CopiedDate, entry.CopiedDate),
                        IsFavorite = existing.IsFavorite || entry.IsFavorite,
                        IsPinned = existing.IsPinned || entry.IsPinned,
                        Revision = result.Revision,
                    };
                    Entries[existing.Id] = merged;
                    HistoryStore.Update(merged);
                    changed = merged;
                }
                else
                {
                    changed = entry with { SyncId = target, Revision = result.Revision, SyncState = SyncState.Synced };
                    Entries[entry.Id] = changed;
                    HistoryStore.Update(changed);
                }
            }
            else
            {
                changed = entry with
                {
                    Revision = result.Revision,
                    SyncState = entry.UpdateDate > pushedUpdateDate ? SyncState.Pending
                        : result.NeedsImage && entry.HasImage ? SyncState.ImagePending
                        : SyncState.Synced,
                };
                Entries[entry.Id] = changed;
                HistoryStore.Update(changed);
            }
        }

        Changed?.Invoke(null, new ClipChange(removed ? ClipChangeKind.Removed : ClipChangeKind.Updated, changed));
    }

    /// <summary>The cloud refused a change for good (e.g. a rule it checks): it stays on this computer, the sync goes on.</summary>
    internal static void KeepLocalOnly(Guid syncId)
    {
        lock (Gate)
        {
            if (Tombstones.Values.FirstOrDefault(entry => entry.SyncId == syncId) is { } tombstone)
            {
                Tombstones.Remove(tombstone.Id);
                HistoryStore.Remove([tombstone.Id]);
                return;
            }

            if (Entries.Values.FirstOrDefault(entry => entry.SyncId == syncId) is { } entry)
            {
                var kept = entry with { SyncState = SyncState.Synced };
                Entries[entry.Id] = kept;
                HistoryStore.Update(kept);
            }
        }
    }

    internal static void ConfirmImageUploaded(long id)
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(id, out var entry) && entry.SyncState == SyncState.ImagePending)
            {
                var synced = entry with { SyncState = SyncState.Synced };
                Entries[id] = synced;
                HistoryStore.Update(synced);
            }
        }
    }

    /// <summary>
    /// Applies a change from the cloud. Returns the entry when its PNG still has to be downloaded. A local change that
    /// has not been pushed yet and is newer than the cloud's wins (it goes up next).
    /// </summary>
    internal static ClipEntry? ApplyRemote(ClipDto remote)
    {
        ClipChange? change = null;
        ClipEntry? needsImage = null;
        lock (Gate)
        {
            var local = Entries.Values.FirstOrDefault(entry => entry.SyncId == remote.SyncId);
            var tombstone = Tombstones.Values.FirstOrDefault(entry => entry.SyncId == remote.SyncId);

            // The same content copied here and on another device before either was synced: one entry, the cloud's key.
            if (local is null && !remote.IsDeleted)
            {
                local = Entries.Values.FirstOrDefault(entry => entry.ContentHash == remote.ContentHash);
            }
            if (tombstone is not null)
            {
                return null; // deleted here, the deletion is on its way up
            }

            if (remote.IsDeleted)
            {
                if (local is not null)
                {
                    Entries.Remove(local.Id);
                    HistoryStore.Remove([local.Id]);
                    change = new ClipChange(ClipChangeKind.Removed, local);
                }
            }
            else if (local is null)
            {
                var entry = FromRemote(remote, null);
                var id = HistoryStore.Insert(entry, null, null);
                entry = entry with { Id = id };
                Entries[id] = entry;
                change = new ClipChange(ClipChangeKind.Added, entry);
                needsImage = entry is { IsImage: true, HasImage: false } && remote.HasImage ? entry : null;
            }
            else if (local.SyncState == SyncState.Pending && local.UpdateDate > remote.UpdatedAt)
            {
                return null;
            }
            else if (local.SyncId == remote.SyncId && local.SyncState == SyncState.Synced && local.Revision >= remote.Revision)
            {
                return null; // our own push coming back
            }
            else
            {
                var entry = FromRemote(remote, local);
                Entries[local.Id] = entry;
                HistoryStore.Update(entry);
                change = new ClipChange(ClipChangeKind.Updated, entry);
                needsImage = entry is { IsImage: true, HasImage: false } && remote.HasImage ? entry : null;
            }
        }

        if (change is not null)
        {
            Changed?.Invoke(null, change);
        }

        return needsImage;
    }

    /// <summary>The PNG of an entry from another device arrived.</summary>
    internal static void SetImageFromCloud(long id, byte[] png)
    {
        var thumbnail = ImageCodec.Thumbnail(png);
        ClipEntry? entry;
        lock (Gate)
        {
            if (!Entries.TryGetValue(id, out var current))
            {
                return;
            }

            HistoryStore.SetImage(id, png, thumbnail);
            entry = current with
            {
                HasImage = true,
                ImageBytes = png.Length,
                OcrState = current.OcrText is null && current.OcrState != OcrState.Done && SettingsService.Get().RecognizeText ? OcrState.Pending : current.OcrState,
            };
            Entries[id] = entry;
            HistoryStore.Update(entry);
        }

        Changed?.Invoke(null, new ClipChange(ClipChangeKind.Updated, entry));
        if (entry.OcrState == OcrState.Pending)
        {
            OcrService.Enqueue(id);
        }
    }

    /// <summary>The SyncId of every entry here (to find images that are missing after a restart).</summary>
    internal static List<ClipEntry> MissingImages()
    {
        lock (Gate)
        {
            return Entries.Values.Where(entry => entry is { IsImage: true, HasImage: false, Revision: > 0 }).ToList();
        }
    }

    internal static ClipDto ToDto(ClipEntry entry, bool isDeleted) => new()
    {
        SyncId = entry.SyncId,
        Kind = entry.Kind,
        Text = isDeleted ? null : entry.Text,
        OcrText = isDeleted ? null : entry.OcrText,
        ContentHash = entry.ContentHash,
        IsFavorite = entry.IsFavorite,
        IsPinned = entry.IsPinned,
        CreatedAt = entry.CreateDate,
        CopiedAt = entry.CopiedDate,
        UpdatedAt = entry.UpdateDate,
        ImageWidth = entry.ImageWidth,
        ImageHeight = entry.ImageHeight,
        ImageBytes = entry.ImageBytes,
        Source = entry.Source,
        IsDeleted = isDeleted,
    };

    /// <summary>Closes the database (at exit).</summary>
    public static void Close() => HistoryStore.Close();

    /// <summary>Tests start with an empty history.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Entries.Clear();
            Tombstones.Clear();
            _loaded = false;
        }
    }

    internal static string HashOf(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    /// <summary>A text is a link when it is exactly one http(s) address (no spaces, no line breaks).</summary>
    internal static bool IsLink(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length is > 8 and < 4096 && !trimmed.Any(char.IsWhiteSpace) &&
               Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static ClipEntry? MoveToTop(string hash)
    {
        ClipEntry? moved;
        lock (Gate)
        {
            var existing = Entries.Values.FirstOrDefault(entry => entry.ContentHash == hash);
            if (existing is null)
            {
                return null;
            }

            var now = AppClock.UtcNow;
            moved = existing with { CopiedDate = now, UpdateDate = now, SyncState = SyncState.Pending };
            Entries[existing.Id] = moved;
            HistoryStore.Update(moved);
        }

        Changed?.Invoke(null, new ClipChange(ClipChangeKind.Updated, moved));
        SyncService.Kick();
        return moved;
    }

    private static ClipEntry Add(ClipEntry template, byte[]? png, byte[]? thumbnail)
    {
        var now = AppClock.UtcNow;
        var entry = template with
        {
            CreateDate = now,
            CopiedDate = now,
            UpdateDate = now,
            Source = DeviceName(),
            SyncState = SyncState.Pending,
        };

        lock (Gate)
        {
            var id = HistoryStore.Insert(entry, png, thumbnail);
            entry = entry with { Id = id };
            Entries[id] = entry;
        }

        Changed?.Invoke(null, new ClipChange(ClipChangeKind.Added, entry));
        SyncService.Kick();
        return entry;
    }

    private static ClipEntry Change(long id, Func<ClipEntry, ClipEntry> change)
    {
        ClipEntry changed;
        lock (Gate)
        {
            var entry = Entries.GetValueOrDefault(id) ?? throw new BusinessException("Den Eintrag gibt es nicht mehr.");
            changed = change(entry) with { UpdateDate = AppClock.UtcNow, SyncState = SyncState.Pending };
            Entries[id] = changed;
            HistoryStore.Update(changed);
        }

        Changed?.Invoke(null, new ClipChange(ClipChangeKind.Updated, changed));
        SyncService.Kick();
        return changed;
    }

    private static DateTimeOffset? StartOf(ClearRange range) => ClearRanges.IsValid(range)
        ? ClearRanges.Start(range, AppClock.UtcNow)
        : throw new BusinessException("Diesen Zeitraum gibt es nicht.");

    private static bool IsCleared(ClipEntry entry, DateTimeOffset? start) => !entry.IsKeptForever && (start is null || entry.CopiedDate >= start);

    /// <summary>Without the cloud an entry simply goes; with it a tombstone tells the other devices.</summary>
    private static void DeleteLocked(ClipEntry entry)
    {
        if (!SettingsService.Get().Cloud.IsConnected)
        {
            HistoryStore.Remove([entry.Id]);
            return;
        }

        HistoryStore.MarkDeleted(entry.Id, SyncState.Pending);
        Tombstones[entry.Id] = entry with { Text = null, OcrText = null, HasImage = false, SyncState = SyncState.Pending, UpdateDate = AppClock.UtcNow };
    }

    private static ClipEntry FromRemote(ClipDto remote, ClipEntry? local) => new()
    {
        Id = local?.Id ?? 0,
        SyncId = remote.SyncId,
        Kind = remote.Kind,
        Text = remote.Text,
        OcrText = remote.OcrText ?? local?.OcrText,
        OcrState = remote.OcrText is not null ? OcrState.Done
            : local?.OcrState ?? (remote.Kind == ClipKind.Image ? OcrState.Pending : OcrState.Unavailable),
        ContentHash = remote.ContentHash,
        IsFavorite = remote.IsFavorite,
        IsPinned = remote.IsPinned,
        CreateDate = remote.CreatedAt,
        CopiedDate = remote.CopiedAt,
        UpdateDate = remote.UpdatedAt,
        Source = remote.Source,
        ImageWidth = remote.ImageWidth,
        ImageHeight = remote.ImageHeight,
        ImageBytes = remote.ImageBytes,
        HasImage = local?.HasImage ?? false,
        SyncState = SyncState.Synced,
        Revision = remote.Revision,
    };

    private static string DeviceName()
    {
        var cloud = SettingsService.Get().Cloud;
        return cloud.DeviceName.Length > 0 ? cloud.DeviceName : AppInfo.MachineName;
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;

    private static void EnsureLoaded()
    {
        if (!_loaded)
        {
            Initialize();
        }
    }
}
