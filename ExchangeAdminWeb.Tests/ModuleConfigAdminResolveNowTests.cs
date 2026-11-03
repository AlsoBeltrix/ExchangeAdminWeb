using ExchangeAdminWeb.Services;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/ProtectedPrincipalIdentifiers-Plan.md S5, B.6: the Module Configuration Admins pane's list of grants stored by name,
/// the Remove beside each, and Resolve now.
/// </summary>
public sealed class ModuleConfigAdminResolveNowTests : IDisposable
{
    private const string Module = "MailboxPermissions";
    private const string NameGrant = "CONTOSO\\Old Helpdesk";
    private const string OtherNameGrant = "Ops Team";
    private const string SidGrant = "S-1-5-21-1000000000-1000000000-1000000000-3999";

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static T Field<T>(ModuleConfigPage page, string name) => SettingsPageHarness.Field<T>(page, typeof(ModuleConfigPage), name);

    private static string Text(ModuleConfigPage page) =>
        string.Join(' ', SettingsPageHarness.RenderedText(page).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void TheAdminsPane_ListsEachGrantStoredByName_WithItsReason_AndARemoveBesideIt()
    {
        _h.ModuleAdmins.SetModuleAdmins(Module, [NameGrant, SidGrant]);

        var page = _h.OpenModuleConfig(Module, handlersOnly: false);
        var text = Text(page);

        Assert.Contains("1 grant is stored by name", text);
        // The flagged row carries its badge, its reason and its own Remove, before the next row begins.
        var flagged = text.IndexOf(NameGrant, StringComparison.Ordinal);
        Assert.True(flagged >= 0);
        var row = text[flagged..text.IndexOf(SidGrant, flagged, StringComparison.Ordinal)];
        Assert.Contains("not converted", row);
        Assert.Contains("no resolve pass has looked at it yet", row);
        Assert.Contains("Remove", row);
    }

    [Fact]
    public async Task RemoveBesideAFlaggedGrant_DeletesExactlyThatRow()
    {
        _h.ModuleAdmins.SetModuleAdmins(Module, [NameGrant, OtherNameGrant, SidGrant]);
        var page = _h.OpenModuleConfig(Module);
        Assert.Equal(2, await CountOnDispatcher(page));

        await _h.RunAsync(page, "RemoveModuleAdmin", NameGrant);
        await _h.RunAsync(page, "SaveModuleAdminsAsync");

        Assert.Equal([OtherNameGrant, SidGrant], _h.ModuleAdmins.GetModuleAdmins(Module).Order(StringComparer.Ordinal));
    }

    private async Task<int> CountOnDispatcher(ModuleConfigPage page)
    {
        var count = 0;
        await _h.OnDispatcherAsync(() => count = (int)typeof(ModuleConfigPage)
            .GetMethod("UnconvertedAdminCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(page, null)!);
        return count;
    }

    [Fact]
    public async Task ResolveNow_RunsThePass_ShowsWhatItLeft_AndFlagsTheGrantWithItsReason()
    {
        // No directory-read credential is configured in the harness: the pass runs, reads nothing, writes nothing, and says so.
        _h.ModuleAdmins.SetModuleAdmins(Module, [NameGrant]);
        var page = _h.OpenModuleConfig(Module);

        await _h.RunAsync(page, "ResolveAdminsNowAsync");

        var shown = Field<string?>(page, "adminResolveResult") ?? "";
        Assert.Contains("Converted 0; left 1", shown);
        Assert.Contains("not configured", shown);
        Assert.True(Field<bool>(page, "adminResolveIsError"));
        Assert.False(Field<bool>(page, "adminResolving"));
        var flags = Field<ModuleAdminMigrationFlags>(page, "adminFlags");
        Assert.Equal(ProtectedPrincipalLeftReason.DirectoryUnavailable, flags.For(Module, NameGrant)?.Reason);
        Assert.Equal([NameGrant], _h.ModuleAdmins.GetModuleAdmins(Module));
        var record = Assert.Single(_h.AuditEvents(), e => e.GetProperty("action").GetString() == ModuleAdminSidMigration.AuditAction);
        Assert.Equal("Failed", record.GetProperty("result").GetString());
    }

    [Fact]
    public async Task ResolveNow_IsRefused_WhileTheTabHasUnsavedChanges_AndRunsNothing()
    {
        _h.ModuleAdmins.SetModuleAdmins(Module, [NameGrant, SidGrant]);
        var page = _h.OpenModuleConfig(Module);
        await _h.RunAsync(page, "RemoveModuleAdmin", SidGrant);

        await _h.RunAsync(page, "ResolveAdminsNowAsync");

        Assert.Contains("Save or discard the changes on this tab first", Field<string?>(page, "adminResolveResult") ?? "");
        Assert.DoesNotContain(_h.AuditEvents(), e => e.GetProperty("action").GetString() == ModuleAdminSidMigration.AuditAction);
    }

    [Fact]
    public async Task ResolveNow_PressedWhileAPassFromThisPageRuns_DoesNotStartAnother()
    {
        _h.ModuleAdmins.SetModuleAdmins(Module, [NameGrant]);
        var page = _h.OpenModuleConfig(Module);
        SettingsPageHarness.SetField(page, typeof(ModuleConfigPage), "adminResolving", true);

        await _h.RunAsync(page, "ResolveAdminsNowAsync");

        Assert.Contains("still running", Field<string?>(page, "adminResolveResult") ?? "");
        Assert.True(Field<bool>(page, "adminResolving"));
        Assert.DoesNotContain(_h.AuditEvents(), e => e.GetProperty("action").GetString() == ModuleAdminSidMigration.AuditAction);
    }
}
