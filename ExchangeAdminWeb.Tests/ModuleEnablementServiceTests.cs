using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class ModuleEnablementServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ModuleCatalog _catalog;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ModuleEnablementService> _logger;
    private readonly ModuleConfigService _moduleConfig;
    private readonly IConfiguration _config;
    private readonly SqliteConfigStore _store;
    private readonly ModuleEnablementRepository _repository;

    public ModuleEnablementServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"moduleenablement_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _catalog = new ModuleCatalog();

        _env = Substitute.For<IWebHostEnvironment>();
        _env.ContentRootPath.Returns(_tempDir);

        _logger = Substitute.For<ILogger<ModuleEnablementService>>();

        var moduleConfigLogger = Substitute.For<ILogger<ModuleConfigService>>();
        _moduleConfig = new ModuleConfigService(_catalog, _env, TestConfigStore.CreateModuleConfig(_tempDir), moduleConfigLogger);
        _config = new ConfigurationBuilder().Build();

        // One shared store/DB for the whole test so seeded state is visible to the service.
        _store = TestConfigStore.Create(_tempDir);
        _repository = new ModuleEnablementRepository(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { }
    }

    private ModuleEnablementService CreateService(IConfigStore? store = null)
    {
        return new ModuleEnablementService(_catalog, _env, _moduleConfig,
            new ModuleEnablementRepository(store ?? _store), _config, _logger);
    }

    // Seeds enablement state into the shared store (the DB analogue of writing the file).
    private void SeedEnablement(Dictionary<string, bool> state) => _repository.SaveAll(state);

    // A store whose reads throw - the DB-integrity analogue of an unparseable file.
    private sealed class UnreadableStore : IConfigStore
    {
        public long GetChangeToken() => throw new InvalidOperationException("store unreadable");
        public T Read<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> read) => throw new InvalidOperationException("store unreadable");
        public T Write<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, T> write) => throw new InvalidOperationException("store unreadable");
        public void Write(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction> write) => throw new InvalidOperationException("store unreadable");
    }

    [Fact]
    public void IsModuleEnabled_SystemModule_ReturnsTrue_RegardlessOfFileState()
    {
        // System module should always be enabled even with no file
        var service = CreateService();
        Assert.True(service.IsModuleEnabled("AdminSettings"));
    }

    [Fact]
    public void IsModuleRawEnabled_NoFileExists_ReturnsEnabledByDefault()
    {
        var service = CreateService();

        // MailboxPermissions has EnabledByDefault = true
        Assert.True(service.IsModuleRawEnabled("MailboxPermissions"));
    }

    [Fact]
    public void IsModuleEnabled_NoFileExists_ChildDisabledWhenParentDisabled()
    {
        var service = CreateService();

        // MailboxPermissions has EnabledByDefault = true but DependsOn = ExchangeOnline
        // ExchangeOnline has EnabledByDefault = false, so effective = false
        Assert.False(service.IsModuleEnabled("MailboxPermissions"));
    }

    [Fact]
    public void IsModuleEnabled_FileExists_ReadsStateFromFile()
    {
        var state = new Dictionary<string, bool>
        {
            ["ExchangeOnline"] = true,
            ["MailboxPermissions"] = false,
            ["Migration"] = true
        };
        SeedEnablement(state);

        var service = CreateService();

        Assert.False(service.IsModuleEnabled("MailboxPermissions"));
        Assert.True(service.IsModuleEnabled("Migration"));
    }

    [Fact]
    public void IsModuleEnabled_CorruptStore_ReturnsFalseForAllNonSystemModules()
    {
        // Unreadable store == the file-world "corrupt file": fail closed, all non-system off.
        var service = CreateService(new UnreadableStore());

        foreach (var module in _catalog.GetAll().Where(m => !m.IsSystemModule))
        {
            Assert.False(service.IsModuleEnabled(module.Id),
                $"Expected module '{module.Id}' to be disabled when store is corrupt");
        }
    }

    [Fact]
    public void GetAllEnablement_ReturnsAllModulesWithCorrectState()
    {
        var state = new Dictionary<string, bool>
        {
            ["MailboxPermissions"] = false,
            ["Migration"] = true,
            ["MessageTrace"] = false
        };
        SeedEnablement(state);

        var service = CreateService();
        var result = service.GetAllEnablement();

        // Should contain all modules
        Assert.Equal(_catalog.GetAll().Count, result.Count);

        // System modules always enabled
        Assert.True(result["AdminSettings"]);
        Assert.True(result["AdminEventLog"]);

        // Non-system modules follow file state
        Assert.False(result["MailboxPermissions"]);
        Assert.True(result["Migration"]);
        Assert.False(result["MessageTrace"]);

        // Modules not in file use EnabledByDefault
        Assert.True(result["CalendarPermissions"]);
    }

    [Fact]
    public void SaveEnablement_WritesValidJson_ThatCanBeReadBack()
    {
        var service = CreateService();
        var enablement = new Dictionary<string, bool>
        {
            ["MailboxPermissions"] = true,
            ["CalendarPermissions"] = false,
            ["Migration"] = true,
            ["DelegationReport"] = false,
            ["MessageTrace"] = true,
            ["RecipientLookup"] = false,
            ["OutOfOffice"] = true
        };

        service.SaveEnablement(enablement);

        // Read back from the store and verify it round-trips.
        Assert.True(_repository.TryGetAll(out var readBack));
        Assert.False(readBack["CalendarPermissions"]);
        Assert.True(readBack["MailboxPermissions"]);
    }

    [Fact]
    public void SaveEnablement_SecondSave_OverwritesFirst()
    {
        var service = CreateService();

        var first = new Dictionary<string, bool>
        {
            ["MailboxPermissions"] = true,
            ["CalendarPermissions"] = true
        };
        service.SaveEnablement(first);
        Assert.True(_repository.TryGetAll(out var afterFirst));
        Assert.True(afterFirst["MailboxPermissions"]);

        var second = new Dictionary<string, bool>
        {
            ["MailboxPermissions"] = false,
            ["CalendarPermissions"] = false
        };
        service.SaveEnablement(second);

        Assert.True(_repository.TryGetAll(out var readBack));
        Assert.False(readBack["MailboxPermissions"]);
        Assert.False(readBack["CalendarPermissions"]);
    }

    [Fact]
    public void SystemModules_AlwaysReportEnabled_EvenIfFileExplicitlySaysFalse()
    {
        // Write a file that explicitly sets system module to false
        var state = new Dictionary<string, bool>
        {
            ["AdminSettings"] = false,
            ["AdminEventLog"] = false
        };
        SeedEnablement(state);

        var service = CreateService();

        // AdminSettings is a system module - always enabled regardless of file
        Assert.True(service.IsModuleEnabled("AdminSettings"));
        // AdminEventLog is no longer a system module - respects the file setting
        Assert.False(service.IsModuleEnabled("AdminEventLog"));
    }

    [Fact]
    public void DisableModule_SaveThenRead_ReturnsFalse()
    {
        var service = CreateService();

        // Save with MailboxPermissions disabled
        var enablement = new Dictionary<string, bool>
        {
            ["MailboxPermissions"] = false,
            ["CalendarPermissions"] = true,
            ["Migration"] = true,
            ["DelegationReport"] = true,
            ["MessageTrace"] = true,
            ["RecipientLookup"] = true,
            ["OutOfOffice"] = true
        };
        service.SaveEnablement(enablement);

        // Create a new service instance to read from file (no caching)
        var service2 = CreateService();
        Assert.False(service2.IsModuleEnabled("MailboxPermissions"));
    }

    // --- Parent/child cascade tests ---

    [Fact]
    public void IsModuleEnabled_ParentOffChildRawOn_EffectiveFalse()
    {
        var state = new Dictionary<string, bool>
        {
            ["ExchangeOnline"] = false,
            ["MailboxPermissions"] = true
        };
        SeedEnablement(state);

        var service = CreateService();

        Assert.True(service.IsModuleRawEnabled("MailboxPermissions"));
        Assert.False(service.IsModuleEnabled("MailboxPermissions"));
    }

    [Fact]
    public void IsModuleEnabled_ParentOnChildRawOn_EffectiveTrue()
    {
        var state = new Dictionary<string, bool>
        {
            ["ExchangeOnline"] = true,
            ["MailboxPermissions"] = true
        };
        SeedEnablement(state);

        var service = CreateService();

        Assert.True(service.IsModuleRawEnabled("MailboxPermissions"));
        Assert.True(service.IsModuleEnabled("MailboxPermissions"));
    }

    [Fact]
    public void IsModuleEnabled_ParentOnChildRawOff_EffectiveFalse()
    {
        var state = new Dictionary<string, bool>
        {
            ["ExchangeOnline"] = true,
            ["MailboxPermissions"] = false
        };
        SeedEnablement(state);

        var service = CreateService();

        Assert.False(service.IsModuleRawEnabled("MailboxPermissions"));
        Assert.False(service.IsModuleEnabled("MailboxPermissions"));
    }

    [Fact]
    public void RawChildState_SurvivesParentDisableEnableCycle()
    {
        // Start with both enabled
        var state = new Dictionary<string, bool>
        {
            ["ExchangeOnline"] = true,
            ["MailboxPermissions"] = true
        };
        SeedEnablement(state);

        var service = CreateService();
        Assert.True(service.IsModuleEnabled("MailboxPermissions"));

        // Disable parent
        state["ExchangeOnline"] = false;
        SeedEnablement(state);

        var service2 = CreateService();
        Assert.False(service2.IsModuleEnabled("MailboxPermissions"));
        Assert.True(service2.IsModuleRawEnabled("MailboxPermissions")); // raw state preserved

        // Re-enable parent
        state["ExchangeOnline"] = true;
        SeedEnablement(state);

        var service3 = CreateService();
        Assert.True(service3.IsModuleEnabled("MailboxPermissions")); // child reactivated
    }

    [Fact]
    public void IsModuleEnabled_IndependentModule_NotAffectedByExchangeOnline()
    {
        var state = new Dictionary<string, bool>
        {
            ["ExchangeOnline"] = false,
            ["MfaReset"] = true
        };
        SeedEnablement(state);

        var service = CreateService();

        // MfaReset has no DependsOn, so it should be enabled regardless of ExchangeOnline
        Assert.True(service.IsModuleEnabled("MfaReset"));
    }

    // --- Startup must never write enablement state (incident 2026-06-12, fix #1) ---
    // Enablement is written ONLY by SaveEnablement from Admin Settings.

    [Fact]
    public void Startup_NoState_ReadsDoNotWriteAnything()
    {
        var tokenBefore = _store.GetChangeToken();
        var service = CreateService();

        service.GetAllEnablement();
        service.IsModuleRawEnabled("MailboxPermissions");

        // No write means the change token is unchanged and no rows were created.
        // (Seeding is a SEPARATE explicit call - SeedMissingModules - not part of construction
        // or reads, so construction+reads must still write nothing.)
        Assert.Equal(tokenBefore, _store.GetChangeToken());
        Assert.False(_repository.HasAny());
    }

    // --- Phase C: startup self-registration (non-destructive seeding) ---

    [Fact]
    public void SeedMissingModules_EmptyStore_SeedsEveryNonSystemModuleAtDefault()
    {
        var service = CreateService();

        var seeded = service.SeedMissingModules();

        Assert.True(_repository.TryGetAll(out var state));
        foreach (var module in _catalog.GetAll().Where(m => !m.IsSystemModule))
        {
            Assert.True(state.ContainsKey(module.Id), $"{module.Id} should be seeded");
            Assert.Equal(module.EnabledByDefault, state[module.Id]);
            Assert.Contains(module.Id, seeded);
        }
        // System modules are always-on and not stored.
        Assert.DoesNotContain("AdminSettings", state.Keys);
    }

    [Fact]
    public void SeedMissingModules_DoesNotOverwriteExistingRows()
    {
        // The 2026-06-12 incident regression guard: seeding must NEVER flip an existing value.
        // ExchangeOnline defaults to false; set it true, then seed and confirm it stays true.
        SeedEnablement(new Dictionary<string, bool> { ["ExchangeOnline"] = true });
        var service = CreateService();

        var seeded = service.SeedMissingModules();

        Assert.True(_repository.TryGetAll(out var state));
        Assert.True(state["ExchangeOnline"], "existing ExchangeOnline=true must NOT be overwritten by seeding");
        Assert.DoesNotContain("ExchangeOnline", seeded); // not newly seeded
        // But a module with no row should have been added.
        Assert.Contains("MailboxPermissions", seeded);
    }

    [Fact]
    public void SeedMissingModules_IsIdempotent()
    {
        var service = CreateService();

        var first = service.SeedMissingModules();
        Assert.NotEmpty(first);

        var tokenAfterFirst = _store.GetChangeToken();
        var second = service.SeedMissingModules();

        Assert.Empty(second); // nothing left to seed
        Assert.Equal(tokenAfterFirst, _store.GetChangeToken()); // no write on the second run
    }

    [Fact]
    public void SeedMissingModules_CorruptLegacyFile_DoesNotSeed_StaysFailClosed()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "modules-enabled.json"), "{ not valid json");

        var service = CreateService();
        var seeded = service.SeedMissingModules();

        // Must not seed over a corrupt store, and must not have written anything.
        Assert.Empty(seeded);
        Assert.False(_repository.HasAny());
        Assert.True(service.IsStoreCorrupt());
    }

    [Fact]
    public void SeedMissingModules_UnreadableStore_DoesNotThrow_DoesNotSeed()
    {
        var service = CreateService(new UnreadableStore());

        var seeded = service.SeedMissingModules();

        Assert.Empty(seeded); // fail-safe: no throw, no seed
    }

    [Fact]
    public void SeedMissingModules_WriteFails_DoesNotThrow_DoesNotAbortStartup()
    {
        // Store reads fine (so TryGetAll passes) but the seed WRITE throws (read-only ACL /
        // exclusive lock). Seeding is non-essential and must not abort startup.
        var service = CreateService(new WriteFailsStore());

        var seeded = service.SeedMissingModules();

        Assert.Empty(seeded); // logged and swallowed, not thrown
    }

    // Reads succeed but writes throw - simulates a readable-but-unwritable store at startup.
    private sealed class WriteFailsStore : IConfigStore
    {
        private readonly SqliteConfigStore _inner;
        public WriteFailsStore() => _inner = TestConfigStore.Create(Path.Combine(Path.GetTempPath(), $"mewfs_{Guid.NewGuid():N}"));
        public long GetChangeToken() => _inner.GetChangeToken();
        public T Read<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> read) => _inner.Read(read);
        public T Write<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, T> write) => throw new InvalidOperationException("store busy");
        public void Write(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction> write) => throw new InvalidOperationException("store busy");
    }

    [Fact]
    public void Startup_MissingExchangeOnlineKey_NoExoConfig_DefaultsFalse_AndWritesNothing()
    {
        SeedEnablement(new Dictionary<string, bool> { ["MailboxPermissions"] = true });
        var tokenBefore = _store.GetChangeToken();

        var service = CreateService();
        var result = service.GetAllEnablement();

        Assert.False(result["ExchangeOnline"]); // EnabledByDefault = false
        Assert.Equal(tokenBefore, _store.GetChangeToken());
    }

    [Fact]
    public void Startup_MissingExchangeOnlineKey_WithExoConfig_DoesNotAutoEnable_AndWritesNothing()
    {
        SeedEnablement(new Dictionary<string, bool> { ["MailboxPermissions"] = true });
        _moduleConfig.SaveModuleConfig("ExchangeOnline",
            new Dictionary<string, string> { ["AppId"] = "00000000-0000-0000-0000-000000000001" });
        var tokenBefore = _store.GetChangeToken();

        var service = CreateService();
        var result = service.GetAllEnablement();

        Assert.False(result["ExchangeOnline"]); // no auto-enable: an admin must enable it explicitly
        Assert.Equal(tokenBefore, _store.GetChangeToken());
    }

    [Fact]
    public void Startup_CorruptStore_ReadsDoNotWrite()
    {
        // An unreadable store must not be written to by reads (no blind rewrite).
        var unreadable = new UnreadableStore();
        var service = CreateService(unreadable);

        // Reads against the corrupt store fail closed without throwing.
        service.GetAllEnablement();
        Assert.False(service.IsModuleRawEnabled("MailboxPermissions"));
    }

    // --- Corrupt-store probe (blank-render-save trap, incident fix #3) ---
    // Admin pages refuse to render/save enablement when this probe flags the store:
    // the read fallback is all-disabled, and saving it would persist that state.

    [Fact]
    public void IsStoreCorrupt_EmptyStore_ReturnsFalse()
    {
        Assert.False(CreateService().IsStoreCorrupt());
    }

    [Fact]
    public void IsStoreCorrupt_PopulatedStore_ReturnsFalse()
    {
        SeedEnablement(new Dictionary<string, bool> { ["ExchangeOnline"] = true });

        Assert.False(CreateService().IsStoreCorrupt());
    }

    [Fact]
    public void IsStoreCorrupt_UnreadableStore_ReturnsTrue()
    {
        Assert.True(CreateService(new UnreadableStore()).IsStoreCorrupt());
    }

    [Fact]
    public void Construction_ImportsLegacyFile_ThenArchives()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var legacy = Path.Combine(configDir, "modules-enabled.json");
        File.WriteAllText(legacy, """{ "Migration": true, "MailboxPermissions": false }""");

        var service = CreateService();

        Assert.True(_repository.TryGetAll(out var state));
        Assert.True(state["Migration"]);
        Assert.False(state["MailboxPermissions"]);
        Assert.False(File.Exists(legacy));
        Assert.Single(Directory.GetFiles(configDir, "modules-enabled.json.imported-*"));
    }

    [Fact]
    public void Construction_DbValueWins_OverLegacyFile()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        SeedEnablement(new Dictionary<string, bool> { ["Migration"] = false }); // DB says false
        File.WriteAllText(Path.Combine(configDir, "modules-enabled.json"),
            """{ "Migration": true }"""); // file says true

        CreateService(); // triggers import

        Assert.True(_repository.TryGetAll(out var state));
        Assert.False(state["Migration"]); // DB wins
    }

    [Fact]
    public void Construction_UnparseableLegacyFile_LeftInPlace_NotImported_AndFailsClosed()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var legacy = Path.Combine(configDir, "modules-enabled.json");
        File.WriteAllText(legacy, "{ not valid json");

        var service = CreateService();

        // A corrupt legacy file must not be silently discarded...
        Assert.True(File.Exists(legacy));
        Assert.Empty(Directory.GetFiles(configDir, "modules-enabled.json.imported-*"));

        // ...and the upgrade window must stay FAIL-CLOSED: store reported corrupt, every
        // non-system module disabled, NOT fallen back to EnabledByDefault against an empty DB.
        Assert.True(service.IsStoreCorrupt());
        foreach (var module in _catalog.GetAll().Where(m => !m.IsSystemModule))
            Assert.False(service.IsModuleEnabled(module.Id),
                $"Expected '{module.Id}' disabled while legacy file is corrupt");
    }

    [Fact]
    public void Construction_ValidLegacyFileButImportFails_FailsClosed()
    {
        // A valid legacy file that parses but cannot be committed to the store (e.g. SQLite busy)
        // must fail closed (all non-system modules disabled), NOT fall through to a readable-but-
        // empty store that lets modules default to EnabledByDefault. Mirrors the unparseable case
        // and the sibling authorization stores.
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var legacy = Path.Combine(configDir, "modules-enabled.json");
        File.WriteAllText(legacy, """{ "Migration": true, "MailboxPermissions": false }""");

        var service = CreateService(new WriteFailsStore());

        Assert.True(service.IsStoreCorrupt());
        foreach (var module in _catalog.GetAll().Where(m => !m.IsSystemModule))
            Assert.False(service.IsModuleEnabled(module.Id),
                $"Expected '{module.Id}' disabled while legacy import is failing");
        // File must remain for the next startup to retry (not archived).
        Assert.True(File.Exists(legacy));
        Assert.Empty(Directory.GetFiles(configDir, "modules-enabled.json.imported-*"));
    }

    [Fact]
    public void Startup_ExistingExchangeOnlineKey_PreservedAndNothingWritten()
    {
        SeedEnablement(new Dictionary<string, bool>
        {
            ["ExchangeOnline"] = true,
            ["MailboxPermissions"] = true
        });
        var tokenBefore = _store.GetChangeToken();

        var service = CreateService();
        var result = service.GetAllEnablement();

        Assert.True(result["ExchangeOnline"]);
        Assert.Equal(tokenBefore, _store.GetChangeToken());
    }
}
