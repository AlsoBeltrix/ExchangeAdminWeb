using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
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
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
        File.WriteAllText(Path.Combine(_tempDir, "config", "module-config.json"), "{not json");
        var moduleConfig = CreateModuleConfig();
        var config = new ConfigurationBuilder().Build();

        var resolved = MigrationTargetDatabaseSelector.Resolve(moduleConfig, config);

        Assert.Empty(resolved);
    }

    private ModuleConfigService CreateModuleConfig()
    {
        Directory.CreateDirectory(_tempDir);
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        return new ModuleConfigService(new ModuleCatalog(), env, Substitute.For<ILogger<ModuleConfigService>>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
