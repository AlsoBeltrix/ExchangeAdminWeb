using ExchangeAdminWeb.Services.Storage;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/ProtectedPrincipalIdentifiers-Plan.md K.3 points 2 and 4 (slice S6): with the Resolve now buttons and the flag-driven
/// badges gone, the Admin Settings Protected Principals pane counts the entries stored by name only and says what they do now
/// and what converts them; the Module Configuration Admins pane lists each grant stored by name with the fixed text and a Remove.
/// </summary>
public sealed class IdentifierConversionPageTests : IDisposable
{
    private const string Module = "MailboxPermissions";
    private const string NameGrant = "CONTOSO\\Old Helpdesk";
    private const string OtherNameGrant = "Ops Team";
    private const string SidGrant = "S-1-5-21-1000000000-1000000000-1000000000-3999";

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Text(object page) =>
        string.Join(' ', SettingsPageHarness.RenderedText((Microsoft.AspNetCore.Components.ComponentBase)page)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // docs/DirectoryObjectVerify-Plan.md C.1 (slice V1): the count banner and the "not in AD" badge are replaced by Verify's
    // badges. What an entry stored by name does is said where it matters - the per-entry "not converted" badge and its
    // hover (pinned in DirectoryObjectVerifyPageTests) and the refusal itself - not in the pane note (help sweep 2026-11-09).
    [Fact]
    public void TheProtectedPrincipalsPane_SaysWhatItProtects_AndOffersVerify_WithNoBanner()
    {
        new ProtectedPrincipalRepository(TestConfigStore.Create(_h.Root)).Save(ProtectedPrincipalData.Empty with { Users = ["cfo@contoso.com"] });
        var page = _h.OpenAdminSettings();
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "tab", "Protected Principals");

        var text = Text(page);

        Assert.Contains("cannot be changed through any module", text);
        Assert.DoesNotContain("older build", text);
        Assert.DoesNotContain("entry is not converted to a directory identifier", text);
        Assert.DoesNotContain("not in AD", text);
        Assert.DoesNotContain("Resolve now", text);
        Assert.False(SettingsPageHarness.IsButtonDisabled(page, "Verify"));
    }

    [Fact]
    public void TheAdminsPane_ListsEachGrantStoredByName_WithTheFixedText_AndARemoveBesideIt()
    {
        _h.ModuleAdmins.SetModuleAdmins(Module, [NameGrant, SidGrant]);

        var page = _h.OpenModuleConfig(Module, handlersOnly: false);
        var text = Text(page);

        Assert.Contains("1 grant is stored by name", text);
        Assert.DoesNotContain("Resolve now", text);
        var flagged = text.IndexOf(NameGrant, StringComparison.Ordinal);
        Assert.True(flagged >= 0);
        var row = text[flagged..text.IndexOf(SidGrant, flagged, StringComparison.Ordinal)];
        Assert.Contains(ModuleConfigPage.NotConvertedAdminText, row);
        Assert.Contains("Remove", row);
    }

    [Fact]
    public async Task RemoveBesideAGrantStoredByName_DeletesExactlyThatRow()
    {
        _h.ModuleAdmins.SetModuleAdmins(Module, [NameGrant, OtherNameGrant, SidGrant]);
        var page = _h.OpenModuleConfig(Module);

        await _h.RunAsync(page, "RemoveModuleAdmin", NameGrant);
        await _h.RunAsync(page, "SaveModuleAdminsAsync");

        Assert.Equal([OtherNameGrant, SidGrant], _h.ModuleAdmins.GetModuleAdmins(Module).Order(StringComparer.Ordinal));
    }
}
