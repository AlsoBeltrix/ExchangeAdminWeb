using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Lower-priority Wave 4 item: "capped raw headers still feed uncapped derived routing
/// collections; large-input runtime impact unmeasured."
/// </summary>
/// <remarks>
/// <b>Half of that premise was wrong, and these tests are what showed it.</b> The derived
/// collections were never uncapped: `EmailHeaders.AddHeader` has enforced
/// `MaxTotalHeaders = 500` and `MaxValuesPerHeader = 50` all along, so a paste containing
/// fifty thousand `Received:` lines produces fifty parsed hops. A first attempt at this
/// item added a 250-hop ceiling of its own; it was dead code, unreachable behind a cap of
/// 50, and it has been removed rather than left to imply a protection that was already
/// there.
///
/// What WAS real: the existing caps were silent. An analyst could not tell a complete
/// message from one cut off, and the earliest surviving Received hop reads as the origin
/// when it is not. That is now reported through to the page.
///
/// The runtime half of the item was simply unmeasured. It is measured here.
///
/// Behavioural throughout - `HeaderAnalysisService` parses text and needs nothing else.
/// </remarks>
public class HeaderBoundsTests
{
    private static readonly HeaderAnalysisService Service =
        new(Substitute.For<ILogger<HeaderAnalysisService>>());

    private static string MessageWithReceivedHops(int count)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < count; i++)
        {
            sb.AppendLine(
                $"Received: from host{i}.example.com (host{i}.example.com [10.0.0.{i % 255}]) " +
                $"by mx.example.com with ESMTP id ABC{i}; Mon, 1 Jan 2026 00:00:00 +0000");
        }
        sb.AppendLine("From: sender@example.com");
        sb.AppendLine("To: recipient@example.com");
        sb.AppendLine("Subject: bounded");
        return sb.ToString();
    }

    [Fact]
    public void AnAbsurdNumberOfReceivedHopsIsBounded()
    {
        // Five thousand lines in, a few dozen hops out. The bound is MaxValuesPerHeader.
        var result = Service.AnalyzeHeaderText(MessageWithReceivedHops(5000));

        Assert.True(result.RoutingPath.Count <= HeaderAnalysisService.MaxReceivedHeaders,
            $"routing path should be capped, got {result.RoutingPath.Count}");
    }

    [Fact]
    public void TruncationIsReported_NotSilent()
    {
        // THE DEFECT. The bound existed; saying so did not. A path cut short reads as a
        // complete path with a different origin, and the analyst concludes the wrong thing
        // about where the message came from.
        var result = Service.AnalyzeHeaderText(MessageWithReceivedHops(5000));

        Assert.True(result.RoutingPathTruncated);
        Assert.True(result.InterestingHeadersTruncated);
    }

    [Fact]
    public void AnOrdinaryMessageIsNotReportedAsTruncated()
    {
        // A warning that fires on real mail would train the analyst to ignore it.
        var result = Service.AnalyzeHeaderText(MessageWithReceivedHops(6));

        Assert.False(result.RoutingPathTruncated);
        Assert.Equal(6, result.RoutingPath.Count);
    }

    [Fact]
    public void TheParserItselfReportsWhatItDropped()
    {
        // The flag belongs on EmailHeaders because that is where the dropping happens;
        // everything above it only relays the fact.
        var headers = new EmailHeaders();
        for (var i = 0; i < 600; i++)
            headers.AddHeader($"X-Thing-{i}", "value");

        Assert.True(headers.HeadersTruncated);
    }

    [Fact]
    public void AParserWithinItsLimitsReportsNothing()
    {
        var headers = new EmailHeaders();
        for (var i = 0; i < 10; i++)
            headers.AddHeader($"X-Thing-{i}", "value");

        Assert.False(headers.HeadersTruncated);
    }

    [Fact]
    public void RepeatingOneHeaderPastItsLimitIsAlsoReported()
    {
        // MaxValuesPerHeader, the bound that actually catches a Received flood - distinct
        // from MaxTotalHeaders and easy to miss because only one key is involved.
        var headers = new EmailHeaders();
        for (var i = 0; i < 80; i++)
            headers.AddHeader("Received", $"hop {i}");

        Assert.True(headers.HeadersTruncated);
        Assert.True(headers.Headers["Received"].Count <= 50);
    }

    [Fact]
    public void ALargeMessageAnalysesInReasonableTime()
    {
        // The audit called the large-input runtime impact "unmeasured". This measures it.
        // The threshold is loose on purpose: what matters is that the work is BOUNDED, not
        // that it hits a particular number on this host.
        var message = MessageWithReceivedHops(20000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = Service.AnalyzeHeaderText(message);
        sw.Stop();

        Assert.True(result.RoutingPathTruncated);
        Assert.True(sw.ElapsedMilliseconds < 10000,
            $"analysis of a 20k-hop message took {sw.ElapsedMilliseconds} ms");
    }
}
