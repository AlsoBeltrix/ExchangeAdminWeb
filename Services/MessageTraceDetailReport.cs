using System.Text;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// The allowed action for a given count of selected messages, per the owner
/// threshold rule (docs/MessageTraceDetail-Plan.md, decision 4).
/// </summary>
public enum MessageTraceDetailAction
{
    /// <summary>No messages selected: no action.</summary>
    None,
    /// <summary>1-10 selected: live retrieval / download allowed (email also allowed).</summary>
    LiveOrEmail,
    /// <summary>11-50 selected: live/download disabled; email is the only option.</summary>
    EmailOnly
}

/// <summary>
/// Pure, side-effect-free assembly of the per-message delivery-detail export and
/// the selection-count threshold rule. Extracted from EXO/UI/JS so both the
/// download path and the off-circuit email/bulk-job path produce identical content
/// and share one threshold definition (docs/MessageTraceDetail-Plan.md, slice 3).
/// </summary>
public static class MessageTraceDetailReport
{
    /// <summary>Live/download is allowed only up to this many selected messages (decision 4).</summary>
    public const int LiveMax = 10;

    /// <summary>Hard cap on a selection / select-all / email job (decision 3 and 5).</summary>
    public const int EmailMax = 50;

    /// <summary>
    /// Maps a selected-message count to its allowed action. Fail-closed at the
    /// bounds: 0 -> None; 1-10 -> LiveOrEmail; 11-50 -> EmailOnly; anything above
    /// the email cap is clamped to EmailOnly (the UI caps selection at 50, so a
    /// higher count is not expected, but the rule must not open a live path for it).
    /// </summary>
    public static MessageTraceDetailAction ResolveAction(int selectedCount)
    {
        if (selectedCount <= 0)
            return MessageTraceDetailAction.None;
        if (selectedCount <= LiveMax)
            return MessageTraceDetailAction.LiveOrEmail;
        return MessageTraceDetailAction.EmailOnly;
    }

    /// <summary>
    /// The number of rows a select-all should tick: the smaller of the available
    /// rows and the email cap (decision 3: select-all ticks at most the first 50).
    /// </summary>
    public static int SelectAllCount(int availableRows) =>
        availableRows < 0 ? 0 : Math.Min(availableRows, EmailMax);

    /// <summary>The export header. One table, one row per message.</summary>
    public const string CsvHeader =
        "Origin Date-Time,Backend,Sender Address,Recipient Address,Message Subject,Status," +
        "Message ID,Message Trace ID,Message Size,Sender IP,Recipient IP," +
        "Outcome,Outcome Detail,Delivery Trail";

    /// <summary>
    /// Builds the delivery-detail export: ONE ROW PER MESSAGE, one header, a real CSV.
    /// </summary>
    /// <remarks>
    /// **The previous format was not a CSV.** It stacked three different row shapes in one file -
    /// a "Message 1 of N" line, an 8-column summary header and row, then a 5-column event header
    /// and rows - repeated per message. Excel reads the first line as the header, so the whole
    /// file landed in a single column and everything after read as garbage. Reported 2026-08-06
    /// against a real export.
    ///
    /// Shape decided by owner direction the same day: the CSV must be "maximally useful" to L1/L2
    /// closing a ticket, and one row per message with no repetition
    /// ("that will get very large. you're trebling the number of rows and duplicating a lot of
    /// data"). So the per-hop trail is summarised into columns beside the message rather than
    /// exploded into rows beneath it.
    ///
    /// The three trailing columns are what this export exists for and what the summary export
    /// cannot give:
    /// - <c>Outcome</c> - the LAST event, which is where the message actually ended up.
    /// - <c>Outcome Detail</c> - that event's reason text, promoted to its own column because it
    ///   is usually the literal answer to "why did this not arrive" and must be filterable, not
    ///   buried at the end of a long cell.
    /// - <c>Delivery Trail</c> - every hop, in order, for the cases the outcome alone does not
    ///   explain.
    ///
    /// A message that failed to fetch still gets its row with the error in
    /// <c>Outcome Detail</c>, so the export never silently drops a requested message
    /// (Known Failure Class #2).
    /// </remarks>
    public static string BuildCsv(IReadOnlyList<MessageTraceDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);

        var sb = new StringBuilder();
        sb.AppendLine(CsvHeader);

        foreach (var detail in details)
        {
            var summary = detail.Summary;

            sb.Append(summary is null ? "" : summary.Received.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
            sb.Append(',');
            sb.Append(CsvEscape(summary?.Backend));
            sb.Append(',');
            sb.Append(CsvEscape(summary?.SenderAddress));
            sb.Append(',');
            sb.Append(CsvEscape(summary?.RecipientAddress));
            sb.Append(',');
            sb.Append(CsvEscape(summary?.Subject));
            sb.Append(',');
            sb.Append(CsvEscape(summary?.Status));
            sb.Append(',');
            sb.Append(CsvEscape(summary?.MessageId));
            sb.Append(',');
            sb.Append(CsvEscape(summary?.MessageTraceId));
            sb.Append(',');
            sb.Append(summary?.Size.ToString() ?? "");
            sb.Append(',');
            sb.Append(CsvEscape(summary?.FromIP));
            sb.Append(',');
            sb.Append(CsvEscape(summary?.ToIP));
            sb.Append(',');
            sb.Append(CsvEscape(FinalOutcome(detail)));
            sb.Append(',');
            sb.Append(CsvEscape(OutcomeDetail(detail)));
            sb.Append(',');
            sb.Append(CsvEscape(FormatTrail(detail.Events)));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// The event a message ended on, or a blank when its trail could not be read.
    /// </summary>
    /// <remarks>
    /// The last event is the outcome: Deliver, Fail, Defer, Quarantine. Deliberately NOT the
    /// summary's Status field - that is the trace-level status, and for a message that was
    /// deferred then delivered the two differ. The trail is the ground truth about what happened.
    /// </remarks>
    public static string FinalOutcome(MessageTraceDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        if (!string.IsNullOrEmpty(detail.Error))
            return "Unavailable";

        var last = detail.Events.LastOrDefault();
        return last is null ? "" : last.Event;
    }

    /// <summary>
    /// Why the message ended that way - the reason text L1/L2 actually needs.
    /// </summary>
    /// <remarks>
    /// A fetch error wins: if the trail could not be read, saying so is more useful than a blank
    /// that reads as "nothing went wrong". Otherwise it is the last event's detail, falling back to
    /// its action when the detail is empty (on-prem rows often carry the reason in one and not the
    /// other).
    /// </remarks>
    public static string OutcomeDetail(MessageTraceDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        if (!string.IsNullOrEmpty(detail.Error))
            return detail.Error;

        var last = detail.Events.LastOrDefault();
        if (last is null)
            return "";

        return !string.IsNullOrWhiteSpace(last.Detail) ? last.Detail : last.Action;
    }

    /// <summary>
    /// The whole hop sequence as one cell: <c>time Event: detail | time Event: detail</c>.
    /// </summary>
    /// <remarks>
    /// Times are short (HH:mm:ss) rather than full timestamps: the message's own date is already a
    /// column, and a full ISO timestamp per hop makes the cell unreadable for no added information.
    /// Empty events are skipped rather than emitting bare separators.
    /// </remarks>
    public static string FormatTrail(IReadOnlyList<MessageTraceDetailEvent> events)
    {
        if (events is null || events.Count == 0)
            return "";

        var parts = new List<string>(events.Count);
        foreach (var evt in events)
        {
            if (evt is null)
                continue;

            var reason = !string.IsNullOrWhiteSpace(evt.Detail) ? evt.Detail
                : !string.IsNullOrWhiteSpace(evt.Action) ? evt.Action
                : "";

            var name = string.IsNullOrWhiteSpace(evt.Event) ? "(event)" : evt.Event;
            var stamp = evt.Date.ToString("HH:mm:ss");

            parts.Add(string.IsNullOrWhiteSpace(reason) ? $"{stamp} {name}" : $"{stamp} {name}: {reason}");
        }

        return string.Join(" | ", parts);
    }

    /// <summary>
    /// CSV field escaping with CSV-injection neutralization, matching the summary
    /// export in MessageTrace.razor: a leading formula/control character is prefixed
    /// with a single quote; a value containing a comma, quote, or newline is quoted
    /// with embedded quotes doubled.
    /// </summary>
    public static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n')
            value = "'" + value;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
