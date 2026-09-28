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
    public DateTime CreatedDateTime { get; set; }
    public DateTime? StartDateTime { get; set; }
    public DateTime? CompletedDateTime { get; set; }
    public int TotalCount { get; set; }
    public int SyncedCount { get; set; }
    public int FinalizedCount { get; set; }
    public int FailedCount { get; set; }
    public string? TargetEndpoint { get; set; }
    public bool AutoStart { get; set; }

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
    public DateTime? CompleteAfter { get; set; }

    /// <summary>
    /// The future time this batch is scheduled to complete, or null when it is not scheduled -
    /// including when CompleteAfter holds a past value, which means "complete now" and is not a
    /// schedule at all.
    /// </summary>
    public DateTime? ScheduledCompletionUtc =>
        CompleteAfter is { } when && when.ToUniversalTime() > DateTime.UtcNow
            ? when.ToUniversalTime()
            : null;

    public MigrationDirection Direction { get; set; }
    public List<MigrationUserInfo> Users { get; set; } = new();
}

public class MigrationUserInfo
{
    public required string EmailAddress { get; set; }
    public required string Status { get; set; }
    public string? ErrorSummary { get; set; }
    public DateTime? LastSyncDateTime { get; set; }
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
