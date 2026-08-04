using System.Security.Claims;
using ExchangeAdminWeb.Authorization;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Slice 3 of docs/SectionAccessSidStorage-Plan.md. These tests previously asserted that a stored
/// <c>DOMAIN\group</c> matched a bare <c>group</c> claim. That behavior WAS the defect - it made
/// two same-named groups in different domains indistinguishable in the field deciding who reaches
/// a privileged module - so those assertions are inverted here rather than retained.
/// </summary>
public class GroupMembershipCheckerTests
{
    private const string DomainSid = "S-1-5-21-8915387-325452579-1788637320";
    private const string IamSid = DomainSid + "-586078";
    private const string OtherSid = DomainSid + "-677335";
    // Same RID, different domain: what a name comparison cannot tell apart and a SID can.
    private const string ForeignIamSid = "S-1-5-21-725345543-2052111302-839522115-586078";

    [Fact]
    public void MatchesSid_CaseInsensitive()
    {
        Assert.True(GroupMembershipChecker.IsMemberOfAny([IamSid], [IamSid]));
        Assert.True(GroupMembershipChecker.IsMemberOfAny([IamSid.ToLowerInvariant()], [IamSid]));
    }

    [Fact]
    public void ForeignDomainSidWithTheSameRid_DoesNotMatch()
    {
        // The point of the whole work stream. Under name comparison both of these were
        // "ExchangeWebAdmins" and matched; as SIDs they are different principals.
        Assert.False(GroupMembershipChecker.IsMemberOfAny([ForeignIamSid], [IamSid]));
    }

    [Fact]
    public void BareNameClaim_DoesNotMatchADomainQualifiedAllowedValue()
    {
        // Was asserted TRUE before this change. The old normalization stripped "CORP\" and matched
        // any bare "ConfRoomAdmins" from any trusted domain.
        Assert.False(GroupMembershipChecker.IsMemberOfAny(["ConfRoomAdmins"], [@"CORP\ConfRoomAdmins"]));
    }

    [Fact]
    public void BlankAllowedValuesAreIgnored()
    {
        Assert.False(GroupMembershipChecker.IsMemberOfAny([IamSid], ["", "   "]));
    }

    [Theory]
    [InlineData("IAM")]
    [InlineData(@"ANALOG\IAM")]
    [InlineData(@"CORP\ConfRoomAdmins")]
    [InlineData("S-1-5-32-544")]  // well-known: unambiguous but far too broad
    public void UsableSidsOnly_DiscardsNonSids(string allowed)
    {
        // The sid-1 rule, now asserted on the FILTER rather than on IsMemberOfAny. Exact
        // comparison alone does not make an unmigrated store fail closed: a role claim can carry
        // a name, and IsInRole resolves names as well as SIDs, so a name-valued row would keep
        // authorizing - with the same-name ambiguity intact - during exactly the window the
        // migration is designed to survive.
        Assert.Empty(GroupMembershipChecker.UsableSidsOnly([allowed]));
    }

    [Fact]
    public void IsMemberOfAny_DoesNotFilter_SoStaticAppsettingsGroupsStillWork()
    {
        // Review finding sidf-1. Filtering INSIDE this function applied the SID rule to
        // Security:AdminGroups too - appsettings values no migration converts, deployed here as
        // "ANALOG\ExchangeWebAdmins" - which locked admins out of the admin page. The filter
        // therefore belongs at the caller, which knows which store the values came from.
        Assert.True(GroupMembershipChecker.IsMemberOfAny(
            [@"ANALOG\ExchangeWebAdmins"], [@"ANALOG\ExchangeWebAdmins"]));
    }

    [Fact]
    public void UnmigratedRowsAreDroppedButSidsBesideThemStillMatch()
    {
        // A partially-migrated section must not be all-or-nothing at authorization time: the rows
        // that did convert keep working. Filter then compare, as the handler's dynamic path does.
        var allowed = GroupMembershipChecker.UsableSidsOnly(["IAM", IamSid]);

        Assert.True(GroupMembershipChecker.IsMemberOfAny([IamSid], allowed));
        Assert.False(GroupMembershipChecker.IsMemberOfAny(["IAM"], allowed));
    }

    [Fact]
    public void UsableSidsOnly_KeepsSidsAndDropsTheRest()
    {
        var filtered = GroupMembershipChecker.UsableSidsOnly([IamSid, "IAM", @"ANALOG\IAM", "", "S-1-1-0"]);

        Assert.Equal([IamSid], filtered);
    }

    [Fact]
    public void UsableSidsOnly_HandlesNull()
        => Assert.Empty(GroupMembershipChecker.UsableSidsOnly(null));

    [Fact]
    public void NoMatch_WhenNoOverlap()
    {
        Assert.False(GroupMembershipChecker.IsMemberOfAny([OtherSid], [IamSid, DomainSid + "-1"]));
    }

    [Fact]
    public void EmptyAllowedGroups_FailsClosed()
    {
        Assert.False(GroupMembershipChecker.IsMemberOfAny([IamSid], []));
    }

    [Fact]
    public void EmptyClaims_FailsClosed()
    {
        Assert.False(GroupMembershipChecker.IsMemberOfAny([], [IamSid]));
    }

    [Fact]
    public void NullInputs_FailClosed()
    {
        Assert.False(GroupMembershipChecker.IsMemberOfAny(null, [IamSid]));
        Assert.False(GroupMembershipChecker.IsMemberOfAny([IamSid], null));
    }

    [Fact]
    public void BlankAllowedGroupEntries_AreIgnored()
    {
        Assert.True(GroupMembershipChecker.IsMemberOfAny([IamSid], ["", IamSid]));
    }

    // ---------------------------------------------------------------- Claim extraction

    [Fact]
    public void ExtractsGroupSidClaims()
    {
        // The claim type a Negotiate token actually populates - 333 of them on this deployment.
        var user = PrincipalWith((ClaimTypes.GroupSid, IamSid), (ClaimTypes.GroupSid, OtherSid));

        var claims = GroupMembershipChecker.ExtractGroupClaims(user);

        Assert.Equal(2, claims.Count);
        Assert.Contains(IamSid, claims);
    }

    [Fact]
    public void ExtractsPrimaryGroupSid()
    {
        var user = PrincipalWith((ClaimTypes.PrimaryGroupSid, IamSid));

        Assert.Contains(IamSid, GroupMembershipChecker.ExtractGroupClaims(user));
    }

    [Fact]
    public void StillExtractsRoleClaims_ForSnapshotsCapturedByAnOlderBuild()
    {
        var user = PrincipalWith((ClaimTypes.Role, IamSid));

        Assert.Contains(IamSid, GroupMembershipChecker.ExtractGroupClaims(user));
    }

    [Fact]
    public void IgnoresUnrelatedClaimTypes()
    {
        // A name claim must never be read as a group claim.
        var user = PrincipalWith((ClaimTypes.Name, @"ANALOG\jdoe"), (ClaimTypes.Email, "j@x.com"));

        Assert.Empty(GroupMembershipChecker.ExtractGroupClaims(user));
    }

    [Fact]
    public void DeduplicatesAndDropsBlanks()
    {
        var user = PrincipalWith(
            (ClaimTypes.GroupSid, IamSid),
            (ClaimTypes.Role, IamSid),
            (ClaimTypes.GroupSid, "  "));

        Assert.Single(GroupMembershipChecker.ExtractGroupClaims(user));
    }

    [Fact]
    public void NullPrincipalYieldsNoClaims()
    {
        Assert.Empty(GroupMembershipChecker.ExtractGroupClaims(null));
    }

    private static ClaimsPrincipal PrincipalWith(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Negotiate"));
}
