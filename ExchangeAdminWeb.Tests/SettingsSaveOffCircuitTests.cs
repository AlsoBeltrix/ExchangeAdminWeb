using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Components;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;
using ExchangeOnlineConfigPage = ExchangeAdminWeb.Components.Pages.ExchangeOnlineConfig;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

#pragma warning disable BL0005
/// <summary>
/// aud-a41: the settings saves called the store on the page's circuit, so a write waiting for a lock held elsewhere held
/// the whole page for the wait. Each save's store write now runs off the circuit. Here each one is held at its store as
/// it opens - no lock and no wait - while an edit is made on the page: the edit runs at once, and the save then writes,
/// and counts as saved, only what it began with, so the edit stays on the page as unsaved.
/// </summary>
/// <remarks>
/// The compiled pages through <see cref="SettingsPageHarness"/>, whose renderer dispatcher is the serialised context a
/// circuit runs on. No directory: no case adds a group grant or a rule that is looked up.
/// </remarks>
public sealed class SettingsSaveOffCircuitTests : IDisposable
{
    private const string Module = "MailboxPermissions";
    private const string GroupA = "S-1-5-21-1000-2000-3000-4001";
    private const string GroupB = "S-1-5-21-1000-2000-3000-4002";
    private const string AppId = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static T Field<T>(object page, Type declaring, string name) => SettingsPageHarness.Field<T>(page, declaring, name);

    private static void Set(object page, Type declaring, string name, object? value) =>
        SettingsPageHarness.SetField(page, declaring, name, value);

    private static AdminPageDirtyState Dirty(object page, Type declaring) => Field<AdminPageDirtyState>(page, declaring, "dirty");

    private static string[] Names(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray() : [];

    /// <summary>
    /// Starts <paramref name="save"/> with its next write on <paramref name="store"/> held as it opens, makes
    /// <paramref name="edit"/> on the page's dispatcher while it is held, then lets the write go and waits for the save.
    /// </summary>
    /// <remarks>
    /// The save is started from another thread. The dispatcher runs a handler, and what it posts to itself, on the thread
    /// that invoked it until the handler truly yields - so a save started here whose write held the dispatcher would hold
    /// this test with it, and the edit would run only after the hold timed out (measured: 30 s, and then a pass).
    /// </remarks>
    private static async Task EditWhileTheWriteIsHeld(AttributeEditorHarness.SwitchableStore store, Func<Task> save, Func<Task> edit)
    {
        var cancel = TestContext.Current.CancellationToken;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.BeforeWrite = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        var saving = Task.Run(save, cancel);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), cancel), "the save never reached its store write");

            var editing = edit();
            var first = await Task.WhenAny(editing, Task.Delay(TimeSpan.FromSeconds(5), cancel));
            Assert.True(ReferenceEquals(first, editing), "an edit on the page waited for the save's store write: the write held the circuit");
            await editing;
            Assert.False(saving.IsCompleted, "the save completed while its store write was held");
        }
        finally
        {
            release.Set();
            await Task.WhenAny(saving, Task.Delay(TimeSpan.FromSeconds(30), cancel));
        }

        await saving;
    }

    private IReadOnlyDictionary<string, bool> StoredEnablement()
    {
        Assert.True(new ModuleEnablementRepository(TestConfigStore.Create(_h.Root)).TryGetAll(out var state), "enablement unreadable");
        return state;
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

    private string? StoredSecret()
    {
        Assert.True(new ModuleConfigRepository(TestConfigStore.Create(_h.Root))
            .TryReadModule(ProtectedPrincipalService.ProtectedPrincipalsModuleKey, out var values), "the secret is unreadable");
        return values.GetValueOrDefault(ProtectedPrincipalService.DirectoryReadSecretConfigKey);
    }

    private async Task AddPattern(AdminSettingsPage page, string pattern)
    {
        await _h.OnDispatcherAsync(() => Set(page, typeof(AdminSettingsPage), "ppNewPattern", pattern));
        await _h.RunAsync(page, "AddPpPattern");
    }

    private List<string> TogglableModules() =>
        _h.Catalog.GetOrdered()
            .Where(m => !m.IsSystemModule && !m.IsConfigOnly && m.DependsOn is null)
            .Select(m => m.Id)
            .ToList();

    // ---- Admin Settings ------------------------------------------------------------------------

    [Fact]
    public async Task AdminSettings_AnEnablementSave_LeavesTheCircuitFree_AndAToggleMadeMeanwhileStaysUnsaved()
    {
        var page = _h.OpenAdminSettings();
        var map = Field<Dictionary<string, bool>>(page, typeof(AdminSettingsPage), "moduleEnablement");
        var modules = TogglableModules().Where(map.ContainsKey).Take(2).ToList();
        Assert.True(modules.Count == 2, "two modules to toggle - this fixture is stale");
        var (first, second) = (modules[0], modules[1]);
        var firstWas = map[first];
        var secondWas = map[second];
        await _h.RunAsync(page, "ToggleModule", first, !firstWas);

        await EditWhileTheWriteIsHeld(_h.EnablementStore,
            () => _h.RunAsync(page, "SaveEnablement"),
            () => _h.RunAsync(page, "ToggleModule", second, !secondWas));

        var stored = StoredEnablement();
        Assert.Equal(!firstWas, stored[first]);
        Assert.False(stored.TryGetValue(second, out var secondNow) && secondNow != secondWas, "the toggle made during the save was written");
        Assert.Equal(!secondWas, map[second]);
        Assert.Equal(1, Dirty(page, typeof(AdminSettingsPage)).SectionCount("Module Enablement"));
    }

    [Fact]
    public async Task AdminSettings_ALogLevelSave_LeavesTheCircuitFree_AndRecordsTheLevelItWrote()
    {
        var page = _h.OpenAdminSettings();
        Set(page, typeof(AdminSettingsPage), "selectedLogLevel", "Warning");

        await EditWhileTheWriteIsHeld(_h.LogLevelStore,
            () => _h.RunAsync(page, "SaveLogLevel"),
            () => _h.OnDispatcherAsync(() => Set(page, typeof(AdminSettingsPage), "selectedLogLevel", "Error")));

        Assert.Equal("Warning", _h.StoredLogLevel());
        var record = Assert.Single(_h.AuditEvents());
        Assert.Contains("Warning", Names(record, "added"));
        Assert.DoesNotContain("Error", Names(record, "added"));
        Assert.Equal("Error", Field<string>(page, typeof(AdminSettingsPage), "selectedLogLevel"));
    }

    [Fact]
    public async Task AdminSettings_AProtectedSaveWhoseRulesWriteWaits_LeavesTheCircuitFree_AndARuleAddedMeanwhileStaysUnsaved()
    {
        var page = _h.OpenAdminSettings();
        await AddPattern(page, "svc-*");

        await EditWhileTheWriteIsHeld(_h.ProtectedStore,
            () => _h.RunAsync(page, "SaveProtectedPrincipals"),
            () => AddPattern(page, "late-*"));

        Assert.Equal(new[] { "sam_pattern:svc-*" }, StoredRules());
        Assert.Equal(new[] { "svc-*", "late-*" }, Field<List<string>>(page, typeof(AdminSettingsPage), "ppPatterns"));
        Assert.Equal(new[] { "svc-*" }, Field<List<string>>(page, typeof(AdminSettingsPage), "ppPatternsBaseline"));
        Assert.Equal(1, Dirty(page, typeof(AdminSettingsPage)).SectionCount("Protected Principals"));
        Assert.False(Field<bool>(page, typeof(AdminSettingsPage), "statusIsError"));
    }

    [Fact]
    public async Task AdminSettings_AProtectedSaveWhoseSecretCompareWaits_LeavesTheCircuitFree_AndASecretTypedMeanwhileStaysUnsaved()
    {
        new ModuleConfigRepository(TestConfigStore.Create(_h.Root)).SaveModule(
            ProtectedPrincipalService.ProtectedPrincipalsModuleKey,
            new Dictionary<string, string> { [ProtectedPrincipalService.DirectoryReadSecretConfigKey] = "777" });
        var page = _h.OpenAdminSettings();
        await _h.RunAsync(page, "SetPpDirectoryReadSecretId", "4242");

        // The secret changed, so the first write on its store is the compare ahead of the rules (aud-a28r).
        await EditWhileTheWriteIsHeld(_h.ModuleConfigStore,
            () => _h.RunAsync(page, "SaveProtectedPrincipals"),
            () => _h.RunAsync(page, "SetPpDirectoryReadSecretId", "5555"));

        Assert.Equal("4242", StoredSecret());
        Assert.Equal("5555", Field<string>(page, typeof(AdminSettingsPage), "ppDirectoryReadSecretId"));
        Assert.Equal("4242", Field<string>(page, typeof(AdminSettingsPage), "ppDirectoryReadSecretIdBaseline"));
        Assert.Equal(1, Dirty(page, typeof(AdminSettingsPage)).SectionCount("Protected Principals"));
    }

    // ---- Exchange Online Config ----------------------------------------------------------------

    [Fact]
    public async Task ExchangeOnlineConfig_AStatusSave_LeavesTheCircuitFree_AndKeepsTheToggleItWroteAsItsBaseline()
    {
        var page = _h.OpenExchangeOnlineConfig();
        var was = Field<bool>(page, typeof(ExchangeOnlineConfigPage), "moduleEnabled");
        Set(page, typeof(ExchangeOnlineConfigPage), "moduleEnabled", !was);

        await EditWhileTheWriteIsHeld(_h.EnablementStore,
            () => _h.RunAsync(page, "SaveModuleStatus"),
            () => _h.OnDispatcherAsync(() => Set(page, typeof(ExchangeOnlineConfigPage), "moduleEnabled", was)));

        Assert.Equal(!was, StoredEnablement()["ExchangeOnline"]);
        Assert.Equal(!was, Field<bool>(page, typeof(ExchangeOnlineConfigPage), "moduleEnabledBaseline"));
        Assert.Equal(was, Field<bool>(page, typeof(ExchangeOnlineConfigPage), "moduleEnabled"));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AConnectionSave_LeavesTheCircuitFree_AndAFieldTypedMeanwhileIsNotTakenAsSaved()
    {
        var page = _h.OpenExchangeOnlineConfig();
        Set(page, typeof(ExchangeOnlineConfigPage), "exoAppId", AppId);
        Set(page, typeof(ExchangeOnlineConfigPage), "exoOrganization", "contoso.onmicrosoft.com");
        Set(page, typeof(ExchangeOnlineConfigPage), "exoCertSubject", "CN=EXO-Automation");

        await EditWhileTheWriteIsHeld(_h.ModuleConfigStore,
            () => _h.RunAsync(page, "SaveExoConfig"),
            () => _h.OnDispatcherAsync(() => Set(page, typeof(ExchangeOnlineConfigPage), "exoOrganization", "fabrikam.onmicrosoft.com")));

        var stored = _h.StoredModuleConfig("ExchangeOnline");
        Assert.Contains("contoso.onmicrosoft.com", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("fabrikam", stored, StringComparison.Ordinal);
        Assert.Equal("fabrikam.onmicrosoft.com", Field<string>(page, typeof(ExchangeOnlineConfigPage), "exoOrganization"));
        Assert.Equal("contoso.onmicrosoft.com",
            Field<Dictionary<string, string>>(page, typeof(ExchangeOnlineConfigPage), "exoShown")[ExoConnectionPool.ConfigOrganizationKey]);
    }

    // ---- aud-a67 item 4: one save at a time from a Save button --------------------------------

    /// <summary>
    /// The button is disabled while a save runs, but a click the browser sent before that render arrived ran a second
    /// save beside the first, and whichever finished first cleared `saving` in its finally, so the button came back
    /// while the other was still running. Each page refuses the second press now.
    /// </summary>
    /// <remarks>
    /// The second press is made while the first save is held at its store write, which is the window a double click
    /// lands in. Each case counts the writes the store saw: one, not two.
    /// </remarks>
    [Theory]
    [InlineData("Admin Settings")]
    [InlineData("Exchange Online Config")]
    [InlineData("Module Configuration")]
    public async Task ASecondSavePressedWhileTheFirstRuns_IsRefused_AndTheStoreSeesOneWrite(string page)
    {
        var cancel = TestContext.Current.CancellationToken;
        var writes = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        Func<Task> press;
        Func<string> Status;
        Func<bool> IsError;
        // What "the press was not taken as handled" looks like on each page: the two with a dirty state keep their
        // count, and Exchange Online Config, which has none, keeps the values typed into its fields.
        Func<bool> KeptItsEdits;
        AttributeEditorHarness.SwitchableStore store;
        switch (page)
        {
            case "Admin Settings":
                {
                    var p = _h.OpenAdminSettings();
                    Set(p, typeof(AdminSettingsPage), "tab", "Module Enablement");
                    var enablement = Field<Dictionary<string, bool>>(p, typeof(AdminSettingsPage), "moduleEnablement");
                    var module = TogglableModules().First(enablement.ContainsKey);
                    await _h.RunAsync(p, "ToggleModule", module, !enablement[module]);
                    press = () => _h.RunAsync(p, "SaveCurrentTabAsync");
                    Status = () => Field<string?>(p, typeof(AdminSettingsPage), "statusMessage") ?? "";
                    IsError = () => Field<bool>(p, typeof(AdminSettingsPage), "statusIsError");
                    var dirtyAdmin = Dirty(p, typeof(AdminSettingsPage)).DirtyCount;
                    Assert.True(dirtyAdmin > 0, "the toggle left nothing unsaved - this fixture is stale");
                    KeptItsEdits = () => Dirty(p, typeof(AdminSettingsPage)).DirtyCount == dirtyAdmin;
                    store = _h.EnablementStore;
                    break;
                }

            case "Exchange Online Config":
                {
                    var p = _h.OpenExchangeOnlineConfig();
                    Set(p, typeof(ExchangeOnlineConfigPage), "exoAppId", AppId);
                    Set(p, typeof(ExchangeOnlineConfigPage), "exoOrganization", "contoso.onmicrosoft.com");
                    Set(p, typeof(ExchangeOnlineConfigPage), "exoCertSubject", "CN=EXO-Automation");
                    press = () => _h.RunAsync(p, "SaveExoConfig");
                    Status = () => Field<string?>(p, typeof(ExchangeOnlineConfigPage), "statusMessage") ?? "";
                    IsError = () => Field<bool>(p, typeof(ExchangeOnlineConfigPage), "statusIsError");
                    KeptItsEdits = () => Field<string>(p, typeof(ExchangeOnlineConfigPage), "exoOrganization") == "contoso.onmicrosoft.com";
                    store = _h.ModuleConfigStore;
                    break;
                }

            default:
                {
                    var p = _h.OpenModuleConfig(Module);
                    Set(p, typeof(ModuleConfigPage), "tab", "Configuration");
                    await _h.RunAsync(p, "SetConfigValue", "DelineaSecretId", "4242");
                    press = () => _h.RunAsync(p, "SaveCurrentTabAsync");
                    Status = () => Field<string?>(p, typeof(ModuleConfigPage), "statusMessage") ?? "";
                    IsError = () => Field<bool>(p, typeof(ModuleConfigPage), "statusIsError");
                    var dirtyModule = Dirty(p, typeof(ModuleConfigPage)).DirtyCount;
                    Assert.True(dirtyModule > 0, "the config edit left nothing unsaved - this fixture is stale");
                    KeptItsEdits = () => Dirty(p, typeof(ModuleConfigPage)).DirtyCount == dirtyModule;
                    store = _h.ModuleConfigStore;
                    break;
                }
        }

        store.BeforeWrite = Hold;

        var first = Task.Run(press, cancel);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), cancel), "the first save never reached its store write");

        // The second press, while the first is still in its write: refused, and it must not even open a write.
        await press();

        // cons-5. The refused press is said, not dropped: the second press can be for a different setting of the same
        // page, and it used to disappear with its fields still dirty and nothing on screen.
        Assert.Equal(ModuleConfigPage.SaveAlreadyRunning, Status());
        Assert.False(IsError(), "the refusal was shown as an error; it is not one - nothing failed");
        Assert.True(KeptItsEdits(), "the refused press cleared what was waiting to be saved");

        release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        Assert.Equal(1, Volatile.Read(ref writes));

        void Hold()
        {
            Interlocked.Increment(ref writes);
            // Armed again, so a second write would be counted rather than passing unseen.
            store.BeforeWrite = () => Interlocked.Increment(ref writes);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// aud-a67 item 3: the connection save's pool drain still ran on the circuit. DrainPool empties the pool one runspace
    /// at a time, each a synchronous Disconnect-ExchangeOnline against the service - up to five round trips - and then
    /// re-reads the connection from the shared config database, so it held the whole page for as long as the service took.
    /// </summary>
    [Fact]
    public async Task ExchangeOnlineConfig_AConnectionSavesPoolDrain_LeavesTheCircuitFree()
    {
        var page = _h.OpenExchangeOnlineConfig();
        Set(page, typeof(ExchangeOnlineConfigPage), "exoAppId", AppId);
        Set(page, typeof(ExchangeOnlineConfigPage), "exoOrganization", "contoso.onmicrosoft.com");
        Set(page, typeof(ExchangeOnlineConfigPage), "exoCertSubject", "CN=EXO-Automation");

        var cancel = TestContext.Current.CancellationToken;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // The drain is the first read after the save's write: it re-reads the three connection values.
        _h.ModuleConfigStore.BeforeWrite = () => _h.ModuleConfigStore.BeforeRead = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        var saving = Task.Run(() => _h.RunAsync(page, "SaveExoConfig"), cancel);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), cancel), "the save never reached its pool drain");

            var editing = _h.OnDispatcherAsync(() => Set(page, typeof(ExchangeOnlineConfigPage), "exoOrganization", "fabrikam.onmicrosoft.com"));
            var first = await Task.WhenAny(editing, Task.Delay(TimeSpan.FromSeconds(5), cancel));
            Assert.True(ReferenceEquals(first, editing), "an edit on the page waited for the pool drain: the drain held the circuit");
            await editing;
            Assert.False(saving.IsCompleted, "the save completed while its drain was held");
        }
        finally
        {
            release.Set();
            await Task.WhenAny(saving, Task.Delay(TimeSpan.FromSeconds(30), cancel));
        }

        await saving;
        Assert.Contains("contoso.onmicrosoft.com", _h.StoredModuleConfig(ExoConnectionPool.ConfigModuleKey), StringComparison.Ordinal);
    }

    // ---- Module Configuration ------------------------------------------------------------------

    [Fact]
    public async Task ModuleConfig_AConfigSave_LeavesTheCircuitFree_AndAValueTypedMeanwhileStaysUnsaved()
    {
        var page = _h.OpenModuleConfig(Module);
        await _h.RunAsync(page, "SetConfigValue", "DelineaSecretId", "4242");

        await EditWhileTheWriteIsHeld(_h.ModuleConfigStore,
            () => _h.RunAsync(page, "SaveConfigAsync"),
            () => _h.RunAsync(page, "SetConfigValue", "DelineaSecretId", "5555"));

        var stored = _h.StoredModuleConfig(Module);
        Assert.Contains("4242", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("5555", stored, StringComparison.Ordinal);
        Assert.Equal(1, Dirty(page, typeof(ModuleConfigPage)).SectionCount("Configuration"));
    }

    [Fact]
    public async Task ModuleConfig_AStatusSave_LeavesTheCircuitFree_AndAToggleMadeMeanwhileStaysUnsaved()
    {
        var page = _h.OpenModuleConfig(Module);
        var was = Field<bool>(page, typeof(ModuleConfigPage), "moduleEnabled");
        await _h.RunAsync(page, "ToggleModuleEnabled", !was);

        await EditWhileTheWriteIsHeld(_h.EnablementStore,
            () => _h.RunAsync(page, "SaveModuleEnablementAsync"),
            () => _h.RunAsync(page, "ToggleModuleEnabled", was));

        Assert.Equal(!was, StoredEnablement()[Module]);
        Assert.Equal(1, Dirty(page, typeof(ModuleConfigPage)).SectionCount("Module Status"));
    }

    [Fact]
    public async Task ModuleConfig_AnAccessSave_LeavesTheCircuitFree_AndARemovalMadeMeanwhileStaysUnsaved()
    {
        new SectionAccessRepository(TestConfigStore.Create(_h.Root))
            .SaveAll(new Dictionary<string, string[]> { [Module] = [GroupA, GroupB] });
        var page = _h.OpenModuleConfig(Module);
        await _h.RunAsync(page, "RemoveAccessGroup", Module, GroupB);

        await EditWhileTheWriteIsHeld(_h.SectionStore,
            () => _h.RunAsync(page, "SaveSectionAccessAsync"),
            () => _h.RunAsync(page, "RemoveAccessGroup", Module, GroupA));

        var stored = _h.StoredSectionAccess();
        Assert.Contains(GroupA, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(GroupB, stored, StringComparison.Ordinal);
        Assert.Empty(Field<Dictionary<string, List<string>>>(page, typeof(ModuleConfigPage), "accessState")[Module]);
        Assert.Equal(1, Dirty(page, typeof(ModuleConfigPage)).SectionCount("Section Access"));
    }

    [Fact]
    public async Task ModuleConfig_AnAdminsSave_LeavesTheCircuitFree_AndARemovalMadeMeanwhileStaysUnsaved()
    {
        new ModuleAdminRepository(TestConfigStore.Create(_h.Root)).SetForModule(Module, [GroupA, GroupB]);
        var page = _h.OpenModuleConfig(Module);
        await _h.RunAsync(page, "RemoveModuleAdmin", GroupB);

        await EditWhileTheWriteIsHeld(_h.AdminsStore,
            () => _h.RunAsync(page, "SaveModuleAdminsAsync"),
            () => _h.RunAsync(page, "RemoveModuleAdmin", GroupA));

        var stored = _h.StoredModuleAdmins(Module);
        Assert.Contains(GroupA, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(GroupB, stored, StringComparison.Ordinal);
        Assert.Empty(Field<List<string>>(page, typeof(ModuleConfigPage), "moduleAdminGroups"));
        Assert.Equal(1, Dirty(page, typeof(ModuleConfigPage)).SectionCount("Module Admins"));
    }

    [Fact]
    public async Task ModuleConfig_AnAllowlistSave_LeavesTheCircuitFree_AndARowAddedMeanwhileStaysUnsaved()
    {
        var page = _h.OpenModuleConfig("ADAttributeEditor", _h.CreateAttributeEditor());
        await _h.RunAsync(page, "AddAttribute");
        await _h.RunAsync(page, "UpdateAttrName", 0, new ChangeEventArgs { Value = "department" });
        await _h.RunAsync(page, "UpdateAttrLabel", 0, new ChangeEventArgs { Value = "Department" });

        await EditWhileTheWriteIsHeld(_h.AllowlistStore,
            () => _h.RunAsync(page, "SaveAllowlistAsync"),
            () => _h.RunAsync(page, "AddAttribute"));

        Assert.True(Field<bool>(page, typeof(ModuleConfigPage), "allowlistSaved"),
            Field<string?>(page, typeof(ModuleConfigPage), "allowlistError") ?? "the allowlist save did not complete");
        var stored = new AttributeEditorRepository(TestConfigStore.Create(_h.Root)).ReadAllowlistSnapshot();
        Assert.Equal(new[] { "department" }, stored.Rows.Select(r => r.Name).ToArray());
        Assert.Equal(1, Dirty(page, typeof(ModuleConfigPage)).SectionCount("Editable Attributes"));
    }

    [Fact]
    public async Task ModuleConfig_AnotherModuleOpenedWhileASaveWaits_LoadsOnlyOnceTheSaveIsDone()
    {
        // The page is reused when its route moves to another module (/module-config/{ModuleId}). With the save's write
        // off the circuit the admin can open another module while it waits; loading that module then, under the save,
        // left the save's bookkeeping on the other module's state.
        var page = _h.OpenModuleConfig(Module);
        Set(page, typeof(ModuleConfigPage), "tab", "Configuration");
        await _h.RunAsync(page, "SetConfigValue", "DelineaSecretId", "4242");
        var other = TogglableModules().First(id => id != Module);
        Task? loading = null;

        await EditWhileTheWriteIsHeld(_h.ModuleConfigStore,
            () => _h.RunAsync(page, "SaveCurrentTabAsync"),
            async () =>
            {
                await _h.OnDispatcherAsync(() => page.ModuleId = other);
                loading = _h.RunAsync(page, "OnParametersSetAsync");
                await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
                Assert.False(loading.IsCompleted, "the page loaded another module while a save on this one waited");
                Assert.Equal(Module, Field<string?>(page, typeof(ModuleConfigPage), "loadedModuleId"));
            });

        await loading!;
        Assert.Equal(other, Field<string?>(page, typeof(ModuleConfigPage), "loadedModuleId"));
        Assert.Contains("4242", _h.StoredModuleConfig(Module), StringComparison.Ordinal);
        Assert.Equal(0, Dirty(page, typeof(ModuleConfigPage)).SectionCount("Configuration"));
    }
}
#pragma warning restore BL0005
