using Microsoft.EntityFrameworkCore;
using Npgsql;
using SkiaSharp;
using ThixClip.Cloud.Business;
using ThixClip.Cloud.Data;
using ThixClip.Shared;

namespace ThixClip.Cloud.Tests;

/// <summary>A test that needs PostgreSQL: it runs when <c>THIXCLIP_TEST_DB</c> names a server (a connection string with the right to create databases), else it is skipped.</summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (TestDatabase.Admin is null)
        {
            Skip = "THIXCLIP_TEST_DB is not set (a PostgreSQL connection string with the right to create databases).";
        }
    }
}

/// <summary>A database of its own for one test, migrated like the real one (so the migrations are tested too), dropped afterwards.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _name = "thixclip_t_" + Guid.NewGuid().ToString("N")[..12];

    private TestDatabase()
    {
    }

    public static string? Admin => Environment.GetEnvironmentVariable("THIXCLIP_TEST_DB");

    public string ConnectionString { get; private set; } = string.Empty;

    public AppDbContext Db { get; private set; } = null!;

    public static async Task<TestDatabase> CreateAsync()
    {
        var test = new TestDatabase();
        await using (var admin = new NpgsqlConnection(Admin))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{test._name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        test.ConnectionString = new NpgsqlConnectionStringBuilder(Admin) { Database = test._name, Timeout = 30 }.ConnectionString;
        test.Db = test.NewContext();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await test.Db.Database.MigrateAsync();
                return test;
            }
            catch (NpgsqlException) when (attempt < 3)
            {
                // a server that has just created the database may refuse the first connection for a moment
                NpgsqlConnection.ClearAllPools();
                await Task.Delay(1000);
            }
        }
    }

    /// <summary>A second context on the same database (another request, another device).</summary>
    public AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task<User> AddUserAsync(string name, bool admin = false, string password = "secret-password")
    {
        var user = new User { UserName = name.ToLowerInvariant(), DisplayName = name, IsAdmin = admin };
        user.PasswordHash = Passwords.Hash(user, password);
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(Admin);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>Clips and pictures for the tests.</summary>
public static class Samples
{
    public static ClipDto Text(string text, DateTimeOffset? at = null, Guid? syncId = null, bool deleted = false, bool favorite = false)
    {
        var when = at ?? DateTimeOffset.UtcNow.AddMinutes(-1);
        return new ClipDto
        {
            SyncId = syncId ?? Guid.NewGuid(),
            Kind = ClipContent.KindOfText(text),
            Text = text,
            ContentHash = ClipContent.HashText(text),
            CreatedAt = when,
            CopiedAt = when,
            UpdatedAt = when,
            IsDeleted = deleted,
            IsFavorite = favorite,
            Source = "Test-PC",
        };
    }

    public static ClipDto Image(byte[] png, DateTimeOffset? at = null, Guid? syncId = null, string? ocr = null)
    {
        var when = at ?? DateTimeOffset.UtcNow.AddMinutes(-1);
        return new ClipDto
        {
            SyncId = syncId ?? Guid.NewGuid(),
            Kind = ClipKind.Image,
            OcrText = ocr,
            ContentHash = ClipContent.HashBytes(png),
            CreatedAt = when,
            CopiedAt = when,
            UpdatedAt = when,
            Source = "Test-PC",
        };
    }

    /// <summary>A picture in a format: a gradient with a square, so that it is not empty.</summary>
    public static byte[] Picture(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x2F, 0x6F, 0xED));
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(width / 4f, height / 4f, width / 2f, height / 2f, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }
}
