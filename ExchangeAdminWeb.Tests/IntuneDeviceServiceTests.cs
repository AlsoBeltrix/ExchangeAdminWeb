using System.Net;
using ExchangeAdminWeb.Services;

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

        public Uri? LastGraphRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"test-token","expires_in":3600}""")
                });
            }

            LastGraphRequestUri = request.RequestUri;
            return Task.FromResult(GraphResponse());
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

    [Fact]
    public async Task SearchDevicesAsync_WithSearchTerm_EmitsFilterAcrossThreeFields()
    {
        var (service, handler) = CreateService();

        await service.SearchDevicesAsync("laptop-1");

        var query = Uri.UnescapeDataString(handler.LastGraphRequestUri!.Query);
        Assert.Contains("deviceName eq 'laptop-1'", query);
        Assert.Contains("serialNumber eq 'laptop-1'", query);
        Assert.Contains("userPrincipalName eq 'laptop-1'", query);
    }

    // T4c: a single quote in the search value is doubled, never interpolated raw.
    [Fact]
    public void BuildFilterExpression_SingleQuoteInValue_IsDoubledNotInjectedRaw()
    {
        var expression = IntuneDeviceService.BuildFilterExpression("O'Brien-Laptop");

        Assert.Equal("deviceName eq 'O''Brien-Laptop' or serialNumber eq 'O''Brien-Laptop' or userPrincipalName eq 'O''Brien-Laptop'", expression);
    }

    [Fact]
    public void BuildFilterExpression_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(IntuneDeviceService.BuildFilterExpression(null));
        Assert.Null(IntuneDeviceService.BuildFilterExpression(""));
        Assert.Null(IntuneDeviceService.BuildFilterExpression("   "));
    }

    [Theory]
    [InlineData("a&b")]
    [InlineData("a#b")]
    public async Task SearchDevicesAsync_ValueWithReservedCharacters_ProducesWellFormedUrl(string value)
    {
        var (service, handler) = CreateService();

        var result = await service.SearchDevicesAsync(value);

        Assert.Empty(result.Devices);
        // A well-formed request URI parses cleanly and the literal reserved character never
        // leaks unescaped into the query string.
        Assert.DoesNotContain(value, handler.LastGraphRequestUri!.Query);
    }

    [Fact]
    public async Task SearchDevicesAsync_OverlongValue_ProducesWellFormedUrl()
    {
        var (service, handler) = CreateService();
        var overlong = new string('x', 500);

        var result = await service.SearchDevicesAsync(overlong);

        Assert.Empty(result.Devices);
        Assert.Contains("deviceName", handler.LastGraphRequestUri!.Query);
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
}
