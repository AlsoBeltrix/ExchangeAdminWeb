namespace ExchangeAdminWeb.Models;

public class DelegationReportResult
{
    public required string EmailAddress { get; set; }
    public List<DelegationEntry> FullAccess { get; set; } = new();
    public List<DelegationEntry> SendAs { get; set; } = new();
    public List<CalendarDelegationEntry> Calendar { get; set; } = new();

    /// <summary>
    /// Why the calendar section could not be read, when it could not be.
    /// </summary>
    /// <remarks>
    /// Audit finding A18. The calendar read has its own catch because a mailbox can legitimately
    /// have no calendar folder, and failing the whole report for that would be wrong. But
    /// swallowing it left <see cref="Calendar"/> empty and <see cref="Error"/> null, so a failed
    /// read rendered as "No calendar delegates" and the audit recorded a success.
    ///
    /// Separate from <see cref="Error"/> on purpose: the rest of the report is still valid and
    /// still worth showing. This says one section of it is unknown, not that the report failed.
    /// </remarks>
    public string? CalendarError { get; set; }

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
    public string FromIP { get; set; } = "";
    public string ToIP { get; set; } = "";
    public string MessageTraceId { get; set; } = "";
    public string Backend { get; set; } = "";
    public string EventId { get; set; } = "";
    public string Server { get; set; } = "";
}

/// <summary>
/// A single event (hop) in a message's delivery trail. On-prem: one
/// Get-MessageTrackingLog event row (RECEIVE, SUBMIT, DELIVER, FAIL, DEFER, ...).
/// Cloud: one Get-MessageTraceDetailV2 event. Field names are normalized across
/// both backends; a field that a backend does not supply is left empty.
/// </summary>
public class MessageTraceDetailEvent
{
    public DateTime Date { get; set; }
    public string Event { get; set; } = "";   // on-prem EventId / cloud Event
    public string Action { get; set; } = "";   // cloud Action; empty on-prem
    public string Detail { get; set; } = "";   // cloud Detail / on-prem SourceContext or RecipientStatus
    public string Source { get; set; } = "";   // on-prem Source; empty cloud
}

/// <summary>
/// The full per-hop delivery trail for one <see cref="MessageTraceResult"/>. The
/// summary list collapses a message to a single row; this preserves every event
/// so an operator can see why a message was deferred/failed/quarantined. Fetched
/// on demand (never for a whole result set) - see docs/MessageTraceDetail-Plan.md.
/// Fail-soft: on a fetch failure <see cref="Error"/> is set and
/// <see cref="Events"/> is empty; the caller is never blanked.
/// </summary>
public class MessageTraceDetail
{
    public MessageTraceResult Summary { get; set; } = default!;
    public List<MessageTraceDetailEvent> Events { get; set; } = new();
    public string? Error { get; set; }
}

public class MessageTraceResponse
{
    public List<MessageTraceResult> Results { get; set; } = new();
    public bool Truncated { get; set; }
    public int TotalAvailable { get; set; }
    public string? Error { get; set; }
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Backends that FAILED, as opposed to backends that returned nothing.
    /// </summary>
    /// <remarks>
    /// The distinction the merge could not express before. A trace queries Exchange Online and
    /// on-prem together; if one fails and the other succeeds, the result is a partial answer that
    /// looks exactly like a complete one, because the missing rows leave no trace in the table.
    /// The page must be able to say so plainly rather than relying on a warning banner the eye
    /// skips over. Empty means every backend answered.
    /// </remarks>
    public List<string> FailedBackends { get; set; } = new();

    /// <summary>True when at least one backend failed, so these results are incomplete.</summary>
    public bool IsPartial => FailedBackends.Count > 0;

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
    public double? DeletedItemSizeGB { get; set; }
    public double? ArchiveSizeGB { get; set; }
    public double? ArchiveDeletedItemSizeGB { get; set; }
    public long? ItemCount { get; set; }
    public long? DeletedItemCount { get; set; }
    public long? ArchiveItemCount { get; set; }
    public long? ArchiveDeletedItemCount { get; set; }
    public DateTime? WhenCreated { get; set; }
    public DateTime? LastLogonTime { get; set; }
    public List<string> EmailAddresses { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string? Error { get; set; }

    public double? TotalSizeGB => MailboxSizeGB.HasValue || DeletedItemSizeGB.HasValue
        ? (MailboxSizeGB ?? 0) + (DeletedItemSizeGB ?? 0) : null;
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

/// <summary>
/// One Out of Office change, captured from the form once, before the first await (aud-a7b).
/// </summary>
/// <remarks>
/// Immutable on purpose: the write, the audit row and the notifications all read this one value,
/// so an edit to the form while the operation is in flight cannot reach any of them.
/// <see cref="FromForm"/> applies the page's own rules exactly once - messages only when the state
/// is not Disabled, a schedule only when it is Scheduled, mailbox and ticket trimmed.
/// </remarks>
public sealed record OutOfOfficeRequest(
    string Mailbox,
    string State,
    string? InternalMessage,
    string? ExternalMessage,
    DateTime? StartTime,
    DateTime? EndTime,
    string Ticket)
{
    public static OutOfOfficeRequest FromForm(
        string? mailbox,
        string state,
        string? internalMessage,
        string? externalMessage,
        DateTime startTime,
        DateTime endTime,
        string? ticket) => new(
            Mailbox: (mailbox ?? "").Trim(),
            State: state,
            InternalMessage: state != "Disabled" ? internalMessage : null,
            ExternalMessage: state != "Disabled" ? externalMessage : null,
            StartTime: state == "Scheduled" ? startTime : null,
            EndTime: state == "Scheduled" ? endTime : null,
            Ticket: (ticket ?? "").Trim());
}
