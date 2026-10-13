using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The pure half of the batched hunting enrichment (S6): the query builder, the id allowlist,
/// and the enrichment factory's refusal to describe a run as something it is not.
/// </summary>
/// <remarks>
/// The service-level half - what happens when some batches fail - lives in
/// <c>DefenderEndpointDeviceServiceTests</c>, because it needs that fixture's HTTP stub. These
/// need no stub at all, which is the reason they are separate rather than an oversight.
/// </remarks>
public class DefenderHuntingBatchTests
{
    // ---- the query is narrowed to its own batch, and cannot degenerate into an unfiltered one ----

    [Fact]
    public void TheBatchQueryNamesOnlyTheIdsInThatBatch()
    {
        var query = DefenderEndpointDeviceService.BuildHuntingQuery(["aa11", "bb22"]);

        Assert.Contains("where DeviceId in (\"aa11\",\"bb22\")", query, StringComparison.Ordinal);
        Assert.StartsWith("DeviceInfo", query, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyBatchIsRejectedRatherThanSentAsAnUnfilteredQuery()
    {
        // `where DeviceId in ()` is not a narrowing - a build that let it through would ask for
        // the whole DeviceInfo table, which is what the batching exists to stop.
        Assert.Throws<ArgumentException>(() => DefenderEndpointDeviceService.BuildHuntingQuery([]));
    }

    [Fact]
    public void TheBatchQueryKeepsTheTwoClausesThatStopDuplicateAndStaleRows()
    {
        // Both are from Microsoft's own discovered-devices example and both are load-bearing:
        // arg_max collapses a device to its latest state row, isempty(MergedToDeviceId) drops
        // records superseded by a merge. Removing either silently duplicates or resurrects
        // devices, which is invisible in a passing build.
        var query = DefenderEndpointDeviceService.BuildHuntingQuery(["aa11"]);

        Assert.Contains("summarize arg_max(Timestamp, *) by DeviceId", query, StringComparison.Ordinal);
        Assert.Contains("isempty(MergedToDeviceId)", query, StringComparison.Ordinal);
    }

    // ---- the ids are interpolated into KQL, so they are vouched for ----

    [Theory]
    [InlineData("abc123")]
    [InlineData("ABCDEF0123456789")]
    [InlineData("a1b2-c3d4_e5.f6")]
    [InlineData("m1")]
    public void AnIdentifierShapedIdIsSafeToInterpolate(string id) =>
        Assert.True(DefenderEndpointDeviceService.IsSafeDeviceId(id));

    [Theory]
    [InlineData("\"")]                                // closes the string literal
    [InlineData("a\",\"b")]                           // closes it and opens another list element
    [InlineData("a\") | union DeviceLogonEvents //")] // the actual escape this guards against
    [InlineData("a\\")]                               // escapes the closing quote
    [InlineData("a b")]
    [InlineData("a,b")]
    [InlineData("a)b")]
    [InlineData("a'b")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingThatCouldBreakOutOfTheKqlStringIsRejected(string? id) =>
        Assert.False(DefenderEndpointDeviceService.IsSafeDeviceId(id));

    [Fact]
    public void TheAllowlistIsNotNarrowedToTodaysDefenderIdFormat()
    {
        // Guards a safety argument, not a format. An earlier version admitted only hexadecimal,
        // which made "is this safe?" depend on a fact about Microsoft's current id format - and
        // would have silently marked every device unavailable the day that format widened.
        // .agents/repo-guidance.md forbids resting a safety argument on an environmental fact.
        Assert.True(DefenderEndpointDeviceService.IsSafeDeviceId("3f2504e0-4f89-11d3-9a0c-0305e82c3301"));
        Assert.True(DefenderEndpointDeviceService.IsSafeDeviceId("device_name.corp"));
    }

    // ---- the factory refuses to describe a run as something it is not ----

    [Fact]
    public void APartialEnrichmentWithNoFailedBatchIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DefenderDiscoveryEnrichment.PartiallySucceeded(
            new Dictionary<string, DefenderDeviceDiscovery>(),
            new HashSet<string> { "m1" },
            failedBatches: 0,
            totalBatches: 3,
            reason: "r"));

    [Fact]
    public void APartialEnrichmentWhereEveryBatchFailedIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DefenderDiscoveryEnrichment.PartiallySucceeded(
            new Dictionary<string, DefenderDeviceDiscovery>(),
            new HashSet<string> { "m1" },
            failedBatches: 3,
            totalBatches: 3,
            reason: "r"));

    [Fact]
    public void APartialEnrichmentNamingNoUnavailableDeviceIsRejected() =>
        Assert.Throws<ArgumentException>(() => DefenderDiscoveryEnrichment.PartiallySucceeded(
            new Dictionary<string, DefenderDeviceDiscovery>(),
            new HashSet<string>(),
            failedBatches: 1,
            totalBatches: 3,
            reason: "r"));

    [Fact]
    public void ASucceededEnrichmentNamesNoUnavailableDevice()
    {
        // The invariant the whole partial design rests on: there is no value of this type that
        // both claims success and tells the page to grey a device.
        var enrichment = DefenderDiscoveryEnrichment.Succeeded(
            new Dictionary<string, DefenderDeviceDiscovery>());

        Assert.Empty(enrichment.UnavailableDeviceIds);
        Assert.Equal(0, enrichment.FailedBatches);
    }
}
