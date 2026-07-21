using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Repository for the row-per-group <c>section_access</c> table (policy_alias, group_value) plus
/// a single <c>section_access_present</c> marker. Backs <see cref="SectionAccessService"/> - the
/// authorization store. Row-per-group means a single bad value can never take down a whole
/// alias. The presence marker preserves the file-world distinction between "configured but
/// empty" (deny all) and "never configured" (fall back), exactly like module-config presence.
/// Sits on <see cref="IConfigStore"/>.
/// </summary>
public sealed class SectionAccessRepository
{
    private readonly IConfigStore _store;

    public SectionAccessRepository(IConfigStore store) => _store = store;

    /// <summary>
    /// Attempts to read all section access. Returns false if the read throws (DB-integrity
    /// failure - the analogue of an unreadable fragment); the service then fails closed.
    /// </summary>
    public bool TryGetAll(out Dictionary<string, string[]> access)
    {
        try
        {
            access = _store.Read(ReadAll);
            return true;
        }
        catch
        {
            access = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            return false;
        }
    }

    private static Dictionary<string, string[]> ReadAll(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT policy_alias, group_value FROM section_access;";
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var alias = reader.GetString(0);
            var group = reader.GetString(1);
            if (!map.TryGetValue(alias, out var list))
                map[alias] = list = new List<string>();
            list.Add(group);
        }
        return map.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True if section access has been configured (the presence marker is set).</summary>
    public bool IsConfigured()
    {
        return _store.Read(connection => ReadConfigured(connection));
    }

    /// <summary>
    /// Reads both the access map AND the configured flag in a single guarded operation. Returns
    /// false if EITHER read fails (a damaged/partial schema - e.g. a missing marker table while
    /// section_access is still readable). Callers in the authorization path use this so a partial
    /// corruption fails closed rather than throwing through. Both out-params are safe-defaulted
    /// on failure.
    /// </summary>
    public bool TryRead(out Dictionary<string, string[]> access, out bool configured)
    {
        try
        {
            (access, configured) = _store.Read(connection =>
            {
                var data = ReadAll(connection);
                var present = ReadConfigured(connection);
                return (data, present);
            });
            return true;
        }
        catch
        {
            access = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            configured = false;
            return false;
        }
    }

    private static bool ReadConfigured(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM section_access_present LIMIT 1;";
        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Replaces the entire section-access set (delete-then-insert) and sets the presence marker,
    /// in one transaction - matching the file version's whole-fragment overwrite.
    /// </summary>
    public void SaveAll(IReadOnlyDictionary<string, string[]> access)
    {
        _store.Write((connection, transaction) =>
        {
            ClearAndInsert(connection, transaction, access);
            MarkPresent(connection, transaction);
        });
    }

    /// <summary>
    /// One-time import of a legacy section-access map, only if not yet configured (presence
    /// marker absent). Marks present even for an empty map (an explicitly-empty fragment still
    /// counted as configured). Returns true if it imported.
    /// </summary>
    public bool ImportIfMissing(IReadOnlyDictionary<string, string[]> legacy)
    {
        return _store.Write((connection, transaction) =>
        {
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT 1 FROM section_access_present LIMIT 1;";
                if (check.ExecuteScalar() is not null)
                    return false;
            }

            ClearAndInsert(connection, transaction, legacy);
            MarkPresent(connection, transaction);
            return true;
        });
    }

    private static void ClearAndInsert(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyDictionary<string, string[]> access)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM section_access;";
            delete.ExecuteNonQuery();
        }

        foreach (var (alias, groups) in access)
        {
            if (string.IsNullOrWhiteSpace(alias) || groups == null)
                continue;

            foreach (var group in groups.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO section_access (policy_alias, group_value) VALUES ($alias, $group) " +
                    "ON CONFLICT(policy_alias, group_value) DO NOTHING;";
                insert.Parameters.AddWithValue("$alias", alias);
                insert.Parameters.AddWithValue("$group", group);
                insert.ExecuteNonQuery();
            }
        }
    }

    private static void MarkPresent(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO section_access_present (marker) VALUES (1) ON CONFLICT(marker) DO NOTHING;";
        command.ExecuteNonQuery();
    }
}
