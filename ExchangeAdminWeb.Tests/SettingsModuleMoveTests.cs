using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components;
using NSubstitute;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

#pragma warning disable BL0005
/// <summary>
/// aud-a66: Module Configuration is one page instance across /module-config/{ModuleId}, and its saves wait off the
/// circuit - for the directory, which confirms every grant a save adds, and for the store. Opening another module
/// while a save waited could apply the first module's diff, computed against the second module's baseline, to the
/// second module. Each save now takes its module, baseline and edits before its first await; a route change waits for
/// every save of the module shown, for at most the store's busy timeout plus 5 s (the folded-in item); a save that
/// finds its page on another module writes nothing, and one that ends there changes nothing on it.
/// </summary>
/// <remarks>
/// The compiled page through <see cref="SettingsPageHarness"/>. The directory is the harness's stand-in, which answers
/// and holds lookups as each case says and reaches no directory. A route change is the page's own parameter load as
/// the router runs it: <c>ModuleId</c> set, then <c>OnParametersSetAsync</c>. Saves start from another thread, as in
/// <see cref="SettingsSaveOffCircuitTests"/>, so a held save holds only the dispatcher.
/// </remarks>
public sealed class SettingsModuleMoveTests : IDisposable
{
    private const string First = "MailboxPermissions";
    private const string Second = "CalendarPermissions";
    private const string Third = "OutOfOffice";
    private const string Kept = "S-1-5-21-1000-2000-3000-6001";
    private const string Added = "S-1-5-21-1000-2000-3000-6002";
    private const string OnSecond = "S-1-5-21-1000-2000-3000-6003";

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static T Field<T>(object page, string name) => SettingsPageHarness.Field<T>(page, typeof(ModuleConfigPage), name);

    private static void Set(object page, string name, object? value) =>
        SettingsPageHarness.SetField(page, typeof(ModuleConfigPage), name, value);

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static string? Shown(ModuleConfigPage page) => Field<AdminModuleDescriptor?>(page, "module")?.Id;

    private static string? Status(ModuleConfigPage page) => Field<string?>(page, "statusMessage");

    private string DisplayName(string moduleId) => _h.Catalog.GetById(moduleId)!.DisplayName;

    private string[] StoredAdmins(string moduleId) =>
        new ModuleAdminRepository(TestConfigStore.Create(_h.Root)).GetForModule(moduleId).Order(StringComparer.Ordinal).ToArray();

    private string[] StoredAllowlist() =>
        new AttributeEditorRepository(TestConfigStore.Create(_h.Root)).ReadAllowlistSnapshot().Rows.Select(r => r.Name).ToArray();

    private static string[] Sorted(params string[] groups) => groups.Order(StringComparer.Ordinal).ToArray();

    private static void Reached(ManualResetEventSlim reached, string what) =>
        Assert.True(reached.Wait(TimeSpan.FromSeconds(10), Cancel), what);

    /// <summary>The first module's admins tab with a group added to the one it loaded; the second module holds its own.</summary>
    private async Task<ModuleConfigPage> AdminsWithAGroupAdded()
    {
        var admins = new ModuleAdminRepository(TestConfigStore.Create(_h.Root));
        admins.SetForModule(First, [Kept]);
        admins.SetForModule(Second, [OnSecond]);
        var page = _h.OpenModuleConfig(First);
        Set(page, "tab", "Module Admins");
        await _h.OnDispatcherAsync(() => Set(page, "newAdminGroup", Added));
        await _h.RunAsync(page, "AddModuleAdmin");
        Assert.Equal([Kept, Added], Field<List<string>>(page, "moduleAdminGroups"));
        _h.GroupDirectory.Answer = SettingsPageHarness.StandInDirectory.Group;
        return page;
    }

    /// <summary>The Save button, pressed from another thread so a held save holds only the dispatcher.</summary>
    private Task Save(ModuleConfigPage page) => Task.Run(() => _h.RunAsync(page, "SaveCurrentTabAsync"), Cancel);

    /// <summary>
    /// A save that begins while another is already in flight, past the button's one-at-a-time guard (aud-a67): the page's
    /// own StartSaveAsync, which is what the button runs once its guard has passed, registered in savesInFlight exactly as
    /// a button press is. The window this stands for is a real one - a route change that outlasts its bound loads the next
    /// module and LoadState clears `saving`, so a save can begin while an older one still runs - and these cases hold the
    /// older save at the directory, which is where that happens.
    /// </summary>
    private Task SaveAlongside(ModuleConfigPage page) => Task.Run(() => _h.RunAsync(page, "StartSaveAsync"), Cancel);

    /// <summary>The router's move to <paramref name="moduleId"/>: the parameter set, then the page's parameter load.</summary>
    private async Task<Task> Open(ModuleConfigPage page, string moduleId)
    {
        await _h.OnDispatcherAsync(() => page.ModuleId = moduleId);
        return _h.RunAsync(page, "OnParametersSetAsync");
    }

    /// <summary>Holds the next write on <paramref name="store"/> as it opens, before its lock, until the release is set.</summary>
    private static (ManualResetEventSlim Reached, ManualResetEventSlim Release) HoldNextWrite(AttributeEditorHarness.SwitchableStore store)
    {
        var reached = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        store.BeforeWrite = () =>
        {
            reached.Set();
            release.Wait(TimeSpan.FromSeconds(60));
        };
        return (reached, release);
    }

    // ---- the route change waits ----------------------------------------------------------------

    [Fact]
    public async Task AnotherModuleOpenedWhileAnAdminsSaveAsksTheDirectory_LoadsOnlyOnceTheSaveIsDone()
    {
        var page = await AdminsWithAGroupAdded();
        var release = _h.GroupDirectory.HoldNext();
        var saving = Save(page);
        Reached(_h.GroupDirectory.Entered, "the save never asked the directory");

        var loading = await Open(page, Second);
        await Task.Delay(Short, Cancel);
        Assert.False(loading.IsCompleted, "the page loaded another module while a save on this one waited for the directory");
        Assert.Equal(First, Shown(page));

        release.Set();
        await saving.WaitAsync(Long, Cancel);
        await loading.WaitAsync(Long, Cancel);

        Assert.Equal(Sorted(Kept, Added), StoredAdmins(First));
        Assert.Equal([OnSecond], StoredAdmins(Second));
        Assert.Equal(Second, Shown(page));
        Assert.Equal([OnSecond], Field<List<string>>(page, "moduleAdminGroups"));
        Assert.Equal([OnSecond], Field<List<string>>(page, "moduleAdminGroupsBaseline"));
        Assert.Equal([Added], _h.GroupDirectory.Asked);
    }

    [Fact]
    public async Task ASecondSaveThatFinishesFirst_DoesNotLetAnotherModuleLoadUnderTheFirst()
    {
        var page = await AdminsWithAGroupAdded();
        var release = _h.GroupDirectory.HoldNext();
        var first = Save(page);
        Reached(_h.GroupDirectory.Entered, "the first save never asked the directory");

        // A second save begun before the first ends (aud-a67: past the button's guard, as one begun after a route change
        // that outlasted its bound would be).
        await SaveAlongside(page).WaitAsync(Long, Cancel);
        Assert.Equal(Sorted(Kept, Added), StoredAdmins(First));
        Assert.False(first.IsCompleted, "the first save finished while its lookup was held");

        var loading = await Open(page, Second);
        await Task.Delay(Short, Cancel);
        var loadedUnderTheFirst = loading.IsCompleted;

        release.Set();
        await first.WaitAsync(Long, Cancel);
        await loading.WaitAsync(Long, Cancel);

        Assert.Equal([OnSecond], StoredAdmins(Second));
        Assert.Equal(Sorted(Kept, Added), StoredAdmins(First));
        Assert.Equal([OnSecond], Field<List<string>>(page, "moduleAdminGroupsBaseline"));
        Assert.False(loadedUnderTheFirst, "the page loaded another module while the first of two saves still waited for the directory");
    }

    [Fact]
    public async Task ASaveStartedWhileAnotherModuleWaitsToLoad_IsWaitedForAsWell()
    {
        var page = await AdminsWithAGroupAdded();
        var releaseFirst = _h.GroupDirectory.HoldNext();
        var first = Save(page);
        Reached(_h.GroupDirectory.Entered, "the first save never asked the directory");

        var loading = await Open(page, Second);
        await Task.Delay(Short, Cancel);
        Assert.False(loading.IsCompleted, "the page loaded another module while a save on this one waited for the directory");

        // Held at its lookup, not its write: the admin service makes one write wait for another.
        var releaseSecond = _h.GroupDirectory.HoldNext();
        var second = SaveAlongside(page);
        Reached(_h.GroupDirectory.Entered, "the second save never asked the directory");

        releaseFirst.Set();
        await first.WaitAsync(Long, Cancel);
        await Task.Delay(Short, Cancel);
        Assert.False(loading.IsCompleted, "the page loaded another module while a save started during its wait still waited for the directory");
        Assert.Equal(First, Shown(page));

        releaseSecond.Set();
        await second.WaitAsync(Long, Cancel);
        await loading.WaitAsync(Long, Cancel);

        Assert.Equal(Second, Shown(page));
        Assert.Equal(Sorted(Kept, Added), StoredAdmins(First));
        Assert.Equal([OnSecond], StoredAdmins(Second));
    }

    // ---- past the bound ------------------------------------------------------------------------

    [Fact]
    public async Task AnAdminsSaveWhoseLookupOutlastsTheWait_Refuses_AndWritesToNeitherModule()
    {
        var page = await AdminsWithAGroupAdded();
        Set(page, "saveWaitBound", Short);
        var release = _h.GroupDirectory.HoldNext();
        var saving = Save(page);
        Reached(_h.GroupDirectory.Entered, "the save never asked the directory");

        var loading = await Open(page, Second);
        await loading.WaitAsync(Long, Cancel);
        Assert.Equal(Second, Shown(page));
        Assert.Contains($"A save on {DisplayName(First)} had not finished", Status(page), StringComparison.Ordinal);

        release.Set();
        await saving.WaitAsync(Long, Cancel);

        Assert.Equal([Kept], StoredAdmins(First));
        Assert.Equal([OnSecond], StoredAdmins(Second));
        Assert.Equal([OnSecond], Field<List<string>>(page, "moduleAdminGroups"));
        Assert.Equal([OnSecond], Field<List<string>>(page, "moduleAdminGroupsBaseline"));
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal($"ModuleAdmins:{First}", Text(record, "section"));
        Assert.Equal("PageMoved", Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.StartsWith("Not saved: the page moved to another module", Text(record, "error"), StringComparison.Ordinal);
        Assert.Contains("nothing was written", Text(record, "error"), StringComparison.Ordinal);
        Assert.Equal($"{DisplayName(First)}: {Text(record, "error")}", Status(page));
        Assert.True(Field<bool>(page, "statusIsError"));
    }

    [Fact]
    public async Task AnAccessSaveWhoseLookupOutlastsTheWait_Refuses_AndWritesToNeitherModule()
    {
        var firstAlias = _h.Catalog.GetById(First)!.MainPermission.PolicyAlias;
        var secondAlias = _h.Catalog.GetById(Second)!.MainPermission.PolicyAlias;
        new SectionAccessRepository(TestConfigStore.Create(_h.Root))
            .SaveAll(new Dictionary<string, string[]> { [firstAlias] = [Kept], [secondAlias] = [OnSecond] });
        var page = _h.OpenModuleConfig(First);
        Set(page, "tab", "Section Access");
        await _h.OnDispatcherAsync(() => Field<Dictionary<string, string>>(page, "accessInputs")[firstAlias] = Added);
        await _h.RunAsync(page, "AddAccessGroup", firstAlias);
        Assert.Equal([Kept, Added], Field<Dictionary<string, List<string>>>(page, "accessState")[firstAlias]);
        _h.GroupDirectory.Answer = SettingsPageHarness.StandInDirectory.Group;
        Set(page, "saveWaitBound", Short);
        var stored = _h.StoredSectionAccess();

        var release = _h.GroupDirectory.HoldNext();
        var saving = Save(page);
        Reached(_h.GroupDirectory.Entered, "the save never asked the directory");
        var loading = await Open(page, Second);
        await loading.WaitAsync(Long, Cancel);
        Assert.Equal(Second, Shown(page));

        release.Set();
        await saving.WaitAsync(Long, Cancel);

        Assert.Equal(stored, _h.StoredSectionAccess());
        Assert.Equal([OnSecond], Field<Dictionary<string, List<string>>>(page, "accessState")[secondAlias]);
        Assert.Equal([OnSecond], Field<Dictionary<string, List<string>>>(page, "accessBaseline")[secondAlias]);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal(firstAlias, Text(record, "section"));
        Assert.Equal("PageMoved", Text(record, "refusedAt"));
        Assert.Equal([firstAlias], record.GetProperty("sections").EnumerateArray().Select(s => s.GetString() ?? "").ToArray());
        Assert.Equal($"{DisplayName(First)}: {Text(record, "error")}", Status(page));
    }

    [Fact]
    public async Task ASaveThatNeverFinishes_HoldsTheNextModuleOnlyForTheBound_AndOnlyOnce()
    {
        var page = await AdminsWithAGroupAdded();
        Set(page, "saveWaitBound", Short);
        var (writing, release) = HoldNextWrite(_h.AdminsStore);
        var saving = Save(page);
        try
        {
            Reached(writing, "the save never reached its write");

            var clock = Stopwatch.StartNew();
            var loading = await Open(page, Second);
            await loading.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
            Assert.True(clock.Elapsed >= Short, $"the page loaded the next module after {clock.ElapsedMilliseconds} ms, inside the wait");
            Assert.Equal(Second, Shown(page));
            var notice = Status(page) ?? "";
            Assert.Contains($"A save on {DisplayName(First)} had not finished after 0.3 seconds", notice, StringComparison.Ordinal);
            Assert.Contains($"never {DisplayName(Second)}", notice, StringComparison.Ordinal);
            Assert.True(Field<bool>(page, "statusIsError"));
            Assert.False(Field<bool>(page, "saving"), "the next module's Save is still withheld by the unfinished save");
            Assert.False(saving.IsCompleted, "the held save finished");

            // The unfinished save belongs to an earlier load: the next move does not wait for it again.
            Set(page, "saveWaitBound", Long);
            var again = await Open(page, Third);
            await again.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
            Assert.Equal(Third, Shown(page));
        }
        finally
        {
            release.Set();
            await saving.WaitAsync(Long, Cancel);
        }
    }

    [Fact]
    public async Task AnAdminsWriteThatOutlastsTheWait_LandsOnItsOwnModule_AndLeavesTheNextOnesStateAlone()
    {
        var page = await AdminsWithAGroupAdded();
        Set(page, "saveWaitBound", Short);
        var (writing, release) = HoldNextWrite(_h.AdminsStore);
        var saving = Save(page);
        try
        {
            Reached(writing, "the save never reached its write");
            var loading = await Open(page, Second);
            await loading.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
            Assert.Equal(Second, Shown(page));
        }
        finally
        {
            release.Set();
        }

        await saving.WaitAsync(Long, Cancel);

        Assert.Equal(Sorted(Kept, Added), StoredAdmins(First));
        Assert.Equal([OnSecond], StoredAdmins(Second));
        Assert.Equal([OnSecond], Field<List<string>>(page, "moduleAdminGroups"));
        Assert.Equal([OnSecond], Field<List<string>>(page, "moduleAdminGroupsBaseline"));
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal($"ModuleAdmins:{First}", Text(record, "section"));
        Assert.False(record.TryGetProperty("refusedAt", out _));
        Assert.Equal($"{DisplayName(First)}: Module admins saved.", Status(page));
    }

    // ---- a save started while a load waits -----------------------------------------------------

    [Fact]
    public async Task ASaveStartedWhileAnotherModuleLoads_SavesTheModuleStillShown_AndNothingReachesTheOther()
    {
        var page = await AdminsWithAGroupAdded();
        var routeAuthorized = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(routeAuthorized.Task, Task.FromResult(AuthorizationResult.Success()));

        var loading = await Open(page, Second);
        await Task.Delay(Short, Cancel);
        Assert.False(loading.IsCompleted, "the route change did not wait at its authorization - this fixture is stale");

        await Save(page).WaitAsync(Long, Cancel);
        Assert.Equal([OnSecond], StoredAdmins(Second));
        Assert.Equal(Sorted(Kept, Added), StoredAdmins(First));

        routeAuthorized.SetResult(AuthorizationResult.Success());
        await loading.WaitAsync(Long, Cancel);
        Assert.Equal(Second, Shown(page));
        Assert.Equal([OnSecond], Field<List<string>>(page, "moduleAdminGroupsBaseline"));
    }

    [Theory]
    [InlineData("Configuration")]
    [InlineData("Module Status")]
    [InlineData("Editable Attributes")]
    public async Task ASaveWhosePageMovesDuringItsAuthorizationRecheck_Refuses_AndWritesNothing(string tab)
    {
        var moduleId = tab == "Editable Attributes" ? "ADAttributeEditor" : First;
        var page = _h.OpenModuleConfig(moduleId, tab == "Editable Attributes" ? _h.CreateAttributeEditor() : null);
        Set(page, "tab", tab);
        switch (tab)
        {
            case "Configuration":
                await _h.RunAsync(page, "SetConfigValue", "DelineaSecretId", "4242");
                break;
            case "Module Status":
                await _h.RunAsync(page, "ToggleModuleEnabled", !Field<bool>(page, "moduleEnabled"));
                break;
            default:
                await _h.RunAsync(page, "AddAttribute");
                await _h.RunAsync(page, "UpdateAttrName", 0, new ChangeEventArgs { Value = "department" });
                await _h.RunAsync(page, "UpdateAttrLabel", 0, new ChangeEventArgs { Value = "Department" });
                break;
        }

        var config = _h.StoredModuleConfig(moduleId);
        var enablement = _h.StoredEnablement();
        var allowlist = StoredAllowlist();
        Set(page, "saveWaitBound", Short);
        using var atRecheck = new ManualResetEventSlim();
        var saveAuthorized = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(_ =>
            {
                atRecheck.Set();
                return saveAuthorized.Task;
            }, _ => Task.FromResult(AuthorizationResult.Success()));

        var saving = Save(page);
        Reached(atRecheck, "the save never re-checked its authorization");
        var loading = await Open(page, Second);
        await loading.WaitAsync(Long, Cancel);
        Assert.Equal(Second, Shown(page));

        saveAuthorized.SetResult(AuthorizationResult.Success());
        await saving.WaitAsync(Long, Cancel);

        Assert.Equal(config, _h.StoredModuleConfig(moduleId));
        Assert.Equal(enablement, _h.StoredEnablement());
        Assert.Equal(allowlist, StoredAllowlist());
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("PageMoved", Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal($"{DisplayName(moduleId)}: {Text(record, "error")}", Status(page));
    }

    // ---- the bound and the seam ------------------------------------------------------------------

    /// <summary>
    /// aud-a67 item 1: only ReloadAsync and the saves cleared the unsaved-edit counts, so opening another module on this
    /// page instance carried the previous module's - its count in the save bar, its dot on a tab whose list had just been
    /// replaced, and the unsaved-changes guard warning about edits no longer anywhere on the page.
    /// </summary>
    [Fact]
    public async Task AnotherModuleOpenedAfterAnEdit_LoadsClean_WithNoCountFromTheModuleLeft()
    {
        var page = await AdminsWithAGroupAdded();
        var dirty = Field<AdminPageDirtyState>(page, "dirty");
        Assert.True(dirty.IsDirty, "the edit left no unsaved count - this fixture is stale");

        await (await Open(page, Second)).WaitAsync(Long, Cancel);

        Assert.Equal(Second, Shown(page));
        Assert.False(dirty.IsDirty, $"the next module's page still counts unsaved edits: {dirty.Summary()}");
        Assert.Equal(0, dirty.DirtyCount);
    }

    /// <summary>
    /// aud-a67 item 9: the load read the ModuleId parameter after its awaits - the descriptor with one read, the
    /// delegated-admin grant with another, on either side of the authorization await - so a route change in one of
    /// those windows made the load fetch the newer module's descriptor, or decide this page with another module's
    /// grant. It takes the module it was asked for before its first await now, and uses that for all of it.
    /// </summary>
    /// <remarks>
    /// One case per window, because each is held at a different await and only one of the two reads sits after each:
    /// the authentication state comes before the descriptor, the authorization re-check before the grant check.
    /// </remarks>
    [Theory]
    [InlineData("the authentication state")]
    [InlineData("the authorization re-check")]
    public async Task ARouteChangeDuringALoad_LeavesTheLoadOnTheModuleItWasAskedFor(string held)
    {
        var page = _h.OpenModuleConfig(First);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (held == "the authentication state")
        {
            var state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, _h.AdminName), new Claim(ClaimTypes.GroupSid, SettingsPageHarness.AdminSid)], "test")));
            var heldState = release.Task.ContinueWith(_ => state, Cancel, TaskContinuationOptions.None, TaskScheduler.Default);
            _h.AuthState.GetAuthenticationStateAsync().Returns(heldState, Task.FromResult(state));
        }
        else
        {
            // Only a module admin, and only of the module this load is for: the grant check then decides the page, and
            // it is the read that sits after the authorization await.
            new ModuleAdminRepository(TestConfigStore.Create(_h.Root)).SetForModule(Second, [SettingsPageHarness.AdminSid]);
            var heldAuth = release.Task.ContinueWith(_ => AuthorizationResult.Failed(), Cancel, TaskContinuationOptions.None, TaskScheduler.Default);
            _h.Authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
                .Returns(heldAuth, Task.FromResult(AuthorizationResult.Failed()));
        }

        var loadingSecond = await Open(page, Second);
        await Task.Delay(Short, Cancel);
        Assert.False(loadingSecond.IsCompleted, $"the load did not wait at {held} - this fixture is stale");

        // A second route change while the first load is still waiting, as a second click in the nav makes.
        await _h.OnDispatcherAsync(() => page.ModuleId = Third);
        release.SetResult();
        await loadingSecond.WaitAsync(Long, Cancel);

        Assert.Equal(Second, Shown(page));
        Assert.DoesNotContain("access-denied", _h.Navigation.Navigations);
    }

    /// <summary>
    /// aud-a67 item 10: the OU browser's enumeration runs off the circuit, and every line after it applied to whatever
    /// module the page was showing when the answer came back - its entries, its filter, the availability flag and the
    /// spinner. The browser renders only on AD Attribute Editor and writes nothing, so what it cost was one module's
    /// organizational units in another module's browser. The answer lands only on the load that asked for it now.
    /// </summary>
    [Fact]
    public async Task AnOuEnumerationThatOutlastsARouteChange_LeavesTheNextModulesBrowserAlone()
    {
        _h.OrganizationalUnits.Entries =
        [
            new OUEntry("Staff", "OU=Staff,DC=example,DC=test", 1),
            new OUEntry("Contractors", "OU=Contractors,DC=example,DC=test", 1),
        ];
        var page = _h.OpenModuleConfig("ADAttributeEditor", _h.CreateAttributeEditor());
        var release = _h.OrganizationalUnits.HoldNext();

        var browsing = Task.Run(() => _h.RunAsync(page, "ToggleOUBrowser"), Cancel);
        Reached(_h.OrganizationalUnits.Entered, "the browser never asked for the organizational units");

        // The page moves while the enumeration is held, and the next module's load starts the browser clean.
        await (await Open(page, First)).WaitAsync(Long, Cancel);
        Assert.Equal(First, Shown(page));

        release.Set();
        await browsing.WaitAsync(Long, Cancel);

        Assert.Empty(Field<List<OUEntry>>(page, "ouAllEntries"));
        Assert.Empty(Field<List<OUEntry>>(page, "ouFilteredEntries"));
        Assert.False(Field<bool>(page, "showOUBrowser"), "the browser opened over the module the page had moved to");
        Assert.False(Field<bool>(page, "ouLoading"));
    }

    /// <summary>
    /// aud-a67 item 11: a save names what it refused from the page's own labels - the alias through the page's
    /// descriptor, the group through the page's display names - and both are replaced by the next module's load. A
    /// refusal whose directory lookup outlasted a route change therefore reached the admin naming a bare SID, which
    /// is the one part of that message they read. Each save takes its labels before its first await now.
    /// </summary>
    [Fact]
    public async Task AnAccessSaveRefusedAfterAMove_StillNamesTheGroupItRefused()
    {
        var alias = _h.Catalog.GetById(First)!.MainPermission.PolicyAlias;
        var page = _h.OpenModuleConfig(First);
        Set(page, "tab", "Section Access");

        // A group added on this page, with the display name this load holds for it and no other.
        Field<Dictionary<string, string>>(page, "accessDisplayNames")[Added] = "EXAMPLE\\Directory-Admins";
        await _h.OnDispatcherAsync(() => Field<Dictionary<string, List<string>>>(page, "accessState")[alias].Add(Added));

        // The directory refuses it, and the lookup is held until the page has moved to another module.
        _h.GroupDirectory.Answer = _ => new DirectoryValidationResult(DirectoryLookupOutcome.NotFound, null);
        var release = _h.GroupDirectory.HoldNext();
        var saving = Save(page);
        Reached(_h.GroupDirectory.Entered, "the access save never asked the directory");

        // Past the bound, so the next module LOADS while the lookup is still held: that load replaces the page display
        // names and its descriptor, which is the window this item is about. Inside the bound the route change waits and
        // the message is built before anything is replaced.
        Set(page, "saveWaitBound", Short);
        var loading = await Open(page, Second);
        await loading.WaitAsync(Long, Cancel);
        Assert.Equal(Second, Shown(page));

        release.Set();
        await saving.WaitAsync(Long, Cancel);

        // The audit record, not the banner: the route change clears statusMessage as it loads, so the refusal the
        // admin can still read is the recorded one - and it is the same text the banner would have carried.
        var refusal = Assert.Single(_h.AuditEvents(), r => Text(r, "refusedAt") == "GroupGrantCheck");
        var shown = Text(refusal, "error");
        Assert.Contains("Directory-Admins", shown, StringComparison.Ordinal);
        Assert.DoesNotContain(Added + ":", shown, StringComparison.Ordinal);
        Assert.Empty(StoredSectionAccess(alias));
    }

    private string[] StoredSectionAccess(string alias)
    {
        var repository = new SectionAccessRepository(TestConfigStore.Create(_h.Root));
        return repository.TryGetAll(out var access) && access.TryGetValue(alias, out var groups)
            ? [.. groups.Order(StringComparer.Ordinal)]
            : [];
    }

    [Fact]
    public void TheWait_IsTheStoresBusyTimeoutPlusFiveSeconds()
    {
        var page = _h.OpenModuleConfig(First);

        Assert.Equal(SqliteConnectionFactory.BusyTimeout + TimeSpan.FromSeconds(5), Field<TimeSpan>(page, "saveWaitBound"));
    }

    [Fact]
    public void ThePageAsksTheDirectoryThroughTheSeam_RegisteredAsTheDirectorySingleton()
    {
        var program = ProgressScan.CodeView(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Program.cs")));
        Assert.Contains(
            "builder.Services.AddSingleton<IGroupGrantDirectory>(sp => sp.GetRequiredService<ADDirectorySearchService>());",
            program, StringComparison.Ordinal);

        var page = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ModuleConfig.razor"));
        Assert.Contains("@inject ExchangeAdminWeb.Services.IGroupGrantDirectory ADSearch", page, StringComparison.Ordinal);
    }
}
#pragma warning restore BL0005
