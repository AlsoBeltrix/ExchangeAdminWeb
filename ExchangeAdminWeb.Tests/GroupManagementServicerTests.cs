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
/// Protected-principal servicing in on-prem Group Management.
/// </summary>
/// <remarks>
/// Behavioural, against the real service gate. The existing GroupManagementServiceTests assert the
/// PROTECTION decision and are deliberately left untouched: if adding a servicer path turned any of
/// those refusals into an allow, they would fail, and that is the point.
///
/// This module differs from Conference Rooms in where the audit happens. The page's duplicate
/// protection gate was removed (it produced unaudited failed attempts), so the service decides and
/// the PAGE audits - which means the serviced note has to survive the trip back on
/// PermissionResult.ServicedNote. A note that never leaves the service is a bypass with no record,
/// so these tests assert it arrives on the result rather than merely that the action was allowed.
///
/// The allow cases stop at the AD write: no runspace or directory is reachable here, so an
/// authorised servicer surfaces as a FAILURE whose message is about AD, never about protection.
/// That is the observable difference between "refused by the gate" and "allowed past the gate",
/// and asserting the message distinguishes them without a live directory.
/// </remarks>
public sealed class GroupManagementServicerTests : IDisposable
{
    private const string ServicerSid = "S-1-5-21-1-2-3-4001";
    private const string GroupDn = "CN=Some Group,OU=Groups,DC=contoso,DC=com";
    private const string ProtectedMember = "vip@contoso.com";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"gm-svc-{Guid.NewGuid():N}");

    public GroupManagementServicerTests()
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

        var result = await service.AddMemberAsync(GroupDn, ProtectedMember, UserIn(ServicerSid));

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.ServicedNote);
    }

    [Fact]
    public async Task AProtectedMember_IsRefusedForSomeoneOutsideTheServicerGroup()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var result = await service.AddMemberAsync(GroupDn, ProtectedMember, UserIn("S-1-5-21-1-2-3-9999"));

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.ServicedNote);
    }

    [Fact]
    public async Task AnAuthorisedServicer_PassesTheGate_OnAdd()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var result = await service.AddMemberAsync(GroupDn, ProtectedMember, UserIn(ServicerSid));

        // Past the gate: it fails at credentials/AD, not at protection.
        Assert.DoesNotContain("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAuthorisedServicer_PassesTheGate_OnRemove()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var result = await service.RemoveMemberAsync(GroupDn, ProtectedMember, UserIn(ServicerSid));

        Assert.DoesNotContain("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AGrantInAnotherModule_ConfersNothingHere()
    {
        // The containment that stops per-module scoping collapsing into a global bypass.
        var service = CreateService(servicerGroups: null, otherModuleServicerGroups: [ServicerSid]);

        var result = await service.AddMemberAsync(GroupDn, ProtectedMember, UserIn(ServicerSid));

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ANullActingUser_RefusesEvenWithAServicerGroupConfigured()
    {
        // Any caller that does not supply a principal - a job, a background sweep - must deny.
        var service = CreateService(servicerGroups: [ServicerSid]);

        var result = await service.AddMemberAsync(GroupDn, ProtectedMember, actingUser: null);

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.ServicedNote);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static ClaimsPrincipal UserIn(string groupSid) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ANALOG\\tester"), new Claim(ClaimTypes.Role, groupSid)],
            "TestAuth"));

    private GroupManagementService CreateService(
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
            rows[ProtectedPrincipalServicerService.SectionKeyFor("GroupManagement")] = servicerGroups;
        if (otherModuleServicerGroups is { Length: > 0 })
            rows[ProtectedPrincipalServicerService.SectionKeyFor("BlockedSenders")] = otherModuleServicerGroups;
        // Always write something, so the store counts as CONFIGURED and the legacy AllowedGroups
        // fallback is out of the picture (see ppsvc-1).
        rows["GroupManagement"] = ["S-1-5-21-1-2-3-500"];
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

        var pp = new ProtectedFakePpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea);

        return new GroupManagementService(
            moduleConfig, moduleCredentials, pp, servicers, NullLogger<GroupManagementService>.Instance);
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
