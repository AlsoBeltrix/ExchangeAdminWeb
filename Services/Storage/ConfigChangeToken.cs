using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// A monotonically increasing version stamp stored in <c>schema_meta</c> and bumped on every
/// config write. Readers that cache config compare the current token against the one they
/// cached under; a mismatch means an out-of-band write happened (the prod→dev refresh tool, a
/// manual DB edit, or simply a different service instance) and the cache must be reloaded.
///
/// This replaces the per-instance TTL/cache-until-save model that would silently drift once a
/// shared database has writers outside the caching instance (SqliteConfigStore-Plan §5B.2).
/// Read is a single indexed primary-key lookup, so callers can check it cheaply on every read.
/// </summary>
public sealed class ConfigChangeToken
{
    internal const string MetaKey = "config_change_token";

    /// <summary>
    /// Returns the current change token, or 0 if it has never been written. Uses the supplied
    /// connection (and optional transaction) so a writer can read-modify-write atomically.
    /// </summary>
    public static long Read(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM schema_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", MetaKey);

        var raw = command.ExecuteScalar();
        if (raw is null || raw is DBNull)
            return 0;

        return long.TryParse(Convert.ToString(raw), out var value) ? value : 0;
    }

    /// <summary>
    /// Increments the change token within the caller's transaction so the bump commits or rolls
    /// back together with the write that triggered it. Returns the new token value.
    /// </summary>
    public static long Bump(SqliteConnection connection, SqliteTransaction transaction)
    {
        var next = Read(connection, transaction) + 1;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO schema_meta (key, value) VALUES ($key, $value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", MetaKey);
        command.Parameters.AddWithValue("$value", next.ToString());
        command.ExecuteNonQuery();

        return next;
    }
}
