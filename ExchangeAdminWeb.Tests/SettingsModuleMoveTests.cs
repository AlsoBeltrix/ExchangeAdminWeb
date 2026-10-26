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
    /// aud-a67 item 9, with cons-4: the load read the ModuleId parameter after its awaits - the descriptor with one read, the
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

        // cons-4 decided what this ends as: the route asks for the third module, so the load for the second applies
        // nothing at all - not its descriptor, and not a navigation from checking a grant for the wrong module. The
        // page keeps what it had until the route change for the third module runs its own load.
        Assert.Equal(First, Shown(page));
        Assert.DoesNotContain("access-denied", _h.Navigation.Navigations);
    }

    /// <summary>
    /// aud-a67 (9)'s mechanism, pinned where behaviour no longer can: since cons-4 a load whose module the route has
    /// moved past applies nothing, so reading the parameter again mid-flight is invisible in the outcome - the abort
    /// hides it. What must stay true is that there is one read. Every later use goes through the captured local, or
    /// through StillRequested, which is the only other place the parameter may be read.
    /// </summary>
    [Fact]
    public void TheLoad_ReadsItsModuleParameterOnce_AndNeverAgainAfterAnAwait()
    {
        var body = AuditCategoryFilingTests.MethodBody(
            ProgressScan.CodeView(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ModuleConfig.razor"))),
            "protected override async Task OnParametersSetAsync()");

        const string capture = "var requested = ModuleId;";
        var captured = body.IndexOf(capture, StringComparison.Ordinal);
        Assert.True(captured > 0, "OnParametersSetAsync no longer captures its module before its first await - this guard is stale");

        // The parameter itself, not a field whose name ends in it (loadedModuleId is the load's own bookkeeping).
        var after = body[(captured + capture.Length)..];
        var reread = System.Text.RegularExpressions.Regex.Match(after, @"(?<![A-Za-z0-9_])ModuleId(?![A-Za-z0-9_])");
        Assert.False(
            reread.Success,
            $"OnParametersSetAsync reads ModuleId again after capturing it: {Line(after, reread.Index)}");
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

    /// <summary>
    /// aud-a67 item 14: the route change waits up to saveWaitBound - ten seconds - for the saves on the module shown,
    /// and showed nothing for any of it. The operator clicked another module and the page sat on the one they left,
    /// with no sign it had heard them. It says what it is waiting for now, and that message is replaced either by the
    /// next module's page or by the notice that the bound ran out.
    /// </summary>
    [Fact]
    public async Task AnotherModuleOpenedWhileASaveRuns_SaysWhatItIsWaitingFor_UntilItOpens()
    {
        var page = await AdminsWithAGroupAdded();
        var release = _h.GroupDirectory.HoldNext();
        var saving = Save(page);
        Reached(_h.GroupDirectory.Entered, "the save never asked the directory");

        var loading = await Open(page, Second);
        await Task.Delay(Short, Cancel);

        Assert.False(loading.IsCompleted, "the route change did not wait - this fixture is stale");
        var waiting = Status(page) ?? "";
        Assert.Contains("Waiting for", waiting, StringComparison.Ordinal);
        Assert.Contains(DisplayName(First), waiting, StringComparison.Ordinal);
        Assert.Contains(DisplayName(Second), waiting, StringComparison.Ordinal);
        Assert.False(Field<bool>(page, "statusIsError"));

        release.Set();
        await saving.WaitAsync(Long, Cancel);
        await loading.WaitAsync(Long, Cancel);

        // The module opened, so the waiting message is gone rather than left over its page.
        Assert.Equal(Second, Shown(page));
        Assert.DoesNotContain("Waiting for", Status(page) ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// cons-4: two loads can be in flight at once, and the one that resumes LAST used to win. Open B, route to C while
    /// B waits for its authorization read, let C finish, then let B resume: B applied its descriptor and its state over
    /// C, so a privileged module's configuration could be shown - and edited - after the route had moved to another.
    /// aud-a67 (9) and (10) closed the orderings where the NEWER work went astray; this is the one where the older
    /// load does. A load whose module the route no longer asks for now ends silently.
    /// </summary>
    [Fact]
    public async Task AnOlderLoadThatResumesAfterANewerOneFinished_AppliesNothing()
    {
        var page = _h.OpenModuleConfig(First);
        var authorizeSecond = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(authorizeSecond.Task, Task.FromResult(AuthorizationResult.Success()));

        // B is held at its authorization read.
        var loadingSecond = await Open(page, Second);
        await Task.Delay(Short, Cancel);
        Assert.False(loadingSecond.IsCompleted, "the second module's load did not wait at its authorization - this fixture is stale");

        // C is routed to and runs to completion while B is still held.
        var loadingThird = await Open(page, Third);
        await loadingThird.WaitAsync(Long, Cancel);
        Assert.Equal(Third, Shown(page));
        var tabAfterThird = Field<string>(page, "tab");

        // B resumes last, and must apply nothing.
        authorizeSecond.SetResult(AuthorizationResult.Success());
        await loadingSecond.WaitAsync(Long, Cancel);

        Assert.Equal(Third, Shown(page));
        Assert.Equal(Third, Field<string?>(page, "loadedModuleId"));
        Assert.Equal(tabAfterThird, Field<string>(page, "tab"));
        Assert.DoesNotContain("access-denied", _h.Navigation.Navigations);
    }

    /// <summary>
    /// cons-4's guard immediately before the state is applied, pinned in the source. Behaviour cannot pin it: the check
    /// after the authorization read already aborts a stale load, and nothing between the two awaits, so removing this
    /// one changes nothing today. It is there so that an await added above cannot reopen the window silently, and that
    /// is a property of the text, not of a run.
    /// </summary>
    [Fact]
    public void TheLoad_ChecksTheRouteHasNotMovedOn_ImmediatelyBeforeItAppliesTheModule()
    {
        var body = AuditCategoryFilingTests.MethodBody(
            ProgressScan.CodeView(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ModuleConfig.razor"))),
            "protected override async Task OnParametersSetAsync()");

        var applied = body.IndexOf("module = next;", StringComparison.Ordinal);
        Assert.True(applied > 0, "OnParametersSetAsync no longer assigns the module it loaded - this guard is stale");

        var before = body[..applied];
        // The call, not its whole argument list: cons-6 added the generation to it, and this guard is about WHERE the
        // check sits, not what it compares.
        var guarded = before.LastIndexOf("if (!StillRequested(", StringComparison.Ordinal);
        Assert.True(guarded > 0, "nothing checks the route before the module is applied");
        Assert.DoesNotContain("await ", before[guarded..], StringComparison.Ordinal);
    }

    /// <summary>The line of <paramref name="text"/> that <paramref name="index"/> falls on, for a failure that names it.</summary>
    private static string Line(string text, int index)
    {
        if (index <= 0 || index >= text.Length)
            return "(nowhere)";

        var start = text.LastIndexOf('\n', index) + 1;
        var end = text.IndexOf('\n', index);
        return text[start..(end < 0 ? text.Length : end)].Trim();
    }

    /// <summary>
    /// cons-6: route to A and suspend its load, route to B, route back to A and let the newer A load finish, edit
    /// something, then let the older A load resume. Its module id matches the route again, so identity alone let it
    /// through to LoadState(), which read the store afresh and dropped the edit - no save, no refusal, no
    /// unsaved-changes prompt. cons-4 closed A then B; only a generation closes A, B, A.
    /// </summary>
    [Fact]
    public async Task AnOlderLoadOfTheSameModule_DoesNotOverwriteTheNewerPage_NorDropItsEdits()
    {
        new ModuleAdminRepository(TestConfigStore.Create(_h.Root)).SetForModule(Second, [Kept]);
        var page = _h.OpenModuleConfig(First);
        var holdFirstA = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(holdFirstA.Task, Task.FromResult(AuthorizationResult.Success()));

        // A, suspended at its authorization read after it has claimed the page.
        var loadingFirstA = await Open(page, Second);
        await Task.Delay(Short, Cancel);
        Assert.False(loadingFirstA.IsCompleted, "the first load of A did not wait at its authorization - this fixture is stale");

        // B, then back to A: the newer A load runs to completion and is the page the operator is looking at.
        await (await Open(page, Third)).WaitAsync(Long, Cancel);
        await (await Open(page, Second)).WaitAsync(Long, Cancel);
        Assert.Equal(Second, Shown(page));

        // An edit on that newer A page.
        Set(page, "tab", "Module Admins");
        await _h.OnDispatcherAsync(() => Set(page, "newAdminGroup", Added));
        await _h.RunAsync(page, "AddModuleAdmin");
        var dirty = Field<AdminPageDirtyState>(page, "dirty");
        Assert.True(dirty.IsDirty, "the edit left nothing unsaved - this fixture is stale");

        // The older A load resumes last. Its module is the one on screen, so only the generation stops it.
        holdFirstA.SetResult(AuthorizationResult.Success());
        await loadingFirstA.WaitAsync(Long, Cancel);

        Assert.Equal(Second, Shown(page));
        Assert.Equal([Kept, Added], Field<List<string>>(page, "moduleAdminGroups"));
        Assert.True(dirty.IsDirty, "the older load of the same module reloaded the page and dropped the edit");
        Assert.Equal([Kept], StoredAdmins(Second));
    }

    /// <summary>
    /// cons-7: with a save in flight on A, route to B - the first B load takes a generation and waits in the save
    /// drain - and let the same B parameters arrive again before that wait ends, so a second B load takes a newer one.
    /// The older B continuation resumed first, claimed `loadedModuleId`, cleared `authChecked` and the status, and
    /// then failed its stale generation; the newer one saw its own module already claimed and returned at the no-op.
    /// Nothing reached `LoadState()`, so the route was B and the page showed nothing - and with `authChecked` false the
    /// unsaved-changes guard was not rendered either.
    /// </summary>
    /// <remarks>
    /// The older load resumes first because its wait was registered on the save first, and the dispatcher runs the two
    /// continuations in that order. The page as it was before this fix fails this case, and it can only do so in that
    /// order: the other way round, that page had the newer load apply B and the older one return at its same-module
    /// check.
    /// </remarks>
    [Fact]
    public async Task ASecondLoadOfTheSameModuleDuringTheSaveDrain_StillLeavesThePageShowingIt()
    {
        var page = await AdminsWithAGroupAdded();
        var release = _h.GroupDirectory.HoldNext();
        var saving = Save(page);
        Reached(_h.GroupDirectory.Entered, "the save never asked the directory");

        // Both loads are for B, and both are in the save drain before either can go on.
        var firstB = await Open(page, Second);
        await Task.Delay(Short, Cancel);
        Assert.False(firstB.IsCompleted, "the first load of B did not wait for the save - this fixture is stale");
        var secondB = _h.RunAsync(page, "OnParametersSetAsync");
        await Task.Delay(Short, Cancel);
        Assert.False(secondB.IsCompleted, "the duplicate load of B did not wait for the save - this fixture is stale");

        release.Set();
        await saving.WaitAsync(Long, Cancel);
        await firstB.WaitAsync(Long, Cancel);
        await secondB.WaitAsync(Long, Cancel);

        // One of the two applied B; neither may leave the page on nothing.
        Assert.Equal(Second, Shown(page));
        Assert.Equal(Second, Field<string?>(page, "loadedModuleId"));
        Assert.True(Field<bool>(page, "authChecked"), "the page was left mid-load, so it renders neither the module nor its unsaved-changes guard");
    }

    /// <summary>
    /// cons-7, in the same window: back to the module shown. A has a save in flight, the route moves to B - whose load
    /// waits in the save drain - and comes back to A before that wait ends. The return to A finds A on screen and does
    /// nothing, rightly; but the B load then came out of the drain and tore the page down - claimed B, cleared
    /// `authChecked` and the status - on its way to failing its check, so the route was A and the page was blank. A load
    /// the route has moved past ends at the drain now, before it touches anything.
    /// </summary>
    [Fact]
    public async Task ARouteBackToTheModuleShown_DuringTheSaveDrain_LeavesThatModuleOnThePage()
    {
        var page = await AdminsWithAGroupAdded();
        var release = _h.GroupDirectory.HoldNext();
        var saving = Save(page);
        Reached(_h.GroupDirectory.Entered, "the save never asked the directory");

        var toSecond = await Open(page, Second);
        await Task.Delay(Short, Cancel);
        Assert.False(toSecond.IsCompleted, "the load of B did not wait for the save - this fixture is stale");

        // Back to A while B's load is still in the drain.
        await (await Open(page, First)).WaitAsync(Long, Cancel);

        release.Set();
        await saving.WaitAsync(Long, Cancel);
        await toSecond.WaitAsync(Long, Cancel);

        Assert.True(Field<bool>(page, "authChecked"), "the page was left torn down, so it renders neither the module nor its unsaved-changes guard");
        Assert.Equal(First, Field<string?>(page, "loadedModuleId"));
        Assert.Equal(First, Shown(page));
        Assert.Equal("Module admins saved.", Status(page));
    }

    /// <summary>
    /// cons-7: a claim that outlived its load. B's load gets through the drain on its bound - the save on A still
    /// running - and waits at its authorization read; the route moves to C, whose load waits in the drain for the same
    /// save, and comes back to B. B's load had claimed the page as it left the drain, so the return to B found B
    /// "loaded" and did nothing; then B's load failed its check on generation and C's on identity, and nothing applied B.
    /// Only a load that settles the page claims it now, so the return to B is a load of its own.
    /// </summary>
    /// <remarks>
    /// The one order where the claim's placement alone decides the outcome: a stale load ending at the drain does not
    /// help, because B's load was still current when it left the drain.
    /// </remarks>
    [Fact]
    public async Task AReturnToAModuleWhoseLoadWasOvertaken_IsALoadOfItsOwn()
    {
        var page = await AdminsWithAGroupAdded();
        var release = _h.GroupDirectory.HoldNext();
        var saving = Save(page);
        Reached(_h.GroupDirectory.Entered, "the save never asked the directory");

        // B's load waits out the bound rather than the save, and is then held at its authorization read.
        var authorizing = new ManualResetEventSlim();
        var holdSecond = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(_ => { authorizing.Set(); return holdSecond.Task; }, _ => Task.FromResult(AuthorizationResult.Success()));
        Set(page, "saveWaitBound", Short);
        var toSecond = await Open(page, Second);
        Reached(authorizing, "the load of B never reached its authorization read");

        // C's load waits in the drain for the same save, for as long as the save runs.
        Set(page, "saveWaitBound", Long);
        var toThird = await Open(page, Third);
        await Task.Delay(Short, Cancel);
        Assert.False(toThird.IsCompleted, "the load of C did not wait for the save - this fixture is stale");

        // Back to B while C's load is still in the drain.
        var backToSecond = await Open(page, Second);

        holdSecond.SetResult(AuthorizationResult.Success());
        await toSecond.WaitAsync(Long, Cancel);
        release.Set();
        await saving.WaitAsync(Long, Cancel);
        await toThird.WaitAsync(Long, Cancel);
        await backToSecond.WaitAsync(Long, Cancel);

        Assert.Equal(Second, Shown(page));
        Assert.Equal(Second, Field<string?>(page, "loadedModuleId"));
        Assert.True(Field<bool>(page, "authChecked"), "the page was left torn down, so it renders neither the module nor its unsaved-changes guard");
    }

    /// <summary>
    /// cons-7: the strand the first draft of this fix introduced, held so it cannot come back. Route from A to B, hold
    /// B's load at its authorization read - by then it has torn the page down - and route back to A. With the claim
    /// moved to the apply but not cleared at the teardown, `loadedModuleId` still named A, so the return to A did
    /// nothing, B's load failed its check, and the page was blank. A page that has been torn down is settled on no
    /// module, so the return to A loads A.
    /// </summary>
    [Fact]
    public async Task AReturnToTheModuleShown_AfterAnotherModulesLoadClearedThePage_LoadsItAgain()
    {
        var page = _h.OpenModuleConfig(First);
        var holdSecond = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(holdSecond.Task, Task.FromResult(AuthorizationResult.Success()));

        var toSecond = await Open(page, Second);
        await Task.Delay(Short, Cancel);
        Assert.False(toSecond.IsCompleted, "the load of B did not wait at its authorization read - this fixture is stale");
        Assert.False(Field<bool>(page, "authChecked"), "the load of B had not torn the page down - this fixture is stale");

        await (await Open(page, First)).WaitAsync(Long, Cancel);
        holdSecond.SetResult(AuthorizationResult.Success());
        await toSecond.WaitAsync(Long, Cancel);

        Assert.True(Field<bool>(page, "authChecked"), "the page was left torn down, so it renders neither the module nor its unsaved-changes guard");
        Assert.Equal(First, Field<string?>(page, "loadedModuleId"));
        Assert.Equal(First, Shown(page));
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
