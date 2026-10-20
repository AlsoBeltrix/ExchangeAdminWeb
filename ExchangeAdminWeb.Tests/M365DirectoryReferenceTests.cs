using System.Net;
using System.Text;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A10: adding a member or owner built
/// <c>directoryObjects/{identity}</c> from whatever the operator typed. That slot needs an
/// object id, so a UPN or email produced a reference Graph could not resolve - and the
/// failure message blamed the app's Graph permissions for a malformed request.
/// </summary>
/// <remarks>
/// Behaviour. The resolution runs against a real <see cref="GraphTokenClient"/> over a stub
/// handler, so the real URL construction, status handling and JSON parsing are exercised.
///
/// What is NOT covered: the add itself. Reaching it means passing the protected-principal
/// gate, which resolves against on-prem AD, so driving <c>AddMemberAsync</c> end to end needs
/// a fake resolver this file does not stand up. The call site is a source scan below, and the
/// finding says so.
/// </remarks>
public class M365DirectoryReferenceTests
{
    private sealed class Stub(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("/oauth2/v2.0/token", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"access_token":"t","expires_in":3600}"""));

            Requested.Add(uri);
            return Task.FromResult(respond(uri));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        public static HttpResponseMessage Ok(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        public static HttpResponseMessage Status(HttpStatusCode code) => new(code);
    }

    private static (GraphTokenClient Client, Stub Handler) Client(Func<string, HttpResponseMessage> respond)
    {
        var handler = new Stub(respond);
        return (new GraphTokenClient("tenant", "client", "secret", new HttpClient(handler, disposeHandler: false)), handler);
    }

    [Fact]
    public async Task AUpnIsResolvedToItsObjectId()
    {
        // The defect: the UPN went straight into the reference.
        var (client, handler) = Client(_ => Stub.Ok("""{"id":"8c4f1f6e-aaaa-bbbb-cccc-000000000001"}"""));

        var (objectId, error) = await M365GroupManagementService.ResolveDirectoryObjectIdAsync(client, "alex@contoso.com");

        Assert.Equal("8c4f1f6e-aaaa-bbbb-cccc-000000000001", objectId);
        Assert.Empty(error);
        Assert.Contains(handler.Requested, u => u.Contains("/users/alex%40contoso.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnObjectIdIsPassedThroughWithoutALookup()
    {
        // A caller that already holds an id must not pay for a round trip, and must not have
        // its id mangled by being looked up as if it were a name.
        var (client, handler) = Client(_ => Stub.Status(HttpStatusCode.InternalServerError));

        var (objectId, error) = await M365GroupManagementService.ResolveDirectoryObjectIdAsync(
            client, "8c4f1f6e-aaaa-bbbb-cccc-000000000001");

        Assert.Equal("8c4f1f6e-aaaa-bbbb-cccc-000000000001", objectId);
        Assert.Empty(error);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task AnIdentityThatDoesNotExist_IsReportedAsNotFound_NotAsAPermissionProblem()
    {
        // The old message blamed Group.ReadWrite.All for what was a bad identity, which sends
        // whoever reads it to the wrong place entirely.
        var (client, _) = Client(_ => Stub.Status(HttpStatusCode.NotFound));

        var (objectId, error) = await M365GroupManagementService.ResolveDirectoryObjectIdAsync(client, "ghost@contoso.com");

        Assert.Null(objectId);
        Assert.Contains("was not found", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFailedLookup_CarriesItsStatus()
    {
        var (client, _) = Client(_ => Stub.Status(HttpStatusCode.TooManyRequests));

        var (objectId, error) = await M365GroupManagementService.ResolveDirectoryObjectIdAsync(client, "alex@contoso.com");

        Assert.Null(objectId);
        Assert.Contains("429", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADirectoryThatReturnsNoId_IsRefused()
    {
        var (client, _) = Client(_ => Stub.Ok("""{"displayName":"Alex"}"""));

        var (objectId, error) = await M365GroupManagementService.ResolveDirectoryObjectIdAsync(client, "alex@contoso.com");

        Assert.Null(objectId);
        Assert.Contains("no object id", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheAddPathResolvesBeforeBuildingTheReference()
    {
        // Source scan: the add cannot be driven here, so this pins that the reference is built
        // from the resolved id and not from the typed identity.
        var source = RazorSyntax.CommentsBlanked(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Services", "M365GroupManagementService.cs")));

        var resolve = source.IndexOf("ResolveDirectoryObjectIdAsync(client, identity)", StringComparison.Ordinal);
        Assert.True(resolve > 0, "The add path must resolve the identity.");

        var reference = source.IndexOf("directoryObjects/{Uri.EscapeDataString(objectId)}", StringComparison.Ordinal);
        Assert.True(reference > resolve, "The reference must be built from the resolved id.");

        Assert.DoesNotContain("directoryObjects/{Uri.EscapeDataString(identity)}", source, StringComparison.Ordinal);
    }
}
