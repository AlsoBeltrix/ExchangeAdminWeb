using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/ProtectedPrincipalIdentifiers-Plan.md S1: protected users and groups stored by objectSid, OUs by objectGUID, as
/// <c>identifier|label</c> rows of their own kinds beside a companion legacy row an older build enforces.
/// </summary>
public class ProtectedPrincipalIdentifierTests : IDisposable
{
    private const string AliceSid = "S-1-5-21-1000000000-1000000000-1000000000-1105";
    private const string MallorySid = "S-1-5-21-1000000000-1000000000-1000000000-1199";
    private const string Alice = "alice@contoso.com";
    private const string GroupSid = "S-1-5-21-1000000000-1000000000-1000000000-2001";
    private const string GroupDn = "CN=Tier0 Admins,OU=Groups,DC=contoso,DC=com";
    private const string OuGuid = "0b5e3c2a-7d41-4f8e-9c6a-2e1f3d4b5a69";
    private const string OuDn = "OU=Tier0,DC=contoso,DC=com";

    private readonly string _tempDir;

    public ProtectedPrincipalIdentifierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ppi-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ----- entry format -----

    [Fact]
    public void Format_Then_Parse_RoundTrips_SplittingOnTheFirstSeparatorOnly()
    {
        // A label may contain the separator (a DN's escaped value, an odd address); the identifier never does.
        var stored = ProtectedPrincipalIdEntry.Format(AliceSid, "CN=a|b,DC=contoso,DC=com");

        var entry = ProtectedPrincipalIdEntry.Parse(stored);

        Assert.Equal($"{AliceSid}|CN=a|b,DC=contoso,DC=com", stored);
        Assert.Equal(new ProtectedPrincipalIdEntry(AliceSid, "CN=a|b,DC=contoso,DC=com"), entry);
        Assert.Equal("CN=a|b,DC=contoso,DC=com", entry!.Display);
    }

    [Fact]
    public void Parse_HandRepairedAndBlankRows()
    {
        Assert.Null(ProtectedPrincipalIdEntry.Parse("  "));
        Assert.Null(ProtectedPrincipalIdEntry.Parse("|label-only"));
        var bare = ProtectedPrincipalIdEntry.Parse(OuGuid);
        Assert.Equal(new ProtectedPrincipalIdEntry(OuGuid, ""), bare);
        Assert.Equal(OuGuid, bare!.Display);
    }

    [Fact]
    public void CanonicalIdentifiers_RefuseWhatIsNotOne()
    {
        Assert.Equal(AliceSid, ProtectedPrincipalIdEntry.CanonicalSid(" " + AliceSid.ToLowerInvariant() + " "));
        // An SDDL alias parses as a SID in the framework; it is a group NAME here.
        Assert.Null(ProtectedPrincipalIdEntry.CanonicalSid("BA"));
        Assert.Null(ProtectedPrincipalIdEntry.CanonicalSid("alice@contoso.com"));
        Assert.Equal(OuGuid, ProtectedPrincipalIdEntry.CanonicalGuid("{" + OuGuid.ToUpperInvariant() + "}"));
        Assert.Null(ProtectedPrincipalIdEntry.CanonicalGuid(OuDn));
    }

    // ----- storage -----

    [Fact]
    public void Repository_StoresEachIdentifierKindInItsOwnKind_AndReadsItBack()
    {
        var repo = new ProtectedPrincipalRepository(TestConfigStore.Create(_tempDir));
        var data = IdentifierData();

        repo.Save(data);
        var read = repo.ReadSnapshot();

        Assert.True(read.Readable);
        Assert.Equal(data.Users, read.Data.Users);
        Assert.Equal(data.Groups, read.Data.Groups);
        Assert.Equal(data.OrganizationalUnits, read.Data.OrganizationalUnits);
        Assert.Equal(data.UserIds, read.Data.UserIds);
        Assert.Equal(data.GroupIds, read.Data.GroupIds);
        Assert.Equal(data.OuIds, read.Data.OuIds);
        Assert.Equal(6, read.Data.RuleCount);
    }

    /// <summary>
    /// The reader as it stood at c94b443e - the build that shares this database until the new one is everywhere
    /// (plan B.1). Copied, not referenced, because the point is that IT does not change.
    /// </summary>
    private static (List<string> Users, List<string> Groups, List<string> Ous, List<string> Patterns, List<string> Targets)
        LegacyReaderAtC94b443e(IEnumerable<(string Kind, string Value)> rows)
    {
        var users = new List<string>();
        var groups = new List<string>();
        var ous = new List<string>();
        var patterns = new List<string>();
        var targets = new List<string>();
        foreach (var (kind, value) in rows)
        {
            switch (kind)
            {
                case "user": users.Add(value); break;
                case "group": groups.Add(value); break;
                case "ou": ous.Add(value); break;
                case "sam_pattern": patterns.Add(value); break;
                case "group_target": targets.Add(value); break;
            }
        }
        return (users, groups, ous, patterns, targets);
    }

    [Fact]
    public void Tripwire_AnOlderBuildsReader_IgnoresTheIdentifierKinds_AndNoIdentifierIsEverWrittenIntoALegacyKind()
    {
        // An older build reads the shared database with the switch above. It must see the companions - the legacy rows it
        // keeps enforcing - and nothing else: an identifier row in a legacy kind would read to it as a user, group or OU
        // named "S-1-...|...", which matches nothing, and the companion it should have enforced in its place is gone.
        var store = TestConfigStore.Create(_tempDir);
        new ProtectedPrincipalRepository(store).Save(IdentifierData());

        var rows = store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind, value FROM protected_principal;";
            using var reader = command.ExecuteReader();
            var all = new List<(string, string)>();
            while (reader.Read())
                all.Add((reader.GetString(0), reader.GetString(1)));
            return all;
        });

        var legacy = LegacyReaderAtC94b443e(rows);
        Assert.Equal([Alice], legacy.Users);
        Assert.Equal([GroupDn], legacy.Groups);
        Assert.Equal([OuDn], legacy.Ous);
        Assert.Empty(legacy.Patterns);
        Assert.Empty(legacy.Targets);

        var legacyKinds = new[] { "user", "group", "ou", "sam_pattern", "group_target" };
        Assert.DoesNotContain(ProtectedPrincipalRepository.KindUserSid, legacyKinds);
        Assert.DoesNotContain(ProtectedPrincipalRepository.KindGroupSid, legacyKinds);
        Assert.DoesNotContain(ProtectedPrincipalRepository.KindOuGuid, legacyKinds);
        foreach (var (kind, value) in rows.Where(r => legacyKinds.Contains(r.Item1) && r.Item1 != "group_target"))
        {
            var head = value.Split('|')[0];
            Assert.True(ProtectedPrincipalIdEntry.CanonicalSid(head) is null && ProtectedPrincipalIdEntry.CanonicalGuid(head) is null,
                $"Legacy kind '{kind}' holds an identifier row: {value}");
        }

        // And every identifier row is where the new reader looks for it.
        Assert.Contains((ProtectedPrincipalRepository.KindUserSid, $"{AliceSid}|{Alice}"), rows);
        Assert.Contains((ProtectedPrincipalRepository.KindGroupSid, $"{GroupSid}|{GroupDn}"), rows);
        Assert.Contains((ProtectedPrincipalRepository.KindOuGuid, $"{OuGuid}|{OuDn}"), rows);
    }

    private static ProtectedPrincipalData IdentifierData() => new([Alice], [GroupDn], [OuDn], [], [])
    {
        UserIds = [$"{AliceSid}|{Alice}"],
        GroupIds = [$"{GroupSid}|{GroupDn}"],
        OuIds = [$"{OuGuid}|{OuDn}"],
    };

    // ----- companion pairing -----

    [Fact]
    public void RowsToEvaluate_ACompanionIsNotEvaluated_AnUnpairedLegacyRowIs_InItsOrder()
    {
        var (legacy, _, ids) = ProtectedPrincipalService.RowsToEvaluate(
            ["bob@contoso.com", "ALICE@contoso.com", "carol@contoso.com"],
            [$"{AliceSid}|{Alice}"],
            ProtectedPrincipalIdEntry.CanonicalSid);

        Assert.Equal(["bob@contoso.com", "carol@contoso.com"], legacy);
        Assert.Equal([new ProtectedPrincipalIdEntry(AliceSid, Alice)], ids);
    }

    [Fact]
    public void RowsToEvaluate_WithNoIdentifierRows_IsEveryLegacyRowExactlyAsStored()
    {
        // Every unpaired row, as stored: the check refuses naming each (K.3), so none may be dropped here.
        string[] stored = ["bob@contoso.com", "CONTOSO\\carol", "bob@contoso.com"];

        var (legacy, _, ids) = ProtectedPrincipalService.RowsToEvaluate(stored, [], ProtectedPrincipalIdEntry.CanonicalSid);

        Assert.Equal(stored, legacy);
        Assert.Empty(ids);
    }

    [Fact]
    public void RowsToEvaluate_AnIdentifierThatDoesNotParse_IsReturnedAsUnreadable_NeverComparedByItsLabel()
    {
        // Its companion is not evaluated, so a row that cannot be compared by identifier must not compare by nothing: since K.3
        // it is returned to be refused by name, never matched by its label.
        var (legacy, unreadable, ids) = ProtectedPrincipalService.RowsToEvaluate(
            [Alice], [$"not-a-sid|{Alice}"], ProtectedPrincipalIdEntry.CanonicalSid);

        Assert.Empty(legacy);
        Assert.Equal([Alice], unreadable);
        Assert.Empty(ids);
    }

    // ----- comparison -----

    [Fact]
    public async Task AUserSidRow_MatchesTheTargetBySid_AndNotByASharedAddress()
    {
        // The aud-a2c shape: another account carries the protected user's address. Compared by string it would be
        // protected in her place; compared by SID it is not - and she is protected whatever her address now is.
        var service = ServiceWith(new ProtectedPrincipalConfig { Users = [Alice], UserIds = [$"{AliceSid}|{Alice}"] });

        var impostor = await service.CheckAsync(Target(upn: Alice, sid: MallorySid));
        var alice = await service.CheckAsync(Target(upn: "alice.renamed@contoso.com", sid: AliceSid));

        Assert.False(impostor.IsProtected);
        Assert.False(impostor.CheckFailed);
        Assert.True(alice.IsProtected);
        Assert.Equal([$"User:{Alice}"], alice.MatchedRules);
    }

    [Fact]
    public async Task AUserSidRow_AgainstAnOnPremisesTargetWhoseSidWasNotRead_RefusesAsIncomplete_NeverByItsLabel()
    {
        // rev-b2. A resolution that did not read the target's SID is not evidence the target is someone else - nor is its
        // label evidence it is the row's user: a renamed protected user keeps the SID and loses the label. The check cannot
        // compare, and refuses naming why, whether the label matches or not.
        var service = ServiceWith(new ProtectedPrincipalConfig { Users = [Alice], UserIds = [$"{AliceSid}|{Alice}"] });

        foreach (var upn in new[] { Alice, "alice.renamed@contoso.com" })
        {
            var result = await service.CheckAsync(Target(upn: upn, sid: null, sam: "alice", dn: "CN=Alice,OU=Staff,DC=contoso,DC=com"));

            Assert.True(result.CheckFailed, result.Reason);
            Assert.Contains(ProtectedPrincipalService.TargetSidNotRead, result.Reason, StringComparison.Ordinal);
            Assert.False(result.IsProtected);
        }
    }

    [Fact]
    public async Task AUserSidRow_RenamedUser_IsProtectedByItsSid_ThoughNoLabelMatches()
    {
        var service = ServiceWith(new ProtectedPrincipalConfig { Users = [Alice], UserIds = [$"{AliceSid}|{Alice}"] });

        var result = await service.CheckAsync(Target(upn: "alice.renamed@contoso.com", sid: AliceSid, sam: "alice2", dn: "CN=Alice2,OU=Staff,DC=contoso,DC=com"));

        Assert.True(result.IsProtected);
    }

    [Fact]
    public async Task AUserSidRow_CannotMatchACloudOnlyTarget_ByItsLabel()
    {
        // Constitution: a cloud-only principal cannot be protected - no DN, no sAMAccountName, no on-premises SID - so no user
        // row can name it, whatever its address. Not incomplete: there is nothing to compare.
        var service = ServiceWith(new ProtectedPrincipalConfig { Users = [Alice], UserIds = [$"{AliceSid}|{Alice}"] });

        var result = await service.CheckAsync(Target(upn: Alice, sid: null));

        Assert.False(result.IsProtected);
        Assert.False(result.CheckFailed, result.Reason);
    }

    [Fact]
    public async Task ALegacyUserRowWithNoCompanion_IsNeverMatchedByName_TheCheckRefusesNamingIt()
    {
        // Bob was added before identifiers were stored; Alice since. K.3: Bob's row is not matched by any form it matched
        // before - the check cannot say a target is not Bob, so it refuses, naming the row and the cure - while Alice's
        // identifier row still matches her.
        var service = ServiceWith(new ProtectedPrincipalConfig
        {
            Users = ["bob@contoso.com", "CONTOSO\\carol", Alice],
            UserIds = [$"{AliceSid}|{Alice}"],
        });

        var bob = await service.CheckAsync(Target(upn: "bob@contoso.com", sid: MallorySid));
        var alice = await service.CheckAsync(Target(upn: Alice, sid: AliceSid));

        Assert.True(bob.CheckFailed, bob.Reason);
        Assert.Contains("protected user 'bob@contoso.com': not converted - run db convert-identifiers", bob.Reason, StringComparison.Ordinal);
        Assert.Contains("protected user 'CONTOSO\\carol': not converted - run db convert-identifiers", bob.Reason, StringComparison.Ordinal);
        Assert.Equal([$"User:{Alice}"], alice.MatchedRules);
    }

    [Fact]
    public void TheWriteTargetCheck_ComparesAnOuGuidRowByItsLabel()
    {
        // Directory-free by construction, so it keeps the DN suffix compare the companion row got: no protection lost.
        var service = ServiceWith(new ProtectedPrincipalConfig { OrganizationalUnits = [OuDn], OuIds = [$"{OuGuid}|{OuDn}"] });

        var result = service.CheckWriteTarget(Target(upn: "", sid: null, dn: "CN=Tier0 Ops," + OuDn));

        Assert.True(result.IsProtected);
        Assert.Equal([$"OU:{OuDn}"], result.MatchedRules);
    }

    [Fact]
    public void AGroupSidEntryTheTokenCarries_MatchesWithNoResolveRead()
    {
        // Plan C.2: the group_sid row is passed as its SID, the token is the directory's answer, so no FindGroups read
        // is made for it - where a DN or name entry is resolved first.
        const string sidA = "S-1-5-21-100-200-300";
        const string tess = "CN=Tess,OU=People,DC=a,DC=example";
        const string staff = "CN=Staff,OU=Groups,DC=a,DC=example";
        var forest = new FakeForest().Domain("A", "a.example");
        forest.Account(tess, $"{sidA}-1105", primaryGroupRid: 513);
        forest.Group("CN=Domain Users,CN=Users,DC=a,DC=example", $"{sidA}-513", DirectoryGroupScope.Global, "Domain Users");
        forest.Group(staff, $"{sidA}-2001", DirectoryGroupScope.Global, "Staff", tess);

        var result = ProtectedGroupMembership.Evaluate([$"{sidA}-2001"], tess, forest, NullLogger.Instance);

        Assert.Equal([$"Group:{sidA}-2001"], result.Matches);
        Assert.False(result.Incomplete);
        Assert.DoesNotContain(forest.Calls, c => c.StartsWith("FindGroups", StringComparison.Ordinal));
        // And the rule is named by the row's label, as the page names it.
        Assert.Equal([$"Group:{staff}"],
            ProtectedPrincipalService.LabelGroupMatches(result, new Dictionary<string, string> { [$"{sidA}-2001"] = staff }));
    }

    private sealed class FakeOuDirectory(params DirectoryObjectGuid[] objects) : IProtectedOuDirectory
    {
        public List<string> Reads { get; } = new();
        public bool Fail { get; init; }

        public IReadOnlyList<DirectoryObjectGuid> ReadObjectGuids(IReadOnlyList<string> dns, string server)
        {
            Reads.Add($"{server}: {string.Join(" ; ", dns)}");
            if (Fail)
                throw new InvalidOperationException("scripted failure");
            return objects.Where(o => dns.Contains(o.DistinguishedName, StringComparer.OrdinalIgnoreCase)).ToList();
        }
    }

    [Fact]
    public void AnOuGuidRow_MatchesAfterTheOuIsRenamed()
    {
        // Stored when the OU was OU=Tier0; it has since been renamed OU=Tier Zero. The DN suffix compare stops matching at
        // the rename; the objectGUID does not change.
        const string target = "CN=Ops Admin,OU=Tier Zero,DC=contoso,DC=com";
        var directory = new FakeOuDirectory(
            new DirectoryObjectGuid(target, "9d1e2f3a-4b5c-4d6e-8f70-112233445566"),
            new DirectoryObjectGuid("OU=Tier Zero,DC=contoso,DC=com", OuGuid.ToUpperInvariant()));

        var (matches, incomplete) = ProtectedOuMembership.Evaluate(
            [new ProtectedPrincipalIdEntry(OuGuid, OuDn)], target, directory, NullLogger.Instance);

        Assert.Equal([$"OU:{OuDn}"], matches);
        Assert.False(incomplete);
        // One read, of the target's own containers, in its own domain - not the domain head above them.
        Assert.Equal([$"contoso.com: {target} ; OU=Tier Zero,DC=contoso,DC=com"], directory.Reads);
    }

    [Fact]
    public void AnOuGuidRow_DoesNotMatchAnotherOuThatTookItsName()
    {
        // The opposite direction, so the test above cannot be passing because every OU matches.
        const string target = "CN=Someone,OU=Tier0,DC=contoso,DC=com";
        var directory = new FakeOuDirectory(
            new DirectoryObjectGuid(target, "9d1e2f3a-4b5c-4d6e-8f70-112233445566"),
            new DirectoryObjectGuid(OuDn, "77777777-7777-4777-8777-777777777777"));

        var (matches, incomplete) = ProtectedOuMembership.Evaluate(
            [new ProtectedPrincipalIdEntry(OuGuid, OuDn)], target, directory, NullLogger.Instance);

        Assert.Empty(matches);
        Assert.False(incomplete);
    }

    [Fact]
    public void AnOuGuidCheck_ThatCannotReadEveryContainer_IsIncomplete()
    {
        const string target = "CN=Someone,OU=Tier0,DC=contoso,DC=com";
        var ous = new[] { new ProtectedPrincipalIdEntry(OuGuid, OuDn) };

        var failed = ProtectedOuMembership.Evaluate(ous, target, new FakeOuDirectory { Fail = true }, NullLogger.Instance);
        var partial = ProtectedOuMembership.Evaluate(
            ous, target, new FakeOuDirectory(new DirectoryObjectGuid(target, OuGuid.Replace('0', '1'))), NullLogger.Instance);
        var noDomain = ProtectedOuMembership.Evaluate(ous, "CN=Someone,OU=Tier0", new FakeOuDirectory(), NullLogger.Instance);

        Assert.True(failed.Incomplete);
        Assert.True(partial.Incomplete);
        Assert.True(noDomain.Incomplete);
    }

    // ----- the admin page's add and remove -----

    [Fact]
    public void Remove_DeletesTheIdentifierRowAndItsCompanion()
    {
        var ids = new List<string> { $"{AliceSid}|{Alice}", $"{MallorySid}|mallory@contoso.com" };
        var legacy = new List<string> { "bob@contoso.com", Alice, "mallory@contoso.com" };

        Assert.True(ProtectedPrincipalIdEntry.RemoveWithCompanion(ids, legacy, $"{AliceSid}|{Alice}"));

        Assert.Equal([$"{MallorySid}|mallory@contoso.com"], ids);
        Assert.Equal(["bob@contoso.com", "mallory@contoso.com"], legacy);
    }

    [Fact]
    public void Remove_KeepsACompanionAnotherIdentifierRowStillPairsWith()
    {
        var ids = new List<string> { $"{AliceSid}|{Alice}", $"{MallorySid}|{Alice}" };
        var legacy = new List<string> { Alice };

        ProtectedPrincipalIdEntry.RemoveWithCompanion(ids, legacy, $"{AliceSid}|{Alice}");

        Assert.Equal([Alice], legacy);
    }

    [Fact]
    public void Add_StoresBothRows_AndTheSameObjectReAddedReplacesItsRow()
    {
        var ids = new List<string>();
        var legacy = new List<string> { Alice };

        // Adding an entry whose legacy row already exists makes that row its companion.
        Assert.True(ProtectedPrincipalIdEntry.AddWithCompanion(ids, legacy, $"{AliceSid}|{Alice}", Alice));
        Assert.Equal([$"{AliceSid}|{Alice}"], ids);
        Assert.Equal([Alice], legacy);

        // Alice renamed: the same SID re-added refreshes the label and the companion, not two rows for one object.
        Assert.True(ProtectedPrincipalIdEntry.AddWithCompanion(ids, legacy, $"{AliceSid}|alice.new@contoso.com", "alice.new@contoso.com"));
        Assert.Equal([$"{AliceSid}|alice.new@contoso.com"], ids);
        Assert.Equal(["alice.new@contoso.com"], legacy);
    }

    // ----- harness -----

    private static ResolvedDirectoryPrincipal Target(string upn, string? sid, string? sam = null, string? dn = null) =>
        new("Test", upn, upn, sam, upn, dn, null, null, ObjectSid: sid);

    private ProtectedPrincipalService ServiceWith(ProtectedPrincipalConfig config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = _tempDir,
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        var moduleConfig = new ModuleConfigService(
            new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_tempDir), NullLogger<ModuleConfigService>.Instance);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(configuration, env, TestConfigStore.CreateAppSettings(_tempDir), NullLogger<ExtendedLogService>.Instance);
        var trace = new OperationTraceService(configuration, new JsonlLogService(configuration, NullLogger<JsonlLogService>.Instance));
        var delinea = new DelineaService(httpClientFactory, configuration, NullLogger<DelineaService>.Instance, extLog, trace);
        var service = new ProtectedPrincipalService(
            env, configuration, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea,
            NullLogger<ProtectedPrincipalService>.Instance);
        service.SaveConfig(config);
        return service;
    }
}
