using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Which bulk action the Migration Status table is being asked to perform on a selection.
/// </summary>
public enum MigrationBatchAction
{
    /// <summary>Remove-MigrationBatch.</summary>
    Delete,

    /// <summary>Start-MigrationBatch on a stopped batch.</summary>
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
    // Remove-MigrationBatch is refused by Exchange on a batch that is still running, so the
    // terminal-ish states are the deletable ones. Mirrors the per-row Delete button's condition.
    private static readonly string[] DeletableStatuses =
        ["Completed", "Failed", "Stopped", "Corrupted"];

    // Start-MigrationBatch resumes a stopped batch. A batch that is already syncing has nothing to
    // resume, and a completed one cannot be restarted.
    private static readonly string[] ResumableStatuses = ["Stopped"];

    /// <summary>
    /// True when <paramref name="status"/> permits <paramref name="action"/>. The single definition;
    /// the page's per-row buttons call this rather than repeating the status list.
    /// </summary>
    public static bool Applies(MigrationBatchAction action, string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        var allowed = action switch
        {
            MigrationBatchAction.Delete => DeletableStatuses,
            MigrationBatchAction.Resume => ResumableStatuses,
            _ => Array.Empty<string>()
        };

        return allowed.Contains(status.Trim(), StringComparer.OrdinalIgnoreCase);
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
