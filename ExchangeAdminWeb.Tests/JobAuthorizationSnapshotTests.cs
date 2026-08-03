using System.Security.Claims;
using ExchangeAdminWeb.Services.Jobs;

namespace ExchangeAdminWeb.Tests;

public class JobAuthorizationSnapshotTests
{
    // Allowed values are group SIDs (docs/SectionAccessSidStorage-Plan.md). These fixtures were
    // group names until review finding sid-1: a name is no longer a usable authorization subject,
    // so a name-valued fixture now proves only that the filter works, not that capture does.
    private const string DomainSid = "S-1-5-21-8915387-325452579-1788637320";
    private const string ConfRoomAdminsSid = DomainSid + "-651293";
    private const string AdminsSid = DomainSid + "-677335";
    private const string OtherGroupSid = DomainSid + "-586078";
    private const string SomeDifferentGroupSid = DomainSid + "-47375";
    private const string CorpConfRoomAdminsSid = "S-1-5-21-725345543-2052111302-839522115-651293";

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
        // The user carries the ConfRoomAdmins SID; two groups are allowed. The captured decision
        // records only the group the user actually satisfied.
        var snap = JobAuthorizationSnapshot.Capture(
            PrincipalWithRoles(ConfRoomAdminsSid, OtherGroupSid), "ConferenceRooms", [ConfRoomAdminsSid, AdminsSid]);

        Assert.Equal("ConferenceRooms", snap.Section);
        Assert.Equal([ConfRoomAdminsSid], snap.AuthorizedGroups);
        Assert.Contains(ConfRoomAdminsSid, snap.RoleClaims);
    }

    [Fact]
    public void Capture_MatchesViaIsInRole_WhenClaimsDoNotContainGroup()
    {
        // Simulates the Windows-auth case: no claim matches the configured group, but the
        // principal answers IsInRole(group) true. The snapshot must still capture authorization.
        var identity = new ClaimsIdentity("test");
        var principal = new IsInRolePrincipal(identity, CorpConfRoomAdminsSid);

        var snap = JobAuthorizationSnapshot.Capture(principal, "ConferenceRooms", [CorpConfRoomAdminsSid]);

        Assert.Equal([CorpConfRoomAdminsSid], snap.AuthorizedGroups);
        Assert.True(snap.IsStillAuthorized([CorpConfRoomAdminsSid]));
    }

    [Fact]
    public void IsStillAuthorized_TrueWhenCapturedGroupStillAllowed()
    {
        var snap = JobAuthorizationSnapshot.Capture(PrincipalWithRoles(ConfRoomAdminsSid), "ConferenceRooms", [ConfRoomAdminsSid, AdminsSid]);
        Assert.True(snap.IsStillAuthorized([ConfRoomAdminsSid, AdminsSid]));
    }

    [Fact]
    public void IsStillAuthorized_FalseWhenCapturedGroupRemovedFromConfig()
    {
        // Captured authorized via ConfRoomAdmins; if that group is later removed from the section's
        // allowed set, the job is no longer authorized (fail closed).
        var snap = JobAuthorizationSnapshot.Capture(PrincipalWithRoles(ConfRoomAdminsSid), "ConferenceRooms", [ConfRoomAdminsSid]);
        Assert.False(snap.IsStillAuthorized([SomeDifferentGroupSid]));
    }

    [Fact]
    public void IsStillAuthorized_FalseWhenNothingCaptured()
    {
        var snap = JobAuthorizationSnapshot.Capture(PrincipalWithRoles(OtherGroupSid), "ConferenceRooms", [ConfRoomAdminsSid]);
        Assert.Empty(snap.AuthorizedGroups);
        Assert.False(snap.IsStillAuthorized([ConfRoomAdminsSid]));
    }

    [Fact]
    public void IsStillAuthorized_FailsClosedOnEmptyAllowedGroups()
    {
        var snap = JobAuthorizationSnapshot.Capture(PrincipalWithRoles(ConfRoomAdminsSid), "ConferenceRooms", [ConfRoomAdminsSid]);
        Assert.False(snap.IsStillAuthorized([]));
    }

    [Fact]
    public void JsonRoundTrip_PreservesSectionAndDecision()
    {
        var original = JobAuthorizationSnapshot.Capture(PrincipalWithRoles(ConfRoomAdminsSid, AdminsSid), "ConferenceRooms", [ConfRoomAdminsSid, AdminsSid]);
        var restored = JobAuthorizationSnapshot.FromJson(original.ToJson());

        Assert.NotNull(restored);
        Assert.Equal("ConferenceRooms", restored!.Section);
        Assert.Equal(original.AuthorizedGroups.OrderBy(x => x), restored.AuthorizedGroups.OrderBy(x => x));
        Assert.True(restored.IsStillAuthorized([ConfRoomAdminsSid]));
    }

    // A principal that returns IsInRole(true) only for one specific role, with no role claims.
    private sealed class IsInRolePrincipal : ClaimsPrincipal
    {
        private readonly string _role;
        public IsInRolePrincipal(ClaimsIdentity identity, string role) : base(identity) => _role = role;
        public override bool IsInRole(string role) => string.Equals(role, _role, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Capture_IgnoresUnmigratedNameValues()
    {
        // Review finding sid-1, job-runner half. IsInRole resolves names, so without the filter a
        // name-valued row would be captured as an authorizing group - and then authorize every row
        // of the job, off-circuit, on an identifier that cannot be disambiguated.
        var principal = new IsInRolePrincipal(new ClaimsIdentity("test"), "ConfRoomAdmins");

        var snap = JobAuthorizationSnapshot.Capture(principal, "ConferenceRooms", ["ConfRoomAdmins"]);

        Assert.Empty(snap.AuthorizedGroups);
        Assert.False(snap.IsStillAuthorized(["ConfRoomAdmins"]));
    }

    [Fact]
    public void FromJson_NullOrInvalid_ReturnsNull()
    {
        Assert.Null(JobAuthorizationSnapshot.FromJson(null));
        Assert.Null(JobAuthorizationSnapshot.FromJson(""));
        Assert.Null(JobAuthorizationSnapshot.FromJson("{not valid json"));
    }
}

