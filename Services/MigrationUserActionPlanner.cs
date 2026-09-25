using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Which bulk action the mailbox table is being asked to perform on a selection.
/// </summary>
public enum MigrationUserAction
{
    /// <summary>Complete-MigrationUser against selected mailboxes that have finished syncing.</summary>
    Complete,

    /// <summary>Set-MigrationUser approval against selected mailboxes awaiting it.</summary>
    Approve,

    /// <summary>Stop-MigrationUser against selected mailboxes that are still moving.</summary>
    Pause,

    /// <summary>Start-MigrationUser against selected mailboxes that are stopped or failed.</summary>
    Resume,

    /// <summary>Remove-MigrationUser against selected mailboxes that have completed.</summary>
    Clear
}

/// <summary>A selected mailbox the action cannot apply to, with the status that disqualified it.</summary>
public sealed record MigrationUserSkip(string EmailAddress, string Status);

/// <summary>The partition of a mailbox selection into what will be acted on and what will not.</summary>
public sealed record MigrationUserActionPlan(
    IReadOnlyList<string> Eligible,
    IReadOnlyList<MigrationUserSkip> Skipped);

/// <summary>
/// Decides which selected mailboxes a bulk action applies to, and owns the single definition of
/// which mailbox statuses each action is valid for.
/// </summary>
/// <remarks>
/// <para>
/// The mailbox counterpart of <see cref="MigrationBatchActionPlanner"/>, added in S5 step 2 of
/// docs/MigrationInterfaceRedesign-Plan.md. R12 says both action bars are identical in look AND in
/// behaviour, and "identical" starts here: a bar that looped over the selection itself would have
/// no eligible/skipped split, no per-row outcome preview and no skipped list, and would only
/// resemble the batch bar from a distance.
/// </para>
/// <para>
/// Pure logic deliberately kept OUT of Migration.razor. There is no bUnit harness in this repo, so
/// anything left in the page is coverable only by a source-level tripwire; the same reasoning that
/// put the batch rules here.
/// </para>
/// <para>
/// <b>Every predicate below is an allowlist, ported character for character from the per-row
/// buttons that owned these rules before S5.</b> D4 on the batch planner warns that allowlists are
/// how CompletedWithErrors became invisible, and the distinction is the same one the owner settled
/// on 2026-09-25: exclusion is right where hiding a ticked row is worse than Exchange refusing it,
/// and an allowlist is right where accepting an unanticipated status is the harmful direction.
/// Every action here mutates ONE mailbox mid-migration - completing it cuts a person over, pausing
/// it halts their move, removing it drops them from the batch - so these stay narrow.
/// </para>
/// </remarks>
public static class MigrationUserActionPlanner
{
    private const string SyncedStatus = "Synced";
    private const string NeedsApprovalStatus = "NeedsApproval";
    private const string SyncingStatus = "Syncing";
    private const string CompletedStatus = "Completed";

    // Resume is offered on a mailbox that has stopped moving, whether it was stopped deliberately
    // or by failure. Two statuses, one meaning: idle and restartable.
    private static readonly string[] ResumableStatuses = ["Stopped", "Failed"];

    /// <summary>
    /// True when <paramref name="status"/> permits <paramref name="action"/>. The single
    /// definition; the page's controls call this rather than repeating the status rules.
    /// </summary>
    public static bool Applies(MigrationUserAction action, string? status)
    {
        var trimmed = status?.Trim();

        // A missing status is refused by every arm below WITHOUT a guard here, because each arm
        // asks whether the status equals a named value and null equals none of them. An explicit
        // early return was written first and a mutation probe proved it dead - deleting it changed
        // no test - so it is gone rather than left as defence that defends nothing.
        //
        // That the refusal is structural rather than guarded is the load-bearing fact: it holds
        // only while every arm stays an allowlist. An arm written by exclusion would start
        // ACCEPTING a missing status, and that is the harmful direction here - every action in
        // this planner changes one person's mailbox mid-migration.
        return action switch
        {
            MigrationUserAction.Complete =>
                string.Equals(trimmed, SyncedStatus, StringComparison.OrdinalIgnoreCase),

            MigrationUserAction.Approve =>
                string.Equals(trimmed, NeedsApprovalStatus, StringComparison.OrdinalIgnoreCase),

            MigrationUserAction.Pause =>
                string.Equals(trimmed, SyncingStatus, StringComparison.OrdinalIgnoreCase),

            MigrationUserAction.Resume =>
                ResumableStatuses.Contains(trimmed, StringComparer.OrdinalIgnoreCase),

            MigrationUserAction.Clear =>
                string.Equals(trimmed, CompletedStatus, StringComparison.OrdinalIgnoreCase),

            _ => false
        };
    }

    /// <summary>
    /// Partitions <paramref name="selectedEmails"/> into the mailboxes <paramref name="action"/>
    /// will run against and the ones it will skip.
    /// </summary>
    /// <remarks>
    /// A selected address no longer in <paramref name="loaded"/> is DROPPED - neither acted on nor
    /// reported as a skip. The mailbox has left the batch; acting on it would produce a failure
    /// that reads as an app bug, and reporting it as skipped would name a row the operator can no
    /// longer see. Order follows <paramref name="loaded"/> so the report reads in table order.
    /// </remarks>
    public static MigrationUserActionPlan Plan(
        IEnumerable<MigrationUserInfo>? loaded,
        IEnumerable<string>? selectedEmails,
        MigrationUserAction action)
    {
        var selected = ToAddressSet(selectedEmails);
        var eligible = new List<string>();
        var skipped = new List<MigrationUserSkip>();

        if (loaded == null || selected.Count == 0)
            return new MigrationUserActionPlan(eligible, skipped);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in loaded)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.EmailAddress))
                continue;
            if (!selected.Contains(user.EmailAddress))
                continue;
            if (!seen.Add(user.EmailAddress))
                continue;

            if (Applies(action, user.Status))
                eligible.Add(user.EmailAddress);
            else
                skipped.Add(new MigrationUserSkip(user.EmailAddress, user.Status ?? "Unknown"));
        }

        return new MigrationUserActionPlan(eligible, skipped);
    }

    /// <summary>
    /// The subset of <paramref name="selectedEmails"/> still present in <paramref name="loaded"/>.
    /// Called after every reload: a mailbox that has left the batch must not stay ticked.
    /// </summary>
    public static IReadOnlyList<string> PruneSelection(
        IEnumerable<MigrationUserInfo>? loaded,
        IEnumerable<string>? selectedEmails)
    {
        var selected = ToAddressSet(selectedEmails);
        var kept = new List<string>();

        if (loaded == null || selected.Count == 0)
            return kept;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in loaded)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.EmailAddress))
                continue;
            if (selected.Contains(user.EmailAddress) && seen.Add(user.EmailAddress))
                kept.Add(user.EmailAddress);
        }

        return kept;
    }

    /// <summary>
    /// The operator-facing tail naming every skipped mailbox and why, or an empty string when
    /// nothing was skipped. Each skip is named individually: a bare count cannot be acted on.
    /// </summary>
    public static string DescribeSkipped(IReadOnlyList<MigrationUserSkip>? skipped)
    {
        if (skipped == null || skipped.Count == 0)
            return "";

        var detail = string.Join(", ", skipped.Select(s => $"{s.EmailAddress} ({s.Status})"));
        return $" Skipped {skipped.Count}: {detail}.";
    }

    private static HashSet<string> ToAddressSet(IEnumerable<string>? addresses)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (addresses == null)
            return set;

        foreach (var address in addresses)
        {
            if (!string.IsNullOrWhiteSpace(address))
                set.Add(address.Trim());
        }

        return set;
    }
}
