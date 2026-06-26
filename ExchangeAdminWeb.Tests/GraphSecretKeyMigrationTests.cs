using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Covers the one-time remap of the renamed Graph credential key
/// (DelineaSecretId -> GraphDelineaSecretId). See docs/GraphSecretKeyMigration-Plan.md.
/// </summary>
public class GraphSecretKeyMigrationTests
{
    [Fact]
    public void Migrate_MovesStrandedValue_ToNewKey()
    {
        using var temp = new TempDir();
        var store = TestConfigStore.Create(temp.Path);
        new ModuleConfigRepository(store).SaveModule("MfaReset",
            new Dictionary<string, string> { ["DelineaSecretId"] = "123" });

        var service = CreateService(temp.Path, store);
        var migrated = service.MigrateGraphSecretKeys();

        Assert.Contains("MfaReset", migrated);
        Assert.Equal("123", service.GetValue("MfaReset", "GraphDelineaSecretId"));
        Assert.Null(service.GetValue("MfaReset", "DelineaSecretId"));
    }

    [Fact]
    public void Migrate_DoesNotOverwrite_ExistingNewKeyValue()
    {
        using var temp = new TempDir();
        var store = TestConfigStore.Create(temp.Path);
        new ModuleConfigRepository(store).SaveModule("MfaReset",
            new Dictionary<string, string> { ["DelineaSecretId"] = "old", ["GraphDelineaSecretId"] = "new" });

        var service = CreateService(temp.Path, store);
        service.MigrateGraphSecretKeys();

        // New key wins; the dead old-key row is cleaned up, no data lost.
        Assert.Equal("new", service.GetValue("MfaReset", "GraphDelineaSecretId"));
        Assert.Null(service.GetValue("MfaReset", "DelineaSecretId"));
    }

    [Fact]
    public void Migrate_DoesNotTouch_OnPremModules()
    {
        using var temp = new TempDir();
        var store = TestConfigStore.Create(temp.Path);
        // ConferenceRooms legitimately uses DelineaSecretId as its CURRENT key.
        new ModuleConfigRepository(store).SaveModule("ConferenceRooms",
            new Dictionary<string, string> { ["DelineaSecretId"] = "onprem-1" });

        var service = CreateService(temp.Path, store);
        var migrated = service.MigrateGraphSecretKeys();

        Assert.DoesNotContain("ConferenceRooms", migrated);
        Assert.Equal("onprem-1", service.GetValue("ConferenceRooms", "DelineaSecretId"));
        Assert.Null(service.GetValue("ConferenceRooms", "GraphDelineaSecretId"));
    }

    [Fact]
    public void Migrate_IsIdempotent_SecondRunIsNoOp()
    {
        using var temp = new TempDir();
        var store = TestConfigStore.Create(temp.Path);
        new ModuleConfigRepository(store).SaveModule("MfaReset",
            new Dictionary<string, string> { ["DelineaSecretId"] = "123" });

        var service = CreateService(temp.Path, store);
        service.MigrateGraphSecretKeys(); // first run performs the write

        var tokenBefore = store.GetChangeToken();
        var migrated = service.MigrateGraphSecretKeys(); // nothing stranded anymore
        var tokenAfter = store.GetChangeToken();

        Assert.Empty(migrated);
        Assert.Equal(tokenBefore, tokenAfter); // a no-op must not bump the change token
        Assert.Equal("123", service.GetValue("MfaReset", "GraphDelineaSecretId"));
    }

    private static ModuleConfigService CreateService(string contentRoot, SqliteConfigStore store)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(contentRoot);
        return new ModuleConfigService(new ModuleCatalog(), env,
            new ModuleConfigRepository(store), Substitute.For<ILogger<ModuleConfigService>>());
    }
}
