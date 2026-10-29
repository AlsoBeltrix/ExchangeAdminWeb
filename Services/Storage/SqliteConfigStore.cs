using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// SQLite-backed <see cref="IConfigStore"/>. Opens a fresh connection per operation via
/// <see cref="SqliteConnectionFactory"/> (never a shared long-lived connection - Section 5B.1) and
/// bumps the change token inside every write transaction (Section 5B.2).
/// </summary>
public sealed class SqliteConfigStore : IConfigStore
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteConfigStore(SqliteConnectionFactory factory) => _factory = factory;

    public long GetChangeToken()
    {
        using var connection = _factory.Open();
        return ConfigChangeToken.Read(connection);
    }

    public T Read<T>(Func<SqliteConnection, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        using var connection = _factory.Open();
        return read(connection);
    }

    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write) => Write(write, CancellationToken.None);

    /// <summary>
    /// kd-58 B1 (docs/CancellationDepth-Plan.md 4.4). The write, the caller's token looked at four times and nowhere else:
    /// before the connection opens (C1); before <c>BEGIN IMMEDIATE</c> (C2); as it returns (C3) - after any busy wait, which
    /// no token reaches - so the callback never runs; and after the callback and the change-token bump, before the commit
    /// (C4). A cancel at C3 or C4 leaves the transaction uncommitted, and its disposal rolls it back - the callback's rows
    /// and the bump together - so nothing is half-written and the other instance on the shared database sees nothing. After
    /// the commit nothing looks at it: a cancel there is not honoured, and the write returns its result. None, the
    /// token-less form's, writes as before.
    /// </summary>
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested(); // C1: nothing opened
        using var connection = _factory.Open();
        cancellationToken.ThrowIfCancellationRequested(); // C2: nothing begun, no busy wait

        // BEGIN IMMEDIATE: the write lock is taken here, before the callback reads anything, so a
        // callback may read, decide and write with no other writer - the other instance on the
        // shared database included - committing in between (aud-a33b, aud-a33c). This provider's
        // parameterless BeginTransaction() already does the same (aud-a33 review); it is spelled
        // out because repositories now rely on it, and SqliteConfigStoreTests pins it.
        using var transaction = connection.BeginTransaction(deferred: false);
        cancellationToken.ThrowIfCancellationRequested(); // C3: rolled back; the callback never runs

        var result = write(connection, transaction);
        ConfigChangeToken.Bump(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested(); // C4: rolled back - the callback's rows and the bump together
        transaction.Commit();
        AfterCommit?.Invoke();
        return result;
    }

    /// <summary>
    /// kd-58 B1. A seam for the tests, run once a write has committed and before it returns - where a cancel is not looked at.
    /// Null in the app.
    /// </summary>
    internal Action? AfterCommit { get; set; }

    public void Write(Action<SqliteConnection, SqliteTransaction> write) => Write(write, CancellationToken.None);

    /// <summary>kd-58 B1. The no-result write with a token, as the overload above.</summary>
    public void Write(Action<SqliteConnection, SqliteTransaction> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        Write<object?>((connection, transaction) =>
        {
            write(connection, transaction);
            return null;
        }, cancellationToken);
    }
}
