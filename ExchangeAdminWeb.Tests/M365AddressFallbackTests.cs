using System.Net;
using System.Text;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// sweep-d item 30. Adding a member or owner by a guest's address, or by a member's alias, read "not found": the
/// lookup was <c>/users/{x}</c>, which takes a UPN, and those are not UPNs. On a 404 for an address the user is now
/// looked up by mail or proxy address, and exactly one match is required. A real <see cref="GraphTokenClient"/> over a
/// stub handler, so the URL, the advanced-query header, the status handling and the parsing are all exercised.
/// </summary>
public class M365AddressFallbackTests
{
    private const string Guest = "pat.partner@fabrikam.test";
    private const string GuestId = "6f0c2a10-1111-4222-8333-000000000001";

    private sealed record Seen(string Uri, string? ConsistencyLevel);

    private sealed class Stub(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("/oauth2/v2.0/token", StringComparison.Ordinal))
                return Task.FromResult(Ok("""{"access_token":"t","expires_in":3600}"""));

            Requested.Add(new Seen(Uri.UnescapeDataString(uri),
                request.Headers.TryGetValues("ConsistencyLevel", out var values) ? values.Single() : null));
            return Task.FromResult(respond(uri));
        }
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static bool IsDirect(string uri) => uri.Contains("/v1.0/users/", StringComparison.Ordinal);

    private static (GraphTokenClient Client, Stub Handler) Client(string filterAnswer, HttpStatusCode direct = HttpStatusCode.NotFound,
        HttpStatusCode filterStatus = HttpStatusCode.OK)
    {
        var handler = new Stub(uri => IsDirect(uri)
            ? new HttpResponseMessage(direct)
            : filterStatus == HttpStatusCode.OK ? Ok(filterAnswer) : new HttpResponseMessage(filterStatus));
        return (new GraphTokenClient("tenant", "client", "secret", new HttpClient(handler, disposeHandler: false)), handler);
    }

    [Fact]
    public async Task AnAddressThatIsNotAUpn_IsFoundByMailOrProxyAddress()
    {
        var (client, handler) = Client($$"""{"@odata.count":1,"value":[{"id":"{{GuestId}}"}]}""");

        var (objectId, error) = await M365GroupManagementService.ResolveDirectoryObjectIdAsync(client, Guest);

        Assert.Equal(GuestId, objectId);
        Assert.Empty(error);
        Assert.Equal(2, handler.Requested.Count);
        var fallback = handler.Requested[1];
        Assert.Contains($"$filter=mail eq '{Guest}' or proxyAddresses/any(p:p eq 'smtp:{Guest}')", fallback.Uri, StringComparison.Ordinal);
        Assert.Contains("$count=true", fallback.Uri, StringComparison.Ordinal);
        Assert.Equal("eventual", fallback.ConsistencyLevel);
        Assert.Null(handler.Requested[0].ConsistencyLevel);
    }

    [Fact]
    public async Task TheSameUserMatchedByMailAndByProxy_IsOneMatch()
    {
        var (client, _) = Client($$"""{"value":[{"id":"{{GuestId}}"},{"id":"{{GuestId.ToUpperInvariant()}}"}]}""");

        var (objectId, error) = await M365GroupManagementService.ResolveDirectoryObjectIdAsync(client, Guest);

        Assert.Equal(GuestId, objectId, ignoreCase: true);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("""{"@odata.count":2,"value":[{"id":"a"},{"id":"b"}]}""", 2)]
    [InlineData("""{"value":[{"id":"a"},{"id":"b"},{"id":"c"}]}""", 3)]
    [InlineData("""{"@odata.count":12,"value":[{"id":"a"},{"id":"b"}]}""", 12)]
    public async Task SeveralMatches_AreRefused_NamingTheCount(string answer, int count)
    {
        var (client, _) = Client(answer);

        var (objectId, error) = await M365GroupManagementService.ResolveDirectoryObjectIdAsync(client, Guest);

        Assert.Null(objectId);
        Assert.Contains($"'{Guest}' matches {count} directory users", error, StringComparison.Ordinal);
        Assert.Contains("user principal name", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoMatch_IsNotFound_NamingTheAddress()
    {
        var (client, _) = Client("""{"@odata.count":0,"value":[]}""");

        var (objectId, error) = await M365GroupManagementService.ResolveDirectoryObjectIdAsync(client, Guest);

        Assert.Null(objectId);
        Assert.Contains($"'{Guest}' was not found", error, StringComparison.Ordinal);
        Assert.Contains("proxy address", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A403OnTheFallback_KeepsTheNotPermittedMessage()
    {
        var (client, _) = Client("", filterStatus: HttpStatusCode.Forbidden);

        var (objectId, error) = await M365GroupManagementService.ResolveDirectoryObjectIdAsync(client, Guest);

        Assert.Null(objectId);
        Assert.StartsWith($"Not permitted to look up '{Guest}'", error, StringComparison.Ordinal);
        Assert.Contains("User.Read.All", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A403OnTheDirectRead_KeepsItsMessage_AndAsksNothingMore()
    {
        var (client, handler) = Client("", direct: HttpStatusCode.Forbidden);

        var (objectId, error) = await M365GroupManagementService.ResolveDirectoryObjectIdAsync(client, Guest);

        Assert.Null(objectId);
        Assert.StartsWith($"Not permitted to look up '{Guest}'", error, StringComparison.Ordinal);
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task ANameThatIsNotAnAddress_IsNotLookedUpByMail()
    {
        var (client, handler) = Client($$"""{"value":[{"id":"{{GuestId}}"}]}""");

        var (objectId, error) = await M365GroupManagementService.ResolveDirectoryObjectIdAsync(client, "ppartner");

        Assert.Null(objectId);
        Assert.Contains("was not found", error, StringComparison.Ordinal);
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task AQuoteInTheAddress_IsEscapedForOData()
    {
        var (client, handler) = Client("""{"value":[]}""");

        await M365GroupManagementService.ResolveDirectoryObjectIdAsync(client, "o'brien@fabrikam.test");

        Assert.Contains("mail eq 'o''brien@fabrikam.test'", handler.Requested[1].Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFoundUserIsNotLookedUpAgain()
    {
        var handler = new Stub(_ => Ok($$"""{"id":"{{GuestId}}"}"""));
        var client = new GraphTokenClient("tenant", "client", "secret", new HttpClient(handler, disposeHandler: false));

        var (objectId, _) = await M365GroupManagementService.ResolveDirectoryObjectIdAsync(client, "pat@contoso.test");

        Assert.Equal(GuestId, objectId);
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task TheHeaderOverload_RefusesToReplaceTheClientsOwnAuthorization()
    {
        var (client, handler) = Client("{}");

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetWithStatusAsync(
            "/users", new Dictionary<string, string> { ["authorization"] = "Bearer stolen" }, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requested);
    }
}
