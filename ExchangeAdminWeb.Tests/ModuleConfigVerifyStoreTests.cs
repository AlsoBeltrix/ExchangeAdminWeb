using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md B.3, C.1, C.2 (slice V4): the config-pane Verify actions against a real SQLite file through
/// the real services - Remove one entry by the module's own parsing, the confirmation before the last AD Attribute Editor
/// search base goes, Ignore, the version check, the ignore prune on Remove and on the pane's ordinary Save, and the page's own
/// gate (a global admin or that module's admin) at every click - and the pane through the compiled page
/// (<see cref="SettingsPageHarness"/>). Nothing reaches a directory or Exchange.
/// </summary>
public sealed class ModuleConfigVerifyStoreTests : IDisposable
{
    private const string Store = VerifyIgnoreList.ConfigStore;
    private const string Gone = "OU=Gone,DC=contoso,DC=com";
    private const string Valid = "OU=Valid,DC=contoso,DC=com";

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private ModuleConfigVerifyActions Actions() => new(_h.ModuleConfig, _h.ModuleAdmins, _h.Authorization, _h.Audit, NullLogger.Instance);

    private ClaimsPrincipal Admin => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, _h.AdminName), new Claim(ClaimTypes.GroupSid, SettingsPageHarness.AdminSid)], "test"));

    private SqliteConfigStore Fresh() => TestConfigStore.Create(_h.Root);

    private void Seed(string module, string key, string value) =>
        _h.ModuleConfig.SaveModuleConfig(module, new Dictionary<string, string> { [key] = value, ["DelineaSecretId"] = "42" });

    private string? Stored(string module, string key) => new ModuleConfigRepository(Fresh()).GetModule(module).GetValueOrDefault(key);

    private void SeedIgnores(params VerifyIgnoreEntry[] entries) =>
        new AppSettingRepository(Fresh()).Set(VerifyIgnoreList.SettingKey, VerifyIgnoreList.Serialize(entries));

    private string? StoredIgnores() => new AppSettingRepository(Fresh()).Get(VerifyIgnoreList.SettingKey);

    private IReadOnlyList<VerifyIgnoreEntry> StoredIgnoreEntries()
    {
        Assert.True(VerifyIgnoreList.TryParse(StoredIgnores(), out var entries));
        return entries;
    }

    private static VerifyIgnoreEntry Ignore(string store, string rowKey, string entry) =>
        new(store, rowKey, entry, "Missing", VerifyIgnoreList.NotFoundObserved, @"EXAMPLE\admin", "2026-11-05T00:00:00.0000000Z");

    private ModuleConfigVerifyWorkset Workset(string module) =>
        Assert.IsType<ModuleConfigVerifyWorkset>(Actions().ReadForVerify(new ModuleConfigVerifyScope(module)));

    private static VerifyItem ItemOf(ModuleConfigVerifyWorkset workset, string key, string entry) =>
        Assert.Single(workset.Items, i => string.Equals(i.RowKey, key, StringComparison.OrdinalIgnoreCase) && i.StoredValue == entry);

    private Task<ModuleConfigVerifyActionResult> RemoveAsync(ModuleConfigVerifyWorkset workset, string key, string entry, bool confirmed = false,
        VerifyFinding? finding = null) =>
        Actions().RemoveAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, ItemOf(workset, key, entry), finding ?? VerifyFinding.Missing(),
            workset.IgnoresRaw, workset.Snapshot.Values.GetValueOrDefault(key), confirmed);

    private IReadOnlyList<JsonElement> VerifyRecords(string store = Store) =>
        _h.AuditEvents().Where(e => e.TryGetProperty("section", out var s) && s.GetString() == ModuleGroupVerifyActions.SectionOf(store)).ToList();

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.ToString() : "";

    // ----- C.2 Remove: one entry, by the module's own parsing -----

    [Fact]
    public async Task RemovingOneMissingSearchBase_FromMissingAndValid_LeavesTheValidOne()
    {
        Seed("ADAttributeEditor", "DefaultSearchBase", $"{Gone};{Valid}");

        var result = await RemoveAsync(Workset("ADAttributeEditor"), "DefaultSearchBase", Gone);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Valid, Stored("ADAttributeEditor", "DefaultSearchBase"));
        Assert.Equal("42", Stored("ADAttributeEditor", "DelineaSecretId"));
        var record = Assert.Single(VerifyRecords());
        Assert.Equal(("Success", "Remove", "Missing"), (Text(record, "result"), Text(record, "action"), Text(record, "finding")));
    }

    [Fact]
    public async Task RemovingTheLastSearchBase_NeedsTheConfirmation_CarryingTheWarning_AndWritesNothingUntilConfirmed()
    {
        Seed("ADAttributeEditor", "DefaultSearchBase", $" {Gone} ;");
        var workset = Workset("ADAttributeEditor");

        var unconfirmed = await RemoveAsync(workset, "DefaultSearchBase", Gone);

        Assert.False(unconfirmed.Succeeded);
        Assert.True(unconfirmed.NeedsConfirmation);
        Assert.Contains("no search-base boundary will remain: the editor may then search the whole directory", unconfirmed.Message, StringComparison.Ordinal);
        Assert.Equal($" {Gone} ;", Stored("ADAttributeEditor", "DefaultSearchBase"));
        Assert.Empty(VerifyRecords());

        var confirmed = await RemoveAsync(workset, "DefaultSearchBase", Gone, confirmed: true);

        Assert.True(confirmed.Succeeded, confirmed.Message);
        Assert.Equal("", Stored("ADAttributeEditor", "DefaultSearchBase"));
    }

    [Fact]
    public async Task TheLastSearchBase_IsNotRemovedUnconfirmed_EvenByAWriteThatSkipsThePageCheck()
    {
        Seed("ADAttributeEditor", "DefaultSearchBase", Gone);
        var workset = Workset("ADAttributeEditor");

        var outcome = _h.ModuleConfig.ApplyVerifyWrite("ADAttributeEditor", r => r.RemoveVerifyEntry(
            "ADAttributeEditor", "DefaultSearchBase", Gone, lastEntryConfirmed: false, workset.Version, workset.IgnoresRaw));

        Assert.NotEqual(VerifyWriteStatus.Written, outcome.Status);
        Assert.Equal(Gone, Stored("ADAttributeEditor", "DefaultSearchBase"));
    }

    [Fact]
    public async Task RemovingAnExcludedGroup_TakesExactlyTheOneCommaEntry_KeepingTheOthersAsStored()
    {
        Seed("Migration", "ExcludedADGroups", "Sales , Nobody ,Staff,Nobody2");

        var result = await RemoveAsync(Workset("Migration"), "ExcludedADGroups", "Nobody");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Sales ,Staff,Nobody2", Stored("Migration", "ExcludedADGroups"));
    }

    [Theory]
    [InlineData("EmergencyDisable", "LockdownOuDn", Gone)]
    [InlineData("BitLockerRecovery", "ActiveDirectorySearchBase", Gone)]
    [InlineData("ConferenceRooms", "ConfCEOGroup", "ceo-room@contoso.com")]
    public async Task RemovingASingleValue_ClearsIt_WithoutAConfirmation(string module, string key, string value)
    {
        Seed(module, key, value);

        var result = await RemoveAsync(Workset(module), key, value);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.NeedsConfirmation);
        Assert.Equal("", Stored(module, key));
    }

    // ----- the version check -----

    [Fact]
    public async Task EveryDirectWrite_RefusesWhenTheSettingsChangedSinceVerifyRead_AndAuditsTheRefusal()
    {
        Seed("ADAttributeEditor", "DefaultSearchBase", $"{Gone};{Valid}");
        var workset = Workset("ADAttributeEditor");
        _h.ModuleConfig.SaveChangedValues("ADAttributeEditor",
            new Dictionary<string, string> { ["DefaultSearchBase"] = $"{Gone};{Valid}" },
            new Dictionary<string, string> { ["DefaultSearchBase"] = $"{Gone};{Valid};OU=New,DC=contoso,DC=com" },
            TestContext.Current.CancellationToken);

        var removed = await RemoveAsync(workset, "DefaultSearchBase", Gone);
        var ignored = await Actions().IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope,
            ItemOf(workset, "DefaultSearchBase", Gone), VerifyFinding.Missing(), workset.IgnoresRaw);

        Assert.False(removed.Succeeded);
        Assert.False(ignored.Succeeded);
        Assert.Equal(ProtectedPrincipalVerifyActions.ChangedSinceVerify, removed.Message);
        Assert.Equal($"{Gone};{Valid};OU=New,DC=contoso,DC=com", Stored("ADAttributeEditor", "DefaultSearchBase"));
        Assert.Null(StoredIgnores());
        Assert.All(VerifyRecords(), r => Assert.Equal(("Failed", "VersionChanged"), (Text(r, "result"), Text(r, "refusedAt"))));
        Assert.Equal(2, VerifyRecords().Count);
    }

    // ----- B.3: ignores pruned atomically with the entry, and by the pane's ordinary Save -----

    [Fact]
    public async Task Remove_TakesTheEntrysIgnores_AndKeepsEveryOtherIgnore()
    {
        Seed("ADAttributeEditor", "DefaultSearchBase", $"{Gone};{Valid}");
        Seed("EmergencyDisable", "LockdownOuDn", Gone);
        SeedIgnores(Ignore(Store, "DefaultSearchBase", Gone), Ignore(Store, "DefaultSearchBase", Valid), Ignore(Store, "LockdownOuDn", Gone),
            Ignore(VerifyIgnoreList.SectionAccessStore, "DefaultSearchBase", Gone));

        var result = await RemoveAsync(Workset("ADAttributeEditor"), "DefaultSearchBase", Gone);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal([(Store, "DefaultSearchBase", Valid), (Store, "LockdownOuDn", Gone), (VerifyIgnoreList.SectionAccessStore, "DefaultSearchBase", Gone)],
            StoredIgnoreEntries().Select(e => (e.Store, e.RowKey, e.IdentifierOrText)));
    }

    [Fact]
    public async Task ThePanesOrdinarySave_WithTheEntryGone_DeletesItsIgnores()
    {
        Seed("ADAttributeEditor", "DefaultSearchBase", $"{Gone};{Valid}");
        SeedIgnores(Ignore(Store, "DefaultSearchBase", Gone), Ignore(Store, "DefaultSearchBase", Valid));
        var page = _h.OpenModuleConfig("ADAttributeEditor", _h.CreateAttributeEditor());

        await _h.RunAsync(page, "SetConfigValue", "DefaultSearchBase", Valid);
        await _h.RunAsync(page, "SaveConfigAsync");

        Assert.Equal(Valid, Stored("ADAttributeEditor", "DefaultSearchBase"));
        Assert.Equal([Valid], StoredIgnoreEntries().Select(e => e.IdentifierOrText));
    }

    [Fact]
    public async Task Ignore_RecordsTheFinding_ChangesNoSetting_AndIsRefusedForAnEntryThatIsGone()
    {
        Seed("Migration", "ExcludedADGroups", "Sales,Nobody");
        var workset = Workset("Migration");
        var item = ItemOf(workset, "ExcludedADGroups", "Nobody");

        var ignored = await Actions().IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, item, VerifyFinding.Missing(), workset.IgnoresRaw);

        Assert.True(ignored.Succeeded, ignored.Message);
        Assert.Equal("Sales,Nobody", Stored("Migration", "ExcludedADGroups"));
        var entry = Assert.Single(StoredIgnoreEntries());
        Assert.Equal((Store, "ExcludedADGroups", "Nobody", "Missing"), (entry.Store, entry.RowKey, entry.IdentifierOrText, entry.Finding));

        var notOffered = await Actions().IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, item,
            VerifyFinding.Unreadable("busy"), workset.IgnoresRaw);
        Assert.False(notOffered.Succeeded);
        Assert.Equal("NotOffered", Text(VerifyRecords().Last(), "refusedAt"));
    }

    // ----- R3: the page's own gate - a global admin, or this module's admin - at every click -----

    [Theory]
    [InlineData("Migration", "ExcludedADGroups", "Nobody")]
    [InlineData("EmergencyDisable", "LockdownOuDn", Gone)]
    [InlineData("ADAttributeEditor", "DefaultSearchBase", Gone)]
    [InlineData("BitLockerRecovery", "ActiveDirectorySearchBase", Gone)]
    [InlineData("ConferenceRooms", "DefaultArbiterGroup", "room-admins@contoso.com")]
    public async Task AModuleAdminWhoIsNotAGlobalAdmin_MayActOnTheConfigPane_AndIsRefusedAndAuditedOnTheGroupTabs(string module, string key, string value)
    {
        Seed(module, key, key == "DefaultSearchBase" ? $"{value};{Valid}" : value);
        _h.ModuleAdmins.SetModuleAdmins(module, [SettingsPageHarness.AdminSid]);
        _h.AdminAllowed = false;
        var workset = Workset(module);
        var item = ItemOf(workset, key, value);
        var actions = Actions();

        Assert.True(await actions.AuthorizePressAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope));
        Assert.True((await actions.AuthorizeEditAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, item, VerifyFinding.Missing())).Succeeded);
        var ignored = await actions.IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, item, VerifyFinding.Missing(), workset.IgnoresRaw);
        Assert.True(ignored.Succeeded, ignored.Message);
        var fresh = Workset(module);
        var removed = await RemoveAsync(fresh, key, value);
        Assert.True(removed.Succeeded, removed.Message);
        Assert.DoesNotContain(value, Stored(module, key) ?? "", StringComparison.Ordinal);

        // The same principal on the Access and Module Admins tabs (A3, A4): global admins only, refused and audited (V3's rule).
        var groups = new ModuleGroupVerifyActions(_h.SectionAccess, _h.ModuleAdmins, _h.Authorization, _h.Audit, NullLogger.Instance);
        foreach (var store in new[] { VerifyIgnoreList.SectionAccessStore, VerifyIgnoreList.ModuleAdminsStore })
        {
            Assert.False(await groups.AuthorizePressAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, new ModuleGroupVerifyScope(store, module, [module])));
            var refusal = Assert.Single(VerifyRecords(store));
            Assert.Equal(("Failed", "Authorization"), (Text(refusal, "result"), Text(refusal, "refusedAt")));
        }
    }

    [Fact]
    public async Task SomeoneWhoIsNeitherAGlobalAdminNorThisModulesAdmin_IsRefusedEveryAction_WritesNothing_AndIsAudited()
    {
        Seed("EmergencyDisable", "LockdownOuDn", Gone);
        _h.ModuleAdmins.SetModuleAdmins("Migration", [SettingsPageHarness.AdminSid]); // another module's admin
        _h.AdminAllowed = false;
        var workset = Workset("EmergencyDisable");
        var item = ItemOf(workset, "LockdownOuDn", Gone);
        var actions = Actions();

        Assert.False(await actions.AuthorizePressAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope));
        Assert.False((await actions.AuthorizeEditAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, item, VerifyFinding.Missing())).Succeeded);
        Assert.False((await actions.IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, workset.Scope, item, VerifyFinding.Missing(), workset.IgnoresRaw)).Succeeded);
        Assert.False((await RemoveAsync(workset, "LockdownOuDn", Gone)).Succeeded);

        Assert.Equal(Gone, Stored("EmergencyDisable", "LockdownOuDn"));
        Assert.Null(StoredIgnores());
        Assert.Equal(4, VerifyRecords().Count);
        Assert.All(VerifyRecords(), r => Assert.Equal(("Failed", "Authorization", "False"), (Text(r, "result"), Text(r, "refusedAt"), Text(r, "mutationAttempted"))));
    }

    // ----- the page -----

    [Fact]
    public async Task AModuleAdmin_IsOfferedVerifyOnTheConfigPane_AndAPressIsAuditedAndStoresTheLastVerifiedLine()
    {
        Seed("ConferenceRooms", "DefaultArbiterGroup", "room-admins@contoso.com");
        _h.ModuleAdmins.SetModuleAdmins("ConferenceRooms", [SettingsPageHarness.AdminSid]);
        _h.AdminAllowed = false;
        var page = _h.OpenModuleConfig("ConferenceRooms");

        Assert.False(SettingsPageHarness.IsButtonDisabled(page, "Verify"));
        await _h.RunAsync(page, "VerifyConfigAsync");

        // The harness's engine reaches no Exchange: the address could not be verified - never missing.
        var report = SettingsPageHarness.Field<VerifyReport?>(page, typeof(ModuleConfigPage), "configVerifyReport");
        Assert.Equal(VerifyFindingKind.Unreadable, Assert.Single(Assert.IsType<VerifyReport>(report).Results).Finding.Kind);
        var press = Assert.Single(VerifyRecords());
        Assert.Equal(("Success", "Verify"), (Text(press, "result"), Text(press, "action")));
        Assert.True(_h.ModuleConfig.ReadVerifyLast().ContainsKey("Config:ConferenceRooms"));
        Assert.Contains("Each group address is looked up in Exchange", SettingsPageHarness.RenderedText(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLastSearchBase_OnThePage_AsksForTheConfirmation_WithTheWarning_ThenRemovesIt()
    {
        Seed("ADAttributeEditor", "DefaultSearchBase", Gone);
        var page = _h.OpenModuleConfig("ADAttributeEditor", _h.CreateAttributeEditor());
        var workset = Workset("ADAttributeEditor");
        var acted = new VerifyItemResult(ItemOf(workset, "DefaultSearchBase", Gone), VerifyFinding.Missing());
        SettingsPageHarness.SetField(page, typeof(ModuleConfigPage), "configVerifyReport", new VerifyReport(DateTimeOffset.UtcNow, [acted], []));
        SettingsPageHarness.SetField(page, typeof(ModuleConfigPage), "configVerifyValues", workset.Snapshot.Values);
        SettingsPageHarness.SetField(page, typeof(ModuleConfigPage), "configVerifyIgnoresRaw", workset.IgnoresRaw);

        await _h.RunAsync(page, "OnConfigVerifyActionAsync", VerifyAction.Remove, acted);

        Assert.Equal(Gone, Stored("ADAttributeEditor", "DefaultSearchBase"));
        Assert.Contains(ModuleConfigVerifyActions.LastEntryWarning, SettingsPageHarness.RenderedText(page), StringComparison.Ordinal);
        Assert.Contains("Remove the last search base", SettingsPageHarness.ButtonLabels(page));

        await _h.RunAsync(page, "ConfirmConfigVerifyRemoveAsync");

        Assert.Equal("", Stored("ADAttributeEditor", "DefaultSearchBase"));
        Assert.Null(SettingsPageHarness.Field<VerifyItemResult?>(page, typeof(ModuleConfigPage), "configVerifyPendingRemove"));
    }

    [Fact]
    public async Task Edit_OnTheLockdownOu_OpensTheOuBrowserForThatSetting_WhichPicksOneOu()
    {
        Seed("EmergencyDisable", "LockdownOuDn", Gone);
        _h.OrganizationalUnits.Entries = [new OUEntry("A", "OU=A,DC=contoso,DC=com", 1), new OUEntry("B", "OU=B,DC=contoso,DC=com", 1)];
        var page = _h.OpenModuleConfig("EmergencyDisable");
        var workset = Workset("EmergencyDisable");
        var acted = new VerifyItemResult(ItemOf(workset, "LockdownOuDn", Gone), VerifyFinding.Missing());
        SettingsPageHarness.SetField(page, typeof(ModuleConfigPage), "configVerifyReport", new VerifyReport(DateTimeOffset.UtcNow, [acted], []));

        await _h.RunAsync(page, "OnConfigVerifyActionAsync", VerifyAction.Replace, acted);

        Assert.True(SettingsPageHarness.Field<bool>(page, typeof(ModuleConfigPage), "showOUBrowser"));
        Assert.Equal("LockdownOuDn", SettingsPageHarness.Field<string>(page, typeof(ModuleConfigPage), "ouBrowserKey"));
        await _h.RunAsync(page, "ToggleOU", "OU=A,DC=contoso,DC=com", true);
        await _h.RunAsync(page, "ToggleOU", "OU=B,DC=contoso,DC=com", true);
        await _h.RunAsync(page, "ApplySelectedOUs");
        await _h.RunAsync(page, "SaveConfigAsync");
        Assert.Equal("OU=B,DC=contoso,DC=com", Stored("EmergencyDisable", "LockdownOuDn"));
    }

    // ----- plan D: the run-book's post-deploy check -----

    [Fact]
    public void TheRunBook_SaysToPressVerifyOnEachPage_AfterTheDeploy_NamingEveryPane()
    {
        var text = File.ReadAllText(Path.Combine(Path.GetDirectoryName(AuditCategoryFilingTests.FindRepoFile("ExchangeAdminWeb.csproj"))!,
            "docs", "ConfigDbOperations.md"));
        var press = text.IndexOf("press Verify on each page", StringComparison.Ordinal);

        Assert.True(press > text.IndexOf("db identifier-status $db", StringComparison.Ordinal), "the Verify step comes after db identifier-status");
        var step = text[press..text.IndexOf("\n\n", press, StringComparison.Ordinal)];
        foreach (var pane in new[] { "Protected principals", "Access", "Module Admins", "Migration", "Emergency Disable", "AD\nAttribute Editor", "BitLocker Recovery", "Conference Rooms" })
            Assert.Contains(pane, step, StringComparison.Ordinal);
    }
}
