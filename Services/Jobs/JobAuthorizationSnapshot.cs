using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Authorization;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// Off-circuit authorization for bulk jobs — option (a) (owner, 2026-07-02; docs/BulkJobRunner-Plan.md).
///
/// The app authorizes entirely against a live <see cref="ClaimsPrincipal"/>'s role claims; there is
/// no SAM→groups lookup, so a job worker thread (no live principal) cannot re-run the exact live
/// check. Instead, at submit time — on the circuit, where the principal is present — we capture the
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

    /// <summary>The submitter's role-claim values captured at submit time.</summary>
    public required IReadOnlyList<string> RoleClaims { get; init; }

    /// <summary>Captures the role claims (and section) from a live principal at submit time.</summary>
    public static JobAuthorizationSnapshot Capture(ClaimsPrincipal user, string section)
    {
        ArgumentNullException.ThrowIfNull(user);
        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new JobAuthorizationSnapshot { Section = section, RoleClaims = roles };
    }

    /// <summary>
    /// Re-evaluates the captured snapshot against the section's current allowed-group set. Returns
    /// true only if the snapshot still matches an allowed group — fail closed on an empty/unknown
    /// group set (mirrors the handler). This is the per-row gate the runner applies before any write.
    /// </summary>
    public bool IsStillAuthorized(IReadOnlyList<string> allowedGroupsForSection)
        => GroupMembershipChecker.IsMemberOfAny(RoleClaims, allowedGroupsForSection);

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
