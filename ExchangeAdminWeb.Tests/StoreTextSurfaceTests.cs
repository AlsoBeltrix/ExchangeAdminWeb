using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26t: the config database's remedy ends "then reload this page", and the services that log or throw it - where no
/// page is involved - had only that text. They have the same fix without the reload clause now, and every page's text is
/// as it was.
/// </summary>
public sealed class StoreTextSurfaceTests
{
    private const string Rows = "The MailboxPermissions rows of the module_config table in the config database cannot be read. ";
    private const string Fix =
        "Correct it as docs/ConfigDbOperations.md describes, or restore a verified backup the same way, with both application pools stopped";

    [Fact]
    public void ThePageText_IsAsItWas()
    {
        Assert.Equal(Fix + ", then reload this page.", SettingsStoreText.DatabaseRemedy);
        Assert.Equal(Rows + Fix + ", then reload this page.", SettingsStoreText.ModuleConfig("MailboxPermissions"));
    }

    [Fact]
    public void TheServiceText_IsThePageTextWithoutItsReloadClause()
    {
        Assert.Equal(Fix + ".", SettingsStoreText.ServiceDatabaseRemedy);
        Assert.Equal(Rows + Fix + ".", SettingsStoreText.ServiceModuleConfig("MailboxPermissions"));
    }

    // aud-a50, part 5: the enablement store's text in a service's words, beside the module config's.

    private const string EnablementTable =
        "The module_enablement table in the config database cannot be read, so every module reads as disabled. ";

    private const string EnablementLegacy =
        "The legacy file config\\modules-enabled.json could not be imported into the config database and is left in place, so every module reads as disabled until it imports. "
        + "Correct or remove the file, then recycle the application pool so the import runs again at start.";

    [Fact]
    public void TheEnablementPageText_IsAsItWas()
    {
        Assert.Equal(EnablementTable + Fix + ", then reload this page.", SettingsStoreText.Enablement(legacyImportFailed: false));
        Assert.Equal(EnablementLegacy, SettingsStoreText.Enablement(legacyImportFailed: true));
    }

    [Fact]
    public void TheServiceEnablementText_IsThePageTextWithoutItsReloadClause_AndTheLegacyTextIsOneText()
    {
        // The legacy file's text names no page to reload, so a service says it as the page does.
        Assert.Equal(EnablementTable + Fix + ".", SettingsStoreText.ServiceEnablement(legacyImportFailed: false));
        Assert.Equal(EnablementLegacy, SettingsStoreText.ServiceEnablement(legacyImportFailed: true));
        Assert.DoesNotContain("reload this page", SettingsStoreText.ServiceEnablement(legacyImportFailed: false), StringComparison.Ordinal);
    }
}
