namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// The pure, principal-free core of the section-access group match. Extracted from
/// <see cref="GroupAuthorizationHandler"/> so the exact same comparison runs in two places that must
/// not diverge: the live authorization handler (on a circuit, with a ClaimsPrincipal) and the bulk
/// job runner's per-row re-check (off-circuit, with only a captured snapshot of the submitter's role
/// claims — see docs/BulkJobRunner-Plan.md, off-circuit authorization option (a)).
///
/// The app has no SAM→groups lookup: authorization is entirely claims-based. A job worker thread has
/// no live principal, so it re-evaluates access against the role claims captured at submit time using
/// this function. That authorizes the submission and re-checks the snapshot per row; it does not
/// detect mid-job group-membership revocation — which the live one-check-per-loop model also does not
/// detect today (accepted, matches current behavior).
/// </summary>
public static class GroupMembershipChecker
{
    /// <summary>
    /// True when any of <paramref name="roleClaims"/> matches any of <paramref name="allowedGroups"/>,
    /// comparing case-insensitively and treating a "DOMAIN\group" allowed value as matching either its
    /// full form or the bare group name (mirroring the handler's normalization). An empty allowed set
    /// returns false (fail closed — no groups configured means deny), matching the handler.
    /// </summary>
    public static bool IsMemberOfAny(IEnumerable<string>? roleClaims, IEnumerable<string>? allowedGroups)
    {
        if (roleClaims is null || allowedGroups is null)
            return false;

        var claims = roleClaims as ICollection<string> ?? roleClaims.ToList();
        if (claims.Count == 0)
            return false;

        foreach (var allowedGroup in allowedGroups)
        {
            if (string.IsNullOrWhiteSpace(allowedGroup))
                continue;

            var normalized = allowedGroup.Contains('\\')
                ? allowedGroup.Split('\\')[1]
                : allowedGroup;

            foreach (var claim in claims)
            {
                if (claim.Equals(allowedGroup, StringComparison.OrdinalIgnoreCase)
                    || claim.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
