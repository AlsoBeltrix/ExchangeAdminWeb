using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Creates and evolves the config database schema using <c>PRAGMA user_version</c> as the
/// version cursor. Each migration is an idempotent step applied in order inside a single
/// transaction; re-running against an up-to-date database is a no-op. This is the only place
/// schema DDL lives (SqliteConfigStore-Plan §3c, §5A).
///
/// Every text column that backs an ID / alias / key is declared <c>COLLATE NOCASE</c> so the
/// case-insensitive comparisons the service layer relies on (OrdinalIgnoreCase throughout)
/// keep working — without it, "ExchangeOnline" and "exchangeonline" would become two rows
/// (plan §5B.3).
/// </summary>
public sealed class ConfigStoreMigrator
{
    private readonly SqliteConnectionFactory _factory;

    // Ordered schema steps. The array index + 1 is the resulting user_version, so appending a
    // new step is the only supported way to evolve the schema. Never edit or reorder an
    // existing step — that would diverge already-migrated databases from fresh ones.
    private static readonly string[] Migrations =
    [
        // v1 — initial schema.
        """
        CREATE TABLE IF NOT EXISTS schema_meta (
            key   TEXT PRIMARY KEY COLLATE NOCASE,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS module_enablement (
            module_id  TEXT PRIMARY KEY COLLATE NOCASE,
            enabled    INTEGER NOT NULL,
            updated_at TEXT
        );

        CREATE TABLE IF NOT EXISTS module_config (
            module_id    TEXT NOT NULL COLLATE NOCASE,
            config_key   TEXT NOT NULL COLLATE NOCASE,
            config_value TEXT,
            updated_at   TEXT,
            PRIMARY KEY (module_id, config_key)
        );

        CREATE TABLE IF NOT EXISTS section_access (
            policy_alias TEXT NOT NULL COLLATE NOCASE,
            group_value  TEXT NOT NULL COLLATE NOCASE,
            PRIMARY KEY (policy_alias, group_value)
        );

        CREATE TABLE IF NOT EXISTS module_admins (
            module_id   TEXT NOT NULL COLLATE NOCASE,
            admin_group TEXT NOT NULL COLLATE NOCASE,
            PRIMARY KEY (module_id, admin_group)
        );

        CREATE TABLE IF NOT EXISTS protected_principal (
            kind  TEXT NOT NULL COLLATE NOCASE,
            value TEXT NOT NULL COLLATE NOCASE,
            PRIMARY KEY (kind, value)
        );

        CREATE TABLE IF NOT EXISTS editable_attribute (
            name        TEXT PRIMARY KEY COLLATE NOCASE,
            label       TEXT,
            type        TEXT,
            choices_json TEXT,
            required    INTEGER,
            allow_clear INTEGER,
            max_length  INTEGER,
            pattern     TEXT,
            level       INTEGER
        );

        CREATE TABLE IF NOT EXISTS attribute_legend (
            attribute_name TEXT NOT NULL COLLATE NOCASE,
            choice_value   TEXT NOT NULL COLLATE NOCASE,
            description    TEXT,
            note           TEXT,
            source         TEXT,
            PRIMARY KEY (attribute_name, choice_value)
        );

        CREATE TABLE IF NOT EXISTS app_setting (
            key   TEXT PRIMARY KEY COLLATE NOCASE,
            value TEXT
        );
        """,

        // v2 — module-config presence marker. Preserves the file world's semantics where an
        // empty module-config-{Id}.json STILL counted as "configured" (HasModuleConfigFile=true)
        // and therefore suppressed the legacy appsettings fallback. With per-row storage an empty
        // module has no rows, so presence must be tracked separately or saving an empty config
        // would silently re-enable the fallback (parity break found in B.3 review).
        """
        CREATE TABLE IF NOT EXISTS module_config_present (
            module_id TEXT PRIMARY KEY COLLATE NOCASE
        );
        """,

        // v3 — section-access presence marker. Same reasoning as v2 for the single section_access
        // store: an admin who clears ALL access must still count as "configured" (the file-world
        // Fragment source — everything denied), NOT fall back to the None source which grants
        // read-only sections the AllowedGroups list. A single sentinel row marks "configured".
        """
        CREATE TABLE IF NOT EXISTS section_access_present (
            marker INTEGER PRIMARY KEY
        );
        """,
    ];

    /// <summary>The schema version this build expects (the count of migration steps).</summary>
    public static int TargetVersion => Migrations.Length;

    public ConfigStoreMigrator(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Brings the database up to <see cref="TargetVersion"/>. Idempotent and safe to run on
    /// every startup: applies only the steps newer than the database's current user_version,
    /// each in its own transaction. Returns the version the database is at afterward.
    /// </summary>
    public int Migrate()
    {
        using var connection = _factory.Open();

        var current = GetUserVersion(connection);

        // Fail fast if the database was migrated by a NEWER build than this one (e.g. an
        // environment rolled back after a later schema shipped). Silently skipping the loop
        // would report "ready" against a schema this binary may not understand.
        if (current > Migrations.Length)
        {
            throw new InvalidOperationException(
                $"Config database schema version {current} is newer than this build supports " +
                $"(max {Migrations.Length}). Deploy a build at or above the schema version, or " +
                "restore a compatible database backup.");
        }

        for (var version = current; version < Migrations.Length; version++)
        {
            using var transaction = connection.BeginTransaction();

            using (var step = connection.CreateCommand())
            {
                step.Transaction = transaction;
                step.CommandText = Migrations[version];
                step.ExecuteNonQuery();
            }

            // user_version cannot be parameterized; it is an integer from our own array index.
            using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                bump.CommandText = $"PRAGMA user_version = {version + 1};";
                bump.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return GetUserVersion(connection);
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
