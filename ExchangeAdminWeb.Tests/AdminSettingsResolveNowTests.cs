using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;

namespace ExchangeAdminWeb.Tests;

/// <summary>docs/ProtectedPrincipalIdentifiers-Plan.md S2, B.5: Admin Settings' Resolve now and the "not converted" flagging.</summary>
public sealed class AdminSettingsResolveNowTests : IDisposable
{
    private const string Cfo = "cfo@contoso.com";

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static T Field<T>(AdminSettingsPage page, string name) => SettingsPageHarness.Field<T>(page, typeof(AdminSettingsPage), name);

    private AdminSettingsPage OpenOnProtectedTab()
    {
        var page = _h.OpenAdminSettings();
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "tab", "Protected Principals");
        return page;
    }

    [Fact]
    public void TheBadges_SayWhatAnUnconvertedOrMissingEntryStillDoes()
    {
        var groupStale = ExchangeAdminWeb.Components.Pages.AdminSettings.StaleTip("Group");
        Assert.Contains("protects nobody", groupStale); // sweep-c item 17: a group entry naming no group no longer refuses every check
        Assert.Contains("a lookup that fails (rather than finding no group) still fails closed", groupStale);

        var flag = new ProtectedPrincipalFlaggedRow(ProtectedPrincipalRepository.KindGroup, "Ops", ProtectedPrincipalLeftReason.Ambiguous, "2 groups match");
        var tip = ExchangeAdminWeb.Components.Pages.AdminSettings.NotConvertedTip(ProtectedPrincipalRepository.KindGroup, flag, null);
        Assert.Contains("2 groups match", tip);
        Assert.Contains("the members of each are", tip);
        Assert.Contains("fails closed", tip);
        Assert.Contains("no resolve pass has looked at this entry yet",
            ExchangeAdminWeb.Components.Pages.AdminSettings.NotConvertedTip(ProtectedPrincipalRepository.KindUser, null, null));
    }

    private void SeedUsers(params string[] users) =>
        new ProtectedPrincipalRepository(TestConfigStore.Create(_h.Root)).Save(ProtectedPrincipalData.Empty with { Users = users });

    [Fact]
    public async Task ResolveNow_RunsThePass_ShowsWhatItLeft_AndFlagsTheRowWithItsReason()
    {
        // No directory-read credential is configured in the harness: the pass runs, reads nothing, writes nothing, and says so.
        SeedUsers(Cfo);
        var page = OpenOnProtectedTab();
        Assert.Contains("1 entry is not converted", string.Join(' ', SettingsPageHarness.RenderedText(page).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));

        await _h.RunAsync(page, "ResolveNowAsync");

        var shown = Field<string?>(page, "ppResolveResult") ?? "";
        Assert.Contains("Converted 0; left 1", shown);
        Assert.Contains("not configured", shown);
        Assert.True(Field<bool>(page, "ppResolveIsError"));
        Assert.False(Field<bool>(page, "ppResolving"));
        var flags = Field<ProtectedPrincipalMigrationFlags>(page, "ppFlags");
        Assert.Equal(ProtectedPrincipalLeftReason.DirectoryUnavailable, flags.For(ProtectedPrincipalRepository.KindUser, Cfo)?.Reason);
        var record = Assert.Single(_h.AuditEvents(), e => e.GetProperty("action").GetString() == ProtectedPrincipalIdentifierMigration.AuditAction);
        Assert.Equal("Failed", record.GetProperty("result").GetString());
    }

    [Fact]
    public async Task ResolveNow_IsRefused_WhileTheTabHasUnsavedChanges_AndRunsNothing()
    {
        SeedUsers(Cfo);
        var page = OpenOnProtectedTab();
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppNewPattern", "svc-*");
        await _h.RunAsync(page, "AddPpPattern");

        await _h.RunAsync(page, "ResolveNowAsync");

        Assert.Contains("Save or discard the changes on this tab first", Field<string?>(page, "ppResolveResult") ?? "");
        Assert.DoesNotContain(_h.AuditEvents(), e => e.GetProperty("action").GetString() == ProtectedPrincipalIdentifierMigration.AuditAction);
    }

    [Fact]
    public async Task ResolveNow_PressedWhileAPassFromThisPageRuns_DoesNotStartAnother()
    {
        SeedUsers(Cfo);
        var page = OpenOnProtectedTab();
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppResolving", true);

        await _h.RunAsync(page, "ResolveNowAsync");

        Assert.Contains("still running", Field<string?>(page, "ppResolveResult") ?? "");
        Assert.True(Field<bool>(page, "ppResolving")); // the running pass's flag is not cleared by the refused press
        Assert.DoesNotContain(_h.AuditEvents(), e => e.GetProperty("action").GetString() == ProtectedPrincipalIdentifierMigration.AuditAction);
    }
}
