using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the in-service protected-principal gate on M365 group member/owner writes.
/// The Constitution requires the check "immediately before the write" and forbids
/// relying on UI hiding, so the service must enforce protection independently and fail
/// closed when the protection lookup is unavailable. The stand-in Graph names every user, and
/// the protection service has no on-premises SID source, so it cannot be asked: the write is
/// denied, and the stand-in records that no write was sent.
/// (Plan: docs/M365MemberOwnerManagement-Plan.md, slice 1.)
/// </summary>
public class M365GroupManagementServiceTests : IDisposable
{
    private readonly string _tempDir;

    public M365GroupManagementServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"m365grpmgmt_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { }
    }

    // Builds a service whose ProtectedPrincipalService has no on-premises SID source, so the
    // member's protection cannot be read - which the in-service gate must treat as fail-closed
    // (deny before any Graph write).
    private M365GroupManagementService CreateServiceWithUnavailableResolver()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Audit:LogRoot"] = _tempDir
            })
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, env, TestConfigStore.CreateModuleConfig(_tempDir), Substitute.For<ILogger<ModuleConfigService>>());

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), Substitute.For<ILogger<ExtendedLogService>>());
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var operationTrace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extLog, operationTrace);
        var audit = new AuditService(jsonlLog, operationTrace);
        var protectedPrincipals = new ProtectedPrincipalService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea, Substitute.For<ILogger<ProtectedPrincipalService>>());

        // Real servicer service over a store with no ProtectedServicer row, so it denies.
        // The existing assertions are about the PROTECTION decision and must keep passing
        // unchanged: a servicer path that made one pass would be a refusal turned into an allow.
        var sectionAccess = new SectionAccessService(
            config, Substitute.For<ILogger<SectionAccessService>>(), env, catalog,
            new ExchangeAdminWeb.Services.Storage.SectionAccessRepository(TestConfigStore.Create(_tempDir)));
        var servicers = new ProtectedPrincipalServicerService(
            sectionAccess, Substitute.For<ILogger<ProtectedPrincipalServicerService>>());

        return new M365GroupManagementService(
            moduleConfig, delinea, httpClientFactory, operationTrace, audit, protectedPrincipals, servicers,
            Substitute.For<ILogger<M365GroupManagementService>>())
        {
            GraphClientForTests = () => new GraphTokenClient("tenant", "client", "secret", new HttpClient(_graph, disposeHandler: false)),
        };
    }

    private readonly EveryUserGraph _graph = new();

    /// <summary>A Graph that names every user it is asked about, and records each write it is sent.</summary>
    private sealed class EveryUserGraph : HttpMessageHandler
    {
        public int Writes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = request.RequestUri!.AbsoluteUri.Contains("/oauth2/v2.0/token", StringComparison.Ordinal);
            if (!token && request.Method != HttpMethod.Get)
                Interlocked.Increment(ref Writes);
            var body = token
                ? """{"access_token":"t","expires_in":3600}"""
                : """{"id":"00000000-0000-0000-0000-0000000000aa"}""";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Theory]
    [InlineData("user@contoso.com")]
    [InlineData("sAMName")]
    [InlineData("CONTOSO\\sAMName")]
    public async Task AddMemberAsync_ResolverUnavailable_FailsClosed(string identity)
    {
        var service = CreateServiceWithUnavailableResolver();

        var result = await service.AddMemberAsync("00000000-0000-0000-0000-000000000001", identity, actingUser: null);

        Assert.False(result.Success);
        Assert.Contains("Protection check unavailable", result.Message);
        Assert.Equal(0, _graph.Writes);
    }

    [Theory]
    [InlineData("user@contoso.com")]
    [InlineData("sAMName")]
    [InlineData("CONTOSO\\sAMName")]
    public async Task AddOwnerAsync_ResolverUnavailable_FailsClosed(string identity)
    {
        var service = CreateServiceWithUnavailableResolver();

        var result = await service.AddOwnerAsync("00000000-0000-0000-0000-000000000001", identity, actingUser: null);

        Assert.False(result.Success);
        Assert.Contains("Protection check unavailable", result.Message);
        Assert.Equal(0, _graph.Writes);
    }

    [Theory]
    [InlineData("user@contoso.com")]
    [InlineData("sAMName")]
    [InlineData("CONTOSO\\sAMName")]
    public async Task RemoveMemberAsync_ResolverUnavailable_FailsClosed(string identity)
    {
        var service = CreateServiceWithUnavailableResolver();

        var result = await service.RemoveMemberAsync(
            "00000000-0000-0000-0000-000000000001",
            "00000000-0000-0000-0000-000000000002",
            identity,
            actingUser: null);

        Assert.False(result.Success);
        Assert.Contains("Protection check unavailable", result.Message);
        Assert.Equal(0, _graph.Writes);
    }

    [Theory]
    [InlineData("user@contoso.com")]
    [InlineData("sAMName")]
    [InlineData("CONTOSO\\sAMName")]
    public async Task RemoveOwnerAsync_ResolverUnavailable_FailsClosed(string identity)
    {
        var service = CreateServiceWithUnavailableResolver();

        var result = await service.RemoveOwnerAsync(
            "00000000-0000-0000-0000-000000000001",
            "00000000-0000-0000-0000-000000000002",
            identity,
            actingUser: null);

        Assert.False(result.Success);
        Assert.Contains("Protection check unavailable", result.Message);
        Assert.Equal(0, _graph.Writes);
    }
}
