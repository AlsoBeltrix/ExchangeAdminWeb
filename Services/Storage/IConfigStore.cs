using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// The single seam every config repository sits on (SqliteConfigStore-Plan §5A). It owns
/// connection and transaction handling so the ~7 stores moving off JSON in Phase B do not each
/// re-implement open/commit/rollback — the duplication that made the 2026-06-12 atomic-write
/// bug need fixing in multiple services instead of one.
///
/// Reads run on a short-lived connection. Writes run inside a transaction and automatically
/// bump the <see cref="ConfigChangeToken"/> on commit, so cache-holding readers can detect the
/// change (§5B.2). No store keeps a long-lived connection (§5B.1).
/// </summary>
public interface IConfigStore
{
    /// <summary>The current change token; readers compare this to decide whether to reload.</summary>
    long GetChangeToken();

    /// <summary>Runs a read against a short-lived connection and returns its result.</summary>
    T Read<T>(Func<SqliteConnection, T> read);

    /// <summary>
    /// Runs a write inside a transaction. The change token is bumped and the transaction
    /// committed only if <paramref name="write"/> returns without throwing; any exception rolls
    /// the whole unit (write + token bump) back. Returns the callback's result.
    /// </summary>
    T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write);

    /// <summary>Write overload for operations with no return value.</summary>
    void Write(Action<SqliteConnection, SqliteTransaction> write);
}
