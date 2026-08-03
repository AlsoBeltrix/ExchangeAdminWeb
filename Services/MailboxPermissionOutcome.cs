using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// One access right's fate in a multi-right permission operation.
/// </summary>
/// <param name="Right">"FullAccess" or "SendAs".</param>
/// <param name="Error">null when the right was applied; the failure text otherwise.</param>
public sealed record RightOutcome(string Right, string? Error)
{
    public bool Succeeded => Error is null;

    public static RightOutcome Ok(string right) => new(right, null);
    public static RightOutcome Failed(string right, string error) => new(right, error);
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
        var (granted, failed) = Split(outcomes);
        var where = onPrem ? " (on-premises)" : "";

        if (failed.Count > 0 && granted.Count > 0)
        {
            return new PermissionResult
            {
                Success = false,
                Message = $"Partial: granted {string.Join(", ", granted)} on {targetMailbox}{where}. "
                          + $"Failed: {string.Join("; ", failed)}",
                // The applied rights, so the caller's audit row records what actually landed
                // rather than only that something went wrong.
                Detail = string.Join(", ", granted)
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
            return new PermissionResult { Success = true, Message = $"{user} has been granted {rights} on {targetMailbox} (on-premises)." };

        return new PermissionResult
        {
            Success = true,
            Message = $"{user} has been granted {rights} rights to {targetMailbox}",
            Detail = "Users can access this mailbox in Outlook or at the following link:\n"
                     + $"https://outlook.office.com/mail/{targetMailbox}/"
        };
    }

    /// <summary>
    /// Builds the result of a revoke. Same aggregation rules as <see cref="ForGrant"/>; only the
    /// wording differs, and there is no access link to offer.
    /// </summary>
    public static PermissionResult ForRevoke(
        string targetMailbox, string user, IReadOnlyList<RightOutcome> outcomes, bool onPrem = false)
    {
        var (removed, failed) = Split(outcomes);
        var where = onPrem ? " (on-premises)" : "";

        if (failed.Count > 0 && removed.Count > 0)
        {
            return new PermissionResult
            {
                Success = false,
                Message = $"Partial: removed {string.Join(", ", removed)} on {targetMailbox}{where}. "
                          + $"Failed: {string.Join("; ", failed)}",
                Detail = string.Join(", ", removed)
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
            ? new PermissionResult { Success = true, Message = $"{rights} removed for {user} on {targetMailbox} (on-premises)." }
            : new PermissionResult { Success = true, Message = $"{rights} rights removed for {user} on {targetMailbox}" };
    }

    private static (List<string> Succeeded, List<string> Failed) Split(IReadOnlyList<RightOutcome> outcomes)
    {
        var succeeded = new List<string>();
        var failed = new List<string>();

        foreach (var o in outcomes)
        {
            if (o.Succeeded)
                succeeded.Add(o.Right);
            else
                failed.Add($"{o.Right}: {o.Error}");
        }

        return (succeeded, failed);
    }
}
