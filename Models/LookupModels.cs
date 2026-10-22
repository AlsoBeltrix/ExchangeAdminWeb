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
/// <para>
/// aud-a7e. <see cref="StartTime"/> and <see cref="EndTime"/> are wall-clock times as the operator
/// typed them, and <see cref="ScheduleZone"/> is the zone they were typed in - the operator's
/// browser's. A wall-clock time alone names no moment: the write used to hand it to Exchange
/// unzoned, and the owner's notice printed it with no zone, so neither could say which 9 AM was
/// meant. The zone travels in the same captured request, so the write and the notice cannot be
/// handed a different one.
/// </para>
/// </remarks>
public sealed record OutOfOfficeRequest(
    string Mailbox,
    string State,
    string? InternalMessage,
    string? ExternalMessage,
    DateTime? StartTime,
    DateTime? EndTime,
    TimeZoneInfo? ScheduleZone,
    string Ticket)
{
    public static OutOfOfficeRequest FromForm(
        string? mailbox,
        string state,
        string? internalMessage,
        string? externalMessage,
        DateTime startTime,
        DateTime endTime,
        TimeZoneInfo? scheduleZone,
        string? ticket) => new(
            Mailbox: (mailbox ?? "").Trim(),
            State: state,
            InternalMessage: state != "Disabled" ? internalMessage : null,
            ExternalMessage: state != "Disabled" ? externalMessage : null,
            StartTime: state == "Scheduled" ? WallClock(startTime) : null,
            EndTime: state == "Scheduled" ? WallClock(endTime) : null,
            ScheduleZone: state == "Scheduled" ? scheduleZone : null,
            Ticket: (ticket ?? "").Trim());

    /// <summary>
    /// The instant a wall-clock time names in <paramref name="zone"/>, as UTC - or null for a time the
    /// zone skips when its clocks go forward, which names no instant at all. (An hour the clocks
    /// repeat is read as standard time, as <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> does.)
    /// </summary>
    public static DateTime? ToUtc(DateTime wallClock, TimeZoneInfo zone)
    {
        var local = WallClock(wallClock);
        return zone.IsInvalidTime(local) ? null : TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>
    /// The zone's name as the page's label and the owner's notice both print it - one definition, so
    /// the two cannot name the zone differently. For a browser's zone this is its IANA id.
    /// </summary>
    public static string ZoneName(TimeZoneInfo zone) => zone.Id;

    /// <summary>
    /// The zone's offset from UTC at that wall-clock time, as <c>UTC+05:30</c>. Per time, not per zone:
    /// a window that crosses a daylight-saving change has two.
    /// </summary>
    public static string OffsetAt(DateTime wallClock, TimeZoneInfo zone)
    {
        var offset = zone.GetUtcOffset(WallClock(wallClock));
        return "UTC" + (offset < TimeSpan.Zero ? "-" : "+")
            + offset.Duration().ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Unspecified, whatever the form handed over: the page's defaults start life as DateTime.Now, of
    // Kind Local - the SERVER's zone - and TimeZoneInfo would convert a Local value out of the server's
    // zone (or refuse it) instead of reading it as a time in ScheduleZone.
    private static DateTime WallClock(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
}
