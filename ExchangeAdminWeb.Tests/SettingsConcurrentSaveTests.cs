using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;
using ExchangeOnlineConfigPage = ExchangeAdminWeb.Components.Pages.ExchangeOnlineConfig;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28k: settings saves wrote the page's loaded snapshot over changes made since, recorded an
/// empty before-state, or reported a log level the store had not kept.
/// </summary>
/// <remarks>
/// The aud-a33b style: "the other instance" is a second service over its own store and connection
/// factory on the same temp database file. The windows are driven, not hoped for: a change made
/// between a page's load and its save runs between the two calls, and one landing inside a save runs
/// from the store's <c>BeforeWrite</c> hook as the save's write opens - after anything the save read
/// outside it. The compiled pages through <see cref="SettingsPageHarness"/>; no directory, no grant
/// added. Conditions are asserted, not wording.
/// </remarks>
public sealed class SettingsConcurrentSaveTests : IDisposable
{
    private const string Module = "MailboxPermissions";
    private const string OnPremAlias = "MailboxPermissionsOnPrem";
    private const string Other = "CalendarPermissions";
    private const string GroupA = "S-1-5-21-1000-2000-3000-4001";
    private const string GroupB = "S-1-5-21-1000-2000-3000-4002";
    private const string GroupC = "S-1-5-21-1000-2000-3000-4003";
    private const string AppId = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static string[] Names(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value)
            ? value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray()
            : [];

    private static string Status<TPage>(TPage page) =>
        SettingsPageHarness.Field<string?>(page!, typeof(TPage), "statusMessage") ?? "";

    private Dictionary<string, bool> StoredToggles()
    {
        Assert.True(new ModuleEnablementRepository(TestConfigStore.Create(_h.Root)).TryGetAll(out var state));
        return state;
    }

    private Dictionary<string, string[]> StoredAccess()
    {
        Assert.True(new SectionAccessRepository(TestConfigStore.Create(_h.Root)).TryGetAll(out var access));
        return access;
    }

    private void SeedToggles() =>
        new ModuleEnablementRepository(TestConfigStore.Create(_h.Root))
            .SaveAll(new Dictionary<string, bool> { [Module] = true, [Other] = true, ["ExchangeOnline"] = true });

    /// <summary>The other instance switches <see cref="Other"/> off, through its own store.</summary>
    private void OtherInstanceSwitchesOtherOff() =>
        _h.CreateOtherEnablement().SetModulesEnabled(new Dictionary<string, bool> { [Other] = false });

    // ---- enablement ---------------------------------------------------------------------------

    [Fact]
    public async Task AdminSettings_AToggleMadeElsewhereSinceThePageLoaded_IsNotReverted()
    {
        SeedToggles();
        var page = _h.OpenAdminSettings();
        OtherInstanceSwitchesOtherOff();
        SettingsPageHarness.Field<Dictionary<string, bool>>(page, typeof(AdminSettingsPage), "moduleEnablement")[Module] = false;

        await _h.RunAsync(page, "SaveEnablement");

        var stored = StoredToggles();
        Assert.False(stored[Module]);
        Assert.False(stored[Other]);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal($"Module:{Module}", Text(record, "section"));
        Assert.Equal(new[] { "Enabled" }, Names(record, "removed"));
    }

    [Fact]
    public async Task ModuleConfig_AToggleMadeElsewhereInsideTheSave_IsNotReverted()
    {
        SeedToggles();
        var page = _h.OpenModuleConfig(Module);
        SettingsPageHarness.SetField(page, typeof(ModuleConfigPage), "moduleEnabled", false);
        _h.EnablementStore.BeforeWrite = OtherInstanceSwitchesOtherOff;

        await _h.RunAsync(page, "SaveModuleEnablementAsync");

        Assert.Null(_h.EnablementStore.BeforeWrite);
        var stored = StoredToggles();
        Assert.False(stored[Module]);
        Assert.False(stored[Other]);
        Assert.Equal($"Module:{Module}", Text(Assert.Single(_h.AuditEvents()), "section"));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AToggleMadeElsewhereInsideTheSave_IsNotReverted()
    {
        SeedToggles();
        var page = _h.OpenExchangeOnlineConfig();
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "moduleEnabled", false);
        _h.EnablementStore.BeforeWrite = OtherInstanceSwitchesOtherOff;

        await _h.RunAsync(page, "SaveModuleStatus");

        Assert.Null(_h.EnablementStore.BeforeWrite);
        var stored = StoredToggles();
        Assert.False(stored["ExchangeOnline"]);
        Assert.False(stored[Other]);
        Assert.Equal("Module:ExchangeOnline", Text(Assert.Single(_h.AuditEvents()), "section"));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AStatusSaveWithNoToggleChanged_WritesNothing()
    {
        // The page used to write its loaded state back on every Save Status, reverting a change made
        // elsewhere since it loaded.
        SeedToggles();
        var page = _h.OpenExchangeOnlineConfig();
        _h.CreateOtherEnablement().SetModulesEnabled(new Dictionary<string, bool> { ["ExchangeOnline"] = false });

        await _h.RunAsync(page, "SaveModuleStatus");

        Assert.False(StoredToggles()["ExchangeOnline"]);
        Assert.Empty(_h.AuditEvents());
    }

    // ---- section access -----------------------------------------------------------------------

    private void SeedAccess(string[] module, string[]? onPrem = null)
    {
        var access = new Dictionary<string, string[]> { [Module] = module };
        if (onPrem is not null)
            access[OnPremAlias] = onPrem;
        new SectionAccessRepository(TestConfigStore.Create(_h.Root)).SaveAll(access);
    }

    private static List<string> AccessList(ModuleConfigPage page, string alias) =>
        SettingsPageHarness.Field<Dictionary<string, List<string>>>(page, typeof(ModuleConfigPage), "accessState")[alias];

    /// <summary>The other instance replaces <paramref name="alias"/>'s groups, through its own store.</summary>
    private void OtherInstanceGrants(string alias, params string[] groups)
    {
        var other = _h.CreateOtherSectionAccess();
        var all = other.GetSectionAccess();
        all[alias] = groups;
        other.SaveSectionAccess(all);
    }

    private void AssertOnlyAConflictRefusal(ModuleConfigPage page, string alias)
    {
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal("ConcurrentChange", Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(Status(page), Text(record, "error"));
        Assert.Contains(alias, Status(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModuleConfig_AGrantChangedElsewhereOnAnEditedAlias_RefusesTheSave_NamingTheAlias()
    {
        SeedAccess([GroupA, GroupB]);
        var page = _h.OpenModuleConfig(Module);
        OtherInstanceGrants(Module, GroupA, GroupB, GroupC);
        Assert.True(AccessList(page, Module).Remove(GroupB));

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        Assert.Equal(new[] { GroupA, GroupB, GroupC }, StoredAccess()[Module].Order());
        AssertOnlyAConflictRefusal(page, Module);
    }

    [Fact]
    public async Task ModuleConfig_AGrantChangedElsewhereInsideTheSave_RefusesTheSave()
    {
        SeedAccess([GroupA, GroupB]);
        var page = _h.OpenModuleConfig(Module);
        Assert.True(AccessList(page, Module).Remove(GroupB));
        _h.SectionStore.BeforeWrite = () => OtherInstanceGrants(Module, GroupA, GroupB, GroupC);

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        Assert.Null(_h.SectionStore.BeforeWrite);
        Assert.Equal(new[] { GroupA, GroupB, GroupC }, StoredAccess()[Module].Order());
        AssertOnlyAConflictRefusal(page, Module);
    }

    [Fact]
    public async Task ModuleConfig_AGrantMadeElsewhereOnAnAliasThePageDidNotEdit_SurvivesTheSave()
    {
        SeedAccess([GroupA, GroupB], onPrem: []);
        var page = _h.OpenModuleConfig(Module);
        OtherInstanceGrants(OnPremAlias, GroupC);
        Assert.True(AccessList(page, Module).Remove(GroupB));

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        var stored = StoredAccess();
        Assert.Equal(new[] { GroupA }, stored.GetValueOrDefault(Module) ?? []);
        Assert.Equal(new[] { GroupC }, stored.GetValueOrDefault(OnPremAlias) ?? []);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal(Module, Text(record, "section"));
        Assert.Equal(new[] { GroupB }, Names(record, "removed"));
    }

    [Fact]
    public async Task ModuleConfig_AnAccessSaveWithNothingEdited_WritesNothing_NotEvenTheChangeToken()
    {
        SeedAccess([GroupA, GroupB]);
        var page = _h.OpenModuleConfig(Module);
        var token = _h.SectionStore.GetChangeToken();

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        Assert.Equal(token, _h.SectionStore.GetChangeToken());
        Assert.Empty(_h.AuditEvents());
        Assert.False(SettingsPageHarness.Field<bool>(page, typeof(ModuleConfigPage), "statusIsError"));
    }

    [Fact]
    public void SectionAccess_TheFirstSaveOnAStoreNeverConfigured_LeavesEveryOtherAliasAsTheGatesReadIt()
    {
        // Saving one module's grants is what marks section access configured, after which the fallback
        // no longer applies - so the save writes the fallback for every other alias, as the whole-table
        // save did, and nobody's effective access moves.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:AllowedGroups:0"] = "EXAMPLE\\AllUsers",
        }).Build();
        var service = new SectionAccessService(config, NullLogger<SectionAccessService>.Instance, _h.Env, _h.Catalog,
            new SectionAccessRepository(TestConfigStore.Create(_h.Root)));
        Assert.False(service.IsSectionAccessConfigured());
        Assert.Equal(new[] { "EXAMPLE\\AllUsers" }, service.GetGroupsForSection("DelegationReport"));

        var outcome = service.SaveEditedAliases(
            new Dictionary<string, string[]> { [Module] = [] },
            new Dictionary<string, string[]> { [Module] = [GroupA] });

        Assert.Equal(SectionAccessSaveStatus.Saved, outcome.Status);
        Assert.True(service.IsSectionAccessConfigured());
        Assert.Equal(new[] { GroupA }, service.GetGroupsForSection(Module));
        Assert.Equal(new[] { "EXAMPLE\\AllUsers" }, service.GetGroupsForSection("DelegationReport"));
        Assert.Empty(service.GetGroupsForSection(OnPremAlias));
    }

    // ---- before-state -------------------------------------------------------------------------

    [Fact]
    public async Task ExchangeOnlineConfig_AConnectionSave_RecordsTheValueItReplaced()
    {
        _h.ModuleConfig.SaveModuleConfig(ExoConnectionPool.ConfigModuleKey, new Dictionary<string, string>
        {
            [ExoConnectionPool.ConfigAppIdKey] = AppId,
            [ExoConnectionPool.ConfigOrganizationKey] = "contoso.onmicrosoft.com",
            [ExoConnectionPool.ConfigCertSubjectKey] = "CN=EXO-Automation",
        });
        var page = _h.OpenExchangeOnlineConfig();
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "exoOrganization", "fabrikam.onmicrosoft.com");

        await _h.RunAsync(page, "SaveExoConfig");

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(new[] { "Org=contoso.onmicrosoft.com" }, Names(record, "removed"));
        Assert.Equal(new[] { "Org=fabrikam.onmicrosoft.com" }, Names(record, "added"));
    }

    // ---- log level ----------------------------------------------------------------------------

    [Fact]
    public async Task AdminSettings_ALogLevelTheStoreRefuses_IsReportedAndRecordedAsAFailure()
    {
        var page = _h.OpenAdminSettings();
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "selectedLogLevel", "Warning");
        _h.LogLevelStore.WritesFail = true;

        await _h.RunAsync(page, "SaveLogLevel");

        _h.LogLevelStore.WritesFail = false;
        Assert.Null(_h.StoredLogLevel());
        Assert.Equal("None", _h.ExtendedLog.CurrentLevel);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal("ExtendedLogLevel", Text(record, "section"));
        Assert.False(record.TryGetProperty("refusedAt", out _));
        Assert.Equal(Status(page), Text(record, "error"));
        Assert.True(SettingsPageHarness.Field<bool>(page, typeof(AdminSettingsPage), "statusIsError"));
        Assert.Contains("still None", Status(page), StringComparison.Ordinal);
    }
}
