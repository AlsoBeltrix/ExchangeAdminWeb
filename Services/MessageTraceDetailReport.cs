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

    /// <summary>
    /// Builds the CSV export: for each message a summary header block followed by
    /// its full event trail, every field CsvEscaped. Messages are emitted in the
    /// order supplied. A message that failed to fetch (Error set / no events) still
    /// gets a block, with its error surfaced, so the export never silently drops a
    /// requested message (Known Failure Class #2).
    /// </summary>
    public static string BuildCsv(IReadOnlyList<MessageTraceDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);

        var sb = new StringBuilder();
        for (var i = 0; i < details.Count; i++)
        {
            var detail = details[i];
            var summary = detail.Summary;

            sb.Append("Message ").Append(i + 1).Append(" of ").Append(details.Count).AppendLine();

            sb.AppendLine("Origin Date-Time,Backend,Sender Address,Recipient Address,Message Subject,Status,Message ID,Message Trace ID");
            if (summary is not null)
            {
                sb.Append(summary.Received.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
                sb.Append(',');
                sb.Append(CsvEscape(summary.Backend));
                sb.Append(',');
                sb.Append(CsvEscape(summary.SenderAddress));
                sb.Append(',');
                sb.Append(CsvEscape(summary.RecipientAddress));
                sb.Append(',');
                sb.Append(CsvEscape(summary.Subject));
                sb.Append(',');
                sb.Append(CsvEscape(summary.Status));
                sb.Append(',');
                sb.Append(CsvEscape(summary.MessageId));
                sb.Append(',');
                sb.Append(CsvEscape(summary.MessageTraceId));
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(detail.Error))
                sb.Append("Detail error,").Append(CsvEscape(detail.Error)).AppendLine();

            sb.AppendLine("Date,Event,Action,Source,Detail");
            foreach (var evt in detail.Events)
            {
                sb.Append(evt.Date.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
                sb.Append(',');
                sb.Append(CsvEscape(evt.Event));
                sb.Append(',');
                sb.Append(CsvEscape(evt.Action));
                sb.Append(',');
                sb.Append(CsvEscape(evt.Source));
                sb.Append(',');
                sb.Append(CsvEscape(evt.Detail));
                sb.AppendLine();
            }

            sb.AppendLine();
        }

        return sb.ToString();
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
