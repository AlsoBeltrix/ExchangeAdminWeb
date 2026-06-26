using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Repository for the per-module key/value <c>module_config</c> table (module_id, config_key,
/// config_value). Backs <see cref="ModuleConfigService"/>, which ~17 feature modules funnel
/// their config through. Sits on <see cref="IConfigStore"/>.
///
/// Corruption in the file world meant an unparseable JSON file; with per-row storage that
/// failure class is gone — a "corrupt" probe now means the table cannot be opened/read at all
/// (a DB-integrity failure), surfaced by <see cref="TryReadModule"/> returning false.
/// </summary>
public sealed class ModuleConfigRepository
{
    private readonly IConfigStore _store;

    public ModuleConfigRepository(IConfigStore store) => _store = store;

    /// <summary>Returns a module's config as a case-insensitive dictionary (empty if none).</summary>
    public Dictionary<string, string> GetModule(string moduleId)
    {
        return _store.Read(connection => ReadModule(connection, moduleId));
    }

    /// <summary>
    /// Attempts to read a module's config. Returns false if the read throws (the DB-integrity
    /// analogue of the old "file is corrupt" probe); the out dictionary is empty in that case.
    /// </summary>
    public bool TryReadModule(string moduleId, out Dictionary<string, string> config)
    {
        try
        {
            config = GetModule(moduleId);
            return true;
        }
        catch
        {
            config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }
    }

    /// <summary>
    /// True if the module has been configured — i.e. saved or imported at least once, even if
    /// its config is empty. Backed by the presence marker, not row count, so an explicitly
    /// empty config still suppresses the legacy appsettings fallback (file-world parity).
    /// </summary>
    public bool HasModule(string moduleId)
    {
        return _store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM module_config_present WHERE module_id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", moduleId);
            return command.ExecuteScalar() is not null;
        });
    }

    /// <summary>True if any module has been configured (presence marker exists).</summary>
    public bool HasAny()
    {
        return _store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM module_config_present LIMIT 1;";
            return command.ExecuteScalar() is not null;
        });
    }

    /// <summary>
    /// Replaces a module's entire config (delete-then-insert) in one transaction, matching the
    /// file version's whole-file overwrite semantics. Empty/whitespace keys are skipped.
    /// </summary>
    public void SaveModule(string moduleId, IReadOnlyDictionary<string, string> values)
    {
        _store.Write((connection, transaction) =>
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM module_config WHERE module_id = $id;";
                delete.Parameters.AddWithValue("$id", moduleId);
                delete.ExecuteNonQuery();
            }

            MarkPresent(connection, transaction, moduleId);
            InsertValues(connection, transaction, moduleId, values);
        });
    }

    /// <summary>
    /// One-time import of a module's config only if it has no rows yet (DB wins). Returns true
    /// if rows were written.
    /// </summary>
    public bool ImportModuleIfMissing(string moduleId, IReadOnlyDictionary<string, string> values)
    {
        return _store.Write((connection, transaction) =>
        {
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT 1 FROM module_config_present WHERE module_id = $id LIMIT 1;";
                check.Parameters.AddWithValue("$id", moduleId);
                if (check.ExecuteScalar() is not null)
                    return false;
            }

            MarkPresent(connection, transaction, moduleId);
            InsertValues(connection, transaction, moduleId, values);
            return true;
        });
    }

    /// <summary>
    /// One-time key remap within a single module: moves a stranded <paramref name="fromKey"/>
    /// value to <paramref name="toKey"/> when the latter is absent/empty, then removes the
    /// stale <paramref name="fromKey"/> row. If <paramref name="toKey"/> already has a non-empty
    /// value it wins (no overwrite) and the dead <paramref name="fromKey"/> row is simply deleted.
    /// Returns true only if a row was actually written or deleted (so a no-op bumps no change
    /// token). Used by the Graph credential key rename (DelineaSecretId -> GraphDelineaSecretId);
    /// see docs/GraphSecretKeyMigration-Plan.md.
    /// </summary>
    public bool RemapKey(string moduleId, string fromKey, string toKey)
    {
        // Read first so a module with nothing to remap never opens a write transaction.
        var current = GetModule(moduleId);
        var hasFrom = current.TryGetValue(fromKey, out var fromValue) && !string.IsNullOrWhiteSpace(fromValue);
        var hasTo = current.TryGetValue(toKey, out var toValue) && !string.IsNullOrWhiteSpace(toValue);
        var fromRowPresent = current.ContainsKey(fromKey);

        if (!fromRowPresent)
            return false; // nothing stranded under the old key

        var copyValue = !hasTo && hasFrom; // move only when the new key has no usable value

        return _store.Write((connection, transaction) =>
        {
            if (copyValue)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO module_config (module_id, config_key, config_value, updated_at) " +
                    "VALUES ($id, $key, $value, $ts) " +
                    "ON CONFLICT(module_id, config_key) DO UPDATE SET config_value = excluded.config_value, updated_at = excluded.updated_at;";
                insert.Parameters.AddWithValue("$id", moduleId);
                insert.Parameters.AddWithValue("$key", toKey);
                insert.Parameters.AddWithValue("$value", fromValue);
                insert.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
                insert.ExecuteNonQuery();
            }

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM module_config WHERE module_id = $id AND config_key = $key;";
            delete.Parameters.AddWithValue("$id", moduleId);
            delete.Parameters.AddWithValue("$key", fromKey);
            delete.ExecuteNonQuery();

            return true;
        });
    }

    private static void MarkPresent(SqliteConnection connection, SqliteTransaction transaction, string moduleId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO module_config_present (module_id) VALUES ($id) ON CONFLICT(module_id) DO NOTHING;";
        command.Parameters.AddWithValue("$id", moduleId);
        command.ExecuteNonQuery();
    }

    private static void InsertValues(SqliteConnection connection, SqliteTransaction transaction,
        string moduleId, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO module_config (module_id, config_key, config_value, updated_at) " +
                "VALUES ($id, $key, $value, $ts) " +
                "ON CONFLICT(module_id, config_key) DO UPDATE SET config_value = excluded.config_value, updated_at = excluded.updated_at;";
            insert.Parameters.AddWithValue("$id", moduleId);
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
            insert.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            insert.ExecuteNonQuery();
        }
    }

    private static Dictionary<string, string> ReadModule(SqliteConnection connection, string moduleId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT config_key, config_value FROM module_config WHERE module_id = $id;";
        command.Parameters.AddWithValue("$id", moduleId);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            result[key] = value;
        }
        return result;
    }
}
