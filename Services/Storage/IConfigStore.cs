using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// The single seam every config repository sits on (SqliteConfigStore-Plan Section 5A). It owns
/// connection and transaction handling so the ~7 stores moving off JSON in Phase B do not each
/// re-implement open/commit/rollback - the duplication that made the 2026-06-12 atomic-write
/// bug need fixing in multiple services instead of one.
///
/// Reads run on a short-lived connection. Writes run inside a transaction and automatically
/// bump the <see cref="ConfigChangeToken"/> on commit (Section 5B.2). No store keeps a long-lived
/// connection (Section 5B.1).
///
/// Note: the change token is available for cache-invalidation but is currently advisory - the
/// two TTL-caching readers (ProtectedPrincipalService, ADAttributeEditorService) do NOT consult
/// it and instead accept a <=30s staleness window (Section 5B.2 permitted this). Only the store and its
/// tests reference <see cref="GetChangeToken"/> today; wiring the readers to it is a future option.
/// </summary>
public interface IConfigStore
{
    /// <summary>
    /// The current change token. Intended for readers that want to compare-and-reload, but no
    /// production reader consults it yet (see the type remarks); the TTL caches accept <=30s drift.
    /// </summary>
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
