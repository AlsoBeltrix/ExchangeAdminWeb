using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.SelfServiceGroups;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// S1-S4 of docs/GroupMemberNesting-Plan.md: the protected-principal transitive membership check
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

    // ----- S3 (D1): the add path refuses a GROUP by name, never as a generic miss -----

    [Fact]
    public void GroupProbeFilter_IsClassBounded_AndMatchesTheThreeIdentifiers()
    {
        Assert.Equal(
            "(&(objectCategory=group)(|(name=Ops Team)(sAMAccountName=Ops Team)(mail=Ops Team)))",
            AdOwnershipFilter.BuildGroupProbeFilter("Ops Team"));
    }

    [Fact]
    public void GroupProbeFilter_EscapesLdapMetacharacters()
    {
        var filter = AdOwnershipFilter.BuildGroupProbeFilter("a*(b)\\c");

        Assert.Contains("\\2a", filter, StringComparison.Ordinal);
        Assert.Contains("\\28", filter, StringComparison.Ordinal);
        Assert.Contains("\\29", filter, StringComparison.Ordinal);
        Assert.Contains("\\5c", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("a*(b)", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void NotFoundMessage_AddOfKnownGroup_NamesTheScopeRuleAndItsd()
    {
        var msg = SelfServiceGroupService.ComposeMemberNotFoundMessage(
            "Ops Team", MembershipOperation.Add, identityIsGroup: true);

        Assert.Contains("'Ops Team' is a group", msg, StringComparison.Ordinal);
        Assert.Contains("IT Support Desk", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void NotFoundMessage_AddOfUnknownIdentity_KeepsTheExistingMiss()
    {
        var msg = SelfServiceGroupService.ComposeMemberNotFoundMessage(
            "jdoe", MembershipOperation.Add, identityIsGroup: false);

        Assert.Equal("'jdoe' did not match exactly one user. Check the identity and try again.", msg);
    }

    [Fact]
    public void NotFoundMessage_RemovePath_IgnoresTheGroupProbe()
    {
        // D1 scopes the refusal to ADD; a remove miss keeps the generic message even when the
        // identity names a group (group REMOVAL arrives in S4 via the list, not the typed box).
        var msg = SelfServiceGroupService.ComposeMemberNotFoundMessage(
            "Ops Team", MembershipOperation.Remove, identityIsGroup: true);

        Assert.Equal("'Ops Team' did not match exactly one user. Check the identity and try again.", msg);
    }

    [Fact]
    public void ChangeMemberAsync_WiresTheProbe_OnTheAddNotFoundPathOnly()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "SelfServiceGroups", "SelfServiceGroupService.cs"));
        var start = text.IndexOf("public async Task<MembershipChangeResult> ChangeMemberAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ChangeMemberAsync signature not found - tripwire is stale.");
        var end = text.IndexOf(
            "return await ApplyMembershipChangeAsync(callerSid, groupObjectGuid, creds.Value, member, operation, protection);",
            start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound the not-found block - update the tripwire.");
        var body = text[start..end];

        Assert.Contains("operation == MembershipOperation.Add", body, StringComparison.Ordinal);
        Assert.Contains("GroupWithIdentityExists(creds.Value, memberIdentity)", body, StringComparison.Ordinal);
        Assert.Contains("ComposeMemberNotFoundMessage(memberIdentity, operation, identityIsGroup)", body, StringComparison.Ordinal);
    }

    // ----- S4 (D2): GUID-keyed list removal, the only path that can remove a group -----

    private static string SelfServiceText() => File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
        "Services", "SelfServiceGroups", "SelfServiceGroupService.cs"));

    [Fact]
    public void RemoveListedMemberAsync_ResolvesByGuid_GatesProtection_AndFeedsTheSharedExecutor()
    {
        var text = SelfServiceText();

        Assert.Contains("public async Task<MembershipChangeResult> RemoveListedMemberAsync(", text, StringComparison.Ordinal);
        Assert.Contains("ResolveListedMemberByGuid(creds.Value, memberObjectGuid)", text, StringComparison.Ordinal);
        Assert.Contains("ApplyMembershipChangeAsync(callerSid, groupObjectGuid, creds.Value, member, MembershipOperation.Remove, protection)", text, StringComparison.Ordinal);
        // Both public entry points run the SAME protection gate before the shared executor.
        Assert.Equal(2, Regex.Matches(text, Regex.Escape("await CheckMemberProtectedAsync(member, actingUser)")).Count);
    }

    [Fact]
    public void TypedPath_StillFeedsTheSharedExecutor()
    {
        // The single-executor refactor: ChangeMemberAsync keeps its signature and hands the
        // check-write-reconcile sequence to the same method the list path uses.
        Assert.Contains(
            "ApplyMembershipChangeAsync(callerSid, groupObjectGuid, creds.Value, member, operation, protection)",
            SelfServiceText(), StringComparison.Ordinal);
    }

    [Fact]
    public void GuidResolver_AcceptsOnlyUserOrGroup_AndKeepsGroupUpnEmpty()
    {
        var text = SelfServiceText();
        var start = text.IndexOf("private static ResolvedDirectoryPrincipal? ResolveListedMemberByGuid(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ResolveListedMemberByGuid not found - tripwire is stale.");
        var end = text.IndexOf("internal readonly record struct ProtectionGate", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound ResolveListedMemberByGuid - update the tripwire.");
        var body = text[start..end];

        Assert.Contains("AddCommand(\"Get-ADObject\")", body, StringComparison.Ordinal);
        Assert.Contains("AddParameter(\"Identity\", memberObjectGuid)", body, StringComparison.Ordinal);
        Assert.Contains("objectClass == \"user\"", body, StringComparison.Ordinal);
        Assert.Contains("objectClass == \"group\"", body, StringComparison.Ordinal);
        Assert.Contains("if (!isUser && !isGroup)", body, StringComparison.Ordinal);
        // A group must NEVER carry its name in the UPN-shaped field - MatchesIdentity would let it
        // false-match a protected USER entry sharing the name (plan S1 note).
        Assert.Contains("UserPrincipalName: isUser ?", body, StringComparison.Ordinal);
        Assert.Contains(": string.Empty", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_RemovesByGuid_AndConfirmsGroupRemovalInline()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Components", "Pages", "SelfServiceGroups.razor"));

        Assert.Contains("RemoveListedMemberAsync(callerSid, group.ObjectGuid, member.ObjectGuid", text, StringComparison.Ordinal);
        Assert.Contains("BeginGroupRemoval", text, StringComparison.Ordinal);
        Assert.Contains("ConfirmGroupRemoval", text, StringComparison.Ordinal);
        // D2's warning text: one-way removal, re-adding needs a ticket.
        Assert.Contains("re-adding it will require an IT Support Desk ticket", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPanel_StatesTheUsersOnlyRule_BeforeAnyAttempt()
    {
        // AC1 is page copy; no bUnit harness exists, so the tripwire pins the static sentence.
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Components", "Pages", "SelfServiceGroups.razor"));

        Assert.Contains("Only users can be added here", text, StringComparison.Ordinal);
        Assert.Contains("IT Support Desk ticket", text, StringComparison.Ordinal);
    }
}
