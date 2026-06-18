using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class MigrationTargetDatabaseSelectorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ExchangeAdminWebTests_{Guid.NewGuid():N}");

    [Fact]
    public void Parse_TrimsSplitsAndDeduplicatesDatabaseNames()
    {
        var parsed = MigrationTargetDatabaseSelector.Parse(" db1,db2; db1\r\ndb3 ");

        Assert.Equal(["db1", "db2", "db3"], parsed);
    }

    [Fact]
    public void Resolve_ReturnsEmptyWhenNothingConfigured()
    {
        var moduleConfig = CreateModuleConfig();
        var config = new ConfigurationBuilder().Build();

        var resolved = MigrationTargetDatabaseSelector.Resolve(moduleConfig, config);

        Assert.Empty(resolved);
    }

    [Fact]
    public void Resolve_UsesModuleConfigWhenPresent()
    {
        var moduleConfig = CreateModuleConfig();
        moduleConfig.SaveModuleConfig("Migration", new Dictionary<string, string>
        {
            ["OnPremTargetDatabases"] = "db-a, db-b"
        });
        var config = new ConfigurationBuilder().Build();

        var resolved = MigrationTargetDatabaseSelector.Resolve(moduleConfig, config);

        Assert.Equal(["db-a", "db-b"], resolved);
    }

    [Fact]
    public void Resolve_FailsClosedWhenModuleConfigIsCorrupt()
    {
        // Post-SQLite, "corrupt" means the config store cannot be read (a DB-integrity
        // failure), not an unparseable JSON file. Simulate an unreadable store and assert the
        // selector still fails closed (returns empty) rather than falling through.
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        Directory.CreateDirectory(_tempDir);
        var moduleConfig = new ModuleConfigService(
            new ModuleCatalog(), env, new ModuleConfigRepository(new UnreadableConfigStore()),
            Substitute.For<ILogger<ModuleConfigService>>());
        var config = new ConfigurationBuilder().Build();

        var resolved = MigrationTargetDatabaseSelector.Resolve(moduleConfig, config);

        Assert.Empty(resolved);
    }

    private ModuleConfigService CreateModuleConfig()
    {
        Directory.CreateDirectory(_tempDir);
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        return new ModuleConfigService(new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_tempDir), Substitute.For<ILogger<ModuleConfigService>>());
    }

    // A config store whose reads always throw, standing in for a corrupt/unopenable DB so the
    // corrupt-store guard (IsModuleCorrupt) trips.
    private sealed class UnreadableConfigStore : ExchangeAdminWeb.Services.Storage.IConfigStore
    {
        public long GetChangeToken() => throw new InvalidOperationException("store unreadable");
        public T Read<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> read) => throw new InvalidOperationException("store unreadable");
        public T Write<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, T> write) => throw new InvalidOperationException("store unreadable");
        public void Write(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction> write) => throw new InvalidOperationException("store unreadable");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
