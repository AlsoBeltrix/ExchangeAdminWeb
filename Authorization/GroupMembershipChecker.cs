using System.Security.Claims;

namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// The pure, principal-free core of the section-access group match. Extracted from
/// <see cref="GroupAuthorizationHandler"/> so the exact same comparison runs in two places that must
/// not diverge: the live authorization handler (on a circuit, with a ClaimsPrincipal) and the bulk
/// job runner's per-row re-check (off-circuit, with only a captured snapshot of the submitter's
/// group SIDs - see docs/BulkJobRunner-Plan.md, off-circuit authorization option (a)).
///
/// The app has no SAM->groups lookup: authorization is entirely claims-based. A job worker thread has
/// no live principal, so it re-evaluates access against the claims captured at submit time using
/// this function. That authorizes the submission and re-checks the snapshot per row; it does not
/// detect mid-job group-membership revocation - which the live one-check-per-loop model also does not
/// detect today (accepted, matches current behavior).
/// </summary>
public static class GroupMembershipChecker
{
    /// <summary>
    /// The claim types carrying a principal's group membership, in the order they are searched.
    /// </summary>
    /// <remarks>
    /// <c>GroupSid</c> is what actually matters here. Measured on this deployment: a Negotiate
    /// principal carries 333 group entries, every one of them a SID, and
    /// <c>ClaimTypes.Role</c> is empty on every request - prod logged 1687 authorizations through
    /// the token path and 0 through the claims path. <c>Role</c> and <c>PrimaryGroupSid</c> are
    /// searched too because a snapshot captured by an older build holds role claims, and neither
    /// costs anything when absent.
    /// </remarks>
    public static readonly string[] GroupClaimTypes =
    [
        ClaimTypes.GroupSid,
        ClaimTypes.PrimaryGroupSid,
        ClaimTypes.Role
    ];

    /// <summary>
    /// The allowed values usable as authorization subjects - the SIDs. Anything else is a row the
    /// migration has not converted yet and must not be compared against; see the remarks on
    /// <see cref="IsMemberOfAny"/>.
    /// </summary>
    public static List<string> UsableSidsOnly(IEnumerable<string>? allowedGroups)
        => allowedGroups?.Where(SectionAccessGroupIdentity.IsUsableGroupSid).ToList() ?? [];

    /// <summary>
    /// Every group identifier a principal carries, across all of <see cref="GroupClaimTypes"/>.
    /// </summary>
    public static List<string> ExtractGroupClaims(ClaimsPrincipal? user)
    {
        if (user is null)
            return [];

        return user.Claims
            .Where(c => GroupClaimTypes.Contains(c.Type, StringComparer.OrdinalIgnoreCase))
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True when any of <paramref name="groupClaims"/> equals any of <paramref name="allowedGroups"/>,
    /// compared case-insensitively and exactly. An empty allowed set returns false (fail closed - no
    /// groups configured means deny), matching the handler.
    /// </summary>
    /// <remarks>
    /// The comparison is EXACT. It previously also matched a stored <c>DOMAIN\group</c> against a
    /// bare <c>group</c> claim, which was the defect: it made
    /// <c>ANALOG\ExchangeWebAdmins</c> and a foreign domain's <c>ExchangeWebAdmins</c>
    /// indistinguishable, in the field that decides who reaches a privileged module. Stored values
    /// are SIDs now (docs/SectionAccessSidStorage-Plan.md), which are self-qualifying, so no
    /// normalization is needed or wanted - re-adding any would reopen the hole.
    ///
    /// A non-SID allowed value is DISCARDED, not compared. Exact comparison alone does not make an
    /// unmigrated store fail closed, which an earlier version of this comment wrongly claimed:
    /// measured on a domain-joined host, <c>WindowsPrincipal.IsInRole</c> resolves names as well as
    /// SIDs (<c>IsInRole("Domain Users")</c> is true), and a role claim can carry a name too. So
    /// while the migration is deferred or halted, a name-valued row would authorize exactly as it
    /// did before this work - the same-name ambiguity intact, during precisely the window the
    /// migration was designed to survive. Discarding makes the fail-closed property real:
    /// a section whose rows are all names denies everyone until the migration completes, which is
    /// the correct trade against authorizing on an identifier the app cannot disambiguate.
    /// Review finding sid-1.
    /// </remarks>
    public static bool IsMemberOfAny(IEnumerable<string>? groupClaims, IEnumerable<string>? allowedGroups)
    {
        if (groupClaims is null || allowedGroups is null)
            return false;

        var claims = groupClaims as ICollection<string> ?? groupClaims.ToList();
        if (claims.Count == 0)
            return false;

        foreach (var allowedGroup in allowedGroups)
        {
            if (!SectionAccessGroupIdentity.IsUsableGroupSid(allowedGroup))
                continue;

            foreach (var claim in claims)
            {
                if (claim.Equals(allowedGroup, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
