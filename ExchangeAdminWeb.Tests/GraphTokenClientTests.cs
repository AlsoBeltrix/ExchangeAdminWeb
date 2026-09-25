using System.Net;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

public class GraphTokenClientTests
{
    /// <summary>
    /// Serves a canned token from login.microsoftonline.com and a configurable
    /// response for every Graph call.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> GraphResponse { get; set; } =
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"value":[]}""") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"test-token","expires_in":3600}""")
                });
            }

            return Task.FromResult(GraphResponse());
        }
    }

    private static (GraphTokenClient Client, StubHandler Handler) CreateClient()
    {
        var handler = new StubHandler();
        return (new GraphTokenClient("tenant", "client", "secret", new HttpClient(handler)), handler);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetWithStatusAsync_NonSuccess_ReturnsNullDocumentWithStatus(HttpStatusCode status)
    {
        var (client, handler) = CreateClient();
        handler.GraphResponse = () => new HttpResponseMessage(status);

        var (document, returnedStatus) = await client.GetWithStatusAsync("/users/x/authentication/methods");

        Assert.Null(document);
        Assert.Equal(status, returnedStatus);
    }

    [Fact]
    public async Task GetWithStatusAsync_Success_ReturnsDocument()
    {
        var (client, _) = CreateClient();

        var (document, status) = await client.GetWithStatusAsync("/users/x/authentication/methods");

        Assert.NotNull(document);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, document.RootElement.GetProperty("value").GetArrayLength());
        document.Dispose();
    }

    [Fact]
    public async Task GetAsync_NonSuccess_StillReturnsNull_BackCompat()
    {
        var (client, handler) = CreateClient();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var document = await client.GetAsync("/groups");

        Assert.Null(document);
    }

    [Fact]
    public async Task PatchWithStatusAsync_Success_ReturnsOk()
    {
        var (client, handler) = CreateClient();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NoContent);

        var (ok, status, safeError) = await client.PatchWithStatusAsync("/users/x", new { accountEnabled = false });

        Assert.True(ok);
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Null(safeError);
    }

    [Fact]
    public async Task PatchWithStatusAsync_NonSuccess_SurfacesStatusAndSanitizedGraphError()
    {
        var (client, handler) = CreateClient();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""
                {"error":{"code":"Request_BadRequest","message":"Unable to update the specified properties for on-premises mastered Directory Synced objects."}}
                """)
        };

        var (ok, status, safeError) = await client.PatchWithStatusAsync("/users/x", new { accountEnabled = false });

        Assert.False(ok);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(safeError);
        Assert.Contains("Request_BadRequest", safeError);
        Assert.Contains("on-premises mastered", safeError);
    }

    [Fact]
    public async Task PatchAsync_BackCompat_ReturnsBool()
    {
        var (client, handler) = CreateClient();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var ok = await client.PatchAsync("/users/x", new { accountEnabled = false });

        Assert.False(ok);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"something\":\"else\"}")]
    public void ExtractGraphError_UnusableBody_ReturnsNull(string? body)
    {
        Assert.Null(GraphTokenClient.ExtractGraphError(body));
    }

    [Fact]
    public async Task DeleteWithStatusAsync_Success_ReturnsOk()
    {
        var (client, handler) = CreateClient();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NoContent);

        var (ok, status, safeError) = await client.DeleteWithStatusAsync("/deviceManagement/managedDevices/x");

        Assert.True(ok);
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Null(safeError);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task DeleteWithStatusAsync_NonSuccess_SurfacesDistinctStatus(HttpStatusCode status)
    {
        var (client, handler) = CreateClient();
        handler.GraphResponse = () => new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"error":{"code":"SomeError","message":"detail"}}""")
        };

        var (ok, returnedStatus, safeError) = await client.DeleteWithStatusAsync("/deviceManagement/managedDevices/x");

        Assert.False(ok);
        Assert.Equal(status, returnedStatus);
        Assert.NotNull(safeError);
        Assert.Contains("SomeError", safeError);
    }

    [Fact]
    public async Task DeleteWithStatusAsync_NonJsonErrorBody_SafeErrorIsNull()
    {
        var (client, handler) = CreateClient();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("<html>not json</html>")
        };

        var (ok, _, safeError) = await client.DeleteWithStatusAsync("/deviceManagement/managedDevices/x");

        Assert.False(ok);
        Assert.Null(safeError);
    }

    [Fact]
    public async Task DeleteAsync_BackCompat_ReturnsBool_ExistingBehaviorUnchanged()
    {
        var (client, handler) = CreateClient();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var ok = await client.DeleteAsync("/deviceManagement/managedDevices/x");

        Assert.False(ok);
    }

    [Fact]
    public async Task PostNoContentWithStatusAsync_Success_ReturnsOk()
    {
        var (client, handler) = CreateClient();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NoContent);

        var (ok, status, safeError) = await client.PostNoContentWithStatusAsync("/deviceManagement/managedDevices/x/retire");

        Assert.True(ok);
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Null(safeError);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task PostNoContentWithStatusAsync_NonSuccess_SurfacesDistinctStatus(HttpStatusCode status)
    {
        var (client, handler) = CreateClient();
        handler.GraphResponse = () => new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"error":{"code":"SomeError","message":"detail"}}""")
        };

        var (ok, returnedStatus, safeError) = await client.PostNoContentWithStatusAsync("/deviceManagement/managedDevices/x/wipe", new { keepUserData = false });

        Assert.False(ok);
        Assert.Equal(status, returnedStatus);
        Assert.NotNull(safeError);
        Assert.Contains("SomeError", safeError);
    }

    [Fact]
    public async Task PostNoContentWithStatusAsync_NonJsonErrorBody_SafeErrorIsNull()
    {
        var (client, handler) = CreateClient();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("<html>not json</html>")
        };

        var (ok, _, safeError) = await client.PostNoContentWithStatusAsync("/deviceManagement/managedDevices/x/retire");

        Assert.False(ok);
        Assert.Null(safeError);
    }

    [Fact]
    public async Task PostNoContentAsync_BackCompat_ReturnsBool_ExistingBehaviorUnchanged()
    {
        var (client, handler) = CreateClient();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var ok = await client.PostNoContentAsync("/deviceManagement/managedDevices/x/retire");

        Assert.False(ok);
    }

    // ---- Absolute @odata.nextLink support (docs/RiskyUsersCompleteResults-Plan.md S1) ----
    //
    // Graph returns @odata.nextLink absolute, and this client prepended its base URL to whatever
    // it was handed, so no caller could follow one. That is why RiskyUsers stopped at 500 rows and
    // a High-risk account was missing from the module while visible in the Entra portal.
    //
    // The capability is deliberately narrow, because following a URL out of a response body while
    // attaching a bearer token is an exfiltration route. The tests below are the guard, not the
    // feature: each one is a way the token could have left the tenant, or a way an existing caller
    // could have been silently widened beyond /v1.0.

    /// <summary>Captures every request URI reached, and whether any carried an Authorization header.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> GraphRequests { get; } = [];
        public List<Uri> AuthenticatedRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"test-token","expires_in":3600}""")
                });
            }

            GraphRequests.Add(request.RequestUri);
            if (request.Headers.Authorization != null) AuthenticatedRequests.Add(request.RequestUri);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":[]}""")
            });
        }
    }

    private static (GraphTokenClient Client, RecordingHandler Handler) CreateRecordingClient()
    {
        var handler = new RecordingHandler();
        return (new GraphTokenClient("tenant", "client", "secret", new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task GetWithStatusAsync_RelativePath_StillGoesToTheV1BaseUnchanged()
    {
        var (client, handler) = CreateRecordingClient();

        await client.GetWithStatusAsync("/identityProtection/riskyUsers?$top=500");

        Assert.Equal(
            "https://graph.microsoft.com/v1.0/identityProtection/riskyUsers?$top=500",
            Assert.Single(handler.GraphRequests).OriginalString);
    }

    /// <summary>
    /// The continuation token must reach Graph exactly as Graph wrote it. A skiptoken that has
    /// been re-encoded is not the token the service issued, and paging silently restarts or skips.
    /// </summary>
    [Fact]
    public async Task GetWithStatusAsync_AbsoluteGraphUrl_IsSentByteForByte()
    {
        var (client, handler) = CreateRecordingClient();
        const string nextLink =
            "https://graph.microsoft.com/v1.0/identityProtection/riskyUsers?$skiptoken=X%2fY%2bZ%3d%3d&$top=500";

        await client.GetWithStatusAsync(nextLink);

        Assert.Equal(nextLink, Assert.Single(handler.GraphRequests).OriginalString);
    }

    [Theory]
    // A different host entirely.
    [InlineData("https://evil.test/v1.0/users")]
    // The lookalike an EndsWith or Contains check would wave through.
    [InlineData("https://graph.microsoft.com.evil.test/v1.0/users")]
    // Right host, wrong scheme - a bearer token must not travel in clear text.
    [InlineData("http://graph.microsoft.com/v1.0/users")]
    // Right host and scheme, but outside /v1.0. This client is confined to v1.0 by construction
    // and accepting /beta would widen every existing caller's reach, not just the new one.
    [InlineData("https://graph.microsoft.com/beta/users")]
    // Credentials in the authority - the userinfo is not the host, and must not be read as one.
    [InlineData("https://graph.microsoft.com@evil.test/v1.0/users")]
    // Segment-boundary cases. A bare StartsWith("/v1.0") waves both of these through, and a
    // codex review found exactly that: the guard was weaker than the comment claimed.
    [InlineData("https://graph.microsoft.com/v1.0beta/users")]
    [InlineData("https://graph.microsoft.com/v1.0.evil/users")]
    public async Task GetWithStatusAsync_RefusesTheUrlAndNeverMintsAToken(string url)
    {
        var (client, handler) = CreateRecordingClient();

        var (document, status) = await client.GetWithStatusAsync(url);

        Assert.Null(document);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(handler.GraphRequests);
        Assert.Empty(handler.AuthenticatedRequests);
    }

    [Fact]
    public async Task GetWithStatusAsync_RefusesAPathThatIsNeitherAbsoluteNorRooted()
    {
        var (client, handler) = CreateRecordingClient();

        var (_, status) = await client.GetWithStatusAsync("identityProtection/riskyUsers");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(handler.GraphRequests);
    }
}
