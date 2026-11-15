using Microsoft.Data.Sqlite;
using ThixClip.Core.Models;
using ThixClip.Shared;

namespace ThixClip.Core.Infrastructure;

/// <summary>
/// The clipboard history on disk (SQLite, history.db). Only the business layer (<see cref="Business.ClipService"/>,
/// <see cref="Business.SyncService"/>) uses this class; the UI never sees a query.
///
/// Tables follow the relational rules of the project: PascalCase names, key "Id" (64-bit), the audit columns
/// CreateDate/CreateUserId/UpdateDate/UpdateUserId and UpdateState (0 deleted, 1 created, 2 updated). A deleted entry
/// stays as a tombstone (UpdateState 0) until the cloud has it, then the row goes. Dates are Unix milliseconds (UTC).
/// The PNG and the thumbnail of an image live in ClipImage, so the list never loads them.
/// </summary>
internal static class HistoryStore
{
    /// <summary>The one local user of a desktop installation (CreateUserId / UpdateUserId).</summary>
    public const long LocalUserId = 1;

    private const int SchemaVersion = 1;

    private static readonly object Gate = new();
    private static SqliteConnection? _connection;
    private static string? _openedPath;

    /// <summary>Every row (tombstones included), without image bytes.</summary>
    public static List<(ClipEntry Entry, bool IsDeleted)> LoadAll()
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT {EntryColumns}, UpdateState FROM ClipEntry ORDER BY CopiedDate";
            using var reader = command.ExecuteReader();
            var rows = new List<(ClipEntry, bool)>();
            while (reader.Read())
            {
                rows.Add((ReadEntry(reader), reader.GetInt32(19) == 0));
            }

            return rows;
        }
    }

    public static long Insert(ClipEntry entry, byte[]? png, byte[]? thumbnail)
    {
        lock (Gate)
        {
            using var transaction = Connection.BeginTransaction();
            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ClipEntry (SyncId, Kind, Text, OcrText, OcrState, ContentHash, IsFavorite, IsPinned, CopiedDate, ClientUpdateDate,
                                       Source, ImageWidth, ImageHeight, ImageBytes, HasImage, SyncState, Revision,
                                       CreateDate, CreateUserId, UpdateDate, UpdateUserId, UpdateState)
                VALUES ($syncId, $kind, $text, $ocrText, $ocrState, $hash, $favorite, $pinned, $copied, $clientUpdate,
                        $source, $width, $height, $bytes, $hasImage, $syncState, $revision,
                        $created, $user, $now, $user, 1);
                SELECT last_insert_rowid();
                """;
            AddEntryParameters(command, entry);
            command.Parameters.AddWithValue("$created", ToMilliseconds(entry.CreateDate));
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$user", LocalUserId);
            var id = (long)command.ExecuteScalar()!;
            if (png is not null)
            {
                WriteImage(transaction, id, png, thumbnail);
            }

            transaction.Commit();
            return id;
        }
    }

    /// <summary>Writes every field of an entry (not the image bytes).</summary>
    public static void Update(ClipEntry entry)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = """
                UPDATE ClipEntry SET SyncId = $syncId, Kind = $kind, Text = $text, OcrText = $ocrText, OcrState = $ocrState, ContentHash = $hash,
                       IsFavorite = $favorite, IsPinned = $pinned, CopiedDate = $copied, ClientUpdateDate = $clientUpdate, Source = $source,
                       ImageWidth = $width, ImageHeight = $height, ImageBytes = $bytes, HasImage = $hasImage, SyncState = $syncState,
                       Revision = $revision, UpdateDate = $now, UpdateUserId = $user, UpdateState = 2
                WHERE Id = $id
                """;
            AddEntryParameters(command, entry);
            command.Parameters.AddWithValue("$id", entry.Id);
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$user", LocalUserId);
            command.ExecuteNonQuery();
        }
    }

    public static void SetImage(long id, byte[] png, byte[]? thumbnail)
    {
        lock (Gate)
        {
            using var transaction = Connection.BeginTransaction();
            WriteImage(transaction, id, png, thumbnail);
            transaction.Commit();
        }
    }

    public static byte[]? ReadPng(long id) => ReadBlob(id, "Png");

    public static byte[]? ReadThumbnail(long id) => ReadBlob(id, "Thumbnail");

    /// <summary>Marks an entry deleted (tombstone): its content goes at once, the row waits for the cloud.</summary>
    public static void MarkDeleted(long id, SyncState syncState)
    {
        lock (Gate)
        {
            using var transaction = Connection.BeginTransaction();
            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE ClipEntry SET Text = NULL, OcrText = NULL, HasImage = 0, SyncState = $syncState, ClientUpdateDate = $now,
                       UpdateDate = $now, UpdateUserId = $user, UpdateState = 0
                WHERE Id = $id;
                DELETE FROM ClipImage WHERE ClipEntryId = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$syncState", (int)syncState);
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$user", LocalUserId);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    /// <summary>Removes rows for good (entries the cloud no longer needs to hear about).</summary>
    public static void Remove(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        lock (Gate)
        {
            using var transaction = Connection.BeginTransaction();
            foreach (var id in ids)
            {
                using var command = Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM ClipImage WHERE ClipEntryId = $id; DELETE FROM ClipEntry WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>Gives the space of removed rows back (after a big clean-up).</summary>
    public static void Compact()
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Closes the database (tests, exit).</summary>
    public static void Close()
    {
        lock (Gate)
        {
            _connection?.Dispose();
            _connection = null;
            _openedPath = null;
            SqliteConnection.ClearAllPools();
        }
    }

    private const string EntryColumns =
        "Id, SyncId, Kind, Text, OcrText, OcrState, ContentHash, IsFavorite, IsPinned, CopiedDate, ClientUpdateDate, Source, " +
        "ImageWidth, ImageHeight, ImageBytes, HasImage, SyncState, Revision, CreateDate";

    private static SqliteConnection Connection
    {
        get
        {
            var path = AppPaths.DatabaseFile;
            if (_connection is not null && _openedPath == path)
            {
                return _connection;
            }

            _connection?.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            Migrate(connection);
            _connection = connection;
            _openedPath = path;
            return connection;
        }
    }

    private static void Migrate(SqliteConnection connection)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        using var versionQuery = connection.CreateCommand();
        versionQuery.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(versionQuery.ExecuteScalar());
        if (version >= SchemaVersion)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS ClipEntry (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SyncId TEXT NOT NULL UNIQUE,
                Kind INTEGER NOT NULL,
                Text TEXT NULL,
                OcrText TEXT NULL,
                OcrState INTEGER NOT NULL DEFAULT 0,
                ContentHash TEXT NOT NULL,
                IsFavorite INTEGER NOT NULL DEFAULT 0,
                IsPinned INTEGER NOT NULL DEFAULT 0,
                CopiedDate INTEGER NOT NULL,
                ClientUpdateDate INTEGER NOT NULL,
                Source TEXT NULL,
                ImageWidth INTEGER NULL,
                ImageHeight INTEGER NULL,
                ImageBytes INTEGER NULL,
                HasImage INTEGER NOT NULL DEFAULT 0,
                SyncState INTEGER NOT NULL DEFAULT 0,
                Revision INTEGER NOT NULL DEFAULT 0,
                CreateDate INTEGER NOT NULL,
                CreateUserId INTEGER NOT NULL,
                UpdateDate INTEGER NOT NULL,
                UpdateUserId INTEGER NOT NULL,
                UpdateState INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS IX_ClipEntry_ContentHash ON ClipEntry (ContentHash);
            CREATE TABLE IF NOT EXISTS ClipImage (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ClipEntryId INTEGER NOT NULL UNIQUE REFERENCES ClipEntry (Id) ON DELETE CASCADE,
                Png BLOB NOT NULL,
                Thumbnail BLOB NULL,
                CreateDate INTEGER NOT NULL,
                CreateUserId INTEGER NOT NULL,
                UpdateDate INTEGER NOT NULL,
                UpdateUserId INTEGER NOT NULL
            );
            PRAGMA user_version = {SchemaVersion};
            """;
        command.ExecuteNonQuery();
    }

    private static void WriteImage(SqliteTransaction transaction, long id, byte[] png, byte[]? thumbnail)
    {
        using var command = Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ClipImage (ClipEntryId, Png, Thumbnail, CreateDate, CreateUserId, UpdateDate, UpdateUserId)
            VALUES ($id, $png, $thumbnail, $now, $user, $now, $user)
            ON CONFLICT (ClipEntryId) DO UPDATE SET Png = excluded.Png, Thumbnail = excluded.Thumbnail,
                UpdateDate = excluded.UpdateDate, UpdateUserId = excluded.UpdateUserId;
            UPDATE ClipEntry SET HasImage = 1 WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$png", png);
        command.Parameters.AddWithValue("$thumbnail", (object?)thumbnail ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$user", LocalUserId);
        command.ExecuteNonQuery();
    }

    private static byte[]? ReadBlob(long id, string column)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT {column} FROM ClipImage WHERE ClipEntryId = $id";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() as byte[];
        }
    }

    private static void AddEntryParameters(SqliteCommand command, ClipEntry entry)
    {
        command.Parameters.AddWithValue("$syncId", entry.SyncId.ToString("D"));
        command.Parameters.AddWithValue("$kind", (int)entry.Kind);
        command.Parameters.AddWithValue("$text", (object?)entry.Text ?? DBNull.Value);
        command.Parameters.AddWithValue("$ocrText", (object?)entry.OcrText ?? DBNull.Value);
        command.Parameters.AddWithValue("$ocrState", (int)entry.OcrState);
        command.Parameters.AddWithValue("$hash", entry.ContentHash);
        command.Parameters.AddWithValue("$favorite", entry.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$pinned", entry.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$copied", ToMilliseconds(entry.CopiedDate));
        command.Parameters.AddWithValue("$clientUpdate", ToMilliseconds(entry.UpdateDate));
        command.Parameters.AddWithValue("$source", (object?)entry.Source ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", (object?)entry.ImageWidth ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)entry.ImageHeight ?? DBNull.Value);
        command.Parameters.AddWithValue("$bytes", (object?)entry.ImageBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$hasImage", entry.HasImage ? 1 : 0);
        command.Parameters.AddWithValue("$syncState", (int)entry.SyncState);
        command.Parameters.AddWithValue("$revision", entry.Revision);
    }

    private static ClipEntry ReadEntry(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        SyncId = Guid.Parse(reader.GetString(1)),
        Kind = (ClipKind)reader.GetInt32(2),
        Text = reader.IsDBNull(3) ? null : reader.GetString(3),
        OcrText = reader.IsDBNull(4) ? null : reader.GetString(4),
        OcrState = (OcrState)reader.GetInt32(5),
        ContentHash = reader.GetString(6),
        IsFavorite = reader.GetInt32(7) != 0,
        IsPinned = reader.GetInt32(8) != 0,
        CopiedDate = FromMilliseconds(reader.GetInt64(9)),
        UpdateDate = FromMilliseconds(reader.GetInt64(10)),
        Source = reader.IsDBNull(11) ? null : reader.GetString(11),
        ImageWidth = reader.IsDBNull(12) ? null : reader.GetInt32(12),
        ImageHeight = reader.IsDBNull(13) ? null : reader.GetInt32(13),
        ImageBytes = reader.IsDBNull(14) ? null : reader.GetInt64(14),
        HasImage = reader.GetInt32(15) != 0,
        SyncState = (SyncState)reader.GetInt32(16),
        Revision = reader.GetInt64(17),
        CreateDate = FromMilliseconds(reader.GetInt64(18)),
    };

    private static long Now() => AppClock.UtcNow.ToUnixTimeMilliseconds();

    private static long ToMilliseconds(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromMilliseconds(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
}
