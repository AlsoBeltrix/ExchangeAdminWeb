using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Opens short-lived SQLite connections to the config database. Registered as a singleton,
/// but it deliberately does NOT hold a single shared <see cref="SqliteConnection"/>: config
/// consumers are a mix of Singleton and Scoped services (see SqliteConfigStore-Plan §5B.1),
/// and a long-lived shared connection is not safe across those lifetimes. Every operation
/// opens its own connection and disposes it; SQLite WAL mode + busy timeout handle the
/// single-writer/multiple-reader concurrency this app produces.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>The resolved absolute path of the config database file.</summary>
    public string DatabasePath { get; }

    public SqliteConnectionFactory(string databasePath)
    {
        DatabasePath = databasePath;

        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Default (private) cache, NOT shared cache: shared cache makes in-process connections
        // share table locks, which surfaces SQLITE_LOCKED to readers instead of the WAL
        // snapshot isolation we depend on (busy_timeout does not cover shared-cache table
        // locks). Private cache + WAL gives the single-writer/multiple-reader behavior the plan
        // assumes.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <summary>
    /// Opens a new connection with the standard PRAGMAs applied (WAL journal, a busy timeout
    /// so concurrent access waits instead of failing immediately, and enforced foreign keys).
    /// The caller owns the returned connection and must dispose it.
    /// </summary>
    public SqliteConnection Open()
    {
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
