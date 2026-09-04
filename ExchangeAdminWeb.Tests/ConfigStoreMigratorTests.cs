using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Tests;

public class ConfigStoreMigratorTests
{
    [Fact]
    public void Migrate_FreshDatabase_ReachesTargetVersionAndCreatesTables()
    {
        using var temp = new TempDir();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        var migrator = new ConfigStoreMigrator(factory);

        var version = migrator.Migrate();

        Assert.Equal(ConfigStoreMigrator.TargetVersion, version);
        Assert.True(version >= 1);

        // Every Phase A table must exist.
        foreach (var table in new[]
        {
            "schema_meta", "module_enablement", "module_config", "module_config_present",
            "section_access", "module_admins", "protected_principal", "editable_attribute",
            "attribute_legend", "app_setting",
        })
        {
            Assert.True(TableExists(factory, table), $"table {table} should exist");
        }
    }

    [Fact]
    public void Migrate_RunTwice_IsIdempotentNoOp()
    {
        using var temp = new TempDir();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        var migrator = new ConfigStoreMigrator(factory);

        var first = migrator.Migrate();
        var second = migrator.Migrate();

        Assert.Equal(first, second);
        Assert.Equal(ConfigStoreMigrator.TargetVersion, second);
    }

    [Fact]
    public void Migrate_PreservesExistingDataOnRerun()
    {
        using var temp = new TempDir();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        var migrator = new ConfigStoreMigrator(factory);
        migrator.Migrate();

        using (var connection = factory.Open())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO app_setting (key, value) VALUES ('k', 'v');";
            insert.ExecuteNonQuery();
        }

        // A second migrate must not drop or recreate populated tables.
        migrator.Migrate();

        using var read = factory.Open();
        using var select = read.CreateCommand();
        select.CommandText = "SELECT value FROM app_setting WHERE key = 'k';";
        Assert.Equal("v", select.ExecuteScalar());
    }

    [Fact]
    public void TextKeys_AreCaseInsensitive_ViaNoCaseCollation()
    {
        using var temp = new TempDir();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new ConfigStoreMigrator(factory).Migrate();

        using var connection = factory.Open();
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO module_enablement (module_id, enabled) VALUES ('ExchangeOnline', 1);";
            insert.ExecuteNonQuery();
        }

        // Reading via different casing must hit the same row (OrdinalIgnoreCase parity).
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT enabled FROM module_enablement WHERE module_id = 'exchangeonline';";
        Assert.Equal(1L, select.ExecuteScalar());

        // And a mixed-case duplicate must collide on the primary key rather than create a 2nd row.
        using var dup = connection.CreateCommand();
        dup.CommandText = "INSERT INTO module_enablement (module_id, enabled) VALUES ('EXCHANGEONLINE', 0);";
        Assert.Throws<SqliteException>(() => dup.ExecuteNonQuery());
    }

    // docs/SharedConfigDb-Plan.md AC2: a database migrated by a NEWER build (the other instance
    // on the shared file) is accepted without writing, as long as every table and column this
    // build needs is present.
    [Fact]
    public void Migrate_DatabaseNewerThanBuild_IsAcceptedWhenTablesExist()
    {
        using var temp = new TempDir();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new ConfigStoreMigrator(factory).Migrate();
        var newer = ConfigStoreMigrator.TargetVersion + 5;
        Execute(factory, $"PRAGMA user_version = {newer};");

        var version = new ConfigStoreMigrator(factory).Migrate();

        Assert.Equal(newer, version);
        Assert.True(TableExists(factory, "app_setting"));
        Assert.True(TableExists(factory, "section_access"));
        Assert.Equal(newer, ReadUserVersion(factory));
    }

    [Fact]
    public void Migrate_DatabaseNewerThanBuild_ThrowsWhenARequiredTableIsMissing()
    {
        using var temp = new TempDir();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new ConfigStoreMigrator(factory).Migrate();
        Execute(factory, $"PRAGMA user_version = {ConfigStoreMigrator.TargetVersion + 5};");
        Execute(factory, "DROP TABLE app_setting;");

        var ex = Assert.Throws<InvalidOperationException>(() => new ConfigStoreMigrator(factory).Migrate());

        Assert.Contains("newer than this build", ex.Message);
        Assert.Contains("app_setting", ex.Message);
    }

    // Review finding scd-3: v6 adds section_access.group_display_name, which
    // SectionAccessRepository reads and writes. A table-only check would pass a database that
    // lacks the column and the failure would surface later inside a save.
    [Fact]
    public void Migrate_DatabaseNewerThanBuild_ThrowsWhenARequiredColumnIsMissing()
    {
        using var temp = new TempDir();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new ConfigStoreMigrator(factory).Migrate();
        Execute(factory, $"PRAGMA user_version = {ConfigStoreMigrator.TargetVersion + 5};");
        Execute(factory,
            "DROP TABLE section_access;" +
            "CREATE TABLE section_access (policy_alias TEXT NOT NULL COLLATE NOCASE, group_value TEXT NOT NULL COLLATE NOCASE, PRIMARY KEY (policy_alias, group_value));");

        var ex = Assert.Throws<InvalidOperationException>(() => new ConfigStoreMigrator(factory).Migrate());

        Assert.Contains("section_access.group_display_name", ex.Message);
    }

    [Fact]
    public void RequiredSchema_MatchesTheMigrationStatements()
    {
        var expected = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in ConfigStoreMigrator.MigrationSteps)
        {
            foreach (System.Text.RegularExpressions.Match create in System.Text.RegularExpressions.Regex.Matches(
                step, @"CREATE TABLE IF NOT EXISTS\s+(\w+)\s*\((.*?)\);", System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                var columns = new List<string>();
                foreach (var line in create.Groups[2].Value.Split('\n'))
                {
                    var trimmed = line.Trim().TrimEnd(',');
                    if (trimmed.Length == 0 || trimmed.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
                        continue;
                    columns.Add(trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
                }
                expected[create.Groups[1].Value] = columns;
            }

            foreach (System.Text.RegularExpressions.Match add in System.Text.RegularExpressions.Regex.Matches(
                step, @"ALTER TABLE\s+(\w+)\s+ADD COLUMN\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                Assert.True(expected.ContainsKey(add.Groups[1].Value), $"ADD COLUMN on unknown table {add.Groups[1].Value}");
                expected[add.Groups[1].Value].Add(add.Groups[2].Value);
            }
        }

        var actual = ConfigStoreMigrator.RequiredSchema.ToDictionary(r => r.Table, r => r.Columns.ToList(), StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected.Keys.OrderBy(k => k), actual.Keys.OrderBy(k => k));
        foreach (var (table, columns) in expected)
            Assert.Equal(columns.OrderBy(c => c), actual[table].OrderBy(c => c));
    }

    // AC3 tripwire (decision 2026-09-04): with both instances on one database and each
    // accepting the other's newer schema, a step may only ADD - tables, nullable-or-defaulted
    // columns, indexes. Dropping, renaming or repurposing needs its own plan and a coordinated
    // deploy of both instances.
    [Fact]
    public void MigrationsAreAdditiveOnly()
    {
        AssertAdditiveOnly(ConfigStoreMigrator.MigrationSteps);
    }

    [Fact]
    public void MigrationsAreAdditiveOnly_Tripwire_CatchesADrop()
    {
        var withDrop = ConfigStoreMigrator.MigrationSteps.Append("DROP TABLE app_setting;").ToList();
        var withRename = ConfigStoreMigrator.MigrationSteps.Append("ALTER TABLE app_setting RENAME TO settings;").ToList();
        var withDropColumn = ConfigStoreMigrator.MigrationSteps.Append("alter table app_setting drop column value;").ToList();

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertAdditiveOnly(withDrop));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertAdditiveOnly(withRename));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertAdditiveOnly(withDropColumn));
    }

    private static void AssertAdditiveOnly(IReadOnlyList<string> steps)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var text = steps[i];
            foreach (var forbidden in new[] { "DROP ", "RENAME" })
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"Migration step v{i + 1} contains '{forbidden.Trim()}'. Schema steps are additive-only " +
                    "(.agents/decisions.md 2026-09-04, shared config database): add tables, nullable-or-defaulted " +
                    "columns and indexes; never drop, rename or repurpose.");
            }

            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(text, @"ALTER\s+TABLE\s+\w+\s+DROP\s+COLUMN", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                $"Migration step v{i + 1} drops a column. Schema steps are additive-only (.agents/decisions.md 2026-09-04).");
        }
    }

    private static void Execute(SqliteConnectionFactory factory, string sql)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int ReadUserVersion(SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static bool TableExists(SqliteConnectionFactory factory, string table)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }
}
