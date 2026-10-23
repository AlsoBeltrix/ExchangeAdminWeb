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

    // ----- Hole 3: the resolver -----

    [Fact]
    public void DomainQualifiedEntry_ResolvesInTheNamedDomain_NotAnother()
    {
        // The finding: "B\Privileged" had its domain stripped and resolved as A\Privileged,
        // which Tess is not in, so a target protected through B's group passed.
        var forest = Forest();

        var result = Evaluate(forest, Tess, @"B\Privileged");

        Assert.Equal([@"Group:B\Privileged"], result.Matches);
        Assert.False(result.Incomplete);
        Assert.Contains($"FindGroups SamAccountName Privileged {DomainB}", forest.Calls);
    }

    [Fact]
    public void DomainQualifiedEntry_ForTheOtherDomain_SelectsThatDomainsGroup()
    {
        // The control for the test above: the same name, the other domain, a group Tess is not
        // in. Proves the domain half SELECTS rather than being ignored either way.
        var result = Evaluate(Forest(), Tess, @"A\Privileged");

        Assert.Empty(result.Matches);
        Assert.False(result.Incomplete);
    }

    [Fact]
    public void DomainQualifiedEntry_NamingNoDomainOfTheForest_FailsClosed()
    {
        var result = Evaluate(Forest(), Tess, @"NOWHERE\Privileged");

        Assert.Empty(result.Matches);
        Assert.True(result.Incomplete);
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
    public void BareName_SharedByTwoDomains_FailsClosed()
    {
        // No domain named, so none is assumed. Resolving to whichever domain answered first is
        // the same defect as stripping the domain half.
        var result = Evaluate(Forest(), Tess, "Privileged");

        Assert.Empty(result.Matches);
        Assert.True(result.Incomplete);
    }

    [Fact]
    public void BareName_UniqueInTheForest_IsResolvedForestWide()
    {
        var forest = Forest();

        Assert.Single(Evaluate(forest, Tess, "Protected VIPs").Matches);
        Assert.Contains("FindGroups SamAccountName Protected VIPs forest", forest.Calls);
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
        Assert.Contains($"GroupsContainingAny {DomainA}", forest.Calls);
    }

    // ----- aud-a3c: universal groups are read from the global catalog -----

    private const string ExecutivesB = "CN=Executives,OU=Groups,DC=b,DC=example";

    [Fact]
    public void UniversalGroupOfAnotherDomain_ProtectsThroughAGlobalGroup()
    {
        // aud-a3c. The universal group's link to A\Staff lives in B and in the global catalog,
        // not in A. The previous query asked A, so it saw this only when A's DC happened to be a
        // GC - which the fake's DCs are not.
        var forest = Forest();
        forest.Group(ExecutivesB, $"{SidB}-3030", DirectoryGroupScope.Universal, "Executives", StaffA);

        var result = Evaluate(forest, Tess, ExecutivesB);

        Assert.Equal(["Group:" + ExecutivesB], result.Matches);
        Assert.False(result.Incomplete);
        Assert.Contains("UniversalGroupsContainingAny forest", forest.Calls);
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
        forest.FailOn = "UniversalGroupsContainingAny";

        var result = Evaluate(forest, Tess, ExecutivesB);

        Assert.Empty(result.Matches);
        Assert.True(result.Incomplete);
    }

    // ----- Fail closed on every read -----

    [Theory]
    [InlineData("ReadTarget")]
    [InlineData("GroupsContainingAny")]
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
        var result = Evaluate(Forest(), Tess, "Privileged", StaffA);

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
    public void ContainsAnyFilter_IsAnInChainOrOverTheMemberAttribute()
    {
        Assert.Equal(
            "(|(member:1.2.840.113556.1.4.1941:=CN=A,DC=x)(member:1.2.840.113556.1.4.1941:=CN=B,DC=x))",
            ProtectedGroupMembership.ContainsAnyFilter(["CN=A,DC=x", "CN=B,DC=x"]));
    }

    [Fact]
    public void GroupContainsAnyFilter_BindsTheGroupAndTheMembers()
    {
        Assert.Equal(
            "(&(distinguishedName=CN=G,DC=b)(|(member:1.2.840.113556.1.4.1941:=CN=A,DC=a)))",
            ProtectedGroupMembership.GroupContainsAnyFilter("CN=G,DC=b", ["CN=A,DC=a"]));
    }

    [Fact]
    public void UniversalContainsAnyFilter_IsLimitedToUniversalGroups()
    {
        // The GC also holds its own domain's non-universal groups in full; the result is limited
        // to universal groups because that is the only scope the GC is complete for forest-wide.
        Assert.Equal(
            "(&(groupType:1.2.840.113556.1.4.803:=8)(|(member:1.2.840.113556.1.4.1941:=CN=A,DC=a)))",
            ProtectedGroupMembership.UniversalContainsAnyFilter(["CN=A,DC=a"]));
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
    [InlineData("public IReadOnlyList<DirectoryGroup> FindGroups(", "public IReadOnlyList<DirectoryGroup> GroupsContainingAny(", "= target,")]
    [InlineData("public IReadOnlyList<DirectoryGroup> GroupsContainingAny(", "public bool DomainLocalGroupContainsAny(", "= server,")]
    [InlineData("public bool DomainLocalGroupContainsAny(", "public IReadOnlyList<DirectoryGroup> UniversalGroupsContainingAny(", "= server,")]
    [InlineData("public IReadOnlyList<DirectoryGroup> UniversalGroupsContainingAny(", "private string GlobalCatalog(", "= catalog,")]
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
            "public IReadOnlyList<DirectoryGroup> FindGroups(", "public IReadOnlyList<DirectoryGroup> GroupsContainingAny(");

        Assert.Contains("var target = server ?? GlobalCatalog();", body, StringComparison.Ordinal);

        // aud-a3c: the universal ancestry is only complete on a global catalog.
        var universal = MethodBody(DirectoryCode(),
            "public IReadOnlyList<DirectoryGroup> UniversalGroupsContainingAny(", "private string GlobalCatalog(");
        Assert.Contains("var catalog = GlobalCatalog();", universal, StringComparison.Ordinal);
        Assert.Contains("ProtectedGroupMembership.UniversalContainsAnyFilter(batch)", universal, StringComparison.Ordinal);
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
            "private (List<string> matches, bool expansionHadErrors) CheckTransitiveGroupMembership(",
            "internal static (string? TargetDn, bool Failed) SelectFallbackDn(");

        Assert.Contains("ProtectedGroupMembership.Evaluate(protectedGroups, targetDn, directory, _logger)", body, StringComparison.Ordinal);
        Assert.Contains("return (matches, outcome.Incomplete);", body, StringComparison.Ordinal);
        // The per-group query that read "no rows" as "not a member" is gone from the service.
        Assert.DoesNotContain("inChainResult", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCredentialedReads_KeepTheirCredential_AndTheSessionIdentityReads_CarryNone()
    {
        // aud-a52. The self-grant check runs the same reads under its own session's identity, with the catalog that session
        // found; the protected-principal check's reads, built with its credential, still pass it on every read.
        using var runspace = System.Management.Automation.Runspaces.RunspaceFactory.CreateRunspace();
        runspace.Open();
        var calls = new System.Collections.ArrayList();
        runspace.SessionStateProxy.SetVariable("ReadCalls", calls);
        using (var init = System.Management.Automation.PowerShell.Create())
        {
            init.Runspace = runspace;
            init.AddScript("""
                function global:Get-ADGroup {
                    [CmdletBinding()] param($LDAPFilter, $Server, $Properties, $Credential)
                    [void]$ReadCalls.Add("$Server credential=$($PSBoundParameters.ContainsKey('Credential'))")
                }
                """).Invoke();
        }
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.Runspace = runspace;
        var credential = new System.Management.Automation.PSCredential("svc", new System.Net.NetworkCredential("", "secret").SecurePassword);

        new PowerShellProtectedGroupDirectory(ps, credential).FindGroups(GroupLookupKey.Sid, $"{SidA}-512", DomainA);
        PowerShellProtectedGroupDirectory.UnderSessionIdentity(ps, "gc.a.example:3268").FindGroups(GroupLookupKey.Sid, $"{SidA}-512", null);

        Assert.Equal([$"{DomainA} credential=True", "gc.a.example:3268 credential=False"], calls.Cast<string>());
        Assert.Throws<ArgumentException>(() => PowerShellProtectedGroupDirectory.UnderSessionIdentity(ps, " "));
    }
}
