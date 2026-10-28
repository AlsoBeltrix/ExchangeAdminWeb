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

    private static Dictionary<string, string[]> ReadAll(SqliteConnection connection) => ReadAll(connection, null);

    private static Dictionary<string, string[]> ReadAll(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static bool ReadConfigured(SqliteConnection connection) => ReadConfigured(connection, null);

    private static bool ReadConfigured(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
        var displayNames = ReadDisplayNames(connection, transaction);

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM section_access;";
            delete.ExecuteNonQuery();
        }

        foreach (var (alias, groups) in access)
            InsertGroups(connection, transaction, alias, groups, displayNames);
    }

    /// <summary>
    /// Replaces the groups of some aliases in ONE write transaction (aud-a28k): <paramref name="decide"/>
    /// is shown the table as it is under the write lock and returns the aliases to replace, each with its
    /// groups, or null to store nothing - in which case the transaction is rolled back and nothing, not
    /// even the change token, is written. Aliases it does not return are not touched. Display names are
    /// carried across, and the presence marker is set.
    /// </summary>
    /// <param name="cancellationToken">kd-58 B2. The save's own token, handed to the store's write (B1): a cancel before the commit leaves nothing written. None, the default, writes as before.</param>
    public SectionAccessReplaceOutcome ReplaceAliases(Func<SectionAccessSnapshot, IReadOnlyDictionary<string, string[]>?> decide, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decide);
        try
        {
            return _store.Write((connection, transaction) =>
            {
                var before = ReadSnapshot(connection, transaction);
                var replacement = decide(before) ?? throw new NothingWritten(before);
                var displayNames = ReadDisplayNames(connection, transaction);

                foreach (var (alias, groups) in replacement)
                {
                    if (string.IsNullOrWhiteSpace(alias) || groups == null)
                        continue;

                    using (var delete = connection.CreateCommand())
                    {
                        delete.Transaction = transaction;
                        delete.CommandText = "DELETE FROM section_access WHERE policy_alias = $alias;";
                        delete.Parameters.AddWithValue("$alias", alias);
                        delete.ExecuteNonQuery();
                    }

                    InsertGroups(connection, transaction, alias, groups, displayNames);
                }

                MarkPresent(connection, transaction);
                return new SectionAccessReplaceOutcome(true, before);
            }, cancellationToken);
        }
        catch (NothingWritten nothing)
        {
            return new SectionAccessReplaceOutcome(false, nothing.Before);
        }
    }

    private static SectionAccessSnapshot ReadSnapshot(SqliteConnection connection, SqliteTransaction transaction)
    {
        try
        {
            return new SectionAccessSnapshot(true, ReadAll(connection, transaction), ReadConfigured(connection, transaction));
        }
        catch
        {
            return SectionAccessSnapshot.Unreadable;
        }
    }

    // Thrown out of the write when decide stores nothing - a refusal, or nothing to save - because
    // IConfigStore.Write commits, and bumps the change token both instances watch, whenever its callback
    // returns. An exception is its documented rollback. Caught in ReplaceAliases; it never leaves this class.
    private sealed class NothingWritten(SectionAccessSnapshot before) : Exception
    {
        public SectionAccessSnapshot Before { get; } = before;
    }

    private static Dictionary<string, string> ReadDisplayNames(SqliteConnection connection, SqliteTransaction transaction)
    {
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT group_value, group_display_name FROM section_access " +
            "WHERE group_display_name IS NOT NULL AND group_display_name <> '';";
        using var reader = read.ExecuteReader();
        while (reader.Read())
            displayNames[reader.GetString(0)] = reader.GetString(1);
        return displayNames;
    }

    private static void InsertGroups(SqliteConnection connection, SqliteTransaction transaction,
        string alias, string[]? groups, IReadOnlyDictionary<string, string> displayNames)
    {
        if (string.IsNullOrWhiteSpace(alias) || groups == null)
            return;

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
    /// Converts each row the SID migration read to the SID it resolved, in ONE write transaction
    /// that re-checks every row under the write lock before changing it.
    /// </summary>
    /// <remarks>
    /// <para><b>Only rows still exactly as read are converted (aud-a33b).</b> The resolution that
    /// produced <paramref name="conversions"/> was a directory round trip made with no lock held,
    /// and dev and prod share this database, so the other instance can change grants in that
    /// window. This used to empty the table and refill it from the earlier read, which wrote back
    /// a grant an administrator had just removed and deleted any grant added meanwhile. Now each
    /// row is checked inside this transaction, which holds the write lock from BEGIN:</para>
    /// <list type="bullet">
    /// <item>(alias, value) still present: converted.</item>
    /// <item>gone, but (alias, SID) present: already in the state this conversion produces - the
    /// other instance's migration got there first, or an administrator re-picked the same group.
    /// Nothing to write and nothing to report.</item>
    /// <item>gone, and (alias, SID) absent: removed or changed by somebody after the read. Skipped
    /// and returned, never re-inserted - a grant that is absent now was removed by somebody, and
    /// re-adding it is the harm.</item>
    /// </list>
    /// <para>Rows the migration never read are never touched, so a grant written meanwhile
    /// survives; a name among them is converted on a later start. The comparison is existence of
    /// the key, in SQL under the columns' NOCASE collation: (policy_alias, group_value) is the
    /// whole of a grant, the display name beside it is cosmetic, and rowids are reassigned by
    /// every whole-table save, so they are no version. The change token is not used either - it
    /// moves on any config write and misses a writer that bypasses the store.</para>
    ///
    /// <para><b>Delete-then-insert per row, not UPDATE</b>, because two names can resolve to one
    /// SID (<c>IAM</c> and <c>ANALOG\IAM</c> are both in the prod store): updating the second in
    /// place would collide on the primary key, while inserting lets the ON CONFLICT clause merge
    /// them into one grant.</para>
    ///
    /// <para><b>The presence marker is re-asserted when anything converts.</b> Rows without the
    /// marker read as "never configured", which falls back to the permissive appsettings path
    /// rather than deny; a migration that rewrote rows and left that state would widen access.
    /// When nothing converts the transaction carries only the change-token bump every store write
    /// carries, as <see cref="ImportIfMissing"/> returning false already does.</para>
    /// </remarks>
    public SidConversionOutcome ConvertToSids(IReadOnlyList<SidConversion> conversions)
    {
        ArgumentNullException.ThrowIfNull(conversions);

        return _store.Write((connection, transaction) =>
        {
            var converted = 0;
            var alreadyConverted = 0;
            var skipped = new List<SidConversion>();

            foreach (var conversion in conversions)
            {
                if (string.IsNullOrWhiteSpace(conversion.PolicyAlias) || string.IsNullOrWhiteSpace(conversion.Sid))
                    continue;

                // Compared against the table as it is now, under the lock - never the earlier read.
                if (!RowExists(connection, transaction, conversion.PolicyAlias, conversion.Value))
                {
                    if (RowExists(connection, transaction, conversion.PolicyAlias, conversion.Sid))
                        alreadyConverted++;
                    else
                        skipped.Add(conversion);
                    continue;
                }

                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText =
                        "DELETE FROM section_access WHERE policy_alias = $alias AND group_value = $value;";
                    delete.Parameters.AddWithValue("$alias", conversion.PolicyAlias);
                    delete.Parameters.AddWithValue("$value", conversion.Value);
                    delete.ExecuteNonQuery();
                }

                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText =
                        "INSERT INTO section_access (policy_alias, group_value, group_display_name) " +
                        "VALUES ($alias, $group, $display) " +
                        "ON CONFLICT(policy_alias, group_value) DO UPDATE SET " +
                        "group_display_name = COALESCE(excluded.group_display_name, group_display_name);";
                    insert.Parameters.AddWithValue("$alias", conversion.PolicyAlias);
                    insert.Parameters.AddWithValue("$group", conversion.Sid);
                    insert.Parameters.AddWithValue("$display", (object?)conversion.DisplayName ?? DBNull.Value);
                    insert.ExecuteNonQuery();
                }

                converted++;
            }

            if (converted > 0)
                MarkPresent(connection, transaction);

            return new SidConversionOutcome(converted, alreadyConverted, skipped);
        });
    }

    private static bool RowExists(SqliteConnection connection, SqliteTransaction transaction, string alias, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT 1 FROM section_access WHERE policy_alias = $alias AND group_value = $value LIMIT 1;";
        command.Parameters.AddWithValue("$alias", alias);
        command.Parameters.AddWithValue("$value", value);
        return command.ExecuteScalar() is not null;
    }

    private static void MarkPresent(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO section_access_present (marker) VALUES (1) ON CONFLICT(marker) DO NOTHING;";
        command.ExecuteNonQuery();
    }
}

/// <summary>One row the SID migration read, and the SID it resolved for it.</summary>
/// <param name="PolicyAlias">The section the row grants.</param>
/// <param name="Value">The stored value exactly as the migration read it.</param>
/// <param name="Sid">The SID to store instead.</param>
/// <param name="DisplayName">The friendly name to store beside it, for display only.</param>
public sealed record SidConversion(string PolicyAlias, string Value, string Sid, string? DisplayName);

/// <summary>What <see cref="SectionAccessRepository.ConvertToSids"/> did with its conversions.</summary>
/// <param name="Converted">Rows still as read, now stored as their SID.</param>
/// <param name="AlreadyConverted">Rows already in the converted state when the lock was held.</param>
/// <param name="Skipped">Rows changed or removed after the read, left exactly as they were.</param>
public sealed record SidConversionOutcome(int Converted, int AlreadyConverted, IReadOnlyList<SidConversion> Skipped);

/// <summary>The section-access rows and presence marker, read together (aud-a28k).</summary>
/// <param name="Readable">False when the read failed; <paramref name="Access"/> is then empty and means nothing.</param>
public sealed record SectionAccessSnapshot(bool Readable, Dictionary<string, string[]> Access, bool Configured)
{
    public static SectionAccessSnapshot Unreadable { get; } =
        new(false, new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase), false);
}

/// <summary>
/// What <see cref="SectionAccessRepository.ReplaceAliases"/> did: whether it wrote, and the table as it was
/// under the write lock.
/// </summary>
public sealed record SectionAccessReplaceOutcome(bool Replaced, SectionAccessSnapshot Before);
