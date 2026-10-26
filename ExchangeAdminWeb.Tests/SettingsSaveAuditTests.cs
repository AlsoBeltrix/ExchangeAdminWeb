using System.Text.Json;
using ExchangeAdminWeb.Services.Storage;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;
using ExchangeOnlineConfigPage = ExchangeAdminWeb.Components.Pages.ExchangeOnlineConfig;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28h: the settings saves on Module Configuration, Admin Settings and Exchange Online Config
/// recorded a success and nothing else - a refused or failed attempt to change the authorization
/// stores among them left no trace.
/// </summary>
/// <remarks>
/// Every one of the nine handlers is driven through its compiled page by
/// <see cref="SettingsPageHarness"/>, over real stores on one temp database file and the real audit
/// writer. Per handler: each refusal leaves exactly one Failed record that says where it refused
/// and that nothing was tried, and the store is unchanged; an exception leaves exactly one Failed
/// record without the refusal fields; a success still leaves exactly one Success record. Nothing
/// here reaches the directory: no case adds a usable group SID, and the group-grant refusals use a
/// grant that GroupGrantValidation refuses on its shape, before any lookup. Conditions are asserted,
/// not wording.
/// </remarks>
public sealed class SettingsSaveAuditTests : IDisposable
{
    private const string Module = "MailboxPermissions";
    private const string GroupA = "S-1-5-21-1000-2000-3000-4001";
    private const string GroupB = "S-1-5-21-1000-2000-3000-4002";
    private const string NotAGroupSid = "not-a-group-sid";

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

    /// <summary>The settings-change record every outcome shares: action, category, who and from where.</summary>
    private void AssertSettingsRecord(JsonElement record, string section, string result)
    {
        Assert.Equal("UpdateSectionAccess", Text(record, "action"));
        Assert.Equal("AdminSettings", Text(record, "category"));
        Assert.Equal(result, Text(record, "result"));
        Assert.Equal(section, Text(record, "section"));
        Assert.Equal(_h.AdminSam, Text(record, "user"));
        Assert.Equal(SettingsPageHarness.Ip, Text(record, "ip"));
    }

    private void AssertOnlyARefusal(string section, string refusedAt, string shown)
    {
        var record = Assert.Single(_h.AuditEvents());
        AssertSettingsRecord(record, section, "Failed");
        Assert.Equal(refusedAt, Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(shown, Text(record, "error"));
    }

    private void AssertOnlyAFailure(string section, string shown)
    {
        var record = Assert.Single(_h.AuditEvents());
        AssertSettingsRecord(record, section, "Failed");
        Assert.Equal(shown, Text(record, "error"));
        // Not a refusal: the write may have been tried, and the record must not claim otherwise.
        Assert.False(record.TryGetProperty("refusedAt", out _));
        Assert.False(record.TryGetProperty("mutationAttempted", out _));
    }

    private JsonElement AssertOnlyASuccess(string section)
    {
        var record = Assert.Single(_h.AuditEvents());
        AssertSettingsRecord(record, section, "Success");
        return record;
    }

    // ---- Module Configuration: Configuration tab (SaveConfigAsync) ----------------------------

    private ModuleConfigPage ConfigEdited()
    {
        var page = _h.OpenModuleConfig(Module);
        SettingsPageHarness.Field<Dictionary<string, string>>(page, typeof(ModuleConfigPage), "currentConfigState")["DelineaSecretId"] = "4242";
        return page;
    }

    [Fact]
    public async Task ModuleConfig_AnUnauthorisedConfigSave_IsRecordedAsARefusal_AndWritesNothing()
    {
        var page = ConfigEdited();
        var before = _h.StoredModuleConfig(Module);
        _h.AdminAllowed = false;

        await _h.RunAsync(page, "SaveConfigAsync");

        Assert.Equal(before, _h.StoredModuleConfig(Module));
        AssertOnlyARefusal($"ModuleConfig:{Module}", "Authorization", Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AConfigSaveOverACorruptStore_IsRecordedAsARefusal_AndWritesNothing()
    {
        var page = ConfigEdited();
        var before = _h.StoredModuleConfig(Module);
        _h.ModuleConfigStore.ReadsFail = true;

        await _h.RunAsync(page, "SaveConfigAsync");

        _h.ModuleConfigStore.ReadsFail = false;
        Assert.Equal(before, _h.StoredModuleConfig(Module));
        AssertOnlyARefusal($"ModuleConfig:{Module}", "StoreCorrupt", Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AConfigSaveWhoseWriteFails_IsRecordedAsAFailure()
    {
        var page = ConfigEdited();
        var before = _h.StoredModuleConfig(Module);
        _h.ModuleConfigStore.WritesFail = true;

        await _h.RunAsync(page, "SaveConfigAsync");

        Assert.Equal(before, _h.StoredModuleConfig(Module));
        AssertOnlyAFailure($"ModuleConfig:{Module}", Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AConfigSave_LeavesExactlyOneSuccessRecord()
    {
        var page = ConfigEdited();

        await _h.RunAsync(page, "SaveConfigAsync");

        Assert.Contains("4242", _h.StoredModuleConfig(Module), StringComparison.Ordinal);
        AssertOnlyASuccess($"ModuleConfig:{Module}");
    }

    // ---- Module Configuration: Status tab (SaveModuleEnablementAsync) --------------------------

    private ModuleConfigPage StatusToggled()
    {
        var page = _h.OpenModuleConfig(Module);
        var enabled = SettingsPageHarness.Field<bool>(page, typeof(ModuleConfigPage), "moduleEnabled");
        SettingsPageHarness.SetField(page, typeof(ModuleConfigPage), "moduleEnabled", !enabled);
        return page;
    }

    [Fact]
    public async Task ModuleConfig_AnUnauthorisedStatusSave_IsRecordedAsARefusal_AndWritesNothing()
    {
        var page = StatusToggled();
        var before = _h.StoredEnablement();
        _h.AdminAllowed = false;

        await _h.RunAsync(page, "SaveModuleEnablementAsync");

        Assert.Equal(before, _h.StoredEnablement());
        AssertOnlyARefusal($"Module:{Module}", "Authorization", Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AStatusSaveOverACorruptStore_IsRecordedAsARefusal_AndWritesNothing()
    {
        var page = StatusToggled();
        var before = _h.StoredEnablement();
        _h.EnablementStore.ReadsFail = true;

        await _h.RunAsync(page, "SaveModuleEnablementAsync");

        _h.EnablementStore.ReadsFail = false;
        Assert.Equal(before, _h.StoredEnablement());
        AssertOnlyARefusal($"Module:{Module}", "StoreCorrupt", Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AStatusSaveWhoseWriteFails_IsRecordedAsAFailure()
    {
        var page = StatusToggled();
        var before = _h.StoredEnablement();
        _h.EnablementStore.WritesFail = true;

        await _h.RunAsync(page, "SaveModuleEnablementAsync");

        Assert.Equal(before, _h.StoredEnablement());
        AssertOnlyAFailure($"Module:{Module}", Status(page));
        Assert.Empty(_h.Navigation.Navigations);
    }

    [Fact]
    public async Task ModuleConfig_AStatusSave_LeavesExactlyOneSuccessRecord_AndReloads()
    {
        var page = StatusToggled();
        var before = _h.StoredEnablement();

        await _h.RunAsync(page, "SaveModuleEnablementAsync");

        Assert.NotEqual(before, _h.StoredEnablement());
        AssertOnlyASuccess($"Module:{Module}");
        Assert.Single(_h.Navigation.Navigations);
    }

    // ---- Module Configuration: Access tab (SaveSectionAccessAsync) -----------------------------

    /// <summary>The module's access granted to two groups, and the page holding a removal of one.</summary>
    private ModuleConfigPage AccessWithARemoval()
    {
        new SectionAccessRepository(TestConfigStore.Create(_h.Root))
            .SaveAll(new Dictionary<string, string[]> { [Module] = [GroupA, GroupB] });
        var page = _h.OpenModuleConfig(Module);
        Assert.True(SettingsPageHarness.Field<bool>(page, typeof(ModuleConfigPage), "isGlobalAdmin"));
        var access = SettingsPageHarness.Field<Dictionary<string, List<string>>>(page, typeof(ModuleConfigPage), "accessState");
        Assert.True(access[Module].Remove(GroupB), "the seeded grant did not load - this fixture is stale");
        return page;
    }

    private static List<string> AccessList(ModuleConfigPage page, string alias) =>
        SettingsPageHarness.Field<Dictionary<string, List<string>>>(page, typeof(ModuleConfigPage), "accessState")[alias];

    [Fact]
    public async Task ModuleConfig_AnUnauthorisedAccessSave_IsRecordedAsARefusal_NamingWhatItWouldChange()
    {
        var page = AccessWithARemoval();
        var before = _h.StoredSectionAccess();
        _h.AdminAllowed = false;

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        Assert.Equal(before, _h.StoredSectionAccess());
        AssertOnlyARefusal(Module, "Authorization", Status(page));
        // Filed under the module's own grant, the one record for an attempt whose success would
        // have written one per alias it changed - and it says which.
        Assert.Equal(new[] { Module }, Names(Assert.Single(_h.AuditEvents()), "sections"));
    }

    [Fact]
    public async Task ModuleConfig_AnAccessSaveOverACorruptStore_IsRecordedAsARefusal_AndWritesNothing()
    {
        var page = AccessWithARemoval();
        var before = _h.StoredSectionAccess();
        _h.SectionStore.ReadsFail = true;

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        _h.SectionStore.ReadsFail = false;
        Assert.Equal(before, _h.StoredSectionAccess());
        AssertOnlyARefusal(Module, "StoreCorrupt", Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AnAccessSaveAddingANonGroup_IsRecordedAsARefusal_AndWritesNothing()
    {
        var page = AccessWithARemoval();
        AccessList(page, Module).Add(NotAGroupSid);
        var before = _h.StoredSectionAccess();

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        Assert.Equal(before, _h.StoredSectionAccess());
        AssertOnlyARefusal(Module, "GroupGrantCheck", Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AnAccessSaveWhoseWriteFails_IsRecordedAsAFailure()
    {
        var page = AccessWithARemoval();
        var before = _h.StoredSectionAccess();
        _h.SectionStore.WritesFail = true;

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        Assert.Equal(before, _h.StoredSectionAccess());
        AssertOnlyAFailure(Module, Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AnAccessSave_LeavesExactlyOneSuccessRecord()
    {
        var page = AccessWithARemoval();

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        Assert.DoesNotContain(GroupB, _h.StoredSectionAccess(), StringComparison.Ordinal);
        var record = AssertOnlyASuccess(Module);
        Assert.Equal(new[] { GroupB }, Names(record, "removed"));
    }

    // ---- Module Configuration: Module admins tab (SaveModuleAdminsAsync) -----------------------

    private ModuleConfigPage AdminsWithARemoval()
    {
        new ModuleAdminRepository(TestConfigStore.Create(_h.Root)).SetForModule(Module, [GroupA, GroupB]);
        var page = _h.OpenModuleConfig(Module);
        var admins = SettingsPageHarness.Field<List<string>>(page, typeof(ModuleConfigPage), "moduleAdminGroups");
        Assert.True(admins.Remove(GroupB), "the seeded admin group did not load - this fixture is stale");
        return page;
    }

    [Fact]
    public async Task ModuleConfig_AnUnauthorisedAdminsSave_IsRecordedAsARefusal_AndWritesNothing()
    {
        var page = AdminsWithARemoval();
        var before = _h.StoredModuleAdmins(Module);
        _h.AdminAllowed = false;

        await _h.RunAsync(page, "SaveModuleAdminsAsync");

        Assert.Equal(before, _h.StoredModuleAdmins(Module));
        AssertOnlyARefusal($"ModuleAdmins:{Module}", "Authorization", Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AnAdminsSaveAddingANonGroup_IsRecordedAsARefusal_AndWritesNothing()
    {
        var page = AdminsWithARemoval();
        SettingsPageHarness.Field<List<string>>(page, typeof(ModuleConfigPage), "moduleAdminGroups").Add(NotAGroupSid);
        var before = _h.StoredModuleAdmins(Module);

        await _h.RunAsync(page, "SaveModuleAdminsAsync");

        Assert.Equal(before, _h.StoredModuleAdmins(Module));
        AssertOnlyARefusal($"ModuleAdmins:{Module}", "GroupGrantCheck", Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AnAdminsSaveWhoseWriteFails_IsRecordedAsAFailure()
    {
        var page = AdminsWithARemoval();
        var before = _h.StoredModuleAdmins(Module);
        _h.AdminsStore.WritesFail = true;

        await _h.RunAsync(page, "SaveModuleAdminsAsync");

        Assert.Equal(before, _h.StoredModuleAdmins(Module));
        AssertOnlyAFailure($"ModuleAdmins:{Module}", Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AnAdminsSave_LeavesExactlyOneSuccessRecord()
    {
        var page = AdminsWithARemoval();

        await _h.RunAsync(page, "SaveModuleAdminsAsync");

        Assert.DoesNotContain(GroupB, _h.StoredModuleAdmins(Module), StringComparison.Ordinal);
        var record = AssertOnlyASuccess($"ModuleAdmins:{Module}");
        Assert.Equal(new[] { GroupB }, Names(record, "removed"));
    }

    // ---- Admin Settings: Modules tab (SaveEnablement) -----------------------------------------

    private AdminSettingsPage EnablementToggled()
    {
        var page = _h.OpenAdminSettings();
        var enablement = SettingsPageHarness.Field<Dictionary<string, bool>>(page, typeof(AdminSettingsPage), "moduleEnablement");
        enablement[Module] = !enablement[Module];
        return page;
    }

    [Fact]
    public async Task AdminSettings_AnUnauthorisedEnablementSave_IsRecordedAsARefusal_NamingWhatItWouldChange()
    {
        var page = EnablementToggled();
        var before = _h.StoredEnablement();
        _h.AdminAllowed = false;

        await _h.RunAsync(page, "SaveEnablement");

        Assert.Equal(before, _h.StoredEnablement());
        AssertOnlyARefusal("ModuleEnablement", "Authorization", Status(page));
        Assert.Equal(new[] { $"Module:{Module}" }, Names(Assert.Single(_h.AuditEvents()), "sections"));
    }

    [Fact]
    public async Task AdminSettings_AnEnablementSaveOverACorruptStore_IsRecordedAsARefusal_AndWritesNothing()
    {
        var page = EnablementToggled();
        var before = _h.StoredEnablement();
        _h.EnablementStore.ReadsFail = true;

        await _h.RunAsync(page, "SaveEnablement");

        _h.EnablementStore.ReadsFail = false;
        Assert.Equal(before, _h.StoredEnablement());
        AssertOnlyARefusal("ModuleEnablement", "StoreCorrupt", Status(page));
    }

    [Fact]
    public async Task AdminSettings_AnEnablementSaveWhoseWriteFails_IsRecordedAsAFailure()
    {
        var page = EnablementToggled();
        var before = _h.StoredEnablement();
        _h.EnablementStore.WritesFail = true;

        await _h.RunAsync(page, "SaveEnablement");

        Assert.Equal(before, _h.StoredEnablement());
        AssertOnlyAFailure("ModuleEnablement", Status(page));
        Assert.Empty(_h.Navigation.Navigations);
    }

    [Fact]
    public async Task AdminSettings_AnEnablementSave_LeavesExactlyOneSuccessRecord_AndReloads()
    {
        var page = EnablementToggled();
        var before = _h.StoredEnablement();

        await _h.RunAsync(page, "SaveEnablement");

        Assert.NotEqual(before, _h.StoredEnablement());
        AssertOnlyASuccess($"Module:{Module}");
        Assert.Single(_h.Navigation.Navigations);
    }

    // ---- Admin Settings: Logging tab (SaveLogLevel) -------------------------------------------

    private AdminSettingsPage LevelChosen()
    {
        var page = _h.OpenAdminSettings();
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "selectedLogLevel", "Warning");
        return page;
    }

    [Fact]
    public async Task AdminSettings_AnUnauthorisedLogLevelSave_IsRecordedAsARefusal_AndWritesNothing()
    {
        var page = LevelChosen();
        var before = _h.StoredLogLevel();
        _h.AdminAllowed = false;

        await _h.RunAsync(page, "SaveLogLevel");

        Assert.Equal(before, _h.StoredLogLevel());
        AssertOnlyARefusal("ExtendedLogLevel", "Authorization", Status(page));
    }

    /// <summary>
    /// aud-a67 item 5: this said SetLevel "logs and swallows" a store failure, so only what came before it could reach
    /// the catch. It has rethrown the store's exception since aud-a28k (ExtendedLogService.SetLevel), which is what lets
    /// a level that did not persist be reported as a failure instead of a success. Both ways in are driven now.
    /// </summary>
    [Theory]
    [InlineData("the authorization store")]
    [InlineData("the settings store the level is written to")]
    public async Task AdminSettings_ALogLevelSaveThatThrows_IsRecordedAsAFailure(string fails)
    {
        var page = LevelChosen();
        var before = _h.StoredLogLevel();
        if (fails == "the authorization store")
            _h.AuthorizationFailure = new InvalidOperationException("the authorization store could not be read");
        else
            _h.LogLevelStore.WritesFail = true;

        await _h.RunAsync(page, "SaveLogLevel");

        _h.LogLevelStore.WritesFail = false;
        Assert.Equal(before, _h.StoredLogLevel());
        AssertOnlyAFailure("ExtendedLogLevel", Status(page));
    }

    [Fact]
    public async Task AdminSettings_ALogLevelSave_LeavesExactlyOneSuccessRecord()
    {
        var page = LevelChosen();

        await _h.RunAsync(page, "SaveLogLevel");

        Assert.Equal("Warning", _h.StoredLogLevel());
        AssertOnlyASuccess("ExtendedLogLevel");
    }

    // ---- Admin Settings: Protected principals tab (SaveProtectedPrincipals) -------------------

    private async Task<AdminSettingsPage> PatternAdded()
    {
        var page = _h.OpenAdminSettings();
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppNewPattern", "svc-audit-*");
        await _h.RunAsync(page, "AddPpPattern");
        return page;
    }

    [Fact]
    public async Task AdminSettings_AnUnauthorisedProtectedSave_IsRecordedAsARefusal_AndWritesNothing()
    {
        var page = await PatternAdded();
        var before = _h.StoredProtectedPrincipals();
        _h.AdminAllowed = false;

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(before, _h.StoredProtectedPrincipals());
        AssertOnlyARefusal("ProtectedPrincipals", "Authorization", Status(page));
    }

    [Fact]
    public async Task AdminSettings_AProtectedSaveDuringAnAdd_IsRecordedAsARefusal_AndWritesNothing()
    {
        var page = await PatternAdded();
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppValidating", true);
        var before = _h.StoredProtectedPrincipals();

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(before, _h.StoredProtectedPrincipals());
        AssertOnlyARefusal("ProtectedPrincipals", "ValidationInProgress", Status(page));
    }

    [Fact]
    public async Task AdminSettings_AProtectedSaveWhoseWriteFails_IsRecordedAsAFailure()
    {
        var page = await PatternAdded();
        var before = _h.StoredProtectedPrincipals();
        _h.ProtectedStore.WritesFail = true;

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(before, _h.StoredProtectedPrincipals());
        AssertOnlyAFailure("ProtectedPrincipals", Status(page));
    }

    [Fact]
    public async Task AdminSettings_AProtectedSave_LeavesExactlyOneSuccessRecord()
    {
        var page = await PatternAdded();

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Contains("svc-audit-*", _h.StoredProtectedPrincipals(), StringComparison.Ordinal);
        AssertOnlyASuccess("ProtectedPrincipals");
    }

    // ---- Exchange Online Config: Module Status (SaveModuleStatus) -----------------------------

    private ExchangeOnlineConfigPage ExoStatusToggled()
    {
        var page = _h.OpenExchangeOnlineConfig();
        var enabled = SettingsPageHarness.Field<bool>(page, typeof(ExchangeOnlineConfigPage), "moduleEnabled");
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "moduleEnabled", !enabled);
        return page;
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AnUnauthorisedStatusSave_IsRecordedAsARefusal_AndWritesNothing()
    {
        var page = ExoStatusToggled();
        var before = _h.StoredEnablement();
        _h.AdminAllowed = false;

        await _h.RunAsync(page, "SaveModuleStatus");

        Assert.Equal(before, _h.StoredEnablement());
        AssertOnlyARefusal("Module:ExchangeOnline", "Authorization", Status(page));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AStatusSaveWhoseWriteFails_IsRecordedAsAFailure()
    {
        var page = ExoStatusToggled();
        var before = _h.StoredEnablement();
        _h.EnablementStore.WritesFail = true;

        await _h.RunAsync(page, "SaveModuleStatus");

        Assert.Equal(before, _h.StoredEnablement());
        AssertOnlyAFailure("Module:ExchangeOnline", Status(page));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AStatusSave_LeavesExactlyOneSuccessRecord()
    {
        var page = ExoStatusToggled();
        var before = _h.StoredEnablement();

        await _h.RunAsync(page, "SaveModuleStatus");

        Assert.NotEqual(before, _h.StoredEnablement());
        AssertOnlyASuccess("Module:ExchangeOnline");
    }

    // ---- Exchange Online Config: Connection (SaveExoConfig) -----------------------------------

    private const string AppId = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

    private ExchangeOnlineConfigPage ConnectionEntered(string appId, string organization, string certificate)
    {
        var page = _h.OpenExchangeOnlineConfig();
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "exoAppId", appId);
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "exoOrganization", organization);
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "exoCertSubject", certificate);
        return page;
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AnUnauthorisedConnectionSave_IsRecordedAsARefusal_AndWritesNothing()
    {
        var page = ConnectionEntered(AppId, "contoso.onmicrosoft.com", "CN=EXO-Automation");
        var before = _h.StoredModuleConfig("ExchangeOnline");
        _h.AdminAllowed = false;

        await _h.RunAsync(page, "SaveExoConfig");

        Assert.Equal(before, _h.StoredModuleConfig("ExchangeOnline"));
        AssertOnlyARefusal("ExchangeOnlineConfig", "Authorization", Status(page));
    }

    [Theory]
    [InlineData(AppId, "", "CN=EXO-Automation")]
    [InlineData("not-a-guid", "contoso.onmicrosoft.com", "CN=EXO-Automation")]
    [InlineData(AppId, "contoso", "CN=EXO-Automation")]
    [InlineData(AppId, "contoso.onmicrosoft.com", "EXO-Automation")]
    public async Task ExchangeOnlineConfig_AnInvalidConnection_IsRecordedAsARefusal_AndWritesNothing(
        string appId, string organization, string certificate)
    {
        var page = ConnectionEntered(appId, organization, certificate);
        var before = _h.StoredModuleConfig("ExchangeOnline");

        await _h.RunAsync(page, "SaveExoConfig");

        Assert.Equal(before, _h.StoredModuleConfig("ExchangeOnline"));
        AssertOnlyARefusal("ExchangeOnlineConfig", "Validation", Status(page));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AConnectionSaveWhoseWriteFails_IsRecordedAsAFailure()
    {
        var page = ConnectionEntered(AppId, "contoso.onmicrosoft.com", "CN=EXO-Automation");
        var before = _h.StoredModuleConfig("ExchangeOnline");
        _h.ModuleConfigStore.WritesFail = true;

        await _h.RunAsync(page, "SaveExoConfig");

        Assert.Equal(before, _h.StoredModuleConfig("ExchangeOnline"));
        AssertOnlyAFailure("ExchangeOnlineConfig", Status(page));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AConnectionSave_LeavesExactlyOneSuccessRecord()
    {
        var page = ConnectionEntered(AppId, "contoso.onmicrosoft.com", "CN=EXO-Automation");

        await _h.RunAsync(page, "SaveExoConfig");

        Assert.Contains("contoso.onmicrosoft.com", _h.StoredModuleConfig("ExchangeOnline"), StringComparison.Ordinal);
        AssertOnlyASuccess("ExchangeOnlineConfig");
    }

    // ---- Tripwire: no refusal or catch in these handlers goes unrecorded -----------------------

    /// <summary>The three settings pages' save handlers, found in their source rather than listed here (aud-a67 item 8).</summary>
    /// <remarks>
    /// The list was nine <c>InlineData</c> rows, so a save added to one of these pages - or renamed - was simply absent
    /// from the guard, and only the FIRST catch of each handler was looked at, so a second or a nested one escaped it
    /// too. Both are discovered now: every <c>Task Save*(</c> declaration on the three pages, and every
    /// <c>catch (Exception ...)</c> inside each.
    /// </remarks>
    public static TheoryData<string, string> SettingsSaves()
    {
        var found = new TheoryData<string, string>();
        foreach (var page in new[] { "AdminSettings.razor", "ExchangeOnlineConfig.razor", "ModuleConfig.razor" })
        {
            foreach (var declaration in SaveDeclarations(PageCode(page)))
                found.Add(page, declaration);
        }

        Assert.True(found.Count >= 11, $"only {found.Count} settings saves were found in the three pages - this guard is stale");
        return found;
    }

    private static string PageCode(string page) =>
        ProgressScan.CodeView(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", page)));

    private static readonly System.Text.RegularExpressions.Regex SaveDeclaration = new(
        @"(?:private|internal|public|protected)\s+(?:async\s+)?Task(?:<[^>\r\n]+>)?\s+Save\w*\s*\([^)\r\n]*\)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static IEnumerable<string> SaveDeclarations(string code) =>
        SaveDeclaration.Matches(code).Select(m => m.Value).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// What marks a catch that may report its own failure instead of going through the settings-save helper: the audit
    /// writes that follow a completed save, which this app names <c>auditEx</c> everywhere. An audit sink that fails
    /// must not make the save look failed (Constitution, Auditing And Tracing), so they log and swallow rather than
    /// record a second time - and this guard holds them to exactly that, below.
    /// </summary>
    /// <remarks>
    /// The marker is the variable, not the message: these scans read <see cref="ProgressScan.CodeView"/>, which blanks
    /// string literals, so the wording of the log line is not there to match on.
    /// </remarks>
    private const string AuditFailureCatch = "catch (Exception auditEx)";

    /// <summary>
    /// The one save whose records are the attribute-editor service's rather than this helper's: the service writes the
    /// refusal or the failure for everything it does (aud-a28l, aud-a67 item 6), and the page's catch covers its own
    /// authorization re-check. Named here with its reason, so a NEW catch anywhere else is still caught by this guard.
    /// </summary>
    private const string AllowlistSave = "SaveAllowlistAsync";

    /// <summary>
    /// The connection save's second catch, around the pooled-connection drain that follows a connection already
    /// written. A failed drain is not a failed save - under the settings-change action it read as one - so it has its
    /// own audit action (aud-a26k) and must use it. Found by this guard once it began reading every catch rather than
    /// the first (aud-a67 item 8).
    /// </summary>
    private const string PoolDrainCatch = "catch (Exception drainEx)";

    [Theory]
    [MemberData(nameof(SettingsSaves))]
    public void EveryRefusalAndCatchInASettingsSave_WritesItsRecord(string page, string signature)
    {
        // The behavioural cases above drive every refusal these handlers have today; this fails
        // the next one added without a record, and names it.
        var code = AuditCategoryFilingTests.MethodBody(PageCode(page), signature);
        var tryAt = System.Text.RegularExpressions.Regex.Match(code, @"\btry\b").Index;
        if (tryAt <= 0)
        {
            // A save that neither tries nor catches decides nothing and records nothing - the Save button's
            // one-at-a-time guard and the hand-off are all it is (aud-a67 item 4). It must still not write by hand.
            Assert.DoesNotContain("Audit.LogSettingsSaveFailed(", code, StringComparison.Ordinal);
            Assert.DoesNotContain("catch (Exception", code, StringComparison.Ordinal);
            return;
        }

        var lines = code[tryAt..].Split('\n');
        var unrecorded = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("return;", StringComparison.Ordinal))
                continue;

            // The allowlist save's refusals are the attribute-editor service's records, as its catch is (below).
            var records = signature.Contains(AllowlistSave, StringComparison.Ordinal)
                ? "AttrEditorService.LogAllowlistSaveRefused("
                : "Audit.LogSettingsSaveRefused(";
            var above = lines.Skip(Math.Max(0, i - 3)).Take(i - Math.Max(0, i - 3) + 1);
            if (!above.Any(l => l.Contains(records, StringComparison.Ordinal)))
                unrecorded.Add(lines[i].Trim());
        }

        Assert.True(unrecorded.Count == 0, $"{page} {signature}: refusal return(s) with no record above them");

        // aud-a40: through the one helper, which tells a busy store from a failure, and never by hand.
        // aud-a67 item 8: EVERY catch in the handler, not only the first one found.
        var catches = CatchBlocks(code).ToList();
        Assert.True(catches.Count > 0, $"{signature} has no catch - this guard is stale");
        foreach (var block in catches)
        {
            if (block.StartsWith(AuditFailureCatch, StringComparison.Ordinal))
            {
                // Held to what the exemption is for: it reports the audit failure and does nothing else. A record
                // written here would be the second one for an operation that completed.
                Assert.Contains("Logger.LogError(auditEx", block, StringComparison.Ordinal);
                Assert.DoesNotContain("Audit.LogSettingsSave", block, StringComparison.Ordinal);
                continue;
            }

            if (block.StartsWith(PoolDrainCatch, StringComparison.Ordinal))
            {
                Assert.Contains("Audit.LogConnectionPoolDrainFailed(", block, StringComparison.Ordinal);
                Assert.DoesNotContain("Audit.LogSettingsSave", block, StringComparison.Ordinal);
                continue;
            }

            if (signature.Contains(AllowlistSave, StringComparison.Ordinal))
            {
                Assert.Contains("AttrEditorService.LogAllowlistSave", block, StringComparison.Ordinal);
                continue;
            }

            Assert.Contains("Audit.LogSettingsSaveException(", block, StringComparison.Ordinal);
            Assert.DoesNotContain("Audit.LogSettingsSaveFailed(", block, StringComparison.Ordinal);
        }
    }

    /// <summary>Every <c>catch (Exception ...)</c> block in <paramref name="code"/>, each from its brace to its match.</summary>
    private static IEnumerable<string> CatchBlocks(string code)
    {
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(code, @"catch \(Exception \w+\)"))
        {
            yield return AuditCategoryFilingTests.MethodBody(code[match.Index..], match.Value);
        }
    }
}
