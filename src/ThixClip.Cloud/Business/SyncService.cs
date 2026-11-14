using Microsoft.EntityFrameworkCore;
using ThixClip.Cloud.Data;
using ThixClip.Shared;

namespace ThixClip.Cloud.Business;

/// <summary>
/// The sync of the desktop apps (<see cref="SyncContract"/>): what changed above a revision, the changes a device pushes, the pictures, the long poll.
/// The rules:
/// <list type="bullet">
/// <item>A clip is known by its SyncId; a push creates it or changes it. The newer change wins (UpdatedAt of the device); an older one is ignored.</item>
/// <item>Deleting wins over everything: a deleted clip stays deleted, whatever comes later for its SyncId.</item>
/// <item>A new SyncId whose content the account already has (same ContentHash, not deleted) is not kept twice: the existing clip moves to the top and
///       the answer names it (<see cref="PushResult.MergedInto"/>); the device takes over that SyncId.</item>
/// <item>An image clip needs its PNG: <see cref="PushResult.NeedsImage"/> asks for it, PUT image delivers it.</item>
/// </list>
/// </summary>
public static class SyncService
{
    public const int DefaultLimit = 200;
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public static ClipDto ToDto(ClipEntry clip) => new()
    {
        SyncId = clip.SyncId,
        Kind = clip.Kind,
        Text = clip.Text,
        OcrText = clip.OcrText,
        ContentHash = clip.ContentHash,
        IsFavorite = clip.IsFavorite,
        IsPinned = clip.IsPinned,
        CreatedAt = Utc(clip.ClientCreateDate),
        CopiedAt = Utc(clip.CopiedDate),
        UpdatedAt = Utc(clip.ClientUpdateDate),
        ImageWidth = clip.ImageWidth,
        ImageHeight = clip.ImageHeight,
        ImageBytes = clip.ImageBytes,
        HasImage = clip.HasImage,
        Source = clip.Source,
        IsDeleted = clip.UpdateState == SoftEntity.Deleted,
        Revision = clip.Revision,
    };

    /// <summary>
    /// Everything above <paramref name="since"/>, oldest first, at most <paramref name="limit"/> (tombstones included). <see cref="ChangeSet.Latest"/> is the
    /// revision of the last clip in the set (or <paramref name="since"/> when there is none): the device stores it and asks again while HasMore.
    /// </summary>
    public static async Task<ChangeSet> ChangesAsync(AppDbContext db, Actor actor, long since, int limit, CancellationToken ct = default)
    {
        since = Math.Max(0, since);
        limit = limit <= 0 ? DefaultLimit : Math.Min(limit, SyncContract.MaxBatchSize);
        var rows = await db.Clips.AsNoTracking()
            .Where(c => c.UserId == actor.UserId && c.Revision > since)
            .OrderBy(c => c.Revision)
            .Take(limit + 1)
            .ToListAsync(ct);
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var retention = await db.Users.Where(u => u.Id == actor.UserId).Select(u => u.RetentionDays).FirstOrDefaultAsync(ct);
        var latest = rows.Count > 0 ? rows[^1].Revision : since;
        return new ChangeSet(rows.Select(ToDto).ToList(), latest, hasMore, retention);
    }

    /// <summary>What is wrong with a push, or null. A push is taken whole or not at all.</summary>
    public static (string Message, string Field)? Validate(PushRequest? request)
    {
        if (request?.Clips is null)
        {
            return ("Die Anfrage enthält keine Einträge.", "clips");
        }

        if (request.Clips.Count > SyncContract.MaxBatchSize)
        {
            return ($"Höchstens {SyncContract.MaxBatchSize} Einträge je Anfrage.", "clips");
        }

        for (var i = 0; i < request.Clips.Count; i++)
        {
            var clip = request.Clips[i];
            var at = $"clips[{i}]";
            if (clip is null || clip.SyncId == Guid.Empty)
            {
                return ("Ein Eintrag hat keine SyncId.", at + ".syncId");
            }

            if (!Enum.IsDefined(clip.Kind))
            {
                return ("Unbekannte Art eines Eintrags.", at + ".kind");
            }

            if (clip.IsDeleted)
            {
                continue;
            }

            if (clip.Kind != ClipKind.Image && ClipService.TextProblem(clip.Text ?? string.Empty) is { } textProblem)
            {
                return (textProblem, at + ".text");
            }

            if (clip.OcrText is { Length: > SyncContract.MaxTextLength })
            {
                return ("Der erkannte Text ist zu lang.", at + ".ocrText");
            }

            if (ClipContent.NormalizeHash(clip.ContentHash) is null)
            {
                return ("ContentHash ist kein SHA-256-Wert (64 Hex-Zeichen).", at + ".contentHash");
            }

            if (clip.Kind != ClipKind.Image && ClipContent.NormalizeHash(clip.ContentHash) != ClipContent.HashText(clip.Text!))
            {
                return ("ContentHash passt nicht zum Text (SHA-256 des Textes als UTF-8).", at + ".contentHash");
            }
        }

        return null;
    }

    /// <summary>Applies the changes of a device in one transaction (in the order they came). Call <see cref="Validate"/> first.</summary>
    public static async Task<PushResponse> PushAsync(AppDbContext db, Actor actor, PushRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var results = await Revisions.ChangeAsync(db, actor.UserId, async () =>
        {
            var ids = request.Clips.Select(c => c.SyncId).Distinct().ToList();
            var known = await db.Clips.Where(c => c.UserId == actor.UserId && ids.Contains(c.SyncId)).ToDictionaryAsync(c => c.SyncId, ct);
            var byHash = new Dictionary<string, ClipEntry>(StringComparer.Ordinal);
            var list = new List<PushResult>(request.Clips.Count);
            foreach (var incoming in request.Clips)
            {
                list.Add(await ApplyAsync(db, actor, incoming, known, byHash, now, ct));
            }

            await db.SaveChangesAsync(ct);
            return list;
        }, ct);

        // A result carries the revision the clip has after the whole push (a clip pushed twice in one request has the last one).
        var keys = results.Select(r => r.MergedInto ?? r.SyncId).Distinct().ToList();
        var revisions = await db.Clips.AsNoTracking()
            .Where(c => c.UserId == actor.UserId && keys.Contains(c.SyncId))
            .Select(c => new { c.SyncId, c.Revision })
            .ToDictionaryAsync(c => c.SyncId, c => c.Revision, ct);
        var final = results.Select(r => r with { Revision = revisions.GetValueOrDefault(r.MergedInto ?? r.SyncId, r.Revision) }).ToList();
        return new PushResponse(final, await Revisions.LatestAsync(db, actor.UserId, ct));
    }

    private static async Task<PushResult> ApplyAsync(
        AppDbContext db, Actor actor, ClipDto incoming, Dictionary<Guid, ClipEntry> known, Dictionary<string, ClipEntry> byHash, DateTime now, CancellationToken ct)
    {
        var updatedAt = Clamp(incoming.UpdatedAt, now);
        if (known.TryGetValue(incoming.SyncId, out var clip))
        {
            if (clip.UpdateState == SoftEntity.Deleted)
            {
                return new PushResult(clip.SyncId, clip.Revision, null, true, false);
            }

            if (incoming.IsDeleted)
            {
                await ClipService.MakeTombstoneAsync(db, clip, updatedAt);
                return new PushResult(clip.SyncId, clip.Revision, null, true, false);
            }

            if (updatedAt >= clip.ClientUpdateDate && Merge(clip, incoming, updatedAt, now, replace: true))
            {
                clip.Revision = await Revisions.NextAsync(db, ct);
            }

            byHash.TryAdd(clip.ContentHash, clip);
            return new PushResult(clip.SyncId, clip.Revision, null, false, NeedsImage(clip));
        }

        if (incoming.IsDeleted)
        {
            // Created and deleted on the device before it was ever synced: there is nothing to keep.
            return new PushResult(incoming.SyncId, 0, null, true, false);
        }

        var hash = ClipContent.NormalizeHash(incoming.ContentHash)!;
        if (!byHash.TryGetValue(hash, out var same))
        {
            same = await ClipService.Live(db, actor.UserId).FirstOrDefaultAsync(c => c.ContentHash == hash, ct);
        }

        if (same is not null && same.UpdateState != SoftEntity.Deleted)
        {
            // The account has this content already: that clip moves to the top and the device takes over its SyncId.
            Merge(same, incoming, Max(same.ClientUpdateDate, updatedAt), now, replace: false);
            same.Revision = await Revisions.NextAsync(db, ct);
            byHash[hash] = same;
            return new PushResult(incoming.SyncId, same.Revision, same.SyncId, false, NeedsImage(same));
        }

        var created = new ClipEntry
        {
            UserId = actor.UserId,
            SyncId = incoming.SyncId,
            Kind = incoming.Kind == ClipKind.Image ? ClipKind.Image : incoming.Kind,
            Text = incoming.Kind == ClipKind.Image ? null : incoming.Text,
            OcrText = string.IsNullOrWhiteSpace(incoming.OcrText) ? null : incoming.OcrText,
            ContentHash = hash,
            IsFavorite = incoming.IsFavorite,
            IsPinned = incoming.IsPinned,
            CopiedDate = Clamp(incoming.CopiedAt, now),
            ClientCreateDate = Clamp(incoming.CreatedAt, now),
            ClientUpdateDate = updatedAt,
            // the name of the signed-in device (as it is in the account), not what the request says
            Source = Trim(actor.Source, 100),
            CreateUserId = actor.UserId,
            Revision = await Revisions.NextAsync(db, ct),
        };
        db.Clips.Add(created);
        known[created.SyncId] = created;
        byHash[hash] = created;
        return new PushResult(created.SyncId, created.Revision, null, false, created.Kind == ClipKind.Image);
    }

    /// <summary>
    /// Takes the values of a push into a clip. <paramref name="replace"/>: the device changed this clip (its values win); otherwise the same content was
    /// copied again elsewhere (the clip moves to the top and keeps what it has, favourite and pinned are added). Answers whether anything changed.
    /// </summary>
    private static bool Merge(ClipEntry clip, ClipDto incoming, DateTime updatedAt, DateTime now, bool replace)
    {
        var before = (clip.Text, clip.OcrText, clip.IsFavorite, clip.IsPinned, clip.CopiedDate, clip.ContentHash, clip.Kind);
        var copiedAt = Clamp(incoming.CopiedAt, now);
        if (replace)
        {
            clip.IsFavorite = incoming.IsFavorite;
            clip.IsPinned = incoming.IsPinned;
            if (clip.Kind != ClipKind.Image && incoming.Kind != ClipKind.Image && incoming.Text is not null && incoming.Text != clip.Text)
            {
                clip.Text = incoming.Text;
                clip.Kind = incoming.Kind;
                clip.ContentHash = ClipContent.NormalizeHash(incoming.ContentHash)!;
            }
        }
        else
        {
            clip.IsFavorite |= incoming.IsFavorite;
            clip.IsPinned |= incoming.IsPinned;
        }

        if (!string.IsNullOrWhiteSpace(incoming.OcrText) && (replace || clip.OcrText is null))
        {
            clip.OcrText = incoming.OcrText;
        }

        clip.CopiedDate = Max(clip.CopiedDate, copiedAt);
        var changed = before != (clip.Text, clip.OcrText, clip.IsFavorite, clip.IsPinned, clip.CopiedDate, clip.ContentHash, clip.Kind);
        if (changed || !replace)
        {
            clip.ClientUpdateDate = Max(clip.ClientUpdateDate, updatedAt);
        }

        return changed;
    }

    /// <summary>PUT image: the PNG of an image clip of the device's account (another format is converted). Answers the new revision.</summary>
    public static async Task<Outcome<long>> UploadImageAsync(AppDbContext db, Actor actor, Guid syncId, byte[] bytes, CancellationToken ct = default)
    {
        var clip = await ClipService.Live(db, actor.UserId).AsNoTracking().FirstOrDefaultAsync(c => c.SyncId == syncId, ct);
        if (clip is null)
        {
            return Outcome<long>.Missing();
        }

        if (clip.Kind != ClipKind.Image)
        {
            return Outcome<long>.Fail("Dieser Eintrag ist kein Bild.", "kind");
        }

        var prepared = ImageTools.Prepare(bytes);
        if (!prepared.Ok)
        {
            return Outcome<long>.Fail(prepared.Error!, "image");
        }

        var revision = await Revisions.ChangeAsync(db, actor.UserId, async () =>
        {
            var tracked = await ClipService.Live(db, actor.UserId).Include(c => c.Image).FirstOrDefaultAsync(c => c.SyncId == syncId, ct);
            if (tracked is null)
            {
                return 0L;
            }

            ClipService.StoreImage(db, tracked, prepared.Value!);
            tracked.Revision = await Revisions.NextAsync(db, ct);
            await db.SaveChangesAsync(ct);
            return tracked.Revision;
        }, ct);
        return revision == 0 ? Outcome<long>.Missing() : Outcome<long>.Success(revision);
    }

    /// <summary>GET image / thumbnail of a clip of the device's account; null when there is none (deleted, not uploaded yet, another account).</summary>
    public static async Task<byte[]?> ImageAsync(AppDbContext db, Actor actor, Guid syncId, bool thumbnail, CancellationToken ct = default)
    {
        var images = db.ClipImages.AsNoTracking()
            .Where(i => i.ClipEntry!.UserId == actor.UserId && i.ClipEntry.SyncId == syncId && i.ClipEntry.UpdateState != SoftEntity.Deleted);
        return thumbnail
            ? await images.Select(i => i.Thumbnail).FirstOrDefaultAsync(ct)
            : await images.Select(i => i.Png).FirstOrDefaultAsync(ct);
    }

    private static bool NeedsImage(ClipEntry clip) => clip.Kind == ClipKind.Image && !clip.HasImage && clip.UpdateState != SoftEntity.Deleted;

    /// <summary>A date of a device as UTC; a date in the future (a clock that is ahead) counts as now.</summary>
    private static DateTime Clamp(DateTimeOffset value, DateTime now)
    {
        var utc = value.UtcDateTime;
        if (value == default || utc > now + FutureTolerance)
        {
            return now;
        }

        return utc;
    }

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string? Trim(string? text, int max)
    {
        var value = text?.Trim();
        return string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
    }
}
