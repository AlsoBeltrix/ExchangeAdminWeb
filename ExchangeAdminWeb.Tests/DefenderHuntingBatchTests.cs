using System.Text.Json;
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

    // ---- the observer column is discovered, not guessed ----

    [Fact]
    public void TheObserverColumnIsNeverReferencedByASingleHardCodedName()
    {
        // The defect this exists for SHIPPED. The query referenced $left.SeenBy - one of the two
        // names Microsoft's own docs give for SeenBy()'s output - and the owner's 26,812-device
        // export came back with that column empty for every row. A left-outer join turns a wrong
        // key into empty cells rather than an error, so nothing failed and nothing said so.
        var query = DefenderEndpointDeviceService.BuildObserverQuery(["aa11"]);

        Assert.DoesNotContain("$left.SeenBy", query, StringComparison.Ordinal);
        Assert.Contains("column_ifexists", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SeenBy")]          // one documented name
    [InlineData("DeviceId1")]       // the other, after KQL renames its collision with DeviceId
    [InlineData("SeenByDeviceId")]  // the third spelling seen in the wild
    public void EveryCandidateSpellingOfTheObserverColumnIsTried(string candidate)
    {
        // Named individually rather than counted, so dropping one is a failure with its name in
        // the message instead of an off-by-one in an arity assertion.
        var query = DefenderEndpointDeviceService.BuildObserverQuery(["aa11"]);

        Assert.Contains($"column_ifexists(\"{candidate}\", \"\")", query, StringComparison.Ordinal);
    }

    [Fact]
    public void TheObserverIsProjectedAsANameAndTheRawIdNeverReachesTheReport()
    {
        // "Where is this machine" cannot be answered by a GUID. A blank says "no answer"; an id
        // looks like an answer and is not one.
        var query = DefenderEndpointDeviceService.BuildObserverQuery(["aa11"]);

        Assert.Contains("RecentlySeenBy = SeenByName", query, StringComparison.Ordinal);
        Assert.DoesNotContain("RecentlySeenBy = ObserverId", query, StringComparison.Ordinal);
    }

    // ---- every observing machine is kept, not whichever row came last ----

    [Fact]
    public void ADeviceSeenBySeveralMachinesKeepsAllOfThem()
    {
        // SeenBy() answers one row per OBSERVER, so a device seen by three machines arrives as
        // three rows. For locating hardware three names are three times the evidence, and the
        // shared row reader would have kept whichever came last - arbitrarily, and differently
        // between runs.
        using var document = JsonDocument.Parse(
            "{\"results\":["
            + "{\"DeviceId\":\"d1\",\"RecentlySeenBy\":\"SWITCH-B\"},"
            + "{\"DeviceId\":\"d1\",\"RecentlySeenBy\":\"SWITCH-A\"},"
            + "{\"DeviceId\":\"d1\",\"RecentlySeenBy\":\"RECEPTION-PC\"}]}");

        Assert.True(DefenderEndpointDeviceService.TryReadObserverNames(document, out var observers));

        // Sorted, so two exports of the same sighting set compare equal.
        Assert.Equal(["RECEPTION-PC", "SWITCH-A", "SWITCH-B"], observers["d1"]);
    }

    [Fact]
    public void TheSameObserverReportedTwiceIsNotListedTwice()
    {
        using var document = JsonDocument.Parse(
            "{\"results\":["
            + "{\"DeviceId\":\"d1\",\"RecentlySeenBy\":\"SWITCH-A\"},"
            + "{\"DeviceId\":\"d1\",\"RecentlySeenBy\":\"switch-a\"}]}");

        Assert.True(DefenderEndpointDeviceService.TryReadObserverNames(document, out var observers));

        Assert.Single(observers["d1"]);
    }

    [Fact]
    public void ARowWithNoObserverNameContributesNothing()
    {
        // The join is left outer, so a device nothing observed comes back with a null name.
        // That must not become an empty entry, which would render as a stray separator.
        using var document = JsonDocument.Parse(
            "{\"results\":["
            + "{\"DeviceId\":\"d1\",\"RecentlySeenBy\":\"\"},"
            + "{\"DeviceId\":\"d2\",\"RecentlySeenBy\":\"SWITCH-A\"}]}");

        Assert.True(DefenderEndpointDeviceService.TryReadObserverNames(document, out var observers));

        Assert.False(observers.ContainsKey("d1"));
        Assert.Equal(["SWITCH-A"], observers["d2"]);
    }

    [Fact]
    public void ABodyThatIsNotAHuntingResultIsAFailureAndNotAnEmptyAnswer()
    {
        // An empty answer would mark nothing unavailable and leave the column blank, which
        // reads as "nothing has seen these devices" - the silent-failure shape this whole
        // column has already been bitten by once.
        using var notAResult = JsonDocument.Parse("{\"error\":{\"code\":\"BadRequest\"}}");

        Assert.False(DefenderEndpointDeviceService.TryReadObserverNames(notAResult, out _));
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
