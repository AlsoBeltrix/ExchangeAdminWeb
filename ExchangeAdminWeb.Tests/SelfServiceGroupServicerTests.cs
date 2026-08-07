using System.Security.Claims;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.SelfServiceGroups;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Protected-principal servicing in Self-Service Groups.
/// </summary>
/// <remarks>
/// Drives CheckMemberProtectedAsync directly. ChangeMemberAsync fetches this module's AD credential
/// and resolves the member against a live directory BEFORE reaching the gate, so no test can get
/// there through the public method; the gate is exposed internally for exactly this reason.
///
/// This module's ordinary user is a group owner, not an administrator, so the grant will rarely
/// match here - which is the correct behaviour rather than a reason to skip it. An IT operator who
/// holds the grant and also owns a group gets the same override they have elsewhere, and every
/// other self-service user keeps hitting the refusal. The refusal tests below are the ones that
/// matter most for this module.
///
/// Servicing does NOT touch ownership or eligibility: those key on callerSid and are decided before
/// this gate. A servicer grant cannot become a second, weaker route into a group the operator does
/// not own.
/// </remarks>
public sealed class SelfServiceGroupServicerTests : IDisposable
{
    private const string ServicerSid = "S-1-5-21-1-2-3-4001";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ssg-svc-{Guid.NewGuid():N}");

    public SelfServiceGroupServicerTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task AProtectedMember_IsRefusedForAnOrdinaryOperator()
    {
        var service = CreateService(servicerGroups: null);

        var gate = await service.CheckMemberProtectedAsync(Member(), UserIn(ServicerSid));

        Assert.NotNull(gate.Denial);
        Assert.Null(gate.ServicedNote);
    }

    [Fact]
    public async Task AProtectedMember_IsRefusedForSomeoneOutsideTheServicerGroup()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var gate = await service.CheckMemberProtectedAsync(Member(), UserIn("S-1-5-21-1-2-3-9999"));

        Assert.NotNull(gate.Denial);
        Assert.Null(gate.ServicedNote);
    }

    [Fact]
    public async Task AnAuthorisedServicer_IsAllowed_AndSaysWhy()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var gate = await service.CheckMemberProtectedAsync(Member(), UserIn(ServicerSid));

        Assert.Null(gate.Denial);
        Assert.NotNull(gate.ServicedNote);
        // The note must name the authorising group: an audit record saying only "serviced" cannot
        // answer who permitted it.
        Assert.Contains(ServicerSid, gate.ServicedNote!, StringComparison.Ordinal);
        Assert.Contains("matched rules", gate.ServicedNote!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AGrantInAnotherModule_ConfersNothingHere()
    {
        // The containment that stops per-module scoping collapsing into a global bypass.
        var service = CreateService(servicerGroups: null, otherModuleServicerGroups: [ServicerSid]);

        var gate = await service.CheckMemberProtectedAsync(Member(), UserIn(ServicerSid));

        Assert.NotNull(gate.Denial);
        Assert.Null(gate.ServicedNote);
    }

    [Fact]
    public async Task ANullActingUser_RefusesEvenWithAServicerGroupConfigured()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var gate = await service.CheckMemberProtectedAsync(Member(), actingUser: null);

        Assert.NotNull(gate.Denial);
        Assert.Null(gate.ServicedNote);
    }

    [Fact]
    public async Task AFailedProtectionCheck_RefusesEvenForAnAuthorisedServicer()
    {
        // Fail-closed outranks servicing: a check that could not complete does not know whether the
        // member is protected, so there is no refusal for a servicer to override.
        var service = CreateService(servicerGroups: [ServicerSid], checkFails: true);

        var gate = await service.CheckMemberProtectedAsync(Member(), UserIn(ServicerSid));

        Assert.NotNull(gate.Denial);
        Assert.Null(gate.ServicedNote);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static ResolvedDirectoryPrincipal Member() =>
        new("AD", "vip@contoso.com", "vip@contoso.com", "vip@contoso.com", "VIP User",
            "CN=VIP User,OU=Users,DC=contoso,DC=com", "guid", null);

    private static ClaimsPrincipal UserIn(string groupSid) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ANALOG\\tester"), new Claim(ClaimTypes.Role, groupSid)],
            "TestAuth"));

    private SelfServiceGroupService CreateService(
        string[]? servicerGroups,
        string[]? otherModuleServicerGroups = null,
        bool checkFails = false)
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

        var rows = new Dictionary<string, string[]>();
        if (servicerGroups is { Length: > 0 })
            rows[ProtectedPrincipalServicerService.SectionKeyFor("SelfServiceGroups")] = servicerGroups;
        if (otherModuleServicerGroups is { Length: > 0 })
            rows[ProtectedPrincipalServicerService.SectionKeyFor("GroupManagement")] = otherModuleServicerGroups;
        // Always write something, so the store counts as CONFIGURED and the legacy AllowedGroups
        // fallback is out of the picture (see ppsvc-1).
        rows["SelfServiceGroups"] = ["S-1-5-21-1-2-3-500"];
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

        var pp = new FakePpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea)
        {
            Verdict = checkFails
                ? ProtectedPrincipalResult.Failed("check unavailable for test")
                : ProtectedPrincipalResult.Protected("protected for test", "ceo-user-rule"),
        };

        return new SelfServiceGroupService(
            moduleCredentials, pp, servicers, NullLogger<SelfServiceGroupService>.Instance);
    }

    /// <summary>Reports a scripted verdict so both the protected and check-failed paths are reachable.</summary>
    private sealed class FakePpService : ProtectedPrincipalService
    {
        public FakePpService(IWebHostEnvironment env, IConfiguration config,
            ModuleConfigService moduleConfig, ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
        { }

        public ProtectedPrincipalResult Verdict { get; init; } =
            ProtectedPrincipalResult.Protected("protected for test", "ceo-user-rule");

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
            => Task.FromResult(Verdict);
    }
}
