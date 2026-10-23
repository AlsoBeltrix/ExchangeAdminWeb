using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Opens short-lived SQLite connections to the config database. Registered as a singleton,
/// but it deliberately does NOT hold a single shared <see cref="SqliteConnection"/>: config
/// consumers are a mix of Singleton and Scoped services (see SqliteConfigStore-Plan Section 5B.1),
/// and a long-lived shared connection is not safe across those lifetimes. Every operation
/// opens its own connection and disposes it; SQLite WAL mode + busy timeout handle the
/// single-writer/multiple-reader concurrency this app produces.
/// </summary>
public sealed class SqliteConnectionFactory
{
    /// <summary>
    /// How long a statement on a connection from this factory waits for a lock another connection holds before it fails
    /// with SQLITE_BUSY: 5 seconds, the one busy wait (aud-a41).
    /// </summary>
    public static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(5);

    private readonly string _connectionString;
    private readonly bool _mustExist;

    /// <summary>The resolved absolute path of the config database file.</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// <paramref name="mustExist"/> false (the default, and the jobs database): the directory is
    /// created and the file is created on first open. True (a configured shared
    /// <c>ConfigStore:Path</c>, docs/SharedConfigDb-Plan.md AC1): nothing is created, the file
    /// is opened read/write WITHOUT create, and <see cref="Open"/> throws
    /// <see cref="FileNotFoundException"/> when the file is missing - a missing shared database
    /// must stop the app, never be replaced by a fresh empty one (review finding scd-1).
    /// </summary>
    public SqliteConnectionFactory(string databasePath, bool mustExist = false)
    {
        DatabasePath = databasePath;
        _mustExist = mustExist;

        if (!mustExist)
        {
            var dir = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        // Default (private) cache, NOT shared cache: shared cache makes in-process connections
        // share table locks, which surfaces SQLITE_LOCKED to readers instead of the WAL
        // snapshot isolation we depend on (busy_timeout does not cover shared-cache table
        // locks). Private cache + WAL gives the single-writer/multiple-reader behavior the plan
        // assumes.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mustExist ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <summary>
    /// Opens a new connection with the standard PRAGMAs applied (WAL journal, a busy timeout
    /// so concurrent access waits instead of failing immediately, and enforced foreign keys).
    /// The caller owns the returned connection and must dispose it.
    /// </summary>
    /// <remarks>
    /// A statement that meets a lock another connection holds waits <see cref="BusyTimeout"/> and then fails with
    /// SQLITE_BUSY - 5 seconds, measured at 5.1 to 6.0 (Windows rounds each of SQLite's sleeps up). Two settings make that
    /// one wait (aud-a41). <c>busy_timeout</c> is SQLite's own wait inside each step. The connection's
    /// <see cref="SqliteConnection.DefaultTimeout"/> is Microsoft.Data.Sqlite's: it runs a busy statement again, each run
    /// another full <c>busy_timeout</c>, until its command timeout has passed - 30 seconds when unset, which made a held lock
    /// a 35-second wait. Set to the same 5 seconds, it has passed by the time SQLite's wait gives up, so nothing runs again;
    /// and a statement SQLite fails without waiting - a new database file's switch to WAL, measured - is retried for the same
    /// 5 seconds. It bounds only that retry, not how long a statement may run. It is set on each connection rather than in
    /// the connection string, so the pool's key stays the plain string that code clearing this file's pool rebuilds.
    /// </remarks>
    public SqliteConnection Open()
    {
        // Checked explicitly rather than relying on SQLITE_CANTOPEN so the operator sees the
        // path and the key, not an opaque native error code.
        if (_mustExist && !File.Exists(DatabasePath))
        {
            throw new FileNotFoundException(
                $"Config database not found at '{DatabasePath}' ({ConfigStorePath.Key}). The configured " +
                "shared database must already exist; the app never creates it. Check the key in " +
                "appsettings.json, or run tools/Move-ConfigDbToShared.ps1 to create the shared file.",
                DatabasePath);
        }

        var connection = new SqliteConnection(_connectionString)
        {
            DefaultTimeout = (int)BusyTimeout.TotalSeconds,
        };
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText =
            "PRAGMA journal_mode=WAL;" +
            $"PRAGMA busy_timeout={(int)BusyTimeout.TotalMilliseconds};" +
            "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }
}
