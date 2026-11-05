using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md C.1, C.2, D (slice V1): the Protected Principals pane's Verify button, its badges, the
/// actions each finding offers and their help, through the compiled page (<see cref="SettingsPageHarness"/>). The harness's
/// protection service has no directory-read credential, so a press verifies nothing - which is itself the fail-closed case.
/// </summary>
public sealed class DirectoryObjectVerifyPageTests : IDisposable
{
    private const string GroupSid = "S-1-5-21-1000000000-1000000000-1000000000-2001";
    private const string GroupDn = "CN=Tier0 Admins,OU=Groups,DC=contoso,DC=com";
    private static string GroupRow => ProtectedPrincipalIdEntry.Format(GroupSid, GroupDn);

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Text(AdminSettingsPage page) =>
        string.Join(' ', SettingsPageHarness.RenderedText(page).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private AdminSettingsPage OpenWith(ProtectedPrincipalData rules)
    {
        new ProtectedPrincipalRepository(TestConfigStore.Create(_h.Root)).Save(rules);
        var page = _h.OpenAdminSettings();
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "tab", "Protected Principals");
        return page;
    }

    [Fact]
    public async Task APress_WithNoDirectoryReadCredential_MarksEveryEntryCouldNotVerify_AndRecordsOnePressWithItsCounts()
    {
        var page = OpenWith(ProtectedPrincipalData.Empty with { Groups = [GroupDn], GroupIds = [GroupRow], Users = ["legacy@contoso.com"] });

        await _h.RunAsync(page, "VerifyProtectedAsync");

        var text = Text(page);
        Assert.Contains("could not verify", text);
        Assert.Contains("Verified 2 entries at", text);
        Assert.Contains("0 findings, 0 ignored, 2 could not be verified.", text);
        Assert.DoesNotContain(">missing<", text);
        var press = Assert.Single(_h.AuditEvents(), e => e.GetProperty("section").GetString() == ProtectedPrincipalVerifyActions.Section);
        Assert.Equal(("Success", "Verify", 2), (press.GetProperty("result").GetString(), press.GetProperty("action").GetString(), press.GetProperty("unreadable").GetInt32()));
        Assert.Contains(VerifyIgnoreList.ProtectedPrincipalsStore,
            new AppSettingRepository(TestConfigStore.Create(_h.Root)).Get(VerifyIgnoreList.LastVerifiedKey));
    }

    [Fact]
    public async Task Verify_IsNotOffered_WhileThePaneHasUnsavedChanges_AndAPressThenVerifiesNothing()
    {
        var page = OpenWith(ProtectedPrincipalData.Empty with { Groups = [GroupDn], GroupIds = [GroupRow] });
        await _h.RunAsync(page, "SetPpDirectoryReadSecretId", "123");

        Assert.True(SettingsPageHarness.IsButtonDisabled(page, "Verify"));
        await _h.RunAsync(page, "VerifyProtectedAsync");

        Assert.Null(SettingsPageHarness.Field<VerifyReport?>(page, typeof(AdminSettingsPage), "ppVerifyReport"));
        Assert.Empty(_h.AuditEvents());
        Assert.Contains("Save or discard", Text(page));
    }

    [Fact]
    public void EachFinding_ShowsItsBadgeAndHelp_AndOnlyTheActionsItOffers_NeverIgnoreForARowStoredByName()
    {
        var page = OpenWith(ProtectedPrincipalData.Empty with { Groups = [GroupDn], GroupIds = [GroupRow], Users = ["legacy@contoso.com"] });
        var workset = Assert.IsType<ProtectedPrincipalVerifyWorkset>(_h.ProtectedPrincipals.ReadForVerify());
        var group = workset.Items.Single(i => i.RowKey == "group_sid");
        var legacy = workset.Items.Single(i => i.RowKey == "user");
        var report = new VerifyReport(DateTimeOffset.UtcNow,
        [
            new VerifyItemResult(group, VerifyFinding.Missing()),
            new VerifyItemResult(legacy, VerifyFinding.NotConverted(new VerifyResolution(
                VerifyResolutionKind.Convertible, "converts to S-1-5-21-1-2-3-1105", "user_sid", "S-1-5-21-1-2-3-1105"))),
        ], []);
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppVerifyReport", report);

        var text = Text(page);

        Assert.Contains("missing", text);
        Assert.Contains("Active Directory answered and holds no object with this entry&#39;s identifier", SettingsPageHarness.RenderedText(page));
        Assert.Contains("not converted", text);
        Assert.Contains("Cannot be ignored.", text);
        var buttons = SettingsPageHarness.ButtonLabels(page);
        // Missing: Replace, Remove now, Ignore. Not converted and convertible: Convert, Remove now - never Ignore.
        Assert.Equal(1, buttons.Count(b => b == "Replace..."));
        Assert.Equal(1, buttons.Count(b => b == "Convert"));
        Assert.Equal(2, buttons.Count(b => b == "Remove now"));
        Assert.Equal(1, buttons.Count(b => b == "Ignore"));
        Assert.DoesNotContain("Rename", buttons);
        Assert.Contains(AdminSettingsPage.VerifyPaneNote, text);
    }

    [Fact]
    public void EveryBadgeAndAction_CarriesItsHelp()
    {
        var item = new VerifyItem("ProtectedPrincipals", "group_sid", VerifyObjectKind.Group, GroupSid, GroupDn, GroupRow, "v");
        foreach (var finding in new[]
                 {
                     VerifyFinding.Verified(), VerifyFinding.Missing(), VerifyFinding.LabelDrift("CN=New"),
                     VerifyFinding.NotConverted(new VerifyResolution(VerifyResolutionKind.None, "not found")),
                     VerifyFinding.Ambiguous("CN=A; CN=B"), VerifyFinding.WrongClass("user"), VerifyFinding.Unreadable("busy"),
                     VerifyFinding.Ignored(VerifyFinding.Missing(), new VerifyIgnoreEntry("ProtectedPrincipals", "group_sid", GroupSid, "Missing", "not found", "EXAMPLE\\admin", "t")),
                 })
        {
            var (badge, css, tip) = AdminSettingsPage.VerifyBadgeParts(new VerifyItemResult(item, finding), "2026-11-04 10:00");
            Assert.False(string.IsNullOrWhiteSpace(badge), finding.Kind.ToString());
            Assert.False(string.IsNullOrWhiteSpace(css), finding.Kind.ToString());
            Assert.True(tip.Length > 40, $"{finding.Kind} has no real hover help");
        }

        foreach (var action in Enum.GetValues<VerifyAction>())
            Assert.True(AdminSettingsPage.VerifyActionTip(action, new VerifyItemResult(item, VerifyFinding.LabelDrift("CN=New"))).Contains("audited", StringComparison.OrdinalIgnoreCase), action.ToString());
        Assert.Contains("Reads only", AdminSettingsPage.VerifyButtonTip);
    }
}
