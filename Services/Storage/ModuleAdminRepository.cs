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

    /// <summary>Returns the admin groups for one module (empty if none).</summary>
    public string[] GetForModule(string moduleId)
    {
        return _store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT admin_group FROM module_admins WHERE module_id = $id;";
            command.Parameters.AddWithValue("$id", moduleId);
            return ReadGroups(command);
        });
    }

    /// <summary>Returns the full map of module_id -> admin groups (only modules with rows).</summary>
    public Dictionary<string, string[]> GetAll()
    {
        return _store.Read(connection =>
        {
            using var command = connection.CreateCommand();
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
        });
    }

    /// <summary>
    /// Replaces the admin groups for one module (delete-then-insert) inside one transaction,
    /// mirroring the file version's whole-list overwrite semantics.
    /// </summary>
    public void SetForModule(string moduleId, string[] groups)
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
    public ModuleAdminChangeOutcome ApplyChanges(string moduleId, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove)
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
                return new ModuleAdminChangeOutcome(
                    true, true, before, ReadModuleGroups(connection, transaction, moduleId), alreadyApplied);
            });
        }
        catch (NothingWritten nothing)
        {
            return nothing.Before is null
                ? new ModuleAdminChangeOutcome(false, false, [], [], [])
                : new ModuleAdminChangeOutcome(true, false, nothing.Before, nothing.Before, nothing.AlreadyApplied);
        }
    }

    private sealed class NothingWritten(string[]? before, IReadOnlyList<string> alreadyApplied) : Exception
    {
        public string[]? Before { get; } = before;

        public IReadOnlyList<string> AlreadyApplied { get; } = alreadyApplied;
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
