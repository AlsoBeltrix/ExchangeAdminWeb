using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Which bulk action the Migration Status table is being asked to perform on a selection.
/// </summary>
public enum MigrationBatchAction
{
    /// <summary>Remove-MigrationBatch against any selected batch, whatever its status.</summary>
    Delete,

    /// <summary>Remove-MigrationBatch against selected batches whose status is exactly Completed.</summary>
    RemoveCompleted,

    /// <summary>Start-MigrationBatch against selected batches that are idle but restartable.</summary>
    Resume
}

/// <summary>A selected batch the action cannot apply to, with the status that disqualified it.</summary>
public sealed record MigrationBatchSkip(string BatchName, string Status);

/// <summary>
/// The partition of a selection into what will be acted on and what will not.
/// </summary>
/// <remarks>
/// Owner ruling D2(a), 2026-08-10: a bulk action acts on the eligible rows and names every skipped
/// row. A skip is NOT a failure - a selection with nothing eligible is "nothing to do", not an
/// error - so the two lists are separate values rather than one outcome with a failure count.
/// </remarks>
public sealed record MigrationBatchActionPlan(
    IReadOnlyList<string> Eligible,
    IReadOnlyList<MigrationBatchSkip> Skipped);

/// <summary>
/// Decides which selected migration batches a bulk action applies to, and owns the single
/// definition of which batch statuses each action is valid for.
/// </summary>
/// <remarks>
/// This is pure logic deliberately kept OUT of Migration.razor. There is no bUnit harness in this
/// repo, so no test can render a page or exercise a Razor handler; anything left in the page is
/// coverable only by a source-level tripwire. Same reasoning as MessageTraceDetailReport and
/// ProtectedPrincipalEntryValidator.
///
/// The status predicates here are also what the PER-ROW buttons read. Two copies of "which statuses
/// may be deleted" is how a bulk action and a row button come to disagree about the same batch.
/// </remarks>
public static class MigrationBatchActionPlanner
{
    /// <summary>The one status Remove Completed will act on (D3).</summary>
    private const string CompletedStatus = "Completed";

    // Statuses Resume/Retry is NOT offered on (D4). Defined by exclusion, deliberately.
    //
    // An allowlist of resumable statuses is what produced this rule: CompletedWithErrors is a real
    // Exchange status that appeared NOWHERE in this codebase, so a batch in that state could not be
    // resumed, could not be deleted, was not swept, and rendered with the unknown-status badge. No
    // test could have caught it - every test used the same status list the code did.
    //
    // Exclusion inverts the failure direction. An unanticipated status gets a button and Exchange
    // refuses it if invalid, which surfaces as that row's own named, aggregated failure. An
    // allowlist makes the same row silently vanish from the UI.
    //
    // Two groups, for two different reasons:
    //   - actively working: there is nothing to resume, the batch is already moving;
    //   - Completed: idle, but DONE. Idle-and-restartable is not the same as idle - the distinction
    //     this rule missed on its first draft.
    private static readonly string[] NonResumableStatuses =
        ["Syncing", "Starting", "Stopping", "Completing", "Removing", CompletedStatus];

    /// <summary>
    /// True when <paramref name="status"/> permits <paramref name="action"/>. The single definition;
    /// the page's per-row buttons call this rather than repeating the status rules.
    /// </summary>
    public static bool Applies(MigrationBatchAction action, string? status)
    {
        var trimmed = status?.Trim();

        return action switch
        {
            // Delete accepts anything (D3). Exchange is the authority on what it will remove, and a
            // refusal returns as a per-row failure the executor names. A client-side allowlist here
            // can only be wrong in the direction that hides a row the operator explicitly ticked.
            MigrationBatchAction.Delete => true,

            // Exactly Completed - never CompletedWithErrors, which is not a batch that finished.
            MigrationBatchAction.RemoveCompleted =>
                string.Equals(trimmed, CompletedStatus, StringComparison.OrdinalIgnoreCase),

            // Everything except the working statuses and Completed. A missing status is refused:
            // we cannot tell whether the batch is mid-flight, and restarting one that is would be
            // the harmful direction.
            MigrationBatchAction.Resume =>
                !string.IsNullOrWhiteSpace(trimmed)
                && !NonResumableStatuses.Contains(trimmed, StringComparer.OrdinalIgnoreCase),

            _ => false
        };
    }

    /// <summary>
    /// Counts of each status among the selected batches, in table order, for the Delete
    /// confirmation (D5). Delete's whole point is that it accepts in-progress batches, so the
    /// operator must see how many of those they are about to destroy before typing a ticket.
    /// </summary>
    public static string DescribeSelectionByStatus(
        IEnumerable<MigrationBatchInfo>? loaded,
        IEnumerable<string>? selectedNames)
    {
        var selected = ToNameSet(selectedNames);
        if (loaded == null || selected.Count == 0)
            return "";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counts = new List<KeyValuePair<string, int>>();

        foreach (var batch in loaded)
        {
            if (batch == null || string.IsNullOrWhiteSpace(batch.BatchName))
                continue;
            if (!selected.Contains(batch.BatchName) || !seen.Add(batch.BatchName))
                continue;

            var status = string.IsNullOrWhiteSpace(batch.Status) ? "Unknown" : batch.Status.Trim();
            var index = counts.FindIndex(c => string.Equals(c.Key, status, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                counts[index] = new KeyValuePair<string, int>(counts[index].Key, counts[index].Value + 1);
            else
                counts.Add(new KeyValuePair<string, int>(status, 1));
        }

        return string.Join(", ", counts.Select(c => $"{c.Value} {c.Key}"));
    }

    /// <summary>
    /// Partitions <paramref name="selectedNames"/> into the batches <paramref name="action"/> will
    /// be run against and the ones it will skip.
    /// </summary>
    /// <remarks>
    /// A selected name that is no longer in <paramref name="loaded"/> is DROPPED - neither acted on
    /// nor reported as a skip. The batch is gone (removed by this operator, another operator, or
    /// Exchange itself); acting on it would produce a failure that reads as an app bug, and
    /// reporting it as skipped would name a row the operator can no longer see. Selection pruning
    /// happens at load time too, but an arbitrary pause at the ticket field sits between the two.
    ///
    /// Order follows <paramref name="loaded"/>, not the selection, so the report reads in the same
    /// order as the table.
    /// </remarks>
    public static MigrationBatchActionPlan Plan(
        IEnumerable<MigrationBatchInfo>? loaded,
        IEnumerable<string>? selectedNames,
        MigrationBatchAction action)
    {
        var selected = ToNameSet(selectedNames);
        var eligible = new List<string>();
        var skipped = new List<MigrationBatchSkip>();

        if (loaded == null || selected.Count == 0)
            return new MigrationBatchActionPlan(eligible, skipped);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in loaded)
        {
            if (batch == null || string.IsNullOrWhiteSpace(batch.BatchName))
                continue;
            if (!selected.Contains(batch.BatchName))
                continue;
            if (!seen.Add(batch.BatchName))
                continue;

            if (Applies(action, batch.Status))
                eligible.Add(batch.BatchName);
            else
                skipped.Add(new MigrationBatchSkip(batch.BatchName, batch.Status ?? "Unknown"));
        }

        return new MigrationBatchActionPlan(eligible, skipped);
    }

    /// <summary>
    /// The subset of <paramref name="selectedNames"/> still present in <paramref name="loaded"/>.
    /// Called after every reload: a batch that has disappeared must not stay ticked.
    /// </summary>
    public static IReadOnlyList<string> PruneSelection(
        IEnumerable<MigrationBatchInfo>? loaded,
        IEnumerable<string>? selectedNames)
    {
        var selected = ToNameSet(selectedNames);
        var kept = new List<string>();

        if (loaded == null || selected.Count == 0)
            return kept;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in loaded)
        {
            if (batch == null || string.IsNullOrWhiteSpace(batch.BatchName))
                continue;
            if (selected.Contains(batch.BatchName) && seen.Add(batch.BatchName))
                kept.Add(batch.BatchName);
        }

        return kept;
    }

    /// <summary>
    /// The operator-facing tail naming every skipped batch and why, or an empty string when nothing
    /// was skipped. Each skip is named individually: a bare count cannot be acted on.
    /// </summary>
    public static string DescribeSkipped(IReadOnlyList<MigrationBatchSkip>? skipped)
    {
        if (skipped == null || skipped.Count == 0)
            return "";

        var detail = string.Join(", ", skipped.Select(s => $"{s.BatchName} ({s.Status})"));
        return $" Skipped {skipped.Count}: {detail}.";
    }

    private static HashSet<string> ToNameSet(IEnumerable<string>? names)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (names == null)
            return set;

        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name))
                set.Add(name.Trim());
        }

        return set;
    }
}
