using System.Net;
using System.Text;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A17: a Graph collection read that failed partway through returned the rows
/// it had already collected, indistinguishable from a complete list.
/// </summary>
/// <remarks>
/// Behaviour, not a source scan. A routing stub handler stands in for the whole network -
/// Delinea's token and secret endpoints, the Graph token endpoint, then the paged Graph
/// collection - so the real service, the real <see cref="GraphTokenClient"/> and the real
/// paging loop all run. The second page is the one that fails.
///
/// A truncated list is the dangerous shape here, not an outright failure: named locations
/// gate conditional access, so a missing entry reads as "that network is trusted nowhere".
/// </remarks>
public sealed class GraphPagingTruncationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gpt-" + Guid.NewGuid().ToString("N"));

    public GraphPagingTruncationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Serves Delinea and Graph from one place, routed by path.</summary>
    private sealed class RoutingHandler(bool secondPageFails) : HttpMessageHandler
    {
        public int SecondPageRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri!.Query;

            if (path.EndsWith("/oauth2/token", StringComparison.Ordinal))
                return Json("""{"access_token":"delinea-token","expires_in":3600}""");

            if (path.Contains("/api/v1/secrets/", StringComparison.Ordinal))
                return Json("""
                    {"items":[
                      {"fieldName":"Tenant ID","itemValue":"tenant"},
                      {"fieldName":"Application ID","itemValue":"client"},
                      {"fieldName":"Client Secret","itemValue":"secret"}
                    ]}
                    """);

            if (path.Contains("/oauth2/v2.0/token", StringComparison.Ordinal))
                return Json("""{"access_token":"graph-token","expires_in":3600}""");

            if (path.Contains("namedLocations", StringComparison.Ordinal))
            {
                if (query.Contains("page2", StringComparison.Ordinal))
                {
                    SecondPageRequests++;
                    return secondPageFails
                        ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        {
                            Content = new StringContent("throttled", Encoding.UTF8, "application/json"),
                        })
                        : Json("""{"value":[{"id":"2","displayName":"Branch office","@odata.type":"#microsoft.graph.ipNamedLocation"}]}""");
                }

                return Json("""
                    {"value":[{"id":"1","displayName":"Head office","@odata.type":"#microsoft.graph.ipNamedLocation"}],
                     "@odata.nextLink":"https://graph.microsoft.com/v1.0/identity/conditionalAccess/namedLocations?page2=true"}
                    """);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private NamedLocationsService Service(RoutingHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://vault.local",
            ["Audit:LogRoot"] = _directory,
        }).Build();

        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_directory);

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, environment,
            TestConfigStore.CreateModuleConfig(_directory), Substitute.For<ILogger<ModuleConfigService>>());
        moduleConfig.SaveModuleConfig("NamedLocations", new Dictionary<string, string> { ["GraphDelineaSecretId"] = "42" });

        var jsonl = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var trace = new OperationTraceService(config, jsonl);
        var extendedLog = new ExtendedLogService(config, environment,
            TestConfigStore.CreateAppSettings(_directory), Substitute.For<ILogger<ExtendedLogService>>());
        // The bootstrap credential seam: without it DelineaService has no API user and returns
        // null for every secret, which would make these tests pass for the wrong reason.
        var delinea = new DelineaService(factory, config, Substitute.For<ILogger<DelineaService>>(),
            extendedLog, trace, _ => ("api-user", "api-key"));

        return new NamedLocationsService(
            Substitute.For<ILogger<NamedLocationsService>>(), moduleConfig, delinea, factory);
    }

    [Fact]
    public async Task ASecondPageFailure_RefusesInsteadOfReturningAShortList()
    {
        // The defect: this logged a warning and returned page one, which the caller and the
        // operator both read as the complete set of named locations.
        using var handler = new RoutingHandler(secondPageFails: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(handler).GetAllAsync());

        Assert.True(handler.SecondPageRequests > 0, "The second page was never requested - this test proves nothing.");
        Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 location", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllPagesSucceeding_StillReturnsEveryLocation()
    {
        // Without this, a fix that always threw would satisfy the test above.
        using var handler = new RoutingHandler(secondPageFails: false);

        var locations = await Service(handler).GetAllAsync();

        Assert.Equal(2, locations.Count);
        Assert.Contains(locations, l => l.DisplayName == "Branch office");
    }
}
