using ExchangeAdminWeb.Authorization;

namespace ExchangeAdminWeb.Tests;

public class GroupMembershipCheckerTests
{
    [Fact]
    public void MatchesBareGroupName_CaseInsensitive()
    {
        Assert.True(GroupMembershipChecker.IsMemberOfAny(["confroomadmins"], ["ConfRoomAdmins"]));
        Assert.True(GroupMembershipChecker.IsMemberOfAny(["ConfRoomAdmins"], ["confroomadmins"]));
    }

    [Fact]
    public void MatchesDomainQualifiedAllowedGroup_AgainstBareClaim()
    {
        // Allowed value "CORP\ConfRoomAdmins" must match a bare "ConfRoomAdmins" claim (handler parity).
        Assert.True(GroupMembershipChecker.IsMemberOfAny(["ConfRoomAdmins"], [@"CORP\ConfRoomAdmins"]));
    }

    [Fact]
    public void MatchesFullDomainQualifiedClaim_AgainstDomainQualifiedAllowed()
    {
        Assert.True(GroupMembershipChecker.IsMemberOfAny([@"CORP\ConfRoomAdmins"], [@"CORP\ConfRoomAdmins"]));
    }

    [Fact]
    public void NoMatch_WhenNoOverlap()
    {
        Assert.False(GroupMembershipChecker.IsMemberOfAny(["SomeOtherGroup"], ["ConfRoomAdmins", "Admins"]));
    }

    [Fact]
    public void EmptyAllowedGroups_FailsClosed()
    {
        Assert.False(GroupMembershipChecker.IsMemberOfAny(["ConfRoomAdmins"], []));
    }

    [Fact]
    public void EmptyClaims_FailsClosed()
    {
        Assert.False(GroupMembershipChecker.IsMemberOfAny([], ["ConfRoomAdmins"]));
    }

    [Fact]
    public void NullInputs_FailClosed()
    {
        Assert.False(GroupMembershipChecker.IsMemberOfAny(null, ["ConfRoomAdmins"]));
        Assert.False(GroupMembershipChecker.IsMemberOfAny(["ConfRoomAdmins"], null));
    }

    [Fact]
    public void BlankAllowedGroupEntries_AreIgnored()
    {
        Assert.False(GroupMembershipChecker.IsMemberOfAny(["ConfRoomAdmins"], ["", "   "]));
        Assert.True(GroupMembershipChecker.IsMemberOfAny(["ConfRoomAdmins"], ["", "ConfRoomAdmins"]));
    }
}
