using System.Net;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Read-path tests for IntuneDeviceService (docs/IntuneDeviceManagement-Plan.md S1), driven
/// through the internal Func&lt;Task&lt;GraphTokenClient?&gt;&gt; seam
/// (IntuneDeviceService.cs, mirroring RiskyUsersService.cs:36-39) against a locally declared HTTP
/// stub. GraphTokenClientTests.StubHandler is private sealed to that class and is not reachable
/// here (plan S1, "do not reference GraphTokenClientTests.StubHandler").
/// </summary>
public class IntuneDeviceServiceTests
{
    /// <summary>
    /// Serves a canned token for login.microsoftonline.com and a configurable response for every
    /// Graph call. Records the last non-token request URI so tests can assert on the emitted
    /// query string ($select, $filter, $top).
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> GraphResponse { get; set; } =
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"value":[]}""") };

        /// <summary>
        /// Per-request response, for the three concurrent per-field search requests (plan T2
        /// Revision 2026-09-03): set this when the three must answer differently. GraphResponse
        /// still serves every request while this is null.
        /// </summary>
        public Func<HttpRequestMessage, HttpResponseMessage>? GraphResponseByRequest { get; set; }

        /// <summary>
        /// Every non-token request URI. A search issues three requests concurrently, so
        /// LastGraphRequestUri alone cannot pin what was asked - and which of the three lands last
        /// is not deterministic.
        /// </summary>
        public List<Uri> GraphRequestUris { get; } = [];

        private readonly object _recordLock = new();

        public Uri? LastGraphRequestUri { get; private set; }

        /// <summary>Method and body of the last non-token request, so the write tests can assert the
        /// verb and the exact serialized payload (S4 / AC16) rather than only the URL.</summary>
        public HttpMethod? LastGraphRequestMethod { get; private set; }

        /// <summary>Null when the request carried no content at all - which is what retire must send
        /// (plan S4: "Retire sends no body, ever"), and is distinguishable here from an empty body.</summary>
        public string? LastGraphRequestBody { get; private set; }

        public int GraphRequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"test-token","expires_in":3600}""")
                };
            }

            var body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            // Three search requests are in flight at once; the recording must not race.
            lock (_recordLock)
            {
                LastGraphRequestUri = request.RequestUri;
                LastGraphRequestMethod = request.Method;
                LastGraphRequestBody = body;
                GraphRequestUris.Add(request.RequestUri!);
                GraphRequestCount++;
            }

            return GraphResponseByRequest?.Invoke(request) ?? GraphResponse();
        }
    }

    private static (IntuneDeviceService Service, StubHandler Handler) CreateService()
    {
        var handler = new StubHandler();
        var client = new GraphTokenClient("tenant", "client", "secret", new HttpClient(handler));
        var service = new IntuneDeviceService(() => Task.FromResult<GraphTokenClient?>(client));
        return (service, handler);
    }

    // T7: a failed search must never render as "no devices found."
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SearchDevicesAsync_NonSuccessStatus_ThrowsRatherThanReturningEmpty(HttpStatusCode status)
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(status);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchDevicesAsync(null));
    }

    [Fact]
    public async Task SearchDevicesAsync_Forbidden_NamesConsentAsLikelyCause()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchDevicesAsync(null));

        Assert.Contains("DeviceManagementManagedDevices.Read.All", ex.Message);
    }

    /// <summary>
    /// T7 for the case the prefix filter actually risks: Graph rejecting a $filter comes back as a
    /// 400, and the operator must read that as a FAILED search rather than as a device that does
    /// not exist. Graph's own error text cannot be quoted - GraphTokenClient's GET helper drops the
    /// response body on a non-success status - so the message says which way to read the 400.
    /// </summary>
    [Fact]
    public async Task SearchDevicesAsync_BadRequest_SaysTheSearchFailedRatherThanFoundNothing()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchDevicesAsync("laptop-1"));

        Assert.Contains("400 Bad Request", ex.Message);
        Assert.Contains("failed search", ex.Message);
    }

    // Inverse of the failure tests: a genuinely empty result must still succeed.
    [Fact]
    public async Task SearchDevicesAsync_EmptyValueArray_ReturnsEmptySuccessNotFailure()
    {
        var (service, _) = CreateService();

        var result = await service.SearchDevicesAsync(null);

        Assert.Empty(result.Devices);
        Assert.False(result.Truncated);
        Assert.Equal(0, result.SearchedCount);
    }

    // T1: truncation must be visible, since GraphTokenClient cannot follow @odata.nextLink.
    [Fact]
    public async Task SearchDevicesAsync_NextLinkPresent_SetsTruncatedTrue()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$skiptoken=abc"}""")
        };

        var result = await service.SearchDevicesAsync(null);

        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task SearchDevicesAsync_NextLinkAbsent_SetsTruncatedFalse()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[{"id":"1","deviceName":"laptop-1"}]}""")
        };

        var result = await service.SearchDevicesAsync(null);

        Assert.False(result.Truncated);
        Assert.Single(result.Devices);
        Assert.Equal(1, result.SearchedCount);
    }

    [Theory]
    [InlineData("5000", 500)]
    [InlineData("0", 50)]
    [InlineData("not-a-number", 50)]
    [InlineData(null, 50)]
    [InlineData("50", 50)]
    [InlineData("1", 1)]
    public void ClampSearchResultLimit_ClampsOrFallsBackTo50(string? rawLimit, int expected)
    {
        // The 50 default from the module descriptor's DefaultValue and the 500 Graph cap share
        // one clamp path (a "5000" input clamps to 500, an unparseable/non-positive one falls
        // back to the descriptor's 50 default, and 50 unparsed passes through unchanged).
        Assert.Equal(expected, IntuneDeviceService.ClampSearchResultLimit(rawLimit));
    }

    [Fact]
    public async Task SearchDevicesAsync_DefaultTop_Is50()
    {
        var (service, handler) = CreateService();

        await service.SearchDevicesAsync(null);

        Assert.Contains("$top=50", handler.LastGraphRequestUri!.Query);
    }

    [Fact]
    public async Task SearchDevicesAsync_QueryCarriesSelect_AndExcludesActivationLockBypassCode()
    {
        var (service, handler) = CreateService();

        await service.SearchDevicesAsync(null);

        var query = Uri.UnescapeDataString(handler.LastGraphRequestUri!.Query);
        Assert.Contains("$select=", query);
        Assert.DoesNotContain("activationLockBypassCode", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchDevicesAsync_NoSearchTerm_EmitsNoFilter()
    {
        var (service, handler) = CreateService();

        await service.SearchDevicesAsync(null);

        Assert.DoesNotContain("filter", handler.LastGraphRequestUri!.Query, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the per-field search requests (plan T2 Revision 2026-09-03) --------------------------

    /// <summary>
    /// The exact relative URL a per-field search request must carry. The $select list is
    /// interpolated from the service's own constant rather than restated here: what these tests
    /// pin is the URL SHAPE - parameter order, separators, and which characters are percent-encoded
    /// - which is exactly what the 2026-09-03 fix changed. The $select CONTENT has its own guard
    /// (SearchDevicesAsync_QueryCarriesSelect_AndExcludesActivationLockBypassCode).
    /// </summary>
    private static string ExpectedSearchUrl(string filter, int top = 50) =>
        $"/deviceManagement/managedDevices?$top={top}&$select={IntuneDeviceService.SelectFields}&$filter={filter}";

    /// <summary>
    /// The device-name request, exactly. A prefix match, with `(`, `,` and the delimiting quotes
    /// LITERAL: the previous form escaped the whole expression, so those arrived percent-encoded
    /// and the tenant answered 200 with an empty value array.
    /// </summary>
    [Fact]
    public void BuildDeviceNameSearchUrl_IsTheExactRelativeUrlWithLiteralODataSyntax()
    {
        Assert.Equal(
            ExpectedSearchUrl("startswith(deviceName,'HYB-')"),
            IntuneDeviceService.BuildDeviceNameSearchUrl("HYB-", 50));
    }

    [Fact]
    public void BuildUserPrincipalNameSearchUrl_IsTheExactRelativeUrlWithLiteralODataSyntax()
    {
        Assert.Equal(
            ExpectedSearchUrl("startswith(userPrincipalName,'HYB-')"),
            IntuneDeviceService.BuildUserPrincipalNameSearchUrl("HYB-", 50));
    }

    /// <summary>
    /// The serial request stays `eq` - a serial is copied whole, never typed as a prefix - and the
    /// spaces around it are emitted as %20, so the builder's output IS the string sent.
    /// </summary>
    [Fact]
    public void BuildSerialNumberSearchUrl_IsTheExactRelativeUrlWithEncodedSpacesAroundEq()
    {
        Assert.Equal(
            ExpectedSearchUrl("serialNumber%20eq%20'HYB-'"),
            IntuneDeviceService.BuildSerialNumberSearchUrl("HYB-", 50));
    }

    /// <summary>
    /// The encoding split, stated as such: OData syntax characters are literal in every one of the
    /// three URLs, and the typed value is what gets escaped.
    /// </summary>
    [Fact]
    public void SearchUrls_LeaveODataSyntaxLiteralAndEscapeOnlyTheTypedValue()
    {
        var urls = new[]
        {
            IntuneDeviceService.BuildDeviceNameSearchUrl("a b&c", 50),
            IntuneDeviceService.BuildUserPrincipalNameSearchUrl("a b&c", 50),
            IntuneDeviceService.BuildSerialNumberSearchUrl("a b&c", 50)
        };

        foreach (var url in urls)
        {
            // Syntax: never percent-encoded.
            Assert.DoesNotContain("%28", url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%29", url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%2C", url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%27", url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%24", url, StringComparison.OrdinalIgnoreCase);
            // Value: always escaped, so the space and the ampersand cannot end the parameter.
            Assert.Contains("'a%20b%26c'", url);
            Assert.DoesNotContain("a b&c", url);
        }

        // And the syntax really is present in its literal form, so the assertions above are not
        // passing on an expression that lost its parentheses altogether.
        Assert.Contains("startswith(deviceName,'", urls[0]);
        Assert.Contains("startswith(userPrincipalName,'", urls[1]);
        Assert.Contains("serialNumber%20eq%20'", urls[2]);
    }

    /// <summary>
    /// T4c: a single quote in the value is DOUBLED as an OData literal escape and then percent-
    /// escaped as part of the value, never interpolated raw where it would close the literal.
    /// </summary>
    [Fact]
    public void SearchUrls_SingleQuoteInValue_IsDoubledThenEscapedNotInjectedRaw()
    {
        Assert.Equal(
            ExpectedSearchUrl("startswith(deviceName,'O%27%27Brien-Laptop')"),
            IntuneDeviceService.BuildDeviceNameSearchUrl("O'Brien-Laptop", 50));

        Assert.Equal(
            ExpectedSearchUrl("serialNumber%20eq%20'O%27%27Brien-Laptop'"),
            IntuneDeviceService.BuildSerialNumberSearchUrl("O'Brien-Laptop", 50));
    }

    /// <summary>
    /// `contains` is NOT usable on managedDevices - Graph answers 400 - so no substring operator
    /// may creep into any of the three requests.
    /// </summary>
    [Fact]
    public void SearchUrls_NeverUseContains()
    {
        foreach (var request in IntuneDeviceService.BuildSearchRequests("laptop-1", 50))
            Assert.DoesNotContain("contains", request.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildUnfilteredSearchUrl_CarriesNoFilterAtAll()
    {
        Assert.Equal(
            $"/deviceManagement/managedDevices?$top=50&$select={IntuneDeviceService.SelectFields}",
            IntuneDeviceService.BuildUnfilteredSearchUrl(50));
    }

    /// <summary>
    /// The heart of the 2026-09-03 fix: three requests, one per field, and NO combined `or` filter
    /// - which this tenant answers 200/empty for, with `eq` and with `startswith` alike, even for a
    /// device the same endpoint returns in an unfiltered page.
    /// </summary>
    [Fact]
    public async Task SearchDevicesAsync_WithSearchTerm_IssuesOneRequestPerFieldAndNoCombinedFilter()
    {
        var (service, handler) = CreateService();

        await service.SearchDevicesAsync("HYB-");

        Assert.Equal(3, handler.GraphRequestCount);

        // OriginalString, not PathAndQuery: it is the exact text the service composed, before
        // System.Uri has any chance to canonicalize part of it.
        foreach (var expected in new[]
                 {
                     IntuneDeviceService.BuildDeviceNameSearchUrl("HYB-", 50),
                     IntuneDeviceService.BuildUserPrincipalNameSearchUrl("HYB-", 50),
                     IntuneDeviceService.BuildSerialNumberSearchUrl("HYB-", 50)
                 })
        {
            Assert.Equal(1, handler.GraphRequestUris.Count(
                uri => uri.OriginalString.EndsWith(expected, StringComparison.Ordinal)));
        }

        Assert.DoesNotContain(handler.GraphRequestUris, uri =>
            uri.OriginalString.Contains(" or ", StringComparison.OrdinalIgnoreCase)
            || uri.OriginalString.Contains("%20or%20", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The merge: distinct by device id with the FIRST occurrence winning, ordered by device name
    /// rather than by which request answered first, and SearchedCount is the distinct count.
    /// </summary>
    [Fact]
    public async Task SearchDevicesAsync_MergesThePerFieldPagesByIdFirstOccurrenceWinningInNameOrder()
    {
        var (service, handler) = CreateService();
        handler.GraphResponseByRequest = request => Ok(SearchFieldOf(request) switch
        {
            "deviceName" =>
                """{"value":[{"id":"2","deviceName":"HYB-B"},{"id":"1","deviceName":"HYB-A","notes":"device-name page"}]}""",
            "userPrincipalName" =>
                """{"value":[{"id":"1","deviceName":"HYB-A","notes":"UPN page"},{"id":"3","deviceName":"AAA-1","userPrincipalName":"HYB-user@contoso.com"}]}""",
            _ =>
                """{"value":[{"id":"4","deviceName":"ZZZ-1","serialNumber":"HYB-"}]}"""
        });

        var result = await service.SearchDevicesAsync("HYB-");

        Assert.Equal(["3", "1", "2", "4"], result.Devices.Select(device => device.Id));
        Assert.Equal(4, result.SearchedCount);
        Assert.Equal(0, result.FilterIgnoredCount);
        // First occurrence wins: id 1 is the copy the device-name request returned.
        Assert.Equal("device-name page", result.Devices.Single(device => device.Id == "1").Notes);
    }

    /// <summary>
    /// T7 per field: one failing request fails the WHOLE search, naming which field's request
    /// failed. A search that returned the other two fields' rows would be a partial result
    /// presented as a complete one.
    /// </summary>
    [Theory]
    [InlineData("deviceName", "device name search")]
    [InlineData("userPrincipalName", "user principal name search")]
    [InlineData("serialNumber", "serial number search")]
    public async Task SearchDevicesAsync_OneFieldRequestFails_FailsTheWholeSearchNamingThatRequest(
        string failingField, string expectedWording)
    {
        var (service, handler) = CreateService();
        handler.GraphResponseByRequest = request => SearchFieldOf(request) == failingField
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            : Ok("""{"value":[{"id":"1","deviceName":"HYB-A"}]}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchDevicesAsync("HYB-"));

        Assert.Contains(expectedWording, ex.Message);
        Assert.Contains("400 Bad Request", ex.Message);
        Assert.Contains("failed search", ex.Message);
    }

    /// <summary>
    /// The defensive half: an endpoint that honours the request and IGNORES the $filter answers
    /// with an arbitrary page, and those rows must not render as matches beside a wipe button. They
    /// are dropped, and the fact that they were is counted so the page can say so.
    /// </summary>
    [Fact]
    public async Task SearchDevicesAsync_GraphIgnoresTheFilter_HidesTheNonMatchingRowsAndCountsThem()
    {
        var (service, handler) = CreateService();
        handler.GraphResponseByRequest = request => Ok(SearchFieldOf(request) == "deviceName"
            ? """{"value":[{"id":"1","deviceName":"HYB-A"},{"id":"9","deviceName":"OTHER-9","userPrincipalName":"someone@contoso.com","serialNumber":"SN-9"}]}"""
            : """{"value":[]}""");

        var result = await service.SearchDevicesAsync("HYB-");

        Assert.Equal(["1"], result.Devices.Select(device => device.Id));
        Assert.Equal(1, result.FilterIgnoredCount);
        Assert.Equal(2, result.SearchedCount);
    }

    // T1, across three responses: one truncated page truncates the whole search.
    [Fact]
    public async Task SearchDevicesAsync_AnyOnePerFieldPageTruncated_SetsTruncatedTrue()
    {
        var (service, handler) = CreateService();
        handler.GraphResponseByRequest = request => Ok(SearchFieldOf(request) == "serialNumber"
            ? """{"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$skiptoken=abc"}"""
            : """{"value":[{"id":"1","deviceName":"HYB-A"}]}""");

        var result = await service.SearchDevicesAsync("HYB-");

        Assert.True(result.Truncated);
        Assert.Single(result.Devices);
    }

    [Fact]
    public async Task SearchDevicesAsync_NoPerFieldPageTruncated_SetsTruncatedFalse()
    {
        var (service, handler) = CreateService();
        handler.GraphResponseByRequest = _ => Ok("""{"value":[{"id":"1","deviceName":"HYB-A"}]}""");

        var result = await service.SearchDevicesAsync("HYB-");

        Assert.False(result.Truncated);
        Assert.Single(result.Devices);
        Assert.Equal(1, result.SearchedCount);
    }

    /// <summary>
    /// The client-side verification's own rules: prefix on the two name fields, EQUALITY on the
    /// serial, case-insensitive throughout - the same three comparisons the three requests ask
    /// Graph for, so a matching device is never hidden by this check.
    /// </summary>
    [Theory]
    [InlineData("hyb-", "HYB-1", "", "", true)]
    [InlineData("HYB-", "OTHER-1", "hyb-user@contoso.com", "", true)]
    [InlineData("sn123", "OTHER-1", "", "SN123", true)]
    [InlineData("HYB-", "X-HYB-1", "user@contoso.com", "SN1", false)]
    [InlineData("SN123", "OTHER-1", "", "SN1234", false)]
    [InlineData("HYB-", "", "", "", false)]
    public void MatchesSearchTerm_IsPrefixOnTheNamesAndEqualityOnTheSerial(
        string searchTerm, string deviceName, string userPrincipalName, string serialNumber, bool expected)
    {
        var device = new IntuneDevice
        {
            Id = "1",
            DeviceName = deviceName,
            UserPrincipalName = userPrincipalName,
            SerialNumber = serialNumber
        };

        Assert.Equal(expected, IntuneDeviceService.MatchesSearchTerm(searchTerm, device));
    }

    [Theory]
    [InlineData("a&b")]
    [InlineData("a#b")]
    public async Task SearchDevicesAsync_ValueWithReservedCharacters_ProducesWellFormedUrls(string value)
    {
        var (service, handler) = CreateService();

        var result = await service.SearchDevicesAsync(value);

        Assert.Empty(result.Devices);
        // A well-formed request URI parses cleanly and the literal reserved character never
        // leaks unescaped into any of the three query strings.
        Assert.All(handler.GraphRequestUris, uri => Assert.DoesNotContain(value, uri.Query));
    }

    [Fact]
    public async Task SearchDevicesAsync_OverlongValue_ProducesWellFormedUrls()
    {
        var (service, handler) = CreateService();
        var overlong = new string('x', 500);

        var result = await service.SearchDevicesAsync(overlong);

        Assert.Empty(result.Devices);
        Assert.Equal(3, handler.GraphRequestUris.Count);
        Assert.All(handler.GraphRequestUris, uri => Assert.Contains(overlong, uri.Query));
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    /// <summary>Which of the three per-field search requests this is, read off its own $filter.</summary>
    private static string SearchFieldOf(HttpRequestMessage request)
    {
        var url = request.RequestUri!.OriginalString;

        if (url.Contains("startswith(deviceName", StringComparison.Ordinal))
            return "deviceName";

        if (url.Contains("startswith(userPrincipalName", StringComparison.Ordinal))
            return "userPrincipalName";

        Assert.Contains("serialNumber", url);
        return "serialNumber";
    }

    [Fact]
    public async Task GetDeviceAsync_Success_ParsesDevice()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"id":"dev-1","deviceName":"laptop-1","userPrincipalName":"a@b.com","serialNumber":"SN123",
                 "isEncrypted":true,"totalStorageSpaceInBytes":1000,"jailBroken":"False"}
                """)
        };

        var device = await service.GetDeviceAsync("dev-1");

        Assert.Equal("dev-1", device.Id);
        Assert.Equal("laptop-1", device.DeviceName);
        Assert.Equal("SN123", device.SerialNumber);
        Assert.True(device.IsEncrypted);
        Assert.Equal(1000, device.TotalStorageSpaceInBytes);
        Assert.False(device.JailBroken);
    }

    [Fact]
    public async Task GetDeviceAsync_NotFound_Throws()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetDeviceAsync("missing"));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDeviceAsync_MalformedJson_Throws()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json")
        };

        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => service.GetDeviceAsync("dev-1"));
    }

    // T4b: activationLockBypassCode has nowhere to land even if a tenant returns it anyway.
    [Fact]
    public async Task GetDeviceAsync_ResponseContainsActivationLockBypassCode_DoesNotSurfaceIt()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"dev-1","activationLockBypassCode":"SECRET-CODE-123"}""")
        };

        var device = await service.GetDeviceAsync("dev-1");

        Assert.DoesNotContain("SECRET-CODE-123", System.Text.Json.JsonSerializer.Serialize(device));
    }

    [Fact]
    public async Task GetGraphClientAsync_GraphClientUnavailable_SearchThrowsRatherThanSilentlySucceeding()
    {
        var service = new IntuneDeviceService(() => Task.FromResult<GraphTokenClient?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchDevicesAsync(null));
    }

    // The seam-constructed service has no ModuleConfigService to read GraphDelineaSecretId from,
    // so IsAvailable must report unavailable rather than default to available.
    [Fact]
    public void IsAvailable_NoModuleConfig_ReturnsFalse()
    {
        var service = new IntuneDeviceService(() => Task.FromResult<GraphTokenClient?>(null));

        Assert.False(service.IsAvailable);
    }

    // ---- S3: delete ---------------------------------------------------------------------------

    [Fact]
    public async Task DeleteDeviceAsync_NoContent_ReportsSuccessAndSaysWhatSurvives()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NoContent);

        var result = await service.DeleteDeviceAsync("dev-1");

        Assert.True(result.Success);
        Assert.Null(result.SafeError);
        // T3 / AC11: delete removes the Intune record only.
        Assert.Contains("company data stays on the device", result.Message);
        Assert.Contains("Entra ID object still exists", result.Message);
    }

    [Fact]
    public async Task DeleteDeviceAsync_TargetsTheManagedDeviceRecordWithDelete()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NoContent);

        await service.DeleteDeviceAsync("dev-1");

        Assert.Equal(HttpMethod.Delete, handler.LastGraphRequestMethod);
        Assert.Equal("/v1.0/deviceManagement/managedDevices/dev-1", handler.LastGraphRequestUri!.AbsolutePath);
    }

    // AC15: 403, 404 and 5xx are three DISTINCT outcomes carrying the sanitized Graph error, never
    // one bare "failed". Reverting DeleteWithStatusAsync to the bool-returning DeleteAsync must fail
    // these, because a bool cannot carry the status.
    [Fact]
    public async Task DeleteDeviceAsync_Forbidden_NamesTheReadWriteConsentAndCarriesTheGraphError()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"code":"Forbidden","message":"Insufficient privileges."}}""")
        };

        var result = await service.DeleteDeviceAsync("dev-1");

        Assert.False(result.Success);
        Assert.Contains("403", result.Message);
        Assert.Contains("DeviceManagementManagedDevices.ReadWrite.All", result.Message);
        Assert.Equal("Forbidden: Insufficient privileges.", result.SafeError);
    }

    [Fact]
    public async Task DeleteDeviceAsync_NotFound_SaysTheDeviceIsAlreadyGoneFromIntune()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NotFound);

        var result = await service.DeleteDeviceAsync("dev-1");

        Assert.False(result.Success);
        Assert.Contains("404", result.Message);
        Assert.Contains("no longer in Intune", result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task DeleteDeviceAsync_ServerError_SaysNothingWasDoneAndCanBeRetried(HttpStatusCode status)
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(status);

        var result = await service.DeleteDeviceAsync("dev-1");

        Assert.False(result.Success);
        Assert.Contains(((int)status).ToString(), result.Message);
        Assert.Contains("retry", result.Message);
    }

    [Fact]
    public async Task DeleteDeviceAsync_EveryFailureStatusProducesADifferentMessage()
    {
        // The point of AC15 stated as one assertion: a caller reading only Message can still tell a
        // missing permission from an already-deleted device from an outage.
        var messages = new List<string>();
        foreach (var status in new[]
                 {
                     HttpStatusCode.Forbidden, HttpStatusCode.NotFound,
                     HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests
                 })
        {
            var (service, handler) = CreateService();
            handler.GraphResponse = () => new HttpResponseMessage(status);
            var result = await service.DeleteDeviceAsync("dev-1");
            Assert.False(result.Success);
            messages.Add(result.Message);
        }

        Assert.Equal(messages.Count, messages.Distinct().Count());
    }

    [Fact]
    public async Task DeleteDeviceAsync_NonJsonErrorBody_DoesNotEchoTheBody()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>gateway exploded, token=abc123</html>")
        };

        var result = await service.DeleteDeviceAsync("dev-1");

        Assert.False(result.Success);
        Assert.Null(result.SafeError);
        Assert.DoesNotContain("abc123", result.Message);
    }

    [Fact]
    public async Task DeleteDeviceAsync_GraphClientUnavailable_Throws()
    {
        var service = new IntuneDeviceService(() => Task.FromResult<GraphTokenClient?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteDeviceAsync("dev-1"));
    }

    // ---- S4: retire ---------------------------------------------------------------------------

    [Fact]
    public async Task RetireDeviceAsync_NoContent_SaysQueuedAndNamesTheCheckIn()
    {
        // T3 / AC11: 204 is Intune ACCEPTING the request, not the device having acted.
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NoContent);

        var result = await service.RetireDeviceAsync("dev-1");

        Assert.True(result.Success);
        Assert.Contains("Queued retire", result.Message);
        Assert.Contains("next check-in", result.Message);
    }

    [Fact]
    public async Task RetireDeviceAsync_SendsNoBodyAtAll()
    {
        // Plan S4: "Retire sends no body, ever." Learn is explicit that it takes none, and this
        // asserts absence rather than emptiness so a shared helper cannot quietly give retire one.
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NoContent);

        await service.RetireDeviceAsync("dev-1");

        Assert.Equal(HttpMethod.Post, handler.LastGraphRequestMethod);
        Assert.Equal("/v1.0/deviceManagement/managedDevices/dev-1/retire", handler.LastGraphRequestUri!.AbsolutePath);
        Assert.Null(handler.LastGraphRequestBody);
    }

    [Fact]
    public async Task RetireDeviceAsync_Forbidden_NamesThePrivilegedOperationsConsent()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"code":"Forbidden","message":"Missing PrivilegedOperations."}}""")
        };

        var result = await service.RetireDeviceAsync("dev-1");

        Assert.False(result.Success);
        Assert.Contains("DeviceManagementManagedDevices.PrivilegedOperations.All", result.Message);
        Assert.Equal("Forbidden: Missing PrivilegedOperations.", result.SafeError);
    }

    [Fact]
    public async Task RetireDeviceAsync_EveryFailureStatusProducesADifferentMessage()
    {
        var messages = new List<string>();
        foreach (var status in new[]
                 {
                     HttpStatusCode.Forbidden, HttpStatusCode.NotFound,
                     HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests
                 })
        {
            var (service, handler) = CreateService();
            handler.GraphResponse = () => new HttpResponseMessage(status);
            var result = await service.RetireDeviceAsync("dev-1");
            Assert.False(result.Success);
            messages.Add(result.Message);
        }

        Assert.Equal(messages.Count, messages.Distinct().Count());
    }

    // ---- S4: wipe -----------------------------------------------------------------------------

    [Fact]
    public async Task WipeDeviceAsync_NoContent_SaysQueuedAndNamesTheCheckIn()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NoContent);

        var result = await service.WipeDeviceAsync("dev-1", new IntuneWipeOptions());

        Assert.True(result.Success);
        Assert.Contains("Queued wipe", result.Message);
        Assert.Contains("next check-in", result.Message);
    }

    [Fact]
    public async Task WipeDeviceAsync_PostsToTheWipeActionWithAnExplicitBody()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NoContent);

        await service.WipeDeviceAsync("dev-1", new IntuneWipeOptions());

        Assert.Equal(HttpMethod.Post, handler.LastGraphRequestMethod);
        Assert.Equal("/v1.0/deviceManagement/managedDevices/dev-1/wipe", handler.LastGraphRequestUri!.AbsolutePath);
        Assert.NotNull(handler.LastGraphRequestBody);
    }

    // AC16, one case per combination. The serialized body is asserted whole, so a flag silently
    // dropped, renamed, or left to a Graph default fails here - and the unset optional three must be
    // ABSENT rather than present-and-null.
    [Fact]
    public async Task WipeDeviceAsync_Defaults_SerializesBothFlagsFalseAndNothingElse()
    {
        var body = await WipeBodyFor(new IntuneWipeOptions());

        Assert.Equal("""{"keepUserData":false,"keepEnrollmentData":false}""", body);
    }

    [Fact]
    public async Task WipeDeviceAsync_KeepUserData_SerializesItTrueAndStillSendsTheOtherFlag()
    {
        var body = await WipeBodyFor(new IntuneWipeOptions(KeepUserData: true));

        Assert.Equal("""{"keepUserData":true,"keepEnrollmentData":false}""", body);
    }

    [Fact]
    public async Task WipeDeviceAsync_KeepEnrollmentData_SerializesItTrueAndStillSendsTheOtherFlag()
    {
        var body = await WipeBodyFor(new IntuneWipeOptions(KeepEnrollmentData: true));

        Assert.Equal("""{"keepUserData":false,"keepEnrollmentData":true}""", body);
    }

    [Fact]
    public async Task WipeDeviceAsync_MacOsUnlockCodeSet_IsIncludedTrimmed()
    {
        var body = await WipeBodyFor(new IntuneWipeOptions(MacOsUnlockCode: " 123456 "));

        Assert.Equal("""{"keepUserData":false,"keepEnrollmentData":false,"macOsUnlockCode":"123456"}""", body);
    }

    [Fact]
    public async Task WipeDeviceAsync_ObliterationBehaviourSet_IsIncluded()
    {
        var body = await WipeBodyFor(new IntuneWipeOptions(ObliterationBehavior: "alwaysObliterate"));

        Assert.Equal("""{"keepUserData":false,"keepEnrollmentData":false,"obliterationBehavior":"alwaysObliterate"}""", body);
    }

    [Fact]
    public async Task WipeDeviceAsync_PersistEsimDataPlan_IsIncludedOnlyWhenSet()
    {
        var on = await WipeBodyFor(new IntuneWipeOptions(PersistEsimDataPlan: true));
        var off = await WipeBodyFor(new IntuneWipeOptions(PersistEsimDataPlan: false));

        Assert.Equal("""{"keepUserData":false,"keepEnrollmentData":false,"persistEsimDataPlan":true}""", on);
        Assert.DoesNotContain("persistEsimDataPlan", off);
    }

    [Fact]
    public void BuildWipeBody_UnsetOptionalParameters_AreAbsentNotNull()
    {
        var body = IntuneDeviceService.BuildWipeBody(new IntuneWipeOptions());

        Assert.Equal(2, body.Count);
        Assert.False(body.ContainsKey("macOsUnlockCode"));
        Assert.False(body.ContainsKey("obliterationBehavior"));
        Assert.False(body.ContainsKey("persistEsimDataPlan"));
    }

    [Fact]
    public async Task WipeDeviceAsync_Forbidden_NamesThePrivilegedOperationsConsent()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var result = await service.WipeDeviceAsync("dev-1", new IntuneWipeOptions());

        Assert.False(result.Success);
        Assert.Contains("DeviceManagementManagedDevices.PrivilegedOperations.All", result.Message);
    }

    [Fact]
    public async Task WipeDeviceAsync_EveryFailureStatusProducesADifferentMessage()
    {
        var messages = new List<string>();
        foreach (var status in new[]
                 {
                     HttpStatusCode.Forbidden, HttpStatusCode.NotFound,
                     HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests
                 })
        {
            var (service, handler) = CreateService();
            handler.GraphResponse = () => new HttpResponseMessage(status);
            var result = await service.WipeDeviceAsync("dev-1", new IntuneWipeOptions());
            Assert.False(result.Success);
            messages.Add(result.Message);
        }

        Assert.Equal(messages.Count, messages.Distinct().Count());
    }

    [Fact]
    public async Task RetireAndWipe_GraphClientUnavailable_Throw()
    {
        var service = new IntuneDeviceService(() => Task.FromResult<GraphTokenClient?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetireDeviceAsync("dev-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WipeDeviceAsync("dev-1", new IntuneWipeOptions()));
    }

    /// <summary>The exact JSON body a wipe put on the wire for these options.</summary>
    private static async Task<string> WipeBodyFor(IntuneWipeOptions options)
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NoContent);

        await service.WipeDeviceAsync("dev-1", options);

        Assert.NotNull(handler.LastGraphRequestBody);
        return handler.LastGraphRequestBody!;
    }

    // ---- S5: the Entra ID device object -------------------------------------------------------

    /// <summary>A real-shaped pair from Learn's own `device: get` example, which returns the object
    /// id and the deviceId as two DIFFERENT GUIDs for the same device. managedDevice carries only
    /// the second one.</summary>
    private const string EntraDeviceId = "6fa60d52-01e7-4b18-8fc7-8f9d1b9b1a5c";

    // AC22, the trap in this slice: the DELETE must address the ALTERNATE KEY. azureADDeviceId is
    // the Entra deviceId, while /devices/{id} wants the directory OBJECT id, so the path form 404s
    // against a real, still-present device - a silent wrong answer. Building the path form here must
    // fail this test.
    [Fact]
    public async Task RemoveEntraDeviceAsync_AddressesTheAlternateKeyNotThePathId()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NoContent);

        await service.RemoveEntraDeviceAsync(EntraDeviceId);

        Assert.Equal(HttpMethod.Delete, handler.LastGraphRequestMethod);
        Assert.Equal($"/v1.0/devices(deviceId='{EntraDeviceId}')", handler.LastGraphRequestUri!.AbsolutePath);
        Assert.NotEqual($"/v1.0/devices/{EntraDeviceId}", handler.LastGraphRequestUri!.AbsolutePath);
    }

    [Fact]
    public void EntraDeviceEndpoint_IsTheAlternateKeyForm()
    {
        // The URL builder on its own, so the assertion above cannot be satisfied by a coincidence in
        // Uri parsing, and so a future caller cannot reintroduce the path form somewhere else.
        var endpoint = IntuneDeviceService.EntraDeviceEndpoint(EntraDeviceId);

        Assert.Equal($"/devices(deviceId='{EntraDeviceId}')", endpoint);
        Assert.DoesNotContain($"/devices/{EntraDeviceId}", endpoint);
    }

    [Fact]
    public async Task RemoveEntraDeviceAsync_NoContent_ReportsSuccessAndSaysWhatItDoesNotDo()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NoContent);

        var result = await service.RemoveEntraDeviceAsync(EntraDeviceId);

        Assert.True(result.Success);
        Assert.Null(result.SafeError);
        Assert.Contains("Removed the device's Entra ID device object", result.Message);
        Assert.Contains("Company data on the device is unaffected", result.Message);
    }

    [Fact]
    public async Task RemoveEntraDeviceAsync_Forbidden_NamesTheDirectoryScopeConsent()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges."}}""")
        };

        var result = await service.RemoveEntraDeviceAsync(EntraDeviceId);

        Assert.False(result.Success);
        Assert.Contains("403", result.Message);
        // D3: the widest grant in the module, so a missing consent must be diagnosable by name.
        Assert.Contains("Device.ReadWrite.All", result.Message);
        Assert.Equal("Authorization_RequestDenied: Insufficient privileges.", result.SafeError);
    }

    [Fact]
    public async Task RemoveEntraDeviceAsync_NotFound_NamesTheDirectoryObjectNotTheIntuneRecord()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NotFound);

        var result = await service.RemoveEntraDeviceAsync(EntraDeviceId);

        Assert.False(result.Success);
        Assert.Contains("404", result.Message);
        Assert.Contains("no Entra ID device object", result.Message);
        // Saying "no longer in Intune" here would name the wrong object entirely.
        Assert.DoesNotContain("no longer in Intune", result.Message);
    }

    [Fact]
    public async Task RemoveEntraDeviceAsync_EveryFailureStatusProducesADifferentMessage()
    {
        // AC15's shape for this half: 403, 404 and 5xx are three distinct outcomes, never one bare
        // "failed" - a bool-returning DELETE could not tell them apart.
        var messages = new List<string>();
        foreach (var status in new[]
                 {
                     HttpStatusCode.Forbidden, HttpStatusCode.NotFound,
                     HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests
                 })
        {
            var (service, handler) = CreateService();
            handler.GraphResponse = () => new HttpResponseMessage(status);
            var result = await service.RemoveEntraDeviceAsync(EntraDeviceId);
            Assert.False(result.Success);
            messages.Add(result.Message);
        }

        Assert.Equal(messages.Count, messages.Distinct().Count());
    }

    // AC24: an unusable azureADDeviceId is refused BEFORE any request is issued - the option is
    // never offered-and-silently-skipped, and no malformed DELETE reaches the tenant.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    public async Task RemoveEntraDeviceAsync_UnusableDeviceId_RefusesBeforeAnyRequest(string? azureAdDeviceId)
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NoContent);

        var result = await service.RemoveEntraDeviceAsync(azureAdDeviceId);

        Assert.False(result.Success);
        Assert.Contains("no usable Entra ID device id", result.Message);
        Assert.Contains("Nothing was attempted", result.Message);
        Assert.Equal(0, handler.GraphRequestCount);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("not-a-guid", false)]
    [InlineData("6fa60d52-01e7-4b18-8fc7-8f9d1b9b1a5c", true)]
    [InlineData("  6fa60d52-01e7-4b18-8fc7-8f9d1b9b1a5c  ", true)]
    public void IsUsableEntraDeviceId_OnlyARealGuidIsUsable(string? azureAdDeviceId, bool expected)
    {
        Assert.Equal(expected, IntuneDeviceService.IsUsableEntraDeviceId(azureAdDeviceId));
    }

    [Fact]
    public async Task RemoveEntraDeviceAsync_GraphClientUnavailable_Throws()
    {
        var service = new IntuneDeviceService(() => Task.FromResult<GraphTokenClient?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveEntraDeviceAsync(EntraDeviceId));
    }

    [Fact]
    public void DeleteSuccessMessages_DifferOnWhetherTheEntraObjectSurvived()
    {
        // AC11's conditional half at the source: the two messages cannot both be right, so they must
        // not be the same string.
        Assert.Contains("Entra ID object still exists", IntuneDeviceService.DeleteSuccessMessage);
        Assert.Contains("Entra ID object was removed as well", IntuneDeviceService.DeleteSuccessMessageEntraRemoved);
        Assert.DoesNotContain("still exists", IntuneDeviceService.DeleteSuccessMessageEntraRemoved);
    }

    // ---- S6: affected-user notification -------------------------------------------------------

    /// <summary>
    /// An EmailService over an in-memory configuration, so the app-wide affected-user switch is the
    /// REAL gate (Email:NotifyUsersOnPermissionGrant, EmailService.cs) rather than a bare bool
    /// invented by the test. No SMTP is reachable and none is needed: only the switch is read.
    /// </summary>
    private static EmailService EmailWithUserNotifications(bool? enabled)
    {
        var settings = new Dictionary<string, string?> { ["Email:AdminNotificationEmail"] = "admin@contoso.com" };
        if (enabled.HasValue)
            settings["Email:NotifyUsersOnPermissionGrant"] = enabled.Value ? "true" : "false";

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new EmailService(config, Substitute.For<ILogger<EmailService>>());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void UserNotificationsEnabled_ReflectsTheDeploymentSwitchAndDefaultsOff(bool? configured, bool expected)
    {
        Assert.Equal(expected, EmailWithUserNotifications(configured).UserNotificationsEnabled);
    }

    [Fact]
    public void NotifyUserStartsTicked_IsFixedPerActionAndNotReadFromModuleConfig()
    {
        // Owner ruling 2026-09-02 (.agents/decisions.md, superseding D2's config half): the starting
        // states are fixed in code and the operator decides at act time, so no module-config field
        // may reappear behind them. Delete off (nothing changes on the user's device, so a mail
        // would confuse), retire and wipe on - D2's states, now hardcoded.
        Assert.False(IntuneDeviceService.NotifyUserStartsTicked(IntuneDeviceAction.Delete));
        Assert.True(IntuneDeviceService.NotifyUserStartsTicked(IntuneDeviceAction.Retire));
        Assert.True(IntuneDeviceService.NotifyUserStartsTicked(IntuneDeviceAction.Wipe));

        // The standalone Entra removal offers no notification at all - null, not false, so the
        // absence is deliberate rather than an unticked box.
        Assert.Null(IntuneDeviceService.NotifyUserStartsTicked(IntuneDeviceAction.EntraDelete));

        // The Entra add-on starts unticked: a second, separately permissioned deletion is opted into.
        Assert.False(IntuneDeviceService.EntraRemovalStartsTicked);

        // The descriptor carries neither set of defaults; ModuleCatalogTests pins that by name.
        var module = new ModuleCatalog().GetById("IntuneDevices")!;
        Assert.DoesNotContain(module.ConfigFields, f => f.Key.StartsWith("NotifyUserOn", StringComparison.Ordinal));
        Assert.DoesNotContain(module.ConfigFields, f => f.Key == "RemoveEntraObjectByDefault");
    }

    // AC18: the starting state decides when the operator changes nothing, and the operator's change
    // at the moment of acting is what takes effect - in BOTH directions.
    [Fact]
    public void DecideUserNotification_ConfigDefaultDecidesWhenTheOperatorChangesNothing()
    {
        var offByDefault = IntuneDeviceService.DecideUserNotification(
            configuredDefault: false, operatorRequested: false, userNotificationsEnabled: true, "user@contoso.com");
        var onByDefault = IntuneDeviceService.DecideUserNotification(
            configuredDefault: true, operatorRequested: true, userNotificationsEnabled: true, "user@contoso.com");

        Assert.Equal(IntuneUserNotificationOutcome.NotRequestedByDefault, offByDefault.Outcome);
        Assert.False(offByDefault.ShouldSend);
        Assert.Equal(IntuneUserNotificationOutcome.Send, onByDefault.Outcome);
        Assert.True(onByDefault.ShouldSend);
    }

    [Fact]
    public void DecideUserNotification_OperatorOverridesTheDefaultInBothDirections()
    {
        var tickedOnADefaultOffAction = IntuneDeviceService.DecideUserNotification(
            configuredDefault: false, operatorRequested: true, userNotificationsEnabled: true, "user@contoso.com");
        var clearedOnADefaultOnAction = IntuneDeviceService.DecideUserNotification(
            configuredDefault: true, operatorRequested: false, userNotificationsEnabled: true, "user@contoso.com");

        Assert.Equal(IntuneUserNotificationOutcome.Send, tickedOnADefaultOffAction.Outcome);
        // The lost-or-stolen case: the reason must say the OPERATOR cleared it, not that config did.
        Assert.Equal(IntuneUserNotificationOutcome.NotRequestedByOperator, clearedOnADefaultOnAction.Outcome);
        Assert.Contains("operator cleared", clearedOnADefaultOnAction.Reason);
    }

    // AC19, and the S6 trap: EmailService gates every affected-user send on an app-wide switch that
    // outranks anything this module sets. A ticked box on a deployment with that switch off must
    // produce a SUPPRESSED outcome with a reason - never a Send. Removing that branch must fail here.
    [Fact]
    public void DecideUserNotification_AppWideSwitchOff_SuppressesAndSaysSoRatherThanSending()
    {
        var email = EmailWithUserNotifications(false);

        var decision = IntuneDeviceService.DecideUserNotification(
            configuredDefault: true, operatorRequested: true,
            userNotificationsEnabled: email.UserNotificationsEnabled, "user@contoso.com");

        Assert.Equal(IntuneUserNotificationOutcome.SuppressedAppWide, decision.Outcome);
        Assert.False(decision.ShouldSend);
        Assert.Contains("disabled for this whole deployment", decision.Reason);
        Assert.Contains("NotifyUsersOnPermissionGrant", decision.Reason);
    }

    [Fact]
    public void DecideUserNotification_AppWideSwitchOn_AllowsTheSend()
    {
        // The inverse of the suppression test, so "always suppressed" cannot pass both.
        var email = EmailWithUserNotifications(true);

        var decision = IntuneDeviceService.DecideUserNotification(
            configuredDefault: true, operatorRequested: true,
            userNotificationsEnabled: email.UserNotificationsEnabled, "user@contoso.com");

        Assert.Equal(IntuneUserNotificationOutcome.Send, decision.Outcome);
        Assert.Contains("user@contoso.com", decision.Reason);
    }

    // AC20: a device with no primary user address offers no notification and records why - it does
    // not fail the action and does not look like a successful send.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    public void DecideUserNotification_NoPrimaryUserAddress_RecordsWhyRatherThanSending(string? address)
    {
        var decision = IntuneDeviceService.DecideUserNotification(
            configuredDefault: true, operatorRequested: true, userNotificationsEnabled: true, address);

        Assert.Equal(IntuneUserNotificationOutcome.NoAddress, decision.Outcome);
        Assert.False(decision.ShouldSend);
        Assert.Contains("no primary user address", decision.Reason);
    }

    [Fact]
    public void DecideUserNotification_EveryNotSentCaseIsDistinguishableFromTheSentCase()
    {
        // D2: "a silent no is indistinguishable from a failure." Four not-sent reasons, one sent
        // reason, five distinct sentences and five distinct outcomes.
        var decisions = new[]
        {
            IntuneDeviceService.DecideUserNotification(false, false, true, "user@contoso.com"),
            IntuneDeviceService.DecideUserNotification(true, false, true, "user@contoso.com"),
            IntuneDeviceService.DecideUserNotification(true, true, false, "user@contoso.com"),
            IntuneDeviceService.DecideUserNotification(true, true, true, ""),
            IntuneDeviceService.DecideUserNotification(true, true, true, "user@contoso.com")
        };

        Assert.Equal(5, decisions.Select(d => d.Outcome).Distinct().Count());
        Assert.Equal(5, decisions.Select(d => d.Reason).Distinct().Count());
        Assert.Single(decisions, d => d.ShouldSend);
    }

    [Fact]
    public void BuildDeviceActionUserBody_NamesTheDeviceTheActionAndTheTicketAndNothingElse()
    {
        // D2's body rule: the mail may be read by the wrong person - on a wipe it may reach a mailbox
        // the user can now only open elsewhere, and on a lost device whoever holds it may read it.
        var body = EmailService.BuildDeviceActionUserBody("laptop-1", "Wipe", "INC0012345");

        Assert.Contains("laptop-1", body);
        Assert.Contains("Wipe", body);
        Assert.Contains("INC0012345", body);
        // No operator identity, no primary user address, no action parameters.
        Assert.DoesNotContain("Performed", body);
        Assert.DoesNotContain("keepUserData", body);
        Assert.DoesNotContain("@contoso", body);
    }

    [Fact]
    public void BuildDeviceActionUserBody_EncodesInterpolatedValues()
    {
        var body = EmailService.BuildDeviceActionUserBody("<script>x</script>", "Wipe", "INC1");

        Assert.DoesNotContain("<script>", body);
        Assert.Contains("&lt;script&gt;", body);
    }

    [Fact]
    public async Task SendDeviceActionUserNotificationAsync_AppWideSwitchOff_ReportsThatItDidNotSend()
    {
        // The send itself honours the same gate and RETURNS whether it actually sent, so a caller can
        // record a suppressed send instead of assuming one happened (plan S6).
        var email = EmailWithUserNotifications(false);

        var sent = await email.SendDeviceActionUserNotificationAsync("user@contoso.com", "laptop-1", "Wipe", "INC1");

        Assert.False(sent);
    }

    // ---- GetGraphClientAsync credential path (plan Test plan) ---------------------------------

    [Fact]
    public void ExtractGraphCredentials_AllThreeFieldsPresent_ReturnsThem()
    {
        var credentials = IntuneDeviceService.ExtractGraphCredentials(new Dictionary<string, string>
        {
            ["Tenant ID"] = "t",
            ["Application ID"] = "a",
            ["Client Secret"] = "s"
        });

        Assert.NotNull(credentials);
        Assert.Equal(("t", "a", "s"), (credentials.Value.TenantId, credentials.Value.ClientId, credentials.Value.ClientSecret));
    }

    [Fact]
    public void ExtractGraphCredentials_SecretUnreadable_ReturnsNull()
    {
        Assert.Null(IntuneDeviceService.ExtractGraphCredentials(null));
    }

    [Theory]
    [InlineData("Client Secret")]
    [InlineData("Tenant ID")]
    [InlineData("Application ID")]
    public void ExtractGraphCredentials_SecretMissingOneField_ReturnsNull(string missingField)
    {
        // AC14: a secret carrying two of the three fields yields NO client, rather than a
        // GraphTokenClient built over an empty credential that then fails at token acquisition.
        var fields = new Dictionary<string, string>
        {
            ["Tenant ID"] = "t",
            ["Application ID"] = "a",
            ["Client Secret"] = "s"
        };
        fields.Remove(missingField);

        Assert.Null(IntuneDeviceService.ExtractGraphCredentials(fields));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-3")]
    public async Task GetGraphClientAsync_SecretIdUnsetOrNonNumeric_ReportsUnavailableAndRefusesToRead(string? secretId)
    {
        // The real config path (not the seam): with GraphDelineaSecretId unset or unparseable the
        // module reports unavailable and falls back to NOTHING - no other module's credential, and
        // no silent empty result (AC14 / T7).
        using var temp = new TempDir();
        var service = CreateConfiguredService(temp.Path, secretId);

        Assert.False(service.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchDevicesAsync("laptop-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteDeviceAsync("dev-1"));
    }

    /// <summary>
    /// The DI-constructed service over a real ModuleConfigService and DelineaService, following
    /// MfaResetServiceConfigTests. Only the id-parsing half of GetGraphClientAsync is reachable this
    /// way: DelineaService.GetSecretFieldsAsync returns null before issuing any HTTP request when the
    /// Secret Server bootstrap credential is absent from Windows Credential Manager, so the
    /// field-level cases are covered directly against ExtractGraphCredentials above instead of
    /// through a stub that would pass for the wrong reason.
    /// </summary>
    private static IntuneDeviceService CreateConfiguredService(string contentRoot, string? secretId)
    {
        var store = TestConfigStore.Create(contentRoot);
        var values = new Dictionary<string, string>();
        if (secretId != null)
            values["GraphDelineaSecretId"] = secretId;
        new ModuleConfigRepository(store).SaveModule("IntuneDevices", values);

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(contentRoot);

        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env,
            new ModuleConfigRepository(store), Substitute.For<ILogger<ModuleConfigService>>());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Audit:LogRoot"] = contentRoot
            })
            .Build();

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var operationTrace = new OperationTraceService(config, jsonlLog);
        var extendedLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(contentRoot),
            Substitute.For<ILogger<ExtendedLogService>>());
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(),
            extendedLog, operationTrace);

        return new IntuneDeviceService(moduleConfig, delinea, httpClientFactory);
    }
}
