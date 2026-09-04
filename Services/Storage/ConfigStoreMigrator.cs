using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Creates and evolves the config database schema using <c>PRAGMA user_version</c> as the
/// version cursor. Each migration is an idempotent step applied in order inside a single
/// transaction; re-running against an up-to-date database is a no-op. This is the only place
/// schema DDL lives (SqliteConfigStore-Plan Section 3c, Section 5A).
///
/// Every text column that backs an ID / alias / key is declared <c>COLLATE NOCASE</c> so the
/// case-insensitive comparisons the service layer relies on (OrdinalIgnoreCase throughout)
/// keep working - without it, "ExchangeOnline" and "exchangeonline" would become two rows
/// (plan Section 5B.3).
/// </summary>
public sealed class ConfigStoreMigrator
{
    private readonly SqliteConnectionFactory _factory;

    // Ordered schema steps. The array index + 1 is the resulting user_version, so appending a
    // new step is the only supported way to evolve the schema. Never edit or reorder an
    // existing step - that would diverge already-migrated databases from fresh ones.
    private static readonly string[] Migrations =
    [
        // v1 - initial schema.
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

        // v2 - module-config presence marker. Preserves the file world's semantics where an
        // empty module-config-{Id}.json STILL counted as "configured" (HasModuleConfigFile=true)
        // and therefore suppressed the legacy appsettings fallback. With per-row storage an empty
        // module has no rows, so presence must be tracked separately or saving an empty config
        // would silently re-enable the fallback (parity break found in B.3 review).
        """
        CREATE TABLE IF NOT EXISTS module_config_present (
            module_id TEXT PRIMARY KEY COLLATE NOCASE
        );
        """,

        // v3 - section-access presence marker. Same reasoning as v2 for the single section_access
        // store: an admin who clears ALL access must still count as "configured" (the file-world
        // Fragment source - everything denied), NOT fall back to the None source which grants
        // read-only sections the AllowedGroups list. A single sentinel row marks "configured".
        """
        CREATE TABLE IF NOT EXISTS section_access_present (
            marker INTEGER PRIMARY KEY
        );
        """,

        // v4 - protected-principal presence marker. HasCentralConfig must distinguish "a
        // protected-principals config exists" (even if all four lists are empty) from "never
        // configured" (legacy ExcludedUsers fallback applies), exactly like the file world's
        // File.Exists check. A single sentinel row marks "configured".
        """
        CREATE TABLE IF NOT EXISTS protected_principal_present (
            marker INTEGER PRIMARY KEY
        );
        """,

        // v5 - AD-attribute-editor presence markers. The allowlist is NULL-on-corrupt but EMPTY
        // when never configured; the marker lets an explicitly-empty allowlist read back as empty
        // (not null). The legend marker drives one-time import. Both are single sentinel rows.
        """
        CREATE TABLE IF NOT EXISTS editable_attribute_present (
            marker INTEGER PRIMARY KEY
        );
        CREATE TABLE IF NOT EXISTS attribute_legend_present (
            marker INTEGER PRIMARY KEY
        );
        """,

        // v6 - display name alongside the section-access group value, so group_value can become a
        // SID while the admin page still shows a name (SectionAccessSidStorage-Plan). Nullable and
        // never read by any authorization path: a stale display name is cosmetic, the SID is the
        // identity. DDL only - the data conversion is a separate, AD-dependent step that must be
        // able to fail and retry without blocking startup, which a schema migration cannot.
        """
        ALTER TABLE section_access ADD COLUMN group_display_name TEXT;
        """,
    ];

    /// <summary>
    /// Every table the steps above create and every column they declare or add - the schema
    /// this build reads and writes. Consulted only when the database is NEWER than this build
    /// (a shared database the other instance migrated first, decision 2026-09-04): the build
    /// serves on the newer schema as long as everything it needs is present. Columns are
    /// listed too, not table names alone, because v6 adds a column SectionAccessRepository
    /// writes and a table-only check would pass and then fail inside a save (review scd-3).
    /// Pinned to the steps by ConfigStoreMigratorTests.RequiredSchema_MatchesTheMigrationStatements.
    /// </summary>
    internal static readonly IReadOnlyList<(string Table, string[] Columns)> RequiredSchema =
    [
        ("schema_meta", ["key", "value"]),
        ("module_enablement", ["module_id", "enabled", "updated_at"]),
        ("module_config", ["module_id", "config_key", "config_value", "updated_at"]),
        ("section_access", ["policy_alias", "group_value", "group_display_name"]),
        ("module_admins", ["module_id", "admin_group"]),
        ("protected_principal", ["kind", "value"]),
        ("editable_attribute", ["name", "label", "type", "choices_json", "required", "allow_clear", "max_length", "pattern", "level"]),
        ("attribute_legend", ["attribute_name", "choice_value", "description", "note", "source"]),
        ("app_setting", ["key", "value"]),
        ("module_config_present", ["module_id"]),
        ("section_access_present", ["marker"]),
        ("protected_principal_present", ["marker"]),
        ("editable_attribute_present", ["marker"]),
        ("attribute_legend_present", ["marker"]),
    ];

    /// <summary>The schema version this build expects (the count of migration steps).</summary>
    public static int TargetVersion => Migrations.Length;

    /// <summary>
    /// The step texts, for the tests that pin <see cref="RequiredSchema"/> to them and enforce
    /// the additive-only rule (decision 2026-09-04: a step may add tables, nullable-or-defaulted
    /// columns and indexes; never drop, rename or repurpose).
    /// </summary>
    internal static IReadOnlyList<string> MigrationSteps => Migrations;

    private readonly ILogger<ConfigStoreMigrator>? _logger;

    public ConfigStoreMigrator(SqliteConnectionFactory factory, ILogger<ConfigStoreMigrator>? logger = null)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// Brings the database up to <see cref="TargetVersion"/>. Idempotent and safe to run on
    /// every startup: applies only the steps newer than the database's current user_version,
    /// each in its own transaction. Returns the version the database is at afterward.
    /// A database NEWER than this build is accepted (with a warning) once its schema is
    /// verified to contain everything this build requires; a missing table or column throws.
    /// </summary>
    public int Migrate()
    {
        using var connection = _factory.Open();

        var current = GetUserVersion(connection);

        if (current > Migrations.Length)
        {
            // Shared-DB rule (decisions 2026-09-04): the other instance is newer and steps are
            // additive-only, so serve on its schema. Verify this build's tables and columns
            // exist first - a non-additive step that slipped through must stop us here, not
            // inside a later read or write.
            var missing = FindMissingSchema(connection);
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Config database schema version {current} is newer than this build ({Migrations.Length}) " +
                    $"and lacks schema this build requires: {string.Join(", ", missing)}. A migration step " +
                    "was not additive-only; restore a compatible database backup or deploy a matching build.");
            }

            _logger?.LogWarning(
                "Config database schema version {DbVersion} is newer than this build supports ({BuildVersion}); " +
                "serving on the newer schema (additive-only rule, decision 2026-09-04)",
                current, Migrations.Length);
            return current;
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

    /// <summary>
    /// Names every <see cref="RequiredSchema"/> table absent from <c>sqlite_master</c> and every
    /// column absent from <c>PRAGMA table_info</c>, as "table" or "table.column". Empty when the
    /// database has everything this build needs.
    /// </summary>
    internal static List<string> FindMissingSchema(SqliteConnection connection)
    {
        var missing = new List<string>();

        foreach (var (table, columns) in RequiredSchema)
        {
            using (var exists = connection.CreateCommand())
            {
                exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
                exists.Parameters.AddWithValue("$name", table);
                if (exists.ExecuteScalar() is null)
                {
                    missing.Add(table);
                    continue;
                }
            }

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var info = connection.CreateCommand())
            {
                // PRAGMA arguments cannot be parameterized; the table name comes from our own
                // static list, never from input.
                info.CommandText = $"PRAGMA table_info(\"{table}\");";
                using var reader = info.ExecuteReader();
                while (reader.Read())
                    present.Add(reader.GetString(1));
            }

            foreach (var column in columns)
            {
                if (!present.Contains(column))
                    missing.Add($"{table}.{column}");
            }
        }

        return missing;
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
