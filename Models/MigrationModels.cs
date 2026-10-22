namespace ExchangeAdminWeb.Models;

public class MigrationEligibilityResult
{
    public required string EmailAddress { get; set; }
    public MigrationStatus Status { get; set; }
    public List<string> IneligibilityReasons { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public double MailboxSizeGB { get; set; }
    public double ArchiveSizeGB { get; set; }
    public double TotalSizeGB => MailboxSizeGB + ArchiveSizeGB;

    /// <summary>
    /// Per-mailbox size limit. The primary mailbox and the archive are each checked
    /// against this on their own; their combined size is not a migration criterion.
    /// </summary>
    public long CloudQuotaGB { get; set; } = 99;

    public bool MailboxExceedsQuota => MailboxSizeGB > CloudQuotaGB;
    public bool ArchiveExceedsQuota => ArchiveSizeGB > CloudQuotaGB;
    public bool ExceedsQuota => MailboxExceedsQuota || ArchiveExceedsQuota;
    internal bool NeedsAdGroupCheck { get; set; }

    /// <summary>
    /// True when the target is a protected principal OR protection could not be
    /// verified (fail-closed). Orthogonal to <see cref="Status"/>: a protected
    /// principal keeps its real Ex/AD eligibility verdict but must be escalated
    /// outside this tool and cannot be migrated here.
    /// </summary>
    public bool IsProtected { get; set; }

    /// <summary>Operator-facing reason for the protection flag (null when not flagged).</summary>
    public string? ProtectionNote { get; set; }
}

public class MigrationBatchResult
{
    public required string BatchName { get; set; }
    public bool Success { get; set; }
    public string? Message { get; set; }
    public MigrationDirection Direction { get; set; }
    public List<MigrationEligibilityResult> EligibilityResults { get; set; } = new();
    public int TotalUsers { get; set; }
    public int EligibleUsers { get; set; }
    public int IneligibleUsers { get; set; }
    public bool AutoStart { get; set; }
    public bool AutoComplete { get; set; }
}

public class MigrationCsvRow
{
    public required string EmailAddress { get; set; }
}

public class MigrationBatchInfo
{
    public required string BatchName { get; set; }
    public required string Status { get; set; }

    /// <summary>
    /// aud-a23d. When Exchange says the batch was created, as reported - one it cannot read is kept as
    /// such and shown as the marker, and sorts after every readable time. aud-a23e: null when none was
    /// reported, shown as "(not reported)", an empty CSV cell, and sorted as none.
    /// </summary>
    /// <remarks>
    /// aud-a23e. It defaulted to <see cref="DateTime.MinValue"/>, which the page showed as a creation in the
    /// year 0001 and an ascending sort put first: a date nobody reported, shown as a fact.
    /// </remarks>
    public ReportedTime? CreatedDateTime { get; set; }

    /// <summary>
    /// aud-a23d. When the batch started, as reported; null only when nothing was reported. A start this
    /// app cannot read is not null, so the page does not call such a batch "not started" - it shows the
    /// marker where the start goes.
    /// </summary>
    public ReportedTime? StartDateTime { get; set; }

    /// <summary>aud-a23d. When the batch completed, as reported; null only when nothing was reported.</summary>
    public ReportedTime? CompletedDateTime { get; set; }

    // aud-a23f. The counts as reported, each read through ReportedCount.Read: Convert.ToInt32 threw on a
    // value it could not convert and the batch vanished from the list. One this app cannot read is its
    // marker where the count goes, and the batch stays. Absent is 0, as it always read here. Being neither
    // 0 nor more, an unreadable count tells the page neither "N pending. Batch not started." nor "contains
    // no mailboxes", and its sorts put it after every count.
    public ReportedCount TotalCount { get; set; } = ReportedCount.Of(0);
    public ReportedCount PendingCount { get; set; } = ReportedCount.Of(0);
    public string? WorkflowStage { get; set; }
    public ReportedCount SyncedCount { get; set; } = ReportedCount.Of(0);
    public ReportedCount FinalizedCount { get; set; } = ReportedCount.Of(0);
    public ReportedCount FailedCount { get; set; } = ReportedCount.Of(0);
    public string? TargetEndpoint { get; set; }

    /// <summary>
    /// aud-a23f. Whether the batch starts on its own, as reported: false when nothing was reported, null when
    /// what was cannot be read as a flag - <c>Convert.ToBoolean</c> threw on it and lost the batch. Nothing
    /// reads it today; false for an unreadable value would be a guess.
    /// </summary>
    public bool? AutoStart { get; set; }

    /// <summary>
    /// True when Exchange holds ANY CompleteAfter value for this batch - which means the batch is
    /// set to finalise without a further instruction, whether that was requested at creation or
    /// scheduled for a time.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="CompleteAfter"/> rather than read separately, so the flag and the
    /// time cannot disagree. Before S8 this was the ONLY thing read from CompleteAfter, which is
    /// what made scheduling impossible to represent: a batch told to complete now and a batch
    /// scheduled for 22:00 were both just "true".
    /// <para>
    /// aud-a23d: a CompleteAfter this app cannot read still counts. Exchange did report one, so the
    /// batch is set to finalise; the old read turned such a value into null and this into false.
    /// </para>
    /// </remarks>
    public bool AutoComplete => CompleteAfter != null;

    /// <summary>
    /// The CompleteAfter timestamp Exchange holds, or null when there is none.
    /// </summary>
    /// <remarks>
    /// <b>A PAST value means "complete now".</b> That is the idiom this codebase and Exchange both
    /// use: Set-MigrationBatch -CompleteAfter with a time already gone tells Exchange to finalise
    /// at the first opportunity. A FUTURE value is a real schedule.
    /// <para>
    /// Reading the two apart is the whole point of S8 and the reason queue item 12 could not be
    /// built before it. <see cref="ScheduledCompletionUtc"/> is the safe way to ask.
    /// </para>
    /// </remarks>
    public ReportedTime? CompleteAfter { get; set; }

    /// <summary>
    /// The future time this batch is scheduled to complete, or null when it is not scheduled -
    /// including when CompleteAfter holds a past value, which means "complete now" and is not a
    /// schedule at all. Also null when the CompleteAfter cannot be read: see
    /// <see cref="CompleteAfterUnreadable"/>, which then says this null is not an answer.
    /// </summary>
    public DateTime? ScheduledCompletionUtc =>
        CompleteAfter?.Time is { } when && when.ToUniversalTime() > DateTime.UtcNow
            ? when.ToUniversalTime()
            : null;

    /// <summary>
    /// aud-a23d. True when Exchange reported a CompleteAfter this app cannot read: the batch is set to
    /// finalise (<see cref="AutoComplete"/>), but whether now or at a time is unknown.
    /// </summary>
    /// <remarks>
    /// A schedule cannot be said either way without reading the time, and guessing "not scheduled"
    /// is the silent-absence this finding removed. No page shows a batch's schedule today, so this is
    /// the result's own statement of it, for whatever asks next.
    /// </remarks>
    public bool CompleteAfterUnreadable => CompleteAfter is { Time: null };

    public MigrationDirection Direction { get; set; }
    public List<MigrationUserInfo> Users { get; set; } = new();
}

/// <summary>
/// aud-a23f. A row <c>Get-MigrationBatch</c> returned that could not be read as a batch: its identity as far
/// as it was read - null when it had none, or when the identity is what could not be read - and why.
/// </summary>
/// <remarks>
/// Such a row used to be logged and dropped, so the page listed the other batches as if they were all there
/// were, and an operator looking for this one concluded it did not exist (Known Failure Class 2). It is
/// carried beside the batches instead, named in a warning above the list and listed in the CSV.
/// </remarks>
public sealed record UnreadableMigrationBatch(string? Identity, string Reason)
{
    /// <summary>How the warning and the CSV name it: the identity when it has one, then the reason.</summary>
    public string Shown => Identity is null ? Reason : $"{Identity}: {Reason}";

    /// <summary>The page's warning, spelled once: "N batch(es) could not be read: name: reason; ...".</summary>
    public static string Warning(IReadOnlyCollection<UnreadableMigrationBatch> rows) =>
        $"{rows.Count} {(rows.Count == 1 ? "batch" : "batches")} could not be read: {string.Join("; ", rows.Select(r => r.Shown))}";
}

/// <summary>
/// aud-a23f. What <c>Get-MigrationBatch</c> returned: the batches, and beside them every row that could not be
/// read as one - one result, so no caller is handed the first without the second.
/// </summary>
/// <remarks>
/// Positional, so the Migration page takes both where it took the list, on that one line: its line-keyed
/// ClickGate entries keep every edit to it in place.
/// </remarks>
public sealed record MigrationBatchList(List<MigrationBatchInfo> Batches, List<UnreadableMigrationBatch> Unreadable);

public class MigrationUserInfo
{
    public required string EmailAddress { get; set; }
    public required string Status { get; set; }
    public string? ErrorSummary { get; set; }

    /// <summary>
    /// aud-a23d. When the mailbox last synced, as reported; null only when nothing was reported. One this
    /// app cannot read is shown as the marker, not as "-", and sorts after every time and before none.
    /// </summary>
    public ReportedTime? LastSyncDateTime { get; set; }
    public long ItemsSynced { get; set; }
    public long ItemsSkipped { get; set; }
}

public class MigrationUserSearchResult
{
    public string? BatchId { get; init; }
    public string? Email { get; init; }
    public string? Error { get; init; }
    public int MatchCount { get; init; }

    public static MigrationUserSearchResult Found(string batchId, string email) =>
        new() { BatchId = batchId, Email = email, MatchCount = 1 };

    public static MigrationUserSearchResult Ambiguous(int count) =>
        new() { MatchCount = count };

    public static MigrationUserSearchResult NotFound() =>
        new() { MatchCount = 0 };

    public static MigrationUserSearchResult Failed(string error) =>
        new() { Error = error };
}
