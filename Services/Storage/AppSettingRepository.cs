using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Repository for the single-row <c>app_setting</c> key/value table. Used for scalar settings
/// that previously lived as standalone files (e.g. extended-log-level.txt) or appsettings keys
/// migrated in later phases. Sits on <see cref="IConfigStore"/> so connection/transaction and
/// change-token handling stay in one place.
/// </summary>
public sealed class AppSettingRepository
{
    private readonly IConfigStore _store;

    public AppSettingRepository(IConfigStore store) => _store = store;

    /// <summary>A change watcher over this repository's store, for the caching reader that sits on it.</summary>
    public ConfigChangeWatcher CreateChangeWatcher(ILogger? logger = null) => new(_store, logger);

    /// <summary>Returns the value for <paramref name="key"/>, or null if unset.</summary>
    public string? Get(string key)
    {
        return _store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_setting WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            var raw = command.ExecuteScalar();
            return raw is null or DBNull ? null : Convert.ToString(raw);
        });
    }

    /// <summary>Inserts or updates <paramref name="key"/> to <paramref name="value"/>.</summary>
    public void Set(string key, string value)
    {
        _store.Write((connection, transaction) => Upsert(connection, transaction, key, value));
    }

    /// <summary>
    /// Inserts <paramref name="key"/> only if it is absent. Returns true if a row was written.
    /// Used by the one-time importer so a value already in the DB is never overwritten by the
    /// legacy file.
    /// </summary>
    public bool SetIfMissing(string key, string value)
    {
        return _store.Write((connection, transaction) =>
        {
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT 1 FROM app_setting WHERE key = $key;";
            check.Parameters.AddWithValue("$key", key);
            if (check.ExecuteScalar() is not null)
                return false;

            Upsert(connection, transaction, key, value);
            return true;
        });
    }

    /// <summary>
    /// The value for <paramref name="key"/> on <paramref name="connection"/>, inside <paramref name="transaction"/> when one is
    /// given; null if unset. For a caller that reads a setting in the same transaction as its own rows
    /// (docs/DirectoryObjectVerify-Plan.md B.3). Throws when the read fails.
    /// </summary>
    internal static string? ReadValue(SqliteConnection connection, SqliteTransaction? transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM app_setting WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var raw = command.ExecuteScalar();
        return raw is null or DBNull ? null : Convert.ToString(raw);
    }

    /// <summary>
    /// docs/DirectoryObjectVerify-Plan.md B.3. Compare-and-swap, inside the caller's write transaction: sets
    /// <paramref name="key"/> to <paramref name="newValue"/> only while it still holds exactly <paramref name="expectedValue"/>
    /// (null: the key is absent), and returns false - writing nothing - otherwise. <see cref="Set"/> upserts blind and is never
    /// used for a key that is authorization state.
    /// </summary>
    public static bool ReplaceIfEqual(
        SqliteConnection connection, SqliteTransaction transaction, string key, string? expectedValue, string newValue)
    {
        ArgumentNullException.ThrowIfNull(newValue);
        var current = ReadValue(connection, transaction, key);
        if (!string.Equals(current, expectedValue, StringComparison.Ordinal))
            return false;

        Upsert(connection, transaction, key, newValue);
        return true;
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO app_setting (key, value) VALUES ($key, $value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
