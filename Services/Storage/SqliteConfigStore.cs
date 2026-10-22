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

    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var connection = _factory.Open();

        // BEGIN IMMEDIATE: the write lock is taken here, before the callback reads anything, so a
        // callback may read, decide and write with no other writer - the other instance on the
        // shared database included - committing in between (aud-a33b, aud-a33c). This provider's
        // parameterless BeginTransaction() already does the same (aud-a33 review); it is spelled
        // out because repositories now rely on it, and SqliteConfigStoreTests pins it.
        using var transaction = connection.BeginTransaction(deferred: false);

        var result = write(connection, transaction);
        ConfigChangeToken.Bump(connection, transaction);
        transaction.Commit();
        return result;
    }

    public void Write(Action<SqliteConnection, SqliteTransaction> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        Write<object?>((connection, transaction) =>
        {
            write(connection, transaction);
            return null;
        });
    }
}
