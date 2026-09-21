using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CsvHelper;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Paging, completion and parsing tests for the Defender for Endpoint device listing
/// (docs/DefenderEndpointDevices-Plan.md S1). Every one runs against a locally declared HTTP stub:
/// this slice makes no live call and cannot, because the module descriptor that would let anyone
/// enter a Secret ID does not arrive until S2.
/// </summary>
/// <remarks>
/// The load-bearing tests here are the completion rule's. A run is complete only on a POSITIVE
/// proof of exhaustion, and the guard test - a full page carrying no continuation cursor must
/// refuse - is the one that fails against the design this plan replaced, where that same response
/// fell into a $skip fallback and rendered a list.
/// </remarks>
public class DefenderEndpointDeviceServiceTests
{
    /// <summary>
    /// Serves a canned token for login.microsoftonline.com and then answers each API request from a
    /// per-test script, recording the request URLs exactly as they went on the wire. A script that
    /// runs out repeats its last entry, so an "always another cursor" test needs one line.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<Func<HttpResponseMessage>> Responses { get; } = [];
        public List<string> RequestUrls { get; } = [];

        /// <summary>The method of each recorded request, so "it POSTed" is asserted and not assumed.</summary>
        public List<string> RequestMethods { get; } = [];

        /// <summary>
        /// Each recorded request's body exactly as it went on the wire. The S4 query-text assertions
        /// read the KQL off the wire rather than off the constant: a test that only compared the
        /// constant to itself would pass against an implementation that posted something else.
        /// </summary>
        public List<string> RequestBodies { get; } = [];

        /// <summary>
        /// How the token endpoint answers. Null is the canned success every other test relies on;
        /// a test that needs the SIGN-IN itself to fail replaces it. Token requests are deliberately
        /// not recorded in RequestUrls - every existing assertion counts API calls, and a recorded
        /// token request would shift each of those counts by one.
        /// </summary>
        public Func<HttpResponseMessage>? TokenResponder { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return TokenResponder?.Invoke()
                    ?? Json(HttpStatusCode.OK, """{"access_token":"test-token","expires_in":3600}""");

            var index = RequestUrls.Count;
            RequestUrls.Add(request.RequestUri.OriginalString);
            RequestMethods.Add(request.Method.Method);
            RequestBodies.Add(request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var responder = Responses.Count == 0
                ? () => Json(HttpStatusCode.OK, """{"value":[]}""")
                : Responses[Math.Min(index, Responses.Count - 1)];

            return responder();
        }

        public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body) };
    }

    private static (DefenderEndpointDeviceService Service, StubHandler Handler) CreateService(string? maxDevices = null)
    {
        var handler = new StubHandler();
        var apiClient = new DefenderApiClient(
            "tenant",
            "client",
            "secret",
            DefenderApiClient.DefenderBaseUrl,
            DefenderApiClient.DefenderTokenScope,
            new HttpClient(handler));
        var service = new DefenderEndpointDeviceService(
            () => Task.FromResult<DefenderApiClient?>(apiClient),
            maxDevices);
        return (service, handler);
    }

    private const string Host = "https://api.security.microsoft.com";

    /// <summary>Builds a machines page body: ids m{first}..m{first+count-1}, optional cursor.</summary>
    private static string Page(
        int count, int first = 1, string? nextLink = null, string osPlatform = "Windows10", string extra = "")
    {
        var body = new StringBuilder("{\"value\":[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                body.Append(',');
            var id = first + i;
            body.Append(
                $"{{\"id\":\"m{id}\",\"computerDnsName\":\"host{id}.corp.example\",\"osPlatform\":\"{osPlatform}\","
                + "\"onboardingStatus\":\"CanBeOnboarded\"" + extra + "}");
        }

        body.Append(']');
        if (nextLink != null)
            body.Append($",\"@odata.nextLink\":\"{nextLink}\"");
        body.Append('}');
        return body.ToString();
    }

    private static HttpResponseMessage Ok(string body) => StubHandler.Json(HttpStatusCode.OK, body);

    // ---- P1: a single short cursor-free page is a complete run --------------------------------

    [Fact]
    public async Task SingleShortCursorFreePage_Completes()
    {
        // Cheapest broken implementation that still passes: returning whatever the first page held,
        // unconditionally. Defeated by the refusal tests below, which feed responses that a
        // return-the-first-page implementation would also render.
        var (service, handler) = CreateService(maxDevices: "50");
        handler.Responses.Add(() => Ok(Page(2)));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Equal(2, result.Devices.Count);
        Assert.Equal(2, result.DistinctCount);
        Assert.Equal(1, result.PagesFetched);
        Assert.Equal("", result.RefusalReason);
        Assert.Single(handler.RequestUrls);
    }

    // ---- P2: a cursor chain, and the absolute link is sent unmodified -------------------------

    [Fact]
    public async Task CursorChain_CompletesAndFollowsTheAbsoluteLinkUnmodified()
    {
        // The absolute nextLink is the trap the shared GraphTokenClient falls into: it prepends its
        // base URL, so the request breaks. Cheapest broken implementation that still passes:
        // ignoring the cursor entirely - defeated because page two's device would be missing.
        const string NextLink = Host + "/api/machines?$top=3&$skiptoken=abc%2Bdef%3D%3D";
        var (service, handler) = CreateService(maxDevices: "100");
        handler.Responses.Add(() => Ok(Page(3, first: 1, nextLink: NextLink)));
        handler.Responses.Add(() => Ok(Page(1, first: 4)));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Equal(4, result.Devices.Count);
        Assert.Equal(2, result.PagesFetched);
        Assert.Equal(2, handler.RequestUrls.Count);
        Assert.Equal(NextLink, handler.RequestUrls[1]);
    }

    [Fact]
    public async Task DuplicateRowAcrossPages_IsDeduplicatedOnId()
    {
        // De-duplication is cheap insurance against a repeated row inside a cursor chain. It is NOT
        // a completeness argument: it removes overlaps and cannot detect a gap, which is why $skip
        // is not in this design at all.
        const string NextLink = Host + "/api/machines?$top=5&$skiptoken=abc";
        var (service, handler) = CreateService(maxDevices: "100");
        handler.Responses.Add(() => Ok(Page(2, first: 1, nextLink: NextLink)));
        handler.Responses.Add(() => Ok(Page(2, first: 2)));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Equal(3, result.DistinctCount);
        Assert.Equal(3, result.Devices.Count);
        Assert.Equal(["m1", "m2", "m3"], result.Devices.Select(device => device.Id).Order().ToArray());
    }

    // ---- THE GUARD: a full response with no continuation cursor is never believed --------------

    [Fact]
    public async Task FullContinuationPageWithNoCursor_RefusesWhenTheRangeCannotBeDivided()
    {
        // The case that LOOKS like success. A response holding exactly as many rows as the request
        // asked for is byte-identical whether that many exist or the service capped us, so there is
        // no way to tell them apart. Revision 3 divides the range and asks again instead of
        // refusing outright - but these rows carry no lastSeen, so there is no point inside the
        // range to divide at, and the refusal is still the only honest answer.
        //
        // Cheapest broken implementation that still passes: none of the obvious ones. "Stop when a
        // response carries no cursor" completes here and fails this test. "Always refuse" fails the
        // two completion tests above and every partition test below. "Divide blindly on wall-clock
        // time" loops until the request budget and reports the budget refusal, not this one.
        const string NextLink = Host + "/api/machines?$top=2&$skiptoken=abc";
        var (service, handler) = CreateService(maxDevices: "100");
        handler.Responses.Add(() => Ok(Page(2, first: 1, nextLink: NextLink)));
        handler.Responses.Add(() => Ok(Page(2, first: 3)));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.IncompletePaging, result.Outcome);
        Assert.Empty(result.Devices);
        Assert.False(result.CeilingExceeded);
        Assert.Contains("cannot be divided further", result.RefusalReason);
        Assert.Contains("no device returned has a Last seen timestamp", result.RefusalReason);
    }

    [Fact]
    public async Task FullFirstPageAtTheApiMaximumWithNoCursor_Refuses()
    {
        // The production shape of the same guard: MaxDevices at the API's documented per-request
        // maximum, so $top clamps to 10000 and a full response cannot be distinguished from a
        // capped one. The ceiling is NOT breached here (10000 is not more than 10000), so this can
        // only be caught by the ambiguity rule and not by the ceiling rule.
        var (service, handler) = CreateService(maxDevices: "10000");
        handler.Responses.Add(() => Ok(Page(DefenderEndpointDeviceService.MaxTop)));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.IncompletePaging, result.Outcome);
        Assert.Empty(result.Devices);
        Assert.Contains("10000", result.RefusalReason);
    }

    // ---- The ceiling refuses; it never truncates ----------------------------------------------

    [Fact]
    public async Task MoreDevicesThanMaxDevices_RefusesInOneRoundTrip()
    {
        // Asking for MaxDevices + 1 is what makes the ceiling test decisive without a second call.
        var (service, handler) = CreateService(maxDevices: "3");
        handler.Responses.Add(() => Ok(Page(4)));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.CeilingExceeded, result.Outcome);
        Assert.True(result.CeilingExceeded);
        Assert.Empty(result.Devices);
        Assert.Equal(3, result.Ceiling);
        Assert.Equal(1, result.PagesFetched);
        Assert.Contains("More than 3 devices match", result.RefusalReason);
        Assert.Contains("Raise Maximum Devices", result.RefusalReason);
    }

    [Fact]
    public void TheCeilingRefusalNamesTheFiltersThatCannotReduceTheFetch()
    {
        // "Narrow the filters" was the advice the shipped module gave, and it was useless: the only
        // server-side filter was one dropdown. Five of the filters on the page now narrow the SHOWN
        // set and not the FETCHED one, so an operator who reaches this refusal while filtering on a
        // machine group would otherwise be sent round a loop that cannot terminate.
        //
        // Cheapest broken implementation that still passes: a message listing the client-side names
        // and no server-side ones - defeated by asserting both halves.
        var reason = DefenderEndpointDeviceService.CeilingRefusal(500);

        Assert.Contains("Raise Maximum Devices", reason);
        // EVERY server-side name, not a sample of them: a regression that drops one sends an
        // operator away from a filter that would in fact have made the fetch smaller, and a
        // four-of-seven assertion would not notice.
        foreach (var serverSide in new[]
        {
            "onboarding status", "platform", "health status", "risk score", "exposure level",
            "device name", "Last seen window"
        })
            Assert.Contains(serverSide, reason);

        foreach (var clientSide in new[] { "Machine tag", "machine group", "First seen", "Any Windows" })
            Assert.Contains(clientSide, reason);

        Assert.Contains("do not reduce it", reason);
    }

    [Fact]
    public async Task CursorChainThatWouldExceedMaxDevices_Refuses()
    {
        // Cheapest broken implementation that still passes: truncating to the ceiling and returning
        // the first N. Defeated by asserting the device list is EMPTY - a partial list that looks
        // complete is the one result this module must never produce.
        const string NextLink = Host + "/api/machines?$top=2&$skiptoken=abc";
        var (service, handler) = CreateService(maxDevices: "3");
        handler.Responses.Add(() => Ok(Page(2, first: 1, nextLink: NextLink)));
        handler.Responses.Add(() => Ok(Page(2, first: 3, nextLink: NextLink)));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.CeilingExceeded, result.Outcome);
        Assert.Empty(result.Devices);
        Assert.Equal(2, result.PagesFetched);
    }

    [Fact]
    public async Task AnEndlessCursorChain_StopsAndRefuses()
    {
        // An unbounded loop against the API is how a read module becomes an outage. Hitting the
        // budget REFUSES, because a run that stopped early has not proved exhaustion.
        var (service, handler) = CreateService(maxDevices: "1000000");
        handler.Responses.Add(() => Ok(Page(1, first: 1, nextLink: Host + "/api/machines?$top=1&$skiptoken=abc")));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.IncompletePaging, result.Outcome);
        Assert.Empty(result.Devices);
        Assert.Equal(DefenderEndpointDeviceService.MaxRequestsPerRun, result.PagesFetched);
        Assert.Contains($"Stopped after {DefenderEndpointDeviceService.MaxRequestsPerRun} requests", result.RefusalReason);
    }

    // ---- The Last seen partition: the whole point of Revision 3 --------------------------------
    //
    // These run against InventoryHandler rather than a canned script. It answers the query it is
    // given - honouring $top, lastSeen ge, lastSeen lt and lastSeen eq null, and issuing no
    // @odata.nextLink, exactly as the live service was observed to behave on 2026-09-21 - so a
    // partition whose parts do not meet, or do not cover, produces a WRONG ANSWER here rather than
    // a passing assertion about a URL.

    /// <summary>
    /// An HTTP stub that ANSWERS the query instead of replaying a script.
    /// </summary>
    /// <remarks>
    /// The point of difference from <see cref="StubHandler"/>, and the reason both exist. A scripted
    /// stub can prove that a request was SENT; only a stub that evaluates the filter can prove that
    /// the set of requests COVERS the inventory. Every assertion about the partition missing or
    /// double-counting a device depends on this class applying lastSeen the way the service does:
    /// <c>ge</c> inclusive, <c>lt</c> exclusive, and a null lastSeen matching neither.
    /// </remarks>
    private sealed class InventoryHandler(
        IReadOnlyList<(string Id, DateTimeOffset? LastSeen)> inventory,
        bool supportsNullFilter = true) : HttpMessageHandler
    {
        public List<string> Filters { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Task.FromResult(StubHandler.Json(
                    HttpStatusCode.OK, """{"access_token":"test-token","expires_in":3600}"""));

            var url = request.RequestUri.OriginalString;
            var query = url[(url.IndexOf('?') + 1)..];
            var top = int.Parse(QueryValue(query, "$top"), CultureInfo.InvariantCulture);
            var filter = QueryValue(query, "$filter");
            Filters.Add(filter);

            if (!supportsNullFilter && filter.Contains("lastSeen eq null", StringComparison.Ordinal))
                return Task.FromResult(StubHandler.Json(
                    HttpStatusCode.BadRequest,
                    """{"error":{"code":"BadRequest","message":"lastSeen eq null is not supported"}}"""));

            var matches = inventory.Where(device => Matches(device.LastSeen, filter)).Take(top).ToList();

            var body = new StringBuilder("{\"value\":[");
            for (var i = 0; i < matches.Count; i++)
            {
                if (i > 0)
                    body.Append(',');

                body.Append($"{{\"id\":\"{matches[i].Id}\",\"osPlatform\":\"Windows10\"");
                if (matches[i].LastSeen is { } seen)
                    body.Append($",\"lastSeen\":\"{seen.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffffZ}\"");
                body.Append('}');
            }

            body.Append("]}");
            return Task.FromResult(StubHandler.Json(HttpStatusCode.OK, body.ToString()));
        }

        /// <summary>The decoded value of one query parameter, or "" when it is absent.</summary>
        private static string QueryValue(string query, string name)
        {
            foreach (var pair in query.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && Uri.UnescapeDataString(pair[..eq]) == name)
                    return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }

            return "";
        }

        /// <summary>
        /// Applies only the lastSeen clauses. Every other clause is satisfied by construction in
        /// these fixtures, and asserting on them is the job of the URL tests rather than this stub.
        /// </summary>
        private static bool Matches(DateTimeOffset? lastSeen, string filter)
        {
            foreach (var clause in filter.Split(" and ", StringSplitOptions.RemoveEmptyEntries))
            {
                if (clause == "lastSeen eq null")
                {
                    if (lastSeen != null)
                        return false;
                }
                else if (clause.StartsWith("lastSeen ge ", StringComparison.Ordinal))
                {
                    if (lastSeen == null || lastSeen.Value < Bound(clause))
                        return false;
                }
                else if (clause.StartsWith("lastSeen lt ", StringComparison.Ordinal))
                {
                    if (lastSeen == null || lastSeen.Value >= Bound(clause))
                        return false;
                }
            }

            return true;
        }

        private static DateTimeOffset Bound(string clause) =>
            DateTimeOffset.Parse(clause[(clause.LastIndexOf(' ') + 1)..],
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    /// <summary>The fixture: <paramref name="dated"/> devices one day apart, plus
    /// <paramref name="undated"/> devices with no lastSeen, deliberately NOT in time order.</summary>
    private static List<(string Id, DateTimeOffset? LastSeen)> Inventory(int dated, int undated)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var devices = new List<(string, DateTimeOffset?)>();

        for (var i = 0; i < dated; i++)
            devices.Add(($"d{i}", start.AddDays(i)));

        for (var i = 0; i < undated; i++)
            devices.Add(($"u{i}", null));

        // A fixed permutation rather than time order: a response is an arbitrary subset of its
        // range, and an implementation that only works when the API answers in order must fail.
        return devices
            .Select((device, index) => (device, key: index * 37 % Math.Max(devices.Count, 1)))
            .OrderBy(entry => entry.key)
            .Select(entry => entry.device)
            .ToList();
    }

    private static DefenderEndpointDeviceService PartitionService(
        InventoryHandler handler, int pageSize, string maxDevices = "100000")
    {
        var apiClient = new DefenderApiClient(
            "tenant", "client", "secret",
            DefenderApiClient.DefenderBaseUrl,
            DefenderApiClient.DefenderTokenScope,
            new HttpClient(handler));

        return new DefenderEndpointDeviceService(
            () => Task.FromResult<DefenderApiClient?>(apiClient),
            maxDevices,
            pageSizeOverride: pageSize);
    }

    [Fact]
    public async Task MoreDevicesThanOneRequestCanReturn_AreAllListedByDividingOnLastSeen()
    {
        // THE test this rebuild exists for. The tenant holds more devices than a single request can
        // return, and the service was observed to issue no continuation link, so the only way to
        // see all of them is to ask a different question each time. Forty dated devices and three
        // undated ones against a ten-row cap: four requests could not possibly cover it.
        //
        // Cheapest broken implementation that still passes: none found. Returning the first
        // response fails on count. Refusing fails on outcome. Dividing but forgetting the undated
        // devices fails on the three "u" ids. Dividing with children that do not meet fails on
        // whichever dated device sits on a boundary.
        var handler = new InventoryHandler(Inventory(dated: 40, undated: 3));
        var service = PartitionService(handler, pageSize: 10);

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Equal(43, result.Devices.Count);
        Assert.Equal(43, result.DistinctCount);
        Assert.True(result.RangesCompleted >= 5,
            $"a 43-device inventory at a ten-row cap cannot be proved in {result.RangesCompleted} part(s)");

        // The cost, bounded rather than merely observed. A division that converges spends roughly
        // 2 * leaves - 1 requests plus one for the no-lastSeen part; anything far above that is a
        // partition that terminates without converging, which the request budget would eventually
        // catch but only after minutes of calls.
        Assert.InRange(result.PagesFetched, 6, 3 * result.RangesCompleted);

        // Named rather than counted, because a count can be right while the membership is wrong.
        var ids = result.Devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < 40; i++)
            Assert.Contains($"d{i}", ids);
        for (var i = 0; i < 3; i++)
            Assert.Contains($"u{i}", ids);
    }

    [Fact]
    public async Task TheDevicesWithNoLastSeen_AreFetchedByATheirOwnRequestOnceTheRootIsDivided()
    {
        // A null lastSeen satisfies neither ge nor lt, so a partition built only from intervals
        // omits these devices and the omission is invisible: the run still reports Complete.
        //
        // Cheapest broken implementation that still passes an "all devices returned" assertion on a
        // small fixture: one that never divides at all, because the undivided root carries no
        // lastSeen clause. Defeated by the ten-row cap, which forces division, and by asserting the
        // eq-null request was actually sent.
        var handler = new InventoryHandler(Inventory(dated: 30, undated: 4));
        var service = PartitionService(handler, pageSize: 10);

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Equal(4, result.Devices.Count(device => device.Id.StartsWith('u')));
        Assert.Single(handler.Filters, filter => filter.Contains("lastSeen eq null", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASmallInventoryIsStillOneRequest_AndTheNoLastSeenRequestIsNeverSent()
    {
        // The undivided root carries no lastSeen clause at all, so it already returns the devices
        // that have none. Sending a second request for them would double the cost of every ordinary
        // load for nothing.
        var handler = new InventoryHandler(Inventory(dated: 5, undated: 2));
        var service = PartitionService(handler, pageSize: 10);

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Equal(7, result.Devices.Count);
        Assert.Equal(1, result.PagesFetched);
        Assert.Equal(1, result.RangesCompleted);
        Assert.DoesNotContain(handler.Filters, filter => filter.Contains("lastSeen eq null", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOperatorSuppliedLastSeenWindow_IsTheRootRangeAndSuppressesTheNoLastSeenRequest()
    {
        // When the operator bounds Last seen, devices carrying no Last seen value are outside what
        // they asked for, and adding them would answer a different question. The bound is also the
        // partition's root, so it narrows the fetch rather than the display.
        //
        // Cheapest broken implementation that still passes: applying the window client-side.
        // Defeated by asserting the ONE request that was sent already carried both bounds.
        var handler = new InventoryHandler(Inventory(dated: 40, undated: 3));
        var service = PartitionService(handler, pageSize: 10);

        var result = await service.ListDevicesAsync(new DefenderDeviceFilters
        {
            LastSeenFrom = new DateTimeOffset(2026, 1, 11, 0, 0, 0, TimeSpan.Zero),
            LastSeenTo = new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero)
        });

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);

        // d10 is 2026-01-11 and d18 is 2026-01-19. d19 is 2026-01-20 and the upper bound is
        // exclusive, so it is out; so are all three devices with no Last seen value.
        Assert.Equal(
            Enumerable.Range(10, 9).Select(i => $"d{i}").Order().ToArray(),
            result.Devices.Select(device => device.Id).Order().ToArray());

        var only = Assert.Single(handler.Filters);
        Assert.Contains("lastSeen ge 2026-01-11T00:00:00.0000000Z", only);
        Assert.Contains("lastSeen lt 2026-01-20T00:00:00.0000000Z", only);
        Assert.DoesNotContain("lastSeen eq null", only);
    }

    [Fact]
    public async Task AServiceThatRejectsTheNoLastSeenFilter_RefusesAndNamesTheWindowToSet()
    {
        // lastSeen eq null is core OData v4 but is not documented by worked example on this
        // collection. If the service will not take it, the devices it reaches are unreachable, so
        // the run must refuse - and the refusal has to name the one thing the operator can do that
        // makes the question answerable.
        //
        // Cheapest broken implementation that still passes an "it refuses" assertion: treating the
        // 400 as an empty part and completing. Defeated by the outcome assertion.
        var handler = new InventoryHandler(Inventory(dated: 40, undated: 3), supportsNullFilter: false);
        var service = PartitionService(handler, pageSize: 10);

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.RequestFailed, result.Outcome);
        Assert.Empty(result.Devices);
        Assert.Contains("no Last seen timestamp", result.RefusalReason);
        Assert.Contains("Set a Last seen window", result.RefusalReason);
    }

    [Fact]
    public void TwoSiblingPartsMeetExactly_WithNoGapAndNoDeviceInBoth()
    {
        // The arithmetic the whole design rests on, asserted on the strings that go on the wire.
        // Swapping ge for gt leaves a device whose lastSeen is exactly the split point in neither
        // part; swapping lt for le puts it in both. The first silently loses a device, which is the
        // failure this module must never produce.
        var split = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var filters = new DefenderDeviceFilters { OnboardingStatus = "" };

        var low = DefenderEndpointDeviceService.BuildMachinesUrl(
            filters, DefenderLastSeenRange.Between(null, split), 10);
        var high = DefenderEndpointDeviceService.BuildMachinesUrl(
            filters, DefenderLastSeenRange.Between(split, null), 10);

        var literal = DefenderEndpointDeviceService.ODataDateTimeLiteral(split);
        Assert.Equal("2026-03-04T05:06:07.0000000Z", literal);
        Assert.Contains($"lastSeen%20lt%20{literal}", low);
        Assert.Contains($"lastSeen%20ge%20{literal}", high);
        Assert.DoesNotContain("gt", low);
        Assert.DoesNotContain("le%20", high);
    }

    [Fact]
    public void ASplitPointIsAlwaysStrictlyInsideItsRange()
    {
        // The termination argument. A split point equal to either bound produces a child identical
        // to its parent, and the run never ends.
        //
        // Cheapest broken implementation that still passes the partition tests above: taking the
        // median of every returned timestamp without the strictly-inside test. Defeated by the
        // second case here, where more than half the rows sit exactly on the lower bound.
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

        var spread = Enumerable.Range(0, 9)
            .Select(i => new DefenderDevice { Id = $"d{i}", LastSeen = from.AddDays(i) })
            .ToList();

        var split = DefenderEndpointDeviceService.ChooseSplit(
            DefenderLastSeenRange.Between(from, to), spread);

        Assert.NotNull(split);
        Assert.True(split > from && split < to);

        // Five of nine rows sit exactly on the lower bound, so the plain median IS the lower bound.
        var clustered = Enumerable.Range(0, 9)
            .Select(i => new DefenderDevice { Id = $"d{i}", LastSeen = i < 5 ? from : from.AddDays(i) })
            .ToList();

        var clusteredSplit = DefenderEndpointDeviceService.ChooseSplit(
            DefenderLastSeenRange.Between(from, to), clustered);

        Assert.NotNull(clusteredSplit);
        Assert.True(clusteredSplit > from && clusteredSplit < to);
    }

    [Fact]
    public void ARangeWithNothingToDivideOn_CannotBeSplit()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Every row exactly on the inclusive lower bound.
        Assert.Null(DefenderEndpointDeviceService.ChooseSplit(
            DefenderLastSeenRange.Between(from, from.AddDays(1)),
            [new DefenderDevice { Id = "a", LastSeen = from }, new DefenderDevice { Id = "b", LastSeen = from }]));

        // No timestamps at all.
        Assert.Null(DefenderEndpointDeviceService.ChooseSplit(
            DefenderLastSeenRange.Between(null, null),
            [new DefenderDevice { Id = "a" }]));

        // The no-lastSeen part has no axis to divide by, whatever it returned.
        Assert.Null(DefenderEndpointDeviceService.ChooseSplit(
            DefenderLastSeenRange.NoLastSeen(),
            [new DefenderDevice { Id = "a", LastSeen = from }]));
    }

    [Fact]
    public async Task MoreDevicesWithNoLastSeenThanOneRequestReturns_RefusesRatherThanUnderReporting()
    {
        // The no-lastSeen part is the one part with no axis to divide on, so a tenant with more of
        // them than a request can return cannot be proved complete. Refuse, and name the window.
        var handler = new InventoryHandler(Inventory(dated: 40, undated: 15));
        var service = PartitionService(handler, pageSize: 10);

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.IncompletePaging, result.Outcome);
        Assert.Empty(result.Devices);
        Assert.Contains("More than 10 devices have no Last seen timestamp", result.RefusalReason);
        Assert.Contains("Set a Last seen window", result.RefusalReason);
    }

    [Fact]
    public async Task ThePartitionVisitsItsPartsInAscendingTimeOrder()
    {
        // A device that reports in mid-run moves FORWARD in lastSeen. Visiting low parts first means
        // it can only move into a part not yet read; visiting high parts first would let it escape
        // between two requests and vanish from the answer.
        //
        // Cheapest broken implementation that still passes every other partition test: pushing the
        // high child first. Nothing else here would notice.
        var handler = new InventoryHandler(Inventory(dated: 40, undated: 0));
        var service = PartitionService(handler, pageSize: 10);

        var result = await service.ListDevicesAsync();
        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);

        // The lower bound of each request that carries one, in the order they were sent.
        var lowerBounds = handler.Filters
            .Select(filter => filter.Split(" and ", StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(clause => clause.StartsWith("lastSeen ge ", StringComparison.Ordinal)))
            .OfType<string>()
            .Select(clause => DateTimeOffset.Parse(clause[(clause.LastIndexOf(' ') + 1)..],
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
            .ToList();

        Assert.NotEmpty(lowerBounds);
        Assert.Equal(lowerBounds.OrderBy(bound => bound).ToList(), lowerBounds);
    }


    // ---- T5: 404 on the FIRST request is the documented empty inventory -----------------------

    [Fact]
    public async Task FirstRequest404_IsAnEmptyInventoryAndNotAFailure()
    {
        // Learn: "If there are no recent machines, you see 404 Not Found." This inverts the repo's
        // usual rule, so both directions are pinned - see the next test.
        var (service, handler) = CreateService();
        handler.Responses.Add(() => StubHandler.Json(HttpStatusCode.NotFound, ""));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Empty(result.Devices);
        Assert.Equal(0, result.DistinctCount);
    }

    [Fact]
    public async Task Continuation404_IsAFailureAndNotAnEmptyInventory()
    {
        // Cheapest broken implementation that still passes the test above: mapping EVERY 404 to an
        // empty inventory. Defeated here - a 404 partway through a chain means the continuation no
        // longer resolves, so the list is incomplete and must not render.
        var (service, handler) = CreateService(maxDevices: "100");
        handler.Responses.Add(() => Ok(Page(2, first: 1, nextLink: Host + "/api/machines?$top=2&$skiptoken=abc")));
        handler.Responses.Add(() => StubHandler.Json(HttpStatusCode.NotFound, ""));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.RequestFailed, result.Outcome);
        Assert.Empty(result.Devices);
        Assert.Contains("continuation", result.RefusalReason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Named failure reasons, each distinct from the others ---------------------------------

    [Fact]
    public async Task Forbidden_NamesBothLiveCauses()
    {
        // A 403 here has exactly two live causes and an operator cannot tell them apart from
        // "failed": the permission was never consented, or the token was issued for the wrong
        // audience. Cheapest broken implementation that still passes: a message naming only the
        // permission - defeated by asserting the audience hostname too.
        var (service, handler) = CreateService();
        handler.Responses.Add(() => StubHandler.Json(HttpStatusCode.Forbidden, """
            {"error":{"code":"Forbidden","message":"Insufficient privileges"}}
            """));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.RequestFailed, result.Outcome);
        Assert.Empty(result.Devices);
        Assert.Contains("Machine.Read.All", result.RefusalReason);
        Assert.Contains("api.securitycenter.microsoft.com", result.RefusalReason);
        Assert.Contains("Insufficient privileges", result.RefusalReason);
    }

    [Fact]
    public async Task Forbidden_Throttled_Timeout_And5xx_AllProduceDifferentReasons()
    {
        // Collapsing any two of these must fail. "The request failed" is not a reason an operator
        // can act on, and these four need four different actions.
        var reasons = new List<string>();

        foreach (var responder in new Func<HttpResponseMessage>[]
        {
            () => StubHandler.Json(HttpStatusCode.Forbidden, ""),
            () => StubHandler.Json(HttpStatusCode.TooManyRequests, ""),
            () => throw new TaskCanceledException("timeout", new TimeoutException()),
            () => StubHandler.Json(HttpStatusCode.ServiceUnavailable, "")
        })
        {
            var (service, handler) = CreateService();
            handler.Responses.Add(responder);
            var result = await service.ListDevicesAsync();

            Assert.Equal(DefenderDeviceListOutcome.RequestFailed, result.Outcome);
            reasons.Add(result.RefusalReason);
        }

        Assert.Equal(4, reasons.Distinct().Count());
        Assert.Contains("timed out", reasons[2]);
        Assert.Contains("throttled", reasons[1]);
    }

    [Fact]
    public async Task ResponseWithNoDeviceCollection_Refuses()
    {
        var (service, handler) = CreateService();
        handler.Responses.Add(() => Ok("""{"unexpected":true}"""));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.RequestFailed, result.Outcome);
        Assert.Empty(result.Devices);
        Assert.Contains("no device collection", result.RefusalReason);
    }


    // ---- T4: the request path that must never be composed -------------------------------------

    [Fact]
    public async Task EveryRequestTargetsTheCollectionAndNeverASingleMachine()
    {
        // Learn's Get-machine-by-id page documents ONLY Machine.ReadWrite.All, so composing
        // /api/machines/{id} would force a write scope onto a read-only module's registration.
        var (service, handler) = CreateService(maxDevices: "100");
        handler.Responses.Add(() => Ok(Page(2, first: 1, nextLink: Host + "/api/machines?$top=5&$skiptoken=abc")));
        handler.Responses.Add(() => Ok(Page(1, first: 3)));

        await service.ListDevicesAsync();

        Assert.Equal(2, handler.RequestUrls.Count);
        Assert.All(handler.RequestUrls, url =>
            Assert.Equal("/api/machines", new Uri(url).AbsolutePath));
    }

    [Fact]
    public void ServiceSourceComposesNoPerMachineRequestPath()
    {
        // The source-level half of T4, so a later change that "just needed one more field" cannot
        // reintroduce the call quietly and pass the request-level test above by never exercising it.
        // Comment lines are stripped first. The file's own remarks say in words that it never calls
        // get-machine-by-id, and a scan that counted that sentence as a violation would force the
        // explanation out of the file to keep the test green - the wrong trade.
        var code = string.Join(
            "\n",
            File.ReadAllLines(Path.Combine(GetRepoDirectory(), "Services", "DefenderEndpointDeviceService.cs"))
                .Where(line =>
                {
                    var trimmed = line.TrimStart();
                    return !trimmed.StartsWith("//") && !trimmed.StartsWith("*") && !trimmed.StartsWith("/*");
                }));

        Assert.DoesNotContain("/api/machines/", code);
        Assert.DoesNotContain("MachinesEndpoint}/", code);
        // And the scan is looking at real code, not an empty string after over-eager stripping.
        Assert.Contains("MachinesEndpoint", code);
    }

    private static string GetRepoDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Services", "DefenderEndpointDeviceService.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Services/DefenderEndpointDeviceService.cs from the test base directory.");
    }

    // ---- T6.1: the OData literal ---------------------------------------------------------------

    /// <summary>The root part of an unfiltered run: no lastSeen clause at all.</summary>
    private static readonly DefenderLastSeenRange WholeAxis = DefenderLastSeenRange.Between(null, null);

    [Fact]
    public void FilterValueWithASingleQuote_HasItDoubledInsideTheLiteral()
    {
        var url = DefenderEndpointDeviceService.BuildMachinesUrl(
            new DefenderDeviceFilters { OnboardingStatus = "O'Brien" }, WholeAxis, 10);

        // Doubled first, then percent-escaped as a VALUE. The delimiting quotes are syntax and stay
        // literal - escaping the whole expression instead is the defect that makes a service answer
        // 200 with an empty result every time.
        Assert.Contains("$filter=onboardingStatus%20eq%20'O%27%27Brien'", url);
    }

    [Fact]
    public void FilterValueWithAmpersandAndHash_IsEscapedValueOnly()
    {
        var url = DefenderEndpointDeviceService.BuildMachinesUrl(
            new DefenderDeviceFilters { OnboardingStatus = "a&b#c" }, WholeAxis, 10);

        Assert.Contains("$filter=onboardingStatus%20eq%20'a%26b%23c'&$top=10", url);
        // The & and # of the VALUE are encoded; the & separating $filter from $top is not.
        Assert.Equal(1, url.Count(c => c == '&'));
    }

    [Fact]
    public void EmptyFilterValue_SendsNoServerSideFilterAtAll()
    {
        var url = DefenderEndpointDeviceService.BuildMachinesUrl(
            new DefenderDeviceFilters { OnboardingStatus = "" }, WholeAxis, 10);

        Assert.Equal("/api/machines?$top=10", url);
    }

    [Fact]
    public void OverlongFilterValue_IsNotTruncatedAndKeepsTheRestOfTheQuery()
    {
        var value = new string('x', 5000);

        var url = DefenderEndpointDeviceService.BuildMachinesUrl(
            new DefenderDeviceFilters { OnboardingStatus = value }, WholeAxis, 10);

        Assert.Contains($"'{value}'", url);
        Assert.EndsWith("&$top=10", url);
    }

    [Fact]
    public void DefaultFilter_UsesTheApiLiteralAndNotThePortalLabel()
    {
        // The owner's phrase "Can be onboarded" is the PORTAL label; the API literal is
        // CanBeOnboarded, and sending the portal spelling matches nothing.
        var url = DefenderEndpointDeviceService.BuildMachinesUrl(new DefenderDeviceFilters(), WholeAxis, 10);

        Assert.Contains("'CanBeOnboarded'", url);
        Assert.DoesNotContain("Can%20be%20onboarded", url);
    }

    // ---- Portal parity: which filters reach the service, and which cannot ----------------------

    [Fact]
    public void EveryServerSideFilterReachesTheQueryAsItsOwnClause()
    {
        // The owner's requirement in one assertion: what the portal filters on, this filters on, and
        // the API does the work. Each clause names the property Learn lists as filterable for this
        // collection; a clause naming anything else is a 400 at best and a silently wrong 200 at
        // worst.
        //
        // Cheapest broken implementation that still passes: emitting the clauses in some other
        // conjunction - there is none, "and" is the only one OData has for this. Emitting them
        // client-side instead fails, because then the URL carries none of them.
        var url = DefenderEndpointDeviceService.BuildMachinesUrl(
            new DefenderDeviceFilters
            {
                OnboardingStatus = "onboarded",
                OsPlatform = "Windows11",
                HealthStatus = "Active",
                RiskScore = "High",
                ExposureLevel = "Medium",
                DeviceNameStartsWith = "srv"
            },
            DefenderLastSeenRange.Between(
                new DateTimeOffset(2026, 2, 3, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 2, 4, 0, 0, 0, TimeSpan.Zero)),
            10);

        var filter = Uri.UnescapeDataString(url["/api/machines?$filter=".Length..^"&$top=10".Length]);

        Assert.Equal(
            "onboardingStatus eq 'onboarded' and osPlatform eq 'Windows11' and healthStatus eq 'Active' "
            + "and riskScore eq 'High' and exposureLevel eq 'Medium' and startswith(computerDnsName,'srv') "
            + "and lastSeen ge 2026-02-03T00:00:00.0000000Z and lastSeen lt 2026-02-04T00:00:00.0000000Z",
            filter);
    }

    [Fact]
    public void TheClientSideFiltersNeverReachTheQuery()
    {
        // The other half, and the one the owner has been bitten by: a filter that looks server-side
        // and is not. Machine tag, machine group, First seen and the Any Windows prefix have no
        // filterable property on this collection, so they must not appear in the URL at all - a
        // guessed clause that the service accepts and ignores is indistinguishable from an empty
        // inventory.
        var url = DefenderEndpointDeviceService.BuildMachinesUrl(
            new DefenderDeviceFilters
            {
                OnboardingStatus = "",
                OsPlatformPrefix = "Windows",
                MachineTag = "kiosk",
                MachineGroup = "Site A",
                FirstSeenFrom = new DateTimeOffset(2026, 2, 3, 0, 0, 0, TimeSpan.Zero),
                FirstSeenTo = new DateTimeOffset(2026, 2, 4, 0, 0, 0, TimeSpan.Zero)
            },
            WholeAxis,
            10);

        Assert.Equal("/api/machines?$top=10", url);
    }

    [Fact]
    public void TheNoLastSeenPartAsksForANullLastSeenAndNothingElseAboutTime()
    {
        var url = DefenderEndpointDeviceService.BuildMachinesUrl(
            new DefenderDeviceFilters { OnboardingStatus = "" }, DefenderLastSeenRange.NoLastSeen(), 10);

        Assert.Equal("/api/machines?$filter=lastSeen%20eq%20null&$top=10", url);
    }

    [Fact]
    public void ADeviceNamePrefixUsesTheOneFunctionLearnDocumentsOnThisCollection()
    {
        // startswith(computerDnsName, ...) is the only function with a worked example for this
        // endpoint. It is a PREFIX, and the page says so beside the box.
        var url = DefenderEndpointDeviceService.BuildMachinesUrl(
            new DefenderDeviceFilters { OnboardingStatus = "", DeviceNameStartsWith = "web-01" }, WholeAxis, 10);

        Assert.Equal("/api/machines?$filter=startswith(computerDnsName,'web-01')&$top=10", url);
    }

    // ---- $top: one more than the ceiling, clamped to the API maximum ---------------------------

    [Fact]
    public async Task TopIsOneMoreThanTheCeiling()
    {
        var (service, handler) = CreateService(maxDevices: "5");
        handler.Responses.Add(() => Ok(Page(1)));

        await service.ListDevicesAsync();

        Assert.Contains("$top=6", Assert.Single(handler.RequestUrls));
    }

    [Fact]
    public async Task TopIsClampedToTheDocumentedApiMaximum()
    {
        var (service, handler) = CreateService(maxDevices: "20000");
        handler.Responses.Add(() => Ok(Page(1)));

        await service.ListDevicesAsync();

        Assert.Contains("$top=10000", Assert.Single(handler.RequestUrls));
    }

    [Theory]
    [InlineData(null, DefenderEndpointDeviceService.DefaultMaxDevices)]
    [InlineData("", DefenderEndpointDeviceService.DefaultMaxDevices)]
    [InlineData("0", DefenderEndpointDeviceService.DefaultMaxDevices)]
    [InlineData("-5", DefenderEndpointDeviceService.DefaultMaxDevices)]
    [InlineData("not a number", DefenderEndpointDeviceService.DefaultMaxDevices)]
    [InlineData("50", 50)]
    public void ClampMaxDevices_FallsBackToTheDescriptorDefault(string? configured, int expected)
    {
        Assert.Equal(expected, DefenderEndpointDeviceService.ClampMaxDevices(configured));
    }

    [Fact]
    public void TheDefaultCeilingIsLargerThanOneRequestCouldEverReturn()
    {
        // The shipped default was 20000 against an API that returns at most 10000 a request, which
        // read as "a big number" and was in fact below the size of a real tenant: the first live
        // load refused on the ceiling. Cheapest broken implementation that still passes every other
        // test here: leaving it at 20000, which is what this exists to stop.
        Assert.True(DefenderEndpointDeviceService.DefaultMaxDevices >= 100000);
        Assert.True(DefenderEndpointDeviceService.DefaultMaxDevices > DefenderEndpointDeviceService.MaxTop);
    }

    [Fact]
    public void TheRequestBudgetLeavesRoomForAPartitionedRunAndStaysUnderTheHourlyLimit()
    {
        // 100 was chosen for a design that issued ONE request. Both bounds are DERIVED, and
        // neither may evaluate to the constant itself: a test whose floor and ceiling are both the
        // current value fails every intentional change and proves nothing about the design.
        //
        // Floor: a balanced division of a ceiling-sized tenant costs 2 * leaves - 1 requests, plus
        // one for the no-lastSeen part. Ten times that leaves room for a badly unbalanced one.
        var leaves = DefenderEndpointDeviceService.DefaultMaxDevices / DefenderEndpointDeviceService.MaxTop;
        var balanced = (2 * leaves) - 1 + 1;
        Assert.True(DefenderEndpointDeviceService.MaxRequestsPerRun >= 10 * balanced,
            $"a budget of {DefenderEndpointDeviceService.MaxRequestsPerRun} leaves no room above the "
            + $"{balanced} requests a balanced division of "
            + $"{DefenderEndpointDeviceService.DefaultMaxDevices} devices costs");

        // Ceiling: one run must not eat the tenant's hour. The endpoint documents 1,500 calls an
        // hour, so a fifth of it leaves four more operators able to run in the same hour.
        Assert.True(DefenderEndpointDeviceService.MaxRequestsPerRun <= 1500 / 5);
    }

    [Fact]
    public void ClampMaxDevices_LeavesHeadroomSoTheDecisiveTopCannotOverflow()
    {
        var ceiling = DefenderEndpointDeviceService.ClampMaxDevices(int.MaxValue.ToString());

        Assert.True(ceiling + 1 > 0);
    }

    [Theory]
    [InlineData("https://h/api/machines?$top=25&$skiptoken=a", 25)]
    [InlineData("https://h/api/machines?$skiptoken=a&$top=7", 7)]
    [InlineData("https://h/api/machines?$skiptoken=a", null)]
    [InlineData("https://h/api/machines", null)]
    public void ExtractTop_ReadsTheContinuationRequestsOwnPageSize(string url, int? expected)
    {
        Assert.Equal(expected, DefenderEndpointDeviceService.ExtractTop(url));
    }


    // ---- T6.2: casing, which Microsoft's own documentation is inconsistent about ---------------

    [Fact]
    public async Task DeviceFieldsParseWhateverCasingTheServiceEmits()
    {
        // The machine resource table spells it onboardingstatus, the filterable-properties list
        // spells it onboardingStatus, and the OData samples page capitalises ComputerDnsName and
        // OsPlatform. Which one the service emits is R1(b)'s question; reading insensitively means
        // the answer cannot break the parser either way.
        var (service, handler) = CreateService(maxDevices: "50");
        handler.Responses.Add(() => Ok("""
            {"value":[{"Id":"m1","ComputerDnsName":"host1.corp.example","OsPlatform":"Windows11",
            "OnboardingStatus":"CanBeOnboarded","LastIpAddress":"10.0.0.1","IsAadJoined":true,
            "MachineTags":["tag-a","tag-b"],
            "IpAddresses":[{"IpAddress":"10.0.0.1","MacAddress":"00-11-22-33-44-55","OperationalStatus":"Up"}]}]}
            """));

        var result = await service.ListDevicesAsync();

        var device = Assert.Single(result.Devices);
        Assert.Equal("m1", device.Id);
        Assert.Equal("host1.corp.example", device.ComputerDnsName);
        Assert.Equal("Windows11", device.OsPlatform);
        Assert.Equal("CanBeOnboarded", device.OnboardingStatus);
        Assert.True(device.IsAadJoined);
        Assert.Equal(["tag-a", "tag-b"], device.MachineTags);
        Assert.Equal("00-11-22-33-44-55", Assert.Single(device.IpAddresses).MacAddress);
    }

    [Fact]
    public async Task MissingOptionalCollections_ParseToEmptyRatherThanThrowing()
    {
        // Whether list responses carry ipAddresses at all is an open question settled by R1(e).
        var (service, handler) = CreateService(maxDevices: "50");
        handler.Responses.Add(() => Ok("""{"value":[{"id":"m1","osPlatform":"Windows10"}]}"""));

        var result = await service.ListDevicesAsync();

        var device = Assert.Single(result.Devices);
        Assert.Empty(device.IpAddresses);
        Assert.Empty(device.MachineTags);
        Assert.Null(device.FirstSeen);
    }

    [Fact]
    public async Task ARowWithNoDeviceId_IsStillCountedRatherThanSilentlyDropped()
    {
        var (service, handler) = CreateService(maxDevices: "50");
        handler.Responses.Add(() => Ok("""
            {"value":[{"id":"m1","osPlatform":"Windows10"},{"osPlatform":"Windows10"}]}
            """));

        var result = await service.ListDevicesAsync();

        Assert.Equal(2, result.DistinctCount);
        Assert.Equal(2, result.Devices.Count);
    }

    // ---- The client-side filters, applied AFTER the partition and never during it ---------------

    [Fact]
    public async Task TheAnyWindowsChoice_MatchesEveryWindowsPlatformValueByPrefix()
    {
        // osPlatform carries Windows10, Windows11 and the server variants as separate values, so
        // `osPlatform eq 'Windows'` matches nothing and startswith on osPlatform is undocumented.
        // Cheapest broken implementation that still passes: an equality test against "Windows10" -
        // defeated by the other two Windows rows.
        var (service, handler) = CreateService(maxDevices: "50");
        handler.Responses.Add(() => Ok("""
            {"value":[
                {"id":"m1","osPlatform":"Windows10"},
                {"id":"m2","osPlatform":"Windows11"},
                {"id":"m3","osPlatform":"WindowsServer2019"},
                {"id":"m4","osPlatform":"macOS"},
                {"id":"m5","osPlatform":"Linux"}
            ]}
            """));

        var result = await service.ListDevicesAsync(new DefenderDeviceFilters
        {
            OsPlatformPrefix = DefenderDeviceFilters.WindowsPlatformPrefix
        });

        Assert.Equal(["m1", "m2", "m3"], result.Devices.Select(device => device.Id).Order().ToArray());
        Assert.Equal(5, result.DistinctCount);
    }

    [Fact]
    public async Task NoPlatformChoice_ReturnsTheNonWindowsDevicesToo()
    {
        var (service, handler) = CreateService(maxDevices: "50");
        handler.Responses.Add(() => Ok("""
            {"value":[{"id":"m1","osPlatform":"Windows10"},{"id":"m2","osPlatform":"Linux"}]}
            """));

        var result = await service.ListDevicesAsync();

        Assert.Equal(2, result.Devices.Count);
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("tag")]
    [InlineData("group")]
    [InlineData("firstSeen")]
    public async Task EveryClientSideFilterIsAppliedAfterThePartitionAndNeverDuringIt(string filter)
    {
        // Filtering while accumulating would make a full response of non-matching rows look SHORT
        // and turn the ambiguous case into a false proof of exhaustion.
        //
        // Every one of the four, not only the platform prefix. An implementation that gets the
        // platform prefix right and applies the tag, the group or a First seen bound inside the
        // page reader has exactly the same defect, and a single-filter test clears it.
        //
        // The shape: a cursor whose own $top is 2, then a response OF 2 carrying no cursor. That is
        // ambiguous, the rows carry no lastSeen so there is nothing to divide on, and the run
        // refuses. Filter during accumulation and the second response reads as 0 of 2 - short,
        // therefore proved - and the run reports Complete with zero devices instead.
        const string NextLink = Host + "/api/machines?$top=2&$skiptoken=abc";

        var (extra, filters) = filter switch
        {
            "platform" => ("", new DefenderDeviceFilters
            {
                OsPlatformPrefix = DefenderDeviceFilters.WindowsPlatformPrefix
            }),
            "tag" => (",\"machineTags\":[\"Lab\"]", new DefenderDeviceFilters { MachineTag = "kiosk" }),
            "group" => (",\"rbacGroupName\":\"Site B\"", new DefenderDeviceFilters { MachineGroup = "Site A" }),
            "firstSeen" => (",\"firstSeen\":\"2020-01-01T00:00:00Z\"", new DefenderDeviceFilters
            {
                FirstSeenFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(filter))
        };

        // Only the platform case needs a non-matching osPlatform on the rows. The other three are
        // excluded by the property in extra, and leaving those rows as Windows proves the exclusion
        // came from the filter under test and not from the platform prefix.
        var osPlatform = filter == "platform" ? "Linux" : "Windows10";

        var (service, handler) = CreateService(maxDevices: "100");
        handler.Responses.Add(() => Ok(Page(2, first: 1, nextLink: NextLink, osPlatform: osPlatform, extra: extra)));
        handler.Responses.Add(() => Ok(Page(2, first: 3, osPlatform: osPlatform, extra: extra)));

        var result = await service.ListDevicesAsync(filters);

        Assert.Equal(DefenderDeviceListOutcome.IncompletePaging, result.Outcome);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void MachineTagAndMachineGroupMatchAnyPartOfTheValue()
    {
        // Both are substring matches, case-insensitive, and the page labels them that way. An
        // operator does not know a machine group's numeric rbacGroupId - which is the only group
        // property this collection can filter on - and does not type a tag exactly.
        var device = new DefenderDevice
        {
            Id = "m1",
            MachineTags = ["Kiosk", "Floor 3"],
            MachineGroup = "Contoso Site A"
        };

        Assert.True(DefenderEndpointDeviceService.MatchesClientSideFilters(
            device, new DefenderDeviceFilters { MachineTag = "kio" }));
        Assert.True(DefenderEndpointDeviceService.MatchesClientSideFilters(
            device, new DefenderDeviceFilters { MachineGroup = "site a" }));

        Assert.False(DefenderEndpointDeviceService.MatchesClientSideFilters(
            device, new DefenderDeviceFilters { MachineTag = "lab" }));
        Assert.False(DefenderEndpointDeviceService.MatchesClientSideFilters(
            device, new DefenderDeviceFilters { MachineGroup = "Site B" }));
    }

    [Fact]
    public void AFirstSeenWindowIsInclusiveBelowAndExclusiveAbove()
    {
        var from = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero);
        var window = new DefenderDeviceFilters { FirstSeenFrom = from, FirstSeenTo = to };

        Assert.True(DefenderEndpointDeviceService.MatchesClientSideFilters(
            new DefenderDevice { FirstSeen = from }, window));
        Assert.True(DefenderEndpointDeviceService.MatchesClientSideFilters(
            new DefenderDevice { FirstSeen = to.AddTicks(-1) }, window));

        Assert.False(DefenderEndpointDeviceService.MatchesClientSideFilters(
            new DefenderDevice { FirstSeen = from.AddTicks(-1) }, window));
        Assert.False(DefenderEndpointDeviceService.MatchesClientSideFilters(
            new DefenderDevice { FirstSeen = to }, window));
    }

    [Fact]
    public void ADeviceWithNoFirstSeenFailsAFirstSeenBoundRatherThanPassingIt()
    {
        // "We do not know when" is not a yes to "first seen in this window". Cheapest broken
        // implementation that still passes the window test above: treating a missing value as
        // unconstrained, which puts devices in the answer that were never shown to satisfy the
        // question.
        var device = new DefenderDevice { Id = "m1" };

        Assert.True(DefenderEndpointDeviceService.MatchesClientSideFilters(device, new DefenderDeviceFilters()));
        Assert.False(DefenderEndpointDeviceService.MatchesClientSideFilters(device, new DefenderDeviceFilters
        {
            FirstSeenFrom = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero)
        }));
        Assert.False(DefenderEndpointDeviceService.MatchesClientSideFilters(device, new DefenderDeviceFilters
        {
            FirstSeenTo = new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero)
        }));
    }

    // ---- The model's own invariants -------------------------------------------------------------

    [Fact]
    public void DnsDomainIsDerivedFromTheFqdnAndIsEmptyWhenThereIsNoDot()
    {
        Assert.Equal("corp.example", new DefenderDevice { ComputerDnsName = "host1.corp.example" }.DnsDomain);
        Assert.Equal("", new DefenderDevice { ComputerDnsName = "host1" }.DnsDomain);
        Assert.Equal("", new DefenderDevice { ComputerDnsName = "" }.DnsDomain);
        Assert.Equal("", new DefenderDevice { ComputerDnsName = "host1." }.DnsDomain);
    }

    [Fact]
    public void ARefusalCannotBeConstructedWithTheCompleteOutcome()
    {
        Assert.Throws<ArgumentException>(() =>
            DefenderDeviceListResult.Refusal(DefenderDeviceListOutcome.Complete, "reason", 0, 1, 0, 10));
    }

    [Fact]
    public void OnlyAnOperatorSuppliedWindowCountsAsALastSeenBound()
    {
        // The one predicate that decides whether the devices with no Last seen value belong in the
        // answer. Getting it backwards either strands them or answers a question nobody asked.
        Assert.False(new DefenderDeviceFilters().HasLastSeenBound);
        Assert.True(new DefenderDeviceFilters { LastSeenFrom = DateTimeOffset.UnixEpoch }.HasLastSeenBound);
        Assert.True(new DefenderDeviceFilters { LastSeenTo = DateTimeOffset.UnixEpoch }.HasLastSeenBound);
    }


    [Fact]
    public async Task NoHuntingCredentials_IsNotAttemptedAndNamesThatRatherThanBlanking()
    {
        // The five enrichment columns must never read as merely blank, whatever stopped them.
        // Cheapest broken implementation that still passes an "it is NotAttempted" assertion alone:
        // returning NotAttempted with an EMPTY reason, which is exactly the blank-cell failure this
        // slice exists to prevent - defeated by pinning the exact constant.
        var (service, handler) = CreateService(maxDevices: "50");
        handler.Responses.Add(() => Ok(Page(1)));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDiscoveryEnrichmentState.NotAttempted, result.DiscoveryEnrichment);
        Assert.Equal(DefenderDiscoveryReasons.CredentialsUnavailable, result.DiscoveryEnrichmentReason);
    }

    // ================ S4: discovery-sources enrichment ==========================================
    //
    // The whole slice rests on one distinction: a blank enrichment cell on a SUCCEEDED run means
    // that device genuinely has no value, and a blank on any other state means no device has one
    // and half the report did not execute. Every test below protects one side of that line, or one
    // of the reasons an operator needs in order to act.

    /// <summary>
    /// The two-client shape T1 describes: ONE app registration and one secret, two instances of the
    /// same module-local client, two hosts, two audiences. Each gets its own stub so a hunting
    /// response can never satisfy an inventory request by accident.
    /// </summary>
    private static (DefenderEndpointDeviceService Service, StubHandler Devices, StubHandler Hunting) CreateEnrichedService(
        string? maxDevices = "50",
        string? includeDiscoverySources = null)
    {
        var deviceHandler = new StubHandler();
        var huntingHandler = new StubHandler();

        var inventory = new DefenderApiClient(
            "tenant", "client", "secret",
            DefenderApiClient.DefenderBaseUrl,
            DefenderApiClient.DefenderTokenScope,
            new HttpClient(deviceHandler));

        var hunting = new DefenderApiClient(
            "tenant", "client", "secret",
            DefenderApiClient.GraphBaseUrl,
            DefenderApiClient.GraphTokenScope,
            new HttpClient(huntingHandler));

        var service = new DefenderEndpointDeviceService(
            () => Task.FromResult<DefenderApiClient?>(inventory),
            maxDevices,
            () => Task.FromResult<DefenderApiClient?>(hunting),
            includeDiscoverySources);

        return (service, deviceHandler, huntingHandler);
    }

    /// <summary>A Graph runHuntingQuery response body wrapping the given row literals.</summary>
    private static string HuntingResults(params string[] rows) =>
        "{\"schema\":[{\"Name\":\"DeviceId\",\"Type\":\"String\"}],\"results\":["
        + string.Join(",", rows) + "]}";

    private static string HuntingRow(
        string deviceId,
        string sources = "MDE",
        string type = "Workstation",
        string category = "Endpoint",
        string vendor = "Contoso",
        string model = "X1") =>
        $"{{\"DeviceId\":\"{deviceId}\",\"DiscoverySources\":\"{sources}\",\"DeviceType\":\"{type}\","
        + $"\"DeviceCategory\":\"{category}\",\"Vendor\":\"{vendor}\",\"Model\":\"{model}\"}}";

    /// <summary>Lists one device and answers the hunting call with the given responder.</summary>
    private static async Task<DefenderDeviceListResult> RunWithHuntingResponse(Func<HttpResponseMessage> responder)
    {
        var (service, devices, hunting) = CreateEnrichedService();
        devices.Responses.Add(() => Ok(Page(1)));
        hunting.Responses.Add(responder);
        return await service.ListDevicesAsync();
    }

    // ---- the merge ------------------------------------------------------------------------------

    [Fact]
    public async Task SuccessfulHuntingQuery_MergesAllFiveColumnsOntoTheMatchingDevices()
    {
        // Cheapest broken implementation that still passes: none of the usual ones. Hard-coding a
        // value fails because ten differently-named values are asserted across two devices. Never
        // calling the hunting client fails because the columns would be empty and the state would
        // not be Succeeded. Merging by list POSITION rather than by DeviceId fails because the
        // hunting rows are deliberately supplied in the opposite order to the device rows.
        var (service, devices, hunting) = CreateEnrichedService();
        devices.Responses.Add(() => Ok(Page(2)));
        hunting.Responses.Add(() => Ok(HuntingResults(
            HuntingRow("m2", "Defender for IoT", "Server", "Infrastructure", "Fabrikam", "R720"),
            HuntingRow("m1", "MDE", "Workstation", "Endpoint", "Contoso", "X1"))));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDiscoveryEnrichmentState.Succeeded, result.DiscoveryEnrichment);
        Assert.Equal("", result.DiscoveryEnrichmentReason);

        var first = result.Devices.Single(device => device.Id == "m1");
        Assert.Equal("MDE", first.DiscoverySources);
        Assert.Equal("Workstation", first.DeviceType);
        Assert.Equal("Endpoint", first.DeviceCategory);
        Assert.Equal("Contoso", first.Vendor);
        Assert.Equal("X1", first.Model);

        var second = result.Devices.Single(device => device.Id == "m2");
        Assert.Equal("Defender for IoT", second.DiscoverySources);
        Assert.Equal("Server", second.DeviceType);
        Assert.Equal("Infrastructure", second.DeviceCategory);
        Assert.Equal("Fabrikam", second.Vendor);
        Assert.Equal("R720", second.Model);
    }

    [Fact]
    public async Task ADeviceWithNoHuntingRow_KeepsItsRowAndOnlyItsOwnColumnsAreEmpty()
    {
        // The run SUCCEEDED, so a blank on this device means it genuinely has no DeviceInfo record,
        // and it must render as a dash rather than "(unavailable)", which would claim the query
        // failed. Cheapest broken implementation that still passes: dropping devices the hunting
        // result did not mention - defeated by asserting both devices are still listed.
        var (service, devices, hunting) = CreateEnrichedService();
        devices.Responses.Add(() => Ok(Page(2)));
        hunting.Responses.Add(() => Ok(HuntingResults(HuntingRow("m1"))));

        var result = await service.ListDevicesAsync();

        Assert.Equal(2, result.Devices.Count);
        Assert.Equal(DefenderDiscoveryEnrichmentState.Succeeded, result.DiscoveryEnrichment);
        Assert.Equal("MDE", result.Devices.Single(device => device.Id == "m1").DiscoverySources);

        var unmatched = result.Devices.Single(device => device.Id == "m2");
        Assert.Equal("", unmatched.DiscoverySources);
        Assert.Equal("", unmatched.Vendor);
        Assert.Equal(
            "-",
            DefenderEndpointDeviceService.DescribeEnrichmentCell(result.DiscoveryEnrichment, unmatched.DiscoverySources));
    }

    [Fact]
    public async Task AHuntingRowForADeviceTheInventoryNeverListed_IsDroppedAndNeverInjected()
    {
        // DeviceInfo answers for the whole tenant over 30 days, so it returns rows for devices the
        // machines API did not list - filtered out by the onboarding-status filter, outside this
        // registration's scope, or simply gone. Inventing a device row out of hunting data would
        // put a machine on an inventory report that the inventory API never returned. Cheapest
        // broken implementation that still passes the merge test above: iterating the hunting rows
        // and upserting into the device list - defeated by the count and by the explicit id check.
        var (service, devices, hunting) = CreateEnrichedService();
        devices.Responses.Add(() => Ok(Page(1)));
        hunting.Responses.Add(() => Ok(HuntingResults(
            HuntingRow("m1"),
            HuntingRow("ghost-42", "Defender for IoT", "Printer", "Peripheral", "Fabrikam", "P1"))));

        var result = await service.ListDevicesAsync();

        var only = Assert.Single(result.Devices);
        Assert.Equal("m1", only.Id);
        Assert.DoesNotContain(result.Devices, device => device.Id == "ghost-42");
        Assert.Equal(1, result.DistinctCount);
    }

    [Fact]
    public async Task DiscoverySourcesArrivingAsAJsonArray_IsJoinedRatherThanBlanked()
    {
        // DiscoverySources is multi-valued and the hunting API is not consistent about whether such
        // a column serialises as a JSON array or as a string holding one. Reading only the string
        // shape would silently blank the one column the owner actually asked for. Cheapest broken
        // implementation that still passes an "it is not empty" assertion: emitting the raw JSON
        // text - defeated by asserting the joined form and the absence of brackets and quotes.
        var (service, devices, hunting) = CreateEnrichedService();
        devices.Responses.Add(() => Ok(Page(1)));
        hunting.Responses.Add(() => Ok(
            "{\"results\":[{\"DeviceId\":\"m1\","
            + "\"DiscoverySources\":[\"MDE\",\"Microsoft Defender for IoT\"],"
            + "\"DeviceType\":\"Workstation\",\"DeviceCategory\":null,"
            + "\"Vendor\":\"Contoso\",\"Model\":\"X1\"}]}"));

        var result = await service.ListDevicesAsync();

        var device = Assert.Single(result.Devices);
        Assert.Equal("MDE; Microsoft Defender for IoT", device.DiscoverySources);
        Assert.DoesNotContain("[", device.DiscoverySources);
        Assert.DoesNotContain("\"", device.DiscoverySources);
        // A null column is a blank on a succeeded run, not a parse failure.
        Assert.Equal("", device.DeviceCategory);
        Assert.Equal(DefenderDiscoveryEnrichmentState.Succeeded, result.DiscoveryEnrichment);
    }

    // ---- the call itself: T1's shape and T7's query text ----------------------------------------

    [Fact]
    public async Task TheHuntingCallIsOnePostToRunHuntingQueryOnTheGraphHostCarryingTheConstantKql()
    {
        // Pins T1's shape off the WIRE rather than off the source. Cheapest broken implementation
        // that still passes the merge test above: any request at all that happens to return the
        // right JSON - defeated here by the host, the path, the method and the body. And one query
        // per refresh, never one per device: the single-request assertion is the rate-limit promise.
        //
        // The BODY assertions are parsed, not substring-matched, and that is the point of this test
        // rather than a tidiness preference. ThreatHunting.Read.All is scoped to the whole hunting
        // schema, so the permission cannot narrow this module to DeviceInfo and the request body is
        // the only boundary there is. Cheapest broken implementation that a "the body contains the
        // constant" assertion would still pass: posting broader KQL as the real Query and parking
        // the safe constant in a second property, or appending a union to it - the first is defeated
        // by the exact equality on the root Query value, the second by the property sweep below.
        var (service, devices, hunting) = CreateEnrichedService();
        devices.Responses.Add(() => Ok(Page(3)));
        hunting.Responses.Add(() => Ok(HuntingResults(HuntingRow("m1"), HuntingRow("m2"), HuntingRow("m3"))));

        await service.ListDevicesAsync();

        Assert.Equal("https://graph.microsoft.com/v1.0/security/runHuntingQuery", Assert.Single(hunting.RequestUrls));
        Assert.Equal("POST", Assert.Single(hunting.RequestMethods));

        var body = Assert.Single(hunting.RequestBodies);
        using var posted = JsonDocument.Parse(body);
        var root = posted.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(DefenderEndpointDeviceService.HuntingQuery, root.GetProperty("Query").GetString());

        // No escape hatch. The named threat first, because its failure message says what happened:
        // a SECOND query-bearing property, with the safe constant parked in the one this test reads.
        Assert.DoesNotContain(
            root.EnumerateObject(),
            other => !string.Equals(other.Name, "Query", StringComparison.Ordinal)
                && other.Name.Contains("query", StringComparison.OrdinalIgnoreCase));

        // Then the catch-all, which is strictly stronger and is not redundant: runHuntingQuery also
        // accepts Timespan, so "no second property at all" forbids widening the request by any
        // sibling field, not only by one with "query" in its name.
        Assert.Equal("Query", Assert.Single(root.EnumerateObject().ToList()).Name);
    }

    [Fact]
    public void TheHuntingQueryNamesDeviceInfoAndNoOtherHuntingTable()
    {
        // ThreatHunting.Read.All is scoped to the WHOLE hunting schema - mail, identity, process
        // events, everything - so the permission cannot enforce the narrowing and the query text is
        // the only thing that does. Cheapest broken implementation that still passes an "it contains
        // DeviceInfo" assertion on its own: appending a union or a join over another table -
        // defeated by the operator checks and by the table sweep.
        var query = DefenderEndpointDeviceService.HuntingQuery;

        Assert.StartsWith("DeviceInfo", query);
        Assert.DoesNotContain("union", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("join", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("externaldata", query, StringComparison.OrdinalIgnoreCase);

        foreach (var table in new[]
        {
            "DeviceNetworkInfo", "DeviceProcessEvents", "DeviceNetworkEvents", "DeviceFileEvents",
            "DeviceRegistryEvents", "DeviceLogonEvents", "DeviceImageLoadEvents", "DeviceEvents",
            "DeviceFileCertificateInfo", "DeviceTvmSoftwareInventory", "DeviceTvmSoftwareVulnerabilities",
            "AlertInfo", "AlertEvidence", "IdentityInfo", "IdentityLogonEvents", "IdentityQueryEvents",
            "IdentityDirectoryEvents", "EmailEvents", "EmailAttachmentInfo", "EmailUrlInfo",
            "EmailPostDeliveryEvents", "UrlClickEvents", "CloudAppEvents", "BehaviorInfo",
            "BehaviorEntities", "ExposureGraphNodes", "ExposureGraphEdges"
        })
        {
            Assert.DoesNotContain(table, query, StringComparison.Ordinal);
        }

        // And the sweep is looking at a real query rather than passing on an empty string.
        Assert.Contains("| project DeviceId, DiscoverySources, DeviceType, DeviceCategory, Vendor, Model", query);
        Assert.Contains("isempty(MergedToDeviceId)", query);
        Assert.Contains("arg_max(Timestamp, *) by DeviceId", query);
    }

    // ---- the named failures, each one a different operator action -------------------------------

    [Fact]
    public async Task HuntingForbidden_NamesTheUnconsentedThreatHuntingPermissionAndNothingElse()
    {
        // A 403 here is not the device list's 403 and does not have the device list's causes. The
        // operator action is a consent grant by a Privileged Role Administrator or a Global
        // Administrator. Cheapest broken implementation that still passes an "it says Forbidden"
        // assertion: reusing the device-list 403 text, which names Machine.Read.All and a token
        // audience - defeated by the permission name and by the two DoesNotContain assertions.
        var result = await RunWithHuntingResponse(() => StubHandler.Json(
            HttpStatusCode.Forbidden,
            "{\"error\":{\"code\":\"Forbidden\",\"message\":\"Insufficient privileges\"}}"));

        // The device list is untouched. A failed enrichment must never take the page down.
        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Single(result.Devices);

        Assert.Equal(DefenderDiscoveryEnrichmentState.Failed, result.DiscoveryEnrichment);
        Assert.Contains("ThreatHunting.Read.All", result.DiscoveryEnrichmentReason);
        Assert.Contains("admin consent", result.DiscoveryEnrichmentReason);
        Assert.Contains("Insufficient privileges", result.DiscoveryEnrichmentReason);
        Assert.DoesNotContain("Machine.Read.All", result.DiscoveryEnrichmentReason);
        Assert.DoesNotContain("quota", result.DiscoveryEnrichmentReason);

        Assert.Equal(
            DefenderEndpointDeviceService.EnrichmentUnavailable,
            DefenderEndpointDeviceService.DescribeEnrichmentCell(result.DiscoveryEnrichment, ""));
    }

    [Fact]
    public async Task HuntingThrottled_NamesTheQuotaAndNotThePermission()
    {
        // 429 means nothing is misconfigured and there is nothing to grant - the action is to wait.
        // Cheapest broken implementation that still passes: one shared "the query failed" string -
        // defeated here and by EveryEnrichmentReasonIsADifferentSentence together.
        var result = await RunWithHuntingResponse(() => StubHandler.Json(HttpStatusCode.TooManyRequests, ""));

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Single(result.Devices);
        Assert.Equal(DefenderDiscoveryEnrichmentState.Failed, result.DiscoveryEnrichment);
        Assert.Contains("quota", result.DiscoveryEnrichmentReason);
        Assert.Contains("429", result.DiscoveryEnrichmentReason);
        Assert.DoesNotContain("ThreatHunting.Read.All", result.DiscoveryEnrichmentReason);
        Assert.DoesNotContain("timed out", result.DiscoveryEnrichmentReason);
    }

    [Fact]
    public async Task HuntingTimeout_IsNamedATimeoutAndNotAQuotaOrAPermission()
    {
        // A client-side timeout arrives as TaskCanceledException, not as a status code, so without
        // the client's explicit mapping it would escape as an unhandled exception and take the whole
        // page down instead of greying five columns. Cheapest broken implementation that still
        // passes: letting it fall into the generic branch - defeated by the exact-constant
        // assertion and by the distinctness test.
        var result = await RunWithHuntingResponse(() => throw new TaskCanceledException("timeout", new TimeoutException()));

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Single(result.Devices);
        Assert.Equal(DefenderDiscoveryEnrichmentState.Failed, result.DiscoveryEnrichment);
        Assert.Equal(DefenderDiscoveryReasons.TimedOut, result.DiscoveryEnrichmentReason);
        Assert.Contains("timed out", result.DiscoveryEnrichmentReason);
        Assert.DoesNotContain("quota", result.DiscoveryEnrichmentReason);
        Assert.DoesNotContain("ThreatHunting.Read.All", result.DiscoveryEnrichmentReason);
    }

    // ---- the throws: a hunting-side exception must not take a COMPLETED list down ----------------
    //
    // Enrichment runs only after the inventory has proved itself complete, so every failure here is
    // by construction a failure of the second half of a run whose first half already succeeded. The
    // page's catch turns any escaping exception into a page-wide failure and no table, which is the
    // exact outcome the service comment at the call site forbids.

    /// <summary>
    /// Every enrichment column, on the SCREEN and in the EXPORT, reads "(unavailable)". Asserted
    /// through the page's own projector rather than restated here, so the file and the screen cannot
    /// describe the same run differently - and asserted at all because a blank cell is how Known
    /// Failure Class 2 hides a half-executed report.
    /// </summary>
    private static void AssertEveryEnrichmentColumnReadsUnavailable(DefenderDeviceListResult result)
    {
        var unavailable = DefenderEndpointDeviceService.EnrichmentUnavailable;

        Assert.NotEmpty(result.Devices);

        foreach (var device in result.Devices)
        {
            foreach (var value in new[]
            {
                device.DiscoverySources, device.DeviceType, device.DeviceCategory,
                device.Vendor, device.Model
            })
            {
                Assert.Equal(
                    unavailable,
                    DefenderEndpointDeviceService.DescribeEnrichmentCell(result.DiscoveryEnrichment, value));
            }
        }

        var csv = DefenderEndpointDevices.BuildCsv(result.Devices, result.DiscoveryEnrichment);
        using var reader = new StringReader(csv);
        using var parser = new CsvReader(reader, CultureInfo.InvariantCulture);

        Assert.True(parser.Read(), "the CSV carried no header row");

        foreach (var _ in result.Devices)
        {
            Assert.True(parser.Read(), "the CSV carried fewer data rows than the run had devices");

            // Columns 22-26 are the five enrichment columns of the plan's "CSV export" table.
            for (var column = 22; column <= 26; column++)
                Assert.Equal(unavailable, parser.GetField(column));
        }
    }

    /// <summary>
    /// A service whose inventory answers normally and whose hunting client comes from the given
    /// factory - the seam the two throw tests need and CreateEnrichedService does not offer.
    /// </summary>
    private static DefenderEndpointDeviceService ServiceWithHuntingFactory(
        StubHandler deviceHandler,
        Func<Task<DefenderApiClient?>> huntingFactory)
    {
        var inventory = new DefenderApiClient(
            "tenant", "client", "secret",
            DefenderApiClient.DefenderBaseUrl,
            DefenderApiClient.DefenderTokenScope,
            new HttpClient(deviceHandler));

        return new DefenderEndpointDeviceService(
            () => Task.FromResult<DefenderApiClient?>(inventory),
            "50",
            huntingFactory,
            null);
    }

    [Fact]
    public async Task AHuntingClientFactoryThatThrows_LeavesTheCompletedListStandingAndSaysWhyInFixedWords()
    {
        // BuildClientAsync throws three ways - no Secret ID configured, the secret unreadable, the
        // fields incomplete - and the Secret Server read it makes can fail on its own. Cheapest
        // broken implementation that still passes: catching the exception and returning
        // DefenderDiscoveryEnrichment.Failed(ex.Message). Defeated three times over - by the
        // exact-constant assertion, by the two DoesNotContain assertions that are the sanitization
        // half, and by the state assertion, because nothing was sent so this is NotAttempted and not
        // Failed. Removing the catch entirely fails at the await: the exception escapes
        // ListDevicesAsync and no assertion is reached.
        var devices = new StubHandler();
        devices.Responses.Add(() => Ok(Page(1)));

        var service = ServiceWithHuntingFactory(
            devices,
            () => throw new InvalidOperationException(
                "Cannot retrieve the Defender for Endpoint Devices app secret 4242 from Secret Server. "
                + "Verify this is the correct Secret ID and that the Delinea SDK client can view it."));

        var result = await service.ListDevicesAsync();

        // The half that already succeeded is untouched.
        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Equal("m1", Assert.Single(result.Devices).Id);
        Assert.Equal("", result.RefusalReason);

        Assert.Equal(DefenderDiscoveryEnrichmentState.NotAttempted, result.DiscoveryEnrichment);
        Assert.Equal(DefenderDiscoveryReasons.CredentialsUnavailable, result.DiscoveryEnrichmentReason);

        // Nothing out of the exception reaches the operator: not the Secret ID, not the condition.
        Assert.DoesNotContain("4242", result.DiscoveryEnrichmentReason);
        Assert.DoesNotContain("Secret Server", result.DiscoveryEnrichmentReason);

        AssertEveryEnrichmentColumnReadsUnavailable(result);
    }

    [Fact]
    public async Task AHuntingTokenRequestThatFails_LeavesTheCompletedListStandingAndSaysWhyInFixedWords()
    {
        // DefenderApiClient throws InvalidOperationException when the sign-in comes back non-2xx,
        // and its send catches only TaskCanceledException - so this escapes PostWithStatusAsync with
        // no status for DescribeEnrichmentFailure to read. Cheapest broken implementation that still
        // passes: catching it and reusing the Unauthorized reason, which names a 401 that did not
        // happen and sends the operator to check a client secret that may be fine. Defeated by the
        // exact-constant assertion. Removing the catch fails at the await, as above.
        //
        // The sign-in body deliberately carries an error code and a correlation id, because that is
        // precisely what DefenderApiClient refuses to echo and what ex.Message could otherwise leak
        // into a rendered page.
        var (service, devices, hunting) = CreateEnrichedService();
        devices.Responses.Add(() => Ok(Page(1)));
        hunting.TokenResponder = () => StubHandler.Json(
            HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_client\",\"correlation_id\":\"00000000-1111-2222-3333-444444444444\"}");

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Equal("m1", Assert.Single(result.Devices).Id);
        Assert.Equal("", result.RefusalReason);

        // It never got as far as a hunting request, so there is no status - which is what separates
        // this reason from every other failed one.
        Assert.Empty(hunting.RequestUrls);
        Assert.Equal(DefenderDiscoveryEnrichmentState.Failed, result.DiscoveryEnrichment);
        Assert.Equal(DefenderDiscoveryReasons.SendFailed, result.DiscoveryEnrichmentReason);

        Assert.DoesNotContain("invalid_client", result.DiscoveryEnrichmentReason);
        Assert.DoesNotContain("1111-2222-3333", result.DiscoveryEnrichmentReason);
        Assert.DoesNotContain("BadRequest", result.DiscoveryEnrichmentReason);

        AssertEveryEnrichmentColumnReadsUnavailable(result);
    }

    [Fact]
    public async Task AHuntingResponseWithNoResultsCollection_FailsAndDoesNotReadAsAnEmptyResult()
    {
        // A 200 whose body is not a hunting result is a FAILED enrichment, not an enrichment that
        // found nothing. Cheapest broken implementation that still passes the merge test: treating
        // "no results array" as zero rows and reporting Succeeded - defeated by the state assertion,
        // which is what stops every column reading as a legitimate blank.
        var result = await RunWithHuntingResponse(() => Ok("{\"schema\":[],\"unexpected\":true}"));

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Single(result.Devices);
        Assert.Equal(DefenderDiscoveryEnrichmentState.Failed, result.DiscoveryEnrichment);
        Assert.Equal(DefenderDiscoveryReasons.MalformedResponse, result.DiscoveryEnrichmentReason);
    }

    [Fact]
    public async Task AHuntingResponseThatIsNotJsonAtAll_ReadsAsMalformedAndNotAsRejected()
    {
        // The other door into the same state: the client refuses to parse the body, so the status is
        // still 200 and the document is null. "Rejected the query (200 OK)" would be nonsense.
        var result = await RunWithHuntingResponse(() => Ok("<html>not json</html>"));

        Assert.Equal(DefenderDiscoveryEnrichmentState.Failed, result.DiscoveryEnrichment);
        Assert.StartsWith(DefenderDiscoveryReasons.MalformedResponse, result.DiscoveryEnrichmentReason);
        Assert.DoesNotContain("200 OK", result.DiscoveryEnrichmentReason);
        // And the raw body is never echoed back to the operator.
        Assert.DoesNotContain("<html>", result.DiscoveryEnrichmentReason);
    }

    [Fact]
    public async Task IncludeDiscoverySourcesOff_NeverCallsGraphAtAllAndSaysWhy()
    {
        // Cheapest broken implementation that still passes a reason-only assertion: calling Graph
        // anyway and discarding the answer. Defeated by asserting the hunting stub saw ZERO
        // requests - the switch exists so a deployment whose registration does not hold the grant
        // stops firing a request that 403s on every single page load.
        var (service, devices, hunting) = CreateEnrichedService(includeDiscoverySources: "false");
        devices.Responses.Add(() => Ok(Page(1)));
        hunting.Responses.Add(() => Ok(HuntingResults(HuntingRow("m1"))));

        var result = await service.ListDevicesAsync();

        Assert.Empty(hunting.RequestUrls);
        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Equal(DefenderDiscoveryEnrichmentState.NotAttempted, result.DiscoveryEnrichment);
        Assert.Equal(DefenderDiscoveryReasons.SwitchedOff, result.DiscoveryEnrichmentReason);
        Assert.Equal("", Assert.Single(result.Devices).DiscoverySources);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("yes", false)]
    [InlineData("1", false)]
    public void IncludeDiscoverySources_DefaultsOnAndReadsAnUnparseableValueAsOff(string? configured, bool expected)
    {
        // Absent is the descriptor's documented default of ON. A non-blank value that will not parse
        // reads as OFF, because the module config page renders an unparseable Boolean as an
        // UNCHECKED box - defaulting it to ON would make the switch and the screen disagree - and
        // because OFF is the direction that does not call a permission the registration may not
        // hold. Cheapest broken implementation that still passes an on/off pair: bool.TryParse with
        // a TRUE fallback - defeated by the "yes" and "1" rows.
        var service = new DefenderEndpointDeviceService(
            () => Task.FromResult<DefenderApiClient?>(null),
            maxDevicesOverride: null,
            huntingClientFactory: null,
            includeDiscoverySourcesOverride: configured);

        Assert.Equal(expected, service.IncludeDiscoverySources);
    }

    [Fact]
    public async Task AnEmptyDeviceList_DoesNotRunTheHuntingQueryAndSaysSo()
    {
        // Nothing to enrich, so nothing is asked of Graph - and the reason says that rather than
        // implying a failure. The devices here exist but are all filtered out client-side, which is
        // the case a "did the API return any rows?" check would get wrong.
        var (service, devices, hunting) = CreateEnrichedService();
        devices.Responses.Add(() => Ok(Page(2, osPlatform: "Linux")));
        hunting.Responses.Add(() => Ok(HuntingResults(HuntingRow("m1"))));

        var result = await service.ListDevicesAsync(new DefenderDeviceFilters { OsPlatformPrefix = DefenderDeviceFilters.WindowsPlatformPrefix });

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Empty(result.Devices);
        Assert.Empty(hunting.RequestUrls);
        Assert.Equal(DefenderDiscoveryEnrichmentState.NotAttempted, result.DiscoveryEnrichment);
        Assert.Equal(DefenderDiscoveryReasons.NoDevices, result.DiscoveryEnrichmentReason);
    }

    [Fact]
    public async Task ARefusedListing_NeverRunsTheHuntingQuery()
    {
        // A refusal carries zero devices by construction, so there is nothing to enrich, and
        // spending a tenant's hunting quota on a report that will not be shown is pure waste.
        var (service, devices, hunting) = CreateEnrichedService(maxDevices: "1");
        devices.Responses.Add(() => Ok(Page(2)));
        hunting.Responses.Add(() => Ok(HuntingResults(HuntingRow("m1"))));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.CeilingExceeded, result.Outcome);
        Assert.Empty(hunting.RequestUrls);
        Assert.Equal(DefenderDiscoveryReasons.ListRefused, result.DiscoveryEnrichmentReason);
    }

    // ---- the collapse test: no two reasons may say the same thing -------------------------------

    [Fact]
    public async Task EveryEnrichmentReasonIsADifferentSentence()
    {
        // The plan's explicit requirement, and the reason all of these strings exist rather than one
        // "discovery sources unavailable". An operator looking at a column of "(unavailable)" has to
        // be able to tell "nobody consented the permission" from "we hit a quota" from "it timed
        // out" from "you turned it off" - four different problems needing four different actions.
        //
        // Cheapest broken implementation that still passes every other test in this file: one shared
        // failure string for the whole enrichment. This is the test that forbids it, and collapsing
        // any two of the constants below fails it outright.
        var reasons = new List<string>
        {
            // The four that never reach Graph.
            DefenderDiscoveryReasons.SwitchedOff,
            DefenderDiscoveryReasons.CredentialsUnavailable,
            DefenderDiscoveryReasons.NoDevices,
            DefenderDiscoveryReasons.ListRefused,

            // And the one that reached for Graph and never got a status back. It cannot be produced
            // by a responder here, because a responder IS a response; the two throw tests above run
            // it for real and each asserts this exact constant, which is where its "collected by
            // running it" evidence lives.
            DefenderDiscoveryReasons.SendFailed
        };

        // The seven that come back off the wire, collected by actually running them rather than by
        // reading the constants - so a describer that stopped consulting them is caught here too.
        foreach (var responder in new Func<HttpResponseMessage>[]
        {
            () => StubHandler.Json(HttpStatusCode.Forbidden, ""),
            () => StubHandler.Json(HttpStatusCode.TooManyRequests, ""),
            () => throw new TaskCanceledException("timeout", new TimeoutException()),
            () => Ok("{\"schema\":[]}"),
            () => StubHandler.Json(HttpStatusCode.Unauthorized, ""),
            () => StubHandler.Json(HttpStatusCode.ServiceUnavailable, ""),
            () => StubHandler.Json(HttpStatusCode.BadRequest, "")
        })
        {
            var result = await RunWithHuntingResponse(responder);

            Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
            Assert.Single(result.Devices);
            Assert.Equal(DefenderDiscoveryEnrichmentState.Failed, result.DiscoveryEnrichment);
            reasons.Add(result.DiscoveryEnrichmentReason);
        }

        Assert.Equal(12, reasons.Count);
        Assert.Equal(reasons.Count, reasons.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("", reasons);

        // None of them is the DEVICE list's own failure text: that one names a different permission
        // and a different token audience, and pointing an operator at it here would send them to
        // check something that is demonstrably working.
        Assert.DoesNotContain(reasons, reason => reason.Contains("Machine.Read.All", StringComparison.Ordinal));
        Assert.DoesNotContain(reasons, reason => reason.Contains("api.securitycenter", StringComparison.Ordinal));
    }

    // ---- the cell: the state decides, never the value -------------------------------------------

    [Theory]
    [InlineData(DefenderDiscoveryEnrichmentState.Succeeded, "MDE", "MDE")]
    [InlineData(DefenderDiscoveryEnrichmentState.Succeeded, "", "-")]
    [InlineData(DefenderDiscoveryEnrichmentState.Succeeded, null, "-")]
    [InlineData(DefenderDiscoveryEnrichmentState.NotAttempted, "", "(unavailable)")]
    [InlineData(DefenderDiscoveryEnrichmentState.NotAttempted, "MDE", "(unavailable)")]
    [InlineData(DefenderDiscoveryEnrichmentState.Failed, "", "(unavailable)")]
    [InlineData(DefenderDiscoveryEnrichmentState.Failed, "MDE", "(unavailable)")]
    public void AnEnrichmentCellReadsUnavailableUnlessTheQueryActuallyRan(
        DefenderDiscoveryEnrichmentState state,
        string? value,
        string expected)
    {
        // The line the whole slice rests on. Cheapest broken implementation that still passes the
        // two Succeeded rows: rendering the value with a dash fallback - defeated by every
        // non-Succeeded row, because a blank cell is exactly how Known Failure Class 2 hides a
        // half-executed report. The "MDE" rows on the failed states pin that the STATE decides and
        // that a stale value cannot override it.
        Assert.Equal(expected, DefenderEndpointDeviceService.DescribeEnrichmentCell(state, value));
    }

    [Fact]
    public void TheUnavailableMarkerIsNeitherBlankNorTheOrdinaryEmptyDash()
    {
        // A dash is what a genuinely empty value renders as, so the unavailable marker must not be
        // one, or the two cases this slice exists to separate would look identical on screen.
        Assert.Equal("(unavailable)", DefenderEndpointDeviceService.EnrichmentUnavailable);
        Assert.NotEqual("-", DefenderEndpointDeviceService.EnrichmentUnavailable);
        Assert.NotEqual("", DefenderEndpointDeviceService.EnrichmentUnavailable);
    }

    // ---- the enrichment carrier's own invariants -------------------------------------------------

    [Fact]
    public void AFailedOrNotAttemptedEnrichmentCarriesNoRowsAndASucceededOneCarriesNoReason()
    {
        // There must be no value of this type that both claims a failure and offers data to merge,
        // and none that populates columns while telling the operator they are unavailable.
        Assert.Empty(DefenderDiscoveryEnrichment.Failed("boom").Rows);
        Assert.Empty(DefenderDiscoveryEnrichment.NotAttempted("off").Rows);
        Assert.Equal("boom", DefenderDiscoveryEnrichment.Failed("boom").Reason);

        var succeeded = DefenderDiscoveryEnrichment.Succeeded(
            new Dictionary<string, DefenderDeviceDiscovery> { ["m1"] = new() { Vendor = "Contoso" } });

        Assert.Equal("", succeeded.Reason);
        Assert.Equal("Contoso", succeeded.Rows["m1"].Vendor);
    }
}
