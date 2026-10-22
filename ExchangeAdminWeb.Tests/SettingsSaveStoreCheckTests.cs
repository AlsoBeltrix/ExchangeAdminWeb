using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;
using ExchangeOnlineConfigPage = ExchangeAdminWeb.Components.Pages.ExchangeOnlineConfig;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28j: a settings save wrote the page's state over its store without asking whether that state
/// came from the store - a protected-principals save after a failed load replaced every protection
/// rule with the empty lists the page held.
/// </summary>
/// <remarks>
/// The compiled pages through <see cref="SettingsPageHarness"/>, over real stores on one temp database
/// file. A store is made unreadable through its switch while the page loads, or as it saves; "the other
/// instance" is a second service over its own store on the same file. "Save not offered" is the
/// rendered disabled attribute on the page's own button. Nothing here reaches the directory: no case
/// adds a group grant. Conditions are asserted, not wording.
/// </remarks>
public sealed class SettingsSaveStoreCheckTests : IDisposable
{
    private const string Module = "MailboxPermissions";
    private const string GroupA = "S-1-5-21-1000-2000-3000-4001";
    private const string GroupB = "S-1-5-21-1000-2000-3000-4002";
    private const string Cfo = "cfo@contoso.com";
    private const string Ceo = "ceo@contoso.com";
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

    private void AssertOnlyARefusal(string section, string refusedAt, string shown)
    {
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(section, Text(record, "section"));
        Assert.Equal(refusedAt, Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(shown, Text(record, "error"));
    }

    // ---- what the stores hold -----------------------------------------------------------------

    private void SeedRules(string[]? users = null, string[]? patterns = null) =>
        new ProtectedPrincipalRepository(TestConfigStore.Create(_h.Root))
            .Save(new ProtectedPrincipalData(users ?? [], [], [], patterns ?? [], []));

    /// <summary>The protection rules as stored rows, read past every switch and past the marker.</summary>
    private List<string> StoredRules() =>
        TestConfigStore.Create(_h.Root).Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind || ':' || value FROM protected_principal ORDER BY 1;";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
                rows.Add(reader.GetString(0));
            return rows;
        });

    private void DropTable(string table) =>
        TestConfigStore.Create(_h.Root).Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DROP TABLE {table};";
            command.ExecuteNonQuery();
        });

    // ---- Admin Settings: protected principals -------------------------------------------------

    private static void OnProtectedTab(AdminSettingsPage page) =>
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "tab", "Protected Principals");

    private async Task AddPattern(AdminSettingsPage page, string pattern)
    {
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppNewPattern", pattern);
        await _h.RunAsync(page, "AddPpPattern");
    }

    [Fact]
    public async Task AdminSettings_AProtectedPageWhoseLoadFailed_DoesNotOfferSave_AndItsSaveKeepsEveryRule()
    {
        SeedRules(users: [Cfo], patterns: ["adm-*"]);
        _h.ProtectedStore.ReadsFail = true;
        var page = _h.OpenAdminSettings();
        _h.ProtectedStore.ReadsFail = false;
        OnProtectedTab(page);

        Assert.True(SettingsPageHarness.IsButtonDisabled(page, "Save"));

        // The worst form of the finding: an entry added on the page that loaded nothing, so the save
        // is not empty and nothing but the page's own load state stands between it and the rules.
        await AddPattern(page, "svc-*");
        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(new[] { "sam_pattern:adm-*", "user:" + Cfo }, StoredRules());
        AssertOnlyARefusal("ProtectedPrincipals", "StoreUnreadable", Status(page));
    }

    [Fact]
    public void AdminSettings_AProtectedPageThatLoaded_OffersSave()
    {
        // The control for the disabled button above.
        SeedRules(users: [Cfo]);
        var page = _h.OpenAdminSettings();
        OnProtectedTab(page);

        Assert.False(SettingsPageHarness.IsButtonDisabled(page, "Save"));
    }

    [Fact]
    public async Task AdminSettings_RulesUnreadableAsTheSaveWrites_AreRefused_AndNothingIsWritten()
    {
        SeedRules(users: [Cfo]);
        var page = _h.OpenAdminSettings();
        await AddPattern(page, "svc-*");
        DropTable("protected_principal_present");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(new[] { "user:" + Cfo }, StoredRules());
        AssertOnlyARefusal("ProtectedPrincipals", "StoreCorrupt", Status(page));
    }

    [Fact]
    public void ProtectedPrincipals_ASaveOverALegacyFileThatWasNotImported_IsRefused_AndLeavesTheStoreUnconfigured()
    {
        // The upgrade window: the rules are still in a legacy file the import refused. Writing now would
        // mark the database configured, and the file would never be imported.
        var config = Path.Combine(_h.Root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "protected-principals.json"), "{ not valid json");
        var service = _h.CreateOtherProtectedPrincipals();

        var outcome = service.ReplaceConfig(
            new ProtectedPrincipalConfig { SamAccountNamePatterns = ["svc-*"] }, pageLoadedRules: false, loadedVersion: "");

        Assert.Equal(ProtectedPrincipalSaveStatus.StoreCorrupt, outcome.Status);
        Assert.Empty(StoredRules());
        Assert.False(new ProtectedPrincipalRepository(TestConfigStore.Create(_h.Root)).IsConfigured());
    }

    [Fact]
    public async Task AdminSettings_ANoRulesSaveOverRulesAddedSinceThePageLoaded_IsRefused_AndTheRulesStay()
    {
        // The page loaded an empty store; the other instance has added a rule since. Saving the page's
        // empty lists now would delete a rule it never saw.
        var page = _h.OpenAdminSettings();
        _h.CreateOtherProtectedPrincipals().SaveConfig(new ProtectedPrincipalConfig { Users = [Cfo] });
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppDirectoryReadSecretId", "4242");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(new[] { "user:" + Cfo }, StoredRules());
        AssertOnlyARefusal("ProtectedPrincipals", "EmptyOverNonEmpty", Status(page));
        Assert.Null(_h.ModuleConfig.GetValue(ProtectedPrincipalService.ProtectedPrincipalsModuleKey, ProtectedPrincipalService.DirectoryReadSecretConfigKey));
    }

    [Fact]
    public async Task AdminSettings_RulesTheAdminRemovedOneByOne_AreSaved()
    {
        // The refusal above is for a page that loaded no rules, not for an admin emptying the list.
        SeedRules(users: [Cfo]);
        var page = _h.OpenAdminSettings();
        var users = SettingsPageHarness.Field<List<string>>(page, typeof(AdminSettingsPage), "ppUsers");
        Assert.True(users.Remove(Cfo), "the seeded rule did not load - this fixture is stale");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Empty(StoredRules());
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(new[] { "Users:" + Cfo }, Names(record, "removed"));
        Assert.Empty(Names(record, "added"));
    }

    [Fact]
    public async Task AdminSettings_ASecretSaveThatThrowsAfterTheRulesSaved_RecordsBoth_AndSaysTheRulesWereSaved()
    {
        SeedRules(users: [Cfo, Ceo]);
        var page = _h.OpenAdminSettings();
        Assert.True(SettingsPageHarness.Field<List<string>>(page, typeof(AdminSettingsPage), "ppUsers").Remove(Ceo));
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppDirectoryReadSecretId", "4242");
        // From the rules' write on (aud-a28r): the secret is compared on its store before the rules are written, so
        // a store failing from the start fails the whole save; one failing after the compare is this case.
        _h.ProtectedStore.BeforeWrite = () => _h.ModuleConfigStore.WritesFail = true;

        await _h.RunAsync(page, "SaveProtectedPrincipals");
        _h.ModuleConfigStore.WritesFail = false;

        // The rules landed and are in effect; the secret did not.
        Assert.Equal(new[] { "user:" + Cfo }, StoredRules());
        Assert.Null(_h.ModuleConfig.GetValue(ProtectedPrincipalService.ProtectedPrincipalsModuleKey, ProtectedPrincipalService.DirectoryReadSecretConfigKey));

        var records = _h.AuditEvents();
        Assert.Equal(2, records.Count);
        var rules = Assert.Single(records, r => Text(r, "section") == "ProtectedPrincipals");
        Assert.Equal("Success", Text(rules, "result"));
        Assert.Equal(new[] { "Users:" + Ceo }, Names(rules, "removed"));
        var secret = Assert.Single(records, r => Text(r, "section") == "ProtectedPrincipals:DirectoryReadSecretId");
        Assert.Equal("Failed", Text(secret, "result"));
        Assert.False(secret.TryGetProperty("refusedAt", out _));
        Assert.Equal(Status(page), Text(secret, "error"));

        Assert.Contains("rules were saved", Status(page), StringComparison.Ordinal);
        Assert.True(SettingsPageHarness.Field<bool>(page, typeof(AdminSettingsPage), "statusIsError"));
    }

    [Fact]
    public async Task AdminSettings_AChangedSecret_IsSavedWithItsOwnRecord()
    {
        var page = _h.OpenAdminSettings();
        await AddPattern(page, "svc-*");
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppDirectoryReadSecretId", " 4242 ");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal("4242", _h.ModuleConfig.GetValue(ProtectedPrincipalService.ProtectedPrincipalsModuleKey, ProtectedPrincipalService.DirectoryReadSecretConfigKey));
        var records = _h.AuditEvents();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal("Success", Text(r, "result")));
        var rules = Assert.Single(records, r => Text(r, "section") == "ProtectedPrincipals");
        Assert.Equal(new[] { "Patterns:svc-*" }, Names(rules, "added"));
        var secret = Assert.Single(records, r => Text(r, "section") == "ProtectedPrincipals:DirectoryReadSecretId");
        Assert.Equal(new[] { "4242" }, Names(secret, "added"));
    }

    // ---- Admin Settings: module enablement ----------------------------------------------------

    [Fact]
    public async Task AdminSettings_AnEnablementPageWhoseLoadFailed_DoesNotOfferSave_AndItsSaveWritesNothing()
    {
        new ModuleEnablementRepository(TestConfigStore.Create(_h.Root)).SaveAll(new Dictionary<string, bool> { [Module] = true });
        _h.EnablementStore.ReadsFail = true;
        var page = _h.OpenAdminSettings();
        _h.EnablementStore.ReadsFail = false;
        var before = _h.StoredEnablement();

        Assert.True(SettingsPageHarness.IsButtonDisabled(page, "Save"));

        var enablement = SettingsPageHarness.Field<Dictionary<string, bool>>(page, typeof(AdminSettingsPage), "moduleEnablement");
        enablement[Module] = !enablement[Module];
        await _h.RunAsync(page, "SaveEnablement");

        Assert.Equal(before, _h.StoredEnablement());
        AssertOnlyARefusal("ModuleEnablement", "StoreUnreadable", Status(page));
        Assert.Empty(_h.Navigation.Navigations);
    }

    // ---- Module Configuration -----------------------------------------------------------------

    private static void OnTab(ModuleConfigPage page, string tab) =>
        SettingsPageHarness.SetField(page, typeof(ModuleConfigPage), "tab", tab);

    [Fact]
    public async Task ModuleConfig_AStatusPageWhoseLoadFailed_DoesNotOfferSave_AndItsSaveWritesNothing()
    {
        _h.EnablementStore.ReadsFail = true;
        var page = _h.OpenModuleConfig(Module);
        _h.EnablementStore.ReadsFail = false;
        var before = _h.StoredEnablement();
        OnTab(page, "Module Status");

        Assert.True(SettingsPageHarness.IsButtonDisabled(page, "Save"));

        var enabled = SettingsPageHarness.Field<bool>(page, typeof(ModuleConfigPage), "moduleEnabled");
        SettingsPageHarness.SetField(page, typeof(ModuleConfigPage), "moduleEnabled", !enabled);
        await _h.RunAsync(page, "SaveModuleEnablementAsync");

        Assert.Equal(before, _h.StoredEnablement());
        AssertOnlyARefusal($"Module:{Module}", "StoreUnreadable", Status(page));
        Assert.Empty(_h.Navigation.Navigations);
    }

    [Fact]
    public async Task ModuleConfig_AnAccessPageWhoseLoadFailed_Loads_DoesNotOfferSave_AndItsSaveKeepsTheGrants()
    {
        new SectionAccessRepository(TestConfigStore.Create(_h.Root))
            .SaveAll(new Dictionary<string, string[]> { [Module] = [GroupA, GroupB] });
        _h.SectionStore.ReadsFail = true;
        var page = _h.OpenModuleConfig(Module);
        _h.SectionStore.ReadsFail = false;
        var before = _h.StoredSectionAccess();
        OnTab(page, "Section Access");

        Assert.True(SettingsPageHarness.Field<bool>(page, typeof(ModuleConfigPage), "authChecked"));
        Assert.True(SettingsPageHarness.IsButtonDisabled(page, "Save"));

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        Assert.Equal(before, _h.StoredSectionAccess());
        AssertOnlyARefusal(Module, "StoreUnreadable", Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AConfigPageWhoseLoadFailed_DoesNotOfferSave_AndItsSaveKeepsTheValues()
    {
        _h.ModuleConfig.SaveModuleConfig(Module, new Dictionary<string, string> { ["DelineaSecretId"] = "77" });
        _h.ModuleConfigStore.ReadsFail = true;
        var page = _h.OpenModuleConfig(Module);
        _h.ModuleConfigStore.ReadsFail = false;
        var before = _h.StoredModuleConfig(Module);
        OnTab(page, "Configuration");

        Assert.True(SettingsPageHarness.IsButtonDisabled(page, "Save"));

        SettingsPageHarness.Field<Dictionary<string, string>>(page, typeof(ModuleConfigPage), "currentConfigState")["DelineaSecretId"] = "4242";
        await _h.RunAsync(page, "SaveConfigAsync");

        Assert.Equal(before, _h.StoredModuleConfig(Module));
        AssertOnlyARefusal($"ModuleConfig:{Module}", "StoreUnreadable", Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AnAdminsPageWhoseLoadFailed_DoesNotOfferSave_AndItsSaveKeepsTheGroups()
    {
        new ModuleAdminRepository(TestConfigStore.Create(_h.Root)).SetForModule(Module, [GroupA, GroupB]);
        _h.AdminsStore.ReadsFail = true;
        var page = _h.OpenModuleConfig(Module);
        _h.AdminsStore.ReadsFail = false;
        var before = _h.StoredModuleAdmins(Module);
        OnTab(page, "Module Admins");

        Assert.True(SettingsPageHarness.IsButtonDisabled(page, "Save"));
        Assert.Contains("module_admins", SettingsPageHarness.RenderedText(page), StringComparison.Ordinal);

        await _h.RunAsync(page, "SaveModuleAdminsAsync");

        Assert.Equal(before, _h.StoredModuleAdmins(Module));
        AssertOnlyARefusal($"ModuleAdmins:{Module}", "StoreUnreadable", Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AnAdminsSaveOverAStoreUnreadableNow_IsRefused_AndKeepsTheGroups()
    {
        new ModuleAdminRepository(TestConfigStore.Create(_h.Root)).SetForModule(Module, [GroupA, GroupB]);
        var page = _h.OpenModuleConfig(Module);
        OnTab(page, "Module Admins");
        Assert.False(SettingsPageHarness.IsButtonDisabled(page, "Save"));
        Assert.True(SettingsPageHarness.Field<List<string>>(page, typeof(ModuleConfigPage), "moduleAdminGroups").Remove(GroupB));
        var before = _h.StoredModuleAdmins(Module);
        _h.AdminsStore.ReadsFail = true;

        await _h.RunAsync(page, "SaveModuleAdminsAsync");

        _h.AdminsStore.ReadsFail = false;
        Assert.Equal(before, _h.StoredModuleAdmins(Module));
        AssertOnlyARefusal($"ModuleAdmins:{Module}", "StoreCorrupt", Status(page));
    }

    // ---- Exchange Online Config ---------------------------------------------------------------

    private void SeedConnection() =>
        _h.ModuleConfig.SaveModuleConfig(ExoConnectionPool.ConfigModuleKey, new Dictionary<string, string>
        {
            [ExoConnectionPool.ConfigAppIdKey] = AppId,
            [ExoConnectionPool.ConfigOrganizationKey] = "contoso.onmicrosoft.com",
            [ExoConnectionPool.ConfigCertSubjectKey] = "CN=EXO-Automation",
        });

    private static void EnterConnection(ExchangeOnlineConfigPage page, string organization)
    {
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "exoAppId", AppId);
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "exoOrganization", organization);
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "exoCertSubject", "CN=EXO-Automation");
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AStatusPageWhoseLoadFailed_DoesNotOfferSave_AndItsSaveWritesNothing()
    {
        _h.EnablementStore.ReadsFail = true;
        var page = _h.OpenExchangeOnlineConfig();
        _h.EnablementStore.ReadsFail = false;
        var before = _h.StoredEnablement();

        Assert.True(SettingsPageHarness.IsButtonDisabled(page, "Save Status"));

        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "moduleEnabled", true);
        await _h.RunAsync(page, "SaveModuleStatus");

        Assert.Equal(before, _h.StoredEnablement());
        AssertOnlyARefusal("Module:ExchangeOnline", "StoreUnreadable", Status(page));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AStatusSaveOverACorruptStore_IsRefused_AndDoesNotWriteTheFallback()
    {
        new ModuleEnablementRepository(TestConfigStore.Create(_h.Root)).SaveAll(new Dictionary<string, bool> { [Module] = true });
        var page = _h.OpenExchangeOnlineConfig();
        Assert.False(SettingsPageHarness.IsButtonDisabled(page, "Save Status"));
        var before = _h.StoredEnablement();
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "moduleEnabled", true);
        _h.EnablementStore.ReadsFail = true;

        await _h.RunAsync(page, "SaveModuleStatus");

        _h.EnablementStore.ReadsFail = false;
        Assert.Equal(before, _h.StoredEnablement());
        AssertOnlyARefusal("Module:ExchangeOnline", "StoreCorrupt", Status(page));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AConnectionPageWhoseLoadFailed_DoesNotOfferSave_AndItsSaveKeepsTheConnection()
    {
        SeedConnection();
        _h.ModuleConfigStore.ReadsFail = true;
        var page = _h.OpenExchangeOnlineConfig();
        _h.ModuleConfigStore.ReadsFail = false;
        var before = _h.StoredModuleConfig(ExoConnectionPool.ConfigModuleKey);

        Assert.True(SettingsPageHarness.IsButtonDisabled(page, "Save Connection"));

        EnterConnection(page, "fabrikam.onmicrosoft.com");
        await _h.RunAsync(page, "SaveExoConfig");

        Assert.Equal(before, _h.StoredModuleConfig(ExoConnectionPool.ConfigModuleKey));
        AssertOnlyARefusal("ExchangeOnlineConfig", "StoreUnreadable", Status(page));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AConnectionSaveOverACorruptStore_IsRefused_AndKeepsTheConnection()
    {
        SeedConnection();
        var page = _h.OpenExchangeOnlineConfig();
        Assert.False(SettingsPageHarness.IsButtonDisabled(page, "Save Connection"));
        var before = _h.StoredModuleConfig(ExoConnectionPool.ConfigModuleKey);
        EnterConnection(page, "fabrikam.onmicrosoft.com");
        _h.ModuleConfigStore.ReadsFail = true;

        await _h.RunAsync(page, "SaveExoConfig");

        _h.ModuleConfigStore.ReadsFail = false;
        Assert.Equal(before, _h.StoredModuleConfig(ExoConnectionPool.ConfigModuleKey));
        AssertOnlyARefusal("ExchangeOnlineConfig", "StoreCorrupt", Status(page));
    }
}
