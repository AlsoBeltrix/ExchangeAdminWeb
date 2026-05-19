namespace ExchangeAdminWeb.Models;

public class MigrationEligibilityResult
{
    public required string EmailAddress { get; set; }
    public MigrationStatus Status { get; set; }
    public List<string> IneligibilityReasons { get; set; } = new();
    public double MailboxSizeGB { get; set; }
    public double ArchiveSizeGB { get; set; }
    public double TotalSizeGB => MailboxSizeGB + ArchiveSizeGB;
    public long CloudQuotaGB { get; set; } = 100;
    public bool ExceedsQuota => TotalSizeGB > CloudQuotaGB;
    internal string? SamAccountName { get; set; }
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
    public bool AutoComplete { get; set; }
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
