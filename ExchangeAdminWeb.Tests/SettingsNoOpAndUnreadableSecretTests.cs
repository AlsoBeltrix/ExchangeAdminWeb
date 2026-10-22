using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26k: a section-access change made elsewhere that already equals the save was refused as a
/// conflict, and Admin Settings showed the directory-read secret from a fail-open read, so an
/// unreadable store looked unset and a save could write over a value the page never showed.
/// </summary>
/// <remarks>
/// The compiled pages through <see cref="SettingsPageHarness"/>; "the other instance" is a second
/// service over its own store on the same temp database file. Nothing here reaches the directory: no
/// case adds a group grant. Conditions are asserted, not wording.
/// </remarks>
public sealed class SettingsNoOpAndUnreadableSecretTests : IDisposable
{
    private const string Module = "MailboxPermissions";
    private const string OnPremAlias = "MailboxPermissionsOnPrem";
    private const string GroupA = "S-1-5-21-1000-2000-3000-4001";
    private const string GroupB = "S-1-5-21-1000-2000-3000-4002";
    private const string Cfo = "cfo@contoso.com";
    private const string SecretPlaceholder = "Required when groups or OUs are configured";

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

    // ---- section access: a change made elsewhere equal to the save ----------------------------

    private void SeedAccess(string[] module, string[]? onPrem = null)
    {
        var access = new Dictionary<string, string[]> { [Module] = module };
        if (onPrem is not null)
            access[OnPremAlias] = onPrem;
        new SectionAccessRepository(TestConfigStore.Create(_h.Root)).SaveAll(access);
    }

    private Dictionary<string, string[]> StoredAccess()
    {
        Assert.True(new SectionAccessRepository(TestConfigStore.Create(_h.Root)).TryGetAll(out var access));
        return access;
    }

    private static List<string> AccessList(ModuleConfigPage page, string alias) =>
        SettingsPageHarness.Field<Dictionary<string, List<string>>>(page, typeof(ModuleConfigPage), "accessState")[alias];

    /// <summary>The other instance saves <paramref name="alias"/> from <paramref name="was"/> to <paramref name="now"/>.</summary>
    private void OtherInstanceSaves(string alias, string[] was, string[] now) =>
        Assert.Equal(SectionAccessSaveStatus.Saved, _h.CreateOtherSectionAccess().SaveEditedAliases(
            new Dictionary<string, string[]> { [alias] = was },
            new Dictionary<string, string[]> { [alias] = now }).Status);

    [Fact]
    public async Task ModuleConfig_AnAccessChangeMadeElsewhereEqualToTheSave_IsARecordedNoOp_NotARefusal()
    {
        SeedAccess([GroupA, GroupB]);
        var page = _h.OpenModuleConfig(Module);
        OtherInstanceSaves(Module, [GroupA, GroupB], [GroupA]);
        Assert.True(AccessList(page, Module).Remove(GroupB));
        var token = _h.SectionStore.GetChangeToken();

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        Assert.Equal(token, _h.SectionStore.GetChangeToken());
        Assert.Equal(new[] { GroupA }, StoredAccess()[Module]);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(Module, Text(record, "section"));
        Assert.Empty(Names(record, "added"));
        Assert.Empty(Names(record, "removed"));
        Assert.Equal(new[] { Module }, Names(record, "alreadyApplied"));
        Assert.False(SettingsPageHarness.Field<bool>(page, typeof(ModuleConfigPage), "statusIsError"));
    }

    [Fact]
    public async Task ModuleConfig_AnEqualChangeOnOneAlias_DoesNotStopTheEditOnAnother()
    {
        // Removals only: a grant the save adds would be asked of the directory, and nothing here may be.
        SeedAccess([GroupA, GroupB], onPrem: [GroupA]);
        var page = _h.OpenModuleConfig(Module);
        OtherInstanceSaves(Module, [GroupA, GroupB], [GroupA]);
        Assert.True(AccessList(page, Module).Remove(GroupB));
        Assert.True(AccessList(page, OnPremAlias).Remove(GroupA));

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        var stored = StoredAccess();
        Assert.Equal(new[] { GroupA }, stored[Module]);
        Assert.Equal(Array.Empty<string>(), stored.GetValueOrDefault(OnPremAlias) ?? []);
        var records = _h.AuditEvents();
        Assert.Equal(2, records.Count);
        var noOp = Assert.Single(records, r => Text(r, "section") == Module);
        Assert.Equal(new[] { Module }, Names(noOp, "alreadyApplied"));
        Assert.Empty(Names(noOp, "removed"));
        var written = Assert.Single(records, r => Text(r, "section") == OnPremAlias);
        Assert.Equal(new[] { GroupA }, Names(written, "removed"));
        Assert.Empty(Names(written, "alreadyApplied"));
    }

    // ---- Admin Settings: the directory-read secret --------------------------------------------

    private static void OnProtectedTab(AdminSettingsPage page) =>
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "tab", "Protected Principals");

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
            .TryReadModule(ProtectedPrincipalService.ProtectedPrincipalsModuleKey, out var values));
        return values.GetValueOrDefault(ProtectedPrincipalService.DirectoryReadSecretConfigKey);
    }

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

    /// <summary>
    /// The secret's own read failing as the page loads, after the rules' read succeeded: the page's
    /// secret load run again with the module-config store failing its reads.
    /// </summary>
    private async Task SecretReadFails(AdminSettingsPage page)
    {
        _h.ModuleConfigStore.ReadsFail = true;
        await _h.RunAsync(page, "LoadDirectoryReadSecret");
        _h.ModuleConfigStore.ReadsFail = false;
    }

    [Fact]
    public void AdminSettings_ASecretThatCouldNotBeRead_ShowsAsUnreadable_AndItsFieldIsDisabled()
    {
        SeedSecret("777");
        _h.ModuleConfigStore.ReadsFail = true;
        var page = _h.OpenAdminSettings();
        _h.ModuleConfigStore.ReadsFail = false;
        OnProtectedTab(page);

        Assert.True(SettingsPageHarness.Field<bool>(page, typeof(AdminSettingsPage), "ppSecretUnreadable"));
        Assert.Equal("", SettingsPageHarness.Field<string>(page, typeof(AdminSettingsPage), "ppDirectoryReadSecretId"));
        var text = SettingsPageHarness.RenderedText(page);
        Assert.Contains("could not be read", text, StringComparison.Ordinal);
        Assert.Contains("ProtectedPrincipals rows of the module_config table", text, StringComparison.Ordinal);
        Assert.True(SettingsPageHarness.IsInputDisabled(page, SecretPlaceholder));
    }

    [Fact]
    public void AdminSettings_AReadableSecret_IsShown_AndItsFieldIsOffered()
    {
        // The control for the case above.
        SeedSecret("777");
        var page = _h.OpenAdminSettings();
        OnProtectedTab(page);

        Assert.False(SettingsPageHarness.Field<bool>(page, typeof(AdminSettingsPage), "ppSecretUnreadable"));
        Assert.Equal("777", SettingsPageHarness.Field<string>(page, typeof(AdminSettingsPage), "ppDirectoryReadSecretId"));
        Assert.False(SettingsPageHarness.IsInputDisabled(page, SecretPlaceholder));
    }

    [Fact]
    public async Task AdminSettings_AChangedSecretThePageCouldNotRead_IsRefused_AndNothingIsWritten()
    {
        // The store reads again by the time of the save, so the fresh re-check passes: only the page's
        // own load state says the value it would replace was never shown.
        SeedRules(Cfo);
        SeedSecret("777");
        var page = _h.OpenAdminSettings();
        await SecretReadFails(page);
        await AddPattern(page, "svc-*");
        // The field is disabled; a stale render or a second tab is what reaches the handler.
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppDirectoryReadSecretId", "4242");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal("777", StoredSecret());
        Assert.Equal(new[] { "user:" + Cfo }, StoredRules());
        AssertOnlyARefusal("ProtectedPrincipals", "StoreUnreadable", Status(page));
    }

    [Fact]
    public async Task AdminSettings_ARulesOnlySaveWithTheSecretUnreadable_StillSaves()
    {
        SeedRules(Cfo);
        SeedSecret("777");
        var page = _h.OpenAdminSettings();
        await SecretReadFails(page);
        await AddPattern(page, "svc-*");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal("777", StoredSecret());
        Assert.Equal(new[] { "sam_pattern:svc-*", "user:" + Cfo }, StoredRules());
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal("ProtectedPrincipals", Text(record, "section"));
    }
}
