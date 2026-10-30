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
        // S5 Emergency Disable: the lookup's resolution and its check (the disable's own re-check is one check of its own).
        { "Components/Pages/EmergencyDisable.razor", "private async Task PerformLookup()",
            "ProtectedPrincipalService.BeginCheck(identity)", "ProtectedPrincipalService.ResolveWithExchangeFallbackAsync(identity)" },
        // S6 Cloud Password Reset: the admin account's resolution and its check.
        { "Components/Pages/CloudPasswordReset.razor", "CheckProtectionAsync(System.Security.Claims.ClaimsPrincipal user, string upn, string entraObjectId)",
            "ProtectedPrincipalService.BeginCheck(upn)", "ProtectedPrincipalService.ResolveWithExchangeFallbackAsync(upn)" },
        // S7 Conference Rooms: the room mailbox's resolution and its check, single and bulk.
        { "Services/ConferenceRoomProtectionGate.cs", "private async Task<ProtectionOutcome> EvaluateAsync(string identity,",
            "_protectedPrincipals.BeginCheck(identity)", "_protectedPrincipals.ResolveExchangeTargetAsync(identity)" },
        // S8 Blocked Senders: the sender's resolution and its check, or the raw address's check.
        { "Services/BlockedSenderProtectionGate.cs", "public async Task<BlockedSenderGateDecision> EvaluateAsync(string address,",
            "_protectedPrincipals.BeginCheck(address)", "_protectedPrincipals.ResolveWithExchangeFallbackAsync(address)" },
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
}
