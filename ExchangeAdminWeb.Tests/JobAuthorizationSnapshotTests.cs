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
    public void Capture_TakesRoleClaimsAndSection()
    {
        var snap = JobAuthorizationSnapshot.Capture(PrincipalWithRoles("ConfRoomAdmins", "OtherGroup"), "ConferenceRooms");

        Assert.Equal("ConferenceRooms", snap.Section);
        Assert.Contains("ConfRoomAdmins", snap.RoleClaims);
        Assert.Contains("OtherGroup", snap.RoleClaims);
    }

    [Fact]
    public void Capture_DeduplicatesRoleClaims()
    {
        var snap = JobAuthorizationSnapshot.Capture(PrincipalWithRoles("ConfRoomAdmins", "ConfRoomAdmins"), "ConferenceRooms");
        Assert.Single(snap.RoleClaims);
    }

    [Fact]
    public void IsStillAuthorized_TrueWhenSnapshotMatchesAllowedGroups()
    {
        var snap = JobAuthorizationSnapshot.Capture(PrincipalWithRoles("ConfRoomAdmins"), "ConferenceRooms");
        Assert.True(snap.IsStillAuthorized(["ConfRoomAdmins", "Admins"]));
    }

    [Fact]
    public void IsStillAuthorized_FalseWhenSnapshotLacksAccess()
    {
        var snap = JobAuthorizationSnapshot.Capture(PrincipalWithRoles("SomeOtherGroup"), "ConferenceRooms");
        Assert.False(snap.IsStillAuthorized(["ConfRoomAdmins"]));
    }

    [Fact]
    public void IsStillAuthorized_FailsClosedOnEmptyAllowedGroups()
    {
        var snap = JobAuthorizationSnapshot.Capture(PrincipalWithRoles("ConfRoomAdmins"), "ConferenceRooms");
        Assert.False(snap.IsStillAuthorized([]));
    }

    [Fact]
    public void JsonRoundTrip_PreservesSectionAndClaims()
    {
        var original = JobAuthorizationSnapshot.Capture(PrincipalWithRoles("ConfRoomAdmins", "Admins"), "ConferenceRooms");
        var restored = JobAuthorizationSnapshot.FromJson(original.ToJson());

        Assert.NotNull(restored);
        Assert.Equal("ConferenceRooms", restored!.Section);
        Assert.Equal(original.RoleClaims.OrderBy(x => x), restored.RoleClaims.OrderBy(x => x));
        Assert.True(restored.IsStillAuthorized(["ConfRoomAdmins"]));
    }

    [Fact]
    public void FromJson_NullOrInvalid_ReturnsNull()
    {
        Assert.Null(JobAuthorizationSnapshot.FromJson(null));
        Assert.Null(JobAuthorizationSnapshot.FromJson(""));
        Assert.Null(JobAuthorizationSnapshot.FromJson("{not valid json"));
    }
}
