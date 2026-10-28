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

    /// <summary>
    /// kd-59. True when the read was stopped by a cancellation of the token its caller passed - the caller's own cancel,
    /// which the page files Cancelled (kd-57). <see cref="Error"/> is set as for any read that failed, so a caller that reads
    /// only the error - the detail job - reads what it read before; the read stays fail-soft.
    /// </summary>
    public bool Cancelled { get; set; }
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

/// <summary>
/// RecipientLookupAccuracy S1. Where a looked-up recipient's mailbox is, as far as Exchange's own answers settle it
/// (<c>Services/RecipientClassification.cs</c>). Recipient Lookup's own, as <see cref="RecipientInfoResult"/> is.
/// </summary>
public enum RecipientLocationClass
{
    /// <summary>Exchange Online holds its mailbox, and this module reads it.</summary>
    Cloud,

    /// <summary>On-premises Exchange holds its mailbox.</summary>
    OnPremises,

    /// <summary>It has no mailbox: a list, group, contact, guest, mail user or mail-enabled public folder.</summary>
    NotApplicable,

    /// <summary>Exchange Online holds a mailbox of a kind this module does not read: a Microsoft 365 group's, a system mailbox.</summary>
    ExchangeHeldNotRead,

    /// <summary>The two sides disagree, or a side that had to be asked could not be.</summary>
    Undetermined,
}

public class RecipientInfoResult
{
    /// <summary>What the operator typed, trimmed. Every read asked about it before RecipientLookupAccuracy S3.</summary>
    public required string EmailAddress { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>
    /// Exchange Online's <c>RecipientTypeDetails</c>, as reported: what the type row showed before RecipientLookupAccuracy
    /// S1, and now the first part of its small print. The row shows <see cref="RecipientTypeLabel"/>.
    /// </summary>
    public string? RecipientType { get; set; }

    /// <summary>S1. Exchange Online's coarser <c>RecipientType</c>, as reported.</summary>
    public string? RecipientTypeCategory { get; set; }

    /// <summary>S1. Exchange Online's <c>PrimarySmtpAddress</c>; null when it reported none.</summary>
    public string? PrimarySmtpAddress { get; set; }

    /// <summary>S1. Exchange Online's <c>IsDirSynced</c>; null when it was not reported.</summary>
    public bool? IsDirSynced { get; set; }

    // RecipientLookupAccuracy E2. Directory facts from Exchange Online's Get-Recipient row; null when not reported.
    public string? EntraObjectId { get; set; }
    public string? Alias { get; set; }
    public bool? HiddenFromAddressLists { get; set; }
    public string? ExternalEmailAddress { get; set; }
    public ReportedTime? WhenChanged { get; set; }

    /// <summary>S1. On-premises Exchange's <c>RecipientTypeDetails</c>, when it was asked and found one.</summary>
    public string? OnPremRecipientType { get; set; }

    /// <summary>S1. What the recipient is, in plain words, or "Could not be determined".</summary>
    public string? RecipientTypeLabel { get; set; }

    /// <summary>S1. Exchange's own type words from each side that answered, shown under the label.</summary>
    public string? RecipientTypeSmallPrint { get; set; }

    /// <summary>S1. Where its mailbox is; <see cref="MailboxLocation"/> is the word the page shows for it.</summary>
    public RecipientLocationClass LocationClass { get; set; } = RecipientLocationClass.Undetermined;

    /// <summary>S2. Why there is no mailbox, shown under "Not applicable"; null for every other location.</summary>
    public string? LocationNote { get; set; }

    public string? MailboxLocation { get; set; }
    public string? ForwardingAddress { get; set; }
    public bool ArchiveEnabled { get; set; }
    // aud-a54. Why the Exchange Online mailbox, and its archive, could not be read; null when read, or when Exchange said
    // there is none. The page shows "Could not be read" for either, where it showed the absence ("None", "Disabled").
    // aud-a68: for an on-premises mailbox the archive's is the on-premises read's, as ArchiveEnabled is.
    public string? MailboxUnreadable { get; set; }
    public string? ArchiveUnreadable { get; set; }

    /// <summary>
    /// RecipientLookupAccuracy S2. Why the mailbox statistics could not be read - the read failed, or answered no row - or, for
    /// an on-premises mailbox, why its size could not be; null when read. Each figure it gives then reads "Could not be
    /// read", where it read "N/A", which says Exchange reported nothing (the plan's AC5).
    /// </summary>
    public string? StatisticsUnreadable { get; set; }
    public double? MailboxSizeGB { get; set; }
    public double? DeletedItemSizeGB { get; set; }
    public double? ArchiveSizeGB { get; set; }
    public double? ArchiveDeletedItemSizeGB { get; set; }
    public long? ItemCount { get; set; }
    public long? DeletedItemCount { get; set; }
    public long? ArchiveItemCount { get; set; }
    public long? ArchiveDeletedItemCount { get; set; }
    public ReportedTime? WhenCreated { get; set; }
    public ReportedTime? LastLogonTime { get; set; }
    public List<string> EmailAddresses { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string? Error { get; set; }

    // RecipientLookupAccuracy S2. The sum only when both sizes were read: a missing part counted as 0 made an on-premises
    // mailbox's size alone read as its total.
    public double? TotalSizeGB => MailboxSizeGB.HasValue && DeletedItemSizeGB.HasValue
        ? MailboxSizeGB.Value + DeletedItemSizeGB.Value : null;
}

public class OutOfOfficeResult
{
    public required string EmailAddress { get; set; }
    public required string State { get; set; }
    public string? InternalMessage { get; set; }
    public string? ExternalMessage { get; set; }
    public ReportedTime? StartTime { get; set; }
    public ReportedTime? EndTime { get; set; }
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
    /// The instant a wall-clock time names in <paramref name="zone"/>, as UTC - or null when it names
    /// no single instant, for the reason <see cref="NoInstantReason"/> gives.
    /// </summary>
    public static DateTime? ToUtc(DateTime wallClock, TimeZoneInfo zone) =>
        NoInstantReason(wallClock, zone) is null ? TimeZoneInfo.ConvertTimeToUtc(WallClock(wallClock), zone) : null;

    /// <summary>
    /// Why a wall-clock time names no single instant in <paramref name="zone"/>, worded to follow the
    /// time in a sentence - or null when it names exactly one.
    /// </summary>
    /// <remarks>
    /// A time the clocks skip when they go forward names no instant. aud-a7g: one they repeat when they
    /// go back names two, and <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> picks
    /// one of them without asking (standard time), so a window entered in that hour could be written an
    /// hour off. Neither is guessed: both are refused with their own reason, here in one place for the
    /// page and the write boundary alike, and choosing a time outside the hour is the operator's.
    /// aud-a7h: worded without "hour". The check is <see cref="TimeZoneInfo.IsAmbiguousTime(DateTime)"/>,
    /// which holds for whatever the clocks go back by - thirty minutes, in some zones.
    /// </remarks>
    public static string? NoInstantReason(DateTime wallClock, TimeZoneInfo zone)
    {
        var local = WallClock(wallClock);
        if (zone.IsInvalidTime(local))
            return $"does not exist in {ZoneName(zone)}: the clocks skip it";
        if (zone.IsAmbiguousTime(local))
            return $"occurs twice in {ZoneName(zone)}: the clocks go back and repeat that time, so it could mean either of two moments; choose a time the clocks do not repeat";
        return null;
    }

    /// <summary>A wall-clock time as the page and the refusals print it: <c>2026-11-01 01:30</c>.</summary>
    public static string ClockText(DateTime wallClock) =>
        wallClock.ToString(ReportedFormat, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The zone's name as the page's label and the owner's notice both print it - one definition, so
    /// the two cannot name the zone differently. For a browser's zone this is its IANA id.
    /// </summary>
    public static string ZoneName(TimeZoneInfo zone) => zone.Id;

    /// <summary>
    /// The zone's offset from UTC at that wall-clock time, as <c>UTC+05:30</c>. Per time, not per zone:
    /// a window that crosses a daylight-saving change has two.
    /// </summary>
    public static string OffsetAt(DateTime wallClock, TimeZoneInfo zone) => UtcOffset(zone.GetUtcOffset(WallClock(wallClock)));

    /// <summary>
    /// aud-a7f. A time Exchange reported, as the Check Status tab shows it: the wall-clock time it
    /// reads in <paramref name="zone"/>, with the zone's name and its offset at that instant - the form
    /// the owner's notice uses. When its Kind is not Utc it is shown as reported, marked as naming no
    /// zone. Migration and Recipient Lookup show their reported times through it too.
    /// </summary>
    /// <remarks>
    /// Shown by what its Kind states, because which Kind the Exchange Online module hands back is not
    /// known here - an assumption the finding record labels; no live call was made. Utc names an
    /// instant, so it converts. Unspecified names none: reading it as UTC would print a confident label
    /// that, whenever the guess is wrong, is wrong by the zone's whole offset - the ambiguity aud-a7e
    /// removed from the write. The offset is the zone's at the instant, not at the wall-clock time as
    /// <see cref="OffsetAt"/> reads it: the two instants of an hour the clocks repeat show the same clock
    /// time and differ only in it.
    /// <para>
    /// aud-a23g. Local is shown as Unspecified is. It converted through this server's zone, so the clock the
    /// reporting module carried was read as this server's - nothing proves it is, so the label it got could
    /// name the wrong instant. One rule for every page that shows a reported time.
    /// </para>
    /// </remarks>
    public static string ShowReported(DateTime reported, TimeZoneInfo zone)
    {
        if (reported.Kind != DateTimeKind.Utc)
            return reported.ToString(ReportedFormat, System.Globalization.CultureInfo.InvariantCulture) + " (time zone not reported)";

        var shown = TimeZoneInfo.ConvertTimeFromUtc(reported, zone);
        return $"{shown.ToString(ReportedFormat, System.Globalization.CultureInfo.InvariantCulture)} ({ZoneName(zone)}, {UtcOffset(zone.GetUtcOffset(reported))})";
    }

    /// <summary>
    /// aud-a7h. A reported time as the Check Status tab shows it: a readable one exactly as the overload
    /// above shows it, and one this app cannot read as a note naming what came back (and, aud-a7i, its
    /// text) - so a schedule Exchange did report is never shown as an empty window.
    /// </summary>
    public static string ShowReported(ReportedTime reported, TimeZoneInfo zone) =>
        reported.Show(time => ShowReported(time, zone));

    // The page's own form of a clock time, culture-free so the separators cannot move.
    private const string ReportedFormat = "yyyy-MM-dd HH:mm";

    // One spelling of an offset, for the notice (OffsetAt) and the Check Status tab (ShowReported).
    private static string UtcOffset(TimeSpan offset) =>
        "UTC" + (offset < TimeSpan.Zero ? "-" : "+")
            + offset.Duration().ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);

    // Unspecified, whatever the form handed over: the page's defaults start life as DateTime.Now, of
    // Kind Local - the SERVER's zone - and TimeZoneInfo would convert a Local value out of the server's
    // zone (or refuse it) instead of reading it as a time in ScheduleZone.
    private static DateTime WallClock(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
}
