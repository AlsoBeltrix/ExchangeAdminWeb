using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// S1/S2 of docs/GroupMemberNesting-Plan.md: the protected-principal transitive membership check
/// must see GROUP targets. Get-ADUser answered a group DN with zero rows and no error, which
/// the check recorded as "no match" - a silent allow in a fail-closed service.
///
/// The check runs a real PowerShell runspace, so behaviour tests cover the two extracted pure
/// rules (SelectFallbackDn, IsProtectedGroupItself) and source tripwires pin how the method
/// wires them - the repo's established pattern where no harness can execute the code path.
/// </summary>
public class GroupMemberNestingProtectionTests
{
    // ----- SelectFallbackDn: the exactly-one resolution rule -----

    [Fact]
    public void SelectFallbackDn_ExactlyOneResult_ReturnsItsDn()
    {
        var (dn, failed) = ProtectedPrincipalService.SelectFallbackDn(
            new[] { "CN=Ops,DC=analog,DC=com" });

        Assert.False(failed);
        Assert.Equal("CN=Ops,DC=analog,DC=com", dn);
    }

    [Fact]
    public void SelectFallbackDn_ZeroResults_FailsClosed()
    {
        var (dn, failed) = ProtectedPrincipalService.SelectFallbackDn(Array.Empty<string?>());

        Assert.True(failed);
        Assert.Null(dn);
    }

    [Fact]
    public void SelectFallbackDn_MultipleResults_FailsClosed()
    {
        // Class-agnostic resolution can match a user and a group sharing a sAMAccountName;
        // picking either would let the check run against the wrong object.
        var (dn, failed) = ProtectedPrincipalService.SelectFallbackDn(
            new[] { "CN=Ops,DC=analog,DC=com", "CN=Ops,OU=Groups,DC=analog,DC=com" });

        Assert.True(failed);
        Assert.Null(dn);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SelectFallbackDn_SingleResultWithoutReadableDn_FailsClosed(string? unreadable)
    {
        var (dn, failed) = ProtectedPrincipalService.SelectFallbackDn(new[] { unreadable });

        Assert.True(failed);
        Assert.Null(dn);
    }

    // ----- IsProtectedGroupItself: the DN self-match half of the Groups rule -----

    [Fact]
    public void IsProtectedGroupItself_SameDn_CaseInsensitive_Matches()
    {
        Assert.True(ProtectedPrincipalService.IsProtectedGroupItself(
            "cn=domain admins,cn=users,dc=analog,dc=com",
            "CN=Domain Admins,CN=Users,DC=analog,DC=com"));
    }

    [Fact]
    public void IsProtectedGroupItself_DifferentDn_DoesNotMatch()
    {
        Assert.False(ProtectedPrincipalService.IsProtectedGroupItself(
            "CN=Helpdesk,CN=Users,DC=analog,DC=com",
            "CN=Domain Admins,CN=Users,DC=analog,DC=com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsProtectedGroupItself_MissingTargetDn_DoesNotMatch(string? targetDn)
    {
        Assert.False(ProtectedPrincipalService.IsProtectedGroupItself(
            targetDn, "CN=Domain Admins,CN=Users,DC=analog,DC=com"));
    }

    // ----- Source tripwires: how CheckTransitiveGroupMembership wires the rules -----
    // No test in this repo can execute the runspace, so these pin the call shapes the
    // behaviour tests above rely on being reachable.

    private static string TransitiveCheckBody()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Services", "ProtectedPrincipalService.cs"));
        var start = text.IndexOf(
            "private (List<string> matches, bool expansionHadErrors) CheckTransitiveGroupMembership(",
            StringComparison.Ordinal);
        Assert.True(start >= 0, "CheckTransitiveGroupMembership signature not found - tripwire is stale.");
        var end = text.IndexOf("\n    internal static (string? TargetDn", start, StringComparison.Ordinal);
        Assert.True(end > start, "SelectFallbackDn no longer follows CheckTransitiveGroupMembership - update the tripwire.");
        return text[start..end];
    }

    [Fact]
    public void TransitiveCheck_UsesClassAgnosticCmdlet_ForBothDirectoryCalls()
    {
        var body = TransitiveCheckBody();

        var adObjectCalls = Regex.Matches(body, Regex.Escape("AddCommand(\"Get-ADObject\")")).Count;
        Assert.Equal(2, adObjectCalls);
        Assert.DoesNotContain("AddCommand(\"Get-ADUser\")", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TransitiveCheck_RoutesFallbackResolution_ThroughExactlyOneRule()
    {
        Assert.Contains("SelectFallbackDn(", TransitiveCheckBody(), StringComparison.Ordinal);
    }

    [Fact]
    public void TransitiveCheck_AppliesTheSelfMatch_InsideTheGroupLoop()
    {
        Assert.Contains("IsProtectedGroupItself(targetDn, groupDn)", TransitiveCheckBody(), StringComparison.Ordinal);
    }

    // ----- S2: the self-service membership read must see group members -----

    [Fact]
    public void SelfServiceMembershipRead_UsesClassAgnosticCmdlet()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "SelfServiceGroups", "SelfServiceGroupService.cs"));
        var start = text.IndexOf("private static bool IsMemberOfGroup(", StringComparison.Ordinal);
        Assert.True(start >= 0, "IsMemberOfGroup signature not found - tripwire is stale.");
        var end = text.IndexOf("\n    private ", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound the IsMemberOfGroup body - update the tripwire.");
        var body = text[start..end];

        // The filter (&(distinguishedName=..)(memberOf=..)) is already class-agnostic; only the
        // cmdlet in front of it decided which member classes the read could see.
        Assert.Contains("AddCommand(\"Get-ADObject\")", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AddCommand(\"Get-ADUser\")", body, StringComparison.Ordinal);
    }
}
