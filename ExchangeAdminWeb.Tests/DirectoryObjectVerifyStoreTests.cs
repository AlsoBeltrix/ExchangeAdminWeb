using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md B.3, C.2 (slice V1): the ignore list and its concurrency, the direct write primitives,
/// and the actions' authorization and audit - against a real SQLite file through the real services; the page's ordinary
/// Remove + Save through the compiled page (<see cref="SettingsPageHarness"/>). Nothing reaches a directory.
/// </summary>
public sealed class DirectoryObjectVerifyStoreTests : IDisposable
{
    private const string Section = ProtectedPrincipalVerifyActions.Section;
    private const string Store = VerifyIgnoreList.ProtectedPrincipalsStore;
    private static readonly string GroupSid = "S-1-5-21-1000000000-1000000000-1000000000-2001";
    private static readonly string OtherSid = "S-1-5-21-1000000000-1000000000-1000000000-2002";
    private const string GroupDn = "CN=Tier0 Admins,OU=Groups,DC=contoso,DC=com";
    private const string OtherDn = "CN=Ops,OU=Groups,DC=contoso,DC=com";
    private static string GroupRow => ProtectedPrincipalIdEntry.Format(GroupSid, GroupDn);
    private static string OtherRow => ProtectedPrincipalIdEntry.Format(OtherSid, OtherDn);

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private SqliteConfigStore Fresh() => TestConfigStore.Create(_h.Root);

    private void Seed(ProtectedPrincipalData rules) => new ProtectedPrincipalRepository(Fresh()).Save(rules);

    private static ProtectedPrincipalData TwoGroups => ProtectedPrincipalData.Empty with
    {
        Groups = [GroupDn, OtherDn],
        GroupIds = [GroupRow, OtherRow],
    };

    private static VerifyIgnoreEntry Ignore(string sid, string finding = "Missing", string observed = VerifyIgnoreList.NotFoundObserved) =>
        new(Store, ProtectedPrincipalRepository.KindGroupSid, sid, finding, observed, @"EXAMPLE\admin", "2026-11-04T00:00:00.0000000Z");

    private void SeedIgnores(params VerifyIgnoreEntry[] entries) =>
        new AppSettingRepository(Fresh()).Set(VerifyIgnoreList.SettingKey, VerifyIgnoreList.Serialize(entries));

    private string? StoredIgnores() => new AppSettingRepository(Fresh()).Get(VerifyIgnoreList.SettingKey);

    private IReadOnlyList<VerifyIgnoreEntry> StoredIgnoreEntries()
    {
        Assert.True(VerifyIgnoreList.TryParse(StoredIgnores(), out var entries));
        return entries;
    }

    private ProtectedPrincipalData StoredRules()
    {
        Assert.True(new ProtectedPrincipalRepository(Fresh()).TryRead(out var data, out _));
        return data;
    }

    private ProtectedPrincipalVerifyWorkset Workset() => Assert.IsType<ProtectedPrincipalVerifyWorkset>(_h.ProtectedPrincipals.ReadForVerify());

    private static VerifyItem ItemOf(ProtectedPrincipalVerifyWorkset workset, string kind, string value) =>
        Assert.Single(workset.Items, i => i.RowKey == kind && i.StoredValue == value);

    private ProtectedPrincipalVerifyActions Actions(ProtectedPrincipalService? service = null) =>
        new(service ?? _h.ProtectedPrincipals, _h.Authorization, _h.Audit, NullLogger.Instance);

    private ClaimsPrincipal Admin => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, _h.AdminName)], "test"));

    private IReadOnlyList<JsonElement> VerifyRecords() =>
        _h.AuditEvents().Where(e => e.TryGetProperty("section", out var s) && s.GetString() == Section).ToList();

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.ToString() : "";

    // ----- B.3: compare-and-swap -----

    [Fact]
    public void ReplaceIfEqual_RefusesAStaleExpectedValue_AndWritesNothing()
    {
        SeedIgnores(Ignore(GroupSid));
        var stale = StoredIgnores();
        SeedIgnores(Ignore(GroupSid), Ignore(OtherSid));
        var current = StoredIgnores();

        var swapped = Fresh().Write((connection, transaction) =>
            AppSettingRepository.ReplaceIfEqual(connection, transaction, VerifyIgnoreList.SettingKey, stale, "[]"));

        Assert.False(swapped);
        Assert.Equal(current, StoredIgnores());
        Assert.True(Fresh().Write((connection, transaction) =>
            AppSettingRepository.ReplaceIfEqual(connection, transaction, VerifyIgnoreList.SettingKey, current, "[]")));
        Assert.Equal("[]", StoredIgnores());
    }

    [Fact]
    public void Remove_TakesTheRowItsCompanionAndItsIgnores_InOneTransaction_AndAStaleListRemovesNothing()
    {
        Seed(TwoGroups);
        SeedIgnores(Ignore(GroupSid), Ignore(OtherSid));
        var workset = Workset();
        var item = ItemOf(workset, "group_sid", GroupRow);
        // Another admin ignores something after this list was read: the remove must not write over it.
        SeedIgnores(Ignore(GroupSid), Ignore(OtherSid), Ignore(OtherSid, "LabelDrift", "CN=X"));
        var rulesBefore = StoredRules();

        var stale = _h.ProtectedPrincipals.ApplyVerifyWrite(r =>
            r.RemoveWithCompanionAndIgnores(new VerifyRowRef(item.RowKey, item.StoredValue), item.SnapshotVersion, workset.IgnoresRaw));

        Assert.Equal(VerifyWriteStatus.IgnoresChanged, stale.Status);
        Assert.Equal(JsonSerializer.Serialize(rulesBefore), JsonSerializer.Serialize(StoredRules()));
        Assert.Equal(3, StoredIgnoreEntries().Count);

        var written = _h.ProtectedPrincipals.ApplyVerifyWrite(r =>
            r.RemoveWithCompanionAndIgnores(new VerifyRowRef(item.RowKey, item.StoredValue), item.SnapshotVersion, StoredIgnores()));

        Assert.Equal(VerifyWriteStatus.Written, written.Status);
        var rules = StoredRules();
        Assert.Equal([OtherRow], rules.GroupIds);
        Assert.Equal([OtherDn], rules.Groups);
        Assert.All(StoredIgnoreEntries(), e => Assert.Equal(OtherSid, e.IdentifierOrText));
        Assert.Equal(2, StoredIgnoreEntries().Count);
    }

    // ----- C.2: every direct primitive checks the version the item was read with -----

    [Theory]
    [InlineData("Rename")]
    [InlineData("Convert")]
    [InlineData("Remove")]
    [InlineData("Replace")]
    [InlineData("Ignore")]
    public void EveryDirectPrimitive_RefusesWhenTheRulesAreNoLongerTheVersionItWasReadWith(string action)
    {
        Seed(TwoGroups with { Users = ["alice@contoso.com"] });
        var workset = Workset();
        // The other instance adds a pattern: the row each action names is untouched, but the rules are not what was read.
        Seed(TwoGroups with { Users = ["alice@contoso.com"], SamAccountNamePatterns = ["svc-*"] });
        var before = JsonSerializer.Serialize(StoredRules());
        var token = Fresh().GetChangeToken();
        var row = new VerifyRowRef("group_sid", GroupRow);
        var v = workset.Version;

        var outcome = _h.ProtectedPrincipals.ApplyVerifyWrite(r => action switch
        {
            "Rename" => r.RewriteLabel("group_sid", GroupSid, "CN=Renamed,OU=Groups,DC=contoso,DC=com", v),
            "Convert" => r.ConvertRow(new ProtectedPrincipalIdConversion("user", "alice@contoso.com", "user_sid", "S-1-5-21-1-2-3-1105"), v),
            "Remove" => r.RemoveWithCompanionAndIgnores(row, v, workset.IgnoresRaw),
            "Replace" => r.ReplaceWithCompanionAndIgnores(row, ProtectedPrincipalIdEntry.Format(OtherSid, OtherDn), OtherDn, v, workset.IgnoresRaw),
            _ => r.AddVerifyIgnore(row, Ignore(GroupSid), v, workset.IgnoresRaw),
        });

        Assert.Equal(VerifyWriteStatus.VersionChanged, outcome.Status);
        Assert.Equal(before, JsonSerializer.Serialize(StoredRules()));
        Assert.Null(StoredIgnores());
        Assert.Equal(token, Fresh().GetChangeToken());
    }

    [Fact]
    public void Rename_RewritesTheIdentifierRowsLabel_AndItsCompanion_InOneWrite()
    {
        Seed(TwoGroups);
        var workset = Workset();
        const string renamed = "CN=Tier0 Administrators,OU=Groups,DC=contoso,DC=com";

        var outcome = _h.ProtectedPrincipals.ApplyVerifyWrite(r => r.RewriteLabel("group_sid", GroupSid, renamed, workset.Version));

        Assert.Equal(VerifyWriteStatus.Written, outcome.Status);
        var rules = StoredRules();
        Assert.Equal([ProtectedPrincipalIdEntry.Format(GroupSid, renamed), OtherRow], rules.GroupIds.Order(StringComparer.Ordinal));
        Assert.Equal([OtherDn, renamed], rules.Groups.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Convert_WritesTheIdentifierRowBesideTheLegacyRow_ThroughTheVerbsOwnPrimitive()
    {
        Seed(ProtectedPrincipalData.Empty with { Users = ["alice@contoso.com", "bob@contoso.com"] });
        var workset = Workset();
        const string aliceSid = "S-1-5-21-1000000000-1000000000-1000000000-1105";

        var outcome = _h.ProtectedPrincipals.ApplyVerifyWrite(r =>
            r.ConvertRow(new ProtectedPrincipalIdConversion("user", "alice@contoso.com", "user_sid", aliceSid), workset.Version));

        Assert.Equal(VerifyWriteStatus.Written, outcome.Status);
        var rules = StoredRules();
        Assert.Equal([ProtectedPrincipalIdEntry.Format(aliceSid, "alice@contoso.com")], rules.UserIds);
        Assert.Equal(["alice@contoso.com", "bob@contoso.com"], rules.Users.Order(StringComparer.Ordinal));
        // A row already converted is not converted again.
        var again = _h.ProtectedPrincipals.ApplyVerifyWrite(r =>
            r.ConvertRow(new ProtectedPrincipalIdConversion("user", "alice@contoso.com", "user_sid", aliceSid), outcome.AfterVersion!));
        Assert.Equal(VerifyWriteStatus.RowChanged, again.Status);
    }

    // ----- B.3 (R2): an Ignore is checked against the row it ignores -----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnIgnoreAfterTheRowWasRemovedOrReplaced_IsRefused_AndRecordsNothingIgnored(bool replaced)
    {
        Seed(TwoGroups);
        var workset = Workset();
        var item = ItemOf(workset, "group_sid", GroupRow);
        // The other instance's admin removes the row, or picks another group in its place, after Verify ran here.
        Seed(replaced
            ? TwoGroups with { GroupIds = [ProtectedPrincipalIdEntry.Format("S-1-5-21-1000000000-1000000000-1000000000-2009", GroupDn), OtherRow] }
            : ProtectedPrincipalData.Empty with { Groups = [OtherDn], GroupIds = [OtherRow] });

        var result = await Actions().IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, item, VerifyFinding.Missing(), workset.IgnoresRaw);

        Assert.False(result.Succeeded);
        Assert.Equal(ProtectedPrincipalVerifyActions.ChangedSinceVerify, result.Message);
        Assert.Null(StoredIgnores());
        var record = Assert.Single(VerifyRecords());
        Assert.Equal(("Failed", "VersionChanged"), (Text(record, "result"), Text(record, "refusedAt")));
    }

    [Fact]
    public async Task AnIgnoreWhoseListChanged_IsRetriedOnce_AndTheRetryRefusesWhenTheRowWentInBetween()
    {
        Seed(TwoGroups);
        var hook = new HookedStore(Fresh());
        var service = new ProtectedPrincipalService(_h.Env, _h.Config, _h.ModuleConfig, new ProtectedPrincipalRepository(hook), _h.Delinea,
            NullLogger<ProtectedPrincipalService>.Instance);
        var workset = Assert.IsType<ProtectedPrincipalVerifyWorkset>(service.ReadForVerify());
        var item = ItemOf(workset, "group_sid", GroupRow);
        // After the read: another admin ignores something else, so the first attempt's compare-and-swap misses ...
        SeedIgnores(Ignore(OtherSid));
        // ... and as the retry re-reads the list, the other instance removes the row.
        hook.BeforeNextRead = () => Seed(ProtectedPrincipalData.Empty with { Groups = [OtherDn], GroupIds = [OtherRow] });

        var result = await Actions(service).IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, item, VerifyFinding.Missing(), workset.IgnoresRaw);

        Assert.True(hook.Fired, "the retry did not re-read the list");
        Assert.False(result.Succeeded);
        Assert.Equal(ProtectedPrincipalVerifyActions.ChangedSinceVerify, result.Message);
        Assert.Equal([Ignore(OtherSid)], StoredIgnoreEntries());
    }

    [Fact]
    public async Task AnIgnoreWhoseListChanged_IsRetriedOnce_AndLandsBesideTheOtherAdminsEntry()
    {
        Seed(TwoGroups);
        var workset = Workset();
        var item = ItemOf(workset, "group_sid", GroupRow);
        SeedIgnores(Ignore(OtherSid));

        var result = await Actions().IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, item, VerifyFinding.Missing(), workset.IgnoresRaw);

        Assert.True(result.Succeeded, result.Message);
        var entries = StoredIgnoreEntries();
        Assert.Equal([OtherSid, GroupSid], entries.Select(e => e.IdentifierOrText));
        Assert.Equal(("Missing", VerifyIgnoreList.NotFoundObserved, _h.AdminName), (entries[1].Finding, entries[1].Observed, entries[1].By));
    }

    // ----- C.2 (R3): authorization at every click; success and refusal audited apart -----

    [Fact]
    public async Task ARevokedAuthorization_AtTheClick_Refuses_WritesNothing_AndIsAuditedAsARefusal()
    {
        Seed(TwoGroups);
        var workset = Workset();
        var item = ItemOf(workset, "group_sid", GroupRow);
        _h.AdminAllowed = false;
        var before = JsonSerializer.Serialize(StoredRules());

        var result = await Actions().RemoveAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, item, VerifyFinding.Missing(), workset.IgnoresRaw);

        Assert.False(result.Succeeded);
        Assert.Equal(before, JsonSerializer.Serialize(StoredRules()));
        var record = Assert.Single(VerifyRecords());
        Assert.Equal(("Failed", "Authorization", "False"), (Text(record, "result"), Text(record, "refusedAt"), Text(record, "mutationAttempted")));
    }

    [Fact]
    public async Task ASuccessAndARefusal_LandInDifferentAuditRows()
    {
        Seed(TwoGroups);
        var workset = Workset();
        var group = ItemOf(workset, "group_sid", GroupRow);
        var other = ItemOf(workset, "group_sid", OtherRow);
        var actions = Actions();

        var landed = await actions.IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, group, VerifyFinding.Missing(), workset.IgnoresRaw);
        // A rule changes after the press: the next action from it is refused.
        Seed(TwoGroups with { SamAccountNamePatterns = ["svc-*"] });
        var refused = await actions.RenameAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, other, VerifyFinding.LabelDrift("CN=Ops Team,OU=Groups,DC=contoso,DC=com"));

        Assert.True(landed.Succeeded, landed.Message);
        Assert.False(refused.Succeeded);
        var records = VerifyRecords();
        Assert.Equal(2, records.Count);
        Assert.Equal(("Success", "Ignore", "Missing"), (Text(records[0], "result"), Text(records[0], "action"), Text(records[0], "finding")));
        Assert.False(records[0].TryGetProperty("refusedAt", out _));
        Assert.Equal(("Failed", "VersionChanged"), (Text(records[1], "result"), Text(records[1], "refusedAt")));
    }

    [Fact]
    public async Task AnIgnoreOfARowStoredByName_IsRefused_EvenWhenAskedForDirectly()
    {
        Seed(ProtectedPrincipalData.Empty with { Users = ["alice@contoso.com"] });
        var workset = Workset();
        var item = ItemOf(workset, "user", "alice@contoso.com");
        var finding = VerifyFinding.NotConverted(new VerifyResolution(VerifyResolutionKind.None, "not found"));

        var result = await Actions().IgnoreAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, item, finding, workset.IgnoresRaw);

        Assert.False(result.Succeeded);
        Assert.Null(StoredIgnores());
        Assert.Equal("NotOffered", Text(Assert.Single(VerifyRecords()), "refusedAt"));
    }

    [Fact]
    public async Task AReplace_PutsThePickedObjectInTheRowsPlace_AndTakesTheOldRowsIgnores()
    {
        Seed(TwoGroups);
        SeedIgnores(Ignore(GroupSid), Ignore(OtherSid));
        var workset = Workset();
        var item = ItemOf(workset, "group_sid", GroupRow);
        const string newSid = "S-1-5-21-1000000000-1000000000-1000000000-2003";
        const string newDn = "CN=Tier0 Admins New,OU=Groups,DC=contoso,DC=com";

        var result = await Actions().ReplaceAsync(Admin, _h.AdminName, SettingsPageHarness.Ip, item, VerifyFinding.Missing(),
            ProtectedPrincipalIdEntry.Format(newSid, newDn), newDn, workset.IgnoresRaw);

        Assert.True(result.Succeeded, result.Message);
        var rules = StoredRules();
        Assert.Equal([OtherRow, ProtectedPrincipalIdEntry.Format(newSid, newDn)], rules.GroupIds.Order(StringComparer.Ordinal));
        Assert.Equal([OtherDn, newDn], rules.Groups.Order(StringComparer.Ordinal));
        Assert.Equal([OtherSid], StoredIgnoreEntries().Select(e => e.IdentifierOrText));
        // The other finding of the same press is kept at the write's version, so it can still be acted on.
        var report = new VerifyReport(DateTimeOffset.UtcNow,
            workset.Items.Select(i => new VerifyItemResult(i, VerifyFinding.Missing())).ToList(), []);
        var acted = report.Results.Single(r => r.Item.StoredValue == GroupRow);
        var rebased = ProtectedPrincipalVerifyActions.Rebase(report, acted, result.Outcome!);
        var kept = Assert.Single(rebased.Results);
        Assert.Equal((OtherRow, result.Outcome!.AfterVersion), (kept.Item.StoredValue, kept.Item.SnapshotVersion));
    }

    // ----- B.3: the enforcement load reads the list with the rules -----

    [Fact]
    public void TheEnforcementLoad_ReadsTheIgnoreListWithTheRules_AndACorruptListIgnoresNothing()
    {
        Seed(TwoGroups);
        SeedIgnores(Ignore(GroupSid));

        var (config, ignores, error) = _h.ProtectedPrincipals.LoadEffectiveConfigAndIgnores();

        Assert.Null(error);
        Assert.Equal([GroupRow, OtherRow], config!.GroupIds);
        Assert.Equal([Ignore(GroupSid)], ignores);

        new AppSettingRepository(Fresh()).Set(VerifyIgnoreList.SettingKey, "[{\"store\": \"ProtectedPrincipals\"");
        _h.ProtectedPrincipals.InvalidateCache();
        var (_, corrupt, corruptError) = _h.ProtectedPrincipals.LoadEffectiveConfigAndIgnores();

        Assert.Null(corruptError);
        Assert.Empty(corrupt);
    }

    [Fact]
    public void IdentifierStatus_PrintsTheIgnoreList_AtTheEnd_ReadOnly()
    {
        Seed(ProtectedPrincipalData.Empty with { GroupIds = [GroupRow], Groups = [GroupDn] });
        SeedIgnores(Ignore(GroupSid));
        var token = Fresh().GetChangeToken();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = ConfigDbCli.Run(["identifier-status", Path.Combine(_h.Root, "exchangeadmin.db")], stdout, stderr, () => throw new InvalidOperationException("no directory"));

        Assert.Equal(ConfigDbCli.ExitOk, exit);
        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal($"ignored {Store} group_sid: {GroupSid} - Missing (not found) by EXAMPLE\\admin at 2026-11-04T00:00:00.0000000Z", lines[^2]);
        Assert.Equal("1 verify ignore", lines[^1]);
        Assert.Equal(token, Fresh().GetChangeToken());
    }

    // ----- round-3 note: the pane's ordinary Remove + Save also takes the removed row's ignores -----

    [Fact]
    public async Task ThePanesOrdinaryRemoveAndSave_DeletesTheRemovedRowsIgnoreEntries()
    {
        Seed(TwoGroups);
        SeedIgnores(Ignore(GroupSid), Ignore(OtherSid, "LabelDrift", "CN=Ops Team,OU=Groups,DC=contoso,DC=com"));
        var page = _h.OpenAdminSettings();
        var ids = SettingsPageHarness.Field<List<string>>(page, typeof(AdminSettingsPage), "ppGroupIds");
        var legacy = SettingsPageHarness.Field<List<string>>(page, typeof(AdminSettingsPage), "ppGroups");

        await _h.RunAsync(page, "RemovePpId", ids, legacy, GroupRow);
        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal([OtherRow], StoredRules().GroupIds);
        Assert.Equal([OtherSid], StoredIgnoreEntries().Select(e => e.IdentifierOrText));
    }

    // A store that runs a hook once, just before its next read transaction: where "the other instance" acts between an
    // action's first attempt and its retry (the aud-a33b shape).
    private sealed class HookedStore(IConfigStore inner) : IConfigStore
    {
        public Action? BeforeNextRead { get; set; }
        public bool Fired { get; private set; }

        public long GetChangeToken() => inner.GetChangeToken();
        public T Read<T>(Func<SqliteConnection, T> read) => inner.Read(read);

        public T ReadInTransaction<T>(Func<SqliteConnection, SqliteTransaction?, T> read)
        {
            if (BeforeNextRead is { } hook)
            {
                BeforeNextRead = null;
                Fired = true;
                hook();
            }
            return inner.ReadInTransaction(read);
        }

        public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write) => inner.Write(write);
        public void Write(Action<SqliteConnection, SqliteTransaction> write) => inner.Write(write);
    }
}
