using System.Net;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Tests for the module-local Defender API client (docs/DefenderEndpointDevices-Plan.md S1, T1/T2).
/// Every one runs against a locally declared HTTP stub; this slice makes no live call and cannot.
/// GraphTokenClientTests.StubHandler is private sealed to that class and is not reachable here, so
/// this file declares its own, matching ServiceHealthServiceTests and RiskyUsersServiceTests.
/// </summary>
public class DefenderApiClientTests
{
    /// <summary>
    /// Serves a canned token for login.microsoftonline.com and hands every other request to a
    /// per-test responder, recording what was actually sent - the request URIs exactly as they went
    /// on the wire, the bearer values, and the form body of each token request, which is how the
    /// scope literal and the "no token for a refused URL" assertions are made.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => Json(HttpStatusCode.OK, """{"value":[]}""");

        public List<string> RequestUrls { get; } = [];
        public List<string?> BearerValues { get; } = [];
        public List<string> TokenRequestBodies { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                TokenRequestBodies.Add(request.Content == null
                    ? ""
                    : await request.Content.ReadAsStringAsync(cancellationToken));
                return Json(HttpStatusCode.OK, """{"access_token":"test-token","expires_in":3600}""");
            }

            RequestUrls.Add(request.RequestUri.OriginalString);
            BearerValues.Add(request.Headers.Authorization?.Parameter);
            RequestBodies.Add(request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return Responder(request);
        }

        public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body) };
    }

    private static (DefenderApiClient Client, StubHandler Handler) CreateClient(
        string? baseUrl = null,
        string? scope = null)
    {
        var handler = new StubHandler();
        var client = new DefenderApiClient(
            "tenant",
            "client",
            "secret",
            baseUrl ?? DefenderApiClient.DefenderBaseUrl,
            scope ?? DefenderApiClient.DefenderTokenScope,
            new HttpClient(handler));
        return (client, handler);
    }

    // ---- T2: the token audience is the legacy hostname ---------------------------------------

    [Fact]
    public void DefenderTokenScope_IsTheLegacyAudienceAndNotTheRequestHost()
    {
        // Cheapest broken implementation that still passes: none - the two constants would have to
        // be wrong in exactly the way Learn warns about, which is a silent 403 in production and is
        // undetectable from the endpoint URL alone.
        Assert.Equal("https://api.securitycenter.microsoft.com/.default", DefenderApiClient.DefenderTokenScope);
        Assert.Equal("https://api.security.microsoft.com", DefenderApiClient.DefenderBaseUrl);
        Assert.DoesNotContain("api.securitycenter", DefenderApiClient.DefenderBaseUrl);
    }

    [Fact]
    public async Task TokenRequest_SendsTheScopeTheClientWasConstructedWith()
    {
        // Cheapest broken implementation that still passes: hardcoding the Defender scope. Defeated
        // by constructing with a scope that is neither of the two real ones - a client that ignores
        // its constructor argument cannot produce this value.
        var (client, handler) = CreateClient(scope: "https://example.invalid/.default");

        await client.GetWithStatusAsync("/api/machines");

        var body = Assert.Single(handler.TokenRequestBodies);
        Assert.Contains("scope=" + Uri.EscapeDataString("https://example.invalid/.default"), body);
        Assert.DoesNotContain("graph.microsoft.com", body);
    }

    [Fact]
    public async Task TokenRequest_ForTheDefenderInstance_AsksForTheLegacyAudience()
    {
        var (client, handler) = CreateClient();

        await client.GetWithStatusAsync("/api/machines");

        var body = Assert.Single(handler.TokenRequestBodies);
        Assert.Contains("scope=" + Uri.EscapeDataString(DefenderApiClient.DefenderTokenScope), body);
    }

    [Fact]
    public async Task TokenIsCachedAcrossRequests()
    {
        // Cheapest broken implementation that still passes: never calling the token endpoint at
        // all. Defeated by asserting exactly one token request, not zero.
        var (client, handler) = CreateClient();

        await client.GetWithStatusAsync("/api/machines");
        await client.GetWithStatusAsync("/api/machines");

        Assert.Equal(2, handler.RequestUrls.Count);
        Assert.Single(handler.TokenRequestBodies);
        Assert.All(handler.BearerValues, bearer => Assert.Equal("test-token", bearer));
    }

    // ---- Relative paths and absolute continuation URLs ---------------------------------------

    [Fact]
    public async Task RelativePath_IsResolvedAgainstTheConfiguredBaseUrl()
    {
        var (client, handler) = CreateClient();

        await client.GetWithStatusAsync("/api/machines?$top=5");

        Assert.Equal("https://api.security.microsoft.com/api/machines?$top=5", Assert.Single(handler.RequestUrls));
    }

    [Fact]
    public async Task AbsoluteContinuationUrl_IsSentUnmodified()
    {
        // The capability the shared GraphTokenClient does not have: it prepends its base URL to
        // whatever it is handed, so an absolute nextLink becomes a broken request. Cheapest broken
        // implementation that still passes: none that also passes the relative-path test above -
        // a client that always concatenates produces a doubled host here, and a client that never
        // concatenates fails that one.
        var (client, handler) = CreateClient();
        const string NextLink =
            "https://api.security.microsoft.com/api/machines?$top=2&$skiptoken=a%2Bb%2Fc%3D%3D&$filter=x%20eq%20'y'";

        await client.GetWithStatusAsync(NextLink);

        Assert.Equal(NextLink, Assert.Single(handler.RequestUrls));
    }

    [Fact]
    public async Task AbsoluteUrlOnAnotherHost_IsRefusedAndNoTokenIsEverAcquired()
    {
        // The absolute-URL path exists so a continuation link out of a RESPONSE BODY can be
        // followed. Following an arbitrary host from a response body would hand this app
        // registration's bearer token to whatever host that body named. Cheapest broken
        // implementation that still passes: refusing but acquiring the token first - defeated by
        // asserting no token request happened either.
        var (client, handler) = CreateClient();

        var result = await client.GetWithStatusAsync("https://attacker.example/api/machines?$top=2");

        Assert.Null(result.Document);
        Assert.Empty(handler.RequestUrls);
        Assert.Empty(handler.TokenRequestBodies);
        Assert.Contains("different host", result.SafeError);
    }

    [Fact]
    public async Task AbsoluteUrlThatIsNotHttps_IsRefused()
    {
        var (client, handler) = CreateClient();

        var result = await client.GetWithStatusAsync("http://api.security.microsoft.com/api/machines");

        Assert.Null(result.Document);
        Assert.Empty(handler.RequestUrls);
        Assert.Empty(handler.TokenRequestBodies);
        Assert.Contains("HTTPS", result.SafeError);
    }

    // ---- Status, sanitized error, and the timeout that is not a status ------------------------

    [Fact]
    public async Task NonSuccess_ReturnsTheStatusAndOnlyTheSanitizedError()
    {
        // This is the whole reason the class exists: GraphTokenClient.PostAsync collapses every
        // non-success status to null. Cheapest broken implementation that still passes: returning
        // the raw body as SafeError - defeated by asserting the body's own private field is absent.
        var (client, handler) = CreateClient();
        handler.Responder = _ => StubHandler.Json(HttpStatusCode.Forbidden, """
            {"error":{"code":"Forbidden","message":"Insufficient privileges","innerError":{"request-id":"secret-correlation-id"}}}
            """);

        var result = await client.GetWithStatusAsync("/api/machines");

        Assert.Null(result.Document);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.False(result.TimedOut);
        Assert.Equal("Forbidden: Insufficient privileges", result.SafeError);
        Assert.DoesNotContain("secret-correlation-id", result.SafeError);
    }

    [Fact]
    public async Task ClientSideTimeout_IsADistinctResultAndNotAnException()
    {
        // A timeout arrives as TaskCanceledException, not as a status code. Cheapest broken
        // implementation that still passes: letting it escape - the test would fail with the
        // exception rather than an assertion, which is the point.
        var (client, handler) = CreateClient();
        handler.Responder = _ => throw new TaskCanceledException("timeout", new TimeoutException());

        var result = await client.GetWithStatusAsync("/api/machines");

        Assert.Null(result.Document);
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task ServiceIssued408_IsNotReportedAsAClientSideTimeout()
    {
        // Distinctness: folding a client-side timeout onto the 408 status would make these two
        // indistinguishable, and T7 requires the timeout to be a reason nothing else collapses into.
        var (client, handler) = CreateClient();
        handler.Responder = _ => StubHandler.Json(HttpStatusCode.RequestTimeout, """{"error":{"code":"Timeout"}}""");

        var result = await client.GetWithStatusAsync("/api/machines");

        Assert.Equal(HttpStatusCode.RequestTimeout, result.StatusCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task UnparseableSuccessBody_IsAFailureAndNeverEchoesTheBody()
    {
        var (client, handler) = CreateClient();
        handler.Responder = _ => StubHandler.Json(HttpStatusCode.OK, "<html>sign in please</html>");

        var result = await client.GetWithStatusAsync("/api/machines");

        Assert.Null(result.Document);
        Assert.Contains("could not be parsed", result.SafeError);
        Assert.DoesNotContain("sign in please", result.SafeError);
    }

    [Fact]
    public async Task SuccessfulGet_ReturnsTheParsedDocument()
    {
        var (client, handler) = CreateClient();
        handler.Responder = _ => StubHandler.Json(HttpStatusCode.OK, """{"value":[{"id":"m1"}]}""");

        var result = await client.GetWithStatusAsync("/api/machines");

        Assert.NotNull(result.Document);
        using var document = result.Document;
        Assert.Equal("m1", document!.RootElement.GetProperty("value")[0].GetProperty("id").GetString());
        Assert.Null(result.SafeError);
        Assert.False(result.TimedOut);
    }

    // ---- POST, which is why this class is parameterised rather than Defender-only --------------

    [Fact]
    public async Task PostWithStatus_SendsTheBodyAndReturnsTheParsedDocument()
    {
        var (client, handler) = CreateClient(
            baseUrl: DefenderApiClient.GraphBaseUrl,
            scope: DefenderApiClient.GraphTokenScope);
        handler.Responder = _ => StubHandler.Json(HttpStatusCode.OK, """{"results":[]}""");

        var result = await client.PostWithStatusAsync("/security/runHuntingQuery", new { Query = "DeviceInfo" });

        Assert.Equal("https://graph.microsoft.com/v1.0/security/runHuntingQuery", Assert.Single(handler.RequestUrls));
        Assert.Contains("DeviceInfo", Assert.Single(handler.RequestBodies));
        Assert.NotNull(result.Document);
        result.Document!.Dispose();
    }

    [Fact]
    public async Task PostWithStatus_DoesNotCollapseNonSuccessToNothing()
    {
        // GraphTokenClient.PostAsync returns a bare JsonDocument? and discards both the status and
        // the body, so 403 and 429 are the same value against it. Cheapest broken implementation
        // that still passes: delegating to that method - defeated because the status would be gone.
        var (client, handler) = CreateClient(
            baseUrl: DefenderApiClient.GraphBaseUrl,
            scope: DefenderApiClient.GraphTokenScope);
        handler.Responder = _ => StubHandler.Json(HttpStatusCode.TooManyRequests, """
            {"error":{"code":"TooManyRequests","message":"CPU quota exhausted"}}
            """);

        var result = await client.PostWithStatusAsync("/security/runHuntingQuery", new { Query = "DeviceInfo" });

        Assert.Equal(HttpStatusCode.TooManyRequests, result.StatusCode);
        Assert.Equal("TooManyRequests: CPU quota exhausted", result.SafeError);
    }

    [Fact]
    public async Task PostTimeout_IsADistinctResultToo()
    {
        var (client, handler) = CreateClient(
            baseUrl: DefenderApiClient.GraphBaseUrl,
            scope: DefenderApiClient.GraphTokenScope);
        handler.Responder = _ => throw new TaskCanceledException("timeout", new TimeoutException());

        var result = await client.PostWithStatusAsync("/security/runHuntingQuery", new { Query = "DeviceInfo" });

        Assert.True(result.TimedOut);
        Assert.Null(result.Document);
    }

    [Fact]
    public void UnconfiguredClient_ReportsItself()
    {
        var client = new DefenderApiClient(
            "tenant", "client", "", DefenderApiClient.DefenderBaseUrl, DefenderApiClient.DefenderTokenScope,
            new HttpClient(new StubHandler()));

        Assert.False(client.IsConfigured);
    }
}
