using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ThixClip.Cloud.Data;

namespace ThixClip.Cloud.Business;

/// <summary>
/// The revisions of the clips of an account. Every change of a clip takes the next number of the one sequence <c>ClipRevision</c>, inside a
/// transaction that holds the lock of the account (<c>pg_advisory_xact_lock</c>). So the changes of one account are numbered in the order
/// they are committed: once a device has seen revision N, nothing below N can still appear, and "everything above N" never misses a change.
/// After the commit the waiting long polls of the account are woken (<see cref="RevisionSignals"/>).
/// </summary>
public static class Revisions
{
    /// <summary>Runs <paramref name="change"/> in a transaction that holds the lock of the account, then wakes the waiters of the account.</summary>
    public static async Task<T> ChangeAsync<T>(AppDbContext db, long userId, Func<Task<T>> change, CancellationToken ct = default)
    {
        T result;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await ExecuteAsync(db, "SELECT pg_advisory_xact_lock(@p0)", ct, LockKey(userId));
            result = await change();
            await transaction.CommitAsync(ct);
        }

        RevisionSignals.Notify(userId);
        return result;
    }

    /// <summary>The next revision (only call it inside <see cref="ChangeAsync{T}"/>).</summary>
    public static async Task<long> NextAsync(AppDbContext db, CancellationToken ct = default)
        => await ScalarAsync(db, $"SELECT nextval('\"{AppDbContext.RevisionSequence}\"')", ct);

    /// <summary>The highest revision of the account (0 when it has no clips).</summary>
    public static async Task<long> LatestAsync(AppDbContext db, long userId, CancellationToken ct = default)
        => await db.Clips.Where(c => c.UserId == userId).MaxAsync(c => (long?)c.Revision, ct) ?? 0;

    /// <summary>The key of the advisory lock of an account (its id, in a range of its own so that nothing else collides).</summary>
    private static long LockKey(long userId) => 0x5443_0000_0000_0000L | userId;

    internal static async Task<long> ScalarAsync(AppDbContext db, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = await CommandAsync(db, sql, parameters, ct);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static async Task<List<long>> ColumnAsync(AppDbContext db, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = await CommandAsync(db, sql, parameters, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var values = new List<long>();
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetInt64(0));
        }

        return values;
    }

    internal static async Task ExecuteAsync(AppDbContext db, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = await CommandAsync(db, sql, parameters, ct);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A command on the connection of the context, in its transaction; the parameters are @p0, @p1 ...</summary>
    private static async Task<DbCommand> CommandAsync(AppDbContext db, string sql, object[] parameters, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "p" + i;
            parameter.Value = parameters[i];
            command.Parameters.Add(parameter);
        }

        return command;
    }
}

/// <summary>
/// The long polls of the accounts, in memory: a waiter takes the signal of its account BEFORE it reads the latest revision, and every committed
/// change completes the signal and replaces it, so no change between the read and the wait is lost. One instance of the server.
/// </summary>
public static class RevisionSignals
{
    private static readonly ConcurrentDictionary<long, TaskCompletionSource> Signals = new();

    /// <summary>The signal the next change of the account completes.</summary>
    public static Task Next(long userId)
        => Signals.GetOrAdd(userId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public static void Notify(long userId)
    {
        if (Signals.TryRemove(userId, out var signal))
        {
            signal.TrySetResult();
        }
    }
}
