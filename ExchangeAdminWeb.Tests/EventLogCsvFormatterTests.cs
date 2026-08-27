using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the Event Log CSV export contract (docs/EventLogCsvTicket-Plan.md AC1-AC5):
/// the eight existing columns keep their names and order, Ticket is appended ninth,
/// an empty ticket still emits a cell, and CsvHelper quoting is preserved.
/// </summary>
public class EventLogCsvFormatterTests
{
    private static EventLogCsvRow Row(string ticket = "", string target = "t") =>
        new("2026-08-27 10:00:00", "Audit", "user", "1.2.3.4",
            "Action", "Category", target, "Success", ticket);

    private static string[] Lines(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
           .Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public void Write_HeaderIsEightExistingColumnsThenTicket()
    {
        var lines = Lines(EventLogCsvFormatter.Write([]));

        Assert.Equal(
            new[] { "Time", "Source", "User", "IP", "Action", "Category", "Target", "Result", "Ticket" },
            lines[0].Split(','));
    }

    [Fact]
    public void Write_CopiesTicketValue()
    {
        var lines = Lines(EventLogCsvFormatter.Write([Row(ticket: "INC001")]));

        Assert.Equal("INC001", lines[1].Split(',').Last());
    }

    [Fact]
    public void Write_EmptyTicketStillEmitsNineColumns()
    {
        var lines = Lines(EventLogCsvFormatter.Write([Row(ticket: "")]));

        var cells = lines[1].Split(',');
        Assert.Equal(9, cells.Length);
        Assert.Equal("", cells[8]);
    }

    [Fact]
    public void Write_QuotesTargetContainingComma()
    {
        var lines = Lines(EventLogCsvFormatter.Write([Row(target: "a,b")]));

        Assert.Contains("\"a,b\"", lines[1]);
    }
}
