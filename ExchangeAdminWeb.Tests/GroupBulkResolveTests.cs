using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/GroupBulkActions-Plan.md S3, AC7/AC10: GroupManagementService.ResolveBatchAsync
/// through the seamed harness (credentials and the directory query are overridden, nothing
/// else). The contract under test: no credentials or a failing query mark EVERY line
/// NotAttempted with the reason - never NotFound - and scripted candidates flow through the
/// pure matcher with groups allowed (the admin module's typed-path scope).
/// </summary>
public class GroupBulkResolveTests : IDisposable
{
    private readonly string _tempDir;

    public GroupBulkResolveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"groupbulk_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { }
    }

    private static IReadOnlyList<BulkIdentityList.Line> Lines(params string[] texts) =>
        texts.Select((t, i) => new BulkIdentityList.Line(i + 1, t)).ToList();

    [Fact]
    public async Task ResolveBatchAsync_NoCredentials_EveryLineNotAttempted()
    {
        var service = CreateService(creds: null, query: _ => throw new InvalidOperationException("must not be called"));

        var results = await service.ResolveBatchAsync(Lines("a", "b"));

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(BulkIdentityList.Status.NotAttempted, r.Status));
        Assert.All(results, r => Assert.Contains("AD credentials unavailable", r.Reason));
        Assert.Equal(0, service.QueryCalls);
    }

    [Fact]
    public async Task ResolveBatchAsync_QueryThrows_EveryLineNotAttempted()
    {
        var service = CreateService(creds: ("svc", "pw", "ANALOG"), query: _ => throw new InvalidOperationException("LDAP server down"));

        var results = await service.ResolveBatchAsync(Lines("a", "b", "c"));

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(BulkIdentityList.Status.NotAttempted, r.Status));
        Assert.All(results, r => Assert.Contains("LDAP server down", r.Reason));
        Assert.DoesNotContain(results, r => r.Status == BulkIdentityList.Status.NotFound);
    }

    [Fact]
    public async Task ResolveBatchAsync_ScriptedCandidates_UserAndGroupResolve()
    {
        var user = new BulkIdentityList.Candidate("CN=J,DC=ad,DC=analog,DC=com", "user", "Jane Doe", "Jane Doe", "jdoe@analog.com", "jdoe", "jane.doe@analog.com", "g1");
        var group = new BulkIdentityList.Candidate("CN=Exchange Web Admins,DC=ad,DC=analog,DC=com", "group", "Exchange Web Admins", null, null, "ExchangeWebAdmins", null, "g2");
        var service = CreateService(creds: ("svc", "pw", "ANALOG"), query: _ => [user, group]);

        var results = await service.ResolveBatchAsync(Lines("jdoe", "Exchange Web Admins", "nobody"));

        Assert.Equal(1, service.QueryCalls);
        Assert.Equal(BulkIdentityList.Status.Resolved, results[0].Status);
        Assert.Same(user, results[0].Match);
        // The admin module resolves groups too, and by Name (gba-3).
        Assert.Equal(BulkIdentityList.Status.Resolved, results[1].Status);
        Assert.Same(group, results[1].Match);
        Assert.Equal(BulkIdentityList.Status.NotFound, results[2].Status);
    }

    [Fact]
    public async Task ResolveBatchAsync_EmptyInput_NeverQueries()
    {
        var service = CreateService(creds: ("svc", "pw", "ANALOG"), query: _ => throw new InvalidOperationException("must not be called"));

        var results = await service.ResolveBatchAsync([]);

        Assert.Empty(results);
        Assert.Equal(0, service.QueryCalls);
    }

    [Fact]
    public void NotAttempted_PrefixesEveryReason()
    {
        var rows = GroupManagementService.NotAttempted(Lines("x", "y"), "because");

        Assert.All(rows, r => Assert.Equal(BulkIdentityList.Status.NotAttempted, r.Status));
        Assert.All(rows, r => Assert.Equal("Not attempted - because", r.Reason));
        Assert.All(rows, r => Assert.Null(r.Match));
    }

    // ---- harness ------------------------------------------------------------------------------

    private SeamedService CreateService(
        (string username, string password, string domain)? creds,
        Func<IReadOnlyList<BulkIdentityList.Line>, IReadOnlyList<BulkIdentityList.Candidate>> query)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = _tempDir,
        }).Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var catalog = new ModuleCatalog();
        var sectionAccessRepo = new SectionAccessRepository(TestConfigStore.Create(_tempDir));
        sectionAccessRepo.SaveAll(new Dictionary<string, string[]> { ["GroupManagement"] = ["S-1-5-21-1-2-3-500"] });
        var sectionAccess = new SectionAccessService(
            config, NullLogger<SectionAccessService>.Instance, env, catalog, sectionAccessRepo);
        var servicers = new ProtectedPrincipalServicerService(
            sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance);

        var moduleConfig = new ModuleConfigService(
            catalog, env, TestConfigStore.CreateModuleConfig(_tempDir), NullLogger<ModuleConfigService>.Instance);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), NullLogger<ExtendedLogService>.Instance);
        var jsonlLog = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, NullLogger<DelineaService>.Instance, extLog, trace);
        var moduleCredentials = new ModuleCredentialService(moduleConfig, delinea, NullLogger<ModuleCredentialService>.Instance);
        var pp = new ProtectedPrincipalService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea, NullLogger<ProtectedPrincipalService>.Instance);

        return new SeamedService(moduleConfig, moduleCredentials, pp, servicers, NullLogger<GroupManagementService>.Instance)
        {
            Creds = creds,
            Query = query,
        };
    }

    private sealed class SeamedService : GroupManagementService
    {
        public SeamedService(ModuleConfigService mc, ModuleCredentialService cred,
            ProtectedPrincipalService pp, ProtectedPrincipalServicerService servicers,
            Microsoft.Extensions.Logging.ILogger<GroupManagementService> logger)
            : base(mc, cred, pp, servicers, logger)
        { }

        public (string username, string password, string domain)? Creds { get; init; }
        public required Func<IReadOnlyList<BulkIdentityList.Line>, IReadOnlyList<BulkIdentityList.Candidate>> Query { get; init; }
        public int QueryCalls { get; private set; }

        internal override Task<(string username, string password, string domain)?> GetCredentialsAsync(string purpose)
            => Task.FromResult(Creds);

        internal override IReadOnlyList<BulkIdentityList.Candidate> QueryBatchCandidates(
            (string username, string password, string domain) creds, IReadOnlyList<BulkIdentityList.Line> kept)
        {
            QueryCalls++;
            return Query(kept);
        }
    }
}
