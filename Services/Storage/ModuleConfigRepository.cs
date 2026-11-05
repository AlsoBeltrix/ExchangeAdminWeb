using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Repository for the per-module key/value <c>module_config</c> table (module_id, config_key,
/// config_value). Backs <see cref="ModuleConfigService"/>, which ~17 feature modules funnel
/// their config through. Sits on <see cref="IConfigStore"/>.
///
/// Corruption in the file world meant an unparseable JSON file; with per-row storage that
/// failure class is gone - a "corrupt" probe now means the table cannot be opened/read at all
/// (a DB-integrity failure), surfaced by <see cref="TryReadModule"/> returning false.
/// </summary>
public sealed class ModuleConfigRepository
{
    private readonly IConfigStore _store;

    public ModuleConfigRepository(IConfigStore store) => _store = store;

    /// <summary>A change watcher over this repository's store, for the caching reader that sits on it.</summary>
    public ConfigChangeWatcher CreateChangeWatcher(ILogger? logger = null) => new(_store, logger);

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
    /// True if the module has been configured - i.e. saved or imported at least once, even if
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
    /// <remarks>
    /// Internal, and reached in production only from <see cref="ModuleConfigService.SaveModuleConfig"/>, itself an
    /// internal test fixture (aud-a26n): it compares nothing, and the settings saves write the keys they changed,
    /// decided under the lock (<see cref="UpdateModuleIf"/>). WholeStoreWriterTripwireTests refuses another caller.
    /// </remarks>
    internal void SaveModule(string moduleId, IReadOnlyDictionary<string, string> values)
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
    /// Upserts only the given keys of a module's config and marks it present, in one write
    /// transaction, and returns the module's values as read under that write's lock and as left by
    /// it (aud-a28j). Keys not named are not touched. Empty/whitespace keys are skipped.
    /// </summary>
    public ModuleConfigUpdate UpdateModule(string moduleId, IReadOnlyDictionary<string, string> values)
    {
        return _store.Write((connection, transaction) =>
        {
            var before = ReadModule(connection, moduleId, transaction);
            MarkPresent(connection, transaction, moduleId);
            InsertValues(connection, transaction, moduleId, values);
            PruneVerifyIgnores(connection, transaction, values.Keys);
            return new ModuleConfigUpdate(before, ReadModule(connection, moduleId, transaction));
        });
    }

    /// <summary>
    /// Upserts some keys of a module's config in ONE write transaction (aud-a28n): <paramref name="decide"/>
    /// is shown the module's values as they are under the write lock - null when they cannot be read - and
    /// returns the keys to upsert, or null to store nothing, in which case the transaction is rolled back
    /// and nothing, not even the change token, is written. Keys it does not return are not touched.
    /// </summary>
    /// <param name="cancellationToken">kd-58 B2. The save's own token, handed to the store's write (B1): a cancel before the commit leaves nothing written. None, the default, writes as before.</param>
    public ModuleConfigConditionalUpdate UpdateModuleIf(
        string moduleId, Func<Dictionary<string, string>?, IReadOnlyDictionary<string, string>?> decide, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decide);
        try
        {
            return _store.Write((connection, transaction) =>
            {
                // A read that throws is handed to decide as null, so the caller refuses on it rather than the
                // exception reaching the page as a failed write when nothing was written.
                Dictionary<string, string>? before;
                try
                {
                    before = ReadModule(connection, moduleId, transaction);
                }
                catch
                {
                    before = null;
                }

                var values = decide(before) ?? throw new NothingWritten(before);
                MarkPresent(connection, transaction, moduleId);
                InsertValues(connection, transaction, moduleId, values);
                PruneVerifyIgnores(connection, transaction, values.Keys);
                return new ModuleConfigConditionalUpdate(true, before, ReadModule(connection, moduleId, transaction));
            }, cancellationToken);
        }
        catch (NothingWritten nothing)
        {
            return new ModuleConfigConditionalUpdate(false, nothing.Before, null);
        }
    }

    // Thrown out of the write when decide stores nothing, because IConfigStore.Write commits - and bumps the
    // change token both instances watch - whenever its callback returns. An exception is its documented
    // rollback. Caught in UpdateModuleIf; it never leaves this class.
    private sealed class NothingWritten(Dictionary<string, string>? before) : Exception
    {
        public Dictionary<string, string>? Before { get; } = before;
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
    /// Returns true only if a row was actually written or deleted. Used by the Graph credential
    /// key rename (DelineaSecretId -> GraphDelineaSecretId); see docs/GraphSecretKeyMigration-Plan.md.
    /// </summary>
    /// <remarks>
    /// <para><b>Decided under the write lock, from the rows as they are then (aud-a33c).</b> Dev and
    /// prod share this database. This used to decide whether to copy from a read made before its
    /// write transaction opened and then upsert unconditionally, so a value the other instance
    /// saved in between - an administrator setting the Graph secret, or rotating it after that
    /// instance's own remap - was overwritten with the stale old one. The store's write
    /// transaction takes the lock at BEGIN, so the read inside it cannot be overtaken before the
    /// write; that is what makes the upsert conditional on the destination the decision saw.
    /// There is deliberately no second, SQL-level condition: it would mask this one.</para>
    ///
    /// <para><b>No-op cost.</b> A module with nothing stranded - every module on every start once
    /// the rename is repaired - is answered by the unlocked read and opens no write transaction,
    /// so it neither waits on another writer nor bumps the change token. If the old row is gone
    /// only by the time the lock is held, this returns false and the transaction carries nothing
    /// but the token bump every store write carries.</para>
    /// </remarks>
    public bool RemapKey(string moduleId, string fromKey, string toKey)
    {
        // Fast path only: nothing read here reaches the write below.
        var unlocked = GetModule(moduleId);
        if (!unlocked.ContainsKey(fromKey))
            return false; // nothing stranded under the old key

        return _store.Write((connection, transaction) =>
        {
            // Re-read through the transaction: the decision is made from these rows, never from
            // the unlocked read above, which the other instance may have overtaken.
            var current = ReadModule(connection, moduleId, transaction);
            if (!current.TryGetValue(fromKey, out var fromValue))
                return false; // another writer moved or removed it after the unlocked read

            var hasFrom = !string.IsNullOrWhiteSpace(fromValue);
            var hasTo = current.TryGetValue(toKey, out var toValue) && !string.IsNullOrWhiteSpace(toValue);

            if (hasFrom && !hasTo) // move only when the new key has no usable value
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

    // ----- docs/DirectoryObjectVerify-Plan.md B.3, C.2 (slice V4): Verify of the settings that hold a directory object -----

    /// <summary>
    /// What a Verify press of <paramref name="moduleId"/>'s config pane reads, in ONE read transaction: the values of its
    /// verified settings (<see cref="ModuleConfigVerifyAdapter.SettingsOf"/>) and the raw ignore list. Unreadable when the
    /// module's config cannot be read.
    /// </summary>
    internal ModuleConfigVerifySnapshot ReadVerifySnapshot(string moduleId)
    {
        try
        {
            return _store.ReadInTransaction((connection, transaction) => new ModuleConfigVerifySnapshot(
                true,
                VerifiedRows(moduleId, ReadModule(connection, moduleId, transaction)).ToDictionary(r => r.RowKey, r => r.Value, StringComparer.OrdinalIgnoreCase),
                AppSettingRepository.ReadValue(connection, transaction, VerifyIgnoreList.SettingKey)));
        }
        catch
        {
            return ModuleConfigVerifySnapshot.Unreadable;
        }
    }

    /// <summary>
    /// Remove (C.2, R3-1): <paramref name="entry"/> taken out of setting <paramref name="key"/> by the module's own parsing -
    /// one comma or semicolon entry, the others kept as stored; a single value cleared - AND every ignore that no longer names
    /// an entry, in one transaction, refused unless the pane's verified values are still the version Verify read and the entry
    /// is still there. A list setting whose empty value widens the module (<see cref="ConfigVerifySetting.WarnBeforeLastEntry"/>)
    /// loses its last entry only with <paramref name="lastEntryConfirmed"/>.
    /// </summary>
    internal ModuleGroupVerifyWriteOutcome RemoveVerifyEntry(
        string moduleId, string key, string entry, bool lastEntryConfirmed, string expectedVersion, string? expectedIgnores) =>
        WriteVerify(moduleId, expectedVersion, casIgnores: true, expectedIgnores, null, (connection, transaction, values) =>
        {
            if (VerifiedSetting(moduleId, key) is not { } setting)
                return VerifyWriteStatus.RowChanged;

            var raw = values.GetValueOrDefault(key);
            if (!setting.EntriesOf(raw).Contains(entry.Trim(), StringComparer.OrdinalIgnoreCase))
                return VerifyWriteStatus.RowChanged;

            var updated = setting.WithoutEntry(raw, entry);
            if (setting.WarnBeforeLastEntry && setting.EntriesOf(updated).Count == 0 && !lastEntryConfirmed)
                return VerifyWriteStatus.RowChanged;

            MarkPresent(connection, transaction, moduleId);
            InsertValues(connection, transaction, moduleId, new Dictionary<string, string> { [setting.Key] = updated });
            return null;
        });

    /// <summary>Ignore (C.2, display-only): records the finding after checking, under the lock, that the entry is still stored.</summary>
    internal ModuleGroupVerifyWriteOutcome AddVerifyIgnore(
        string moduleId, string key, string entry, VerifyIgnoreEntry ignore, string expectedVersion, string? expectedIgnores) =>
        WriteVerify(moduleId, expectedVersion, casIgnores: true, expectedIgnores, ignore, (_, _, values) =>
            VerifiedSetting(moduleId, key) is { } setting
            && setting.EntriesOf(values.GetValueOrDefault(key)).Contains(entry.Trim(), StringComparer.OrdinalIgnoreCase)
                ? null
                : VerifyWriteStatus.RowChanged);

    /// <summary>The stored <c>verify_last</c> map, raw (plan C.1, display-only).</summary>
    internal string? ReadVerifyLast() => _store.Read(connection => AppSettingRepository.ReadValue(connection, null, VerifyIgnoreList.LastVerifiedKey));

    /// <summary>Writes <c>verify_last</c> as <paramref name="update"/> makes it from the stored value: display-only (plan C.1).</summary>
    internal void UpdateVerifyLast(Func<string?, string> update) =>
        _store.Write((connection, transaction) =>
        {
            var raw = AppSettingRepository.ReadValue(connection, transaction, VerifyIgnoreList.LastVerifiedKey);
            AppSettingRepository.ReplaceIfEqual(connection, transaction, VerifyIgnoreList.LastVerifiedKey, raw, update(raw));
        });

    private ModuleGroupVerifyWriteOutcome WriteVerify(
        string moduleId, string expectedVersion, bool casIgnores, string? expectedIgnores, VerifyIgnoreEntry? addIgnore,
        Func<SqliteConnection, SqliteTransaction, Dictionary<string, string>, VerifyWriteStatus?> decide) =>
        ModuleGroupVerifyStore.Write(
            _store, VerifyIgnoreList.ConfigStore,
            (connection, transaction) => (true, VerifiedRows(moduleId, ReadModule(connection, moduleId, transaction))),
            AllVerifiedEntries,
            expectedVersion, casIgnores, expectedIgnores, addIgnore,
            (connection, transaction, _) => decide(connection, transaction, ReadModule(connection, moduleId, transaction)),
            EntryIdentity);

    private static ConfigVerifySetting? VerifiedSetting(string moduleId, string key) =>
        ModuleConfigVerifyAdapter.SettingsOf(moduleId).FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    // What an ignore of a text setting names: one entry, trimmed.
    private static string EntryIdentity(string entry) => entry.Trim();

    // A pane's verified values as rows, for the version: each verified setting the module stores, with its value exactly as stored.
    internal static IReadOnlyList<GroupGrantRow> VerifiedRows(string moduleId, IReadOnlyDictionary<string, string> values) =>
        ModuleConfigVerifyAdapter.SettingsOf(moduleId)
            .Where(s => values.ContainsKey(s.Key))
            .Select(s => new GroupGrantRow(s.Key, values[s.Key] ?? ""))
            .ToList();

    // Every entry of every verified setting of every module, as the ignore list names them: what the prune keeps ignores of.
    private static IReadOnlyList<GroupGrantRow> AllVerifiedEntries(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<GroupGrantRow>();
        foreach (var byModule in ModuleConfigVerifyAdapter.Settings.GroupBy(s => s.ModuleId, StringComparer.OrdinalIgnoreCase))
        {
            var values = ReadModule(connection, byModule.Key, transaction);
            foreach (var setting in byModule)
                rows.AddRange(setting.EntriesOf(values.GetValueOrDefault(setting.Key)).Select(e => new GroupGrantRow(setting.Key, e)));
        }
        return rows;
    }

    // B.3, the ordinary save's cleanup: a save that wrote a verified setting drops, in its own transaction, the Verify ignores of
    // entries no setting holds any more. A save of no verified setting leaves the list alone.
    private static void PruneVerifyIgnores(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<string> writtenKeys)
    {
        if (!writtenKeys.Any(k => ModuleConfigVerifyAdapter.SettingFor(k) is not null))
            return;
        ModuleGroupVerifyStore.Prune(connection, transaction, VerifyIgnoreList.ConfigStore, AllVerifiedEntries(connection, transaction), EntryIdentity);
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

    // The transaction is named when reading inside a write, as every command in these
    // repositories names it. Convention, not a provider requirement: with the assignment
    // removed the remap tests still pass (checked for aud-a33c).
    private static Dictionary<string, string> ReadModule(SqliteConnection connection, string moduleId,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

/// <summary>
/// A module's config values as read under the write lock before an update, and as the update left
/// them (<see cref="ModuleConfigRepository.UpdateModule"/>).
/// </summary>
public sealed record ModuleConfigUpdate(Dictionary<string, string> Before, Dictionary<string, string> After);

/// <summary>
/// What <see cref="ModuleConfigRepository.UpdateModuleIf"/> did: whether it wrote, the module's values as
/// read under the write lock (null when they could not be read) and, after a write, as read back.
/// </summary>
public sealed record ModuleConfigConditionalUpdate(
    bool Written, Dictionary<string, string>? Before, Dictionary<string, string>? After);

/// <summary>
/// What a Verify press of one module's config pane reads (docs/DirectoryObjectVerify-Plan.md B.1, B.3, slice V4), in ONE read
/// transaction: the values of its verified settings exactly as stored, and the raw ignore list.
/// </summary>
internal sealed record ModuleConfigVerifySnapshot(bool Readable, IReadOnlyDictionary<string, string> Values, string? IgnoresRaw)
{
    /// <summary>The version of <see cref="Values"/>, which every Verify write checks.</summary>
    public string Version => VersionOf(Values);

    /// <summary>A stable version of a pane's verified values: their order does not matter, their exact text does.</summary>
    public static string VersionOf(IReadOnlyDictionary<string, string> values) =>
        ModuleGroupVerifyStore.VersionOf(true, values.Select(kv => new GroupGrantRow(kv.Key, kv.Value)));

    public static ModuleConfigVerifySnapshot Unreadable { get; } =
        new(false, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null);
}
