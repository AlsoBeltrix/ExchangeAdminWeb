using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class SectionAccessServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configDir;
    private readonly string _legacyFilePath;
    private readonly SqliteConfigStore _store;
    private readonly SectionAccessRepository _repository;

    public SectionAccessServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sectionaccess_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(_configDir);
        _legacyFilePath = Path.Combine(_configDir, "sectionaccess.json");

        // One shared store/DB so seeded state is visible to the service under test.
        _store = TestConfigStore.Create(_tempDir);
        _repository = new SectionAccessRepository(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { }
    }

    private SectionAccessService CreateService(Dictionary<string, string?>? extraConfig = null, IConfigStore? store = null)
    {
        var configEntries = new Dictionary<string, string?>
        {
            ["Security:AllowedGroups:0"] = "AllUsersGroup",
            ["Security:AllowedGroups:1"] = "PowerUsersGroup",
            ["Security:AdminGroups:0"] = "AdminGroup"
        };

        if (extraConfig != null)
        {
            foreach (var kv in extraConfig)
                configEntries[kv.Key] = kv.Value;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configEntries)
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var logger = Substitute.For<ILogger<SectionAccessService>>();

        return new SectionAccessService(config, logger, env, new ExchangeAdminWeb.Modules.ModuleCatalog(),
            new SectionAccessRepository(store ?? _store));
    }

    // Seeds section access into the shared store (the DB analogue of writing the fragment file).
    private void SeedSectionAccess(Dictionary<string, string[]> sections) => _repository.SaveAll(sections);

    // A store whose reads throw — the DB-integrity analogue of an unreadable fragment.
    private sealed class UnreadableStore : IConfigStore
    {
        public long GetChangeToken() => throw new InvalidOperationException("store unreadable");
        public T Read<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> read) => throw new InvalidOperationException("store unreadable");
        public T Write<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, T> write) => throw new InvalidOperationException("store unreadable");
        public void Write(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction> write) => throw new InvalidOperationException("store unreadable");
    }

    [Fact]
    public void GetGroupsForSection_ConfiguredWithSection_ReturnsThoseGroups()
    {
        SeedSectionAccess(new() { ["MailboxPermissions"] = new[] { "GroupA", "GroupB" } });

        var service = CreateService();
        var result = service.GetGroupsForSection("MailboxPermissions");

        Assert.Equal(new[] { "GroupA", "GroupB" }, result);
    }

    [Fact]
    public void GetGroupsForSection_ConfiguredButSectionMissing_ReturnsEmptyArray()
    {
        SeedSectionAccess(new() { ["OtherSection"] = new[] { "GroupX" } });

        var service = CreateService();
        var result = service.GetGroupsForSection("MailboxPermissions");

        Assert.Empty(result);
    }

    [Fact]
    public void GetGroupsForSection_StoreUnreadable_ReturnsEmptyArray_FailsClosed()
    {
        // Configure the real store, then point the service at an unreadable one: it must fail
        // closed (empty), never the permissive fallback.
        SeedSectionAccess(new() { ["MailboxPermissions"] = new[] { "GroupA" } });
        var service = CreateService(store: new UnreadableStore());

        Assert.Empty(service.GetGroupsForSection("MailboxPermissions"));
    }

    [Fact]
    public void GetGroupsForSection_OutOfBandWrite_IsVisibleImmediately_NoStaleCache()
    {
        // The change-token model means every read is fresh: a write through a DIFFERENT
        // repository instance (the prod->dev refresh tool, a second writer) is seen at once,
        // without an app-pool restart. This replaces the old file-stamp staleness handling.
        var service = CreateService();
        Assert.Empty(service.GetGroupsForSection("MailboxPermissions")); // not configured yet

        // Out-of-band write via a separate repository over the same store.
        new SectionAccessRepository(_store).SaveAll(new Dictionary<string, string[]> { ["MailboxPermissions"] = new[] { "GroupA", "GroupB" } });

        Assert.Equal(new[] { "GroupA", "GroupB" }, service.GetGroupsForSection("MailboxPermissions"));
    }

    [Fact]
    public void GetGroupsForSection_NotConfiguredButLegacyAppSettings_ReturnsLegacyGroups()
    {
        var legacyConfig = new Dictionary<string, string?>
        {
            ["Security:SectionAccess:MailboxPermissions:0"] = "LegacyGroup1",
            ["Security:SectionAccess:MailboxPermissions:1"] = "LegacyGroup2"
        };

        var service = CreateService(legacyConfig);
        var result = service.GetGroupsForSection("MailboxPermissions");

        Assert.Equal(new[] { "LegacyGroup1", "LegacyGroup2" }, result);
    }

    [Fact]
    public void GetGroupsForSection_NeitherExists_ReadOnlySection_ReturnsAllowedGroups()
    {
        // Read-only modules (DelegationReport, RecipientLookup) are the only
        // sections still allowed to fall back to the global AllowedGroups.
        var service = CreateService();
        var result = service.GetGroupsForSection("DelegationReport");

        Assert.Equal(new[] { "AllUsersGroup", "PowerUsersGroup" }, result);
    }

    [Theory]
    [InlineData("MailboxPermissions")]
    [InlineData("CalendarPermissions")]
    [InlineData("MigrationCheck")]
    [InlineData("MigrationCreate")]
    [InlineData("MigrationManage")]
    [InlineData("OutOfOffice")]
    [InlineData("MailboxPermissionsOnPrem")]
    public void GetGroupsForSection_NeitherExists_MutatingSection_ReturnsEmpty(string section)
    {
        // Mutating modules are FailClosed: with no section-access config at all, they must deny
        // access, never fall back to the global AllowedGroups.
        var service = CreateService();
        var result = service.GetGroupsForSection(section);

        Assert.Empty(result);
    }

    [Fact]
    public void GetGroupsForSection_ConfiguredEmpty_MutatingSection_DeniesNotFallsBack()
    {
        // An admin who clears ALL access (configured-but-empty) must deny mutating sections —
        // the Fragment source, NOT the None source that would grant read-only AllowedGroups.
        // Guards the same parity break the presence marker fixes in module-config (B.3).
        SeedSectionAccess(new Dictionary<string, string[]>());
        var service = CreateService();

        Assert.Empty(service.GetGroupsForSection("MailboxPermissions"));
        // And a read-only section must ALSO be empty now (configured-empty != unconfigured).
        Assert.Empty(service.GetGroupsForSection("DelegationReport"));
    }

    [Fact]
    public void IsSectionAccessConfigured_StoreConfigured_ReturnsTrue()
    {
        SeedSectionAccess(new() { ["MailboxPermissions"] = new[] { "GroupA" } });

        Assert.True(CreateService().IsSectionAccessConfigured());
    }

    [Fact]
    public void IsSectionAccessConfigured_ConfiguredEmpty_ReturnsTrue()
    {
        SeedSectionAccess(new Dictionary<string, string[]>());

        Assert.True(CreateService().IsSectionAccessConfigured());
    }

    [Fact]
    public void IsSectionAccessConfigured_LegacyAppSettingsExists_ReturnsTrue()
    {
        var legacyConfig = new Dictionary<string, string?>
        {
            ["Security:SectionAccess:SomeSection:0"] = "SomeGroup"
        };

        Assert.True(CreateService(legacyConfig).IsSectionAccessConfigured());
    }

    [Fact]
    public void SaveSectionAccess_ReadsBackWhatWasSaved()
    {
        var service = CreateService();
        var data = new Dictionary<string, string[]>
        {
            ["MailboxPermissions"] = new[] { "GroupA", "GroupB" },
            ["CalendarPermissions"] = new[] { "GroupC" }
        };

        service.SaveSectionAccess(data);
        var result = service.GetSectionAccess();

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "GroupA", "GroupB" }, result["MailboxPermissions"]);
        Assert.Equal(new[] { "GroupC" }, result["CalendarPermissions"]);
    }

    [Fact]
    public void SaveSectionAccess_OverwritesWholeSet()
    {
        var service = CreateService();
        service.SaveSectionAccess(new() { ["MailboxPermissions"] = new[] { "A" }, ["CalendarPermissions"] = new[] { "B" } });
        service.SaveSectionAccess(new() { ["MailboxPermissions"] = new[] { "C" } });

        var result = service.GetSectionAccess();
        Assert.Equal(new[] { "C" }, result["MailboxPermissions"]);
        Assert.False(result.ContainsKey("CalendarPermissions"));
    }

    // --- Legacy import (one-time, DB-wins, archive) ---

    [Fact]
    public void Construction_ImportsLegacyFragment_ThenArchives()
    {
        File.WriteAllText(_legacyFilePath,
            """{ "Security": { "SectionAccess": { "MailboxPermissions": ["GroupA","GroupB"] } } }""");

        var service = CreateService();

        Assert.Equal(new[] { "GroupA", "GroupB" }, service.GetGroupsForSection("MailboxPermissions"));
        Assert.False(File.Exists(_legacyFilePath));
        Assert.Single(Directory.GetFiles(_configDir, "sectionaccess.json.imported-*"));
    }

    [Fact]
    public void Construction_DbValueWins_OverLegacyFragment()
    {
        SeedSectionAccess(new() { ["MailboxPermissions"] = new[] { "FromDb" } });
        File.WriteAllText(_legacyFilePath,
            """{ "Security": { "SectionAccess": { "MailboxPermissions": ["FromFile"] } } }""");

        var service = CreateService();

        Assert.Equal(new[] { "FromDb" }, service.GetGroupsForSection("MailboxPermissions"));
    }

    // --- Corrupt probe (blank-render-save trap, incident fix #3) + upgrade fail-closed (B.4 class) ---

    [Fact]
    public void IsFragmentCorrupt_HealthyStore_ReturnsFalse()
    {
        Assert.False(CreateService().IsFragmentCorrupt());
    }

    [Fact]
    public void IsFragmentCorrupt_UnreadableStore_ReturnsTrue()
    {
        Assert.True(CreateService(store: new UnreadableStore()).IsFragmentCorrupt());
    }

    [Fact]
    public void Construction_UnparseableLegacyFile_LeftInPlace_FailsClosed()
    {
        File.WriteAllText(_legacyFilePath, "{ this is not valid json !!!");

        var service = CreateService();

        // Corrupt authorization fragment during upgrade must stay fail-closed, not fall back.
        Assert.True(service.IsFragmentCorrupt());
        Assert.Empty(service.GetGroupsForSection("MailboxPermissions"));
        Assert.Empty(service.GetGroupsForSection("DelegationReport")); // even read-only denied
        Assert.True(File.Exists(_legacyFilePath)); // not archived
        Assert.Empty(Directory.GetFiles(_configDir, "sectionaccess.json.imported-*"));
    }

    [Fact]
    public void PartialSchemaDamage_MarkerTableDropped_FailsClosed_DoesNotThrow()
    {
        // Simulate partial corruption: section_access readable, but the presence-marker table is
        // gone. The authorization path must fail closed, not throw.
        SeedSectionAccess(new() { ["MailboxPermissions"] = new[] { "GroupA" } });
        _store.Write((connection, tx) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DROP TABLE section_access_present;";
            cmd.ExecuteNonQuery();
        });

        var service = CreateService();

        Assert.True(service.IsFragmentCorrupt());
        Assert.Empty(service.GetGroupsForSection("MailboxPermissions")); // fail closed, no throw
    }

    [Fact]
    public void Construction_LegacyFileMissingSectionAccessNode_FailsClosed()
    {
        File.WriteAllText(_legacyFilePath, """{ "Security": { } }""");

        var service = CreateService();

        Assert.True(service.IsFragmentCorrupt());
        Assert.Empty(service.GetGroupsForSection("MailboxPermissions"));
        Assert.True(File.Exists(_legacyFilePath)); // not archived
    }
}
