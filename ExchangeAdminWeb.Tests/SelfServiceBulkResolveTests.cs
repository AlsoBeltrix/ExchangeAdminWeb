using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.SelfServiceGroups;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/GroupBulkActions-Plan.md S5, AC7/AC9: SelfServiceGroupService.ResolveBatchAsync
/// through the seamed harness (credentials, the user query and the group probe are overridden,
/// nothing else). The contract under test: the caller must be a SID; no credentials or a
/// failing query mark EVERY line NotAttempted - never NotFound; groups never resolve here even
/// when the query returns one; and a miss that names a group gets the scope-rule reason while a
/// failed probe keeps the generic one.
/// </summary>
public class SelfServiceBulkResolveTests : IDisposable
{
    private const string CallerSid = "S-1-5-21-8915387-325452579-1788637320-1001";
    private readonly string _tempDir;

    public SelfServiceBulkResolveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ssgbulk_test_{Guid.NewGuid():N}");
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

    [Theory]
    [InlineData("")]
    [InlineData("CN=Someone,DC=x")]
    [InlineData("jdoe")]
    public async Task ResolveBatchAsync_RejectsNonSidCaller(string caller)
    {
        var service = CreateService(("svc", "pw", "ANALOG"), _ => [], _ => []);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ResolveBatchAsync(caller, Lines("a")));
        Assert.Equal(0, service.QueryCalls);
    }

    [Fact]
    public async Task ResolveBatchAsync_NoCredentials_EveryLineNotAttempted()
    {
        var service = CreateService(null, _ => throw new InvalidOperationException("must not be called"), _ => []);

        var results = await service.ResolveBatchAsync(CallerSid, Lines("a", "b"));

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(BulkIdentityList.Status.NotAttempted, r.Status));
        Assert.All(results, r => Assert.Contains("AD credentials unavailable", r.Reason));
        Assert.Equal(0, service.QueryCalls);
    }

    [Fact]
    public async Task ResolveBatchAsync_QueryThrows_EveryLineNotAttempted()
    {
        var service = CreateService(("svc", "pw", "ANALOG"), _ => throw new InvalidOperationException("LDAP down"), _ => []);

        var results = await service.ResolveBatchAsync(CallerSid, Lines("a", "b"));

        Assert.All(results, r => Assert.Equal(BulkIdentityList.Status.NotAttempted, r.Status));
        Assert.All(results, r => Assert.Contains("LDAP down", r.Reason));
        Assert.Equal(0, service.ProbeCalls);
    }

    [Fact]
    public async Task ResolveBatchAsync_UserResolves_GroupCandidateNeverDoes()
    {
        var user = new BulkIdentityList.Candidate("CN=J,DC=ad,DC=analog,DC=com", "user", "Jane Doe", "Jane Doe", "jdoe@analog.com", "jdoe", null, "g1");
        // A group the query should never return, but if it did (or a user-class oddity), the
        // matcher must still refuse it: self-service never adds a group (nesting plan D1).
        var group = new BulkIdentityList.Candidate("CN=Ops,DC=ad,DC=analog,DC=com", "group", "Ops", null, null, "ops", null, "g2");
        var service = CreateService(("svc", "pw", "ANALOG"), _ => [user, group], _ => []);

        var results = await service.ResolveBatchAsync(CallerSid, Lines("jdoe", "ops"));

        Assert.Equal(BulkIdentityList.Status.Resolved, results[0].Status);
        Assert.Same(user, results[0].Match);
        Assert.Equal(BulkIdentityList.Status.NotFound, results[1].Status);
    }

    [Fact]
    public async Task ResolveBatchAsync_MissNamingAGroup_GetsTheScopeRuleReason()
    {
        var service = CreateService(("svc", "pw", "ANALOG"), _ => [], misses => misses.Where(m => m.Text == "ExchangeWebAdmins").Select(m => m.Text).ToList());

        var results = await service.ResolveBatchAsync(CallerSid, Lines("ExchangeWebAdmins", "typo"));

        Assert.Equal(1, service.ProbeCalls);
        Assert.Equal(BulkIdentityList.Status.NotFound, results[0].Status);
        Assert.Equal(SelfServiceGroupService.ComposeMemberNotFoundMessage("ExchangeWebAdmins", MembershipOperation.Add, identityIsGroup: true), results[0].Reason);
        Assert.Contains("Only users can be added here", results[0].Reason);
        Assert.Equal(BulkIdentityList.Status.NotFound, results[1].Status);
        Assert.DoesNotContain("is a group", results[1].Reason);
    }

    [Fact]
    public async Task ResolveBatchAsync_ProbeThrows_KeepsGenericReasons()
    {
        var service = CreateService(("svc", "pw", "ANALOG"), _ => [], _ => throw new InvalidOperationException("probe down"));

        var results = await service.ResolveBatchAsync(CallerSid, Lines("something"));

        var r = Assert.Single(results);
        Assert.Equal(BulkIdentityList.Status.NotFound, r.Status);
        Assert.DoesNotContain("is a group", r.Reason);
        Assert.DoesNotContain("probe down", r.Reason);
    }

    [Fact]
    public async Task ResolveBatchAsync_NoMisses_SkipsTheProbe()
    {
        var user = new BulkIdentityList.Candidate("CN=J,DC=x", "user", null, null, "jdoe@analog.com", "jdoe", null, "g1");
        var service = CreateService(("svc", "pw", "ANALOG"), _ => [user], _ => throw new InvalidOperationException("must not be called"));

        var results = await service.ResolveBatchAsync(CallerSid, Lines("jdoe"));

        Assert.Equal(BulkIdentityList.Status.Resolved, Assert.Single(results).Status);
        Assert.Equal(0, service.ProbeCalls);
    }

    // ---- harness ------------------------------------------------------------------------------

    private SeamedService CreateService(
        (string username, string password, string domain)? creds,
        Func<IReadOnlyList<BulkIdentityList.Line>, IReadOnlyList<BulkIdentityList.Candidate>> query,
        Func<IReadOnlyList<BulkIdentityList.Line>, IReadOnlyList<string>> probe)
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
        sectionAccessRepo.SaveAll(new Dictionary<string, string[]> { ["SelfServiceGroups"] = ["S-1-5-21-1-2-3-500"] });
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

        return new SeamedService(moduleCredentials, pp, servicers, NullLogger<SelfServiceGroupService>.Instance)
        {
            Creds = creds,
            Query = query,
            Probe = probe,
        };
    }

    private sealed class SeamedService : SelfServiceGroupService
    {
        public SeamedService(ModuleCredentialService cred, ProtectedPrincipalService pp,
            ProtectedPrincipalServicerService servicers,
            Microsoft.Extensions.Logging.ILogger<SelfServiceGroupService> logger)
            : base(cred, pp, servicers, logger)
        { }

        public (string username, string password, string domain)? Creds { get; init; }
        public required Func<IReadOnlyList<BulkIdentityList.Line>, IReadOnlyList<BulkIdentityList.Candidate>> Query { get; init; }
        public required Func<IReadOnlyList<BulkIdentityList.Line>, IReadOnlyList<string>> Probe { get; init; }
        public int QueryCalls { get; private set; }
        public int ProbeCalls { get; private set; }

        internal override Task<(string username, string password, string domain)?> GetCredentialsAsync(string purpose)
            => Task.FromResult(Creds);

        internal override IReadOnlyList<BulkIdentityList.Candidate> QueryBatchCandidates(
            (string username, string password, string domain) creds, IReadOnlyList<BulkIdentityList.Line> kept)
        {
            QueryCalls++;
            return Query(kept);
        }

        internal override IReadOnlyList<string> ProbeGroupIdentities(
            (string username, string password, string domain) creds, IReadOnlyList<BulkIdentityList.Line> misses)
        {
            ProbeCalls++;
            return Probe(misses);
        }
    }
}
