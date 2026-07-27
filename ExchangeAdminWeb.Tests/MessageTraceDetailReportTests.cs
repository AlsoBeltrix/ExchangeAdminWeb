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

    [Fact]
    public void BuildCsv_EmitsSummaryHeaderBlockAndFullTrail_PerMessage()
    {
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

        Assert.Contains("Message 1 of 1", csv);
        Assert.Contains("Origin Date-Time,Backend,Sender Address", csv);
        Assert.Contains("sender@contoso.com", csv);
        Assert.Contains("Date,Event,Action,Source,Detail", csv);
        // No collapse: every event row present.
        Assert.Contains("RECEIVE", csv);
        Assert.Contains("SUBMIT", csv);
        Assert.Contains("FAIL", csv);
        Assert.Contains("550 5.7.1 blocked", csv);
    }

    [Fact]
    public void BuildCsv_OrdersMessagesAsSupplied_AndNumbersThem()
    {
        var details = new[]
        {
            new MessageTraceDetail { Summary = Summary("first@contoso.com") },
            new MessageTraceDetail { Summary = Summary("second@contoso.com") }
        };

        var csv = MessageTraceDetailReport.BuildCsv(details);

        Assert.Contains("Message 1 of 2", csv);
        Assert.Contains("Message 2 of 2", csv);
        Assert.True(csv.IndexOf("first@contoso.com", StringComparison.Ordinal)
            < csv.IndexOf("second@contoso.com", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildCsv_FailedMessage_StillEmittedWithError_NotDropped()
    {
        // Known Failure Class #2: a requested message that failed to fetch is not
        // silently dropped; its block appears with the error surfaced.
        var details = new[]
        {
            new MessageTraceDetail { Summary = Summary("ok@contoso.com"), Events = { Event(0, "RECEIVE") } },
            new MessageTraceDetail { Summary = Summary("bad@contoso.com"), Error = "Delivery detail failed: pool busy" }
        };

        var csv = MessageTraceDetailReport.BuildCsv(details);

        Assert.Contains("Message 2 of 2", csv);
        Assert.Contains("bad@contoso.com", csv);
        Assert.Contains("Detail error,", csv);
        Assert.Contains("pool busy", csv);
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
