using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28n: the protected-principals save replaced every rule, and the module-config and module-admins
/// saves replaced a module's whole config or admin list, from the page's state - so a rule, key or group
/// changed elsewhere since the page loaded was overwritten.
/// </summary>
/// <remarks>
/// The aud-a33b style: "the other instance" is a second service over its own store and connection
/// factory on the same temp database file. A change made between a page's load and its save runs
/// between the two calls; one landing inside a save runs from the store's <c>BeforeWrite</c> hook as the
/// save's write opens. The compiled pages through <see cref="SettingsPageHarness"/>; no directory, no
/// group grant added. Conditions are asserted, not wording.
/// </remarks>
public sealed class SettingsPerRowSaveTests : IDisposable
{
    private const string Module = "MailboxPermissions";
    private const string GroupA = "S-1-5-21-1000-2000-3000-4001";
    private const string GroupB = "S-1-5-21-1000-2000-3000-4002";
    private const string Cfo = "cfo@contoso.com";
    private const string Ceo = "ceo@contoso.com";

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static string[] Names(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
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

    /// <summary>A save that wrote nothing because the store already held it: one success, nothing added or removed.</summary>
    private void AssertOnlyANoOp(string section, params string[] alreadyApplied)
    {
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(section, Text(record, "section"));
        Assert.Empty(Names(record, "added"));
        Assert.Empty(Names(record, "removed"));
        Assert.Equal(alreadyApplied, Names(record, "alreadyApplied"));
    }

    // ---- Admin Settings: protected principals -------------------------------------------------

    private void SeedRules(params string[] users) =>
        new ProtectedPrincipalRepository(TestConfigStore.Create(_h.Root))
            .Save(new ProtectedPrincipalData(users, [], [], [], []));

    /// <summary>The protection rules as stored rows, read past every switch.</summary>
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

    private async Task AddPattern(AdminSettingsPage page, string pattern)
    {
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppNewPattern", pattern);
        await _h.RunAsync(page, "AddPpPattern");
    }

    [Fact]
    public async Task AdminSettings_ARuleAddedElsewhereSinceThePageLoaded_RefusesTheSave_AndTheRuleStays()
    {
        SeedRules(Cfo);
        var page = _h.OpenAdminSettings();
        _h.CreateOtherProtectedPrincipals().SaveConfig(new ProtectedPrincipalConfig { Users = [Cfo, Ceo] });
        await AddPattern(page, "svc-*");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(new[] { "user:" + Ceo, "user:" + Cfo }, StoredRules());
        AssertOnlyARefusal("ProtectedPrincipals", "ConcurrentChange", Status(page));
    }

    [Fact]
    public async Task AdminSettings_ARuleAddedElsewhereInsideTheSave_RefusesTheSave_AndTheRuleStays()
    {
        SeedRules(Cfo);
        var page = _h.OpenAdminSettings();
        await AddPattern(page, "svc-*");
        _h.ProtectedStore.BeforeWrite = () =>
            _h.CreateOtherProtectedPrincipals().SaveConfig(new ProtectedPrincipalConfig { Users = [Cfo, Ceo] });

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Null(_h.ProtectedStore.BeforeWrite);
        Assert.Equal(new[] { "user:" + Ceo, "user:" + Cfo }, StoredRules());
        AssertOnlyARefusal("ProtectedPrincipals", "ConcurrentChange", Status(page));
    }

    [Fact]
    public async Task AdminSettings_TheSameRuleAddedElsewhere_IsANoOp_NotARefusal()
    {
        SeedRules(Cfo);
        var page = _h.OpenAdminSettings();
        await AddPattern(page, "svc-*");
        _h.CreateOtherProtectedPrincipals().SaveConfig(new ProtectedPrincipalConfig { Users = [Cfo], SamAccountNamePatterns = ["svc-*"] });
        var token = _h.ProtectedStore.GetChangeToken();

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(token, _h.ProtectedStore.GetChangeToken());
        Assert.Equal(new[] { "sam_pattern:svc-*", "user:" + Cfo }, StoredRules());
        AssertOnlyANoOp("ProtectedPrincipals", "Patterns:svc-*");
        Assert.False(SettingsPageHarness.Field<bool>(page, typeof(AdminSettingsPage), "statusIsError"));
    }

    [Fact]
    public async Task AdminSettings_TwoSavesFromOnePage_BothLand()
    {
        // The version the first save leaves is the one the page holds for the second.
        SeedRules(Cfo);
        var page = _h.OpenAdminSettings();
        await AddPattern(page, "svc-*");
        await _h.RunAsync(page, "SaveProtectedPrincipals");
        await AddPattern(page, "adm-*");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(new[] { "sam_pattern:adm-*", "sam_pattern:svc-*", "user:" + Cfo }, StoredRules());
        var records = _h.AuditEvents();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal("Success", Text(r, "result")));
        Assert.Equal(new[] { "Patterns:adm-*" }, Names(records[1], "added"));
    }

    // ---- Module Configuration: Configuration tab ----------------------------------------------

    private static Dictionary<string, string> ConfigState(ModuleConfigPage page) =>
        SettingsPageHarness.Field<Dictionary<string, string>>(page, typeof(ModuleConfigPage), "currentConfigState");

    private Dictionary<string, string> StoredConfig()
    {
        Assert.True(new ModuleConfigRepository(TestConfigStore.Create(_h.Root)).TryReadModule(Module, out var config));
        return config;
    }

    private void SeedConfig() =>
        new ModuleConfigRepository(TestConfigStore.Create(_h.Root)).SaveModule(Module, new Dictionary<string, string>
        {
            ["DelineaSecretId"] = "77",
            ["PreventSelfGrant"] = "true",
            ["ExcludedUsers"] = Cfo,
        });

    [Fact]
    public async Task ModuleConfig_KeysChangedElsewhereSinceThePageLoaded_AreNotReverted()
    {
        // ExcludedUsers is a protection list the page does not show: the whole-config save wrote it back as
        // the page had loaded it.
        SeedConfig();
        var page = _h.OpenModuleConfig(Module);
        _h.CreateOtherModuleConfig().UpdateModuleValues(Module, new Dictionary<string, string>
        {
            ["PreventSelfGrant"] = "false",
            ["ExcludedUsers"] = Cfo + ";" + Ceo,
        });
        ConfigState(page)["DelineaSecretId"] = "4242";

        await _h.RunAsync(page, "SaveConfigAsync");

        var stored = StoredConfig();
        Assert.Equal("4242", stored["DelineaSecretId"]);
        Assert.Equal("false", stored["PreventSelfGrant"]);
        Assert.Equal(Cfo + ";" + Ceo, stored["ExcludedUsers"]);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(new[] { "DelineaSecretId=77" }, Names(record, "removed"));
        Assert.Equal(new[] { "DelineaSecretId=4242" }, Names(record, "added"));
    }

    [Fact]
    public async Task ModuleConfig_TheAdminsOwnKeyChangedElsewhere_RefusesTheSave_NamingTheKey()
    {
        SeedConfig();
        var page = _h.OpenModuleConfig(Module);
        _h.CreateOtherModuleConfig().UpdateModuleValues(Module, new Dictionary<string, string> { ["DelineaSecretId"] = "88" });
        ConfigState(page)["DelineaSecretId"] = "4242";
        ConfigState(page)["PreventSelfGrant"] = "false";

        await _h.RunAsync(page, "SaveConfigAsync");

        var stored = StoredConfig();
        Assert.Equal("88", stored["DelineaSecretId"]);
        Assert.Equal("true", stored["PreventSelfGrant"]);
        AssertOnlyARefusal($"ModuleConfig:{Module}", "ConcurrentChange", Status(page));
        Assert.Contains("DelineaSecretId", Status(page), StringComparison.Ordinal);
        Assert.DoesNotContain("PreventSelfGrant", Status(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModuleConfig_TheAdminsOwnKeyChangedElsewhereInsideTheSave_RefusesTheSave()
    {
        SeedConfig();
        var page = _h.OpenModuleConfig(Module);
        ConfigState(page)["DelineaSecretId"] = "4242";
        _h.ModuleConfigStore.BeforeWrite = () =>
            _h.CreateOtherModuleConfig().UpdateModuleValues(Module, new Dictionary<string, string> { ["DelineaSecretId"] = "88" });

        await _h.RunAsync(page, "SaveConfigAsync");

        Assert.Null(_h.ModuleConfigStore.BeforeWrite);
        Assert.Equal("88", StoredConfig()["DelineaSecretId"]);
        AssertOnlyARefusal($"ModuleConfig:{Module}", "ConcurrentChange", Status(page));
    }

    [Fact]
    public async Task ModuleConfig_AnEqualChangeMadeElsewhere_IsANoOp_NotARefusal()
    {
        SeedConfig();
        var page = _h.OpenModuleConfig(Module);
        _h.CreateOtherModuleConfig().UpdateModuleValues(Module, new Dictionary<string, string> { ["DelineaSecretId"] = "4242" });
        ConfigState(page)["DelineaSecretId"] = "4242";
        var token = _h.ModuleConfigStore.GetChangeToken();

        await _h.RunAsync(page, "SaveConfigAsync");

        Assert.Equal(token, _h.ModuleConfigStore.GetChangeToken());
        Assert.Equal("4242", StoredConfig()["DelineaSecretId"]);
        AssertOnlyANoOp($"ModuleConfig:{Module}", "DelineaSecretId");
        Assert.False(SettingsPageHarness.Field<bool>(page, typeof(ModuleConfigPage), "statusIsError"));
    }

    // ---- Module Configuration: Module admins tab ----------------------------------------------

    private static List<string> AdminList(ModuleConfigPage page) =>
        SettingsPageHarness.Field<List<string>>(page, typeof(ModuleConfigPage), "moduleAdminGroups");

    private string[] StoredAdmins() =>
        new ModuleAdminRepository(TestConfigStore.Create(_h.Root)).GetForModule(Module).Order(StringComparer.Ordinal).ToArray();

    private void SeedAdmins(params string[] groups) =>
        new ModuleAdminRepository(TestConfigStore.Create(_h.Root)).SetForModule(Module, groups);

    [Fact]
    public async Task ModuleConfig_AnAdminGroupAddedElsewhere_IsKept()
    {
        SeedAdmins(GroupA);
        var page = _h.OpenModuleConfig(Module);
        _h.CreateOtherModuleAdmins().ApplyAdminChanges(Module, [GroupB], [], TestContext.Current.CancellationToken);
        Assert.True(AdminList(page).Remove(GroupA));

        await _h.RunAsync(page, "SaveModuleAdminsAsync");

        Assert.Equal(new[] { GroupB }, StoredAdmins());
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(new[] { GroupA }, Names(record, "removed"));
        Assert.Empty(Names(record, "added"));
    }

    [Fact]
    public async Task ModuleConfig_AnAdminGroupRemovedElsewhere_IsNotRestored()
    {
        SeedAdmins(GroupA, GroupB);
        var page = _h.OpenModuleConfig(Module);
        _h.CreateOtherModuleAdmins().ApplyAdminChanges(Module, [], [GroupB], TestContext.Current.CancellationToken);
        Assert.True(AdminList(page).Remove(GroupA));

        await _h.RunAsync(page, "SaveModuleAdminsAsync");

        Assert.Empty(StoredAdmins());
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal(new[] { GroupA }, Names(record, "removed"));
    }

    [Fact]
    public async Task ModuleConfig_AnEqualAdminRemovalElsewhere_IsANoOp()
    {
        SeedAdmins(GroupA, GroupB);
        var page = _h.OpenModuleConfig(Module);
        _h.CreateOtherModuleAdmins().ApplyAdminChanges(Module, [], [GroupB], TestContext.Current.CancellationToken);
        Assert.True(AdminList(page).Remove(GroupB));
        var token = _h.AdminsStore.GetChangeToken();

        await _h.RunAsync(page, "SaveModuleAdminsAsync");

        Assert.Equal(token, _h.AdminsStore.GetChangeToken());
        Assert.Equal(new[] { GroupA }, StoredAdmins());
        AssertOnlyANoOp($"ModuleAdmins:{Module}", GroupB);
    }
}
