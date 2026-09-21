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

        var result = await service.ListDevicesAsync(new DefenderDeviceFilters(WindowsOnly: true));

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
