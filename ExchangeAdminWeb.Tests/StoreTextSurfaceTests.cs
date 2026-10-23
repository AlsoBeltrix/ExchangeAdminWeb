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
}
