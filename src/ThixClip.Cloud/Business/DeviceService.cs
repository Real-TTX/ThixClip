using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ThixClip.Cloud.Data;
using ThixClip.Shared;

namespace ThixClip.Cloud.Business;

/// <summary>A device as the API knows it after its token was checked.</summary>
public sealed record DeviceIdentity(long DeviceId, long UserId, bool IsAdmin, string Name, DateTime LastSeenDate)
{
    public Actor Actor => new(UserId, IsAdmin, DeviceId, Name);
}

/// <summary>A device in the account pages.</summary>
public sealed record DeviceView(long Id, string Name, string Platform, string AppVersion, DateTime CreateDate, DateTime LastSeenDate, string? IpAddress);

/// <summary>
/// The desktop apps of an account. A device signs in with the user name and password of the account and gets a token (shown once); with it the
/// device syncs. Removing a device in the browser (or signing out on the device) ends its access at once: the next request answers 401.
/// </summary>
public static class DeviceService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<Guid, (DeviceIdentity Identity, DateTime Cached, int Generation)> Cache = new();
    private static int _generation;

    public static async Task<Outcome<DeviceLoginResponse>> LoginAsync(AppDbContext db, DeviceLoginRequest request, string? ipAddress, string serverVersion)
    {
        var deviceName = (request.DeviceName ?? string.Empty).Trim();
        if (deviceName.Length == 0 || deviceName.Length > 100)
        {
            return Outcome<DeviceLoginResponse>.Fail("Der Gerätename fehlt oder ist zu lang (höchstens 100 Zeichen).", "deviceName");
        }

        var signIn = await UserService.SignInAsync(db, request.UserName ?? string.Empty, request.Password ?? string.Empty);
        if (!signIn.Ok)
        {
            return Outcome<DeviceLoginResponse>.Fail(signIn.Error!, "password");
        }

        var user = signIn.Value!;
        var platform = Clip((request.Platform ?? string.Empty).Trim().ToLowerInvariant(), 40);

        // The same computer signing in again replaces its old entry (a fresh token), so the list of devices does not fill up.
        var previous = await db.Devices.Where(d => d.UserId == user.Id && d.Name == deviceName && d.Platform == platform).ToListAsync();
        db.Devices.RemoveRange(previous);

        var now = DateTime.UtcNow;
        var device = new Device
        {
            UserId = user.Id,
            Token = Guid.NewGuid(),
            Name = deviceName,
            Platform = platform,
            AppVersion = Clip((request.AppVersion ?? string.Empty).Trim(), 60),
            LastSeenDate = now,
            IpAddress = Clip(ipAddress ?? string.Empty, 64),
            CreateUserId = user.Id,
        };
        db.Devices.Add(device);
        db.ActingUserId = user.Id;
        await db.SaveChangesAsync();
        Invalidate();

        var account = new AccountInfo(user.UserName, user.DisplayName, user.RetentionDays, device.Name, serverVersion);
        return Outcome<DeviceLoginResponse>.Success(new DeviceLoginResponse(device.Token.ToString("D"), device.Id, account));
    }

    /// <summary>The device of a bearer token, or null when the token is unknown, the device removed or the account inactive. Cached for 20 seconds.</summary>
    public static async Task<DeviceIdentity?> AuthenticateAsync(AppDbContext db, string? bearer, CancellationToken ct = default)
    {
        if (!Guid.TryParse(bearer, out var token) || token == Guid.Empty)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if (Cache.TryGetValue(token, out var hit) && hit.Generation == _generation && now - hit.Cached < CacheFor)
        {
            return hit.Identity;
        }

        var row = await db.Devices.AsNoTracking()
            .Where(d => d.Token == token)
            .Select(d => new { d.Id, d.UserId, d.Name, d.LastSeenDate, d.User!.IsAdmin, d.User.IsActive })
            .FirstOrDefaultAsync(ct);
        if (row is null || !row.IsActive)
        {
            Cache.TryRemove(token, out _);
            return null;
        }

        var identity = new DeviceIdentity(row.Id, row.UserId, row.IsAdmin, row.Name, row.LastSeenDate);
        Cache[token] = (identity, now, _generation);
        return identity;
    }

    /// <summary>Notes that the device was seen (at most every two minutes), with its address and the version of the app.</summary>
    public static async Task TouchAsync(AppDbContext db, DeviceIdentity device, string? ipAddress, string? appVersion, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (now - device.LastSeenDate < TouchEvery)
        {
            return;
        }

        var ip = Clip(ipAddress ?? string.Empty, 64);
        var version = Clip((appVersion ?? string.Empty).Trim(), 60);
        if (version.Length > 0)
        {
            await db.Devices.Where(d => d.Id == device.DeviceId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenDate, now).SetProperty(d => d.IpAddress, ip).SetProperty(d => d.AppVersion, version), ct);
        }
        else
        {
            await db.Devices.Where(d => d.Id == device.DeviceId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenDate, now).SetProperty(d => d.IpAddress, ip), ct);
        }

        foreach (var (token, entry) in Cache.Where(e => e.Value.Identity.DeviceId == device.DeviceId).ToList())
        {
            Cache[token] = (entry.Identity with { LastSeenDate = now }, entry.Cached, entry.Generation);
        }
    }

    /// <summary>The device signs itself out: its token ends.</summary>
    public static async Task LogoutAsync(AppDbContext db, Actor actor)
    {
        if (actor.DeviceId is not { } id)
        {
            return;
        }

        await RemoveAsync(db, actor, id);
    }

    /// <summary>The devices of the actor's account (search in name, platform and version; sort by last contact, name or sign-in) and how many there are in all.</summary>
    public static async Task<(IReadOnlyList<DeviceView> Items, int Total)> ListAsync(AppDbContext db, Actor actor, string? search, string sort, bool descending)
    {
        var all = db.Devices.AsNoTracking().Where(d => d.UserId == actor.UserId);
        var total = await all.CountAsync();
        var filtered = all;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + UserService.EscapeLike(search.Trim()) + "%";
            filtered = filtered.Where(d => EF.Functions.ILike(d.Name, pattern, "\\") || EF.Functions.ILike(d.Platform, pattern, "\\") || EF.Functions.ILike(d.AppVersion, pattern, "\\"));
        }

        filtered = (sort, descending) switch
        {
            ("name", false) => filtered.OrderBy(d => d.Name),
            ("name", true) => filtered.OrderByDescending(d => d.Name),
            ("created", false) => filtered.OrderBy(d => d.CreateDate),
            ("created", true) => filtered.OrderByDescending(d => d.CreateDate),
            (_, false) => filtered.OrderBy(d => d.LastSeenDate),
            _ => filtered.OrderByDescending(d => d.LastSeenDate),
        };
        var items = await filtered
            .Select(d => new DeviceView(d.Id, d.Name, d.Platform, d.AppVersion, d.CreateDate, d.LastSeenDate, d.IpAddress))
            .ToListAsync();
        return (items, total);
    }

    /// <summary>Gives a device of the actor's account another name (shown in the list and as the source of its clips from now on).</summary>
    public static async Task<Outcome> RenameAsync(AppDbContext db, Actor actor, long id, string name)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0 || clean.Length > 100)
        {
            return Outcome.Fail("Der Name hat 1 bis 100 Zeichen.", "Name");
        }

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && d.UserId == actor.UserId);
        if (device is null)
        {
            return Outcome.Missing();
        }

        device.Name = clean;
        await db.SaveChangesAsync();
        Invalidate();
        return Outcome.Success();
    }

    public static async Task<Outcome<DeviceView>> GetAsync(AppDbContext db, Actor actor, long id)
    {
        var device = await db.Devices.AsNoTracking()
            .Where(d => d.Id == id && d.UserId == actor.UserId)
            .Select(d => new DeviceView(d.Id, d.Name, d.Platform, d.AppVersion, d.CreateDate, d.LastSeenDate, d.IpAddress))
            .FirstOrDefaultAsync();
        return device is null ? Outcome<DeviceView>.Missing() : Outcome<DeviceView>.Success(device);
    }

    /// <summary>Removes a device of the actor's own account: its token answers 401 from now on.</summary>
    public static async Task<Outcome> RemoveAsync(AppDbContext db, Actor actor, long id)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && d.UserId == actor.UserId);
        if (device is null)
        {
            return Outcome.Missing();
        }

        db.Devices.Remove(device);
        await db.SaveChangesAsync();
        Invalidate();
        return Outcome.Success();
    }

    /// <summary>Every device of an account ends (the account was deactivated or deleted).</summary>
    public static async Task RemoveAllAsync(AppDbContext db, long userId)
    {
        await db.Devices.Where(d => d.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.UpdateState, SoftEntity.Deleted).SetProperty(d => d.UpdateDate, DateTime.UtcNow));
        Invalidate();
    }

    public static string PlatformName(string platform) => platform switch
    {
        "windows" => "Windows",
        "macos" or "osx" => "macOS",
        "linux" => "Linux",
        "" => "Unbekannt",
        _ => platform,
    };

    public static string PlatformIcon(string platform) => platform switch
    {
        "windows" or "macos" or "osx" or "linux" => "monitor",
        _ => "smartphone",
    };

    private static void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        Cache.Clear();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
