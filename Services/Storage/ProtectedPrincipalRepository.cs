using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Repository for the row-per-value <c>protected_principal</c> table (kind, value) plus a single
/// <c>protected_principal_present</c> marker. Backs <see cref="ProtectedPrincipalService"/>.
/// The four config lists (Users / Groups / OrganizationalUnits / SamAccountNamePatterns) are
/// stored as rows keyed by <c>kind</c>; the presence marker preserves the file-world
/// File.Exists distinction (configured-but-empty vs never configured). Row-per-value means one
/// bad entry can't drop the whole config. Sits on <see cref="IConfigStore"/>.
/// </summary>
public sealed class ProtectedPrincipalRepository
{
    public const string KindUser = "user";
    public const string KindGroup = "group";
    public const string KindOu = "ou";
    public const string KindSamPattern = "sam_pattern";

    /// <summary>
    /// Protected write TARGETS (docs/ProtectedGroupWriteTarget-Plan.md T0): a group protected
    /// AS AN OBJECT being written into, never a rule about its members. Stored beside the four
    /// principal-rule kinds and reinterpreting none of them. Value format: "objectGUID|DN".
    /// </summary>
    public const string KindGroupTarget = "group_target";

    /// <summary>
    /// Identifier rows (docs/ProtectedPrincipalIdentifiers-Plan.md A.2): a protected user by objectSid, a protected
    /// group by objectSid, a protected OU by objectGUID. Value format "identifier|label"
    /// (<see cref="ProtectedPrincipalIdEntry"/>), the label being the value an older build stores in the legacy kind.
    /// An older build's reader has no arm for these kinds and ignores them; it keeps enforcing the companion legacy row
    /// the admin page writes beside each one (plan B.1, B.2).
    /// </summary>
    public const string KindUserSid = "user_sid";

    /// <inheritdoc cref="KindUserSid"/>
    public const string KindGroupSid = "group_sid";

    /// <inheritdoc cref="KindUserSid"/>
    public const string KindOuGuid = "ou_guid";

    private readonly IConfigStore _store;

    public ProtectedPrincipalRepository(IConfigStore store) => _store = store;

    /// <summary>A change watcher over this repository's store, for the caching reader that sits on it.</summary>
    public ConfigChangeWatcher CreateChangeWatcher(ILogger? logger = null) => new(_store, logger);

    /// <summary>
    /// Reads the four lists AND the configured flag in a single guarded operation. Returns false
    /// if either read throws (DB-integrity failure / partial schema damage); both out-params are
    /// safe-defaulted so callers fail closed rather than throw.
    /// </summary>
    public bool TryRead(out ProtectedPrincipalData data, out bool configured)
    {
        try
        {
            (data, configured) = _store.Read(connection =>
            {
                var lists = ReadLists(connection);
                var present = ReadConfigured(connection);
                return (lists, present);
            });
            return true;
        }
        catch
        {
            data = ProtectedPrincipalData.Empty;
            configured = false;
            return false;
        }
    }

    /// <summary>True if a protected-principals config has been saved (presence marker set).</summary>
    public bool IsConfigured()
    {
        return _store.Read(ReadConfigured);
    }

    /// <summary>
    /// Replaces the entire protected-principal config (clear + insert all four lists) and sets
    /// the presence marker, in one transaction - matching the file version's whole-file save.
    /// </summary>
    /// <remarks>
    /// Internal, and reached in production only from <see cref="ProtectedPrincipalService.SaveConfig"/>, itself an
    /// internal test fixture (aud-a26n): it reads nothing first, and the admin page's save decides under the lock
    /// (<see cref="Replace"/>). WholeStoreWriterTripwireTests refuses another caller.
    /// </remarks>
    internal void Save(ProtectedPrincipalData data)
    {
        _store.Write((connection, transaction) =>
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM protected_principal;";
                delete.ExecuteNonQuery();
            }

            InsertAll(connection, transaction, data);

            MarkPresent(connection, transaction);
        });
    }

    /// <summary>
    /// Reads the rules and replaces them in ONE write transaction (aud-a28j): <paramref name="decide"/>
    /// is shown the rows and the presence marker as they are under the write lock and returns the
    /// rules to store, or null to store nothing - in which case the transaction is rolled back and
    /// nothing, not even the change token, is written.
    /// </summary>
    /// <remarks>
    /// The admin page's save used to replace the table with whatever lists the page held, reading
    /// nothing first, so a page whose load had failed wrote its empty lists over every rule. The decision
    /// is made here, from the rows as they are under the lock <c>SqliteConfigStore.Write</c> takes at
    /// BEGIN IMMEDIATE: dev and prod share this database, and a read made before the write could be
    /// overtaken by the other instance before the replace lands (the aud-a28i and aud-a33b shape). The
    /// rows are read back before the commit so the caller's record names exactly what it left.
    /// </remarks>
    public ProtectedPrincipalReplaceOutcome Replace(Func<ProtectedPrincipalSnapshot, ProtectedPrincipalData?> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        try
        {
            return _store.Write((connection, transaction) =>
            {
                var before = ReadSnapshot(connection, transaction);
                var replacement = decide(before) ?? throw new NothingWritten(before);

                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM protected_principal;";
                    delete.ExecuteNonQuery();
                }

                InsertAll(connection, transaction, replacement);
                MarkPresent(connection, transaction);

                return new ProtectedPrincipalReplaceOutcome(true, before, ReadSnapshot(connection, transaction));
            });
        }
        catch (NothingWritten nothing)
        {
            return new ProtectedPrincipalReplaceOutcome(false, nothing.Before, null);
        }
    }

    /// <summary>
    /// The rows and the presence marker from one fresh read (aud-a28n); an unreadable store as
    /// <see cref="ProtectedPrincipalSnapshot.Unreadable"/>, never an exception.
    /// </summary>
    public ProtectedPrincipalSnapshot ReadSnapshot()
    {
        try
        {
            return _store.Read(connection => ReadSnapshot(connection, null));
        }
        catch
        {
            return ProtectedPrincipalSnapshot.Unreadable;
        }
    }

    // A read that throws - a damaged or missing table - is a snapshot that says it could not be read: the
    // service refuses on it, and inside the write it must not escape as an exception the page would record
    // as a failed write rather than a refusal.
    private static ProtectedPrincipalSnapshot ReadSnapshot(SqliteConnection connection, SqliteTransaction? transaction)
    {
        try
        {
            return new ProtectedPrincipalSnapshot(
                true, ReadLists(connection, transaction), ReadConfigured(connection, transaction));
        }
        catch
        {
            return ProtectedPrincipalSnapshot.Unreadable;
        }
    }

    // Thrown out of the write when decide stores nothing, because IConfigStore.Write commits - and bumps the
    // change token every cache on both instances watches - whenever its callback returns. An exception is its
    // documented rollback. Caught in Replace; it never leaves this class.
    private sealed class NothingWritten(ProtectedPrincipalSnapshot before) : Exception
    {
        public ProtectedPrincipalSnapshot Before { get; } = before;
    }

    /// <summary>
    /// One-time import only if not yet configured (presence marker absent). Marks present even
    /// for an all-empty config (an existing file with empty lists still counted as configured).
    /// Returns true if it imported.
    /// </summary>
    public bool ImportIfMissing(ProtectedPrincipalData data)
    {
        return _store.Write((connection, transaction) =>
        {
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT 1 FROM protected_principal_present LIMIT 1;";
                if (check.ExecuteScalar() is not null)
                    return false;
            }

            InsertAll(connection, transaction, data);
            MarkPresent(connection, transaction);
            return true;
        });
    }

    /// <summary>
    /// The rows of every kind on <paramref name="connection"/>, inside <paramref name="transaction"/> when one is given: the read
    /// <c>db convert-identifiers</c> and <c>db identifier-status</c> make, which have no <see cref="IConfigStore"/> to read through
    /// (docs/ProtectedPrincipalIdentifiers-Plan.md K.3). Throws when the read fails.
    /// </summary>
    internal static ProtectedPrincipalData ReadRows(SqliteConnection connection, SqliteTransaction? transaction)
        => ReadLists(connection, transaction);

    /// <summary>
    /// <c>db convert-identifiers</c>' write of one conversion: a NEW identifier row, <c>identifier|legacy value</c>, in its
    /// identifier kind beside the legacy row, which is never rewritten and becomes its companion (plan B.1). Inside the verb's
    /// one transaction, which has re-read every row under the write lock first.
    /// </summary>
    internal static void InsertIdentifierRow(SqliteConnection connection, SqliteTransaction transaction, ProtectedPrincipalIdConversion conversion)
    {
        // Never into a legacy kind, where an older build would read "S-1-...|..." as a name (ProtectedPrincipalIdKindsTripwireTests).
        if (conversion.IdKind is not (KindUserSid or KindGroupSid or KindOuGuid))
            throw new ArgumentException($"'{conversion.IdKind}' is not an identifier kind.", nameof(conversion));
        InsertKind(connection, transaction, conversion.IdKind, [ProtectedPrincipalIdEntry.Format(conversion.Identifier, conversion.LegacyValue)]);
    }

    private static ProtectedPrincipalData ReadLists(SqliteConnection connection) => ReadLists(connection, null);

    private static ProtectedPrincipalData ReadLists(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT kind, value FROM protected_principal;";
        var users = new List<string>();
        var groups = new List<string>();
        var ous = new List<string>();
        var patterns = new List<string>();
        var targets = new List<string>();
        var userIds = new List<string>();
        var groupIds = new List<string>();
        var ouIds = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var kind = reader.GetString(0);
            var value = reader.GetString(1);
            switch (kind)
            {
                case KindUser: users.Add(value); break;
                case KindGroup: groups.Add(value); break;
                case KindOu: ous.Add(value); break;
                case KindSamPattern: patterns.Add(value); break;
                case KindGroupTarget: targets.Add(value); break;
                case KindUserSid: userIds.Add(value); break;
                case KindGroupSid: groupIds.Add(value); break;
                case KindOuGuid: ouIds.Add(value); break;
            }
        }
        return new ProtectedPrincipalData(
            users.ToArray(), groups.ToArray(), ous.ToArray(), patterns.ToArray(), targets.ToArray())
        {
            UserIds = userIds.ToArray(),
            GroupIds = groupIds.ToArray(),
            OuIds = ouIds.ToArray(),
        };
    }

    private static bool ReadConfigured(SqliteConnection connection) => ReadConfigured(connection, null);

    private static bool ReadConfigured(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM protected_principal_present LIMIT 1;";
        return command.ExecuteScalar() is not null;
    }

    // Every kind, legacy and identifier, each into its own kind: an identifier row is never written into a legacy kind,
    // where an older build would read it as a user, group or OU named "S-1-...|..." (ProtectedPrincipalIdKindsTripwireTests).
    private static void InsertAll(SqliteConnection connection, SqliteTransaction transaction, ProtectedPrincipalData data)
    {
        InsertKind(connection, transaction, KindUser, data.Users);
        InsertKind(connection, transaction, KindGroup, data.Groups);
        InsertKind(connection, transaction, KindOu, data.OrganizationalUnits);
        InsertKind(connection, transaction, KindSamPattern, data.SamAccountNamePatterns);
        InsertKind(connection, transaction, KindGroupTarget, data.GroupTargets);
        InsertKind(connection, transaction, KindUserSid, data.UserIds);
        InsertKind(connection, transaction, KindGroupSid, data.GroupIds);
        InsertKind(connection, transaction, KindOuGuid, data.OuIds);
    }

    private static void InsertKind(SqliteConnection connection, SqliteTransaction transaction, string kind, string[] values)
    {
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO protected_principal (kind, value) VALUES ($kind, $value) " +
                "ON CONFLICT(kind, value) DO NOTHING;";
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$value", value);
            insert.ExecuteNonQuery();
        }
    }

    private static void MarkPresent(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO protected_principal_present (marker) VALUES (1) ON CONFLICT(marker) DO NOTHING;";
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// The protected-principal lists, repository-shaped (decoupled from the service config type).
/// <paramref name="GroupTargets"/> is the write-target list (kind <c>group_target</c>), a
/// separate rule set from the four principal lists (docs/ProtectedGroupWriteTarget-Plan.md T0).
/// </summary>
public sealed record ProtectedPrincipalData(
    string[] Users,
    string[] Groups,
    string[] OrganizationalUnits,
    string[] SamAccountNamePatterns,
    string[] GroupTargets)
{
    public static ProtectedPrincipalData Empty { get; } = new([], [], [], [], []);

    /// <summary>Identifier rows of kind <c>user_sid</c>, each "objectSid|label" (docs/ProtectedPrincipalIdentifiers-Plan.md A.2).</summary>
    public string[] UserIds { get; init; } = [];

    /// <summary>Identifier rows of kind <c>group_sid</c>, each "objectSid|DN".</summary>
    public string[] GroupIds { get; init; } = [];

    /// <summary>Identifier rows of kind <c>ou_guid</c>, each "objectGUID|DN".</summary>
    public string[] OuIds { get; init; } = [];

    /// <summary>How many rows of all eight kinds these lists hold, a companion legacy row counted as its own row.</summary>
    public int RuleCount =>
        Users.Length + Groups.Length + OrganizationalUnits.Length + SamAccountNamePatterns.Length + GroupTargets.Length
        + UserIds.Length + GroupIds.Length + OuIds.Length;
}

/// <summary>The protected-principal rows and presence marker, read together (aud-a28j).</summary>
/// <param name="Readable">False when the read failed; <paramref name="Data"/> is then empty and means nothing.</param>
public sealed record ProtectedPrincipalSnapshot(bool Readable, ProtectedPrincipalData Data, bool Configured)
{
    public static ProtectedPrincipalSnapshot Unreadable { get; } = new(false, ProtectedPrincipalData.Empty, false);
}

/// <summary>
/// What <see cref="ProtectedPrincipalRepository.Replace"/> did: whether it replaced the rules, the rows
/// it found under the write lock, and - after a replace - the rows it left, read back before the commit.
/// </summary>
public sealed record ProtectedPrincipalReplaceOutcome(
    bool Replaced, ProtectedPrincipalSnapshot Before, ProtectedPrincipalSnapshot? After);

/// <summary>
/// One conversion <c>db convert-identifiers</c> writes (docs/ProtectedPrincipalIdentifiers-Plan.md K.3): the legacy row as the
/// verb read it, and the identifier row it adds beside it, the legacy value its label.
/// </summary>
public sealed record ProtectedPrincipalIdConversion(string LegacyKind, string LegacyValue, string IdKind, string Identifier);
