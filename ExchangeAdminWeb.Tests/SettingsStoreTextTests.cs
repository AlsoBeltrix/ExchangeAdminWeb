using System.Text.Json;
using ExchangeAdminWeb.Services;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;
using ExchangeOnlineConfigPage = ExchangeAdminWeb.Components.Pages.ExchangeOnlineConfig;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26h: the corrupt-store texts for the stores other than the allowlist told an administrator to fix
/// a <c>config\*.json</c> file those stores no longer live in, and a pool drain that failed after a saved
/// connection was reported and recorded as a failed save.
/// </summary>
/// <remarks>
/// The compiled pages through <see cref="SettingsPageHarness"/>, their markup rendered to the frame list
/// and their refusals read from the handlers, over real stores on one temp database file. This finding is
/// about the text, so the text is what is asserted: which store and fix it names, and which retired file
/// it does not. A legacy file the import refused is made by writing it before the services are built.
/// </remarks>
public sealed class SettingsStoreTextTests : IDisposable
{
    private const string Module = "MailboxPermissions";
    private const string AppId = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

    private static readonly string[] RetiredFiles =
    [
        "modules-enabled.json", "sectionaccess.json", "module-config-", "module-config.json",
        "protected-principals.json", "module-admins.json",
    ];

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

    private static void AssertNamesTheTable(string text, string table)
    {
        Assert.Contains(table, text, StringComparison.Ordinal);
        Assert.Contains("docs/ConfigDbOperations.md", text, StringComparison.Ordinal);
        Assert.Contains("both application pools stopped", text, StringComparison.Ordinal);
        foreach (var retired in RetiredFiles)
            Assert.DoesNotContain(retired, text, StringComparison.Ordinal);
    }

    /// <summary>A harness whose content root holds an unparseable legacy file when its services are built.</summary>
    private static SettingsPageHarness WithRefusedLegacyFile(string fileName) =>
        new(root =>
        {
            var config = Path.Combine(root, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, fileName), "{ not valid json");
        });

    private AttributeEditorHarness.SwitchableStore Store(string name) => name switch
    {
        "enablement" => _h.EnablementStore,
        "section" => _h.SectionStore,
        "config" => _h.ModuleConfigStore,
        "admins" => _h.AdminsStore,
        "protected" => _h.ProtectedStore,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    // ---- Admin Settings -----------------------------------------------------------------------

    [Fact]
    public void AdminSettings_AnUnreadableEnablementTable_IsNamedInTheBanner()
    {
        _h.EnablementStore.ReadsFail = true;
        var page = _h.OpenAdminSettings();
        _h.EnablementStore.ReadsFail = false;

        AssertNamesTheTable(SettingsPageHarness.RenderedText(page), "module_enablement");
    }

    [Fact]
    public void AdminSettings_ALegacyEnablementFileTheImportRefused_IsNamedInTheBanner()
    {
        using var h = WithRefusedLegacyFile("modules-enabled.json");
        var page = h.OpenAdminSettings();

        Assert.Contains("config\\modules-enabled.json", SettingsPageHarness.RenderedText(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminSettings_AnEnablementSaveOverAnUnreadableTable_IsRefusedNamingTheTable()
    {
        var page = _h.OpenAdminSettings();
        var enablement = SettingsPageHarness.Field<Dictionary<string, bool>>(page, typeof(AdminSettingsPage), "moduleEnablement");
        enablement[Module] = !enablement[Module];
        _h.EnablementStore.ReadsFail = true;

        await _h.RunAsync(page, "SaveEnablement");

        _h.EnablementStore.ReadsFail = false;
        AssertNamesTheTable(Status(page), "module_enablement");
    }

    [Theory]
    [InlineData("protected", "protected_principal")]
    [InlineData("config", "ExcludedUsers")]
    public void AdminSettings_TheProtectedPrincipalsLoadError_NamesItsCause(string failing, string named)
    {
        Store(failing).ReadsFail = true;
        var page = _h.OpenAdminSettings();
        Store(failing).ReadsFail = false;

        var error = SettingsPageHarness.Field<string?>(page, typeof(AdminSettingsPage), "ppLoadError") ?? "";
        AssertNamesTheTable(error, named);
        Assert.Contains(error, SettingsPageHarness.RenderedText(page), StringComparison.Ordinal);
    }

    [Fact]
    public void AdminSettings_ALegacyProtectedPrincipalsFileTheImportRefused_IsNamedInTheLoadError()
    {
        using var h = WithRefusedLegacyFile("protected-principals.json");
        var page = h.OpenAdminSettings();

        var error = SettingsPageHarness.Field<string?>(page, typeof(AdminSettingsPage), "ppLoadError") ?? "";
        Assert.Contains("config\\protected-principals.json", error, StringComparison.Ordinal);
    }

    // ---- Module Configuration -----------------------------------------------------------------

    [Theory]
    [InlineData("enablement", "module_enablement")]
    [InlineData("section", "section_access")]
    [InlineData("config", "module_config")]
    [InlineData("admins", "module_admins")]
    public void ModuleConfig_AnUnreadableStore_IsNamedInItsBanner(string failing, string table)
    {
        Store(failing).ReadsFail = true;
        var page = _h.OpenModuleConfig(Module);
        Store(failing).ReadsFail = false;

        AssertNamesTheTable(SettingsPageHarness.RenderedText(page), table);
    }

    [Fact]
    public void ModuleConfig_ALegacySectionAccessFileTheImportRefused_IsNamedInTheBanner()
    {
        using var h = WithRefusedLegacyFile("sectionaccess.json");
        var page = h.OpenModuleConfig(Module);

        Assert.Contains("config\\sectionaccess.json", SettingsPageHarness.RenderedText(page), StringComparison.Ordinal);
    }

    [Fact]
    public void ModuleConfig_TheNeverConfiguredNote_NamesNoRetiredFile()
    {
        var page = _h.OpenModuleConfig(Module);
        Assert.True(SettingsPageHarness.Field<bool>(page, typeof(ModuleConfigPage), "sectionAccessUnconfigured"));

        var text = SettingsPageHarness.RenderedText(page);

        Assert.Contains("never been configured", text, StringComparison.Ordinal);
        foreach (var retired in RetiredFiles)
            Assert.DoesNotContain(retired, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SaveModuleEnablementAsync", "enablement", "module_enablement")]
    [InlineData("SaveSectionAccessAsync", "section", "section_access")]
    [InlineData("SaveConfigAsync", "config", "module_config")]
    public async Task ModuleConfig_ASaveOverAnUnreadableStore_IsRefusedNamingTheTable(string handler, string failing, string table)
    {
        var page = _h.OpenModuleConfig(Module);
        Store(failing).ReadsFail = true;

        await _h.RunAsync(page, handler);

        Store(failing).ReadsFail = false;
        AssertNamesTheTable(Status(page), table);
    }

    // ---- Exchange Online Config ---------------------------------------------------------------

    private void SeedConnection() =>
        _h.ModuleConfig.SaveModuleConfig(ExoConnectionPool.ConfigModuleKey, new Dictionary<string, string>
        {
            [ExoConnectionPool.ConfigAppIdKey] = AppId,
            [ExoConnectionPool.ConfigOrganizationKey] = "contoso.onmicrosoft.com",
            [ExoConnectionPool.ConfigCertSubjectKey] = "CN=EXO-Automation",
        });

    [Fact]
    public async Task ExchangeOnlineConfig_AnUnreadableConnectionStore_IsNamedInTheRefusalAndTheBanner()
    {
        SeedConnection();
        var page = _h.OpenExchangeOnlineConfig();
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "exoOrganization", "fabrikam.onmicrosoft.com");
        _h.ModuleConfigStore.ReadsFail = true;

        await _h.RunAsync(page, "SaveExoConfig");

        _h.ModuleConfigStore.ReadsFail = false;
        AssertNamesTheTable(Status(page), "module_config");
        Assert.True(SettingsPageHarness.Field<bool>(page, typeof(ExchangeOnlineConfigPage), "configCorrupt"));
        AssertNamesTheTable(SettingsPageHarness.RenderedText(page), "module_config");
    }

    [Fact]
    public void ExchangeOnlineConfig_AnUnreadableEnablementTable_IsNamedInTheBanner()
    {
        _h.EnablementStore.ReadsFail = true;
        var page = _h.OpenExchangeOnlineConfig();
        _h.EnablementStore.ReadsFail = false;

        AssertNamesTheTable(SettingsPageHarness.RenderedText(page), "module_enablement");
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AFailingDrainAfterASavedConnection_IsItsOwnWarning_NotAFailedSave()
    {
        SeedConnection();
        var page = _h.OpenExchangeOnlineConfig();
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "exoOrganization", "fabrikam.onmicrosoft.com");
        // DrainPool re-reads the connection from the config store after the save, so a store that stops
        // answering reads once the save's write has opened makes the drain - and only the drain - throw.
        _h.ModuleConfigStore.BeforeWrite = () => _h.ModuleConfigStore.ReadsFail = true;

        await _h.RunAsync(page, "SaveExoConfig");
        _h.ModuleConfigStore.ReadsFail = false;

        Assert.Contains("fabrikam.onmicrosoft.com", _h.StoredModuleConfig(ExoConnectionPool.ConfigModuleKey), StringComparison.Ordinal);

        var records = _h.AuditEvents();
        Assert.Equal(2, records.Count);
        var save = Assert.Single(records, r => Text(r, "section") == "ExchangeOnlineConfig");
        Assert.Equal("Success", Text(save, "result"));
        Assert.Equal(new[] { "Org=fabrikam.onmicrosoft.com" }, Names(save, "added"));
        var drain = Assert.Single(records, r => Text(r, "section") == "ExchangeOnlineConfig:ConnectionPool");
        Assert.Equal("Failed", Text(drain, "result"));
        Assert.False(drain.TryGetProperty("refusedAt", out _));
        Assert.Equal(Status(page), Text(drain, "error"));

        Assert.Contains("configuration saved", Status(page), StringComparison.Ordinal);
        Assert.DoesNotContain("Failed to save", Status(page), StringComparison.Ordinal);
        Assert.False(SettingsPageHarness.Field<bool>(page, typeof(ExchangeOnlineConfigPage), "statusIsError"));
        Assert.True(SettingsPageHarness.Field<bool>(page, typeof(ExchangeOnlineConfigPage), "statusIsWarning"));
    }

    // ---- README -------------------------------------------------------------------------------

    [Fact]
    public void Readme_SaysTheConfigPagesWriteTheConfigDatabase_NotTheRetiredFiles()
    {
        var readme = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("README.md"));

        Assert.DoesNotContain("Changes are persisted to `config/modules-enabled.json`", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("The config is stored in `config/sectionaccess.json`", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("when no `config/sectionaccess.json`", readme, StringComparison.Ordinal);
        Assert.Contains("`module_enablement`", readme, StringComparison.Ordinal);
        Assert.Contains("`section_access`", readme, StringComparison.Ordinal);
    }
}
