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
}
