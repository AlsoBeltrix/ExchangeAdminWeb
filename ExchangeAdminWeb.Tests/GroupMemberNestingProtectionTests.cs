using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.SelfServiceGroups;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// S1 onward of docs/GroupMemberNesting-Plan.md: the protected-principal transitive membership check
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
    public void RemoveListedMemberAsync_ResolvesGatesAndDenies_InOrder_BeforeTheSharedExecutor()
    {
        // gmn-5: whole-file Contains stayed green with the denial return deleted. Bound the
        // method and assert the ORDER: resolve -> gate -> denial return -> executor.
        var text = SelfServiceText();
        var start = text.IndexOf("public async Task<MembershipChangeResult> RemoveListedMemberAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "RemoveListedMemberAsync signature not found - tripwire is stale.");
        var end = text.IndexOf("private static ResolvedDirectoryPrincipal? ResolveListedMemberByGuid(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound RemoveListedMemberAsync - update the tripwire.");
        var body = text[start..end];

        var iResolve = body.IndexOf("ResolveListedMemberByGuid(creds.Value, memberObjectGuid)", StringComparison.Ordinal);
        var iGate = body.IndexOf("await CheckMemberProtectedAsync(member, actingUser)", StringComparison.Ordinal);
        var iDenialCheck = body.IndexOf("if (protection.Denial is not null)", StringComparison.Ordinal);
        var iDenialReturn = body.IndexOf("return MembershipChangeResult.From(protection.Denial);", StringComparison.Ordinal);
        var iExecutor = body.IndexOf("return await ApplyMembershipChangeAsync(callerSid, groupObjectGuid, creds.Value, member, MembershipOperation.Remove, protection);", StringComparison.Ordinal);

        Assert.True(iResolve >= 0, "GUID resolution missing from RemoveListedMemberAsync.");
        Assert.True(iGate > iResolve, "The protection gate must follow the resolution it gates.");
        Assert.True(iDenialCheck > iGate, "The denial check is missing after the gate.");
        Assert.True(iDenialReturn > iDenialCheck, "A denial must RETURN before the executor.");
        Assert.True(iExecutor > iDenialReturn, "The executor must come after the denial return.");

        // And the typed path still runs the same gate (two call sites file-wide).
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
    public void Page_RemovesByGuid_AndGroupRowsOnlyEnterThePendingState()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Components", "Pages", "SelfServiceGroups.razor"));

        Assert.Contains("RemoveListedMemberAsync(callerSid, group.ObjectGuid, member.ObjectGuid", text, StringComparison.Ordinal);
        // D2's warning text: one-way removal, re-adding needs a ticket.
        Assert.Contains("re-adding it will require an IT Support Desk ticket", text, StringComparison.Ordinal);

        // gmn-5: the GROUP button must only OPEN the pending state - a one-click route to the
        // removal would erase D2's second action. Bound the Group branch of the row markup.
        var groupBranch = text.IndexOf("@if (member.Kind == \"Group\")", StringComparison.Ordinal);
        Assert.True(groupBranch >= 0, "Group branch not found in the member row - tripwire is stale.");
        var branchEnd = text.IndexOf("else", groupBranch, StringComparison.Ordinal);
        Assert.True(branchEnd > groupBranch, "Could not bound the Group branch - update the tripwire.");
        var branch = text[groupBranch..branchEnd];
        Assert.Contains("BeginGroupRemoval(member)", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmGroupRemoval", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveListedMember(", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveMember(", branch, StringComparison.Ordinal);

        // The confirming action exists exactly once, inside the pending-state block.
        Assert.Single(Regex.Matches(text, Regex.Escape("ConfirmGroupRemoval(member)")));
        var pendingBlock = text.IndexOf("pendingGroupRemoval?.ObjectGuid == member.ObjectGuid", StringComparison.Ordinal);
        Assert.True(pendingBlock >= 0, "Pending-state block not found.");
        Assert.True(text.IndexOf("ConfirmGroupRemoval(member)", StringComparison.Ordinal) > pendingBlock,
            "ConfirmGroupRemoval must be reachable only from the pending-state block.");
    }

    [Fact]
    public void PendingGroupRemoval_IsClearedOnReload_AndRevalidatedOnConfirm()
    {
        // gmn-4: the pending confirm must not survive a list reload or a group switch, and the
        // second action must confirm exactly the row the warning was opened for.
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Components", "Pages", "SelfServiceGroups.razor"));

        var load = text.IndexOf("private async Task LoadMembers()", StringComparison.Ordinal);
        Assert.True(load >= 0, "LoadMembers not found - tripwire is stale.");
        var loadEnd = text.IndexOf("private async Task RemoveMember(", load, StringComparison.Ordinal);
        Assert.True(loadEnd > load, "Could not bound LoadMembers - update the tripwire.");
        Assert.Contains("pendingGroupRemoval = null", text[load..loadEnd], StringComparison.Ordinal);

        var confirm = text.IndexOf("private async Task ConfirmGroupRemoval(GroupMember member)", StringComparison.Ordinal);
        Assert.True(confirm >= 0, "ConfirmGroupRemoval not found - tripwire is stale.");
        var confirmEnd = text.IndexOf("private async Task RemoveListedMember(", confirm, StringComparison.Ordinal);
        Assert.True(confirmEnd > confirm, "Could not bound ConfirmGroupRemoval - update the tripwire.");
        Assert.Contains("pendingGroupRemoval?.ObjectGuid != member.ObjectGuid", text[confirm..confirmEnd], StringComparison.Ordinal);
    }

    // ----- S5a: the admin member list reports what a member actually is -----

    [Fact]
    public void AdminMemberListing_ReadsClassFromMembershipOutput_AndResolvesDetailsByGuid()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "GroupManagementService.cs"));
        var start = text.IndexOf("public async Task<GroupMemberList> GetMembersAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "GetMembersAsync signature not found - tripwire is stale.");
        var end = text.IndexOf("public async Task<PermissionResult> AddMemberAsync(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound GetMembersAsync - update the tripwire.");
        var body = text[start..end];

        // The old shape resolved every member with Get-ADUser -Identity <sam> and hardcoded
        // RecipientType "ADUser", so a nested group rendered as a user with an empty Email.
        Assert.Contains("GroupMemberClassifier.KindOf(objectClass)", body, StringComparison.Ordinal);
        Assert.Contains("AddCommand(\"Get-ADObject\")", body, StringComparison.Ordinal);
        Assert.Contains("AddParameter(\"Identity\", objectGuid)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AddCommand(\"Get-ADUser\")", body, StringComparison.Ordinal);
        Assert.Contains("MemberKind = kind", body, StringComparison.Ordinal);
        Assert.Contains("ObjectGuid = objectGuid", body, StringComparison.Ordinal);
    }

    // ----- S5b (gmn-2): nesting guards in the SERVICE, with the non-inverted cycle probe -----

    [Fact]
    public void SelfNest_MatchesOnResolvedDns_CaseInsensitive()
    {
        Assert.True(GroupManagementService.IsSelfNest("CN=Ops,DC=analog,DC=com", "cn=ops,dc=analog,dc=com"));
        Assert.False(GroupManagementService.IsSelfNest("CN=Ops,DC=analog,DC=com", "CN=Other,DC=analog,DC=com"));
    }

    [Fact]
    public void CycleProbe_SubjectIsTarget_ChainIsCandidate_NeverInverted()
    {
        // AC11b: a single-direction assertion passes against the inverted filter (which answers
        // the benign already-a-member question), so both ends are pinned.
        var filter = GroupManagementService.BuildCycleProbeFilter("CN=Target,DC=x", "CN=Candidate,DC=x");

        Assert.StartsWith("(&(distinguishedName=CN=Target,DC=x)", filter, StringComparison.Ordinal);
        Assert.EndsWith("(memberOf:1.2.840.113556.1.4.1941:=CN=Candidate,DC=x))", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void CycleProbe_EscapesLdapMetacharacters()
    {
        var filter = GroupManagementService.BuildCycleProbeFilter("CN=A(1),DC=x", "CN=B*,DC=x");

        Assert.Contains("\\28", filter, StringComparison.Ordinal);
        Assert.Contains("\\29", filter, StringComparison.Ordinal);
        Assert.Contains("\\2a", filter, StringComparison.Ordinal);
    }

    // ----- gmn-8: forest-wide selections route to the owning domain -----

    [Theory]
    [InlineData("CN=Ops,OU=Groups,DC=winroot,DC=analog,DC=com", "winroot.analog.com")]
    [InlineData("CN=Ops,DC=analog,DC=com", "analog.com")]
    [InlineData("cn=x,dc=sub,dc=example,dc=org", "sub.example.org")]
    public void ServerFromDn_DerivesTheOwningDomain(string dn, string expected)
    {
        Assert.Equal(expected, GroupManagementService.ServerFromDn(dn));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CN=NoDomainHere")]
    public void ServerFromDn_ReturnsNull_WithoutDcComponents(string? dn)
    {
        Assert.Null(GroupManagementService.ServerFromDn(dn));
    }

    [Fact]
    public void ServerFromDn_IgnoresEscapedCommas_InsideAName()
    {
        Assert.Equal("analog.com",
            GroupManagementService.ServerFromDn("CN=Ops\\, Team,OU=G,DC=analog,DC=com"));
    }

    [Fact]
    public void ImmutableKeyLookups_BindToTheOwningDomainServer()
    {
        var text = AdminServiceText();

        // The write-path resolver derives -Server from the DN it holds (picker DN or the DN
        // hint carried beside the removal GUID)...
        var rStart = text.IndexOf("internal virtual ResolvedMember ResolveMemberForWrite(", StringComparison.Ordinal);
        Assert.True(rStart >= 0, "ResolveMemberForWrite not found - tripwire is stale.");
        var rEnd = text.IndexOf("private async Task<ProtectionGate> CheckResolvedMemberAsync", rStart, StringComparison.Ordinal);
        Assert.True(rEnd > rStart, "Could not bound ResolveMemberForWrite - update the tripwire.");
        var resolver = text[rStart..rEnd];
        Assert.Contains("ServerFromDn(memberDn)", resolver, StringComparison.Ordinal);
        Assert.Contains("AddParameter(\"Server\", server)", resolver, StringComparison.Ordinal);

        // ...and the member-list detail lookup routes by each member's own DN.
        var lStart = text.IndexOf("public async Task<GroupMemberList> GetMembersAsync(", StringComparison.Ordinal);
        var lEnd = text.IndexOf("public async Task<PermissionResult> AddMemberAsync(", lStart, StringComparison.Ordinal);
        Assert.True(lStart >= 0 && lEnd > lStart, "Could not bound GetMembersAsync - update the tripwire.");
        var listing = text[lStart..lEnd];
        Assert.Contains("ServerFromDn(memberDn)", listing, StringComparison.Ordinal);
        Assert.Contains("DistinguishedName = memberDn", listing, StringComparison.Ordinal);
    }

    private static string AdminServiceText() => File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
        "Services", "GroupManagementService.cs"));

    [Fact]
    public void AdminAddPath_OrdersResolveGateGuardsWriteReadback()
    {
        var text = AdminServiceText();
        var start = text.IndexOf("public async Task<PermissionResult> AddMemberAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "AddMemberAsync signature not found - tripwire is stale.");
        var end = text.IndexOf("public async Task<PermissionResult> RemoveMemberAsync(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound AddMemberAsync - update the tripwire.");
        var body = text[start..end];

        var iResolve = body.IndexOf("ResolveMemberForWrite(creds.Value, member, memberDn, memberObjectGuid: null)", StringComparison.Ordinal);
        var iGate = body.IndexOf("CheckResolvedMemberAsync(resolvedMember.Principal!, actingUser)", StringComparison.Ordinal);
        var iDenial = body.IndexOf("return resolvedGate.Denial;", StringComparison.Ordinal);
        // gmn-6: no class condition may guard the resolved-principal gate. The write-phase
        // nesting guards legitimately branch on IsGroup, so the pin is scoped to the region
        // between the resolution and the denial return.
        Assert.True(iResolve >= 0 && iDenial > iResolve, "Gate region not found - tripwire is stale.");
        Assert.DoesNotContain("if (resolvedMember.IsGroup)", body[iResolve..iDenial], StringComparison.Ordinal);
        var iSelf = body.IndexOf("IsSelfNest(resolvedGroupDn, candidateDn)", StringComparison.Ordinal);
        var iCycle = body.IndexOf("BuildCycleProbeFilter(resolvedGroupDn, candidateDn)", StringComparison.Ordinal);
        var iFailClosed = body.IndexOf("The nesting check could not be completed.", StringComparison.Ordinal);
        var iWrite = body.IndexOf("AddCommand(\"Add-ADGroupMember\")", StringComparison.Ordinal);
        var iReadback = body.LastIndexOf("IsDirectMemberOf(ps, credential, resolvedGroupDn, candidateDn)", StringComparison.Ordinal);

        Assert.True(iResolve >= 0, "Class-agnostic resolution missing from AddMemberAsync.");
        Assert.True(iGate > iResolve, "The resolved-principal gate must follow the resolution (gmn-1).");
        Assert.True(iDenial > iGate, "A gate denial must RETURN before the write.");
        Assert.True(iSelf > iDenial, "Self-nest guard missing before the write (gmn-2).");
        Assert.True(iCycle > iSelf, "Cycle probe missing before the write (gmn-2).");
        Assert.True(iFailClosed > iCycle, "The cycle probe must fail closed on errors.");
        Assert.True(iWrite > iFailClosed, "The write must come after every guard.");
        Assert.True(iReadback > iWrite, "Read-back reconciliation must follow the write.");

        // The interpolated PowerShell -Filter strings are gone from this path.
        Assert.DoesNotContain("UserPrincipalName -eq '", body, StringComparison.Ordinal);
        Assert.DoesNotContain("EmailAddress -eq '", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminRemovePath_GuidKeyed_GatesResolved_AndReadsBack()
    {
        var text = AdminServiceText();
        var start = text.IndexOf("public async Task<PermissionResult> RemoveMemberAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "RemoveMemberAsync signature not found - tripwire is stale.");
        var end = text.IndexOf("// --- Helpers ---", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound RemoveMemberAsync - update the tripwire.");
        var body = text[start..end];

        var iResolve = body.IndexOf("ResolveMemberForWrite(creds.Value, member, memberDn: memberDnHint, memberObjectGuid)", StringComparison.Ordinal);
        var iGate = body.IndexOf("CheckResolvedMemberAsync(resolvedMember.Principal!, actingUser)", StringComparison.Ordinal);
        var iDenial = body.IndexOf("return resolvedGate.Denial;", StringComparison.Ordinal);
        var iWrite = body.IndexOf("AddCommand(\"Remove-ADGroupMember\")", StringComparison.Ordinal);
        var iReadback = body.LastIndexOf("IsDirectMemberOf(ps, credential, resolvedGroupDn, memberDnResolved)", StringComparison.Ordinal);

        Assert.True(iResolve >= 0, "GUID-capable resolution missing from RemoveMemberAsync.");
        Assert.True(iGate > iResolve, "The resolved-principal gate must follow the resolution (gmn-1).");
        // gmn-6: no label/class condition may guard the resolved-principal gate.
        Assert.DoesNotContain("resolvedMember.IsGroup || string.IsNullOrWhiteSpace(member)", body, StringComparison.Ordinal);
        Assert.True(iDenial > iGate, "A gate denial must RETURN before the write.");
        Assert.True(iWrite > iDenial, "The write must come after the gate.");
        Assert.True(iReadback > iWrite, "Read-back reconciliation must follow the write.");

        Assert.DoesNotContain("UserPrincipalName -eq '", body, StringComparison.Ordinal);
        Assert.DoesNotContain("EmailAddress -eq '", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminPage_PassesTheListedGuid_ToTheRemove()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Components", "Pages", "GroupManagement.razor"));

        // The Remove button used to pass member.Email - the empty string for every group
        // (S5a's mislabelled listing) and a driftable identity for users.
        Assert.Contains("RemoveMemberAsync(group.Identity, member, authState.User, group.SamAccountName, listed.ObjectGuid, listed.DistinguishedName)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveMember(member.Email)", text, StringComparison.Ordinal);
    }

    // ----- S5c (gmn-3): the picker's DN travels with the selection, and dies on a retype -----

    [Fact]
    public void AdminPage_AddPicker_IsAdBacked_HoldsTheDn_AndClearsItOnTypedInput()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Components", "Pages", "GroupManagement.razor"));

        // AD objects, any class - not Exchange recipients this on-prem chain would reject.
        Assert.Contains("ADIdentityAutocomplete ObjectKind=\"Any\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RecipientAutocomplete", text, StringComparison.Ordinal);

        // The held DN is what the service writes; forest-wide search can offer two same-named
        // groups from different domains and only the DN distinguishes them (gmn-3).
        Assert.Contains("AddMemberAsync(group.Identity, memberLabel, authState.User, group.SamAccountName, selection?.DistinguishedName)", text, StringComparison.Ordinal);

        // Typed input has no DN: the ValueChanged handler must clear the held selection so a
        // stale DN from a previous pick cannot survive a retype.
        var changed = text.IndexOf("private void OnNewMemberChanged(string value)", StringComparison.Ordinal);
        Assert.True(changed >= 0, "OnNewMemberChanged not found - tripwire is stale.");
        var changedEnd = text.IndexOf("private void OnNewMemberSelected(", changed, StringComparison.Ordinal);
        Assert.True(changedEnd > changed, "Could not bound OnNewMemberChanged - update the tripwire.");
        Assert.Contains("newMemberSelection = null", text[changed..changedEnd], StringComparison.Ordinal);

        // The member list renders what a member IS (S5a's kind reaches the operator).
        Assert.Contains("<th>Kind</th>", text, StringComparison.Ordinal);
        Assert.Contains("@member.MemberKind", text, StringComparison.Ordinal);
    }

    // ----- gmn-7: in-flight handlers act on snapshots, never on live navigation state -----

    [Fact]
    public void AdminPage_HandlersSnapshotState_BeforeTheFirstAwait()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Components", "Pages", "GroupManagement.razor"));

        foreach (var handler in new[] { "private async Task AddMember()", "private async Task RemoveMember(GroupMemberInfo listed)" })
        {
            var start = text.IndexOf(handler, StringComparison.Ordinal);
            Assert.True(start >= 0, handler + " not found - tripwire is stale.");
            var end = text.IndexOf("finally { isLoading = false; }", start, StringComparison.Ordinal);
            Assert.True(end > start, "Could not bound " + handler + " - update the tripwire.");
            var body = text[start..end];

            var iSnap = body.IndexOf("var group = selectedGroup;", StringComparison.Ordinal);
            // Both handlers open their async work with Task.Yield - the canonical first await
            // (a bare "await " token also appears in comments).
            var iAwait = body.IndexOf("await Task.Yield();", StringComparison.Ordinal);
            Assert.True(iSnap >= 0, "Group snapshot missing in " + handler);
            Assert.True(iAwait > iSnap, "The snapshot must precede the first await in " + handler);

            // After the snapshot, the handler must not dereference the live selection state
            // (ReferenceEquals comparison for the refresh guard is the one allowed read).
            Assert.DoesNotContain("selectedGroup!.", body, StringComparison.Ordinal);
            Assert.DoesNotContain("selectedGroup.", body, StringComparison.Ordinal);
            Assert.DoesNotContain("newMemberSelection?.", body, StringComparison.Ordinal);
        }
    }

    // ----- gmn-9: the immutable identity reaches EVERY audit branch -----

    [Fact]
    public void AdminPage_AuditsCarryTheImmutableIdentity_OnEveryBranch()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Components", "Pages", "GroupManagement.razor"));

        var addStart = text.IndexOf("private async Task AddMember()", StringComparison.Ordinal);
        var addEnd = text.IndexOf("finally { isLoading = false; }", addStart, StringComparison.Ordinal);
        Assert.True(addStart >= 0 && addEnd > addStart, "Could not bound AddMember - update the tripwire.");
        var add = text[addStart..addEnd];
        // Ticket denial, auth denial, the outcome record, and the exception path - all four.
        Assert.Equal(4, Regex.Matches(add, Regex.Escape("[\"memberDn\"] = selection?.DistinguishedName")).Count);

        var remStart = text.IndexOf("private async Task RemoveMember(GroupMemberInfo listed)", StringComparison.Ordinal);
        var remEnd = text.IndexOf("finally { isLoading = false; }", remStart, StringComparison.Ordinal);
        Assert.True(remStart >= 0 && remEnd > remStart, "Could not bound RemoveMember - update the tripwire.");
        var remove = text[remStart..remEnd];
        Assert.Equal(4, Regex.Matches(remove, Regex.Escape("[\"memberObjectGuid\"] = listed.ObjectGuid")).Count);
        Assert.Equal(4, Regex.Matches(remove, Regex.Escape("[\"memberDn\"] = listed.DistinguishedName")).Count);
    }

    [Fact]
    public void SelectGroup_ClearsTheHeldPickerSelection()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Components", "Pages", "GroupManagement.razor"));
        var start = text.IndexOf("private void SelectGroup(GroupInfo group)", StringComparison.Ordinal);
        Assert.True(start >= 0, "SelectGroup not found - tripwire is stale.");
        var end = text.IndexOf("private async Task LoadMembers()", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound SelectGroup - update the tripwire.");

        Assert.Contains("newMemberSelection = null", text[start..end], StringComparison.Ordinal);
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
