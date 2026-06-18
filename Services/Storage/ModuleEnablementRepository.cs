namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Repository for the <c>module_enablement</c> table (module_id, enabled, updated_at). Backs
/// <see cref="ModuleEnablementService"/>. Stores only explicitly-toggled modules; an absent
/// module falls back to its descriptor default in the service (same as file-not-present).
/// Sits on <see cref="IConfigStore"/>.
/// </summary>
public sealed class ModuleEnablementRepository
{
    private readonly IConfigStore _store;

    public ModuleEnablementRepository(IConfigStore store) => _store = store;

    /// <summary>
    /// Attempts to read all enablement rows. Returns false if the read throws (the DB-integrity
    /// analogue of "modules-enabled.json exists but won't parse"); the service treats that as a
    /// corrupt store and fails closed (all disabled).
    /// </summary>
    public bool TryGetAll(out Dictionary<string, bool> state)
    {
        try
        {
            state = _store.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT module_id, enabled FROM module_enablement;";
                var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    map[reader.GetString(0)] = reader.GetInt64(1) != 0;
                return map;
            });
            return true;
        }
        catch
        {
            state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            return false;
        }
    }

    /// <summary>True if any enablement rows exist (the DB analogue of "the file exists").</summary>
    public bool HasAny()
    {
        return _store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM module_enablement LIMIT 1;";
            return command.ExecuteScalar() is not null;
        });
    }

    /// <summary>Replaces the entire enablement set (delete-then-insert) in one transaction.</summary>
    public void SaveAll(IReadOnlyDictionary<string, bool> state)
    {
        _store.Write((connection, transaction) =>
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM module_enablement;";
                delete.ExecuteNonQuery();
            }

            foreach (var (moduleId, enabled) in state)
            {
                if (string.IsNullOrWhiteSpace(moduleId))
                    continue;

                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO module_enablement (module_id, enabled, updated_at) VALUES ($id, $enabled, $ts) " +
                    "ON CONFLICT(module_id) DO UPDATE SET enabled = excluded.enabled, updated_at = excluded.updated_at;";
                insert.Parameters.AddWithValue("$id", moduleId);
                insert.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
                insert.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
                insert.ExecuteNonQuery();
            }
        });
    }

    /// <summary>
    /// Non-destructive startup seeding (SqliteConfigStore-Plan §3d): inserts a row for each
    /// supplied module ONLY if it has no row yet (INSERT ... ON CONFLICT DO NOTHING). Existing
    /// rows are never touched — the destructive startup write that caused the 2026-06-12 incident
    /// (flipping ExchangeOnline to false) stays forbidden. Returns the list of module IDs that
    /// were newly seeded (empty if all already had rows).
    /// </summary>
    public IReadOnlyList<string> SeedMissing(IReadOnlyDictionary<string, bool> defaults)
    {
        // Determine the missing set with a read first, so a no-op seed does NOT open a write
        // transaction (which would bump the change token and invalidate every reader's cache for
        // nothing). Only write when there is something to insert.
        var existing = _store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT module_id FROM module_enablement;";
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetString(0));
            return ids;
        });

        var toSeed = defaults
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && !existing.Contains(kvp.Key))
            .ToList();

        if (toSeed.Count == 0)
            return Array.Empty<string>();

        return _store.Write((connection, transaction) =>
        {
            var seeded = new List<string>();
            foreach (var (moduleId, enabled) in toSeed)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO module_enablement (module_id, enabled, updated_at) VALUES ($id, $enabled, $ts) " +
                    "ON CONFLICT(module_id) DO NOTHING;";
                insert.Parameters.AddWithValue("$id", moduleId);
                insert.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
                insert.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
                if (insert.ExecuteNonQuery() > 0)
                    seeded.Add(moduleId);
            }
            return (IReadOnlyList<string>)seeded;
        });
    }

    /// <summary>
    /// One-time import of a legacy enablement map, only if the table is empty (the DB-has-no-
    /// rows analogue of "no file yet"). Returns true if rows were written.
    /// </summary>
    public bool ImportIfMissing(IReadOnlyDictionary<string, bool> legacy)
    {
        return _store.Write((connection, transaction) =>
        {
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT 1 FROM module_enablement LIMIT 1;";
                if (check.ExecuteScalar() is not null)
                    return false;
            }

            var wrote = false;
            foreach (var (moduleId, enabled) in legacy)
            {
                if (string.IsNullOrWhiteSpace(moduleId))
                    continue;

                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO module_enablement (module_id, enabled, updated_at) VALUES ($id, $enabled, $ts) " +
                    "ON CONFLICT(module_id) DO NOTHING;";
                insert.Parameters.AddWithValue("$id", moduleId);
                insert.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
                insert.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
                insert.ExecuteNonQuery();
                wrote = true;
            }
            return wrote;
        });
    }
}
