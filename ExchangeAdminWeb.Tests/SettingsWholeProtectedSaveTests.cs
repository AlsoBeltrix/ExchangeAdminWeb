using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28r: a protected-principals save wrote the rules before it compared a changed directory-read Secret ID, so a
/// secret changed elsewhere since the page loaded was refused after the rules had landed - the one change the admin
/// confirmed was saved as half.
/// </summary>
/// <remarks>
/// The aud-a33b style: "the other instance" is a second module-config service over its own store on the same temp
/// database file, and a change landing inside a save runs from a store's <c>BeforeWrite</c> hook as that store's next
/// write opens. The compiled page through <see cref="SettingsPageHarness"/>. Nothing reaches the directory: the one
/// rule edit is a pattern, which is never looked up. Conditions are asserted, not wording.
/// </remarks>
public sealed class SettingsWholeProtectedSaveTests : IDisposable
{
    private const string Cfo = "cfo@contoso.com";
    private const string Pattern = "svc-*";
    private const string Section = "ProtectedPrincipals";
    private const string SecretSection = "ProtectedPrincipals:DirectoryReadSecretId";
    private const string SecretKey = ProtectedPrincipalService.DirectoryReadSecretConfigKey;

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static string[] Names(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray()
            : [];

    private static string Status(AdminSettingsPage page) =>
        SettingsPageHarness.Field<string?>(page, typeof(AdminSettingsPage), "statusMessage") ?? "";

    private static bool IsError(AdminSettingsPage page) =>
        SettingsPageHarness.Field<bool>(page, typeof(AdminSettingsPage), "statusIsError");

    private static bool IsDirty(AdminSettingsPage page) =>
        SettingsPageHarness.Field<AdminPageDirtyState>(page, typeof(AdminSettingsPage), "dirty").IsDirty;

    private void SeedRules(params string[] users) =>
        new ProtectedPrincipalRepository(TestConfigStore.Create(_h.Root))
            .Save(new ProtectedPrincipalData(users, [], [], [], []));

    private void SeedSecret(string value) =>
        new ModuleConfigRepository(TestConfigStore.Create(_h.Root)).SaveModule(
            ProtectedPrincipalService.ProtectedPrincipalsModuleKey, new Dictionary<string, string> { [SecretKey] = value });

    private string? StoredSecret()
    {
        Assert.True(new ModuleConfigRepository(TestConfigStore.Create(_h.Root))
            .TryReadModule(ProtectedPrincipalService.ProtectedPrincipalsModuleKey, out var values), "the secret is unreadable");
        return values.GetValueOrDefault(SecretKey);
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

    /// <summary>When the secret's row was last written; the change token is the database's, and the rules move it too.</summary>
    private string SecretWrittenAt() =>
        TestConfigStore.Create(_h.Root).Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT updated_at FROM module_config WHERE module_id = $id AND config_key = $key;";
            command.Parameters.AddWithValue("$id", ProtectedPrincipalService.ProtectedPrincipalsModuleKey);
            command.Parameters.AddWithValue("$key", SecretKey);
            return command.ExecuteScalar() as string ?? "";
        });

    private void OtherInstanceSetsSecret(string value) =>
        _h.CreateOtherModuleConfig().UpdateModuleValues(
            ProtectedPrincipalService.ProtectedPrincipalsModuleKey, new Dictionary<string, string> { [SecretKey] = value });

    // The field is bound to SetPpDirectoryReadSecretId; set directly, as a stale render or a second tab reaches it.
    private static void EnterSecret(AdminSettingsPage page, string value) =>
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppDirectoryReadSecretId", value);

    private async Task AddPattern(AdminSettingsPage page, string pattern)
    {
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppNewPattern", pattern);
        await _h.RunAsync(page, "AddPpPattern");
    }

    /// <summary>A page that loaded one rule and the secret 777, with a pattern added and the secret changed to 4242.</summary>
    private async Task<AdminSettingsPage> AnEditedPage()
    {
        SeedRules(Cfo);
        SeedSecret("777");
        var page = _h.OpenAdminSettings();
        await AddPattern(page, Pattern);
        EnterSecret(page, "4242");
        return page;
    }

    private void AssertOnlyTheWholeSaveRefused(AdminSettingsPage page, string refusedAt)
    {
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(Section, Text(record, "section"));
        Assert.Equal(refusedAt, Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(Status(page), Text(record, "error"));
        Assert.Contains("Delinea Secret ID", Status(page), StringComparison.Ordinal);
        Assert.True(IsError(page));
    }

    [Fact]
    public async Task ASecretChangedElsewhereBeforeTheSave_RefusesTheWholeSave_AndWritesNothing_RulesIncluded()
    {
        var page = await AnEditedPage();
        OtherInstanceSetsSecret("888");
        var rules = _h.StoredProtectedPrincipals();
        var token = _h.ProtectedStore.GetChangeToken();

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(rules, _h.StoredProtectedPrincipals());
        Assert.Equal(new[] { "user:" + Cfo }, StoredRules());
        Assert.Equal("888", StoredSecret());
        Assert.Equal(token, _h.ProtectedStore.GetChangeToken());
        AssertOnlyTheWholeSaveRefused(page, "ConcurrentChange");
        // Nothing saved, so the admin's edits are still pending on the page.
        Assert.True(IsDirty(page));
    }

    [Fact]
    public async Task ASecretChangedElsewhereAsTheCompareOpensItsWrite_RefusesTheWholeSave_RulesIncluded()
    {
        var page = await AnEditedPage();
        _h.ModuleConfigStore.BeforeWrite = () => OtherInstanceSetsSecret("888");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Null(_h.ModuleConfigStore.BeforeWrite);
        Assert.Equal(new[] { "user:" + Cfo }, StoredRules());
        Assert.Equal("888", StoredSecret());
        AssertOnlyTheWholeSaveRefused(page, "ConcurrentChange");
    }

    [Fact]
    public async Task ASecretStoreThatCannotOpenAWrite_FailsTheWholeSave_BeforeTheRules()
    {
        // The compare holds the secret's write lock, so a store that cannot open a write fails it, and with it the
        // save, before anything is written.
        var page = await AnEditedPage();
        _h.ModuleConfigStore.WritesFail = true;

        await _h.RunAsync(page, "SaveProtectedPrincipals");
        _h.ModuleConfigStore.WritesFail = false;

        Assert.Equal(new[] { "user:" + Cfo }, StoredRules());
        Assert.Equal("777", StoredSecret());
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(Section, Text(record, "section"));
        Assert.False(record.TryGetProperty("refusedAt", out _));
        Assert.Equal(Status(page), Text(record, "error"));
        Assert.True(IsError(page));
    }

    [Fact]
    public async Task ACleanSave_WritesTheRulesAndTheSecret_EachWithItsRecord()
    {
        var page = await AnEditedPage();

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(new[] { "sam_pattern:" + Pattern, "user:" + Cfo }, StoredRules());
        Assert.Equal("4242", StoredSecret());
        var records = _h.AuditEvents();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal("Success", Text(r, "result")));
        var rules = Assert.Single(records, r => Text(r, "section") == Section);
        Assert.Equal(new[] { "Patterns:" + Pattern }, Names(rules, "added"));
        var secret = Assert.Single(records, r => Text(r, "section") == SecretSection);
        Assert.Equal(new[] { "4242" }, Names(secret, "added"));
        Assert.Equal(new[] { "777" }, Names(secret, "removed"));
        Assert.False(IsError(page));
        Assert.False(IsDirty(page));
    }

    [Fact]
    public async Task ASecretChangedElsewhereAfterTheCompare_IsTheNamedWindow_TheRulesSaveAndTheSecretIsRefused()
    {
        // The window aud-a28r leaves: the rules' write sits between the compare's lock and the secret's. A change
        // landing as the rules' write opens is refused by the secret's own compare after the rules landed, and both
        // halves are recorded - aud-a28j's partial path, not a silent overwrite.
        var page = await AnEditedPage();
        _h.ProtectedStore.BeforeWrite = () => OtherInstanceSetsSecret("888");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Null(_h.ProtectedStore.BeforeWrite);
        Assert.Equal(new[] { "sam_pattern:" + Pattern, "user:" + Cfo }, StoredRules());
        Assert.Equal("888", StoredSecret());
        var records = _h.AuditEvents();
        Assert.Equal(2, records.Count);
        Assert.Equal("Success", Text(Assert.Single(records, r => Text(r, "section") == Section), "result"));
        var secret = Assert.Single(records, r => Text(r, "section") == SecretSection);
        Assert.Equal("Failed", Text(secret, "result"));
        Assert.Equal("ConcurrentChange", Text(secret, "refusedAt"));
        Assert.False(secret.GetProperty("mutationAttempted").GetBoolean());
        Assert.Contains("rules were saved", Status(page), StringComparison.Ordinal);
        Assert.True(IsError(page));
    }

    [Fact]
    public void TheCompare_WritesNothing_AndReportsWhatASaveWouldMeet()
    {
        SeedSecret("777");
        var writtenAt = SecretWrittenAt();
        var token = _h.ModuleConfigStore.GetChangeToken();

        var clear = _h.ProtectedPrincipals.CompareDirectoryReadSecretId("777", "4242");

        Assert.Equal(ModuleConfigSaveStatus.Saved, clear.Status);
        Assert.Empty(clear.ConflictingKeys);
        Assert.Equal("777", StoredSecret());
        Assert.Equal(writtenAt, SecretWrittenAt());
        Assert.Equal(token, _h.ModuleConfigStore.GetChangeToken());

        OtherInstanceSetsSecret("888");
        var conflict = _h.ProtectedPrincipals.CompareDirectoryReadSecretId("777", "4242");
        Assert.Equal(ModuleConfigSaveStatus.ConcurrentChange, conflict.Status);
        Assert.Equal(new[] { SecretKey }, conflict.ConflictingKeys);

        var applied = _h.ProtectedPrincipals.CompareDirectoryReadSecretId("777", "888");
        Assert.Equal(ModuleConfigSaveStatus.NothingToSave, applied.Status);
        Assert.Equal(new[] { SecretKey }, applied.AlreadyApplied);
        Assert.Equal("888", StoredSecret());
    }
}
