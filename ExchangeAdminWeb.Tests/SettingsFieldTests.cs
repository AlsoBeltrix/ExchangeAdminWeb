using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;
using ExchangeOnlineConfigPage = ExchangeAdminWeb.Components.Pages.ExchangeOnlineConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26p: the directory-read Secret ID took any text, so a typo was found only when a lookup later fell back;
/// Exchange Online Config showed values the appsettings fallback or the built-in default supplied with nothing saying
/// so; and the protected-principals count ignored casing, so an entry added back in other casing - which the save
/// writes as typed - showed no unsaved change.
/// </summary>
/// <remarks>
/// The compiled pages through <see cref="SettingsPageHarness"/>; no directory - the one rule edit is a pattern, which is
/// never looked up - and no Exchange Online: the connection save drains a pool that holds no session. Conditions are
/// asserted, not wording, except the captions, which are what this finding adds.
/// </remarks>
public sealed class SettingsFieldTests : IDisposable
{
    private const string AppId = "6f9619ff-8b86-d011-b42d-00c04fc964ff";
    private const string Cfo = "cfo@contoso.com";
    private const string SecretKey = ProtectedPrincipalService.DirectoryReadSecretConfigKey;
    private const string ProtectedSection = "Protected Principals";

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static string Status<TPage>(TPage page) =>
        SettingsPageHarness.Field<string?>(page!, typeof(TPage), "statusMessage") ?? "";

    // ---- Admin Settings: the Secret ID --------------------------------------------------------

    private void SeedRules(string[]? users = null, string[]? patterns = null) =>
        new ProtectedPrincipalRepository(TestConfigStore.Create(_h.Root))
            .Save(new ProtectedPrincipalData(users ?? [], [], [], patterns ?? [], []));

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

    // The field is bound to SetPpDirectoryReadSecretId; set directly, as a stale render or a second tab reaches it.
    private static void EnterSecret(AdminSettingsPage page, string value) =>
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppDirectoryReadSecretId", value);

    private async Task AddPattern(AdminSettingsPage page, string pattern)
    {
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppNewPattern", pattern);
        await _h.RunAsync(page, "AddPpPattern");
    }

    [Theory]
    [InlineData("12a")]
    [InlineData("secret 4242")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("42.5")]
    [InlineData("99999999999")]
    public async Task AdminSettings_ASecretIdThatIsNotAWholeNumberAboveZero_IsRefused_NamingTheField_AndNothingIsWritten(string entered)
    {
        SeedRules(users: [Cfo]);
        SeedSecret("777");
        var page = _h.OpenAdminSettings();
        await AddPattern(page, "svc-*");
        EnterSecret(page, entered);

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal("777", StoredSecret());
        Assert.Equal(new[] { "user:" + Cfo }, StoredRules());
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal("ProtectedPrincipals", Text(record, "section"));
        Assert.Equal("Validation", Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(Status(page), Text(record, "error"));
        Assert.Contains("Directory Read Credential (Delinea Secret ID)", Status(page), StringComparison.Ordinal);
        Assert.True(SettingsPageHarness.Field<bool>(page, typeof(AdminSettingsPage), "statusIsError"));
    }

    [Fact]
    public async Task AdminSettings_ARefusedSecretId_IsNotRepeatedInTheRecord()
    {
        // A field that takes an ID is where a pasted password lands; the refusal names the field, never its text.
        const string pasted = "Winter2026!pasted";
        SeedSecret("777");
        var page = _h.OpenAdminSettings();
        EnterSecret(page, pasted);

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Validation", Text(record, "refusedAt"));
        Assert.DoesNotContain(pasted, record.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(pasted, Status(page), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("4242", "4242")]
    [InlineData(" 4242 ", "4242")]
    [InlineData("", "")]
    public async Task AdminSettings_AWholeNumberSecretIdOrABlankOne_IsSaved(string entered, string stored)
    {
        // The controls for the refusal above: an id the lookups read, and blank, which clears the stored id.
        SeedRules(users: [Cfo]);
        SeedSecret("777");
        var page = _h.OpenAdminSettings();
        EnterSecret(page, entered);

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(stored, StoredSecret());
        Assert.All(_h.AuditEvents(), r => Assert.Equal("Success", Text(r, "result")));
        Assert.False(SettingsPageHarness.Field<bool>(page, typeof(AdminSettingsPage), "statusIsError"));
    }

    // ---- Admin Settings: the count ----------------------------------------------------------------

    [Fact]
    public async Task AdminSettings_AnEntryAddedBackInOtherCasing_CountsAsTwoChanges_AndIsSavedAsTyped()
    {
        SeedRules(patterns: ["SVC-*"]);
        var page = _h.OpenAdminSettings();
        var patterns = SettingsPageHarness.Field<List<string>>(page, typeof(AdminSettingsPage), "ppPatterns");
        var dirty = SettingsPageHarness.Field<AdminPageDirtyState>(page, typeof(AdminSettingsPage), "dirty");

        // RemovePp's two steps; it takes arguments the harness does not pass.
        Assert.True(patterns.Remove("SVC-*"), "the seeded pattern did not load - this fixture is stale");
        await _h.RunAsync(page, "RecountProtected");
        await AddPattern(page, "svc-*");

        Assert.True(dirty.IsDirty);
        Assert.Equal(2, dirty.SectionCount(ProtectedSection));

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(new[] { "sam_pattern:svc-*" }, StoredRules());
        Assert.False(dirty.IsDirty);
    }

    // ---- Exchange Online Config: the fallback marking -----------------------------------------

    private const string ConfiguredCaption =
        "Not set in the database; the configured fallback applies: " + AppId + " (ExchangeOnline:AppId in appsettings.json).";

    private const string BuiltInCaption = "Not set in the database; the built-in default applies: CN=EXO-Automation.";

    private void SeedConnection(Dictionary<string, string> values) =>
        new ModuleConfigRepository(TestConfigStore.Create(_h.Root)).SaveModule(ExoConnectionPool.ConfigModuleKey, values);

    private Dictionary<string, string> StoredConnection()
    {
        Assert.True(new ModuleConfigRepository(TestConfigStore.Create(_h.Root))
            .TryReadModule(ExoConnectionPool.ConfigModuleKey, out var values), "the connection is unreadable");
        return values;
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void ExchangeOnlineConfig_AFieldWithNoRow_IsMarkedWithTheFallbackThatSuppliesIt()
    {
        // The organization is stored; the app id has no row and comes from appsettings; the certificate has neither
        // and is the built-in default the runtime uses.
        SeedConnection(new Dictionary<string, string> { [ExoConnectionPool.ConfigOrganizationKey] = "contoso.onmicrosoft.com" });
        _h.Config["ExchangeOnline:AppId"] = AppId;

        var page = _h.OpenExchangeOnlineConfig();

        var text = SettingsPageHarness.RenderedText(page);
        Assert.Contains(ConfiguredCaption, text, StringComparison.Ordinal);
        Assert.Contains(BuiltInCaption, text, StringComparison.Ordinal);
        Assert.Equal(2, Count(text, "Not set in the database"));
        Assert.DoesNotContain("ExchangeOnline:Organization", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExchangeOnlineConfig_AStoredFieldAndFieldsNothingSupplies_AreNotMarked()
    {
        // The controls: the organization is stored; the app id has no row and no appsettings value; the certificate
        // has no row and appsettings sets it blank, which the runtime takes over its built-in default. Nothing
        // supplies either, so both are blank and neither is marked.
        SeedConnection(new Dictionary<string, string> { [ExoConnectionPool.ConfigOrganizationKey] = "contoso.onmicrosoft.com" });
        _h.Config["ExchangeOnline:CertificateSubject"] = "";

        var page = _h.OpenExchangeOnlineConfig();

        Assert.Equal("", SettingsPageHarness.Field<string>(page, typeof(ExchangeOnlineConfigPage), "exoAppId"));
        Assert.Equal("", SettingsPageHarness.Field<string>(page, typeof(ExchangeOnlineConfigPage), "exoCertSubject"));
        Assert.DoesNotContain("Not set in the database", SettingsPageHarness.RenderedText(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeOnlineConfig_SavingAMarkedField_StoresIt_AndOnlyThatFieldLosesItsMark()
    {
        SeedConnection(new Dictionary<string, string> { [ExoConnectionPool.ConfigOrganizationKey] = "contoso.onmicrosoft.com" });
        _h.Config["ExchangeOnline:AppId"] = AppId;
        var page = _h.OpenExchangeOnlineConfig();
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "exoCertSubject", "CN=EXO-Rotated");

        await _h.RunAsync(page, "SaveExoConfig");

        var stored = StoredConnection();
        Assert.Equal("CN=EXO-Rotated", stored[ExoConnectionPool.ConfigCertSubjectKey]);
        Assert.False(stored.ContainsKey(ExoConnectionPool.ConfigAppIdKey));
        var text = SettingsPageHarness.RenderedText(page);
        Assert.DoesNotContain(BuiltInCaption, text, StringComparison.Ordinal);
        Assert.Contains(ConfiguredCaption, text, StringComparison.Ordinal);
    }
}
