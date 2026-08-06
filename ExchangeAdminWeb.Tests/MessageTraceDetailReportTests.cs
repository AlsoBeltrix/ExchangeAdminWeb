using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Slice-3 coverage for the pure detail-export builder and the selection-count
/// threshold rule (docs/MessageTraceDetail-Plan.md). No EXO/UI/JS: both the
/// download path and the email/bulk-job path share this code, so the CSV content
/// and the threshold definition are pinned here.
/// </summary>
public sealed class MessageTraceDetailReportTests
{
    private static MessageTraceResult Summary(string messageId = "abc@contoso.com", string subject = "Test") => new()
    {
        Received = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc),
        SenderAddress = "sender@contoso.com",
        RecipientAddress = "rcpt@contoso.com",
        Subject = subject,
        Status = "Delivered",
        MessageId = messageId,
        MessageTraceId = "trace-1",
        Backend = "OnPrem"
    };

    private static MessageTraceDetailEvent Event(int second, string evt, string action = "", string source = "", string detail = "") => new()
    {
        Date = new DateTime(2026, 7, 27, 10, 0, second, DateTimeKind.Utc),
        Event = evt,
        Action = action,
        Source = source,
        Detail = detail
    };

    // ---- Threshold rule (decision 4) --------------------------------------------------------

    [Theory]
    [InlineData(0, MessageTraceDetailAction.None)]
    [InlineData(-3, MessageTraceDetailAction.None)]
    [InlineData(1, MessageTraceDetailAction.LiveOrEmail)]
    [InlineData(10, MessageTraceDetailAction.LiveOrEmail)]
    [InlineData(11, MessageTraceDetailAction.EmailOnly)]
    [InlineData(50, MessageTraceDetailAction.EmailOnly)]
    [InlineData(999, MessageTraceDetailAction.EmailOnly)]
    public void ResolveAction_MapsCountToAllowedAction(int count, MessageTraceDetailAction expected)
    {
        Assert.Equal(expected, MessageTraceDetailReport.ResolveAction(count));
    }

    [Fact]
    public void ResolveAction_NeverAllowsLiveAboveLiveMax()
    {
        // Fail-closed: no count above 10 may open a live/download path.
        for (var n = MessageTraceDetailReport.LiveMax + 1; n <= 200; n++)
            Assert.NotEqual(MessageTraceDetailAction.LiveOrEmail, MessageTraceDetailReport.ResolveAction(n));
    }

    // ---- Select-all cap (decision 3) --------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(7, 7)]
    [InlineData(50, 50)]
    [InlineData(51, 50)]
    [InlineData(1000, 50)]
    public void SelectAllCount_CapsAtEmailMax(int available, int expected)
    {
        Assert.Equal(expected, MessageTraceDetailReport.SelectAllCount(available));
    }

    // ---- CSV builder ------------------------------------------------------------------------

    // The export is ONE TABLE: one header, one row per message. The previous format stacked three
    // different row shapes per message, so Excel read the first line as a header and dropped the
    // whole file into one column. Reported 2026-08-06 against a real export.

    [Fact]
    public void BuildCsv_IsASingleTable_OneHeaderAndOneRowPerMessage()
    {
        var details = new[]
        {
            new MessageTraceDetail { Summary = Summary("first@contoso.com"), Events = { Event(0, "RECEIVE") } },
            new MessageTraceDetail { Summary = Summary("second@contoso.com"), Events = { Event(0, "RECEIVE") } }
        };

        var csv = MessageTraceDetailReport.BuildCsv(details);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Equal(MessageTraceDetailReport.CsvHeader, lines[0]);

        // Every row has the same column count as the header - the property the old format broke.
        var columns = MessageTraceDetailReport.CsvHeader.Split(',').Length;
        Assert.All(lines, l => Assert.Equal(columns, CountColumns(l)));
    }

    [Fact]
    public void BuildCsv_NoLongerEmitsTheStackedShapeMarkers()
    {
        var csv = MessageTraceDetailReport.BuildCsv(new[]
        {
            new MessageTraceDetail { Summary = Summary(), Events = { Event(0, "RECEIVE") } }
        });

        // These are the three shapes that made the file unreadable.
        Assert.DoesNotContain("Message 1 of", csv);
        Assert.DoesNotContain("Date,Event,Action,Source,Detail", csv);
        Assert.DoesNotContain("Detail error,", csv);
    }

    [Fact]
    public void BuildCsv_OrdersMessagesAsSupplied()
    {
        var details = new[]
        {
            new MessageTraceDetail { Summary = Summary("first@contoso.com") },
            new MessageTraceDetail { Summary = Summary("second@contoso.com") }
        };

        var csv = MessageTraceDetailReport.BuildCsv(details);

        Assert.True(csv.IndexOf("first@contoso.com", StringComparison.Ordinal)
            < csv.IndexOf("second@contoso.com", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildCsv_PutsTheWholeTrailInOneCell()
    {
        // Owner direction: one row per message, no repetition. The trail is summarised into
        // columns beside the message rather than exploded into rows beneath it.
        var detail = new MessageTraceDetail
        {
            Summary = Summary(),
            Events =
            {
                Event(0, "RECEIVE", source: "SMTP"),
                Event(1, "SUBMIT", source: "STOREDRIVER"),
                Event(2, "FAIL", source: "SMTP", detail: "550 5.7.1 blocked")
            }
        };

        var csv = MessageTraceDetailReport.BuildCsv(new[] { detail });
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Contains("RECEIVE", csv);
        Assert.Contains("SUBMIT", csv);
        Assert.Contains("550 5.7.1 blocked", csv);
    }

    [Fact]
    public void BuildCsv_FailedMessage_StillEmittedWithError_NotDropped()
    {
        // Known Failure Class #2: a requested message that failed to fetch is not silently
        // dropped; it keeps its row and the error surfaces in Outcome Detail, where a reader
        // looking for "what happened" will actually find it.
        var details = new[]
        {
            new MessageTraceDetail { Summary = Summary("ok@contoso.com"), Events = { Event(0, "RECEIVE") } },
            new MessageTraceDetail { Summary = Summary("bad@contoso.com"), Error = "Delivery detail failed: pool busy" }
        };

        var csv = MessageTraceDetailReport.BuildCsv(details);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Contains("bad@contoso.com", csv);
        Assert.Contains("pool busy", csv);
        Assert.Contains("Unavailable", csv);
    }

    // ---- Outcome columns: the reason a ticket is closed with ---------------------------------

    [Fact]
    public void TheOutcomeIsTheLastEvent_NotTheTraceStatus()
    {
        // A message deferred then delivered has a trace Status that disagrees with what actually
        // happened. The trail is ground truth.
        var detail = new MessageTraceDetail
        {
            Summary = Summary(),
            Events = { Event(0, "RECEIVE"), Event(1, "DEFER"), Event(2, "DELIVER", detail: "delivered") }
        };

        Assert.Equal("DELIVER", MessageTraceDetailReport.FinalOutcome(detail));
    }

    [Fact]
    public void TheOutcomeDetailCarriesTheFailureReason()
    {
        // The single most useful field in the export: the literal answer to "why did this not
        // arrive". It gets its own column so it can be filtered, not buried in the trail cell.
        var detail = new MessageTraceDetail
        {
            Summary = Summary(),
            Events = { Event(0, "RECEIVE"), Event(1, "FAIL", detail: "550 5.7.1 blocked by policy") }
        };

        Assert.Equal("550 5.7.1 blocked by policy", MessageTraceDetailReport.OutcomeDetail(detail));
    }

    [Fact]
    public void AFetchErrorBeatsAnEmptyTrail()
    {
        // A blank outcome would read as "nothing went wrong". Saying the trail could not be read
        // is the honest answer.
        var detail = new MessageTraceDetail { Summary = Summary(), Error = "EXO unavailable" };

        Assert.Equal("Unavailable", MessageTraceDetailReport.FinalOutcome(detail));
        Assert.Equal("EXO unavailable", MessageTraceDetailReport.OutcomeDetail(detail));
    }

    [Fact]
    public void TheOutcomeDetailFallsBackToTheActionWhenThereIsNoDetail()
    {
        // On-prem rows often carry the reason in one field and not the other.
        var detail = new MessageTraceDetail
        {
            Summary = Summary(),
            Events = { new MessageTraceDetailEvent { Date = DateTime.UtcNow, Event = "FAIL", Action = "rejected", Detail = "" } }
        };

        Assert.Equal("rejected", MessageTraceDetailReport.OutcomeDetail(detail));
    }

    [Fact]
    public void AMessageWithNoEventsHasABlankOutcome_NotAnError()
    {
        var detail = new MessageTraceDetail { Summary = Summary() };

        Assert.Equal("", MessageTraceDetailReport.FinalOutcome(detail));
        Assert.Equal("", MessageTraceDetailReport.OutcomeDetail(detail));
    }

    // ---- Trail formatting --------------------------------------------------------------------

    [Fact]
    public void TheTrailKeepsEveryHopInOrder()
    {
        var events = new List<MessageTraceDetailEvent>
        {
            Event(0, "RECEIVE", detail: "received by server"),
            Event(1, "DELIVER", detail: "delivered")
        };

        var trail = MessageTraceDetailReport.FormatTrail(events);

        Assert.Contains("RECEIVE: received by server", trail);
        Assert.Contains("DELIVER: delivered", trail);
        Assert.True(trail.IndexOf("RECEIVE", StringComparison.Ordinal) < trail.IndexOf("DELIVER", StringComparison.Ordinal));
        Assert.Contains(" | ", trail);
    }

    [Fact]
    public void TheTrailSkipsANullEvent()
    {
        // A PowerShell pipeline can yield a null row (docs/MessageTraceNullRow-Plan.md).
        var events = new List<MessageTraceDetailEvent> { null!, Event(0, "DELIVER") };

        Assert.Contains("DELIVER", MessageTraceDetailReport.FormatTrail(events));
    }

    [Fact]
    public void AnEmptyTrailIsBlank()
    {
        Assert.Equal("", MessageTraceDetailReport.FormatTrail(new List<MessageTraceDetailEvent>()));
        Assert.Equal("", MessageTraceDetailReport.FormatTrail(null!));
    }

    /// <summary>Counts CSV columns, honouring quoted fields that contain commas.</summary>
    private static int CountColumns(string line)
    {
        var count = 1;
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                inQuotes = !inQuotes;
            else if (line[i] == ',' && !inQuotes)
                count++;
        }

        return count;
    }

    [Fact]
    public void BuildCsv_EscapesInjectionAndSeparators()
    {
        var detail = new MessageTraceDetail
        {
            Summary = Summary(subject: "=cmd(),\"quote\""),
            Events = { Event(0, "FAIL", detail: "line1\r\nline2") }
        };

        var csv = MessageTraceDetailReport.BuildCsv(new[] { detail });

        // Leading '=' neutralized with a single quote, then quoted for the comma/quote.
        Assert.Contains("\"'=cmd(),\"\"quote\"\"\"", csv);
        // Newline-bearing field quoted.
        Assert.Contains("\"line1\r\nline2\"", csv);
    }

    [Fact]
    public void BuildCsv_NullDetails_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MessageTraceDetailReport.BuildCsv(null!));
    }

    // ---- CsvEscape --------------------------------------------------------------------------

    [Theory]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("=formula", "'=formula")]
    [InlineData("+plus", "'+plus")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    public void CsvEscape_NeutralizesInjectionAndSeparators(string input, string expected)
    {
        Assert.Equal(expected, MessageTraceDetailReport.CsvEscape(input));
    }
}
