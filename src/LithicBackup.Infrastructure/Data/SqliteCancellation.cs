using Microsoft.Data.Sqlite;

namespace LithicBackup.Infrastructure.Data;

/// <summary>
/// Stopping an SQLite statement part-way when a token is cancelled. While
/// <see cref="Watch"/> is in force, SQLite asks a progress handler on the
/// connection every few thousand virtual-machine instructions, and the
/// handler's answer once the token is cancelled aborts the running statement
/// with SQLITE_INTERRUPT, which <see cref="Interruptible{T}"/> turns into the
/// cancel. A check between rows alone cannot do this: an ordered read sorts
/// before its first row, and a VACUUM INTO is one long step.
/// </summary>
/// <remarks>
/// Only for statements an abort cannot harm: a read, or a statement outside any
/// transaction. An INSERT, UPDATE or DELETE aborted inside an explicit
/// transaction rolls the whole transaction back and returns the connection to
/// autocommit, under the code that opened it.
/// </remarks>
internal static class SqliteCancellation
{
    /// <summary>
    /// Instructions between two calls of the handler. VACUUM INTO copies pages in
    /// bulk with few instructions each: at 10,000 its cancel was felt about 330 ms
    /// late, at 1,000 within a few. A read pays a call into .NET per thousand
    /// instructions, a small fraction of the read.
    /// </summary>
    private const int Instructions = 1_000;

    /// <summary>
    /// Abort whatever runs on <paramref name="connection"/> once
    /// <paramref name="ct"/> is cancelled, until the result is disposed. Nothing
    /// to watch when the token cannot be cancelled.
    /// </summary>
    public static IDisposable Watch(SqliteConnection connection, CancellationToken ct)
    {
        if (!ct.CanBeCanceled)
            return Unwatched.Instance;
        var db = connection.Handle ?? throw new InvalidOperationException("The connection is not open.");
        SQLitePCL.raw.sqlite3_progress_handler(db, Instructions, _ => ct.IsCancellationRequested ? 1 : 0, null);
        return new Unwatch(db);
    }

    /// <summary>
    /// <paramref name="step"/> (a statement's execution, or a reader's next row),
    /// with an abort by <see cref="Watch"/> surfacing as the cancel it is.
    /// </summary>
    public static T Interruptible<T>(Func<T> step, CancellationToken ct)
    {
        try
        {
            return step();
        }
        catch (SqliteException ex)
            when (ex.SqliteErrorCode == SQLitePCL.raw.SQLITE_INTERRUPT && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    private sealed class Unwatch(SQLitePCL.sqlite3 db) : IDisposable
    {
        public void Dispose() => SQLitePCL.raw.sqlite3_progress_handler(db, 0, null, null);
    }

    private sealed class Unwatched : IDisposable
    {
        public static readonly Unwatched Instance = new();
        public void Dispose() { }
    }
}
