namespace ExchangeAdminWeb.Models;

public class DelegationReportResult
{
    public required string EmailAddress { get; set; }
    public List<DelegationEntry> FullAccess { get; set; } = new();
    public List<DelegationEntry> SendAs { get; set; } = new();
    public List<CalendarDelegationEntry> Calendar { get; set; } = new();
    public string? Error { get; set; }
}

public class DelegationEntry
{
    public required string User { get; set; }
}

public class CalendarDelegationEntry
{
    public required string User { get; set; }
    public required string AccessRights { get; set; }
}

public class MessageTraceResult
{
    public DateTime Received { get; set; }
    public required string SenderAddress { get; set; }
    public required string RecipientAddress { get; set; }
    public required string Subject { get; set; }
    public required string Status { get; set; }
    public required string MessageId { get; set; }
    public long Size { get; set; }
}

public class MessageTraceResponse
{
    public List<MessageTraceResult> Results { get; set; } = new();
    public bool Truncated { get; set; }
    public int TotalAvailable { get; set; }
    public string? Error { get; set; }

    public static readonly int MaxResults = 1000;
}

public class RecipientInfoResult
{
    public required string EmailAddress { get; set; }
    public string? DisplayName { get; set; }
    public string? RecipientType { get; set; }
    public string? MailboxLocation { get; set; }
    public string? ForwardingAddress { get; set; }
    public bool ArchiveEnabled { get; set; }
    public double? MailboxSizeGB { get; set; }
    public double? ArchiveSizeGB { get; set; }
    public long? ItemCount { get; set; }
    public DateTime? WhenCreated { get; set; }
    public DateTime? LastLogonTime { get; set; }
    public List<string> EmailAddresses { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string? Error { get; set; }
}

public class OutOfOfficeResult
{
    public required string EmailAddress { get; set; }
    public required string State { get; set; }
    public string? InternalMessage { get; set; }
    public string? ExternalMessage { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? Error { get; set; }
}
