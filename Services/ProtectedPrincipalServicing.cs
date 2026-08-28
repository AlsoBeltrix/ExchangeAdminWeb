using System.Security.Claims;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// The two things every module needs when it honours a protected-principal servicer grant:
/// decide whether this operator may proceed, and describe it for the audit record.
/// </summary>
/// <remarks>
/// Extracted rather than copied into each page. Fifteen modules apply the same rule, and the rule
/// has two parts that are easy to get subtly wrong in isolation:
///
/// 1. The note must NAME the authorising group and the rules it overrode. A record saying only
///    "serviced" cannot answer who permitted it, which is the question an audit exists for.
/// 2. It must travel in the audit event's <c>extra</c>, never <c>errorDetail</c>. Every audit
///    method writes <c>["error"] = success ? null : errorDetail</c>, and a serviced action
///    SUCCEEDS - so the failure channel silently discards it. That defect already shipped once
///    (Blocked Senders, blr-era) and is the single most repeatable mistake in this work.
///
/// This does NOT decide protection. Callers evaluate protection first and only reach here for a
/// target already known to be protected; nothing in this file can weaken a protection result.
/// </remarks>
public static class ProtectedPrincipalServicing
{
    /// <summary>The audit key under which a serviced action is recorded, identical everywhere.</summary>
    public const string AuditKey = "protectedPrincipalServiced";

    /// <summary>
    /// A description of the override when <paramref name="user"/> may service protected principals
    /// in <paramref name="moduleId"/>, or null when they may not.
    /// </summary>
    /// <remarks>
    /// Null means REFUSE. Returning a nullable note rather than a bool keeps the two facts a caller
    /// needs - may they proceed, and what should be recorded - impossible to separate, so a caller
    /// cannot allow the action while forgetting to record why.
    ///
    /// A null <paramref name="user"/> refuses, which is what makes off-circuit bulk work safe by
    /// default: <c>Evaluate</c> denies a null principal.
    /// </remarks>
    public static string? NoteFor(
        ProtectedPrincipalServicerService servicers,
        ClaimsPrincipal? user,
        string moduleId,
        IEnumerable<string> matchedRules,
        string? qualifier = null)
    {
        var decision = servicers.Evaluate(user, moduleId);
        if (!decision.Allowed)
            return null;

        var rules = string.Join(", ", matchedRules);
        var scope = string.IsNullOrWhiteSpace(qualifier) ? "" : $" ({qualifier})";

        return $"Protected principal{scope} serviced by an authorised operator - "
             + $"matched rules: {rules}; authorised by {decision.ServicerGroup}";
    }

    /// <summary>
    /// Wraps a serviced note for an audit call's <c>extra</c>, or null when nothing was serviced.
    /// </summary>
    public static Dictionary<string, object?>? Extra(string? servicedNote) =>
        string.IsNullOrWhiteSpace(servicedNote)
            ? null
            : new Dictionary<string, object?> { [AuditKey] = servicedNote };

    /// <summary>
    /// Outcome of the shared write-target gate (docs/ProtectedGroupWriteTarget-Plan.md T1).
    /// <paramref name="Allowed"/> false with a null <paramref name="FailReason"/> means the
    /// target is protected and unserviced - the CALLER supplies its audience's refusal wording
    /// (admin vs self-service); a non-null <paramref name="FailReason"/> is a fail-closed check
    /// failure and is used verbatim. <paramref name="ServicedNote"/> is set only when an
    /// authorised servicer overrode the refusal and must reach the audit call's extra.
    /// </summary>
    public sealed record WriteTargetDecision(bool Allowed, string? FailReason, string? ServicedNote);

    /// <summary>
    /// The one write-target gate every on-prem group module consults (plan T1): may members be
    /// added to or removed from <paramref name="targetGroup"/>?
    /// </summary>
    /// <remarks>
    /// The invariants are the servicer stream's, not re-litigated: protection is evaluated
    /// FIRST via <see cref="ProtectedPrincipalService.CheckWriteTarget"/> and never weakened;
    /// fail-closed outranks servicing (a failed or errored check denies with its reason, AC5);
    /// a null acting principal refuses; the grant is per module; the note names the authorising
    /// group and the rules overridden, qualified "write target" so an audit can tell a serviced
    /// TARGET from a serviced MEMBER. Two hand-written gates is how they come to disagree about
    /// what "protected" means - this is the only one.
    /// </remarks>
    public static WriteTargetDecision ForWriteTarget(
        ProtectedPrincipalService protection,
        ProtectedPrincipalServicerService servicers,
        ResolvedDirectoryPrincipal targetGroup,
        ClaimsPrincipal? actingUser,
        string moduleId)
    {
        try
        {
            var check = protection.CheckWriteTarget(targetGroup);
            if (check.CheckFailed)
                return new(false, $"Protection check failed: {check.Reason}", null);
            if (!check.IsProtected)
                return new(true, null, null);

            var note = NoteFor(servicers, actingUser, moduleId, check.MatchedRules, qualifier: "write target");
            return note is null ? new(false, null, null) : new(true, null, note);
        }
        catch (Exception ex)
        {
            // Fail closed: an errored check says nothing about the target (AC5).
            return new(false, $"Protection check error: {ex.Message}", null);
        }
    }
}
