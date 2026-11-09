using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md B.3, B.4, C.1, C.2 (slice V3): Module Configuration's Verify actions on the Access tab
/// (access and Protection servicer groups, A3) and the Module Admins tab (A4) - against a real SQLite file through the real
/// services, and the tabs' ordinary Remove + Save and Replace through the compiled page (<see cref="SettingsPageHarness"/>).
/// Nothing reaches a directory.
/// </summary>
public sealed class ModuleGroupVerifyStoreTests : IDisposable
{
    private const string Module = "BlockedSenders";
    private const string Access = VerifyIgnoreList.SectionAccessStore;
    private const string Admins = VerifyIgnoreList.ModuleAdminsStore;
    private static string Sid(int rid) => $"S-1-5-21-1000000000-1000000000-1000000000-{rid}";

    // One row as it appears in the serialized row list: quoted, so a row is never matched inside a longer one.
    private static string Q(string row) => $"\"{row}\"";

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string MainAlias => _h.Catalog.GetById(Module)!.MainPermission.PolicyAlias;
    private static string Servicer => ProtectedPrincipalServicerService.SectionKeyFor(Module);

    private IReadOnlyList<string> Aliases =>
        [.. new[] { MainAlias }.Concat(_h.Catalog.GetById(Module)!.GranularPermissions.Select(p => p.PolicyAlias)), Servicer];

    private ModuleGroupVerifyScope Scope(string store) => new(store, Module, store == Access ? Aliases : []);

    private ModuleGroupVerifyActions Actions() => new(_h.SectionAccess, _h.ModuleAdmins, _h.Authorization, _h.Audit, NullLogger.Instance);

    private ClaimsPrincipal Admin => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, _h.AdminName), new Claim(ClaimTypes.GroupSid, SettingsPageHarness.AdminSid)], "test"));

    private SqliteConfigStore Fresh() => TestConfigStore.Create(_h.Root);

    private void SeedAccess(Dictionary<string, string[]> access, Dictionary<string, string>? names = null)
    {
        _h.SectionAccess.SaveSectionAccess(access);
        if (names is not null)
            new SectionAccessRepository(Fresh()).SaveDisplayNames(names);
    }

    private void SeedIgnores(params VerifyIgnoreEntry[] entries) =>
        new AppSettingRepository(Fresh()).Set(VerifyIgnoreList.SettingKey, VerifyIgnoreList.Serialize(entries));

    private string? StoredIgnores() => new AppSettingRepository(Fresh()).Get(VerifyIgnoreList.SettingKey);

    private IReadOnlyList<VerifyIgnoreEntry> StoredIgnoreEntries()
    {
        Assert.True(VerifyIgnoreList.TryParse(StoredIgnores(), out var entries));
        return entries;
    }

    private static VerifyIgnoreEntry Ignore(string store, string rowKey, string id, string finding = "Missing", string observed = VerifyIgnoreList.NotFoundObserved) =>
        new(store, rowKey, id, finding, observed, @"EXAMPLE\admin", "2026-11-05T00:00:00.0000000Z");

    private string AccessRows() => JsonSerializer.Serialize(new SectionAccessRepository(Fresh()).GetAllRows()
        .Select(r => $"{r.PolicyAlias}:{r.GroupValue}").Order(StringComparer.Ordinal));

    private Dictionary<string, string> Names() => new SectionAccessRepository(Fresh()).GetDisplayNames();

    private string AdminRows() => JsonSerializer.Serialize(new ModuleAdminRepository(Fresh()).GetAll()
        .SelectMany(kv => kv.Value.Select(v => $"{kv.Key}:{v}")).Order(StringComparer.Ordinal));

    private ModuleGroupVerifyWorkset Workset(string store) => Assert.IsType<ModuleGroupVerifyWorkset>(Actions().ReadForVerify(Scope(store)));

    private static VerifyItem ItemOf(ModuleGroupVerifyWorkset workset, string rowKey, string value) =>
        Assert.Single(workset.Items, i => string.Equals(i.RowKey, rowKey, StringComparison.OrdinalIgnoreCase) && i.StoredValue == value);

    private IReadOnlyList<JsonElement> VerifyRecords(string store) =>
        _h.AuditEvents().Where(e => e.TryGetProperty("section", out var s) && s.GetString() == ModuleGroupVerifyActions.SectionOf(store)).ToList();

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.ToString() : "";

    // ----- C.2 Rename: only that SID's display name -----

    [Fact]
    public async Task AccessRename_WritesOnlyThatSidsDisplayName_AndNoGrant()
    {
        SeedAccess(new() { [MainAlias] = [Sid(1), Sid(2)], [Servicer] = [Sid(1)] },
            new() { [Sid(1)] = @"CONTOSO\Old", [Sid(2)] = @"CONTOSO\Two" });
        var rows = AccessRows();
        var workset = Workset(Access);

        var result = await Actions().RenameAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope,
            ItemOf(workset, MainAlias, Sid(1)), VerifyFinding.LabelDrift(@"CONTOSO\New"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(rows, AccessRows());
        Assert.Equal(new Dictionary<string, string> { [Sid(1)] = @"CONTOSO\New", [Sid(2)] = @"CONTOSO\Two" }, Names());
        Assert.Null(StoredIgnores());
        var record = Assert.Single(VerifyRecords(Access));
        Assert.Equal(("Success", "Rename", "LabelDrift"), (Text(record, "result"), Text(record, "action"), Text(record, "finding")));
    }

    [Fact]
    public async Task ModuleAdminRename_WritesOnlyThatSidsBorrowedDisplayName_AndNoGrant()
    {
        _h.ModuleAdmins.SetModuleAdmins(Module, [Sid(1), Sid(2)]);
        SeedAccess(new() { [MainAlias] = [Sid(1), Sid(2)] }, new() { [Sid(1)] = "Old", [Sid(2)] = "Two" });
        var admins = AdminRows();
        var access = AccessRows();
        var workset = Workset(Admins);

        var result = await Actions().RenameAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope,
            ItemOf(workset, Module, Sid(1)), VerifyFinding.LabelDrift("New"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal((admins, access), (AdminRows(), AccessRows()));
        Assert.Equal(new Dictionary<string, string> { [Sid(1)] = "New", [Sid(2)] = "Two" }, Names());
    }

    // ----- C.2 Convert: exactly that alias and value (A3), that module's grant (A4) -----

    [Fact]
    public async Task AccessConvert_PersistsThroughConvertToSids_ForExactlyThatAliasAndValue()
    {
        SeedAccess(new() { [MainAlias] = [@"CONTOSO\Ops", Sid(2)], [Servicer] = [@"CONTOSO\Ops"], ["SomeOtherModule"] = [@"CONTOSO\Ops"] });
        var workset = Workset(Access);
        var finding = VerifyFinding.NotConverted(new VerifyResolution(
            VerifyResolutionKind.Convertible, "converts", ProtectedPrincipalRepository.KindGroupSid, Sid(5), @"CONTOSO\Ops"));

        var result = await Actions().ConvertAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope,
            ItemOf(workset, MainAlias, @"CONTOSO\Ops"), finding);

        Assert.True(result.Succeeded, result.Message);
        var expected = new[] { $"{MainAlias}:{Sid(2)}", $"{MainAlias}:{Sid(5)}", $"{Servicer}:CONTOSO\\Ops", @"SomeOtherModule:CONTOSO\Ops" }
            .Order(StringComparer.Ordinal);
        Assert.Equal(JsonSerializer.Serialize(expected), AccessRows());
        Assert.Equal(@"CONTOSO\Ops", Names()[Sid(5)]);
    }

    [Fact]
    public async Task ModuleAdminConvert_PersistsThroughConvertModuleAdminGrant_ForThatModuleOnly()
    {
        _h.ModuleAdmins.SetModuleAdmins(Module, ["Ops", Sid(2)]);
        _h.ModuleAdmins.SetModuleAdmins("MfaReset", ["Ops"]);
        var workset = Workset(Admins);
        var finding = VerifyFinding.NotConverted(new VerifyResolution(
            VerifyResolutionKind.Convertible, "converts", ProtectedPrincipalRepository.KindGroupSid, Sid(5)));

        var result = await Actions().ConvertAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, ItemOf(workset, Module, "Ops"), finding);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(JsonSerializer.Serialize(new[] { $"{Module}:{Sid(2)}", $"{Module}:{Sid(5)}", "MfaReset:Ops" }.Order(StringComparer.Ordinal)), AdminRows());
    }

    // ----- B.3 Remove: the grant and its ignores in one transaction; a stale list removes nothing -----

    [Theory]
    [InlineData(Access)]
    [InlineData(Admins)]
    public async Task Remove_TakesTheGrantAndItsIgnores_InOneTransaction_AndAStaleListRemovesNothing(string store)
    {
        var rowKey = store == Access ? MainAlias : Module;
        if (store == Access)
            SeedAccess(new() { [MainAlias] = [Sid(1), Sid(2)], [Servicer] = [Sid(1)] });
        else
            _h.ModuleAdmins.SetModuleAdmins(Module, [Sid(1), Sid(2)]);
        var otherKey = store == Access ? Servicer : "MfaReset";
        if (store == Admins)
            _h.ModuleAdmins.SetModuleAdmins("MfaReset", [Sid(1)]);
        SeedIgnores(Ignore(store, rowKey, Sid(1)), Ignore(store, rowKey, Sid(2)), Ignore(store, otherKey, Sid(1)),
            Ignore(VerifyIgnoreList.ProtectedPrincipalsStore, "group_sid", Sid(1)));
        var workset = Workset(store);
        var item = ItemOf(workset, rowKey, Sid(1));
        var rowsBefore = store == Access ? AccessRows() : AdminRows();
        // Another admin ignores something after this list was read: the write must not land over it.
        SeedIgnores(Ignore(store, rowKey, Sid(1)), Ignore(store, rowKey, Sid(2)), Ignore(store, otherKey, Sid(1)),
            Ignore(VerifyIgnoreList.ProtectedPrincipalsStore, "group_sid", Sid(1)), Ignore(store, rowKey, Sid(2), "LabelDrift", "X"));

        var stale = store == Access
            ? _h.SectionAccess.ApplyVerifyWrite(r => r.RemoveWithIgnores(Aliases, new GroupGrantRow(rowKey, Sid(1)), item.SnapshotVersion, workset.IgnoresRaw))
            : _h.ModuleAdmins.ApplyVerifyWrite(r => r.RemoveWithIgnores(Module, Sid(1), item.SnapshotVersion, workset.IgnoresRaw));

        Assert.Equal(VerifyWriteStatus.IgnoresChanged, stale.Status);
        Assert.Equal(rowsBefore, store == Access ? AccessRows() : AdminRows());
        Assert.Equal(5, StoredIgnoreEntries().Count);

        // Through the action: the list is read again and the write tried once more.
        var result = await Actions().RemoveAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, item, VerifyFinding.Missing(), workset.IgnoresRaw);

        Assert.True(result.Succeeded, result.Message);
        Assert.DoesNotContain(Q($"{rowKey}:{Sid(1)}"), store == Access ? AccessRows() : AdminRows());
        var left = StoredIgnoreEntries();
        Assert.DoesNotContain(left, e => e.Store == store && e.RowKey == rowKey && e.IdentifierOrText == Sid(1));
        Assert.Equal(4, left.Count);
    }

    // ----- C.2: every direct primitive checks the version the item was read with -----

    [Theory]
    [InlineData(Access, "Rename")]
    [InlineData(Access, "Convert")]
    [InlineData(Access, "Remove")]
    [InlineData(Access, "Ignore")]
    [InlineData(Admins, "Rename")]
    [InlineData(Admins, "Convert")]
    [InlineData(Admins, "Remove")]
    [InlineData(Admins, "Ignore")]
    public async Task EveryDirectPrimitive_RefusesWhenTheGrantsAreNoLongerTheVersionVerifyRead_AndWritesNothing(string store, string action)
    {
        var rowKey = store == Access ? MainAlias : Module;
        SeedAccess(new() { [MainAlias] = [Sid(1), "Ops"] }, new() { [Sid(1)] = "Old" });
        _h.ModuleAdmins.SetModuleAdmins(Module, [Sid(1), "Ops"]);
        var workset = Workset(store);
        // The other instance adds a grant to the same tab: the row each action names is untouched, but the tab is not as read.
        if (store == Access)
            SeedAccess(new() { [MainAlias] = [Sid(1), "Ops"], [Servicer] = [Sid(9)] });
        else
            _h.ModuleAdmins.SetModuleAdmins(Module, [Sid(1), "Ops", Sid(9)]);
        var before = (AccessRows(), AdminRows(), JsonSerializer.Serialize(Names()));
        var token = Fresh().GetChangeToken();
        var actions = Actions();
        var sid = ItemOf(workset, rowKey, Sid(1));
        var byName = ItemOf(workset, rowKey, "Ops");
        var convertible = VerifyFinding.NotConverted(new VerifyResolution(VerifyResolutionKind.Convertible, "c", "group_sid", Sid(5), "Ops"));

        var result = action switch
        {
            "Rename" => await actions.RenameAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, sid, VerifyFinding.LabelDrift("New")),
            "Convert" => await actions.ConvertAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, byName, convertible),
            "Remove" => await actions.RemoveAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, sid, VerifyFinding.Missing(), workset.IgnoresRaw),
            _ => await actions.IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, sid, VerifyFinding.Missing(), workset.IgnoresRaw),
        };

        Assert.False(result.Succeeded);
        Assert.Equal(ProtectedPrincipalVerifyActions.ChangedSinceVerify, result.Message);
        Assert.Equal(before, (AccessRows(), AdminRows(), JsonSerializer.Serialize(Names())));
        Assert.Null(StoredIgnores());
        Assert.Equal(token, Fresh().GetChangeToken());
        var record = Assert.Single(VerifyRecords(store));
        Assert.Equal(("Failed", "VersionChanged"), (Text(record, "result"), Text(record, "refusedAt")));
    }

    [Theory]
    [InlineData(Access)]
    [InlineData(Admins)]
    public async Task AnIgnoreAfterTheGrantWasRemoved_IsRefused_AndRecordsNothing(string store)
    {
        var rowKey = store == Access ? MainAlias : Module;
        SeedAccess(new() { [MainAlias] = [Sid(1)] });
        _h.ModuleAdmins.SetModuleAdmins(Module, [Sid(1)]);
        var workset = Workset(store);
        if (store == Access)
            SeedAccess(new() { [MainAlias] = [] });
        else
            _h.ModuleAdmins.SetModuleAdmins(Module, []);

        var result = await Actions().IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope,
            ItemOf(workset, rowKey, Sid(1)), VerifyFinding.Missing(), workset.IgnoresRaw);

        Assert.False(result.Succeeded);
        Assert.Null(StoredIgnores());
    }

    // ----- C.2 (R3): global admin at every click; a module admin is refused and audited -----

    [Theory]
    [InlineData(Access, "Press")]
    [InlineData(Access, "Rename")]
    [InlineData(Access, "Convert")]
    [InlineData(Access, "Replace")]
    [InlineData(Access, "Remove")]
    [InlineData(Access, "Ignore")]
    [InlineData(Admins, "Press")]
    [InlineData(Admins, "Rename")]
    [InlineData(Admins, "Convert")]
    [InlineData(Admins, "Replace")]
    [InlineData(Admins, "Remove")]
    [InlineData(Admins, "Ignore")]
    public async Task AModuleAdminWhoIsNotAGlobalAdmin_IsRefusedEveryVerifyAction_WritesNothing_AndIsAuditedAsARefusal(string store, string action)
    {
        var rowKey = store == Access ? MainAlias : Module;
        SeedAccess(new() { [MainAlias] = [Sid(1), "Ops"], [Servicer] = [Sid(1)] }, new() { [Sid(1)] = "Old" });
        // The clicking admin is this module's module admin - and not a global admin.
        _h.ModuleAdmins.SetModuleAdmins(Module, [SettingsPageHarness.AdminSid, Sid(1), "Ops"]);
        Assert.True(_h.ModuleAdmins.IsModuleAdmin(Module, Admin));
        var workset = Workset(store);
        _h.AdminAllowed = false;
        var before = (AccessRows(), AdminRows(), JsonSerializer.Serialize(Names()));
        var actions = Actions();
        var sid = ItemOf(workset, rowKey, Sid(1));
        var convertible = VerifyFinding.NotConverted(new VerifyResolution(VerifyResolutionKind.Convertible, "c", "group_sid", Sid(5), "Ops"));

        var succeeded = action switch
        {
            "Press" => await actions.AuthorizePressAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope),
            "Rename" => (await actions.RenameAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, sid, VerifyFinding.LabelDrift("New"))).Succeeded,
            "Convert" => (await actions.ConvertAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, ItemOf(workset, rowKey, "Ops"), convertible)).Succeeded,
            "Replace" => (await actions.AuthorizeReplaceAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, sid, VerifyFinding.Missing())).Succeeded,
            "Remove" => (await actions.RemoveAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, sid, VerifyFinding.Missing(), workset.IgnoresRaw)).Succeeded,
            _ => (await actions.IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, sid, VerifyFinding.Missing(), workset.IgnoresRaw)).Succeeded,
        };

        Assert.False(succeeded);
        Assert.Equal(before, (AccessRows(), AdminRows(), JsonSerializer.Serialize(Names())));
        Assert.Null(StoredIgnores());
        var record = Assert.Single(VerifyRecords(store));
        Assert.Equal(("Failed", "Authorization", "False"), (Text(record, "result"), Text(record, "refusedAt"), Text(record, "mutationAttempted")));
    }

    [Theory]
    [InlineData(Access)]
    [InlineData(Admins)]
    public async Task AGlobalAdminsSuccess_AndARefusal_LandInDifferentAuditRows(string store)
    {
        var rowKey = store == Access ? MainAlias : Module;
        SeedAccess(new() { [MainAlias] = [Sid(1), Sid(2)] }, new() { [Sid(2)] = "Old" });
        _h.ModuleAdmins.SetModuleAdmins(Module, [Sid(1), Sid(2)]);
        var workset = Workset(store);
        var actions = Actions();

        var landed = await actions.IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope,
            ItemOf(workset, rowKey, Sid(1)), VerifyFinding.Missing(), workset.IgnoresRaw);
        _h.AdminAllowed = false;
        var refused = await actions.RenameAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope,
            ItemOf(workset, rowKey, Sid(2)), VerifyFinding.LabelDrift("New"));

        Assert.True(landed.Succeeded, landed.Message);
        Assert.False(refused.Succeeded);
        Assert.Equal([Ignore(store, rowKey, Sid(1)) with { By = _h.AdminName, At = StoredIgnoreEntries()[0].At }], StoredIgnoreEntries());
        var records = VerifyRecords(store);
        Assert.Equal(2, records.Count);
        Assert.Equal(("Success", "Ignore", "Missing", Module), (Text(records[0], "result"), Text(records[0], "action"), Text(records[0], "finding"), Text(records[0], "module")));
        Assert.False(records[0].TryGetProperty("refusedAt", out _));
        Assert.Equal(("Failed", "Authorization"), (Text(records[1], "result"), Text(records[1], "refusedAt")));
    }

    [Theory]
    [InlineData(Access)]
    [InlineData(Admins)]
    public async Task AnIgnoreOfAGrantStoredByName_IsRefused_EvenWhenAskedForDirectly(string store)
    {
        var rowKey = store == Access ? MainAlias : Module;
        SeedAccess(new() { [MainAlias] = ["Ops"] });
        _h.ModuleAdmins.SetModuleAdmins(Module, ["Ops"]);
        var workset = Workset(store);

        var result = await Actions().IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, ItemOf(workset, rowKey, "Ops"),
            VerifyFinding.NotConverted(new VerifyResolution(VerifyResolutionKind.None, "not found")), workset.IgnoresRaw);

        Assert.False(result.Succeeded);
        Assert.Null(StoredIgnores());
        Assert.Equal("NotOffered", Text(Assert.Single(VerifyRecords(store)), "refusedAt"));
    }

    // ----- B.4: an ignore on these stores changes no authorization -----

    [Fact]
    public async Task AnIgnoreOnTheseStores_ChangesNoAuthorization_AccessServicerOrModuleAdmin()
    {
        const string alias = "VerifyIgnoreProbe";
        SeedAccess(new() { [alias] = [SettingsPageHarness.AdminSid], [Servicer] = [SettingsPageHarness.AdminSid] });
        _h.ModuleAdmins.SetModuleAdmins(Module, [SettingsPageHarness.AdminSid]);
        var handler = new GroupAuthorizationHandler(NullLogger<GroupAuthorizationHandler>.Instance, _h.SectionAccess, _h.Catalog, _h.Enablement);
        var servicer = new ProtectedPrincipalServicerService(_h.SectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance);
        // Group SIDs as role claims too, so the servicer's IsInRole sees them as the Windows principal would.
        ClaimsPrincipal Principal(string name, string sid) => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.GroupSid, sid)], "test", ClaimTypes.Name, ClaimTypes.GroupSid));
        var member = Principal(_h.AdminName, SettingsPageHarness.AdminSid);
        var outsider = Principal("EXAMPLE\\outsider", Sid(77));

        async Task<(bool Access, bool Servicer, bool ModuleAdmin)> Decide(ClaimsPrincipal user)
        {
            var requirement = new GroupAuthorizationRequirement(alias, dynamic: true);
            var context = new AuthorizationHandlerContext([requirement], user, null);
            await handler.HandleAsync(context);
            return (context.HasSucceeded, servicer.Evaluate(user, Module).Allowed, _h.ModuleAdmins.IsModuleAdmin(Module, user));
        }

        var withoutIgnores = (await Decide(member), await Decide(outsider));
        Assert.Equal(((true, true, true), (false, false, false)), withoutIgnores);

        SeedIgnores(
            Ignore(Access, alias, SettingsPageHarness.AdminSid), Ignore(Access, Servicer, SettingsPageHarness.AdminSid),
            Ignore(Admins, Module, SettingsPageHarness.AdminSid), Ignore(Access, alias, SettingsPageHarness.AdminSid, "WrongClass", "user"),
            Ignore(Admins, Module, SettingsPageHarness.AdminSid, "LabelDrift", "X"));

        Assert.Equal(withoutIgnores, (await Decide(member), await Decide(outsider)));
    }

    // ----- the tabs' ordinary Remove + Save, and Replace through the picker and Save, take the grant's ignores -----

    [Fact]
    public async Task TheAccessTabsOrdinaryRemoveAndSave_DeletesTheRemovedGrantsIgnoreEntries()
    {
        SeedAccess(new() { [MainAlias] = [Sid(1), Sid(2)], [Servicer] = [Sid(1)] });
        SeedIgnores(Ignore(Access, MainAlias, Sid(1)), Ignore(Access, MainAlias, Sid(2)), Ignore(Access, Servicer, Sid(1)));
        var page = _h.OpenModuleConfig(Module);

        await _h.RunAsync(page, "RemoveAccessGroup", MainAlias, Sid(1));
        await _h.RunAsync(page, "SaveSectionAccessAsync");

        Assert.DoesNotContain(Q($"{MainAlias}:{Sid(1)}"), AccessRows());
        Assert.Equal([(MainAlias, Sid(2)), (Servicer, Sid(1))], StoredIgnoreEntries().Select(e => (e.RowKey, e.IdentifierOrText)));
    }

    [Fact]
    public async Task TheModuleAdminsTabsOrdinaryRemoveAndSave_DeletesTheRemovedGrantsIgnoreEntries()
    {
        _h.ModuleAdmins.SetModuleAdmins(Module, [Sid(1), Sid(2)]);
        SeedIgnores(Ignore(Admins, Module, Sid(1)), Ignore(Admins, Module, Sid(2)), Ignore(Access, Module, Sid(1)));
        var page = _h.OpenModuleConfig(Module);

        await _h.RunAsync(page, "RemoveModuleAdmin", Sid(1));
        await _h.RunAsync(page, "SaveModuleAdminsAsync");

        Assert.Equal(JsonSerializer.Serialize(new[] { $"{Module}:{Sid(2)}" }), AdminRows());
        Assert.Equal([(Admins, Sid(2)), (Access, Sid(1))], StoredIgnoreEntries().Select(e => (e.Store, e.IdentifierOrText)));
    }

    [Theory]
    [InlineData(Access)]
    [InlineData(Admins)]
    public async Task Replace_TakesTheGrantOutOfTheList_AndThePickAndSaveReplaceIt_WithItsIgnores(string store)
    {
        var rowKey = store == Access ? MainAlias : Module;
        SeedAccess(new() { [MainAlias] = [Sid(1), Sid(2)] });
        _h.ModuleAdmins.SetModuleAdmins(Module, [Sid(1), Sid(2)]);
        SeedIgnores(Ignore(store, rowKey, Sid(1)), Ignore(store, rowKey, Sid(2)));
        var page = _h.OpenModuleConfig(Module);
        var workset = Workset(store);
        var acted = new VerifyItemResult(ItemOf(workset, rowKey, Sid(1)), VerifyFinding.Missing());
        SettingsPageHarness.SetField(page, typeof(ModuleConfigPage), store == Access ? "accessVerifyReport" : "adminsVerifyReport",
            new VerifyReport(DateTimeOffset.UtcNow, [acted], []));
        _h.GroupDirectory.IsAvailable = true;
        _h.GroupDirectory.Answer = SettingsPageHarness.StandInDirectory.Group;

        await _h.RunAsync(page, "OnGroupVerifyActionAsync", VerifyAction.Replace, acted);

        // Nothing written yet: the grant left the list on the page only.
        Assert.Contains(Q($"{rowKey}:{Sid(1)}"), store == Access ? AccessRows() : AdminRows());
        Assert.Equal(2, StoredIgnoreEntries().Count);
        if (store == Access)
        {
            SettingsPageHarness.Field<Dictionary<string, string>>(page, typeof(ModuleConfigPage), "accessInputs")[MainAlias] = Sid(3);
            await _h.RunAsync(page, "AddAccessGroup", MainAlias);
            await _h.RunAsync(page, "SaveSectionAccessAsync");
        }
        else
        {
            SettingsPageHarness.SetField(page, typeof(ModuleConfigPage), "newAdminGroup", Sid(3));
            await _h.RunAsync(page, "AddModuleAdmin");
            await _h.RunAsync(page, "SaveModuleAdminsAsync");
        }

        var rows = store == Access ? AccessRows() : AdminRows();
        Assert.DoesNotContain(Q($"{rowKey}:{Sid(1)}"), rows);
        Assert.Contains(Q($"{rowKey}:{Sid(3)}"), rows);
        Assert.Equal([Sid(2)], StoredIgnoreEntries().Select(e => e.IdentifierOrText));
    }

    // ----- C.1: the page -----

    [Fact]
    public async Task APress_WithNoDirectoryReadCredential_MarksEverySidGrantCouldNotVerify_AndRecordsOnePress()
    {
        SeedAccess(new() { [MainAlias] = [Sid(1)], [Servicer] = [Sid(2)] });
        var page = _h.OpenModuleConfig(Module);

        await _h.RunAsync(page, "VerifyGroupsAsync", Access);

        var report = Assert.IsType<VerifyReport>(SettingsPageHarness.Field<VerifyReport?>(page, typeof(ModuleConfigPage), "accessVerifyReport"));
        Assert.Equal(2, report.Total);
        Assert.All(report.Results, r => Assert.Equal(VerifyFindingKind.Unreadable, r.Finding.Kind));
        Assert.Contains("could not verify", SettingsPageHarness.RenderedText(page));
        var press = Assert.Single(VerifyRecords(Access));
        Assert.Equal(("Success", "Verify", "2"), (Text(press, "result"), Text(press, "action"), Text(press, "unreadable")));
        Assert.Contains($"{Access}:{Module}", new AppSettingRepository(Fresh()).Get(VerifyIgnoreList.LastVerifiedKey));
    }

    [Fact]
    public async Task Verify_IsNotOffered_WhileTheTabHasUnsavedChanges_AndAPressThenVerifiesNothing()
    {
        SeedAccess(new() { [MainAlias] = [Sid(1), Sid(2)] });
        var page = _h.OpenModuleConfig(Module);
        await _h.RunAsync(page, "RemoveAccessGroup", MainAlias, Sid(1));

        Assert.False(CanVerify(page, Access));
        Assert.True(CanVerify(page, Admins));
        await _h.RunAsync(page, "VerifyGroupsAsync", Access);

        Assert.Null(SettingsPageHarness.Field<VerifyReport?>(page, typeof(ModuleConfigPage), "accessVerifyReport"));
        Assert.Empty(VerifyRecords(Access));
    }

    [Fact]
    public async Task AModuleAdmin_IsNotOfferedVerify_AndADirectPressIsRefusedAndAudited()
    {
        _h.ModuleAdmins.SetModuleAdmins(Module, [SettingsPageHarness.AdminSid]);
        _h.AdminAllowed = false;
        var page = _h.OpenModuleConfig(Module);

        Assert.False(CanVerify(page, Admins));
        Assert.False(CanVerify(page, Access));
        await _h.RunAsync(page, "VerifyGroupsAsync", Admins);

        Assert.Null(SettingsPageHarness.Field<VerifyReport?>(page, typeof(ModuleConfigPage), "adminsVerifyReport"));
        Assert.Equal("Authorization", Text(Assert.Single(VerifyRecords(Admins)), "refusedAt"));
    }

    [Fact]
    public void EachFinding_ShowsItsBadgeAndHelp_AndOnlyTheActionsItOffers_NeverIgnoreForAGrantStoredByName()
    {
        SeedAccess(new() { [MainAlias] = [Sid(1), "Ops"] });
        var page = _h.OpenModuleConfig(Module);
        var workset = Workset(Access);
        var report = new VerifyReport(DateTimeOffset.UtcNow,
        [
            new VerifyItemResult(ItemOf(workset, MainAlias, Sid(1)), VerifyFinding.Missing()),
            new VerifyItemResult(ItemOf(workset, MainAlias, "Ops"), VerifyFinding.NotConverted(new VerifyResolution(
                VerifyResolutionKind.Convertible, $"converts to {Sid(5)}", "group_sid", Sid(5), @"CONTOSO\Ops"))),
        ], []);
        SettingsPageHarness.SetField(page, typeof(ModuleConfigPage), "accessVerifyReport", report);

        var text = SettingsPageHarness.RenderedText(page);
        var buttons = SettingsPageHarness.ButtonLabels(page);

        Assert.Contains("missing", text);
        Assert.Contains("not converted", text);
        Assert.Contains("matches no one until it is converted", text);
        Assert.Equal(1, buttons.Count(b => b == "Replace..."));
        Assert.Equal(1, buttons.Count(b => b == "Convert"));
        Assert.Equal(2, buttons.Count(b => b == "Remove now"));
        Assert.Equal(1, buttons.Count(b => b == "Ignore"));
        Assert.Contains("An ignore here changes only what Verify reports, never who is granted anything", text);
    }

    [Fact]
    public void EveryBadgeAndAction_CarriesItsHelp_AndThePaneNotesStayShort()
    {
        var item = new VerifyItem(Access, "BlockedSenders", VerifyObjectKind.Group, Sid(1), @"CONTOSO\Old", Sid(1), "v");
        foreach (var finding in new[]
                 {
                     VerifyFinding.Verified(), VerifyFinding.Missing(), VerifyFinding.LabelDrift(@"CONTOSO\New"),
                     VerifyFinding.NotConverted(new VerifyResolution(VerifyResolutionKind.None, "not found")),
                     VerifyFinding.Ambiguous("CN=A; CN=B"), VerifyFinding.WrongClass("user"), VerifyFinding.Unreadable("busy"),
                     VerifyFinding.Ignored(VerifyFinding.Missing(), Ignore(Access, "BlockedSenders", Sid(1))),
                 })
        {
            var (badge, css, tip) = ModuleConfigPage.GroupVerifyBadgeParts(new VerifyItemResult(item, finding), "2026-11-05 10:00");
            Assert.False(string.IsNullOrWhiteSpace(badge), finding.Kind.ToString());
            Assert.False(string.IsNullOrWhiteSpace(css), finding.Kind.ToString());
            Assert.True(tip.Length > 40, $"{finding.Kind} has no real hover help");
        }

        foreach (var action in Enum.GetValues<VerifyAction>())
            Assert.Contains("audited", ModuleConfigPage.GroupVerifyActionTip(action, new VerifyItemResult(item, VerifyFinding.LabelDrift("X"))), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never who is granted anything", ModuleConfigPage.GroupVerifyActionTip(VerifyAction.Ignore, new VerifyItemResult(item, VerifyFinding.Missing())));
        Assert.Contains("Reads only", ModuleConfigPage.AccessVerifyButtonTip);
        Assert.Contains("Reads only", ModuleConfigPage.AdminsVerifyButtonTip);
        Assert.True(ModuleConfigPage.AccessVerifyPaneNote.Length < 350, "the Access tab's Verify note is over the help limit");
        Assert.True(ModuleConfigPage.AdminsVerifyPaneNote.Length < 350, "the Admins tab's Verify note is over the help limit");
    }

    private static bool CanVerify(ModuleConfigPage page, string store) =>
        (bool)typeof(ModuleConfigPage).GetMethod("CanVerifyGroups", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [store])!;
}
