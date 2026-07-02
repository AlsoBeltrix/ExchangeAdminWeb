using System.Security.Claims;
using ExchangeAdminWeb.Services.Jobs;

namespace ExchangeAdminWeb.Tests;

public class JobAuthorizationSnapshotTests
{
    private static ClaimsPrincipal PrincipalWithRoles(params string[] roles)
    {
        var identity = new ClaimsIdentity("test");
        foreach (var r in roles)
            identity.AddClaim(new Claim(ClaimTypes.Role, r));
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void Capture_RecordsSatisfiedAllowedGroups_NotRawClaims()
    {
        // The user has claim "ConfRoomAdmins"; allowed groups are ConfRoomAdmins + Admins. The
        // captured decision records only the group the user actually satisfied.
        var snap = JobAuthorizationSnapshot.Capture(
            PrincipalWithRoles("ConfRoomAdmins", "OtherGroup"), "ConferenceRooms", ["ConfRoomAdmins", "Admins"]);

        Assert.Equal("ConferenceRooms", snap.Section);
        Assert.Equal(["ConfRoomAdmins"], snap.AuthorizedGroups);
        Assert.Contains("ConfRoomAdmins", snap.RoleClaims);
    }

    [Fact]
    public void Capture_MatchesViaIsInRole_WhenClaimsDoNotContainGroup()
    {
        // Simulates the Windows-auth case: no role claim matches the configured group name, but the
        // principal answers IsInRole(group) true. The snapshot must still capture authorization.
        var identity = new ClaimsIdentity("test");
        var principal = new IsInRolePrincipal(identity, "CORP\\ConfRoomAdmins");

        var snap = JobAuthorizationSnapshot.Capture(principal, "ConferenceRooms", ["CORP\\ConfRoomAdmins"]);

        Assert.Equal(["CORP\\ConfRoomAdmins"], snap.AuthorizedGroups);
        Assert.True(snap.IsStillAuthorized(["CORP\\ConfRoomAdmins"]));
    }

    [Fact]
    public void IsStillAuthorized_TrueWhenCapturedGroupStillAllowed()
    {
        var snap = JobAuthorizationSnapshot.Capture(PrincipalWithRoles("ConfRoomAdmins"), "ConferenceRooms", ["ConfRoomAdmins", "Admins"]);
        Assert.True(snap.IsStillAuthorized(["ConfRoomAdmins", "Admins"]));
    }

    [Fact]
    public void IsStillAuthorized_FalseWhenCapturedGroupRemovedFromConfig()
    {
        // Captured authorized via ConfRoomAdmins; if that group is later removed from the section's
        // allowed set, the job is no longer authorized (fail closed).
        var snap = JobAuthorizationSnapshot.Capture(PrincipalWithRoles("ConfRoomAdmins"), "ConferenceRooms", ["ConfRoomAdmins"]);
        Assert.False(snap.IsStillAuthorized(["SomeDifferentGroup"]));
    }

    [Fact]
    public void IsStillAuthorized_FalseWhenNothingCaptured()
    {
        var snap = JobAuthorizationSnapshot.Capture(PrincipalWithRoles("SomeOtherGroup"), "ConferenceRooms", ["ConfRoomAdmins"]);
        Assert.Empty(snap.AuthorizedGroups);
        Assert.False(snap.IsStillAuthorized(["ConfRoomAdmins"]));
    }

    [Fact]
    public void IsStillAuthorized_FailsClosedOnEmptyAllowedGroups()
    {
        var snap = JobAuthorizationSnapshot.Capture(PrincipalWithRoles("ConfRoomAdmins"), "ConferenceRooms", ["ConfRoomAdmins"]);
        Assert.False(snap.IsStillAuthorized([]));
    }

    [Fact]
    public void JsonRoundTrip_PreservesSectionAndDecision()
    {
        var original = JobAuthorizationSnapshot.Capture(PrincipalWithRoles("ConfRoomAdmins", "Admins"), "ConferenceRooms", ["ConfRoomAdmins", "Admins"]);
        var restored = JobAuthorizationSnapshot.FromJson(original.ToJson());

        Assert.NotNull(restored);
        Assert.Equal("ConferenceRooms", restored!.Section);
        Assert.Equal(original.AuthorizedGroups.OrderBy(x => x), restored.AuthorizedGroups.OrderBy(x => x));
        Assert.True(restored.IsStillAuthorized(["ConfRoomAdmins"]));
    }

    // A principal that returns IsInRole(true) only for one specific role, with no role claims.
    private sealed class IsInRolePrincipal : ClaimsPrincipal
    {
        private readonly string _role;
        public IsInRolePrincipal(ClaimsIdentity identity, string role) : base(identity) => _role = role;
        public override bool IsInRole(string role) => string.Equals(role, _role, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromJson_NullOrInvalid_ReturnsNull()
    {
        Assert.Null(JobAuthorizationSnapshot.FromJson(null));
        Assert.Null(JobAuthorizationSnapshot.FromJson(""));
        Assert.Null(JobAuthorizationSnapshot.FromJson("{not valid json"));
    }
}
