using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;
using ExchangeOnlineConfigPage = ExchangeAdminWeb.Components.Pages.ExchangeOnlineConfig;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26n: the Exchange Online connection save wrote all three keys whatever changed and the directory-read
/// secret was written with no compare, so a change made elsewhere was overwritten; a secret config field changed
/// value to value left no trace in its record; a cleared secret showed the appsettings fallback; an access save
/// that wrote nothing still wrote the page's stale display names; and a protected-principals save that found its
/// change already applied kept the page's stale entries, so the next save wrote their casing back.
/// </summary>
/// <remarks>
/// The aud-a33b style: "the other instance" is a second service over its own store and connection factory on
/// the same temp database file; a change landing inside a save runs from the store's <c>BeforeWrite</c> hook as
/// the save's write opens. The compiled pages through <see cref="SettingsPageHarness"/>; no directory, no
/// Exchange Online, no group grant added. Conditions are asserted, not wording.
/// </remarks>
public sealed class SettingsConnectionAndSecretSaveTests : IDisposable
{
    private const string AppId = "6f9619ff-8b86-d011-b42d-00c04fc964ff";
    private const string Module = "MailboxPermissions";
    private const string GroupA = "S-1-5-21-1000-2000-3000-4001";
    private const string GroupB = "S-1-5-21-1000-2000-3000-4002";
    private const string GroupC = "S-1-5-21-1000-2000-3000-4003";
    private const string GroupD = "S-1-5-21-1000-2000-3000-4004";
    private const string Cfo = "cfo@contoso.com";
    private const string SecretField = "ApiToken";
    private const string SecretSection = "ProtectedPrincipals:DirectoryReadSecretId";
    private const string FallbackSetting = "Security:ProtectedPrincipalDirectoryReadSecretId";

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

    private static bool IsError<TPage>(TPage page) =>
        SettingsPageHarness.Field<bool>(page!, typeof(TPage), "statusIsError");

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
    private static void AssertANoOp(JsonElement record, string section, params string[] alreadyApplied)
    {
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(section, Text(record, "section"));
        Assert.Empty(Names(record, "added"));
        Assert.Empty(Names(record, "removed"));
        Assert.Equal(alreadyApplied, Names(record, "alreadyApplied"));
    }

    // ---- Exchange Online Config: the connection ----------------------------------------------

    private void SeedConnection(bool withCertificate = true)
    {
        var values = new Dictionary<string, string>
        {
            [ExoConnectionPool.ConfigAppIdKey] = AppId,
            [ExoConnectionPool.ConfigOrganizationKey] = "contoso.onmicrosoft.com",
        };
        if (withCertificate)
            values[ExoConnectionPool.ConfigCertSubjectKey] = "CN=EXO-Automation";
        new ModuleConfigRepository(TestConfigStore.Create(_h.Root)).SaveModule(ExoConnectionPool.ConfigModuleKey, values);
    }

    private Dictionary<string, string> StoredConnection()
    {
        Assert.True(new ModuleConfigRepository(TestConfigStore.Create(_h.Root))
            .TryReadModule(ExoConnectionPool.ConfigModuleKey, out var values), "the connection is unreadable");
        return values;
    }

    private void OtherInstanceSetsConnection(string key, string value) =>
        _h.CreateOtherModuleConfig().UpdateModuleValues(
            ExoConnectionPool.ConfigModuleKey, new Dictionary<string, string> { [key] = value });

    private static void EnterOrganization(ExchangeOnlineConfigPage page, string organization) =>
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "exoOrganization", organization);

    [Fact]
    public async Task ExchangeOnlineConfig_AKeyChangedElsewhereAndNotEditedHere_IsNotReverted()
    {
        SeedConnection();
        var page = _h.OpenExchangeOnlineConfig();
        OtherInstanceSetsConnection(ExoConnectionPool.ConfigCertSubjectKey, "CN=EXO-Rotated");
        EnterOrganization(page, "fabrikam.onmicrosoft.com");

        await _h.RunAsync(page, "SaveExoConfig");

        var stored = StoredConnection();
        Assert.Equal("CN=EXO-Rotated", stored[ExoConnectionPool.ConfigCertSubjectKey]);
        Assert.Equal("fabrikam.onmicrosoft.com", stored[ExoConnectionPool.ConfigOrganizationKey]);
        Assert.Equal(AppId, stored[ExoConnectionPool.ConfigAppIdKey]);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(new[] { "Org=contoso.onmicrosoft.com" }, Names(record, "removed"));
        Assert.Equal(new[] { "Org=fabrikam.onmicrosoft.com" }, Names(record, "added"));
        Assert.False(IsError(page));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AKeyShownFromTheFallbackAndNotEdited_IsNotWritten()
    {
        // The certificate is not stored: the page shows the default the runtime falls back to, and a save that
        // did not edit it leaves it to that fallback rather than writing the default into the store.
        SeedConnection(withCertificate: false);
        var page = _h.OpenExchangeOnlineConfig();
        Assert.Equal("CN=EXO-Automation", SettingsPageHarness.Field<string>(page, typeof(ExchangeOnlineConfigPage), "exoCertSubject"));
        EnterOrganization(page, "fabrikam.onmicrosoft.com");

        await _h.RunAsync(page, "SaveExoConfig");

        var stored = StoredConnection();
        Assert.False(stored.ContainsKey(ExoConnectionPool.ConfigCertSubjectKey));
        Assert.Equal("fabrikam.onmicrosoft.com", stored[ExoConnectionPool.ConfigOrganizationKey]);
    }

    [Fact]
    public async Task ExchangeOnlineConfig_TheAdminsOwnKeyChangedElsewhere_RefusesTheSave_NamingIt()
    {
        SeedConnection();
        var page = _h.OpenExchangeOnlineConfig();
        OtherInstanceSetsConnection(ExoConnectionPool.ConfigOrganizationKey, "northwind.onmicrosoft.com");
        EnterOrganization(page, "fabrikam.onmicrosoft.com");
        var token = _h.ModuleConfigStore.GetChangeToken();

        await _h.RunAsync(page, "SaveExoConfig");

        Assert.Equal(token, _h.ModuleConfigStore.GetChangeToken());
        Assert.Equal("northwind.onmicrosoft.com", StoredConnection()[ExoConnectionPool.ConfigOrganizationKey]);
        AssertOnlyARefusal("ExchangeOnlineConfig", "ConcurrentChange", Status(page));
        Assert.Contains("Organization", Status(page), StringComparison.Ordinal);
        Assert.DoesNotContain("App Registration ID", Status(page), StringComparison.Ordinal);
        Assert.True(IsError(page));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_TheAdminsOwnKeyChangedElsewhereInsideTheSave_RefusesTheSave()
    {
        SeedConnection();
        var page = _h.OpenExchangeOnlineConfig();
        EnterOrganization(page, "fabrikam.onmicrosoft.com");
        _h.ModuleConfigStore.BeforeWrite = () =>
            OtherInstanceSetsConnection(ExoConnectionPool.ConfigOrganizationKey, "northwind.onmicrosoft.com");

        await _h.RunAsync(page, "SaveExoConfig");

        Assert.Null(_h.ModuleConfigStore.BeforeWrite);
        Assert.Equal("northwind.onmicrosoft.com", StoredConnection()[ExoConnectionPool.ConfigOrganizationKey]);
        AssertOnlyARefusal("ExchangeOnlineConfig", "ConcurrentChange", Status(page));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AnEqualChangeMadeElsewhere_IsANoOp_NotARefusal()
    {
        SeedConnection();
        var page = _h.OpenExchangeOnlineConfig();
        OtherInstanceSetsConnection(ExoConnectionPool.ConfigOrganizationKey, "fabrikam.onmicrosoft.com");
        EnterOrganization(page, "fabrikam.onmicrosoft.com");
        var token = _h.ModuleConfigStore.GetChangeToken();

        await _h.RunAsync(page, "SaveExoConfig");

        Assert.Equal(token, _h.ModuleConfigStore.GetChangeToken());
        AssertANoOp(Assert.Single(_h.AuditEvents()), "ExchangeOnlineConfig", ExoConnectionPool.ConfigOrganizationKey);
        Assert.False(IsError(page));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_NothingEdited_WritesNothing_AndRecordsNothing()
    {
        SeedConnection();
        var page = _h.OpenExchangeOnlineConfig();
        var token = _h.ModuleConfigStore.GetChangeToken();

        await _h.RunAsync(page, "SaveExoConfig");

        Assert.Equal(token, _h.ModuleConfigStore.GetChangeToken());
        Assert.Empty(_h.AuditEvents());
        Assert.False(IsError(page));
    }

    // ---- Admin Settings: the directory-read secret -------------------------------------------

    private void SeedRules(params string[] users) =>
        new ProtectedPrincipalRepository(TestConfigStore.Create(_h.Root))
            .Save(new ProtectedPrincipalData(users, [], [], [], []));

    private void SeedSecret(string value) =>
        new ModuleConfigRepository(TestConfigStore.Create(_h.Root)).SaveModule(
            ProtectedPrincipalService.ProtectedPrincipalsModuleKey,
            new Dictionary<string, string> { [ProtectedPrincipalService.DirectoryReadSecretConfigKey] = value });

    private string? StoredSecret()
    {
        Assert.True(new ModuleConfigRepository(TestConfigStore.Create(_h.Root))
            .TryReadModule(ProtectedPrincipalService.ProtectedPrincipalsModuleKey, out var values), "the secret is unreadable");
        return values.GetValueOrDefault(ProtectedPrincipalService.DirectoryReadSecretConfigKey);
    }

    private void OtherInstanceSetsSecret(string value) =>
        _h.CreateOtherModuleConfig().UpdateModuleValues(
            ProtectedPrincipalService.ProtectedPrincipalsModuleKey,
            new Dictionary<string, string> { [ProtectedPrincipalService.DirectoryReadSecretConfigKey] = value });

    // The field is bound to SetPpDirectoryReadSecretId; set directly, as a stale render or a second tab reaches it.
    private static void EnterSecret(AdminSettingsPage page, string value) =>
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppDirectoryReadSecretId", value);

    private static string SecretShown(AdminSettingsPage page) =>
        SettingsPageHarness.Field<string>(page, typeof(AdminSettingsPage), "ppDirectoryReadSecretId");

    // aud-a28r. A secret changed elsewhere is compared before the rules are written, so its refusal is the whole
    // save's: one record under the save's section. The window left after that compare is SettingsWholeProtectedSaveTests'.
    [Fact]
    public async Task AdminSettings_ASecretChangedElsewhere_IsNotOverwritten_AndItsSaveIsRefused()
    {
        SeedRules(Cfo);
        SeedSecret("777");
        var page = _h.OpenAdminSettings();
        OtherInstanceSetsSecret("888");
        EnterSecret(page, "4242");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal("888", StoredSecret());
        AssertOnlyARefusal("ProtectedPrincipals", "ConcurrentChange", Status(page));
        Assert.True(IsError(page));
    }

    [Fact]
    public async Task AdminSettings_ASecretChangedElsewhereInsideTheSave_IsNotOverwritten()
    {
        // The module-config store's first write is the secret's compare, made before the rules' write.
        SeedRules(Cfo);
        SeedSecret("777");
        var page = _h.OpenAdminSettings();
        EnterSecret(page, "4242");
        _h.ModuleConfigStore.BeforeWrite = () => OtherInstanceSetsSecret("888");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Null(_h.ModuleConfigStore.BeforeWrite);
        Assert.Equal("888", StoredSecret());
        AssertOnlyARefusal("ProtectedPrincipals", "ConcurrentChange", Status(page));
    }

    /// <summary>When the secret's row was last written. The change token will not do here: it is the database's, and the rules' write moves it.</summary>
    private string SecretWrittenAt() =>
        TestConfigStore.Create(_h.Root).Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT updated_at FROM module_config WHERE module_id = $id AND config_key = $key;";
            command.Parameters.AddWithValue("$id", ProtectedPrincipalService.ProtectedPrincipalsModuleKey);
            command.Parameters.AddWithValue("$key", ProtectedPrincipalService.DirectoryReadSecretConfigKey);
            return command.ExecuteScalar() as string ?? "";
        });

    [Fact]
    public async Task AdminSettings_ASecretChangedElsewhereToTheSameValue_IsARecordedNoOp()
    {
        SeedRules(Cfo);
        SeedSecret("777");
        var page = _h.OpenAdminSettings();
        OtherInstanceSetsSecret("4242");
        EnterSecret(page, "4242");
        var writtenAt = SecretWrittenAt();

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(writtenAt, SecretWrittenAt());
        Assert.Equal("4242", StoredSecret());
        AssertANoOp(Assert.Single(_h.AuditEvents(), r => Text(r, "section") == SecretSection),
            SecretSection, ProtectedPrincipalService.DirectoryReadSecretConfigKey);
        Assert.False(IsError(page));
    }

    // ---- Admin Settings: a cleared secret and the appsettings fallback -----------------------

    [Fact]
    public void AdminSettings_AClearedSecretWithAFallbackConfigured_ShowsNotSetInTheDatabase()
    {
        SeedSecret("");
        _h.Config[FallbackSetting] = "55";

        var page = _h.OpenAdminSettings();

        Assert.Equal("", SecretShown(page));
        var text = SettingsPageHarness.RenderedText(page);
        Assert.Contains("Not set in the database; the configured fallback applies", text, StringComparison.Ordinal);
        Assert.Contains("Secret ID 55", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminSettings_AStoredSecretWithAFallbackConfigured_ShowsTheStoredId_AndNoFallback()
    {
        // The control for the case above.
        SeedSecret("777");
        _h.Config[FallbackSetting] = "55";

        var page = _h.OpenAdminSettings();

        Assert.Equal("777", SecretShown(page));
        Assert.DoesNotContain("fallback applies", SettingsPageHarness.RenderedText(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminSettings_ClearingTheSecretWithAFallbackConfigured_IsThenShownAsNotSetInTheDatabase()
    {
        SeedRules(Cfo);
        SeedSecret("777");
        _h.Config[FallbackSetting] = "55";
        var page = _h.OpenAdminSettings();
        EnterSecret(page, "");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal("", StoredSecret());
        Assert.Equal("", SecretShown(page));
        Assert.Contains("Not set in the database; the configured fallback applies",
            SettingsPageHarness.RenderedText(page), StringComparison.Ordinal);
    }

    // ---- Module Configuration: a secret field in the record ----------------------------------

    private static Dictionary<string, string> ConfigState(ModuleConfigPage page) =>
        SettingsPageHarness.Field<Dictionary<string, string>>(page, typeof(ModuleConfigPage), "currentConfigState");

    private Dictionary<string, string> StoredConfig()
    {
        Assert.True(new ModuleConfigRepository(TestConfigStore.Create(_h.Root)).TryReadModule(Module, out var config));
        return config;
    }

    private void SeedConfig(Dictionary<string, string> values) =>
        new ModuleConfigRepository(TestConfigStore.Create(_h.Root)).SaveModule(Module, values);

    /// <summary>
    /// Module Configuration with one secret field added to the module's descriptor. No catalog field is a secret
    /// today; the record's masking is for the descriptor that declares one.
    /// </summary>
    private ModuleConfigPage OpenWithASecretField()
    {
        var page = _h.OpenModuleConfig(Module);
        var module = SettingsPageHarness.Field<AdminModuleDescriptor>(page, typeof(ModuleConfigPage), "module");
        SettingsPageHarness.SetField(page, typeof(ModuleConfigPage), "module", module with
        {
            ConfigFields = [.. module.ConfigFields, new ModuleConfigField(SecretField, "API token", "", Required: false, IsSecret: true)],
        });
        return page;
    }

    [Fact]
    public async Task ModuleConfig_ASecretFieldChangedValueToValue_IsNamedInChangedSecretKeys_AndNotAsAddedOrRemoved()
    {
        SeedConfig(new Dictionary<string, string> { ["DelineaSecretId"] = "77", [SecretField] = "old-token-value" });
        var page = OpenWithASecretField();
        ConfigState(page)[SecretField] = "new-token-value";
        ConfigState(page)["DelineaSecretId"] = "4242";

        await _h.RunAsync(page, "SaveConfigAsync");

        Assert.Equal("new-token-value", StoredConfig()[SecretField]);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(new[] { SecretField }, Names(record, "changedSecretKeys"));
        Assert.Equal(new[] { "DelineaSecretId=77" }, Names(record, "removed"));
        Assert.Equal(new[] { "DelineaSecretId=4242" }, Names(record, "added"));
        var raw = record.GetRawText();
        Assert.DoesNotContain("old-token-value", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("new-token-value", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModuleConfig_ASecretFieldSetFromEmpty_IsMaskedOnlyWhereItHasAValue()
    {
        SeedConfig(new Dictionary<string, string> { ["DelineaSecretId"] = "77" });
        var page = OpenWithASecretField();
        ConfigState(page)[SecretField] = "first-token-value";

        await _h.RunAsync(page, "SaveConfigAsync");

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal(new[] { SecretField + "=(empty)" }, Names(record, "removed"));
        Assert.Equal(new[] { SecretField + "=***" }, Names(record, "added"));
        Assert.Equal(new[] { SecretField }, Names(record, "changedSecretKeys"));
        Assert.DoesNotContain("first-token-value", record.GetRawText(), StringComparison.Ordinal);
    }

    // ---- Module Configuration: display names after an access save ----------------------------

    private void SeedAccess(Dictionary<string, string[]> access, Dictionary<string, string> names)
    {
        var repository = new SectionAccessRepository(TestConfigStore.Create(_h.Root));
        repository.SaveAll(access);
        repository.SaveDisplayNames(names);
    }

    private Dictionary<string, string[]> StoredAccess()
    {
        Assert.True(new SectionAccessRepository(TestConfigStore.Create(_h.Root)).TryGetAll(out var access));
        return access;
    }

    private Dictionary<string, string> StoredDisplayNames() =>
        new SectionAccessRepository(TestConfigStore.Create(_h.Root)).GetDisplayNames();

    private static List<string> AccessList(ModuleConfigPage page, string alias) =>
        SettingsPageHarness.Field<Dictionary<string, List<string>>>(page, typeof(ModuleConfigPage), "accessState")[alias];

    [Fact]
    public async Task ModuleConfig_AnAccessSaveWhoseEditWasAlreadyApplied_WritesNoDisplayName()
    {
        // Removals only: a grant the save adds would be asked of the directory, and nothing here may be.
        SeedAccess(new() { [Module] = [GroupA, GroupB] }, new() { [GroupA] = "EXAMPLE\\Old-A" });
        var page = _h.OpenModuleConfig(Module);
        var other = _h.CreateOtherSectionAccess();
        Assert.Equal(SectionAccessSaveStatus.Saved, other.SaveEditedAliases(
            new Dictionary<string, string[]> { [Module] = [GroupA, GroupB] },
            new Dictionary<string, string[]> { [Module] = [GroupA] }).Status);
        other.SaveGroupDisplayNames(new Dictionary<string, string> { [GroupA] = "EXAMPLE\\New-A" });
        Assert.True(AccessList(page, Module).Remove(GroupB));
        var token = _h.SectionStore.GetChangeToken();

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        Assert.Equal(token, _h.SectionStore.GetChangeToken());
        Assert.Equal("EXAMPLE\\New-A", StoredDisplayNames()[GroupA]);
        AssertANoOp(Assert.Single(_h.AuditEvents()), Module, Module);
    }

    [Fact]
    public async Task ModuleConfig_AnAccessSave_DoesNotRevertADisplayNameChangedElsewhere()
    {
        SeedAccess(new() { [Module] = [GroupA, GroupB] }, new() { [GroupA] = "EXAMPLE\\Old-A" });
        var page = _h.OpenModuleConfig(Module);
        _h.CreateOtherSectionAccess().SaveGroupDisplayNames(new Dictionary<string, string> { [GroupA] = "EXAMPLE\\New-A" });
        Assert.True(AccessList(page, Module).Remove(GroupB));

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        Assert.Equal(new[] { GroupA }, StoredAccess()[Module]);
        Assert.Equal("EXAMPLE\\New-A", StoredDisplayNames()[GroupA]);
    }

    [Fact]
    public void DisplayNamesToStore_IsThePickedNamesOfTheGroupsAddedToTheAliasesWritten()
    {
        var names = ModuleConfigPage.DisplayNamesToStore(
            written: new Dictionary<string, string[]> { ["Alias1"] = [GroupA, GroupB, GroupD] },
            loaded: new Dictionary<string, List<string>> { ["Alias1"] = [GroupA], ["Alias2"] = [GroupC] },
            picked: new Dictionary<string, string>
            {
                [GroupA] = "EXAMPLE\\A",
                [GroupB] = "EXAMPLE\\B",
                [GroupC] = "EXAMPLE\\C",
            });

        // A was loaded, not added; C is on an alias not written; D was added with no name picked.
        Assert.Equal(new Dictionary<string, string> { [GroupB] = "EXAMPLE\\B" }, names);
    }

    // ---- Admin Settings: rules already applied elsewhere -------------------------------------

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
    public async Task AdminSettings_AfterAnAlreadyAppliedSave_ThePageHoldsTheStoredRows_AndALaterSaveKeepsTheirCasing()
    {
        // The other save changed only an entry's casing: the same rules as the page's, ignoring case, so the
        // page's save is already applied.
        SeedRules(Cfo);
        var page = _h.OpenAdminSettings();
        _h.CreateOtherProtectedPrincipals().SaveConfig(new ProtectedPrincipalConfig { Users = ["CFO@Contoso.com"] });

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(new[] { "CFO@Contoso.com" }, SettingsPageHarness.Field<List<string>>(page, typeof(AdminSettingsPage), "ppUsers"));
        Assert.Equal(new[] { "CFO@Contoso.com" }, SettingsPageHarness.Field<List<string>>(page, typeof(AdminSettingsPage), "ppUsersBaseline"));

        await AddPattern(page, "svc-*");
        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(new[] { "sam_pattern:svc-*", "user:CFO@Contoso.com" }, StoredRules());
        var records = _h.AuditEvents();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal("Success", Text(r, "result")));
        Assert.Equal(new[] { "Patterns:svc-*" }, Names(records[1], "added"));
    }
}
