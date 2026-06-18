using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class ModuleConfigServiceTests
{
    [Fact]
    public void SaveAndGet_RoundTripsValues()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);

        service.SaveModuleConfig("Migration", new Dictionary<string, string>
        {
            ["OnPremTargetDatabases"] = "db-a,db-b",
        });

        Assert.Equal("db-a,db-b", service.GetValue("Migration", "OnPremTargetDatabases"));
        Assert.Equal("db-a,db-b", service.GetModuleConfig("Migration")["OnPremTargetDatabases"]);
    }

    [Fact]
    public void GetValue_UnknownKey_ReturnsNull()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);

        Assert.Null(service.GetValue("Migration", "Nope"));
    }

    [Fact]
    public void SaveModuleConfig_OverwritesWholeModule()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);

        service.SaveModuleConfig("Migration", new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        service.SaveModuleConfig("Migration", new Dictionary<string, string> { ["a"] = "9" });

        var config = service.GetModuleConfig("Migration");
        Assert.Equal("9", config["a"]);
        Assert.False(config.ContainsKey("b"));
    }

    [Fact]
    public void Key_IsCaseInsensitive()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);

        service.SaveModuleConfig("Migration", new Dictionary<string, string> { ["AppId"] = "x" });

        Assert.Equal("x", service.GetValue("Migration", "appid"));
    }

    [Fact]
    public void ConfigSaved_EventFires_OnSave()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);
        string? saved = null;
        service.ConfigSaved += id => saved = id;

        service.SaveModuleConfig("Migration", new Dictionary<string, string> { ["a"] = "1" });

        Assert.Equal("Migration", saved);
    }

    [Fact]
    public void HasModuleConfigFile_TrueOnlyAfterSave()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);

        Assert.False(service.HasModuleConfigFile("Migration"));
        service.SaveModuleConfig("Migration", new Dictionary<string, string> { ["a"] = "1" });
        Assert.True(service.HasModuleConfigFile("Migration"));
    }

    [Fact]
    public void HasModuleConfigFile_TrueAfterEmptySave_SuppressesFallback()
    {
        // Parity with the file world: an explicitly-saved EMPTY config still counts as
        // "configured" (presence marker), so consumers that suppress the appsettings fallback
        // when HasModuleConfigFile is true keep doing so. Without this, an empty save would
        // silently re-enable the legacy fallback (B.3 review finding).
        using var temp = new TempDir();
        var service = CreateService(temp.Path);

        service.SaveModuleConfig("Migration", new Dictionary<string, string>());

        Assert.True(service.HasModuleConfigFile("Migration"));
        Assert.Empty(service.GetModuleConfig("Migration"));
    }

    [Fact]
    public void IsModuleCorrupt_TrueWhenStoreUnreadable_ElseFalse()
    {
        using var temp = new TempDir();
        var healthy = CreateService(temp.Path);
        Assert.False(healthy.IsModuleCorrupt("Migration"));

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(temp.Path);
        var broken = new ModuleConfigService(new ModuleCatalog(), env,
            new ModuleConfigRepository(new ThrowingStore()), Substitute.For<ILogger<ModuleConfigService>>());
        Assert.True(broken.IsModuleCorrupt("Migration"));
    }

    [Fact]
    public void Construction_ImportsPerModuleLegacyFile_ThenArchives()
    {
        using var temp = new TempDir();
        var configDir = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(configDir);
        var legacy = Path.Combine(configDir, "module-config-Migration.json");
        File.WriteAllText(legacy, "{\"OnPremTargetDatabases\":\"db-legacy\"}");

        var service = CreateService(temp.Path);

        Assert.Equal("db-legacy", service.GetValue("Migration", "OnPremTargetDatabases"));
        Assert.False(File.Exists(legacy));
        Assert.Single(Directory.GetFiles(configDir, "module-config-Migration.json.imported-*"));
    }

    [Fact]
    public void Construction_ImportsOldSingleFileShape()
    {
        using var temp = new TempDir();
        var configDir = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "module-config.json"),
            "{\"Migration\":{\"OnPremTargetDatabases\":\"db-old\"},\"Comms10k\":{\"k\":\"v\"}}");

        var service = CreateService(temp.Path);

        Assert.Equal("db-old", service.GetValue("Migration", "OnPremTargetDatabases"));
        Assert.Equal("v", service.GetValue("Comms10k", "k"));
    }

    [Fact]
    public void Construction_DbValueWins_OverLegacyFile()
    {
        using var temp = new TempDir();
        var configDir = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(configDir);

        // DB pre-seeded for Migration.
        var store = TestConfigStore.Create(temp.Path);
        new ModuleConfigRepository(store).SaveModule("Migration",
            new Dictionary<string, string> { ["OnPremTargetDatabases"] = "db-from-db" });

        // Legacy file with a different value.
        File.WriteAllText(Path.Combine(configDir, "module-config-Migration.json"),
            "{\"OnPremTargetDatabases\":\"db-from-file\"}");

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(temp.Path);
        var service = new ModuleConfigService(new ModuleCatalog(), env,
            new ModuleConfigRepository(store), Substitute.For<ILogger<ModuleConfigService>>());

        Assert.Equal("db-from-db", service.GetValue("Migration", "OnPremTargetDatabases"));
    }

    private static ModuleConfigService CreateService(string contentRoot)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(contentRoot);
        return new ModuleConfigService(new ModuleCatalog(), env,
            TestConfigStore.CreateModuleConfig(contentRoot), Substitute.For<ILogger<ModuleConfigService>>());
    }

    private sealed class ThrowingStore : IConfigStore
    {
        public long GetChangeToken() => throw new InvalidOperationException();
        public T Read<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> read) => throw new InvalidOperationException();
        public T Write<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, T> write) => throw new InvalidOperationException();
        public void Write(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction> write) => throw new InvalidOperationException();
    }
}
