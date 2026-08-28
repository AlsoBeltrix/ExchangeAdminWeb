using System.Management.Automation;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.SelfServiceGroups;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Cross-domain member listing fix (2026-08-28). Get-ADGroupMember makes ADWS resolve every
/// member server-side and faults the WHOLE read ("An operations error occurred",
/// GetADGroupMemberFault) when a member belongs to another forest domain the calling credential
/// cannot chase - one WINROOT group nested in ANALOG's ExchangeWebAdmins (since 2026-05-12)
/// broke both member listings. Not a 2.9.0 regression: the failing code was identical in 2.8.1;
/// the nesting validation simply exercised a nested-group case first.
///
/// Both group modules now read the group's linked <c>member</c> attribute (the Comms10k
/// pattern) and resolve each member individually, routed to its own domain, degrading a single
/// unresolvable member to a DN-named read-only row instead of failing the list.
///
/// The live reads cannot run without a directory, so behaviour tests cover the extracted pure
/// helpers and source tripwires pin the wiring - the repo's established pattern.
/// </summary>
public class GroupMemberListingTests
{
    // ----- MemberDnsOf: projection of the linked member attribute -----

    [Fact]
    public void MemberDnsOf_ReadsACollection()
    {
        var group = PSObject.AsPSObject(new { member = new[] { "CN=A,DC=x", "CN=B,DC=y" } });

        Assert.Equal(new[] { "CN=A,DC=x", "CN=B,DC=y" }, GroupManagementService.MemberDnsOf(group));
    }

    [Fact]
    public void MemberDnsOf_ReadsASingleString()
    {
        var group = PSObject.AsPSObject(new { member = "CN=Only,DC=x" });

        Assert.Equal(new[] { "CN=Only,DC=x" }, GroupManagementService.MemberDnsOf(group));
    }

    [Fact]
    public void MemberDnsOf_EmptyGroup_YieldsNothing()
    {
        Assert.Empty(GroupManagementService.MemberDnsOf(PSObject.AsPSObject(new { other = 1 })));
    }

    [Fact]
    public void MemberDnsOf_SkipsBlankEntries()
    {
        var group = PSObject.AsPSObject(new { member = new[] { "", "  ", "CN=A,DC=x" } });

        Assert.Equal(new[] { "CN=A,DC=x" }, GroupManagementService.MemberDnsOf(group));
    }

    // ----- DisplayNameFromDn: what a degraded (unresolvable) row is called -----

    [Fact]
    public void DisplayNameFromDn_ExtractsTheCn()
    {
        Assert.Equal("Organization Management", GroupManagementService.DisplayNameFromDn(
            "CN=Organization Management,OU=Microsoft Exchange Security Groups,DC=winroot,DC=analog,DC=com"));
    }

    [Fact]
    public void DisplayNameFromDn_UnescapesCommas()
    {
        Assert.Equal("Coelho, Michael", GroupManagementService.DisplayNameFromDn(
            "CN=Coelho\\, Michael,OU=Users,DC=ad,DC=analog,DC=com"));
    }

    [Fact]
    public void DisplayNameFromDn_FallsBackToTheDn_WhenThereIsNoCn()
    {
        Assert.Equal("OU=Weird,DC=x", GroupManagementService.DisplayNameFromDn("OU=Weird,DC=x"));
    }

    // ----- Tripwire: the self-service listing uses the same shape -----
    // (The admin listing's tripwire lives in GroupMemberNestingProtectionTests, updated with
    // this fix.)

    [Fact]
    public void SelfServiceListing_ReadsTheMemberAttribute_AndResolvesPerDomain()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "SelfServiceGroups", "SelfServiceGroupService.cs"));
        var start = text.IndexOf("public async Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "GetGroupMembersAsync signature not found - tripwire is stale.");
        var end = text.IndexOf("public async Task<MembershipChangeResult> ChangeMemberAsync(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound GetGroupMembersAsync - update the tripwire.");
        var body = text[start..end];

        Assert.DoesNotContain("AddCommand(\"Get-ADGroupMember\")", body, StringComparison.Ordinal);
        Assert.Contains("AddParameter(\"Properties\", new[] { \"member\" })", body, StringComparison.Ordinal);
        Assert.Contains("MemberDnsOf(", body, StringComparison.Ordinal);
        Assert.Contains("ServerFromDn(memberDn)", body, StringComparison.Ordinal);
        Assert.Contains("DisplayNameFromDn(memberDn)", body, StringComparison.Ordinal);
        // Removability comes only from a RESOLVED objectClass, so an unresolvable member is
        // inert: KindOf(null) is "Other" and IsRemovable(null) is false.
        Assert.Contains("GroupMemberClassifier.IsRemovable(objectClass)", body, StringComparison.Ordinal);
        Assert.Equal("Other", GroupMemberClassifier.KindOf(null));
        Assert.False(GroupMemberClassifier.IsRemovable(null));
    }

    // ----- lst-1: a degraded row (blank ObjectGuid) must be inert end to end -----

    [Theory]
    [InlineData(null, "CN=A,DC=x", "CN=A,DC=x")]
    [InlineData("", "CN=A,DC=x", "CN=A,DC=x")]
    [InlineData("   ", "CN=A,DC=x", "CN=A,DC=x")]
    [InlineData("guid-1", "CN=A,DC=x", "guid-1")]
    [InlineData(null, null, null)]
    [InlineData("", "  ", null)]
    public void FirstNonBlank_NeverLetsABlankKeyShadowARealOne(string? first, string? second, string? expected)
    {
        Assert.Equal(expected, GroupManagementService.FirstNonBlank(first, second));
    }

    [Fact]
    public void AdminRemove_RefusesABlankListedGuid_AndPageDisablesTheButton()
    {
        var service = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "GroupManagementService.cs"));

        // The service guard sits at the top of RemoveMemberAsync, BEFORE any resolution.
        var start = service.IndexOf("public async Task<PermissionResult> RemoveMemberAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "RemoveMemberAsync signature not found - tripwire is stale.");
        var end = service.IndexOf("// --- Helpers ---", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound RemoveMemberAsync - update the tripwire.");
        var body = service[start..end];
        var iGuard = body.IndexOf("memberObjectGuid is not null && string.IsNullOrWhiteSpace(memberObjectGuid)", StringComparison.Ordinal);
        var iResolve = body.IndexOf("ResolveMemberForWrite(creds.Value, member, memberDn: memberDnHint, memberObjectGuid)", StringComparison.Ordinal);
        Assert.True(iGuard >= 0, "Blank-listed-GUID refusal missing from RemoveMemberAsync (lst-1).");
        Assert.True(iResolve > iGuard, "The blank-GUID refusal must precede resolution (lst-1).");

        // The resolver coalesces on non-blank, so no other caller can regress the same way.
        Assert.Contains("FirstNonBlank(memberObjectGuid, memberDn)", service, StringComparison.Ordinal);
        Assert.DoesNotContain("memberObjectGuid ?? memberDn", service, StringComparison.Ordinal);

        // And the page removes the affordance for a row with no immutable id.
        var page = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Components", "Pages", "GroupManagement.razor"));
        Assert.Contains("string.IsNullOrEmpty(member.ObjectGuid)", page, StringComparison.Ordinal);
    }

    // ----- lst-3: an errored admin membership read is a read ERROR, not an empty group -----

    [Fact]
    public void AdminListing_RejectsAnErroredMembershipRead()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "GroupManagementService.cs"));
        var start = text.IndexOf("public async Task<GroupMemberList> GetMembersAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "GetMembersAsync signature not found - tripwire is stale.");
        var end = text.IndexOf("public async Task<PermissionResult> AddMemberAsync(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound GetMembersAsync - update the tripwire.");
        var body = text[start..end];

        // The stream is cleared BEFORE the membership read (so earlier SilentlyContinue probes
        // cannot pollute the check), and HadErrors is rejected BEFORE any member is projected.
        var iRead = body.IndexOf("AddCommand(\"Get-ADGroup\")", StringComparison.Ordinal);
        var iPreClear = body.IndexOf("ps.Streams.Error.Clear();", StringComparison.Ordinal);
        var iReject = body.IndexOf("if (ps.HadErrors || groupWithMembers is null)", StringComparison.Ordinal);
        var iProject = body.IndexOf("MemberDnsOf(groupWithMembers)", StringComparison.Ordinal);
        Assert.True(iRead >= 0, "Membership read not found - tripwire is stale.");
        Assert.True(iPreClear >= 0 && iPreClear < iRead, "The pre-read error-stream clear must precede the membership read (lst-3).");
        Assert.True(iReject > iRead, "The HadErrors rejection is missing after the membership read (lst-3).");
        Assert.True(iProject > iReject, "Members must only be projected after the errored-read rejection (lst-3).");
        Assert.Contains("The group's membership could not be read.", body, StringComparison.Ordinal);
    }

    // ----- lst-2: primaryGroupID members are unioned back in, read-only, fail-closed -----

    [Theory]
    [InlineData("S-1-5-21-8915387-325452579-1788637320-513", "513")]
    [InlineData("s-1-5-32-544", "544")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("S-1-5-21-1-2-3-", null)]
    [InlineData("not-a-sid", null)]
    [InlineData("S-1-5-21-1-2-3-51x", null)]
    public void RidFromSid_DerivesTheFinalSubAuthority_OrRefuses(string? sid, string? expected)
    {
        Assert.Equal(expected, GroupManagementService.RidFromSid(sid));
    }

    [Fact]
    public void Listings_UnionPrimaryGroupMembers_ReadOnly_AndFailClosed()
    {
        var admin = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "GroupManagementService.cs"));
        var aStart = admin.IndexOf("public async Task<GroupMemberList> GetMembersAsync(", StringComparison.Ordinal);
        var aEnd = admin.IndexOf("public async Task<PermissionResult> AddMemberAsync(", aStart, StringComparison.Ordinal);
        Assert.True(aStart >= 0 && aEnd > aStart, "Could not bound GetMembersAsync - update the tripwire.");
        var aBody = admin[aStart..aEnd];
        Assert.Contains("(primaryGroupID={rid})", aBody, StringComparison.Ordinal);
        Assert.Contains("RidFromSid(", aBody, StringComparison.Ordinal);
        Assert.Contains("IsPrimaryMember = true", aBody, StringComparison.Ordinal);
        // Fail-closed: every primary-read failure clears the members and reports a read error,
        // so the linked half can never present alone as the complete membership.
        Assert.Contains("result.Members.Clear();", aBody, StringComparison.Ordinal);

        var ss = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "SelfServiceGroups", "SelfServiceGroupService.cs"));
        var sStart = ss.IndexOf("public async Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(", StringComparison.Ordinal);
        var sEnd = ss.IndexOf("public async Task<MembershipChangeResult> ChangeMemberAsync(", sStart, StringComparison.Ordinal);
        Assert.True(sStart >= 0 && sEnd > sStart, "Could not bound GetGroupMembersAsync - update the tripwire.");
        var sBody = ss[sStart..sEnd];
        Assert.Contains("(primaryGroupID={rid})", sBody, StringComparison.Ordinal);
        Assert.Contains("IsRemovable = false", sBody, StringComparison.Ordinal);

        // The admin page offers no Remove for a primary row.
        var page = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Components", "Pages", "GroupManagement.razor"));
        Assert.Contains("member.IsPrimaryMember", page, StringComparison.Ordinal);
    }
}
