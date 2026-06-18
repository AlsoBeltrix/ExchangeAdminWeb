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

    private readonly IConfigStore _store;

    public ProtectedPrincipalRepository(IConfigStore store) => _store = store;

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
    /// the presence marker, in one transaction — matching the file version's whole-file save.
    /// </summary>
    public void Save(ProtectedPrincipalData data)
    {
        _store.Write((connection, transaction) =>
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM protected_principal;";
                delete.ExecuteNonQuery();
            }

            InsertKind(connection, transaction, KindUser, data.Users);
            InsertKind(connection, transaction, KindGroup, data.Groups);
            InsertKind(connection, transaction, KindOu, data.OrganizationalUnits);
            InsertKind(connection, transaction, KindSamPattern, data.SamAccountNamePatterns);

            MarkPresent(connection, transaction);
        });
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

            InsertKind(connection, transaction, KindUser, data.Users);
            InsertKind(connection, transaction, KindGroup, data.Groups);
            InsertKind(connection, transaction, KindOu, data.OrganizationalUnits);
            InsertKind(connection, transaction, KindSamPattern, data.SamAccountNamePatterns);
            MarkPresent(connection, transaction);
            return true;
        });
    }

    private static ProtectedPrincipalData ReadLists(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, value FROM protected_principal;";
        var users = new List<string>();
        var groups = new List<string>();
        var ous = new List<string>();
        var patterns = new List<string>();
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
            }
        }
        return new ProtectedPrincipalData(users.ToArray(), groups.ToArray(), ous.ToArray(), patterns.ToArray());
    }

    private static bool ReadConfigured(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM protected_principal_present LIMIT 1;";
        return command.ExecuteScalar() is not null;
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

/// <summary>The four protected-principal lists, repository-shaped (decoupled from the service config type).</summary>
public sealed record ProtectedPrincipalData(
    string[] Users,
    string[] Groups,
    string[] OrganizationalUnits,
    string[] SamAccountNamePatterns)
{
    public static ProtectedPrincipalData Empty { get; } = new([], [], [], []);
}
