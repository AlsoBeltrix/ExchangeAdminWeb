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
/// The change token is consulted (docs/SharedConfigDb-Plan.md AC4, decision 2026-09-04): the
/// four caching readers (ProtectedPrincipalService, ADAttributeEditorService, PermissionValidator,
/// ExtendedLogService) each hold a <see cref="ConfigChangeWatcher"/> over this store and reload
/// when the token moves - that is how a write by the other instance on the shared database
/// reaches this one without a restart. Their TTLs remain as a bound when the token is unreadable.
/// </summary>
public interface IConfigStore
{
    /// <summary>
    /// The current change token, bumped inside every <see cref="Write"/>. Read through
    /// <see cref="ConfigChangeWatcher"/> by the caching readers (see the type remarks).
    /// </summary>
    long GetChangeToken();

    /// <summary>Runs a read against a short-lived connection and returns its result.</summary>
    T Read<T>(Func<SqliteConnection, T> read);

    /// <summary>
    /// Runs a write inside a transaction. The change token is bumped and the transaction
    /// committed only if <paramref name="write"/> returns without throwing; any exception rolls
    /// the whole unit (write + token bump) back. Returns the callback's result.
    /// </summary>
    /// <remarks>
    /// The transaction holds the write lock from the moment it opens, so anything the callback
    /// reads through it is still true when it writes. A decision about what to write belongs
    /// inside the callback, made from what it reads there: dev and prod share this database, and
    /// a read made before the call can be overtaken by the other instance (aud-a33b, aud-a33c).
    /// </remarks>
    T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write);

    /// <summary>Write overload for operations with no return value.</summary>
    void Write(Action<SqliteConnection, SqliteTransaction> write);

    /// <summary>
    /// kd-58 B1 (docs/CancellationDepth-Plan.md 4.3, 4.4; aud-a67 (12)). The write, with a token the store looks at before it
    /// commits and never after: a cancel before the commit leaves nothing written - the transaction, its change-token bump
    /// included, rolls back whole - and a cancel during or after the commit is not honoured, the write returning its result.
    /// This default looks once and then writes, for the stand-ins that implement only the token-less form;
    /// <see cref="SqliteConfigStore"/> looks at each of its four points itself.
    /// </summary>
    T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Write(write);
    }

    /// <summary>kd-58 B1. The no-result write with a token, as the overload above.</summary>
    void Write(Action<SqliteConnection, SqliteTransaction> write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(write);
    }
}
