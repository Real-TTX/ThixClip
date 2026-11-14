using Microsoft.EntityFrameworkCore;
using ThixClip.Cloud.Data;
using ThixClip.Shared;

namespace ThixClip.Cloud.Business;

/// <summary>The account as its owner sees it.</summary>
public sealed record AccountView(string UserName, string DisplayName, int RetentionDays, string Theme, bool IsAdmin, DateTime CreateDate);

/// <summary>What a person changes about their own account: the name, how long clips are kept, the theme, the password.</summary>
public static class AccountService
{
    public static async Task<Outcome<AccountView>> GetAsync(AppDbContext db, Actor actor)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == actor.UserId);
        return user is null
            ? Outcome<AccountView>.Missing()
            : Outcome<AccountView>.Success(new AccountView(user.UserName, user.DisplayName, user.RetentionDays, user.Theme, user.IsAdmin, user.CreateDate));
    }

    /// <summary>The account as a device sees it (GET /api/v1/account).</summary>
    public static async Task<Outcome<AccountInfo>> InfoAsync(AppDbContext db, Actor actor, string serverVersion)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == actor.UserId);
        return user is null
            ? Outcome<AccountInfo>.Missing()
            : Outcome<AccountInfo>.Success(new AccountInfo(user.UserName, user.DisplayName, user.RetentionDays, actor.DeviceName ?? string.Empty, serverVersion));
    }

    /// <summary>The tab "Allgemein": the name and how long clips are kept. A shorter time removes what is older at once (on every device too).</summary>
    public static async Task<Outcome> SaveGeneralAsync(AppDbContext db, Actor actor, string displayName, int retentionDays)
    {
        var name = (displayName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return Outcome.Fail("Bitte einen Namen eingeben.", "DisplayName");
        }

        if (name.Length > 120)
        {
            return Outcome.Fail("Der Name hat höchstens 120 Zeichen.", "DisplayName");
        }

        if (!SyncContract.IsValidRetention(retentionDays))
        {
            return Outcome.Fail("Bitte eine der angebotenen Speicherdauern wählen.", "RetentionDays");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == actor.UserId);
        if (user is null)
        {
            return Outcome.Missing();
        }

        user.DisplayName = name;
        user.RetentionDays = retentionDays;
        await db.SaveChangesAsync();
        SessionService.Invalidate();
        await ClipService.ApplyRetentionAsync(db, actor.UserId);
        return Outcome.Success();
    }

    /// <summary>A device changes how long clips are kept (PUT /api/v1/account).</summary>
    public static async Task<Outcome<AccountInfo>> SetRetentionAsync(AppDbContext db, Actor actor, int retentionDays, string serverVersion)
    {
        if (!SyncContract.IsValidRetention(retentionDays))
        {
            return Outcome<AccountInfo>.Fail("Diese Speicherdauer gibt es nicht. Erlaubt: " + string.Join(", ", SyncContract.RetentionChoices) + " Tage (0 = unbegrenzt).", "retentionDays");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == actor.UserId);
        if (user is null)
        {
            return Outcome<AccountInfo>.Missing();
        }

        user.RetentionDays = retentionDays;
        await db.SaveChangesAsync();
        await ClipService.ApplyRetentionAsync(db, actor.UserId);
        return await InfoAsync(db, actor, serverVersion);
    }

    public static async Task SetThemeAsync(AppDbContext db, Actor actor, string theme)
    {
        var value = theme is "light" or "dark" ? theme : "system";
        await db.Users.Where(u => u.Id == actor.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Theme, value));
        SessionService.Invalidate();
    }

    /// <summary>A new password: the current one is checked; the other sessions of the browser end, the devices stay signed in.</summary>
    public static async Task<Outcome> ChangePasswordAsync(AppDbContext db, Actor actor, string current, string password, string repeat, long? currentSessionId)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == actor.UserId);
        if (user is null)
        {
            return Outcome.Missing();
        }

        if (!Passwords.Verify(user, current))
        {
            return Outcome.Fail("Das aktuelle Passwort stimmt nicht.", "Current");
        }

        if (Passwords.Problem(password, repeat) is { } problem)
        {
            return Outcome.Fail(problem, problem.Contains("überein", StringComparison.Ordinal) ? "Repeat" : "New");
        }

        if (password == current)
        {
            return Outcome.Fail("Das neue Passwort muss sich vom aktuellen unterscheiden.", "New");
        }

        user.PasswordHash = Passwords.Hash(user, password);
        user.MustChangePassword = false;
        await db.SaveChangesAsync();
        await SessionService.RevokeAllAsync(db, user.Id, currentSessionId);
        return Outcome.Success();
    }

    public static string RetentionName(int days) => days switch
    {
        0 => "Unbegrenzt",
        1 => "1 Tag",
        7 => "7 Tage",
        30 => "30 Tage",
        90 => "90 Tage",
        365 => "1 Jahr",
        _ => $"{days} Tage",
    };
}
