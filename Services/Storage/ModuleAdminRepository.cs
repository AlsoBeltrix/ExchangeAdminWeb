using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Repository for the row-per-group <c>module_admins</c> table (module_id, admin_group).
/// Row-per-group (vs. a JSON blob) means a single bad value can never take down a whole
/// module's admin list. Sits on <see cref="IConfigStore"/>.
/// </summary>
public sealed class ModuleAdminRepository
{
    private readonly IConfigStore _store;

    public ModuleAdminRepository(IConfigStore store) => _store = store;

    /// <summary>Returns the full map of module_id -> admin groups (only modules with rows).</summary>
    public Dictionary<string, string[]> GetAll() => _store.Read(connection => ReadAll(connection, null));

    /// <summary>
    /// Every module's grants on <paramref name="connection"/>, inside <paramref name="transaction"/> when one is given: also the
    /// read <c>db convert-identifiers</c> and <c>db identifier-status</c> make, which have no <see cref="IConfigStore"/> to read
    /// through (docs/ProtectedPrincipalIdentifiers-Plan.md K.3). Throws when the read fails.
    /// </summary>
    internal static Dictionary<string, string[]> ReadAll(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT module_id, admin_group FROM module_admins;";
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var group = reader.GetString(1);
            if (!map.TryGetValue(id, out var list))
                map[id] = list = new List<string>();
            list.Add(group);
        }
        return map.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// docs/ProtectedPrincipalIdentifiers-Plan.md K.3, B.6. <c>db convert-identifiers</c>' write of one grant: the name row
    /// replaced in place by the SID it resolved to, or - where the module already holds that SID - deleted, so the grant is
    /// held once, by SID. Inside the verb's one transaction, which has re-read every grant under the write lock first. Throws
    /// when the name row is not there.
    /// </summary>
    /// <remarks>
    /// In place is safe here and nowhere else in the plan: every build since aud-a1 ignores the name, and every build before
    /// it evaluates a grant with <c>WindowsPrincipal.IsInRole(string)</c>, which accepts a SID string as well as a name.
    /// </remarks>
    internal static void ReplaceNameWithSid(SqliteConnection connection, SqliteTransaction transaction, ModuleAdminSidConversion conversion)
    {
        if (!RowExists(connection, transaction, conversion.ModuleId, conversion.Name))
            throw new InvalidOperationException($"Module {conversion.ModuleId} holds no grant '{conversion.Name}'.");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RowExists(connection, transaction, conversion.ModuleId, conversion.Sid)
            ? "DELETE FROM module_admins WHERE module_id = $id AND admin_group = $name;"
            : "UPDATE module_admins SET admin_group = $sid WHERE module_id = $id AND admin_group = $name;";
        command.Parameters.AddWithValue("$id", conversion.ModuleId);
        command.Parameters.AddWithValue("$name", conversion.Name);
        command.Parameters.AddWithValue("$sid", conversion.Sid);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Replaces the admin groups for one module (delete-then-insert) inside one transaction,
    /// mirroring the file version's whole-list overwrite semantics.
    /// </summary>
    /// <remarks>
    /// Internal, and reached in production only from <see cref="ModuleAdminService.SetModuleAdmins"/>, itself an
    /// internal test fixture (aud-a26n): it compares nothing, and the admins save adds and removes the groups it
    /// changed, decided under the lock (<see cref="ApplyChanges"/>). WholeStoreWriterTripwireTests refuses another
    /// caller.
    /// </remarks>
    internal void SetForModule(string moduleId, string[] groups)
    {
        _store.Write((connection, transaction) =>
        {
            ReplaceModuleGroups(connection, transaction, moduleId, groups);
        });
    }

    /// <summary>
    /// Adds and removes some of a module's admin groups in ONE write transaction (aud-a28n), each decided
    /// against the module's rows as they are under the write lock: an added group already present, or a
    /// removed group already absent, is reported as already applied and not written. Groups not named are
    /// not touched. Nothing to write - or rows that cannot be read - rolls the transaction back, so nothing,
    /// not even the change token, is written.
    /// </summary>
    /// <remarks>
    /// The page used to replace the module's whole admin list with its snapshot, so a group added elsewhere
    /// since it loaded was dropped and one removed elsewhere came back. Each named group is decided on its own
    /// row under the write lock instead, and no other group is touched. There is no conflict to refuse: a
    /// membership has two states, the one the page loaded and the one it wants, so the row is always one or the
    /// other - unchanged, and written, or already as wanted, and reported (aud-a28k's enablement reasoning, and
    /// the aud-a26k rule).
    /// </remarks>
    /// <param name="cancellationToken">kd-58 B2. The save's own token, handed to the store's write (B1): a cancel before the commit leaves nothing written. None, the default, writes as before.</param>
    public ModuleAdminChangeOutcome ApplyChanges(string moduleId, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        try
        {
            return _store.Write((connection, transaction) =>
            {
                string[] before;
                try
                {
                    before = ReadModuleGroups(connection, transaction, moduleId);
                }
                catch
                {
                    throw new NothingWritten(null, []);
                }

                var present = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase);
                var alreadyApplied = new List<string>();
                var inserts = new List<string>();
                var deletes = new List<string>();
                foreach (var group in add.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase))
                    (present.Contains(group) ? alreadyApplied : inserts).Add(group);
                foreach (var group in remove.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase))
                    (present.Contains(group) ? deletes : alreadyApplied).Add(group);

                if (inserts.Count == 0 && deletes.Count == 0)
                    throw new NothingWritten(before, alreadyApplied);

                foreach (var group in deletes)
                {
                    using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM module_admins WHERE module_id = $id AND admin_group = $group;";
                    delete.Parameters.AddWithValue("$id", moduleId);
                    delete.Parameters.AddWithValue("$group", group);
                    delete.ExecuteNonQuery();
                }

                InsertGroups(connection, transaction, moduleId, inserts.ToArray());

                // docs/DirectoryObjectVerify-Plan.md B.3 (slice V3): the Module Admins pane's ordinary Remove + Save lands here,
                // so a removed grant's Verify ignores go in this same transaction.
                if (deletes.Count > 0)
                    ModuleGroupVerifyStore.Prune(connection, transaction, VerifyIgnoreList.ModuleAdminsStore, ReadAllRows(connection, transaction));
                return new ModuleAdminChangeOutcome(
                    true, true, before, ReadModuleGroups(connection, transaction, moduleId), alreadyApplied);
            }, cancellationToken);
        }
        catch (NothingWritten nothing)
        {
            return nothing.Before is null
                ? new ModuleAdminChangeOutcome(false, false, [], [], [])
                : new ModuleAdminChangeOutcome(true, false, nothing.Before, nothing.Before, nothing.AlreadyApplied);
        }
    }

    // Thrown out of the write when there is nothing to write, or the rows cannot be read (a null Before):
    // IConfigStore.Write commits, and bumps the change token both instances watch, whenever its callback
    // returns, and an exception is its documented rollback. Caught in ApplyChanges; it never leaves this class.
    private sealed class NothingWritten(string[]? before, IReadOnlyList<string> alreadyApplied) : Exception
    {
        public string[]? Before { get; } = before;

        public IReadOnlyList<string> AlreadyApplied { get; } = alreadyApplied;
    }

    // ----- docs/DirectoryObjectVerify-Plan.md B.3, C.2 (slice V3): the Module Admins pane's Verify -----

    /// <summary>Every module's grants, as rows (row key = module id), inside <paramref name="transaction"/>.</summary>
    internal static IReadOnlyList<GroupGrantRow> ReadAllRows(SqliteConnection connection, SqliteTransaction? transaction) =>
        ReadAll(connection, transaction)
            .SelectMany(kv => kv.Value.Select(v => new GroupGrantRow(kv.Key, v)))
            .ToList();

    // The module's grants. module_admins has no presence marker: it is always "configured".
    private static (bool Configured, IReadOnlyList<GroupGrantRow> Rows) ReadScope(SqliteConnection connection, SqliteTransaction? transaction, string moduleId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT admin_group FROM module_admins WHERE module_id = $id;";
        command.Parameters.AddWithValue("$id", moduleId);
        return (true, ReadGroups(command).Select(g => new GroupGrantRow(moduleId, g)).ToList());
    }

    /// <summary>
    /// What a Verify press of a module's Module Admins tab reads (plan B.1, B.3): the module's grants, the section-access display
    /// names the pane borrows, and the raw ignore list, in ONE read transaction. Unreadable when any read fails.
    /// </summary>
    internal ModuleGroupVerifySnapshot ReadVerifySnapshot(string moduleId)
    {
        try
        {
            return _store.ReadInTransaction((connection, transaction) =>
            {
                var (configured, rows) = ReadScope(connection, transaction, moduleId);
                return new ModuleGroupVerifySnapshot(true, configured, rows, SectionAccessRepository.ReadDisplayNames(connection, transaction),
                    AppSettingRepository.ReadValue(connection, transaction, VerifyIgnoreList.SettingKey));
            });
        }
        catch
        {
            return ModuleGroupVerifySnapshot.Unreadable;
        }
    }

    private ModuleGroupVerifyWriteOutcome WriteVerify(
        string moduleId, string expectedVersion, bool casIgnores, string? expectedIgnores, VerifyIgnoreEntry? addIgnore,
        Func<SqliteConnection, SqliteTransaction, VerifyWriteStatus?> decide) =>
        ModuleGroupVerifyStore.Write(
            _store, VerifyIgnoreList.ModuleAdminsStore,
            (connection, transaction) => ReadScope(connection, transaction, moduleId),
            (connection, transaction) => ReadAllRows(connection, transaction),
            expectedVersion, casIgnores, expectedIgnores, addIgnore,
            (connection, transaction, _) => decide(connection, transaction));

    /// <summary>Remove (C.2): deletes the one grant AND every Verify ignore that names it, in one transaction.</summary>
    internal ModuleGroupVerifyWriteOutcome RemoveWithIgnores(string moduleId, string group, string expectedVersion, string? expectedIgnores) =>
        WriteVerify(moduleId, expectedVersion, casIgnores: true, expectedIgnores, null, (connection, transaction) =>
        {
            if (!RowExists(connection, transaction, moduleId, group))
                return VerifyWriteStatus.RowChanged;

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM module_admins WHERE module_id = $id AND admin_group = $group;";
            delete.Parameters.AddWithValue("$id", moduleId);
            delete.Parameters.AddWithValue("$group", group);
            delete.ExecuteNonQuery();
            return null;
        });

    /// <summary>
    /// Rename (C.2, LabelDrift): module admins borrow section-access display names, so the name stored for the grant's SID in
    /// <c>section_access</c> becomes <paramref name="newLabel"/>, through <see cref="SectionAccessRepository.SaveDisplayNames(SqliteConnection, SqliteTransaction, IReadOnlyDictionary{string, string})"/>
    /// for that SID only. Refused unless the grant is still there and the stored name is still <paramref name="expectedLabel"/>.
    /// </summary>
    internal ModuleGroupVerifyWriteOutcome RenameDisplayName(
        string moduleId, string group, string expectedLabel, string newLabel, string expectedVersion) =>
        WriteVerify(moduleId, expectedVersion, casIgnores: false, null, null, (connection, transaction) =>
        {
            if (!RowExists(connection, transaction, moduleId, group)
                || !SectionAccessRepository.ReadDisplayNames(connection, transaction).TryGetValue(group, out var stored)
                || !string.Equals(stored.Trim(), expectedLabel.Trim(), StringComparison.OrdinalIgnoreCase))
                return VerifyWriteStatus.RowChanged;

            SectionAccessRepository.SaveDisplayNames(connection, transaction,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [group] = newLabel });
            return null;
        });

    /// <summary>
    /// Convert (C.2, NotConverted convertible): the one name grant becomes its group's SID through
    /// <see cref="ConfigDbIdentifiers.ConvertModuleAdminGrant"/> - the primitive <c>db convert-identifiers</c> writes with.
    /// </summary>
    internal ModuleGroupVerifyWriteOutcome ConvertOne(ModuleAdminSidConversion conversion, string expectedVersion) =>
        WriteVerify(conversion.ModuleId, expectedVersion, casIgnores: false, null, null, (connection, transaction) =>
        {
            if (!RowExists(connection, transaction, conversion.ModuleId, conversion.Name))
                return VerifyWriteStatus.RowChanged;

            ConfigDbIdentifiers.ConvertModuleAdminGrant(connection, transaction, conversion);
            return null;
        });

    /// <summary>Ignore (B.3, R2): adds <paramref name="entry"/> after confirming the grant is still stored as read. Changes no grant.</summary>
    internal ModuleGroupVerifyWriteOutcome AddVerifyIgnore(
        string moduleId, string group, VerifyIgnoreEntry entry, string expectedVersion, string? expectedIgnores) =>
        WriteVerify(moduleId, expectedVersion, casIgnores: true, expectedIgnores, entry, (connection, transaction) =>
            RowExists(connection, transaction, moduleId, group) ? null : VerifyWriteStatus.RowChanged);

    private static bool RowExists(SqliteConnection connection, SqliteTransaction transaction, string moduleId, string group)
    {
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT 1 FROM module_admins WHERE module_id = $id AND admin_group = $group LIMIT 1;";
        check.Parameters.AddWithValue("$id", moduleId);
        check.Parameters.AddWithValue("$group", group);
        return check.ExecuteScalar() is not null;
    }

    private static string[] ReadModuleGroups(SqliteConnection connection, SqliteTransaction transaction, string moduleId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT admin_group FROM module_admins WHERE module_id = $id;";
        command.Parameters.AddWithValue("$id", moduleId);
        return ReadGroups(command);
    }

    /// <summary>
    /// One-time import of a legacy module_id -> groups map. Only writes modules that have no
    /// rows yet, so existing DB state always wins. Returns the number of modules imported.
    /// </summary>
    public int ImportIfMissing(IReadOnlyDictionary<string, string[]> legacy)
    {
        return _store.Write((connection, transaction) =>
        {
            var imported = 0;
            foreach (var (moduleId, groups) in legacy)
            {
                using var check = connection.CreateCommand();
                check.Transaction = transaction;
                check.CommandText = "SELECT 1 FROM module_admins WHERE module_id = $id LIMIT 1;";
                check.Parameters.AddWithValue("$id", moduleId);
                if (check.ExecuteScalar() is not null)
                    continue;

                InsertGroups(connection, transaction, moduleId, groups);
                imported++;
            }
            return imported;
        });
    }

    private static void ReplaceModuleGroups(SqliteConnection connection, SqliteTransaction transaction,
        string moduleId, string[] groups)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM module_admins WHERE module_id = $id;";
            delete.Parameters.AddWithValue("$id", moduleId);
            delete.ExecuteNonQuery();
        }

        InsertGroups(connection, transaction, moduleId, groups);
    }

    private static void InsertGroups(SqliteConnection connection, SqliteTransaction transaction,
        string moduleId, string[] groups)
    {
        foreach (var group in groups.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO module_admins (module_id, admin_group) VALUES ($id, $group) " +
                "ON CONFLICT(module_id, admin_group) DO NOTHING;";
            insert.Parameters.AddWithValue("$id", moduleId);
            insert.Parameters.AddWithValue("$group", group);
            insert.ExecuteNonQuery();
        }
    }

    private static string[] ReadGroups(SqliteCommand command)
    {
        var groups = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            groups.Add(reader.GetString(0));
        return groups.ToArray();
    }
}

/// <summary>
/// What <see cref="ModuleAdminRepository.ApplyChanges"/> did: whether the module's rows could be read under
/// the write lock, whether it wrote, the groups as read and as left, and the named groups that already held
/// the wanted state.
/// </summary>
public sealed record ModuleAdminChangeOutcome(
    bool Readable, bool Written, string[] Before, string[] After, IReadOnlyList<string> AlreadyApplied);

/// <summary>One module-admin grant stored by name and the SID <c>db convert-identifiers</c> resolved it to.</summary>
public sealed record ModuleAdminSidConversion(string ModuleId, string Name, string Sid);
