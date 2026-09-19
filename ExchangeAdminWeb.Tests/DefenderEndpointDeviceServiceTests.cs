using System.Net;
using System.Text;
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"test-token","expires_in":3600}"""));

            var index = RequestUrls.Count;
            RequestUrls.Add(request.RequestUri.OriginalString);

            var responder = Responses.Count == 0
                ? () => Json(HttpStatusCode.OK, """{"value":[]}""")
                : Responses[Math.Min(index, Responses.Count - 1)];

            return Task.FromResult(responder());
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
    private static string Page(int count, int first = 1, string? nextLink = null, string osPlatform = "Windows10")
    {
        var body = new StringBuilder("{\"value\":[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                body.Append(',');
            var id = first + i;
            body.Append(
                $"{{\"id\":\"m{id}\",\"computerDnsName\":\"host{id}.corp.example\",\"osPlatform\":\"{osPlatform}\","
                + "\"onboardingStatus\":\"CanBeOnboarded\"}");
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

    // ---- THE GUARD: a full page with no continuation cursor must refuse -----------------------

    [Fact]
    public async Task FullContinuationPageWithNoCursor_RefusesInsteadOfCompleting()
    {
        // The case that LOOKS like success. A response holding exactly as many rows as the request
        // asked for is byte-identical whether that many exist or the service capped us, so there is
        // no way to tell them apart and this must not guess.
        //
        // Cheapest broken implementation that still passes: none of the obvious ones. "Stop when a
        // page carries no cursor" completes here and fails this test - which is exactly what the
        // design this plan replaced did, where the same response fell into a $skip fallback and
        // rendered a list. "Always refuse" fails the two completion tests above.
        const string NextLink = Host + "/api/machines?$top=2&$skiptoken=abc";
        var (service, handler) = CreateService(maxDevices: "100");
        handler.Responses.Add(() => Ok(Page(2, first: 1, nextLink: NextLink)));
        handler.Responses.Add(() => Ok(Page(2, first: 3)));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.IncompletePaging, result.Outcome);
        Assert.Empty(result.Devices);
        Assert.False(result.CeilingExceeded);
        Assert.Contains("no way to tell", result.RefusalReason);
    }

    [Fact]
    public async Task FullFirstPageAtTheApiMaximumWithNoCursor_Refuses()
    {
        // The production shape of the same guard: MaxDevices at the API's documented per-request
        // maximum, so $top clamps to 10000 and a full page cannot be distinguished from a capped
        // one. The ceiling is NOT breached here (10000 is not more than 10000), so this can only be
        // caught by the ambiguity rule and not by the ceiling rule.
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
        Assert.Contains("maximum of 3", result.RefusalReason);
        Assert.Contains("Raise Maximum Devices", result.RefusalReason);
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
        // An unbounded loop against a paged API is how a read module becomes an outage. The cap is
        // the endpoint's documented 100-calls-per-minute limit, and hitting it REFUSES, because a
        // run that stopped early has not proved exhaustion.
        var (service, handler) = CreateService(maxDevices: "1000000");
        handler.Responses.Add(() => Ok(Page(1, first: 1, nextLink: Host + "/api/machines?$top=1&$skiptoken=abc")));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.IncompletePaging, result.Outcome);
        Assert.Empty(result.Devices);
        Assert.Equal(DefenderEndpointDeviceService.MaxRequestsPerRun, result.PagesFetched);
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
        Assert.Contains("not an empty inventory", result.RefusalReason);
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

    [Fact]
    public void FilterValueWithASingleQuote_HasItDoubledInsideTheLiteral()
    {
        var url = DefenderEndpointDeviceService.BuildMachinesUrl(new DefenderDeviceFilters("O'Brien"), 10);

        // Doubled first, then percent-escaped as a VALUE. The delimiting quotes are syntax and stay
        // literal - escaping the whole expression instead is the defect that makes a service answer
        // 200 with an empty result every time.
        Assert.Contains("$filter=onboardingStatus%20eq%20'O%27%27Brien'", url);
    }

    [Fact]
    public void FilterValueWithAmpersandAndHash_IsEscapedValueOnly()
    {
        var url = DefenderEndpointDeviceService.BuildMachinesUrl(new DefenderDeviceFilters("a&b#c"), 10);

        Assert.Contains("$filter=onboardingStatus%20eq%20'a%26b%23c'&$top=10", url);
        // The & and # of the VALUE are encoded; the & separating $filter from $top is not.
        Assert.Equal(1, url.Count(c => c == '&'));
    }

    [Fact]
    public void EmptyFilterValue_SendsNoServerSideFilterAtAll()
    {
        var url = DefenderEndpointDeviceService.BuildMachinesUrl(new DefenderDeviceFilters(""), 10);

        Assert.Equal("/api/machines?$top=10", url);
    }

    [Fact]
    public void OverlongFilterValue_IsNotTruncatedAndKeepsTheRestOfTheQuery()
    {
        var value = new string('x', 5000);

        var url = DefenderEndpointDeviceService.BuildMachinesUrl(new DefenderDeviceFilters(value), 10);

        Assert.Contains($"'{value}'", url);
        Assert.EndsWith("&$top=10", url);
    }

    [Fact]
    public void DefaultFilter_UsesTheApiLiteralAndNotThePortalLabel()
    {
        // The owner's phrase "Can be onboarded" is the PORTAL label; the API literal is
        // CanBeOnboarded, and sending the portal spelling matches nothing.
        var url = DefenderEndpointDeviceService.BuildMachinesUrl(new DefenderDeviceFilters(), 10);

        Assert.Contains("'CanBeOnboarded'", url);
        Assert.DoesNotContain("Can%20be%20onboarded", url);
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

    // ---- T6.3: the Windows rule, client-side and applied AFTER paging --------------------------

    [Fact]
    public async Task WindowsOnly_MatchesEveryWindowsPlatformValueByPrefix()
    {
        // osPlatform carries Windows10, Windows11 and the server variants as separate values, so
        // `osPlatform eq 'Windows'` matches nothing. Cheapest broken implementation that still
        // passes: an equality test against "Windows10" - defeated by the other two Windows rows.
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

        var result = await service.ListDevicesAsync(new DefenderDeviceFilters(WindowsOnly: true));

        Assert.Equal(["m1", "m2", "m3"], result.Devices.Select(device => device.Id).Order().ToArray());
        Assert.Equal(5, result.DistinctCount);
    }

    [Fact]
    public async Task WindowsOnlyCleared_ReturnsTheNonWindowsDevicesToo()
    {
        var (service, handler) = CreateService(maxDevices: "50");
        handler.Responses.Add(() => Ok("""
            {"value":[{"id":"m1","osPlatform":"Windows10"},{"id":"m2","osPlatform":"Linux"}]}
            """));

        var result = await service.ListDevicesAsync(new DefenderDeviceFilters(WindowsOnly: false));

        Assert.Equal(2, result.Devices.Count);
    }

    [Fact]
    public async Task TheWindowsRuleIsAppliedAfterPagingAndNeverDuringIt()
    {
        // Filtering while accumulating would make a full page of non-Windows rows look SHORT and
        // turn the ambiguous case into a false proof of exhaustion. Cheapest broken implementation
        // that still passes the two tests above: filtering inside the page reader - defeated here,
        // because it would report a complete run with zero devices instead of refusing.
        const string NextLink = Host + "/api/machines?$top=2&$skiptoken=abc";
        var (service, handler) = CreateService(maxDevices: "100");
        handler.Responses.Add(() => Ok(Page(2, first: 1, nextLink: NextLink, osPlatform: "Linux")));
        handler.Responses.Add(() => Ok(Page(2, first: 3, osPlatform: "Linux")));

        var result = await service.ListDevicesAsync(new DefenderDeviceFilters(WindowsOnly: true));

        Assert.Equal(DefenderDeviceListOutcome.IncompletePaging, result.Outcome);
        Assert.Empty(result.Devices);
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
            DefenderDeviceListResult.Refusal(DefenderDeviceListOutcome.Complete, "reason", 0, 1, 10));
    }

    [Fact]
    public async Task S1NeverAttemptsDiscoverySourceEnrichment()
    {
        // The hunting query is S4 and is conditional on the owner granting ThreatHunting.Read.All.
        // The five enrichment columns must never read as merely blank.
        var (service, handler) = CreateService(maxDevices: "50");
        handler.Responses.Add(() => Ok(Page(1)));

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDiscoveryEnrichmentState.NotAttempted, result.DiscoveryEnrichment);
        Assert.NotEqual("", result.DiscoveryEnrichmentReason);
    }
}
