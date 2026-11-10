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

    /// <summary>Why <see cref="ForCloudUserAsync"/> refused, or <see cref="None"/> when it did not.</summary>
    public enum CloudUserRefusal
    {
        None,

        /// <summary>The user's on-premises account could not be told apart from another.</summary>
        Ambiguous,

        /// <summary>What protection depends on could not be read: refused, never read as "not protected".</summary>
        Unavailable,

        /// <summary>The rules could not be evaluated; <see cref="CloudUserDecision.FailReason"/> says why.</summary>
        CheckFailed,

        /// <summary>The user is protected and this operator may not service it.</summary>
        Protected,

        /// <summary>Asked for the user by its UPN, Graph holds no such user.</summary>
        NotFound,
    }

    /// <summary>
    /// Outcome of <see cref="ForCloudUserAsync"/>. The caller words the refusal for its audience; <paramref name="FailReason"/>
    /// is the check's own reason for <see cref="CloudUserRefusal.CheckFailed"/>. <paramref name="MatchedRules"/> are the rules
    /// that matched a protected user; <paramref name="Unresolved"/> is true when neither Active Directory nor Exchange knew the
    /// user, so it was checked by its UPN and object id alone. <paramref name="ServicedNote"/> is set only when an authorised
    /// servicer overrode the refusal and must reach the audit call's extra.
    /// </summary>
    public sealed record CloudUserDecision(
        CloudUserRefusal Refusal, string? FailReason, IReadOnlyList<string> MatchedRules, bool Unresolved, string? ServicedNote)
    {
        public bool Allowed => Refusal == CloudUserRefusal.None;

        /// <summary>The audit detail of a <see cref="CloudUserRefusal.Protected"/> refusal, naming the rules that matched.</summary>
        public string ProtectedAuditDetail =>
            $"Protected principal{(Unresolved ? " (directory-unresolved)" : "")} - matched rules: {string.Join(", ", MatchedRules)}";
    }

    /// <summary>
    /// The one protected-principal gate for a write to an Entra user: may <paramref name="actingUser"/> change the user
    /// whose object id is <paramref name="entraObjectId"/>? <paramref name="userPrincipalName"/> names it; a write that names
    /// its user by UPN alone passes no object id, and the user is read from Graph by that UPN.
    /// </summary>
    /// <remarks>
    /// The user is resolved by its object id (<see cref="ProtectedPrincipalService.ResolveCloudUserAsync"/>), so the object
    /// checked is the object the write touches, and a synced user's account is found by its on-premises SID in whatever domain
    /// holds it. Fail closed: a blank UPN, a user Graph does not hold, an ambiguous or unavailable resolution and a failed
    /// check each refuse. A protected user proceeds only for an authorised servicer, with the note naming the group and the
    /// rules it overrode - qualified "directory-unresolved" when the user was checked by UPN and object id alone. One timed
    /// check, one directory-read credential fetch. An exception is the caller's to catch: it says nothing about the user, so
    /// it refuses.
    /// </remarks>
    public static async Task<CloudUserDecision> ForCloudUserAsync(
        ProtectedPrincipalService protection,
        ProtectedPrincipalServicerService servicers,
        string? entraObjectId,
        string? userPrincipalName,
        ClaimsPrincipal? actingUser,
        string moduleId)
    {
        if (string.IsNullOrWhiteSpace(userPrincipalName))
            return new(CloudUserRefusal.Unavailable, null, [], false, null);

        using var check = protection.BeginCheck(userPrincipalName);
        var (principal, status, unresolved) = await protection.ResolveCloudUserAsync(entraObjectId?.Trim(), userPrincipalName);
        if (status == ProtectedPrincipalService.ResolutionStatus.Ambiguous)
            return new(CloudUserRefusal.Ambiguous, null, [], false, null);
        if (status == ProtectedPrincipalService.ResolutionStatus.NotFound)
            return new(CloudUserRefusal.NotFound, null, [], false, null);
        if (status != ProtectedPrincipalService.ResolutionStatus.Resolved || principal is null)
            return new(CloudUserRefusal.Unavailable, null, [], false, null);

        var result = await protection.CheckAsync(principal);
        if (result.CheckFailed)
            return new(CloudUserRefusal.CheckFailed, result.Reason, [], unresolved, null);
        if (!result.IsProtected)
            return new(CloudUserRefusal.None, null, [], unresolved, null);

        var note = NoteFor(servicers, actingUser, moduleId, result.MatchedRules, unresolved ? "directory-unresolved" : null);
        return note is null
            ? new(CloudUserRefusal.Protected, null, result.MatchedRules, unresolved, null)
            : new(CloudUserRefusal.None, null, result.MatchedRules, unresolved, note);
    }

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
    /// FIRST via <see cref="ProtectedPrincipalService.CheckWriteTargetAsync"/> and never weakened;
    /// fail-closed outranks servicing (a failed or errored check denies with its reason, AC5);
    /// a null acting principal refuses; the grant is per module; the note names the authorising
    /// group and the rules overridden, qualified "write target" so an audit can tell a serviced
    /// TARGET from a serviced MEMBER. Two hand-written gates is how they come to disagree about
    /// what "protected" means - this is the only one.
    /// </remarks>
    public static async Task<WriteTargetDecision> ForWriteTargetAsync(
        ProtectedPrincipalService protection,
        ProtectedPrincipalServicerService servicers,
        ResolvedDirectoryPrincipal targetGroup,
        ClaimsPrincipal? actingUser,
        string moduleId)
    {
        try
        {
            // rev-b3: the full check - its OU rule is read from the directory.
            var check = await protection.CheckWriteTargetAsync(targetGroup);
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
