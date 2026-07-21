namespace ExchangeAdminWeb.Services;

/// <summary>
/// A protected-principal denial: the operator-facing <see cref="Message"/> and the
/// <see cref="AuditDetail"/> the caller records. Kept separate so each caller audits the denial
/// under its OWN action label (single-room Finder vs Type vs the bulk <c>_Bulk</c> actions) and
/// context, while the protection decision itself lives in exactly one place.
/// </summary>
public sealed record ProtectionDenial(string Message, string AuditDetail);

/// <summary>
/// The single protected-principal enforcement point for every ConferenceRooms room-mutating write
/// (single-room Finder + Type on the page, and each bulk row in
/// <see cref="Jobs.ConferenceRoomBulkProcessor"/>). Consolidates what used to be three near-duplicate
/// copies of the check into one guarded-execution helper so the gate runs exactly once per write and
/// no path can write without passing it.
///
/// The write is reachable only through the <c>onAllowed</c> delegate, which the caller invokes to open
/// its own trace scope and perform the mutation - so the protection decision is fully made BEFORE any
/// side effect (Known Failure Class #1, fail-closed authorization). This is module-scoped on purpose:
/// keeping the combined check-then-run out of the shared <see cref="ProtectedPrincipalService"/> keeps
/// this a ConferenceRooms-only change (module-version bump, no app-version bump).
/// </summary>
public sealed class ConferenceRoomProtectionGate
{
    private readonly ProtectedPrincipalService _protectedPrincipals;
    private readonly ILogger<ConferenceRoomProtectionGate> _logger;

    public ConferenceRoomProtectionGate(
        ProtectedPrincipalService protectedPrincipals,
        ILogger<ConferenceRoomProtectionGate> logger)
    {
        _protectedPrincipals = protectedPrincipals;
        _logger = logger;
    }

    /// <summary>
    /// Evaluate the protected-principal gate for <paramref name="identity"/> exactly once, then run
    /// exactly one of the two delegates: <paramref name="onDenied"/> when the target is protected or
    /// the check cannot be trusted (fail closed), otherwise <paramref name="onAllowed"/>. The write
    /// must live inside <paramref name="onAllowed"/> so it is unreachable on any deny path.
    /// </summary>
    public async Task<TResult> GuardThenRunAsync<TResult>(
        string identity,
        Func<ProtectionDenial, TResult> onDenied,
        Func<Task<TResult>> onAllowed)
    {
        var denial = await EvaluateAsync(identity);
        if (denial is not null)
            return onDenied(denial);
        return await onAllowed();
    }

    /// <summary>
    /// The protection decision. Returns a <see cref="ProtectionDenial"/> to block, or null to allow.
    /// Fail-closed: Unavailable / Ambiguous / CheckFailed / any exception all deny. NotFound (AD could
    /// not resolve, e.g. a cloud-only mailbox) is treated as not protected - an accepted, documented
    /// limitation consistent with the other gated modules.
    /// </summary>
    private async Task<ProtectionDenial?> EvaluateAsync(string identity)
    {
        try
        {
            var (resolved, status) = await _protectedPrincipals.ResolveWithStatusAsync(identity);
            if (status is ProtectedPrincipalService.ResolutionStatus.Unavailable or ProtectedPrincipalService.ResolutionStatus.Ambiguous)
            {
                var reason = status == ProtectedPrincipalService.ResolutionStatus.Ambiguous
                    ? "Identity is ambiguous - matches multiple AD users."
                    : "Protection check unavailable.";
                return new ProtectionDenial(reason, $"{reason} - blocked");
            }
            if (resolved != null)
            {
                var check = await _protectedPrincipals.CheckAsync(resolved);
                if (check.CheckFailed)
                {
                    var msg = $"Protection check failed: {check.Reason}";
                    return new ProtectionDenial(msg, msg);
                }
                if (check.IsProtected)
                    return new ProtectionDenial(
                        "This is a protected principal. Operation not permitted.",
                        $"Protected principal - matched rules: {string.Join(", ", check.MatchedRules)}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Protected principal check failed for {Identity} - blocking as precaution", identity);
            return new ProtectionDenial($"Protection check error: {ex.Message}", $"Protection check exception: {ex.Message}");
        }
        return null;
    }
}
