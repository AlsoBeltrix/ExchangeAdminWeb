using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a3b: the protected-group membership evaluation. Each hole in the record is reproduced
/// against a fake forest in which every domain controller holds ONLY its own domain's group
/// links - so the data is genuinely absent from the place the old check asked, the way it is in
/// a real multi-domain forest, rather than merely unrequested.
/// </summary>
public class ProtectedGroupMembershipTests
{
    // Two domains. The SIDs are made up; only their shape matters.
    private const string DomainA = "a.example";
    private const string DomainB = "b.example";
    private const string SidA = "S-1-5-21-100-200-300";
    private const string SidB = "S-1-5-21-400-500-600";

    private const string Tess = "CN=Tess,OU=People,DC=a,DC=example";
    private const string Uma = "CN=Uma,OU=People,DC=a,DC=example";
    private const string DomainUsersA = "CN=Domain Users,CN=Users,DC=a,DC=example";
    private const string StaffA = "CN=Staff,OU=Groups,DC=a,DC=example";
    private const string ProtectedVipsB = "CN=Protected VIPs,OU=Groups,DC=b,DC=example";
    private const string PrivilegedA = "CN=Privileged,OU=Groups,DC=a,DC=example";
    private const string PrivilegedB = "CN=Privileged,OU=Groups,DC=b,DC=example";

    /// <summary>
    /// Tess (domain A) is in A\Staff, which is nested in B's DOMAIN-LOCAL Protected VIPs. Her
    /// primary group is A\Domain Users, which - as in AD - carries no member link to her. Uma is
    /// in nothing but her primary group. A\Privileged and B\Privileged share a name; only the B
    /// one contains Tess.
    /// </summary>
    private static FakeForest Forest()
    {
        var f = new FakeForest()
            .Domain("A", DomainA)
            .Domain("B", DomainB);

        f.Account(Tess, $"{SidA}-1105", primaryGroupRid: 513);
        f.Account(Uma, $"{SidA}-1106", primaryGroupRid: 513);
        f.Group(DomainUsersA, $"{SidA}-513", DirectoryGroupScope.Global, "Domain Users");
        f.Group(StaffA, $"{SidA}-2001", DirectoryGroupScope.Global, "Staff", Tess);
        f.Group(ProtectedVipsB, $"{SidB}-3001", DirectoryGroupScope.DomainLocal, "Protected VIPs", StaffA);
        f.Group(PrivilegedA, $"{SidA}-2002", DirectoryGroupScope.Global, "Privileged");
        f.Group(PrivilegedB, $"{SidB}-3002", DirectoryGroupScope.DomainLocal, "Privileged", Tess);
        return f;
    }

    private static ProtectedGroupMembership.Result Evaluate(FakeForest forest, string targetDn, params string[] protectedGroups)
        => ProtectedGroupMembership.Evaluate(protectedGroups, targetDn, forest, NullLogger.Instance);

    // ----- Hole 1: a domain-local group in another domain -----

    [Fact]
    public void DomainLocalGroupInAnotherDomain_ProtectsThroughANestedGlobalGroup()
    {
        // The finding: A's DC does not hold B's domain-local links, so asking A - all the old
        // check ever did - returns nothing while the answer is yes.
        var forest = Forest();

        var result = Evaluate(forest, Tess, ProtectedVipsB);

        Assert.Equal(["Group:" + ProtectedVipsB], result.Matches);
        Assert.False(result.Incomplete);
        // Asked where the links live, not where the target lives.
        Assert.Contains($"DomainLocalGroupContainsAny {DomainB}", forest.Calls);
    }

    [Fact]
    public void DomainLocalGroupInAnotherDomain_DoesNotProtectSomeoneOutsideIt()
    {
        // The opposite direction, so the test above cannot be passing because everything matches.
        var result = Evaluate(Forest(), Uma, ProtectedVipsB);

        Assert.Empty(result.Matches);
        Assert.False(result.Incomplete);
    }

    [Fact]
    public void DomainLocalGroupInAnotherDomain_ProtectsADirectMember()
    {
        var forest = Forest();
        forest.AddMember(ProtectedVipsB, Uma);

        Assert.Single(Evaluate(forest, Uma, ProtectedVipsB).Matches);
    }

    [Fact]
    public void DomainLocalGroup_NestedInsideAnotherOfItsOwnDomain_IsFollowed()
    {
        // B\Outer (domain-local) contains B\Inner (domain-local) contains A\Staff contains Tess.
        // Only B holds either link.
        var forest = Forest();
        const string inner = "CN=Inner,OU=Groups,DC=b,DC=example";
        const string outer = "CN=Outer,OU=Groups,DC=b,DC=example";
        forest.Group(inner, $"{SidB}-3010", DirectoryGroupScope.DomainLocal, "Inner", StaffA);
        forest.Group(outer, $"{SidB}-3011", DirectoryGroupScope.DomainLocal, "Outer", inner);

        Assert.Single(Evaluate(forest, Tess, outer).Matches);
    }

    // ----- Hole 2: primary-group membership -----

    [Fact]
    public void PrimaryGroup_IsAMembership()
    {
        // Domain Users has no member link to Tess - primaryGroupID carries it - so a link query
        // alone can never see it. Protecting the primary group used to protect nobody.
        var result = Evaluate(Forest(), Tess, DomainUsersA);

        Assert.Equal(["Group:" + DomainUsersA], result.Matches);
        Assert.False(result.Incomplete);
    }

    [Fact]
    public void PrimaryGroup_NestedInAProtectedGroupOfTheSameDomain_Protects()
    {
        var forest = Forest();
        const string allAccounts = "CN=All Accounts,OU=Groups,DC=a,DC=example";
        forest.Group(allAccounts, $"{SidA}-2010", DirectoryGroupScope.Global, "All Accounts", DomainUsersA);

        Assert.Single(Evaluate(forest, Tess, allAccounts).Matches);
    }

    [Fact]
    public void PrimaryGroup_NestedInADomainLocalGroupOfAnotherDomain_Protects()
    {
        // Both holes at once: the only route is the primary group, and the only place the link
        // exists is the other domain.
        var forest = Forest();
        const string remote = "CN=Remote Staff,OU=Groups,DC=b,DC=example";
        forest.Group(remote, $"{SidB}-3020", DirectoryGroupScope.DomainLocal, "Remote Staff", DomainUsersA);

        Assert.Single(Evaluate(forest, Uma, remote).Matches);
    }

    [Fact]
    public void AnAccountWhosePrimaryGroupCannotBeRead_FailsClosed()
    {
        // An account always has a primary group. One that cannot be read is an incomplete read,
        // not an account without one - skipping it would quietly reopen hole 2.
        var forest = Forest();
        const string odd = "CN=Odd,OU=People,DC=a,DC=example";
        forest.Account(odd, $"{SidA}-1107", primaryGroupRid: null);

        var result = Evaluate(forest, odd, StaffA);

        Assert.Empty(result.Matches);
        Assert.True(result.Incomplete);
    }

    [Fact]
    public void APrimaryGroupThatDoesNotResolve_FailsClosed()
    {
        var forest = Forest();
        const string odd = "CN=Odd,OU=People,DC=a,DC=example";
        forest.Account(odd, $"{SidA}-1108", primaryGroupRid: 9999);

        Assert.True(Evaluate(forest, odd, StaffA).Incomplete);
    }

    // ----- Hole 3: the resolver. K.3: names are resolved only by db convert-identifiers (ConfigDbIdentifierTests) -----

    [Theory]
    [InlineData(@"B\Privileged")]
    [InlineData("Privileged")]
    [InlineData("Protected VIPs")]
    public void ANameEntry_IsNeverLookedUp_ItIsIncomplete_NotConverted(string name)
    {
        // Tess is in B\Privileged and in Protected VIPs: a lookup by name would have matched. The check does not look a name
        // up at all - not in the named domain, not forest-wide - and cannot answer for it.
        var forest = Forest();

        var result = Evaluate(forest, Tess, name);

        Assert.Empty(result.Matches);
        Assert.True(result.Incomplete);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(name, problem.Entry);
        Assert.Equal(ProtectedGroupMembership.NotConverted, problem.Problem);
        Assert.DoesNotContain(forest.Calls, c => c.Contains("SamAccountName", StringComparison.Ordinal) || c.Contains("CommonName", StringComparison.Ordinal));
    }

    [Fact]
    public void ASidEntryNamingNoGroup_IsIncomplete_AndNamed()
    {
        // A deleted group's SID: sweep-c item 17's first build let it protect nobody; K.3 removed that branch - the check
        // cannot answer for it, refuses, and names it until it is removed or re-picked.
        var result = Evaluate(Forest(), Uma, $"{SidA}-9999", StaffA);

        Assert.True(result.Incomplete);
        var problem = Assert.Single(result.Problems);
        Assert.Equal($"{SidA}-9999", problem.Entry);
        Assert.Equal(ProtectedGroupMembership.NoSuchGroup, problem.Problem);
    }

    [Fact]
    public void DnEntry_ThatNoLongerExists_FailsClosed()
    {
        // A configured DN used to be passed through with no lookup at all. A group that is gone
        // (deleted, or renamed so the DN moved) protects nobody, and the check must say it cannot
        // answer rather than answer for it.
        var result = Evaluate(Forest(), Tess, "CN=Gone,OU=Groups,DC=a,DC=example");

        Assert.Empty(result.Matches);
        Assert.True(result.Incomplete);
    }

    [Fact]
    public void DnEntry_IsReadInItsOwnDomain()
    {
        var forest = Forest();

        Assert.Single(Evaluate(forest, Tess, PrivilegedB).Matches);
        Assert.Contains($"FindGroups DistinguishedName {PrivilegedB} {DomainB}", forest.Calls);
    }

    [Fact]
    public void AnEntryForAGroupJustRead_ThatNamesNoGroup_IsStillIncomplete()
    {
        // The self-grant check and the self-service deny trustees pass groups they have just read: no such group there means
        // the read raced a change, and the answer stays "cannot tell".
        var result = Evaluate(Forest(), Uma, "CN=Gone,OU=Groups,DC=a,DC=example");

        Assert.True(result.Incomplete);
        Assert.Equal(ProtectedGroupMembership.NoSuchGroup, Assert.Single(result.Problems).Problem);
    }

    [Fact]
    public void SidEntry_IsResolvedForestWide()
    {
        Assert.Single(Evaluate(Forest(), Tess, $"{SidB}-3001").Matches);
    }

    // ----- Rules carried over from the previous check -----

    [Fact]
    public void AGroupTarget_ThatIsTheProtectedGroup_Matches()
    {
        // In-chain ancestry never includes self, so a protected group must match as itself
        // (owner 2026-08-11; nesting plan S1). Compared by SID now, not DN.
        var result = Evaluate(Forest(), ProtectedVipsB, ProtectedVipsB);

        Assert.Single(result.Matches);
        Assert.False(result.Incomplete);
    }

    [Fact]
    public void AGroupTarget_DoesNotNeedAPrimaryGroup()
    {
        var result = Evaluate(Forest(), StaffA, ProtectedVipsB);

        Assert.Single(result.Matches);
        Assert.False(result.Incomplete);
    }

    [Theory]
    [InlineData("CN=NoDomainHere")]
    [InlineData("CN=Tess,OU=People")]
    public void ATargetWhoseOwningDomainCannotBeDetermined_FailsClosed(string targetDn)
    {
        // aud-a3's rule, now in the evaluator: Group Management omits -Server for such a DN and
        // keeps the local binding, which is right for a listing and wrong for a protection check.
        var forest = Forest();

        var result = Evaluate(forest, targetDn, StaffA);

        Assert.Empty(result.Matches);
        Assert.True(result.Incomplete);
        Assert.Empty(forest.Calls);
    }

    [Fact]
    public void TheTargetIsReadInItsOwnDomain()
    {
        var forest = Forest();

        Evaluate(forest, Tess, StaffA);

        Assert.Contains($"ReadTarget {DomainA}", forest.Calls);
        // kd-88: the target's groups are its own domain's answer, its token - read there.
        Assert.Contains($"ReadTokenGroups {DomainA}", forest.Calls);
    }

    // ----- aud-a3c: universal groups are read from the global catalog -----

    private const string ExecutivesB = "CN=Executives,OU=Groups,DC=b,DC=example";

    [Fact]
    public void UniversalGroupOfAnotherDomain_ProtectsThroughAGlobalGroup()
    {
        // aud-a3c. The universal group's link to A\Staff lives in B and in the global catalog,
        // not in A. The previous query asked A, so it saw this only when A's DC happened to be a
        // GC - which the fake's DCs are not. kd-88: A's DC computes the token through a global
        // catalog, so the universal group is in it - and nothing else is read.
        var forest = Forest();
        forest.Group(ExecutivesB, $"{SidB}-3030", DirectoryGroupScope.Universal, "Executives", StaffA);

        var result = Evaluate(forest, Tess, ExecutivesB);

        Assert.Equal(["Group:" + ExecutivesB], result.Matches);
        Assert.False(result.Incomplete);
        Assert.Contains($"ReadTokenGroups {DomainA}", forest.Calls);
        Assert.DoesNotContain(forest.Calls, call => call.StartsWith("ReadMemberOf ", StringComparison.Ordinal));
    }

    [Fact]
    public void UniversalGroupOfAnotherDomain_DoesNotProtectSomeoneOutsideIt()
    {
        var forest = Forest();
        forest.Group(ExecutivesB, $"{SidB}-3030", DirectoryGroupScope.Universal, "Executives", StaffA);

        var result = Evaluate(forest, Uma, ExecutivesB);

        Assert.Empty(result.Matches);
        Assert.False(result.Incomplete);
    }

    [Fact]
    public void DomainLocalGroup_ReachedOnlyThroughAUniversalGroupOfAnotherDomain_Protects()
    {
        // A\Admins Local (domain-local, A) contains B\Executives (universal) contains A\Staff
        // contains Tess. A holds the first link, the GC the second: neither domain's data alone
        // completes the chain, so the domain-local query must start from the universal ancestor.
        var forest = Forest();
        const string adminsLocal = "CN=Admins Local,OU=Groups,DC=a,DC=example";
        forest.Group(ExecutivesB, $"{SidB}-3030", DirectoryGroupScope.Universal, "Executives", StaffA);
        forest.Group(adminsLocal, $"{SidA}-2030", DirectoryGroupScope.DomainLocal, "Admins Local", ExecutivesB);

        var result = Evaluate(forest, Tess, adminsLocal);

        Assert.Equal(["Group:" + adminsLocal], result.Matches);
        Assert.False(result.Incomplete);
    }

    [Fact]
    public void UniversalGroupsNestedInUniversalGroups_AreFollowed()
    {
        var forest = Forest();
        const string board = "CN=Board,OU=Groups,DC=a,DC=example";
        forest.Group(ExecutivesB, $"{SidB}-3030", DirectoryGroupScope.Universal, "Executives", StaffA);
        forest.Group(board, $"{SidA}-2031", DirectoryGroupScope.Universal, "Board", ExecutivesB);

        Assert.Single(Evaluate(forest, Tess, board).Matches);
    }

    [Fact]
    public void AFailedGlobalCatalogRead_FailsClosed()
    {
        var forest = Forest();
        forest.Group(ExecutivesB, $"{SidB}-3030", DirectoryGroupScope.Universal, "Executives", StaffA);
        // kd-88: the universal membership is the token's, which A's DC computes through a global catalog.
        forest.FailOn = "ReadTokenGroups";

        var result = Evaluate(forest, Tess, ExecutivesB);

        Assert.Empty(result.Matches);
        Assert.True(result.Incomplete);
    }

    // ----- kd-88: an account's groups are its token; only what a token cannot carry is read from the links -----

    private static bool ReadAnyLink(FakeForest forest)
        => forest.Calls.Any(call => call.StartsWith("ReadMemberOf ", StringComparison.Ordinal)
                                    || call.StartsWith("DomainLocalGroupContainsAny ", StringComparison.Ordinal));

    [Fact]
    public void AnAccountsSecurityGroups_AreAnsweredByItsTokenAlone()
    {
        var forest = Forest();

        var result = Evaluate(forest, Tess, StaffA);

        Assert.Equal(["Group:" + StaffA], result.Matches);
        Assert.False(result.Incomplete);
        Assert.Equal([$"ReadTarget {DomainA}", $"ReadTokenGroups {DomainA}", $"FindGroups DistinguishedName {StaffA} {DomainA}"], forest.Calls);
    }

    [Fact]
    public void AnAccountOutsideEveryProtectedSecurityGroup_IsAnsweredByItsTokenAlone()
    {
        // The common case - a target nobody protects - costs the target read, the token read and the entries' own reads.
        var forest = Forest();
        forest.Group(ExecutivesB, $"{SidB}-3030", DirectoryGroupScope.Universal, "Executives", StaffA);

        var result = Evaluate(forest, Uma, StaffA, ExecutivesB, PrivilegedA);

        Assert.Empty(result.Matches);
        Assert.False(result.Incomplete);
        Assert.False(ReadAnyLink(forest));
        Assert.Equal(5, forest.Calls.Count);
    }

    [Fact]
    public void APrimaryGroupOnlyMembership_IsDetected()
    {
        // Hole 2, by construction: Uma is in nothing but her primary group, which no member link records; her token
        // carries it, and the match comes from the token with no link read at all.
        var forest = Forest();

        var result = Evaluate(forest, Uma, DomainUsersA);

        Assert.Equal(["Group:" + DomainUsersA], result.Matches);
        Assert.False(result.Incomplete);
        Assert.False(ReadAnyLink(forest));
    }

    [Fact]
    public void ATokenThatDoesNotCarryThePrimaryGroup_FailsClosed()
    {
        // A token the directory withholds from the reader reads as empty, not as "in nothing": every account's token
        // carries its primary group, so one without it was not computed.
        var forest = Forest();
        forest.WithholdTokenGroups = true;

        var result = Evaluate(forest, Tess, PrivilegedA);

        Assert.Empty(result.Matches);
        Assert.True(result.Incomplete);
    }

    [Fact]
    public void ADistributionGroupHoldingTheTarget_Protects_ThroughItsLinks()
    {
        // No token carries a distribution group, so the token's silence is no answer for one: the target's links are.
        var forest = Forest();
        const string list = "CN=Exec List,OU=Groups,DC=a,DC=example";
        forest.DistributionGroup(list, $"{SidA}-2040", DirectoryGroupScope.Universal, "Exec List", Tess);

        var result = Evaluate(forest, Tess, list);

        Assert.Equal(["Group:" + list], result.Matches);
        Assert.False(result.Incomplete);
        Assert.Contains($"ReadMemberOf {DomainA}", forest.Calls);
    }

    [Fact]
    public void ADistributionGroupOfAnotherDomain_HoldingTheTargetsGroup_Protects()
    {
        // B's universal distribution list holds A\Staff, which holds Tess: A holds the first link, the global catalog the second.
        var forest = Forest();
        const string list = "CN=All Hands,OU=Groups,DC=b,DC=example";
        forest.DistributionGroup(list, $"{SidB}-3040", DirectoryGroupScope.Universal, "All Hands", StaffA);

        var result = Evaluate(forest, Tess, list);

        Assert.Equal(["Group:" + list], result.Matches);
        Assert.False(result.Incomplete);
        Assert.Contains("ReadMemberOf forest", forest.Calls);
    }

    [Fact]
    public void ADistributionGroupWithoutTheTarget_DoesNotProtect()
    {
        var forest = Forest();
        const string list = "CN=All Hands,OU=Groups,DC=b,DC=example";
        forest.DistributionGroup(list, $"{SidB}-3040", DirectoryGroupScope.Universal, "All Hands", StaffA);

        var result = Evaluate(forest, Uma, list);

        Assert.Empty(result.Matches);
        Assert.False(result.Incomplete);
    }

    [Fact]
    public void AGroupTarget_IsReadFromItsLinks_NotFromATokenItDoesNotHave()
    {
        var forest = Forest();

        var result = Evaluate(forest, StaffA, ProtectedVipsB);

        Assert.Single(result.Matches);
        Assert.False(result.Incomplete);
        Assert.DoesNotContain(forest.Calls, call => call.StartsWith("ReadTokenGroups ", StringComparison.Ordinal));
        Assert.Contains($"ReadMemberOf {DomainA}", forest.Calls);
        Assert.Contains($"DomainLocalGroupContainsAny {DomainB}", forest.Calls);
    }

    [Fact]
    public void AGroupTarget_NestedThroughItsOwnDomainIntoAUniversalGroupOfAnother_Protects()
    {
        // A\Staff is in A\Staff Leads (global), which is in B\Council (universal): A holds the first link, the global
        // catalog the second - the shape the 2026-10-21 version found with its two unanchored searches.
        var forest = Forest();
        const string leads = "CN=Staff Leads,OU=Groups,DC=a,DC=example";
        const string council = "CN=Council,OU=Groups,DC=b,DC=example";
        forest.Group(leads, $"{SidA}-2050", DirectoryGroupScope.Global, "Staff Leads", StaffA);
        forest.Group(council, $"{SidB}-3050", DirectoryGroupScope.Universal, "Council", leads);

        Assert.Equal(["Group:" + council], Evaluate(forest, StaffA, council).Matches);
        Assert.Empty(Evaluate(Forest(), StaffA, PrivilegedA).Matches);
    }

    [Fact]
    public void AContactInAProtectedGroup_IsProtected_ThroughItsLinks()
    {
        // A contact has no SID and so no token; its links are all there is.
        var forest = Forest();
        const string contact = "CN=Vendor,OU=Contacts,DC=a,DC=example";
        forest.Contact(contact);
        forest.AddMember(StaffA, contact);

        var result = Evaluate(forest, contact, StaffA);

        Assert.Equal(["Group:" + StaffA], result.Matches);
        Assert.False(result.Incomplete);
    }

    [Fact]
    public void AnObjectTheLinkReadDoesNotReturn_FailsClosed()
    {
        // Everything the ancestry read asks for is known to exist; one left out of the answer was not read, and its links
        // are not "none".
        var forest = Forest();
        forest.Unreturned.Add(StaffA);

        var result = Evaluate(forest, StaffA, ProtectedVipsB);

        Assert.Empty(result.Matches);
        Assert.True(result.Incomplete);
    }

    [Theory]
    [InlineData("ReadMemberOf")]
    [InlineData("DomainLocalGroupContainsAny")]
    public void AFailedLinkRead_FailsClosed(string failingRead)
    {
        var forest = Forest();
        forest.FailOn = failingRead;

        var result = Evaluate(forest, Uma, ProtectedVipsB);

        Assert.Empty(result.Matches);
        Assert.True(result.Incomplete);
    }

    [Fact]
    public void ANestingDeeperThanTheLinkReadFollows_FailsClosed()
    {
        var forest = Forest();
        var below = StaffA;
        for (var i = 0; i < ProtectedGroupMembership.MaxNestingLevels + 2; i++)
        {
            var dn = $"CN=Layer {i},OU=Groups,DC=a,DC=example";
            forest.Group(dn, $"{SidA}-{5000 + i}", DirectoryGroupScope.Global, $"Layer {i}", below);
            below = dn;
        }

        var result = Evaluate(forest, StaffA, ProtectedVipsB);

        // ProtectedVipsB holds StaffA directly, but the answer could only be had from a read that gave up: incomplete.
        Assert.Empty(result.Matches);
        Assert.True(result.Incomplete);
    }

    [Fact]
    public void TheDirectorySeam_HasNoReadThatSearchesForWhateverContainsTheTarget()
    {
        // kd-88. The seam's reads, by name. GroupsContainingAny (every group of a domain whose chain reaches a DN) and
        // UniversalGroupsContainingAny (every universal group of the forest likewise) are gone; a new member here is a new
        // read and must be shown to be anchored before this list grows.
        var reads = typeof(IProtectedGroupDirectory).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                nameof(IProtectedGroupDirectory.DomainLocalGroupContainsAny), nameof(IProtectedGroupDirectory.FindGroups),
                nameof(IProtectedGroupDirectory.FindObjectDnsBySid), nameof(IProtectedGroupDirectory.ReadMemberOf),
                nameof(IProtectedGroupDirectory.ReadTarget), nameof(IProtectedGroupDirectory.ReadTokenGroups),
                nameof(IProtectedGroupDirectory.ResolveDomainServer),
            ],
            reads);
    }

    public static TheoryData<string> PathsThroughTheDirectory =>
    [
        "an account and its own domain's group", "an account and a domain-local group of another domain",
        "an account and a universal group", "an account and a distribution group", "an account and its primary group",
        "a group and a domain-local group of another domain", "a group and a universal group",
    ];

    [Theory]
    [MemberData(nameof(PathsThroughTheDirectory))]
    public void NoPathSendsAnUnanchoredInChainFilter(string path)
    {
        // kd-88's tripwire, over the runspace reads themselves: every filter the check sends to the stand-in directory is
        // recorded, and one that carries the in-chain rule must be anchored on one named group - never "every group whose
        // chain reaches these", over a domain or the forest. The evaluation must also be answered, so no path passes by
        // failing.
        var forest = new SelfGrantDirectoryStandIn();
        var user = forest.AddUser("tess");
        var team = forest.AddGroup("Team", members: [user]);
        const string other = SelfGrantDirectoryStandIn.OtherDomainDn;
        const string otherSid = SelfGrantDirectoryStandIn.OtherDomainSid;
        var (target, protectedGroup) = path switch
        {
            "an account and its own domain's group" => (user, team),
            "an account and a domain-local group of another domain" =>
                (user, forest.AddGroup("Remote", domainDn: other, domainSid: otherSid, domainLocal: true, members: [team])),
            "an account and a universal group" =>
                (user, forest.AddGroup("Council", domainDn: other, domainSid: otherSid, universal: true, members: [team])),
            "an account and a distribution group" =>
                (user, forest.AddGroup("List", domainDn: other, domainSid: otherSid, universal: true, distribution: true, members: [team])),
            "an account and its primary group" => (user, forest.DomainUsers),
            "a group and a domain-local group of another domain" =>
                (team, forest.AddGroup("Remote", domainDn: other, domainSid: otherSid, domainLocal: true, members: [team])),
            _ => (team, forest.AddGroup("Council", domainDn: other, domainSid: otherSid, universal: true, members: [team])),
        };

        using var runspace = forest.Open();
        runspace.Open();
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.Runspace = runspace;
        var credential = new System.Management.Automation.PSCredential("svc", new System.Net.NetworkCredential("", "secret").SecurePassword);

        var result = ProtectedGroupMembership.Evaluate(
            [protectedGroup.Dn], target.Dn, new PowerShellProtectedGroupDirectory(ps, credential), NullLogger.Instance);

        Assert.Equal(["Group:" + protectedGroup.Dn], result.Matches);
        Assert.False(result.Incomplete);
        Assert.Empty(forest.Errors);
        var inChain = forest.Searches.Where(search => search.Contains(ProtectedGroupMembership.InChainRule, StringComparison.Ordinal)).ToList();
        Assert.All(inChain, search =>
            Assert.StartsWith("(&(distinguishedName=", search.Split(' ', 3)[2], StringComparison.Ordinal));
        Assert.DoesNotContain(forest.Searches,
            search => search.Contains("memberOf:" + ProtectedGroupMembership.InChainRule, StringComparison.OrdinalIgnoreCase));
    }

    // ----- Fail closed on every read -----

    [Theory]
    [InlineData("ReadTarget")]
    [InlineData("ReadTokenGroups")]
    [InlineData("FindGroups")]
    public void ADirectoryReadThatFails_FailsClosed(string failingRead)
    {
        var forest = Forest();
        forest.FailOn = failingRead;

        var result = Evaluate(forest, Tess, StaffA);

        Assert.Empty(result.Matches);
        Assert.True(result.Incomplete);
    }

    [Fact]
    public void AFailedDomainLocalQuery_FailsClosed()
    {
        var forest = Forest();
        forest.FailOn = "DomainLocalGroupContainsAny";

        var result = Evaluate(forest, Uma, ProtectedVipsB);

        Assert.Empty(result.Matches);
        Assert.True(result.Incomplete);
    }

    [Fact]
    public void AMatchElsewhere_StillProtects_WhenAnotherEntryCannotBeAnswered()
    {
        // Protected is the safe answer; an unresolvable entry must not cancel a real match.
        // K.3: a name is never looked up by the check - it is incomplete, "not converted" - and still cannot cancel a real match.
        var result = Evaluate(Forest(), Tess, @"NOWHERE\Privileged", StaffA);

        Assert.Equal(["Group:" + StaffA], result.Matches);
        Assert.True(result.Incomplete);
    }

    [Fact]
    public void NoEntries_IsAnsweredWithoutTheDirectory()
    {
        var forest = Forest();

        var result = Evaluate(forest, Tess, "", "  ");

        Assert.Empty(result.Matches);
        Assert.False(result.Incomplete);
        Assert.Empty(forest.Calls);
    }

    // ----- Pure helpers -----

    [Theory]
    [InlineData("CN=Ops,OU=Groups,DC=a,DC=example", "DistinguishedName", "CN=Ops,OU=Groups,DC=a,DC=example", null)]
    [InlineData(@"CN=Ops\, East,OU=Groups,DC=a,DC=example", "DistinguishedName", @"CN=Ops\, East,OU=Groups,DC=a,DC=example", null)]
    [InlineData(@"B\Privileged", "DomainQualified", "Privileged", "B")]
    [InlineData(@" b.example\VR Staff ", "DomainQualified", "VR Staff", "b.example")]
    [InlineData("S-1-5-21-400-500-600-3001", "Sid", "S-1-5-21-400-500-600-3001", null)]
    [InlineData("Privileged", "Name", "Privileged", null)]
    [InlineData("BA", "Name", "BA", null)]
    [InlineData(@"B\", "Name", @"B\", null)]
    public void Parse_ClassifiesEachShape(string raw, string kind, string value, string? domain)
    {
        // "BA" is an SDDL alias the framework parses as BUILTIN\Administrators; it must stay a
        // name, or a group called BA would be looked up as a SID. (The kind arrives as text
        // because a public test method cannot take an internal enum.)
        var entry = ProtectedGroupEntry.Parse(raw);

        Assert.Equal(Enum.Parse<ProtectedGroupEntryKind>(kind), entry.Kind);
        Assert.Equal(value, entry.Value);
        Assert.Equal(domain, entry.Domain);
    }

    [Fact]
    public void PrimaryGroupSid_IsTheAccountDomainSidWithTheRid()
    {
        Assert.Equal($"{SidA}-513", ProtectedGroupMembership.PrimaryGroupSid($"{SidA}-1105", 513));
    }

    [Theory]
    [InlineData("S-1-5-32-544", 513)]
    [InlineData("S-1-5-21-100-200-300-1105", 0)]
    [InlineData("not a sid", 513)]
    public void PrimaryGroupSid_IsNull_WhenItCannotBeDerived(string accountSid, int rid)
    {
        Assert.Null(ProtectedGroupMembership.PrimaryGroupSid(accountSid, rid));
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1105", "user", true)]
    [InlineData("S-1-5-21-1-2-3-1105", "computer", true)]
    [InlineData("S-1-5-21-1-2-3-1105", null, true)]
    [InlineData("S-1-5-21-1-2-3-2001", "group", false)]
    [InlineData(null, "contact", false)]
    public void RequiresPrimaryGroup_ForEverySecurityPrincipalThatIsNotAGroup(string? sid, string? objectClass, bool expected)
    {
        Assert.Equal(expected, ProtectedGroupMembership.RequiresPrimaryGroup(new DirectoryTarget(sid, objectClass, null)));
    }

    [Theory]
    [InlineData("DomainLocal")]
    [InlineData("Global")]
    [InlineData("Universal")]
    public void ParseScope_ReadsTheModulesRendering(string text)
    {
        Assert.Equal(Enum.Parse<DirectoryGroupScope>(text), ProtectedGroupMembership.ParseScope(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Local")]
    public void ParseScope_IsNull_ForAnythingElse(string? text)
    {
        // An unclassifiable group is a failed read in the directory implementation, never a
        // guessed scope.
        Assert.Null(ProtectedGroupMembership.ParseScope(text));
    }

    [Fact]
    public void GroupContainsAnyFilter_BindsTheGroupAndTheMembers()
    {
        Assert.Equal(
            "(&(distinguishedName=CN=G,DC=b)(|(member:1.2.840.113556.1.4.1941:=CN=A,DC=a)(member:1.2.840.113556.1.4.1941:=CN=B,DC=a)))",
            ProtectedGroupMembership.GroupContainsAnyFilter("CN=G,DC=b", ["CN=A,DC=a", "CN=B,DC=a"]));
    }

    [Fact]
    public void AnyDnFilter_NamesEachObjectByItsDn_AndCarriesNoInChainRule()
    {
        // kd-88: the ancestry read asks for named objects, never for whatever contains them.
        var filter = ProtectedGroupMembership.AnyDnFilter(["CN=A,DC=a", @"CN=B\,C,DC=a"]);

        Assert.Equal(@"(|(distinguishedName=CN=A,DC=a)(distinguishedName=CN=B\5c,C,DC=a))", filter);
        Assert.DoesNotContain(ProtectedGroupMembership.InChainRule, filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Filters_EscapeLdapMetacharacters()
    {
        var filter = ProtectedGroupMembership.GroupContainsAnyFilter(@"CN=G*(1),DC=b", [@"CN=A\,B,DC=a"]);

        Assert.Contains(@"\2a", filter, StringComparison.Ordinal);
        Assert.Contains(@"\28", filter, StringComparison.Ordinal);
        Assert.Contains(@"\29", filter, StringComparison.Ordinal);
        Assert.Contains(@"\5c", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("G*(1)", filter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DistinguishedName", "(distinguishedName=CN=G,DC=b)")]
    [InlineData("Sid", "(objectSid=CN=G,DC=b)")]
    [InlineData("SamAccountName", "(sAMAccountName=CN=G,DC=b)")]
    [InlineData("CommonName", "(|(cn=CN=G,DC=b)(name=CN=G,DC=b))")]
    public void GroupLookupFilter_KeysOnTheRequestedAttribute(string key, string expected)
    {
        Assert.Equal(expected, ProtectedGroupMembership.GroupLookupFilter(Enum.Parse<GroupLookupKey>(key), "CN=G,DC=b"));
    }

    [Fact]
    public void DomainCrossRefFilter_IsLimitedToDomainCrossRefs()
    {
        // The configuration and schema crossRefs carry the forest root's dnsRoot as well; without
        // the nETBIOSName presence test the root domain's DNS name would read as ambiguous.
        Assert.Equal(
            "(&(objectClass=crossRef)(nETBIOSName=*)(|(nETBIOSName=B)(dnsRoot=B)))",
            ProtectedGroupMembership.DomainCrossRefFilter("B"));
    }

    [Fact]
    public void Batches_SplitLongDnListsAcrossQueries()
    {
        var dns = Enumerable.Range(0, 120).Select(i => $"CN=G{i},DC=a").ToList();

        var batches = ProtectedGroupMembership.Batches(dns).ToList();

        Assert.Equal([50, 50, 20], batches.Select(b => b.Count));
        Assert.Equal(dns, batches.SelectMany(b => b));
    }

    // ----- Source tripwires: the runspace implementation routes every read -----
    // The implementation needs a domain controller, so how it wires each read is pinned in
    // source, in the code view so a commented-out parameter does not satisfy it.

    private static string DirectoryCode() => ProgressScan.CodeView(File.ReadAllText(
        AuditCategoryFilingTests.FindRepoFile("Services", "PowerShellProtectedGroupDirectory.cs")));

    private static string MethodBody(string code, string signature, string nextSignature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found - tripwire is stale.");
        var end = code.IndexOf(nextSignature, start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound " + signature + " - update the tripwire.");
        return code[start..end];
    }

    [Theory]
    [InlineData("public DirectoryTarget ReadTarget(", "public string ResolveDomainServer(", "= server,")]
    [InlineData("public IReadOnlyList<DirectoryGroup> FindGroups(", "public IReadOnlyList<string> ReadTokenGroups(", ", server, new Dictionary<string, object>")]
    [InlineData("public IReadOnlyList<string> ReadTokenGroups(", "public IReadOnlyList<DirectoryMemberOf> ReadMemberOf(", "= server,")]
    [InlineData("public IReadOnlyList<DirectoryMemberOf> ReadMemberOf(", "public bool DomainLocalGroupContainsAny(", ", server, new Dictionary<string, object>")]
    [InlineData("public bool DomainLocalGroupContainsAny(", "public IReadOnlyList<string> FindObjectDnsBySid(", "= server,")]
    public void EveryRead_CarriesTheServerItWasGiven(string signature, string next, string serverAssignment)
    {
        var body = MethodBody(DirectoryCode(), signature, next);

        // The key is a string literal, which the code view blanks; the assignment survives.
        Assert.Contains(serverAssignment, body, StringComparison.Ordinal);
    }

    [Fact]
    public void AForestLookup_GoesToTheGlobalCatalog_NeverTheLocalDomain()
    {
        var body = MethodBody(DirectoryCode(),
            "public IReadOnlyList<DirectoryGroup> FindGroups(", "public IReadOnlyList<string> ReadTokenGroups(");

        Assert.Contains("RunAt(", body, StringComparison.Ordinal);

        // aud-a3c: universal links are only complete on a global catalog - kd-88's ancestry read asks it for them, by name.
        var ancestry = MethodBody(DirectoryCode(),
            "public IReadOnlyList<DirectoryMemberOf> ReadMemberOf(", "public bool DomainLocalGroupContainsAny(");
        Assert.Contains("RunAt(", ancestry, StringComparison.Ordinal);
        Assert.Contains("ProtectedGroupMembership.AnyDnFilter(batch)", ancestry, StringComparison.Ordinal);

        // rev-a3, rev-b1, rev-c1: the forest-wide form names the catalog AND the empty base; raw text, the keys are literals.
        var raw = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "PowerShellProtectedGroupDirectory.cs"));
        var runAt = raw[raw.IndexOf("private Collection<PSObject> RunAt(", StringComparison.Ordinal)..raw.IndexOf("private Collection<PSObject> Run(", StringComparison.Ordinal)];
        Assert.Contains("parameters[\"Server\"] = server ?? GlobalCatalog();", runAt, StringComparison.Ordinal);
        Assert.Contains("if (server is null)\n            parameters[\"SearchBase\"] = string.Empty;", runAt.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        // No catalog read is built any other way.
        Assert.Equal(1, raw.Split("GlobalCatalog();").Length - 1);
    }

    [Fact]
    public void TheTokenRead_IsABaseSearchAtTheAccount_BoundByItsSid()
    {
        // kd-88, in kd-87's shape: tokenGroups is constructed only in a base-scope search, so the scope is stated in the
        // command and the read is bound to the account by its SID. Raw text: the parameter names are string literals, which
        // the code view blanks.
        var raw = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "PowerShellProtectedGroupDirectory.cs"));
        var start = raw.IndexOf("public IReadOnlyList<string> ReadTokenGroups(", StringComparison.Ordinal);
        var end = raw.IndexOf("public IReadOnlyList<DirectoryMemberOf> ReadMemberOf(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Could not bound ReadTokenGroups - tripwire is stale.");
        var body = raw[start..end];

        Assert.Contains("[\"SearchBase\"] = accountDn,", body, StringComparison.Ordinal);
        Assert.Contains("[\"SearchScope\"] = \"Base\",", body, StringComparison.Ordinal);
        Assert.Contains("GroupLookupFilter(GroupLookupKey.Sid, accountSid)", body, StringComparison.Ordinal);
        Assert.Contains("[\"Properties\"] = new[] { \"tokenGroups\" },", body, StringComparison.Ordinal);
        Assert.Contains("Run(\"Get-ADObject\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTargetRead_IsClassAgnostic()
    {
        var raw = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "PowerShellProtectedGroupDirectory.cs"));
        var start = raw.IndexOf("public DirectoryTarget ReadTarget(", StringComparison.Ordinal);
        var end = raw.IndexOf("public string ResolveDomainServer(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Could not bound ReadTarget - tripwire is stale.");

        // Raw text: the cmdlet name is a string literal, which the code view blanks. The comment
        // in that method names Get-ADUser to explain why it is not used, so the needle is the call.
        Assert.Contains("Run(\"Get-ADObject\"", raw[start..end], StringComparison.Ordinal);
        Assert.DoesNotContain("Run(\"Get-ADUser\"", raw[start..end], StringComparison.Ordinal);
    }

    [Fact]
    public void AReadThatReportedAnError_IsAFailedRead()
    {
        var body = MethodBody(DirectoryCode(), "private Collection<PSObject> Run(", "private static List<DirectoryGroup> ToGroups(");

        Assert.Contains("if (_ps.HadErrors)", body, StringComparison.Ordinal);
        Assert.Contains("throw new InvalidOperationException(error);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServiceDelegatesTheDecision_AndPassesIncompleteThrough()
    {
        var code = ProgressScan.CodeView(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Services", "ProtectedPrincipalService.cs")));
        var body = MethodBody(code,
            "private (List<string> matches, bool expansionHadErrors, IReadOnlyList<string> problems) CheckTransitiveGroupMembership(",
            "internal static (string? TargetDn, bool Failed) SelectFallbackDn(");

        // docs/DirectoryObjectVerify-Plan.md B.4: the call also hands over the ignored missing group_sid rows.
        Assert.Contains("ProtectedGroupMembership.Evaluate(protectedGroups, targetDn, directory, _logger, ignoredMissing)", body, StringComparison.Ordinal);
        // ppi-s1: the ou_guid comparison shares the session, so the evaluator's incompleteness is folded into one flag
        // that is what the method returns.
        Assert.Contains("incomplete |= outcome.Incomplete;", body, StringComparison.Ordinal);
        Assert.Contains("return (matches, incomplete, problems);", body, StringComparison.Ordinal);
        // The per-group query that read "no rows" as "not a member" is gone from the service.
        Assert.DoesNotContain("inChainResult", body, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRead_CarriesItsCredential_AndNoFormWithoutOneRemains()
    {
        // aud-a60. aud-a52 gave the self-grant check a form of these reads under its session's own identity, with no
        // credential, and an identity that cannot see a member link reads it as no row - which the evaluator takes for "not a
        // member". The self-grant check reads under the protected-principal directory-read credential now, as the
        // protected-principal check does: every read passes the credential it was built with, the catalog's discovery too,
        // and nothing builds the class without one.
        using var runspace = System.Management.Automation.Runspaces.RunspaceFactory.CreateRunspace();
        runspace.Open();
        var calls = new System.Collections.ArrayList();
        runspace.SessionStateProxy.SetVariable("ReadCalls", calls);
        using (var init = System.Management.Automation.PowerShell.Create())
        {
            init.Runspace = runspace;
            init.AddScript("""
                function global:Get-ADGroup {
                    [CmdletBinding()] param($LDAPFilter, $Server, $Properties, $Credential, $SearchBase)
                    [void]$ReadCalls.Add("Get-ADGroup $Server as $($Credential.UserName)$(if ($PSBoundParameters.ContainsKey('SearchBase')) { " base '$SearchBase'" })")
                }
                function global:Get-ADForest {
                    [CmdletBinding()] param($Credential)
                    [void]$ReadCalls.Add("Get-ADForest as $($Credential.UserName)")
                    [pscustomobject]@{ GlobalCatalogs = @('gc.a.example') }
                }
                """).Invoke();
        }
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.Runspace = runspace;
        var credential = new System.Management.Automation.PSCredential("svc", new System.Net.NetworkCredential("", "secret").SecurePassword);
        var directory = new PowerShellProtectedGroupDirectory(ps, credential);

        directory.FindGroups(GroupLookupKey.Sid, $"{SidA}-512", DomainA);
        directory.FindGroups(GroupLookupKey.Sid, $"{SidA}-512", null);

        Assert.Equal([$"Get-ADGroup {DomainA} as svc", "Get-ADForest as svc", "Get-ADGroup gc.a.example:3268 as svc base ''"], calls.Cast<string>());
        const System.Reflection.BindingFlags Every = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        var type = typeof(PowerShellProtectedGroupDirectory);
        Assert.All(type.GetConstructors(Every & ~System.Reflection.BindingFlags.Static), constructor =>
            Assert.Contains(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(System.Management.Automation.PSCredential)));
        Assert.DoesNotContain(type.GetMethods(Every), method => method.IsStatic && method.ReturnType == type);
    }
}
