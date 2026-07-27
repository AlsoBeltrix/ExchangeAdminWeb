using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// Outcome of a self-service membership change (plan docs/SelfServiceGroupManagement-Plan.md
/// section 6.5). Wraps the existing <see cref="PermissionResult"/> the page shows the user, and
/// additively carries the facts the page needs to satisfy the mandatory notifications (AC10,
/// Constitution "Notifications") that are ONLY known inside the service: the affected member's
/// resolved primary SMTP address and display name, and whether the target group is a SECURITY group.
/// The service intentionally owns none of the audit/notify itself - per <see cref="SelfServiceGroupService.ChangeMemberAsync"/>'s
/// documented contract, audit and notification are the caller's (page) responsibility; this record is
/// how the service hands the caller enough to do so without a second directory lookup (the codex F1
/// anti-pattern of re-resolving the member is avoided - the SMTP/display come from the SAME single
/// resolution the write used).
/// </summary>
/// <param name="Result">The user-facing success/failure the page renders.</param>
/// <param name="AffectedMemberEmail">The resolved member's primary SMTP, when known; null on paths that
/// failed before/without a successful resolution.</param>
/// <param name="AffectedMemberDisplayName">The resolved member's display name, when known.</param>
/// <param name="IsSecurityGroup">True when the target group is a security group (distribution groups do
/// not get an affected-user notification - Constitution scopes affected-user notify to access changes,
/// and on-prem security-group membership is the access-bearing case, plan AC10).</param>
/// <param name="MembershipChanged">True only when a real add/remove was applied (NOT an idempotent
/// already-satisfied no-op, NOT a denied/failed attempt). Gates the affected-user notification so an
/// unchanged membership does not email the member.</param>
public sealed record MembershipChangeResult(
    PermissionResult Result,
    string? AffectedMemberEmail = null,
    string? AffectedMemberDisplayName = null,
    bool IsSecurityGroup = false,
    bool MembershipChanged = false)
{
    /// <summary>
    /// The affected user must be notified only when the change actually succeeded, actually altered
    /// membership, the group is security (access-bearing), and we have an address to reach them at.
    /// Pure decision so it is unit-testable without AD/email (Known Failure Class coverage).
    /// </summary>
    public bool NotifyAffectedUser =>
        Result.Success
        && MembershipChanged
        && IsSecurityGroup
        && !string.IsNullOrWhiteSpace(AffectedMemberEmail);

    /// <summary>Wraps a bare result for the paths that carry no member/group notify metadata.</summary>
    public static MembershipChangeResult From(PermissionResult result) => new(result);
}
