using System.Management.Automation;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Slice-2 coverage for the per-message delivery-detail seams of
/// <see cref="MessageTraceService"/>. The live PowerShell fetch runs through the sealed
/// connection pool / on-prem runspace and is manual-validation-only (see the BlockedSenders
/// precedent); what is covered here is backend routing and the pure PSObject-to-event mapping,
/// including the on-prem NO-collapse guarantee and the reason fields the summary path drops.
/// </summary>
public sealed class MessageTraceDetailTests
{
    private static MessageTraceResult Summary(string backend, string messageId = "abc@contoso.com") => new()
    {
        Received = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc),
        SenderAddress = "sender@contoso.com",
        RecipientAddress = "rcpt@contoso.com",
        Subject = "Test",
        Status = "Delivered",
        MessageId = messageId,
        Backend = backend
    };

    private static PSObject OnPremRow(DateTime timestamp, string eventId, string source, string sourceContext, string recipientStatus, string messageId = "abc@contoso.com")
    {
        var ps = new PSObject();
        ps.Properties.Add(new PSNoteProperty("Timestamp", timestamp));
        ps.Properties.Add(new PSNoteProperty("EventId", eventId));
        ps.Properties.Add(new PSNoteProperty("Source", source));
        ps.Properties.Add(new PSNoteProperty("SourceContext", sourceContext));
        ps.Properties.Add(new PSNoteProperty("RecipientStatus", recipientStatus));
        ps.Properties.Add(new PSNoteProperty("MessageId", messageId));
        return ps;
    }

    private static PSObject CloudEvent(DateTime date, string evt, string action, string detail)
    {
        var ps = new PSObject();
        ps.Properties.Add(new PSNoteProperty("Date", date));
        ps.Properties.Add(new PSNoteProperty("Event", evt));
        ps.Properties.Add(new PSNoteProperty("Action", action));
        ps.Properties.Add(new PSNoteProperty("Detail", detail));
        return ps;
    }

    // ---- Backend routing --------------------------------------------------------------------

    [Theory]
    [InlineData("OnPrem", "OnPrem")]
    [InlineData("ExchangeOnline", "Cloud")]
    [InlineData("", "Unknown")]
    [InlineData("Nonsense", "Unknown")]
    [InlineData(null, "Unknown")]
    public void ClassifyDetailBackend_RoutesByBackendString(string? backend, string expected)
    {
        // Compare by name: DetailBackend is internal, so it cannot appear in this public signature.
        Assert.Equal(expected, MessageTraceService.ClassifyDetailBackend(backend).ToString());
    }

    [Fact]
    public void UnknownBackend_YieldsErrorNotThrow()
    {
        var detail = MessageTraceService.UnknownBackendDetail(Summary("Nonsense"));
        Assert.Empty(detail.Events);
        Assert.NotNull(detail.Error);
        Assert.Contains("Nonsense", detail.Error);
    }

    // ---- Outer fail-soft guard (pre-delegate throws) ----------------------------------------

    [Fact]
    public async Task RunDetailBackend_ThrowingQuery_ReturnsFailSoftDetail_NeverThrows()
    {
        // A pre-delegate throw (EXO borrow/config/pool/connect for cloud; throttle timeout for
        // on-prem) escapes the inner catches. The outer guard must convert it to a detail, not
        // propagate it into the caller (mandate item 3: never throws into the caller).
        var summary = Summary("ExchangeOnline");

        var detail = await MessageTraceService.RunDetailBackendAsync(
            summary,
            () => throw new InvalidOperationException("Exchange service is busy. Please try again shortly."));

        Assert.Same(summary, detail.Summary);
        Assert.Empty(detail.Events);
        Assert.NotNull(detail.Error);
        Assert.Contains("Exchange service is busy", detail.Error);
    }

    [Fact]
    public async Task RunDetailBackend_SucceedingQuery_PassesResultThrough()
    {
        var expected = new MessageTraceDetail { Summary = Summary("OnPrem") };

        var detail = await MessageTraceService.RunDetailBackendAsync(
            expected.Summary!,
            () => Task.FromResult(expected));

        Assert.Same(expected, detail);
    }

    // ---- On-prem: NO collapse + reason fields -----------------------------------------------

    [Fact]
    public void OnPremDetail_PreservesEveryEventRow_NoCollapse()
    {
        // Contrast the summary path, which GroupBy(...).First() collapses to a single row.
        var rows = new[]
        {
            OnPremRow(new DateTime(2026, 7, 27, 10, 0, 0), "RECEIVE", "SMTP", "", ""),
            OnPremRow(new DateTime(2026, 7, 27, 10, 0, 1), "SUBMIT", "STOREDRIVER", "", ""),
            OnPremRow(new DateTime(2026, 7, 27, 10, 0, 2), "DEFER", "SMTP", "Retry", "451 4.4.0 retry"),
            OnPremRow(new DateTime(2026, 7, 27, 10, 0, 3), "FAIL", "SMTP", "Bounce", "550 5.7.1 blocked")
        };

        var detail = MessageTraceService.BuildOnPremDetail(Summary("OnPrem"), rows);

        Assert.Equal(4, detail.Events.Count);
        Assert.Null(detail.Error);
    }

    [Fact]
    public void OnPremDetail_OrdersEventsByTimestamp()
    {
        var rows = new[]
        {
            OnPremRow(new DateTime(2026, 7, 27, 10, 0, 3), "FAIL", "SMTP", "", ""),
            OnPremRow(new DateTime(2026, 7, 27, 10, 0, 0), "RECEIVE", "SMTP", "", ""),
            OnPremRow(new DateTime(2026, 7, 27, 10, 0, 1), "SUBMIT", "STOREDRIVER", "", "")
        };

        var detail = MessageTraceService.BuildOnPremDetail(Summary("OnPrem"), rows);

        Assert.Equal(new[] { "RECEIVE", "SUBMIT", "FAIL" }, detail.Events.Select(e => e.Event).ToArray());
    }

    [Fact]
    public void OnPremDetail_CarriesReasonFields_SourceAndDetail()
    {
        var rows = new[]
        {
            OnPremRow(new DateTime(2026, 7, 27, 10, 0, 2), "FAIL", "SMTP", "Bounce", "550 5.7.1 blocked")
        };

        var evt = Assert.Single(MessageTraceService.BuildOnPremDetail(Summary("OnPrem"), rows).Events);

        Assert.Equal("SMTP", evt.Source);
        Assert.Contains("Bounce", evt.Detail);
        Assert.Contains("550 5.7.1 blocked", evt.Detail);
    }

    [Fact]
    public void OnPremDetail_FiltersRowsForOtherMessages()
    {
        var rows = new[]
        {
            OnPremRow(new DateTime(2026, 7, 27, 10, 0, 0), "RECEIVE", "SMTP", "", "", "abc@contoso.com"),
            OnPremRow(new DateTime(2026, 7, 27, 10, 0, 1), "RECEIVE", "SMTP", "", "", "other@contoso.com")
        };

        var detail = MessageTraceService.BuildOnPremDetail(Summary("OnPrem"), rows);

        var evt = Assert.Single(detail.Events);
        Assert.Equal("RECEIVE", evt.Event);
    }

    [Fact]
    public void OnPremDetail_NoEvents_SetsError()
    {
        var detail = MessageTraceService.BuildOnPremDetail(Summary("OnPrem"), Array.Empty<PSObject>());

        Assert.Empty(detail.Events);
        Assert.NotNull(detail.Error);
    }

    // ---- Cloud: mapping + aged-out ----------------------------------------------------------

    [Fact]
    public void CloudDetail_MapsEventsAndOrdersByDate()
    {
        var events = new[]
        {
            CloudEvent(new DateTime(2026, 7, 27, 10, 0, 2), "Deliver", "Delivered", "to mailbox"),
            CloudEvent(new DateTime(2026, 7, 27, 10, 0, 0), "Receive", "GettingMessageStatus", "received")
        };

        var detail = MessageTraceService.BuildCloudDetail(Summary("ExchangeOnline"), events);

        Assert.Equal(new[] { "Receive", "Deliver" }, detail.Events.Select(e => e.Event).ToArray());
        Assert.Equal("Delivered", detail.Events[1].Action);
        Assert.Equal("to mailbox", detail.Events[1].Detail);
        Assert.Null(detail.Error);
    }

    [Fact]
    public void CloudDetail_FallsBackToDataWhenNoDetail()
    {
        var evt = new PSObject();
        evt.Properties.Add(new PSNoteProperty("Date", new DateTime(2026, 7, 27, 10, 0, 0)));
        evt.Properties.Add(new PSNoteProperty("Event", "Receive"));
        evt.Properties.Add(new PSNoteProperty("Data", "<raw xml>"));

        var mapped = Assert.Single(MessageTraceService.BuildCloudDetail(Summary("ExchangeOnline"), new[] { evt }).Events);

        Assert.Equal("<raw xml>", mapped.Detail);
    }

    [Fact]
    public void CloudDetail_AgedOut_EmptyEventsWithExplanatoryMessageNotThrow()
    {
        var detail = MessageTraceService.BuildCloudDetail(Summary("ExchangeOnline"), Array.Empty<PSObject>());

        Assert.Empty(detail.Events);
        Assert.NotNull(detail.Error);
        Assert.Contains("aged out", detail.Error);
    }

    // ---- Outdated-module predicate ----------------------------------------------------------

    [Theory]
    [InlineData("The term 'Get-MessageTraceDetailV2' is not recognized as the name of a cmdlet")]
    [InlineData("CommandNotFoundException: not found")]
    public void IsOutdatedModuleError_MatchesNotRecognizedSignatures(string message)
    {
        Assert.True(MessageTraceService.IsOutdatedModuleError(new InvalidOperationException(message)));
    }

    [Theory]
    [InlineData("The mailbox could not be found.")]
    [InlineData("Access denied.")]
    public void IsOutdatedModuleError_DoesNotMatchUnrelatedErrors(string message)
    {
        Assert.False(MessageTraceService.IsOutdatedModuleError(new InvalidOperationException(message)));
    }

    // ---- Null pipeline rows -----------------------------------------------------------------
    // A live EXO trace crashed with NullReferenceException because the PowerShell pipeline
    // returned a collection containing a null element and the mapping loops dereferenced it
    // (prod 2026-07-29; docs/MessageTraceNullRow-Plan.md). Every GetProperty* helper takes a
    // non-nullable PSObject and reads obj.Properties directly, so a null row throws. The loops
    // skip nulls and keep the surrounding valid rows.

    [Fact]
    public void CloudDetail_NullPipelineRow_IsSkippedAndValidRowsSurvive()
    {
        var events = new PSObject?[]
        {
            CloudEvent(new DateTime(2026, 7, 27, 10, 0, 0), "Receive", "GettingMessageStatus", "received"),
            null,
            CloudEvent(new DateTime(2026, 7, 27, 10, 0, 2), "Deliver", "Delivered", "to mailbox")
        };

        var detail = MessageTraceService.BuildCloudDetail(Summary("ExchangeOnline"), events!);

        Assert.Equal(new[] { "Receive", "Deliver" }, detail.Events.Select(e => e.Event).ToArray());
        Assert.Null(detail.Error);
    }

    [Fact]
    public void OnPremDetail_NullPipelineRow_IsSkippedAndValidRowsSurvive()
    {
        var rows = new PSObject?[]
        {
            OnPremRow(new DateTime(2026, 7, 27, 10, 0, 0), "RECEIVE", "SMTP", "ctx1", "250 ok"),
            null,
            OnPremRow(new DateTime(2026, 7, 27, 10, 0, 2), "DELIVER", "STOREDRIVER", "ctx2", "250 delivered")
        };

        var detail = MessageTraceService.BuildOnPremDetail(Summary("OnPrem"), rows!);

        Assert.Equal(new[] { "RECEIVE", "DELIVER" }, detail.Events.Select(e => e.Event).ToArray());
    }

    [Fact]
    public void CloudDetail_AllRowsNull_ReturnsEmptyRatherThanThrowing()
    {
        var detail = MessageTraceService.BuildCloudDetail(Summary("ExchangeOnline"), new PSObject?[] { null, null }!);

        Assert.Empty(detail.Events);
    }
}
