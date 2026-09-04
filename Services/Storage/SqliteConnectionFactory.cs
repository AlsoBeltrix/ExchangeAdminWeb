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

        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText =
            "PRAGMA journal_mode=WAL;" +
            "PRAGMA busy_timeout=5000;" +
            "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }
}
