using Microsoft.EntityFrameworkCore;
using ThixClip.Cloud.Data;
using ThixClip.Shared;

namespace ThixClip.Cloud.Business;

/// <summary>The query of the list of clips (the toolbar of the page "Verlauf").</summary>
public sealed record ClipQuery(string? Search, string? Kind, string? Flag, string Sort, bool Descending, int Page, int PageSize);

/// <summary>One row of the list.</summary>
public sealed record ClipListItem(
    long Id, ClipKind Kind, string Preview, bool HasOcr, bool IsFavorite, bool IsPinned, DateTime CopiedDate, DateTime CreatedDate,
    string? Source, bool HasImage, int? ImageWidth, int? ImageHeight);

public sealed record ClipListResult(IReadOnlyList<ClipListItem> Items, int Total, int Matching, long Latest);

/// <summary>A clip on its own page.</summary>
public sealed record ClipDetail(
    long Id, Guid SyncId, ClipKind Kind, string? Text, string? OcrText, bool IsFavorite, bool IsPinned, DateTime CopiedDate, DateTime CreatedDate,
    DateTime ChangedDate, string? Source, bool HasImage, int? ImageWidth, int? ImageHeight, long? ImageBytes, int RetentionDays)
{
    /// <summary>When the clip goes by itself (null: never, because it is a favourite, pinned, or the account keeps clips forever).</summary>
    public DateTime? ExpiresDate => RetentionDays == 0 || IsFavorite || IsPinned ? null : CopiedDate.AddDays(RetentionDays);
}

/// <summary>
/// The clips of an account as the browser shows and changes them. Every function works only on the clips of the actor's own account: a clip of
/// another account does not exist for it (an administrator has no access to the clips of others either). Every change takes a new revision and
/// reaches the devices through the sync (<see cref="SyncService"/>).
/// </summary>
public static class ClipService
{
    public const int DefaultPageSize = 50;

    public static async Task<ClipListResult> ListAsync(AppDbContext db, Actor actor, ClipQuery query)
    {
        var live = Live(db, actor.UserId).AsNoTracking();
        var total = await live.CountAsync();

        var filtered = live;
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = "%" + UserService.EscapeLike(query.Search.Trim()) + "%";
            filtered = filtered.Where(c => EF.Functions.ILike(c.Text ?? string.Empty, pattern, "\\") || EF.Functions.ILike(c.OcrText ?? string.Empty, pattern, "\\"));
        }

        filtered = query.Kind switch
        {
            "text" => filtered.Where(c => c.Kind == ClipKind.Text),
            "link" => filtered.Where(c => c.Kind == ClipKind.Link),
            "image" => filtered.Where(c => c.Kind == ClipKind.Image),
            _ => filtered,
        };
        filtered = query.Flag switch
        {
            "favorite" => filtered.Where(c => c.IsFavorite),
            "pinned" => filtered.Where(c => c.IsPinned),
            _ => filtered,
        };

        var matching = await filtered.CountAsync();
        filtered = (query.Sort, query.Descending) switch
        {
            ("created", true) => filtered.OrderByDescending(c => c.ClientCreateDate).ThenByDescending(c => c.Id),
            ("created", false) => filtered.OrderBy(c => c.ClientCreateDate).ThenBy(c => c.Id),
            (_, false) => filtered.OrderBy(c => c.CopiedDate).ThenBy(c => c.Id),
            _ => filtered.OrderByDescending(c => c.CopiedDate).ThenByDescending(c => c.Id),
        };

        var rows = await filtered
            .Skip((Math.Max(1, query.Page) - 1) * query.PageSize).Take(query.PageSize)
            .Select(c => new
            {
                c.Id, c.Kind, Start = c.Text == null ? null : c.Text.Substring(0, 800), OcrStart = c.OcrText == null ? null : c.OcrText.Substring(0, 400),
                c.IsFavorite, c.IsPinned, c.CopiedDate, c.ClientCreateDate, c.Source, c.HasImage, c.ImageWidth, c.ImageHeight,
            })
            .ToListAsync();
        var items = rows.Select(r => new ClipListItem(
                r.Id, r.Kind, ClipContent.Preview(r.Kind == ClipKind.Image ? r.OcrStart : r.Start), !string.IsNullOrWhiteSpace(r.OcrStart),
                r.IsFavorite, r.IsPinned, r.CopiedDate, r.ClientCreateDate, r.Source, r.HasImage, r.ImageWidth, r.ImageHeight))
            .ToList();
        var latest = await Revisions.LatestAsync(db, actor.UserId);
        return new ClipListResult(items, total, matching, latest);
    }

    public static async Task<Outcome<ClipDetail>> GetAsync(AppDbContext db, Actor actor, long id)
    {
        var retention = await db.Users.Where(u => u.Id == actor.UserId).Select(u => u.RetentionDays).FirstOrDefaultAsync();
        var clip = await Live(db, actor.UserId).AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        return clip is null
            ? Outcome<ClipDetail>.Missing()
            : Outcome<ClipDetail>.Success(new ClipDetail(
                clip.Id, clip.SyncId, clip.Kind, clip.Text, clip.OcrText, clip.IsFavorite, clip.IsPinned, clip.CopiedDate, clip.ClientCreateDate,
                clip.UpdateDate, clip.Source, clip.HasImage, clip.ImageWidth, clip.ImageHeight, clip.ImageBytes, retention));
    }

    /// <summary>A text typed or pasted in the browser: it appears on every device. The same text that is already there moves to the top instead.</summary>
    public static async Task<Outcome<long>> CreateTextAsync(AppDbContext db, Actor actor, string text)
    {
        if (TextProblem(text) is { } problem)
        {
            return Outcome<long>.Fail(problem, "Text");
        }

        var hash = ClipContent.HashText(text);
        var now = DateTime.UtcNow;
        var id = await Revisions.ChangeAsync(db, actor.UserId, async () =>
        {
            var existing = await Live(db, actor.UserId).FirstOrDefaultAsync(c => c.ContentHash == hash);
            if (existing is not null)
            {
                existing.CopiedDate = now;
                existing.ClientUpdateDate = now;
                existing.Revision = await Revisions.NextAsync(db);
                await db.SaveChangesAsync();
                return existing.Id;
            }

            var clip = NewClip(actor, ClipContent.KindOfText(text), hash, now);
            clip.Text = text;
            clip.Revision = await Revisions.NextAsync(db);
            db.Clips.Add(clip);
            await db.SaveChangesAsync();
            return clip.Id;
        });
        return Outcome<long>.Success(id);
    }

    /// <summary>A picture uploaded in the browser (any format SkiaSharp reads; it is kept as PNG).</summary>
    public static async Task<Outcome<long>> CreateImageAsync(AppDbContext db, Actor actor, byte[] bytes)
    {
        var prepared = ImageTools.Prepare(bytes);
        if (!prepared.Ok)
        {
            return Outcome<long>.Fail(prepared.Error!, "Image");
        }

        var image = prepared.Value!;
        var now = DateTime.UtcNow;
        var id = await Revisions.ChangeAsync(db, actor.UserId, async () =>
        {
            var existing = await Live(db, actor.UserId).Include(c => c.Image).FirstOrDefaultAsync(c => c.ContentHash == image.ContentHash);
            if (existing is not null)
            {
                existing.CopiedDate = now;
                existing.ClientUpdateDate = now;
                if (!existing.HasImage)
                {
                    StoreImage(db, existing, image);
                }

                existing.Revision = await Revisions.NextAsync(db);
                await db.SaveChangesAsync();
                return existing.Id;
            }

            var clip = NewClip(actor, ClipKind.Image, image.ContentHash, now);
            StoreImage(db, clip, image);
            clip.Revision = await Revisions.NextAsync(db);
            db.Clips.Add(clip);
            await db.SaveChangesAsync();
            return clip.Id;
        });
        return Outcome<long>.Success(id);
    }

    /// <summary>The edit page: the text (only of a text or a link; a picture keeps its content), favourite and pinned.</summary>
    public static async Task<Outcome> UpdateAsync(AppDbContext db, Actor actor, long id, string? text, bool isFavorite, bool isPinned)
    {
        var clip = await Live(db, actor.UserId).AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (clip is null)
        {
            return Outcome.Missing();
        }

        if (clip.Kind != ClipKind.Image && TextProblem(text ?? string.Empty) is { } problem)
        {
            return Outcome.Fail(problem, "Text");
        }

        return await Revisions.ChangeAsync(db, actor.UserId, async () =>
        {
            var tracked = await Live(db, actor.UserId).FirstOrDefaultAsync(c => c.Id == id);
            if (tracked is null)
            {
                return Outcome.Missing();
            }

            var changed = tracked.IsFavorite != isFavorite || tracked.IsPinned != isPinned;
            tracked.IsFavorite = isFavorite;
            tracked.IsPinned = isPinned;
            if (tracked.Kind != ClipKind.Image && text is not null && text != tracked.Text)
            {
                tracked.Text = text;
                tracked.Kind = ClipContent.KindOfText(text);
                tracked.ContentHash = ClipContent.HashText(text);
                changed = true;
            }

            if (!changed)
            {
                return Outcome.Success();
            }

            tracked.ClientUpdateDate = DateTime.UtcNow;
            tracked.Revision = await Revisions.NextAsync(db);
            await db.SaveChangesAsync();
            return Outcome.Success();
        });
    }

    /// <summary>The star of a row: favourite on or off.</summary>
    public static async Task<Outcome<bool>> SetFavoriteAsync(AppDbContext db, Actor actor, long id, bool isFavorite)
        => await Revisions.ChangeAsync(db, actor.UserId, async () =>
        {
            var clip = await Live(db, actor.UserId).FirstOrDefaultAsync(c => c.Id == id);
            if (clip is null)
            {
                return Outcome<bool>.Missing();
            }

            if (clip.IsFavorite != isFavorite)
            {
                clip.IsFavorite = isFavorite;
                clip.ClientUpdateDate = DateTime.UtcNow;
                clip.Revision = await Revisions.NextAsync(db);
                await db.SaveChangesAsync();
            }

            return Outcome<bool>.Success(clip.IsFavorite);
        });

    /// <summary>Deletes a clip: it goes on every device too (the tombstone keeps only its id and revision).</summary>
    public static async Task<Outcome> DeleteAsync(AppDbContext db, Actor actor, long id)
        => await Revisions.ChangeAsync(db, actor.UserId, async () =>
        {
            var clip = await Live(db, actor.UserId).Include(c => c.Image).FirstOrDefaultAsync(c => c.Id == id);
            if (clip is null)
            {
                return Outcome.Missing();
            }

            await MakeTombstoneAsync(db, clip, DateTime.UtcNow);
            await db.SaveChangesAsync();
            return Outcome.Success();
        });

    /// <summary>"Verlauf leeren": every clip goes except favourites and pinned ones. Answers how many went.</summary>
    public static async Task<int> ClearHistoryAsync(AppDbContext db, Actor actor)
        => await Revisions.ChangeAsync(db, actor.UserId, () => TombstoneManyAsync(db, actor.UserId, null, actor.UserId));

    /// <summary>The PNG or the preview of an image clip of the actor; null when there is none.</summary>
    public static async Task<byte[]?> ImageAsync(AppDbContext db, Actor actor, long id, bool thumbnail)
    {
        var images = db.ClipImages.AsNoTracking().Where(i => i.ClipEntryId == id && i.ClipEntry!.UserId == actor.UserId && i.ClipEntry.UpdateState != SoftEntity.Deleted);
        return thumbnail
            ? await images.Select(i => i.Thumbnail).FirstOrDefaultAsync()
            : await images.Select(i => i.Png).FirstOrDefaultAsync();
    }

    public static Task<long> LatestAsync(AppDbContext db, Actor actor, CancellationToken ct = default) => Revisions.LatestAsync(db, actor.UserId, ct);

    /// <summary>
    /// Waits (at most <paramref name="timeout"/>) until the account has a revision above <paramref name="since"/>; answers the latest revision. The live list of
    /// the browser and the long poll of the devices use it.
    /// </summary>
    public static async Task<long> WaitAsync(AppDbContext db, Actor actor, long since, TimeSpan timeout, CancellationToken ct)
    {
        var signal = RevisionSignals.Next(actor.UserId);
        var latest = await Revisions.LatestAsync(db, actor.UserId, ct);
        if (latest > since)
        {
            return latest;
        }

        try
        {
            await signal.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            return latest;
        }

        return await Revisions.LatestAsync(db, actor.UserId, ct);
    }

    /// <summary>The retention of one account: clips copied before the time it keeps them go (not favourites, not pinned). Answers how many went.</summary>
    public static async Task<int> ApplyRetentionAsync(AppDbContext db, long userId, CancellationToken ct = default)
    {
        var days = await db.Users.Where(u => u.Id == userId).Select(u => (int?)u.RetentionDays).FirstOrDefaultAsync(ct);
        if (days is null or 0)
        {
            return 0;
        }

        var limit = DateTime.UtcNow.AddDays(-days.Value);
        var expired = await Live(db, userId).AnyAsync(c => c.CopiedDate < limit && !c.IsFavorite && !c.IsPinned, ct);
        if (!expired)
        {
            return 0;
        }

        return await Revisions.ChangeAsync(db, userId, () => TombstoneManyAsync(db, userId, limit, null, ct), ct);
    }

    /// <summary>The retention of every account (the housekeeping, every hour).</summary>
    public static async Task<int> ApplyRetentionForAllAsync(AppDbContext db, CancellationToken ct = default)
    {
        var users = await db.Users.Where(u => u.RetentionDays > 0).Select(u => u.Id).ToListAsync(ct);
        var removed = 0;
        foreach (var userId in users)
        {
            removed += await ApplyRetentionAsync(db, userId, ct);
        }

        return removed;
    }

    /// <summary>The clips of an account that are not deleted.</summary>
    internal static IQueryable<ClipEntry> Live(AppDbContext db, long userId)
        => db.Clips.Where(c => c.UserId == userId && c.UpdateState != SoftEntity.Deleted);

    internal static string? TextProblem(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Bitte einen Text eingeben.";
        }

        return text.Length > SyncContract.MaxTextLength ? $"Der Text ist zu lang (höchstens {SyncContract.MaxTextLength:N0} Zeichen)." : null;
    }

    internal static ClipEntry NewClip(Actor actor, ClipKind kind, string hash, DateTime now) => new()
    {
        UserId = actor.UserId,
        SyncId = Guid.NewGuid(),
        Kind = kind,
        ContentHash = hash,
        CopiedDate = now,
        ClientCreateDate = now,
        ClientUpdateDate = now,
        Source = actor.Source,
        CreateUserId = actor.UserId,
    };

    internal static void StoreImage(AppDbContext db, ClipEntry clip, PreparedImage image)
    {
        if (clip.Image is null)
        {
            clip.Image = new ClipImage { Png = image.Png, Thumbnail = image.Thumbnail };
            if (clip.Id != 0)
            {
                db.ClipImages.Add(clip.Image);
            }
        }
        else
        {
            clip.Image.Png = image.Png;
            clip.Image.Thumbnail = image.Thumbnail;
        }

        clip.HasImage = true;
        clip.ImageWidth = image.Width;
        clip.ImageHeight = image.Height;
        clip.ImageBytes = image.Png.LongLength;
    }

    /// <summary>Turns a clip into a tombstone: deleted, a new revision, the content wiped (the picture is removed for good).</summary>
    internal static async Task MakeTombstoneAsync(AppDbContext db, ClipEntry clip, DateTime now)
    {
        clip.UpdateState = SoftEntity.Deleted;
        clip.Text = null;
        clip.OcrText = null;
        clip.ContentHash = string.Empty;
        clip.IsFavorite = false;
        clip.IsPinned = false;
        clip.HasImage = false;
        clip.ImageWidth = null;
        clip.ImageHeight = null;
        clip.ImageBytes = null;
        clip.ClientUpdateDate = now;
        clip.Revision = await Revisions.NextAsync(db);
        if (clip.Image is not null)
        {
            db.ClipImages.Remove(clip.Image);
            clip.Image = null;
        }
        else
        {
            await db.ClipImages.Where(i => i.ClipEntryId == clip.Id).ExecuteDeleteAsync();
        }
    }

    /// <summary>Tombstones every live clip of an account that is neither a favourite nor pinned (and, with a limit, was copied before it), in one statement.</summary>
    private static async Task<int> TombstoneManyAsync(AppDbContext db, long userId, DateTime? copiedBefore, long? actingUserId, CancellationToken ct = default)
    {
        var sql = """
            UPDATE "ClipEntry" SET
                "UpdateState" = 0, "Text" = NULL, "OcrText" = NULL, "ContentHash" = '', "IsFavorite" = FALSE, "IsPinned" = FALSE,
                "HasImage" = FALSE, "ImageWidth" = NULL, "ImageHeight" = NULL, "ImageBytes" = NULL,
                "ClientUpdateDate" = now(), "UpdateDate" = now(), "UpdateUserId" = @p1, "Revision" = nextval('"ClipRevision"')
            WHERE "UserId" = @p0 AND "UpdateState" <> 0 AND NOT "IsFavorite" AND NOT "IsPinned"
            """;
        var parameters = new List<object> { userId, (object?)actingUserId ?? DBNull.Value };
        if (copiedBefore is { } limit)
        {
            sql += " AND \"CopiedDate\" < @p2";
            parameters.Add(DateTime.SpecifyKind(limit, DateTimeKind.Utc));
        }

        var ids = await Revisions.ColumnAsync(db, sql + " RETURNING \"Id\"", ct, [.. parameters]);
        if (ids.Count > 0)
        {
            await db.ClipImages.Where(i => ids.Contains(i.ClipEntryId)).ExecuteDeleteAsync(ct);
        }

        return ids.Count;
    }
}
