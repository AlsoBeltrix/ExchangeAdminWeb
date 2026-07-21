using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Authorization;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// Off-circuit authorization for bulk jobs - option (a) (owner, 2026-07-02; docs/BulkJobRunner-Plan.md).
///
/// The app authorizes entirely against a live <see cref="ClaimsPrincipal"/>'s role claims; there is
/// no SAM->groups lookup, so a job worker thread (no live principal) cannot re-run the exact live
/// check. Instead, at submit time - on the circuit, where the principal is present - we capture the
/// submitter's role claims and the section (policy alias) into this snapshot, persisted on the job.
/// The runner then re-checks the snapshot against the section's current allowed-group set per row
/// using the same <see cref="GroupMembershipChecker"/> the live handler uses.
///
/// Honest scope: this authorizes the submission and re-checks the captured claims; it does NOT detect
/// mid-job group-membership revocation. Neither does today's live one-check-per-loop model, so this
/// is parity, not a regression. Submission is still gated up front by the normal policy check on the
/// circuit before a job is ever enqueued.
/// </summary>
public sealed class JobAuthorizationSnapshot
{
    /// <summary>The policy alias / section the job authorizes against (e.g. "ConferenceRooms").</summary>
    public required string Section { get; init; }

    /// <summary>The submitter's role-claim values captured at submit time (kept for audit/debug).</summary>
    public required IReadOnlyList<string> RoleClaims { get; init; }

    /// <summary>
    /// The section's allowed groups the submitter actually satisfied at submit time - the captured
    /// authorization DECISION, not raw claims. Computed on the circuit using the same match the live
    /// handler uses (role claims AND <see cref="ClaimsPrincipal.IsInRole"/>), so a user authorized
    /// only via a Windows token role (common when role claims are SIDs but groups are configured by
    /// name) is captured correctly and does not fail closed off-circuit.
    /// </summary>
    public required IReadOnlyList<string> AuthorizedGroups { get; init; }

    /// <summary>
    /// Captures the authorization decision from a live principal at submit time: evaluates each of
    /// <paramref name="allowedGroupsForSection"/> with the full check (claims + IsInRole) and records
    /// the groups the user satisfied. Must be called on the circuit, where the principal is live.
    /// </summary>
    public static JobAuthorizationSnapshot Capture(ClaimsPrincipal user, string section, IReadOnlyList<string> allowedGroupsForSection)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(allowedGroupsForSection);

        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var authorized = new List<string>();
        foreach (var group in allowedGroupsForSection)
        {
            if (string.IsNullOrWhiteSpace(group))
                continue;
            var normalized = group.Contains('\\') ? group.Split('\\')[1] : group;
            // Full live check: token roles (IsInRole) OR role claims - mirrors GroupAuthorizationHandler.
            if (user.IsInRole(group) || user.IsInRole(normalized) || GroupMembershipChecker.IsMemberOfAny(roles, [group]))
                authorized.Add(group);
        }

        return new JobAuthorizationSnapshot { Section = section, RoleClaims = roles, AuthorizedGroups = authorized };
    }

    /// <summary>
    /// Re-evaluates the captured decision against the section's CURRENT allowed-group set: still
    /// authorized only if a group that authorized the submitter at submit time is still allowed now.
    /// Fails closed when nothing was captured or the authorizing group was removed from config. This
    /// is the per-row gate the runner applies before any write.
    /// </summary>
    public bool IsStillAuthorized(IReadOnlyList<string> allowedGroupsForSection)
    {
        if (AuthorizedGroups is null || AuthorizedGroups.Count == 0 || allowedGroupsForSection is null)
            return false;
        return AuthorizedGroups.Any(captured =>
            allowedGroupsForSection.Any(current => current.Equals(captured, StringComparison.OrdinalIgnoreCase)));
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Deserializes a snapshot; returns null for null/blank/invalid JSON so callers fail closed.</summary>
    public static JobAuthorizationSnapshot? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<JobAuthorizationSnapshot>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
