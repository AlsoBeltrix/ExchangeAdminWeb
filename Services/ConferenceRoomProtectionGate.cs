using System.Security.Claims;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// A protected-principal denial: the operator-facing <see cref="Message"/> and the
/// <see cref="AuditDetail"/> the caller records. Kept separate so each caller audits the denial
/// under its OWN action label (single-room Finder vs Type vs the bulk <c>_Bulk</c> actions) and
/// context, while the protection decision itself lives in exactly one place.
/// </summary>
public sealed record ProtectionDenial(string Message, string AuditDetail);

/// <summary>
/// The outcome of the Conference Rooms protection gate: proceed, proceed as an authorised servicer,
/// or refuse.
/// </summary>
/// <remarks>
/// A servicer allow carries <see cref="ServicedAuditDetail"/>, which the caller MUST record in the
/// audit event's <c>extra</c> - never as an error detail, since a serviced action succeeds and
/// every audit method drops the error field on success.
/// </remarks>
public sealed record ProtectionOutcome(ProtectionDenial? Denial, string? ServicedAuditDetail)
{
    public bool IsDenied => Denial is not null;

    public static ProtectionOutcome Allow() => new(null, null);
    public static ProtectionOutcome AllowAsServicer(string auditDetail) => new(null, auditDetail);
    public static ProtectionOutcome Deny(ProtectionDenial denial) => new(denial, null);
}

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
    /// <summary>Module id for the servicer grant. Must match the catalog descriptor.</summary>
    public const string ModuleId = "ConferenceRooms";

    private readonly ProtectedPrincipalService _protectedPrincipals;
    private readonly ProtectedPrincipalServicerService _servicers;
    private readonly ILogger<ConferenceRoomProtectionGate> _logger;

    public ConferenceRoomProtectionGate(
        ProtectedPrincipalService protectedPrincipals,
        ProtectedPrincipalServicerService servicers,
        ILogger<ConferenceRoomProtectionGate> logger)
    {
        _protectedPrincipals = protectedPrincipals;
        _servicers = servicers;
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
        => await GuardThenRunAsync(identity, user: null, onDenied, _ => onAllowed());

    /// <summary>
    /// As above, but consults the authorised-servicer grant for <paramref name="user"/> when the
    /// target turns out to be protected.
    /// </summary>
    /// <remarks>
    /// The principal is a REQUIRED parameter with no default, deliberately. An off-circuit bulk job
    /// has no operator, and a defaulted or ambient principal there would either fail open or
    /// attribute a bypass to whoever happened to be on the thread. Bulk callers pass null
    /// explicitly, which denies - see <see cref="Jobs.ConferenceRoomBulkProcessor"/>.
    ///
    /// <paramref name="onAllowed"/> receives the serviced audit detail: null on an ordinary allow,
    /// and a description of the overridden rules when a servicer authorised it. The caller must put
    /// that in the audit event's <c>extra</c>, because a serviced action SUCCEEDS and the error
    /// field is dropped on success.
    /// </remarks>
    public async Task<TResult> GuardThenRunAsync<TResult>(
        string identity,
        ClaimsPrincipal? user,
        Func<ProtectionDenial, TResult> onDenied,
        Func<string?, Task<TResult>> onAllowed)
    {
        var outcome = await EvaluateAsync(identity, user);
        if (outcome.Denial is not null)
            return onDenied(outcome.Denial);
        return await onAllowed(outcome.ServicedAuditDetail);
    }

    /// <summary>
    /// The protection decision. Returns a <see cref="ProtectionDenial"/> to block, or null to allow.
    /// Fail-closed: Unavailable / Ambiguous / CheckFailed / any exception all deny. NotFound is
    /// treated as not protected - an accepted limitation, and a much narrower one since resolution
    /// began falling back to Exchange: a mailbox reached by a secondary SMTP alias, a mail-enabled
    /// group, and a cloud-only mailbox all used to land here as NotFound and be allowed through.
    /// The alias case was a real bypass, because protected rows are stored as primary addresses.
    /// NotFound now means both directories were asked and neither knows the recipient.
    /// </summary>
    private async Task<ProtectionOutcome> EvaluateAsync(string identity, ClaimsPrincipal? user)
    {
        try
        {
            var (resolved, status) = await _protectedPrincipals.ResolveWithExchangeFallbackAsync(identity);
            if (status is ProtectedPrincipalService.ResolutionStatus.Unavailable or ProtectedPrincipalService.ResolutionStatus.Ambiguous)
            {
                var reason = status == ProtectedPrincipalService.ResolutionStatus.Ambiguous
                    ? "Identity is ambiguous - matches multiple AD users."
                    : "Protection check unavailable.";
                return ProtectionOutcome.Deny(new ProtectionDenial(reason, $"{reason} - blocked"));
            }
            if (resolved != null)
            {
                var check = await _protectedPrincipals.CheckAsync(resolved);
                if (check.CheckFailed)
                {
                    var msg = $"Protection check failed: {check.Reason}";
                    return ProtectionOutcome.Deny(new ProtectionDenial(msg, msg));
                }
                if (check.IsProtected)
                {
                    var rules = string.Join(", ", check.MatchedRules);

                    // The target IS protected, and that result is never weakened - the servicer
                    // check only decides whether THIS operator may act on a target already known
                    // to be protected. A null user (bulk job) denies, because Evaluate refuses a
                    // null principal.
                    var servicer = _servicers.Evaluate(user, ModuleId);
                    if (servicer.Allowed)
                    {
                        _logger.LogInformation(
                            "Authorised servicer acting on protected room {Identity} - matched rules: {Rules}, authorised by {Group}",
                            identity, rules, servicer.ServicerGroup);

                        return ProtectionOutcome.AllowAsServicer(
                            $"Protected principal serviced by an authorised operator - matched rules: {rules}; authorised by {servicer.ServicerGroup}");
                    }

                    return ProtectionOutcome.Deny(new ProtectionDenial(
                        "This is a protected principal. Operation not permitted.",
                        $"Protected principal - matched rules: {rules}"));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Protected principal check failed for {Identity} - blocking as precaution", identity);
            return ProtectionOutcome.Deny(new ProtectionDenial($"Protection check error: {ex.Message}", $"Protection check exception: {ex.Message}"));
        }
        return ProtectionOutcome.Allow();
    }
}
