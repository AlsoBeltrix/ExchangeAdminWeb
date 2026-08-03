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
        // Carry display names across the delete-and-reinsert. Without this, every admin save wipes
        // the names of groups it did not touch, and the page falls back to showing raw SIDs until
        // the next migration run - which, being idempotent, would never run again.
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                "SELECT group_value, group_display_name FROM section_access " +
                "WHERE group_display_name IS NOT NULL AND group_display_name <> '';";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                displayNames[reader.GetString(0)] = reader.GetString(1);
        }

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
                    "INSERT INTO section_access (policy_alias, group_value, group_display_name) " +
                    "VALUES ($alias, $group, $display) " +
                    "ON CONFLICT(policy_alias, group_value) DO NOTHING;";
                insert.Parameters.AddWithValue("$alias", alias);
                insert.Parameters.AddWithValue("$group", group);
                insert.Parameters.AddWithValue("$display",
                    displayNames.TryGetValue(group, out var name) ? name : (object)DBNull.Value);
                insert.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// Every row as (policy_alias, group_value), for the SID migration. Flat rather than grouped:
    /// the migration reports per-row failures and needs the alias beside each value.
    /// </summary>
    public IReadOnlyList<(string PolicyAlias, string GroupValue)> GetAllRows()
    {
        return _store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT policy_alias, group_value FROM section_access;";
            var rows = new List<(string, string)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1)));
            return (IReadOnlyList<(string, string)>)rows;
        });
    }

    /// <summary>
    /// The display name stored against each SID, for the admin page. Rows with no display name are
    /// omitted, so a caller falls back to showing the stored value itself.
    /// </summary>
    /// <remarks>
    /// Keyed by group value alone, not by (alias, value): the same group appears under several
    /// aliases and its name does not differ between them. Never consulted by an authorization
    /// path - see the migration plan's Design section.
    /// </remarks>
    public Dictionary<string, string> GetDisplayNames()
    {
        return _store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT group_value, group_display_name FROM section_access " +
                "WHERE group_display_name IS NOT NULL AND group_display_name <> '';";
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                map[reader.GetString(0)] = reader.GetString(1);
            return map;
        });
    }

    /// <summary>
    /// Stores friendly names against existing rows. Updates only; never inserts, so a name for a
    /// group that is not granted anything cannot create a grant.
    /// </summary>
    public void SaveDisplayNames(IReadOnlyDictionary<string, string> displayNames)
    {
        if (displayNames.Count == 0)
            return;

        _store.Write((connection, transaction) =>
        {
            foreach (var (groupValue, name) in displayNames)
            {
                if (string.IsNullOrWhiteSpace(groupValue) || string.IsNullOrWhiteSpace(name))
                    continue;

                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE section_access SET group_display_name = $name WHERE group_value = $group;";
                update.Parameters.AddWithValue("$name", name);
                update.Parameters.AddWithValue("$group", groupValue);
                update.ExecuteNonQuery();
            }
        });
    }

    /// <summary>
    /// Rewrites every row to its SID plus display name, in ONE transaction.
    /// </summary>
    /// <remarks>
    /// Delete-then-insert rather than per-row UPDATE, because two names can map to one SID
    /// (<c>IAM</c> and <c>ANALOG\IAM</c> are both in the prod store) and updating them in place
    /// would collide on the (policy_alias, group_value) primary key. Inserting into an emptied
    /// table lets the ON CONFLICT clause merge them, which is the correct outcome: one grant.
    ///
    /// The presence marker is deliberately re-asserted. Emptying the table without it would, for
    /// the instant before the insert, mean "never configured" - and if anything went wrong between
    /// the two, the store would fall back to the permissive appsettings path rather than deny.
    /// The transaction already prevents that being observable; the marker makes it true even in
    /// the state the transaction is hiding.
    /// </remarks>
    public void ReplaceAllWithSids(IReadOnlyList<(string PolicyAlias, string Sid, string? DisplayName)> rows)
    {
        _store.Write((connection, transaction) =>
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM section_access;";
                delete.ExecuteNonQuery();
            }

            foreach (var (alias, sid, display) in rows)
            {
                if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(sid))
                    continue;

                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO section_access (policy_alias, group_value, group_display_name) " +
                    "VALUES ($alias, $group, $display) " +
                    "ON CONFLICT(policy_alias, group_value) DO UPDATE SET " +
                    "group_display_name = COALESCE(excluded.group_display_name, group_display_name);";
                insert.Parameters.AddWithValue("$alias", alias);
                insert.Parameters.AddWithValue("$group", sid);
                insert.Parameters.AddWithValue("$display", (object?)display ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }

            MarkPresent(connection, transaction);
        });
    }

    private static void MarkPresent(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO section_access_present (marker) VALUES (1) ON CONFLICT(marker) DO NOTHING;";
        command.ExecuteNonQuery();
    }
}
