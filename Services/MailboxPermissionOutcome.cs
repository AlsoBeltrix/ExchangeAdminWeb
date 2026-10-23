using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// One access right's fate in a multi-right permission operation.
/// </summary>
/// <param name="Right">"FullAccess" or "SendAs".</param>
/// <param name="Error">null when the right was applied; the failure text otherwise.</param>
/// <param name="Unconfirmed">
/// aud-a70 (14). The right's command was sent and its session failed before Exchange answered: whether it took is unknown.
/// False for a right Exchange refused - it answered - and for one that never reached Exchange.
/// </param>
public sealed record RightOutcome(string Right, string? Error, bool Unconfirmed = false)
{
    public bool Succeeded => Error is null;

    public static RightOutcome Ok(string right) => new(right, null);
    public static RightOutcome Failed(string right, string error) => new(right, error);

    /// <summary>aud-a70 (14). Sent, and its session failed before Exchange answered (<see cref="Unconfirmed"/>).</summary>
    public static RightOutcome Unanswered(string right, string error) => new(right, error, Unconfirmed: true);
}

/// <summary>
/// Composes the <see cref="PermissionResult"/> for a mailbox permission operation from the
/// per-right outcomes.
/// </summary>
/// <remarks>
/// Extracted from <see cref="MailboxPermissionService"/>, where this aggregation was written out
/// four times (EXO add/remove, on-prem add/remove) with only the wording differing. It is the
/// repo's Known Failure Class #2 - "loops over N items must aggregate per-item failures, never
/// report blanket success" - and it was the least testable code in the app: every copy sat inside
/// a closure that needed a live Exchange connection to reach.
///
/// This class is the seam. The service keeps the PowerShell calls and hands the results here; the
/// decision about what the operator is told becomes provable without a tenant.
///
/// The partial case is the one that matters. Granting FullAccess and then failing SendAs leaves
/// real access in place, so reporting a flat failure would send an operator to retry an operation
/// that half-applied - and the audit row would understate what was granted.
/// </remarks>
public static class MailboxPermissionOutcome
{
    /// <summary>
    /// Builds the result of a grant.
    /// </summary>
    /// <param name="targetMailbox">The mailbox acted on, named in every message.</param>
    /// <param name="user">The trustee.</param>
    /// <param name="outcomes">One entry per right attempted.</param>
    /// <param name="onPrem">Selects the on-premises wording and drops the OWA link.</param>
    public static PermissionResult ForGrant(
        string targetMailbox, string user, IReadOnlyList<RightOutcome> outcomes, bool onPrem = false)
    {
        var (granted, failed, unconfirmed) = Split(outcomes);
        var where = onPrem ? " (on-premises)" : "";

        if (unconfirmed.Count > 0)
            return NotConfirmed(targetMailbox, where, "granted", granted, failed, unconfirmed);

        if (failed.Count > 0 && granted.Count > 0)
        {
            return new PermissionResult
            {
                Success = false,
                Message = $"Partial: granted {string.Join(", ", granted)} on {targetMailbox}{where}. "
                          + $"Failed: {string.Join("; ", failed)}",
                // The applied rights, so the caller's audit row records what actually landed
                // rather than only that something went wrong.
                Detail = string.Join(", ", granted),
                AppliedRights = granted
            };
        }

        if (failed.Count > 0)
            return PermissionResult.Fail($"Failed on {targetMailbox}{where}: {string.Join("; ", failed)}");

        // No rights attempted at all is a caller bug, not a success. Reporting "granted  rights"
        // would tell an operator an empty operation worked.
        if (granted.Count == 0)
            return PermissionResult.Fail($"No permissions were specified for {targetMailbox}.");

        var rights = string.Join(" and ", granted);

        // As in ForRevoke: the two paths worded success differently before extraction and that is
        // preserved verbatim. The on-prem path also has no OWA link to offer.
        if (onPrem)
            return new PermissionResult { Success = true, Message = $"{user} has been granted {rights} on {targetMailbox} (on-premises).", AppliedRights = granted };

        return new PermissionResult
        {
            Success = true,
            Message = $"{user} has been granted {rights} rights to {targetMailbox}",
            Detail = "Users can access this mailbox in Outlook or at the following link:\n"
                     + $"https://outlook.office.com/mail/{targetMailbox}/",
            AppliedRights = granted
        };
    }

    /// <summary>
    /// Builds the result of a revoke. Same aggregation rules as <see cref="ForGrant"/>; only the
    /// wording differs, and there is no access link to offer.
    /// </summary>
    public static PermissionResult ForRevoke(
        string targetMailbox, string user, IReadOnlyList<RightOutcome> outcomes, bool onPrem = false)
    {
        var (removed, failed, unconfirmed) = Split(outcomes);
        var where = onPrem ? " (on-premises)" : "";

        if (unconfirmed.Count > 0)
            return NotConfirmed(targetMailbox, where, "removed", removed, failed, unconfirmed);

        if (failed.Count > 0 && removed.Count > 0)
        {
            return new PermissionResult
            {
                Success = false,
                Message = $"Partial: removed {string.Join(", ", removed)} on {targetMailbox}{where}. "
                          + $"Failed: {string.Join("; ", failed)}",
                Detail = string.Join(", ", removed),
                AppliedRights = removed
            };
        }

        if (failed.Count > 0)
            return PermissionResult.Fail($"Failed on {targetMailbox}{where}: {string.Join("; ", failed)}");

        if (removed.Count == 0)
            return PermissionResult.Fail($"No permissions were specified for {targetMailbox}.");

        // Wording preserved exactly as each path phrased it before extraction: the on-prem message
        // omits the word "rights" and ends with a period, the EXO one does neither. Pointless
        // divergence, but this refactor must be behavior-neutral - operators and the audit log
        // both read these strings, so normalizing them is a separate, visible change.
        var rights = string.Join(" and ", removed);
        return onPrem
            ? new PermissionResult { Success = true, Message = $"{rights} removed for {user} on {targetMailbox} (on-premises).", AppliedRights = removed }
            : new PermissionResult { Success = true, Message = $"{rights} rights removed for {user} on {targetMailbox}", AppliedRights = removed };
    }

    /// <summary>
    /// aud-a70 (14). A grant or revoke with a right whose outcome is unknown: not a success, and not a failure either - what
    /// took and what Exchange refused are said as such, and the rest is said not confirmed, with the session's own words and
    /// what to do. <see cref="PermissionResult.UnconfirmedRights"/> names those rights, so the record is filed Undetermined.
    /// </summary>
    private static PermissionResult NotConfirmed(string targetMailbox, string where, string verb, List<string> done,
        List<string> failed, List<RightOutcome> unconfirmed)
    {
        var said = new List<string>();
        if (done.Count > 0)
            said.Add($"{char.ToUpperInvariant(verb[0])}{verb[1..]} {string.Join(", ", done)} on {targetMailbox}{where}.");
        if (failed.Count > 0)
            said.Add($"Failed: {string.Join("; ", failed.Select(f => f.Trim().TrimEnd('.')))}.");
        var rights = unconfirmed.Select(o => o.Right).ToList();
        var causes = unconfirmed.Count == 1
            ? unconfirmed[0].Error!.Trim().TrimEnd('.')
            : string.Join("; ", unconfirmed.Select(o => $"{o.Right}: {o.Error!.Trim().TrimEnd('.')}"));
        said.Add($"Whether {string.Join(" and ", rights)} {(rights.Count == 1 ? "was" : "were")} {verb} on {targetMailbox}{where} could not be confirmed: {causes}. Check the permissions before trying again.");

        return new PermissionResult
        {
            Success = false,
            Message = string.Join(" ", said),
            Detail = done.Count > 0 ? string.Join(", ", done) : null,
            AppliedRights = done,
            UnconfirmedRights = rights,
        };
    }

    private static (List<string> Succeeded, List<string> Failed, List<RightOutcome> Unconfirmed) Split(IReadOnlyList<RightOutcome> outcomes)
    {
        var succeeded = new List<string>();
        var failed = new List<string>();
        var unconfirmed = new List<RightOutcome>();

        foreach (var o in outcomes)
        {
            if (o.Succeeded)
                succeeded.Add(o.Right);
            else if (o.Unconfirmed)
                unconfirmed.Add(o);
            else
                failed.Add($"{o.Right}: {o.Error}");
        }

        return (succeeded, failed, unconfirmed);
    }
}
