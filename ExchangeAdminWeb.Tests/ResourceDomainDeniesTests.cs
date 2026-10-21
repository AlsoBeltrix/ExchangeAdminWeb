using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.SelfServiceGroups;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a4b finding 7: the self-service DACL gate evaluated the group's ACL against the caller's
/// token as the web server received it, which need not carry the domain-local groups of the
/// group's own domain. A Deny carried by such a group was skipped while an individual Allow
/// granted. Driven against the shared <see cref="FakeForest"/>, whose DCs hold only their own
/// domain's links, so the domain-local membership is genuinely absent from the caller's domain.
/// </summary>
public class ResourceDomainDeniesTests
{
    private const string SidA = "S-1-5-21-100-200-300";
    private const string SidB = "S-1-5-21-400-500-600";

    private const string Tess = "CN=Tess,OU=People,DC=a,DC=example";
    private const string Uma = "CN=Uma,OU=People,DC=a,DC=example";
    private const string TessSid = SidA + "-1105";
    private const string UmaSid = SidA + "-1106";
    private const string DomainUsersSid = SidA + "-513";
    private const string StaffSid = SidA + "-2001";
    private const string ContractorsSid = SidB + "-3001";
    private const string AccountOperatorsSid = "S-1-5-32-548";

    private const string StaffA = "CN=Staff,OU=Groups,DC=a,DC=example";
    private const string ContractorsB = "CN=Contractors,OU=Groups,DC=b,DC=example";

    /// <summary>The group whose DACL is being evaluated. It lives in domain B.</summary>
    private const string ProjectGroupB = "CN=Project,OU=Groups,DC=b,DC=example";

    private const int WriteProperty = 0x00000020;
    private static readonly Guid Member = GroupMembershipAce.MemberAttribute;

    /// <summary>
    /// Tess (A) is in A\Staff, which is in B's DOMAIN-LOCAL Contractors. Her token, issued for the
    /// web server, carries A's groups and not B's domain-local ones. Uma is in neither.
    /// </summary>
    private static FakeForest Forest()
    {
        var f = new FakeForest().Domain("A", "a.example").Domain("B", "b.example");
        f.Account(Tess, TessSid, primaryGroupRid: 513);
        f.Account(Uma, UmaSid, primaryGroupRid: 513);
        f.Group("CN=Domain Users,CN=Users,DC=a,DC=example", DomainUsersSid, DirectoryGroupScope.Global, "Domain Users");
        f.Group(StaffA, StaffSid, DirectoryGroupScope.Global, "Staff", Tess);
        f.Group(ContractorsB, ContractorsSid, DirectoryGroupScope.DomainLocal, "Contractors", StaffA);
        f.Group(ProjectGroupB, SidB + "-3100", DirectoryGroupScope.Global, "Project");
        return f;
    }

    private static GroupMembershipAce.Entry Allow(string sid, bool inherited = false)
        => new(sid, IsDeny: false, WriteProperty, Member, IsInherited: inherited, InheritOnly: false);

    private static GroupMembershipAce.Entry Deny(string sid, bool inherited = false)
        => new(sid, IsDeny: true, WriteProperty, Member, IsInherited: inherited, InheritOnly: false);

    /// <summary>The account-domain token the web server sees for Tess: her own groups, no B domain-local ones.</summary>
    private static readonly string[] TessToken = [DomainUsersSid, StaffSid];
    private static readonly string[] UmaToken = [DomainUsersSid];

    private static bool Decide(FakeForest forest, string callerSid, string[] token, params GroupMembershipAce.Entry[] dacl)
        => ResourceDomainDenies.CallerMayWriteMembers(
            dacl, callerSid, token, Array.Empty<string>(), ProjectGroupB, forest, NullLogger.Instance);

    [Fact]
    public void ADenyCarriedByTheGroupsOwnDomainLocalGroup_RefusesTheCaller()
    {
        // The finding. The token alone grants: Contractors is not in it, so its Deny is skipped
        // and Tess's individual Allow decides. Windows, evaluating on B's DC, includes B's
        // domain-local groups and refuses.
        var dacl = new[] { Deny(ContractorsSid), Allow(TessSid) };
        Assert.True(GroupMembershipAce.GrantsMemberWrite(dacl, TessSid, TessToken));

        Assert.False(Decide(Forest(), TessSid, TessToken, dacl));
    }

    [Fact]
    public void TheSameDeny_DoesNotRefuseSomeoneOutsideTheGroup()
    {
        // The opposite direction, so the refusal above cannot be passing by refusing everyone
        // the moment a Deny names an unknown group.
        var forest = Forest();

        Assert.True(Decide(forest, UmaSid, UmaToken, Deny(ContractorsSid), Allow(UmaSid)));
        Assert.Contains($"FindObjectDnsBySid {UmaSid}", forest.Calls);
    }

    [Fact]
    public void ADenyTheTokenAlreadyCarries_IsDecidedWithoutTheDirectory()
    {
        var forest = Forest();

        Assert.False(Decide(forest, TessSid, TessToken, Deny(StaffSid), Allow(TessSid)));
        Assert.Empty(forest.Calls);
    }

    [Fact]
    public void AnUnknownInheritedDenyBelowAnExplicitAllow_DoesNotNeedTheDirectory()
    {
        // Canonical order decides the explicit Allow first; no Deny below it can change the
        // answer, so nothing is read - and nothing can fail.
        var forest = Forest();

        Assert.True(Decide(forest, TessSid, TessToken, Deny(ContractorsSid, inherited: true), Allow(TessSid)));
        Assert.Empty(forest.Calls);
    }

    [Fact]
    public void ADenyForAnotherAccount_DoesNotRefuse()
    {
        // A trustee that is not a group cannot contain the caller: Uma's Deny is Uma's.
        Assert.True(Decide(Forest(), TessSid, TessToken, Deny(UmaSid), Allow(TessSid)));
    }

    [Fact]
    public void ABuiltinAlias_IsResolvedInTheGroupsOwnDomain()
    {
        // BUILTIN aliases share one SID in every domain; the one this DACL means is B's. A's copy
        // does not contain Tess, B's does - resolving forest-wide would be ambiguous, resolving in
        // A would miss.
        var forest = Forest();
        forest.Group("CN=Account Operators,CN=Builtin,DC=a,DC=example", AccountOperatorsSid, DirectoryGroupScope.DomainLocal, "Account Operators");
        forest.Group("CN=Account Operators,CN=Builtin,DC=b,DC=example", AccountOperatorsSid, DirectoryGroupScope.DomainLocal, "Account Operators", StaffA);

        Assert.False(Decide(forest, TessSid, TessToken, Deny(AccountOperatorsSid), Allow(TessSid)));
        Assert.Contains($"FindGroups Sid {AccountOperatorsSid} b.example", forest.Calls);
    }

    [Theory]
    [InlineData("FindGroups")]
    [InlineData("FindObjectDnsBySid")]
    [InlineData("ReadTarget")]
    [InlineData("UniversalGroupsContainingAny")]
    [InlineData("DomainLocalGroupContainsAny")]
    public void AReadThatFails_Refuses(string failingRead)
    {
        var forest = Forest();
        forest.FailOn = failingRead;

        Assert.False(Decide(forest, UmaSid, UmaToken, Deny(ContractorsSid), Allow(UmaSid)));
    }

    [Fact]
    public void ACallerWhoCannotBeFound_Refuses()
    {
        Assert.False(Decide(Forest(), SidA + "-9999", UmaToken, Deny(ContractorsSid), Allow(SidA + "-9999")));
    }

    [Fact]
    public void AMembershipFoundInTheDirectory_CanOnlyDeny_EvenWhereItsAllowIsDecidedFirst()
    {
        // B\Contractors has an EXPLICIT Allow and an INHERITED Deny; Tess's own Allow is
        // inherited. The token grants through her inherited Allow. Her Contractors membership,
        // found in the directory, must make the inherited Deny apply - and must NOT satisfy the
        // explicit Allow, which canonical order would reach first and which would grant.
        var dacl = new[] { Allow(ContractorsSid), Deny(ContractorsSid, inherited: true), Allow(TessSid, inherited: true) };

        Assert.False(Decide(Forest(), TessSid, TessToken, dacl));
    }

    [Fact]
    public void AnAllowCarriedByTheGroupsOwnDomainLocalGroup_IsStillNotHonoured()
    {
        // The conservative half, recorded as a known gap: the directory is read only to settle
        // Denies, so a grant that exists only through B's domain-local group is refused as before.
        Assert.False(Decide(Forest(), TessSid, TessToken, Allow(ContractorsSid)));
    }

    [Fact]
    public void TheServiceGate_RoutesThroughTheResourceDomainDecision()
    {
        // CallerCanManageMembers reads the DACL through a domain controller, so how it reaches
        // the decision is pinned in source - code view, so a commented-out call cannot satisfy it.
        var code = ProgressScan.CodeView(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "SelfServiceGroups", "SelfServiceGroupService.cs")));
        var start = code.IndexOf("private bool CallerCanManageMembers(", StringComparison.Ordinal);
        Assert.True(start >= 0, "CallerCanManageMembers not found - tripwire is stale.");
        var end = code.IndexOf("private static List<string> CollectOwnerDns(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound CallerCanManageMembers - update the tripwire.");
        var body = code[start..end];

        Assert.Contains("return ResourceDomainDenies.CallerMayWriteMembers(", body, StringComparison.Ordinal);
        Assert.Contains("new PowerShellProtectedGroupDirectory(ps, credential)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("return GroupMembershipAce.GrantsMemberWrite(", body, StringComparison.Ordinal);
    }
}
