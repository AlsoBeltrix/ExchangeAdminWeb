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
            "schema_meta", "module_enablement", "module_config", "section_access",
            "module_admins", "protected_principal", "editable_attribute",
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

    private static bool TableExists(SqliteConnectionFactory factory, string table)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }
}
