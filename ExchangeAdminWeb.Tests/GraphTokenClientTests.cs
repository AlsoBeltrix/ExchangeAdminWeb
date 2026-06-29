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
}
