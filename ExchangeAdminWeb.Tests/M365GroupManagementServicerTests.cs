using System.Security.Claims;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Protected-principal servicing in M365 (cloud) Group Management.
/// </summary>
/// <remarks>
/// Behavioural, against the real service gate. The existing M365GroupManagementServiceTests assert
/// the PROTECTION decision and are deliberately left untouched: if adding a servicer path turned
/// any of those refusals into an allow, they would fail, and that is the point.
///
/// As in on-prem Group Management, the service decides and the PAGE audits, so the serviced note
/// has to survive the trip back on M365GroupResult.ServicedNote. A note that never leaves the
/// service is a bypass with no record.
///
/// The allow cases stop at the Graph write: no Graph credential is configured here, so an
/// authorised servicer gets past the gate and then THROWS from GetGraphClientAsync. A refusal
/// returns a result and never throws, so the exception is itself the proof the gate was passed -
/// a sharper signal than any message match, since it can only come from code beyond the gate.
/// </remarks>
public sealed class M365GroupManagementServicerTests : IDisposable
{
    private const string ServicerSid = "S-1-5-21-1-2-3-4001";
    private const string GroupId = "00000000-0000-0000-0000-000000000001";
    private const string ObjectId = "00000000-0000-0000-0000-000000000002";
    private const string ProtectedMember = "vip@contoso.com";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"m365-svc-{Guid.NewGuid():N}");

    public M365GroupManagementServicerTests()
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

        var result = await service.AddMemberAsync(GroupId, ProtectedMember, UserIn(ServicerSid));

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.ServicedNote);
    }

    [Fact]
    public async Task AProtectedMember_IsRefusedForSomeoneOutsideTheServicerGroup()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var result = await service.AddMemberAsync(GroupId, ProtectedMember, UserIn("S-1-5-21-1-2-3-9999"));

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.ServicedNote);
    }

    [Fact]
    public async Task AnAuthorisedServicer_PassesTheGate_OnAddMember()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        // Past the gate: it reaches the Graph client and throws there. A gate refusal would have
        // returned a result instead, so reaching this exception is the pass.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddMemberAsync(GroupId, ProtectedMember, UserIn(ServicerSid)));

        Assert.Contains("not configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAuthorisedServicer_PassesTheGate_OnAddOwner()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddOwnerAsync(GroupId, ProtectedMember, UserIn(ServicerSid)));
    }

    [Fact]
    public async Task AnAuthorisedServicer_PassesTheGate_OnRemoveMember()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RemoveMemberAsync(GroupId, ObjectId, ProtectedMember, UserIn(ServicerSid)));
    }

    [Fact]
    public async Task AnAuthorisedServicer_PassesTheGate_OnRemoveOwner()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RemoveOwnerAsync(GroupId, ObjectId, ProtectedMember, UserIn(ServicerSid)));
    }

    [Fact]
    public async Task AGrantInAnotherModule_ConfersNothingHere()
    {
        // The containment that stops per-module scoping collapsing into a global bypass. It matters
        // doubly here: on-prem GroupManagement is the adjacent module an operator would assume
        // covers "groups", and it must not.
        var service = CreateService(servicerGroups: null, otherModuleServicerGroups: [ServicerSid]);

        var result = await service.AddMemberAsync(GroupId, ProtectedMember, UserIn(ServicerSid));

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ANullActingUser_RefusesEvenWithAServicerGroupConfigured()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var result = await service.AddMemberAsync(GroupId, ProtectedMember, actingUser: null);

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.ServicedNote);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static ClaimsPrincipal UserIn(string groupSid) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ANALOG\\tester"), new Claim(ClaimTypes.Role, groupSid)],
            "TestAuth"));

    private M365GroupManagementService CreateService(
        string[]? servicerGroups,
        string[]? otherModuleServicerGroups = null)
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
            rows[ProtectedPrincipalServicerService.SectionKeyFor("M365GroupManagement")] = servicerGroups;
        if (otherModuleServicerGroups is { Length: > 0 })
            rows[ProtectedPrincipalServicerService.SectionKeyFor("GroupManagement")] = otherModuleServicerGroups;
        // Always write something, so the store counts as CONFIGURED and the legacy AllowedGroups
        // fallback is out of the picture (see ppsvc-1).
        rows["M365GroupManagement"] = ["S-1-5-21-1-2-3-500"];
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
        var audit = new AuditService(jsonlLog, trace);

        var pp = new ProtectedFakePpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea);

        return new M365GroupManagementService(
            moduleConfig, delinea, httpClientFactory, trace, audit, pp, servicers,
            NullLogger<M365GroupManagementService>.Instance);
    }

    /// <summary>Resolves any identity and reports it protected, so the servicer path is reachable.</summary>
    private sealed class ProtectedFakePpService : ProtectedPrincipalService
    {
        public ProtectedFakePpService(IWebHostEnvironment env, IConfiguration config,
            ModuleConfigService moduleConfig, ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
        { }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity)
            => Task.FromResult<(ResolvedDirectoryPrincipal?, ResolutionStatus)>(
                (new ResolvedDirectoryPrincipal("AD", identity, identity, identity, identity, null, "guid", null),
                 ResolutionStatus.Resolved));

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
            => Task.FromResult(ProtectedPrincipalResult.Protected("protected for test", "ceo-user-rule"));
    }
}
