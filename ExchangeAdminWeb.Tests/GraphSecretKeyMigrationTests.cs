using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
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

    // ---- Dual-key modules: the old key is a LIVE field, not residue -------------------------
    //
    // The pre-existing on-prem case above uses ConferenceRooms, which declares no Graph key at
    // all, so it passed the ORIGINAL predicate trivially and never exercised this. A module
    // that is both an on-prem module and a Graph module is the one the predicate got wrong.

    [Fact]
    public void Migrate_DualKeyModule_KeepsItsOnPremSecret_WhenTheGraphKeyIsSet()
    {
        using var temp = new TempDir();
        var store = TestConfigStore.Create(temp.Path);
        new ModuleConfigRepository(store).SaveModule(DualKeyModule(),
            new Dictionary<string, string> { ["DelineaSecretId"] = "onprem-7", ["GraphDelineaSecretId"] = "graph-9" });

        var service = CreateService(temp.Path, store);
        var migrated = service.MigrateGraphSecretKeys();

        // Before the fix this deleted the on-prem row outright and copied it nowhere, because
        // the new key already held a value - so the AD credential reference was simply gone,
        // and the next disable failed its credential lookup.
        Assert.DoesNotContain(DualKeyModule(), migrated);
        Assert.Equal("onprem-7", service.GetValue(DualKeyModule(), "DelineaSecretId"));
        Assert.Equal("graph-9", service.GetValue(DualKeyModule(), "GraphDelineaSecretId"));
    }

    [Fact]
    public void Migrate_DualKeyModule_DoesNotPromoteTheOnPremSecret_IntoTheGraphSlot()
    {
        using var temp = new TempDir();
        var store = TestConfigStore.Create(temp.Path);
        new ModuleConfigRepository(store).SaveModule(DualKeyModule(),
            new Dictionary<string, string> { ["DelineaSecretId"] = "onprem-7" });

        var service = CreateService(temp.Path, store);
        service.MigrateGraphSecretKeys();

        // The worse half of the same defect: with the Graph slot empty the old behaviour MOVED
        // an on-prem AD credential reference into the cloud credential field. That is a
        // credential-isolation problem, not only a lost setting.
        Assert.Equal("onprem-7", service.GetValue(DualKeyModule(), "DelineaSecretId"));
        Assert.Null(service.GetValue(DualKeyModule(), "GraphDelineaSecretId"));
    }

    [Fact]
    public void Migrate_EveryDualKeyModuleInTheCatalog_IsSkipped()
    {
        // Catalog-driven on purpose. Naming EmergencyDisable would leave the next module that
        // grows a second credential field uncovered, which is exactly how this shipped.
        var dualKey = new ModuleCatalog().GetAll()
            .Where(m => m.ConfigFields.Any(f => f.Key == "DelineaSecretId")
                     && m.ConfigFields.Any(f => f.Key == "GraphDelineaSecretId"))
            .Select(m => m.Id)
            .ToList();

        Assert.NotEmpty(dualKey);

        using var temp = new TempDir();
        var store = TestConfigStore.Create(temp.Path);
        var repo = new ModuleConfigRepository(store);
        foreach (var id in dualKey)
            repo.SaveModule(id, new Dictionary<string, string> { ["DelineaSecretId"] = $"onprem-{id}" });

        var service = CreateService(temp.Path, store);
        var migrated = service.MigrateGraphSecretKeys();

        foreach (var id in dualKey)
        {
            Assert.DoesNotContain(id, migrated);
            Assert.Equal($"onprem-{id}", service.GetValue(id, "DelineaSecretId"));
        }
    }

    [Fact]
    public void Migrate_DualKeyModule_StaysConfigured_AcrossTheMigration()
    {
        // The operator-visible consequence: a module whose required credential row vanished
        // reports itself unconfigured and its page stops working.
        using var temp = new TempDir();
        var store = TestConfigStore.Create(temp.Path);
        var module = new ModuleCatalog().GetById(DualKeyModule())!;
        var values = module.ConfigFields.Where(f => f.Required).ToDictionary(f => f.Key, f => "set-" + f.Key);
        new ModuleConfigRepository(store).SaveModule(DualKeyModule(), values);

        var service = CreateService(temp.Path, store);
        Assert.True(service.IsModuleConfigured(DualKeyModule()));

        service.MigrateGraphSecretKeys();

        Assert.True(service.IsModuleConfigured(DualKeyModule()));
    }

    /// <summary>
    /// A catalog module declaring both credential keys. Resolved from the catalog rather than
    /// hard-coded so these tests follow the real descriptors.
    /// </summary>
    private static string DualKeyModule() =>
        new ModuleCatalog().GetAll()
            .First(m => m.ConfigFields.Any(f => f.Key == "DelineaSecretId")
                     && m.ConfigFields.Any(f => f.Key == "GraphDelineaSecretId"))
            .Id;

    private static ModuleConfigService CreateService(string contentRoot, IConfigStore store)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(contentRoot);
        return new ModuleConfigService(new ModuleCatalog(), env,
            new ModuleConfigRepository(store), Substitute.For<ILogger<ModuleConfigService>>());
    }

    // ---- Another writer between the decision and the write (aud-a33c) -------------------------
    //
    // Dev and prod share this database, and the remap decided what to copy from a read made
    // before its write transaction opened. The decorator below opens exactly that window: it
    // runs "the other instance" - a second store over its own connection factory on the same
    // file - immediately before handing the remap's write to the real store.

    private sealed class WriteWindowStore : IConfigStore
    {
        private readonly IConfigStore _inner;

        public WriteWindowStore(IConfigStore inner) => _inner = inner;

        /// <summary>Runs once, just before the next write reaches the real store.</summary>
        public Action? BeforeNextWrite { get; set; }

        public bool Opened { get; private set; }

        public long GetChangeToken() => _inner.GetChangeToken();

        public T Read<T>(Func<SqliteConnection, T> read) => _inner.Read(read);

        public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write)
        {
            OpenWindow();
            return _inner.Write(write);
        }

        public void Write(Action<SqliteConnection, SqliteTransaction> write)
        {
            OpenWindow();
            _inner.Write(write);
        }

        private void OpenWindow()
        {
            var during = BeforeNextWrite;
            BeforeNextWrite = null;
            if (during is null)
                return;
            during();
            Opened = true;
        }
    }

    private static ModuleConfigRepository OtherInstance(TempDir temp) =>
        new(new SqliteConfigStore(new SqliteConnectionFactory(temp.DbPath)));

    [Fact]
    public void Migrate_DoesNotRevertAGraphSecretSavedMeanwhile_WhenTheStaleRowIsStillThere()
    {
        // The finding. Prod's config page loaded MfaReset while the old key was still stranded,
        // so its save carries that row along and sets the new key. This remap had already
        // decided, from its own earlier read, to copy the old value into the empty new key - and
        // wrote it over the administrator's save.
        using var temp = new TempDir();
        var store = TestConfigStore.Create(temp.Path);
        new ModuleConfigRepository(store).SaveModule("MfaReset",
            new Dictionary<string, string> { ["DelineaSecretId"] = "123" });

        var window = new WriteWindowStore(store)
        {
            BeforeNextWrite = () => OtherInstance(temp).SaveModule("MfaReset",
                new Dictionary<string, string> { ["DelineaSecretId"] = "123", ["GraphDelineaSecretId"] = "saved-on-prod" })
        };
        var migrated = CreateService(temp.Path, window).MigrateGraphSecretKeys();

        Assert.True(window.Opened, "The other instance never wrote inside the window - this test proves nothing.");
        var after = new ModuleConfigRepository(store).GetModule("MfaReset");
        Assert.Equal("saved-on-prod", after["GraphDelineaSecretId"]);
        Assert.False(after.ContainsKey("DelineaSecretId"));
        Assert.Contains("MfaReset", migrated); // the stale row was still there, and removing it is a change
    }

    [Fact]
    public void Migrate_DoesNotResurrectAMovedSecret_WhenTheOldRowIsGone()
    {
        // The other shape. By the time this remap's write runs the old row has gone - the other
        // instance's own remap moved it, and the Graph secret has since been rotated. Acting on
        // the earlier read copies the stale old value over the rotation.
        using var temp = new TempDir();
        var store = TestConfigStore.Create(temp.Path);
        new ModuleConfigRepository(store).SaveModule("MfaReset",
            new Dictionary<string, string> { ["DelineaSecretId"] = "123" });

        var window = new WriteWindowStore(store)
        {
            BeforeNextWrite = () => OtherInstance(temp).SaveModule("MfaReset",
                new Dictionary<string, string> { ["GraphDelineaSecretId"] = "rotated-456" })
        };
        var migrated = CreateService(temp.Path, window).MigrateGraphSecretKeys();

        Assert.True(window.Opened, "The other instance never wrote inside the window - this test proves nothing.");
        var after = new ModuleConfigRepository(store).GetModule("MfaReset");
        Assert.Equal("rotated-456", after["GraphDelineaSecretId"]);
        Assert.False(after.ContainsKey("DelineaSecretId"));
        Assert.DoesNotContain("MfaReset", migrated);
    }
}
