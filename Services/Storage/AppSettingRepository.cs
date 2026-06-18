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
