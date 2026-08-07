using System.Security.Claims;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Protected-principal servicing in Migration.
/// </summary>
/// <remarks>
/// Migration is the only serviced module that operates on a BATCH, so the decision is per target
/// and a single batch can carry a mix: clear targets, excluded targets, and protected-but-serviced
/// targets. The serviced notes come back as a list beside the allowed targets, one per override, so
/// the audit can name WHICH targets were overridden - a batch-level "something was serviced" would
/// not answer that, which is the question an audit exists for.
///
/// The existing MigrationServiceProtectedPrincipalTests assert the PROTECTION decision and the
/// owner's filter-and-report rule; they are left untouched apart from the seam's type, so if
/// servicing turned any of those exclusions into an inclusion they would fail.
/// </remarks>
public sealed class MigrationServicerTests : IDisposable
{
    private const string ServicerSid = "S-1-5-21-1-2-3-4001";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mig-svc-{Guid.NewGuid():N}");

    public MigrationServicerTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task AProtectedTarget_IsExcludedForAnOrdinaryOperator()
    {
        var service = CreateService(servicerGroups: null);

        var (allowed, excluded, notes) = await service.PartitionByProtectionAsync(
            ["vip@contoso.com"], UserIn(ServicerSid));

        Assert.Empty(allowed);
        Assert.Single(excluded);
        Assert.Empty(notes);
    }

    [Fact]
    public async Task AProtectedTarget_IsExcludedForSomeoneOutsideTheServicerGroup()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var (allowed, excluded, notes) = await service.PartitionByProtectionAsync(
            ["vip@contoso.com"], UserIn("S-1-5-21-1-2-3-9999"));

        Assert.Empty(allowed);
        Assert.Single(excluded);
        Assert.Empty(notes);
    }

    [Fact]
    public async Task AnAuthorisedServicer_MigratesTheProtectedTarget_AndSaysWhy()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var (allowed, excluded, notes) = await service.PartitionByProtectionAsync(
            ["vip@contoso.com"], UserIn(ServicerSid));

        Assert.Equal(["vip@contoso.com"], allowed);
        Assert.Empty(excluded);

        var note = Assert.Single(notes);
        // The note must name the authorising group AND the target: a batch override that cannot say
        // which mailbox it covered is not an audit record.
        Assert.Contains(ServicerSid, note, StringComparison.Ordinal);
        Assert.Contains("vip@contoso.com", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("matched rules", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EachOverrideInABatch_IsRecordedSeparately()
    {
        // The per-target shape that is unique to this module: two protected targets serviced in one
        // batch must produce two notes, each naming its own target.
        var service = CreateService(servicerGroups: [ServicerSid]);

        var (allowed, _, notes) = await service.PartitionByProtectionAsync(
            ["vip1@contoso.com", "vip2@contoso.com"], UserIn(ServicerSid));

        Assert.Equal(2, allowed.Count);
        Assert.Equal(2, notes.Count);
        Assert.Contains(notes, n => n.Contains("vip1@contoso.com", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notes, n => n.Contains("vip2@contoso.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AGrantInAnotherModule_ConfersNothingHere()
    {
        // The containment that stops per-module scoping collapsing into a global bypass.
        var service = CreateService(servicerGroups: null, otherModuleServicerGroups: [ServicerSid]);

        var (allowed, excluded, _) = await service.PartitionByProtectionAsync(
            ["vip@contoso.com"], UserIn(ServicerSid));

        Assert.Empty(allowed);
        Assert.Single(excluded);
    }

    [Fact]
    public async Task ANullActingUser_ExcludesEvenWithAServicerGroupConfigured()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var (allowed, excluded, notes) = await service.PartitionByProtectionAsync(
            ["vip@contoso.com"], actingUser: null);

        Assert.Empty(allowed);
        Assert.Single(excluded);
        Assert.Empty(notes);
    }

    [Fact]
    public async Task AnUnresolvableTarget_IsExcludedEvenForAnAuthorisedServicer()
    {
        // Fail-closed outranks servicing: a resolution that did not complete does not know whether
        // the target is protected, so there is no exclusion for a servicer to override.
        var service = CreateService(servicerGroups: [ServicerSid], resolutionUnavailable: true);

        var (allowed, excluded, notes) = await service.PartitionByProtectionAsync(
            ["vip@contoso.com"], UserIn(ServicerSid));

        Assert.Empty(allowed);
        var only = Assert.Single(excluded);
        Assert.Contains("unavailable", only, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(notes);
    }

    [Fact]
    public async Task TheEligibilityFlag_DoesNotService_SoTheOperatorStillSeesTheTargetAsProtected()
    {
        // ApplyProtectionFlagAsync is the DISPLAY path, not a write. An operator holding the grant
        // must still see the target flagged, or the UI would hide from them that they are about to
        // use their override. The override is applied and recorded at the batch write instead.
        var service = CreateService(servicerGroups: [ServicerSid]);
        var result = new MigrationEligibilityResult
        {
            EmailAddress = "vip@contoso.com",
            Status = MigrationStatus.Eligible,
        };

        await service.ApplyProtectionFlagAsync(result);

        Assert.True(result.IsProtected);
        Assert.Contains("protected principal", result.ProtectionNote!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static ClaimsPrincipal UserIn(string groupSid) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ANALOG\\tester"), new Claim(ClaimTypes.Role, groupSid)],
            "TestAuth"));

    private MigrationService CreateService(
        string[]? servicerGroups,
        string[]? otherModuleServicerGroups = null,
        bool resolutionUnavailable = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = _tempDir,
            ["OnPremExchange:ServerUri"] = "https://fake.local/powershell",
        }).Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var catalog = new ModuleCatalog();
        var sectionAccessRepo = new SectionAccessRepository(TestConfigStore.Create(_tempDir));

        var rows = new Dictionary<string, string[]>();
        if (servicerGroups is { Length: > 0 })
            rows[ProtectedPrincipalServicerService.SectionKeyFor("Migration")] = servicerGroups;
        if (otherModuleServicerGroups is { Length: > 0 })
            rows[ProtectedPrincipalServicerService.SectionKeyFor("GroupManagement")] = otherModuleServicerGroups;
        // Always write something, so the store counts as CONFIGURED and the legacy AllowedGroups
        // fallback is out of the picture (see ppsvc-1).
        rows["Migration"] = ["S-1-5-21-1-2-3-500"];
        sectionAccessRepo.SaveAll(rows);

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
        var enablement = new ModuleEnablementService(catalog, env, moduleConfig, TestConfigStore.CreateModuleEnablement(_tempDir), config, NullLogger<ModuleEnablementService>.Instance);
        var exoPool = new ExoConnectionPool(config, moduleConfig, enablement, NullLogger<ExoConnectionPool>.Instance, trace);

        var pp = new ProtectedFakePpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea)
        {
            Unavailable = resolutionUnavailable,
        };

        return new MigrationService(
            config, exoPool, delinea, NullLogger<MigrationService>.Instance, moduleConfig,
            moduleCredentials, trace, pp, servicers);
    }

    /// <summary>Resolves any identity and reports it protected, so the servicer path is reachable.</summary>
    private sealed class ProtectedFakePpService : ProtectedPrincipalService
    {
        public ProtectedFakePpService(IWebHostEnvironment env, IConfiguration config,
            ModuleConfigService moduleConfig, ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
        { }

        /// <summary>When true the resolver cannot answer, which must fail closed ahead of servicing.</summary>
        public bool Unavailable { get; init; }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity)
            => Task.FromResult<(ResolvedDirectoryPrincipal?, ResolutionStatus)>(
                Unavailable
                    ? (null, ResolutionStatus.Unavailable)
                    : (new ResolvedDirectoryPrincipal("AD", identity, identity, identity, identity, null, "guid", null),
                       ResolutionStatus.Resolved));

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
            => Task.FromResult(ProtectedPrincipalResult.Protected("protected for test", "ceo-user-rule"));
    }
}
