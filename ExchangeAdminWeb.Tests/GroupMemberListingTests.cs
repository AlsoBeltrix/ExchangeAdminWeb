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
}
