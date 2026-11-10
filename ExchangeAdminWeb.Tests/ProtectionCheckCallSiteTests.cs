namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-88's sweep (owner, 2026-10-30: "this is a comprehensive fix across the codebase, not a narrow fix to one module"). Every
/// site whose operation resolves a target and then checks it - or checks several - begins ONE protected-principal check
/// around them (<see cref="Services.ProtectedPrincipalService.BeginCheck"/>): the directory-read credential is fetched once
/// for the operation, where the resolution and the check each fetched it, and the operation logs one timing line. Pinned in
/// the code view, as the pages' other wiring is: the reads behind these sites need a domain controller, and the mechanism
/// itself is proved end to end in <see cref="DirectoryReadCredentialOnceTests"/>.
/// </summary>
public sealed class ProtectionCheckCallSiteTests
{
    /// <summary>
    /// The method's code view from its signature to the next member, so a check begun in another method - or in a comment -
    /// does not count.
    /// </summary>
    private static string Method(string[] path, string signature)
    {
        var code = ProgressScan.CodeView(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(path)));
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found in {string.Join('/', path)} - the tripwire is stale.");
        var end = code.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        var next = code.IndexOf("\n    internal ", start + signature.Length, StringComparison.Ordinal);
        var nextPublic = code.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        var bound = new[] { end, next, nextPublic }.Where(i => i > start).DefaultIfEmpty(code.Length).Min();
        return code[start..bound];
    }

    public static TheoryData<string, string, string, string> Sites => new()
    {
        // S4 Group Management: the member's resolution and its check.
        { "Services/GroupManagementService.cs", "private async Task<ProtectionGate> CheckProtectedAsync(string member,",
            "_protectedPrincipals.BeginCheck(member)", "_protectedPrincipals.ResolveWithExchangeFallbackAsync(member)" },
        // S4b Group Management: the add's and the removal's two gates - the typed identity's and the resolved principal's.
        { "Services/GroupManagementService.cs", "public virtual async Task<PermissionResult> AddMemberAsync(",
            "_protectedPrincipals.BeginCheck(member)", "CheckProtectedAsync(member, actingUser)" },
        { "Services/GroupManagementService.cs", "public virtual async Task<PermissionResult> RemoveMemberAsync(",
            "_protectedPrincipals.BeginCheck(member)", "CheckProtectedAsync(member, actingUser)" },
        // S4c Group Management page: a confirmed batch's members, one operation.
        { "Components/Pages/GroupManagement.razor", "private async Task RemoveSelectedAsync()",
            "DirectoryReadOperation.Begin()", "await RemoveOneAsync(group, row, ticket, sendAdminEmail: false)" },
        { "Components/Pages/GroupManagement.razor", "private async Task AddResolvedAsync()",
            "DirectoryReadOperation.Begin()", "await AddOneAsync(group, row.Line.Text, row.Match!.DistinguishedName, ticket, sendAdminEmail: false)" },
        // S5 Emergency Disable: the lookup's resolution and its check (the disable's own re-check is one check of its own).
        { "Components/Pages/EmergencyDisable.razor", "private async Task PerformLookup()",
            "ProtectedPrincipalService.BeginCheck(identity)", "ProtectedPrincipalService.ResolveWithExchangeFallbackAsync(identity)" },
        // The Entra-user gate MFA Reset, Risky Users, Intune Devices, Microsoft 365 groups and Cloud Password Reset share: the
        // user's resolution by object id and its check (one fetch, proved in CloudUserProtectionTests).
        { "Services/ProtectedPrincipalServicing.cs", "public static async Task<CloudUserDecision> ForCloudUserAsync(",
            "protection.BeginCheck(userPrincipalName)", "protection.ResolveCloudUserAsync(" },
        // S7 Conference Rooms: no longer a protection call site - protected principals do not apply to it (owner ruling
        // 2026-11-03); see ConferenceRoomBinder_RunsNoProtectionCheck below.
        // S8 Blocked Senders: the sender's resolution and its check, or the raw address's check.
        { "Services/BlockedSenderProtectionGate.cs", "public async Task<BlockedSenderGateDecision> EvaluateAsync(string address,",
            "_protectedPrincipals.BeginCheck(address)", "_protectedPrincipals.ResolveWithExchangeFallbackAsync(address)" },
        // S11 Account Lockout Remediation: each target user's resolution, check and re-read; the guard and the SID step one operation.
        { "Services/AccountLockoutRemediationService.cs", "GuardTargetUsersAsync(\n",
            "_protectedPrincipals.BeginCheck(user)", "_protectedPrincipals.ResolveWithExchangeFallbackAsync(user)" },
        { "Services/AccountLockoutRemediationService.cs", "internal async Task<AccountLogoffResult> RunLogoffAsync(",
            "DirectoryReadOperation.Begin()", "await GuardTargetUsersAsync(" },
        // S15 Licensing Updates: a batch's rows, one operation.
        { "Services/LicensingUpdatesService.cs", "servicedNotesByUpn)> EvaluateProtectionAsync(",
            "DirectoryReadOperation.Begin()", "_protectedPrincipalService.CheckAsync(row.Principal!)" },
        // S16 Self-Service Groups: the write's DACL re-check (its resource-domain Deny reads are the evaluator's, item 1).
        { "Services/SelfServiceGroups/SelfServiceGroupService.cs", "private async Task<MembershipChangeResult> ApplyMembershipChangeAsync(",
            "DirectoryReadOperation.BeginCheck(_logger, ", "CallerCanManageMembers(ps, credential, groupDn, callerSid, callerGroupSids, denyOnlySids)" },
        // S9 Microsoft 365 group management: the member's or owner's resolution and its check.
        { "Services/M365GroupManagementService.cs", "private async Task<ProtectionGate> CheckProtectedAsync(string identity,",
            "_protectedPrincipals.BeginCheck(identity)", "_protectedPrincipals.ResolveWithExchangeFallbackAsync(identity)" },
        // S10 Migration: each mailbox's resolution and its check, and one operation for a batch's targets.
        { "Services/MigrationService.cs", "private async Task<ProtectionGate> CheckProtectedAsync(string identity,",
            "_protectedPrincipals.BeginCheck(identity)", "_protectedPrincipals.ResolveWithExchangeFallbackAsync(identity)" },
        { "Services/MigrationService.cs", "PartitionByProtectionAsync(",
            "DirectoryReadOperation.Begin()", "await checker(target)" },
    };

    [Theory]
    [MemberData(nameof(Sites))]
    public void TheOperationBeginsOneCheck_BeforeItsFirstProtectionRead(string file, string signature, string begin, string firstRead)
    {
        var body = Method(file.Split('/'), signature);

        var begun = body.IndexOf(begin, StringComparison.Ordinal);
        var read = body.IndexOf(firstRead, StringComparison.Ordinal);
        Assert.True(read >= 0, $"{firstRead} not found in {signature} - the tripwire is stale.");
        Assert.True(begun >= 0 && begun < read,
            $"{file} {signature}: no protected-principal check is begun before {firstRead} - each read fetches the credential for itself.");
    }

    /// <summary>
    /// docs/ProtectedPrincipalIdentifiers-Plan.md D.2/D.3 (slice S4): Conference Rooms runs no protected-principal check of any
    /// kind (owner ruling 2026-11-03). Its binder binds the room and nothing else - it begins no check, runs none, reads no
    /// rules and consults no servicer. The behavioural half is <c>ConferenceRoomBinderTests</c>.
    /// </summary>
    [Fact]
    public void ConferenceRoomBinder_RunsNoProtectionCheck()
    {
        var code = ProgressScan.CodeView(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "ConferenceRoomBinder.cs")));

        Assert.Contains("BindSyncedRecipientAsync(", code, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "CheckAsync(", "BeginCheck(", "ResolveExchangeTargetAsync(", "CheckWriteTarget(", "LoadEffectiveConfig(",
                     "ProtectedPrincipalServicerService", ".Evaluate(", "ClaimsPrincipal",
                 })
        {
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }
    }
}
