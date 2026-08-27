using System.Globalization;
using CsvHelper;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// One CSV row of the Admin Event Log export. Field order is the CSV column order.
/// </summary>
public sealed record EventLogCsvRow(
    string Time, string Source, string User, string Ip,
    string Action, string Category, string Target, string Result,
    string Ticket);

/// <summary>
/// Formats Admin Event Log entries as the CSV the Download CSV button serves.
/// Extracted from AdminEventLog.razor so the header set and quoting are testable
/// without a Blazor harness. Stays on CsvWriter.WriteField (no ClassMap): the
/// export's quoting contract is pinned to this writer
/// (docs/EventLogCsvTicket-Plan.md AC5).
/// </summary>
public static class EventLogCsvFormatter
{
    public static string Write(IEnumerable<EventLogCsvRow> rows)
    {
        using var writer = new StringWriter();
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        csv.WriteField("Time");
        csv.WriteField("Source");
        csv.WriteField("User");
        csv.WriteField("IP");
        csv.WriteField("Action");
        csv.WriteField("Category");
        csv.WriteField("Target");
        csv.WriteField("Result");
        csv.WriteField("Ticket");
        csv.NextRecord();

        foreach (var row in rows)
        {
            csv.WriteField(row.Time);
            csv.WriteField(row.Source);
            csv.WriteField(row.User);
            csv.WriteField(row.Ip);
            csv.WriteField(row.Action);
            csv.WriteField(row.Category);
            csv.WriteField(row.Target);
            csv.WriteField(row.Result);
            csv.WriteField(row.Ticket);
            csv.NextRecord();
        }

        csv.Flush();
        return writer.ToString();
    }
}
