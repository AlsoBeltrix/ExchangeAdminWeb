using System.Security.Claims;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Protected-principal servicing in Conference Rooms.
/// </summary>
/// <remarks>
/// Behavioural, against the real gate. The existing ConferenceRoomProtectionGateTests assert the
/// PROTECTION decision and are deliberately left untouched: if adding a servicer path turned any
/// of those refusals into an allow, they would fail, and that is the point.
///
/// The bulk case is the one worth stating plainly. A job runs off-circuit - the submitting
/// operator's circuit is gone and the row executes later under nobody. The job record carries
/// SubmittedBy as a STRING, which cannot answer "is this principal in the servicing group":
/// IsInRole needs a real token, and building one from a name would be inventing an identity. So a
/// protected room in a bulk CSV refuses regardless of who submitted it.
/// </remarks>
public sealed class ConferenceRoomServicerTests : IDisposable
{
    private const string ServicerSid = "S-1-5-21-1-2-3-4001";
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"cr-svc-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task AProtectedRoom_IsRefusedForAnOrdinaryOperator()
    {
        var gate = CreateGate(servicerGroups: null);

        var (denied, serviced) = await RunAsync(gate, UserIn(ServicerSid));

        Assert.True(denied, "a protected room must be refused when no servicer group is configured");
        Assert.Null(serviced);
    }

    [Fact]
    public async Task AProtectedRoom_IsRefusedForSomeoneOutsideTheServicerGroup()
    {
        var gate = CreateGate(servicerGroups: [ServicerSid]);

        var (denied, serviced) = await RunAsync(gate, UserIn("S-1-5-21-1-2-3-9999"));

        Assert.True(denied);
        Assert.Null(serviced);
    }

    [Fact]
    public async Task AProtectedRoom_IsAllowedForAnAuthorisedServicer_AndSaysWhy()
    {
        var gate = CreateGate(servicerGroups: [ServicerSid]);

        var (denied, serviced) = await RunAsync(gate, UserIn(ServicerSid));

        Assert.False(denied, "an authorised servicer must be able to act on a protected room");
        Assert.NotNull(serviced);
        // The detail must name the authorising group: an audit record saying only "serviced"
        // cannot answer who permitted it.
        Assert.Contains(ServicerSid, serviced!, StringComparison.Ordinal);
        Assert.Contains("matched rules", serviced!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AGrantInAnotherModule_ConfersNothingHere()
    {
        // The containment that stops per-module scoping collapsing into a global bypass.
        var gate = CreateGate(servicerGroups: null, otherModuleServicerGroups: [ServicerSid]);

        var (denied, _) = await RunAsync(gate, UserIn(ServicerSid));

        Assert.True(denied, "a Blocked Senders servicer grant must not authorise Conference Rooms");
    }

    [Fact]
    public async Task ABulkJobRow_RefusesEvenWithAServicerGroupConfigured()
    {
        // The off-circuit rule. null is what the bulk processor passes, and it must deny.
        var gate = CreateGate(servicerGroups: [ServicerSid]);

        var (denied, serviced) = await RunAsync(gate, user: null);

        Assert.True(denied, "a bulk job has no operator and must never service a protected room");
        Assert.Null(serviced);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static ClaimsPrincipal UserIn(string groupSid) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ANALOG\\tester"), new Claim(ClaimTypes.Role, groupSid)],
            "TestAuth"));

    /// <summary>Runs the gate against a target the fake reports as protected.</summary>
    private static async Task<(bool denied, string? serviced)> RunAsync(
        ConferenceRoomProtectionGate gate, ClaimsPrincipal? user)
    {
        string? captured = null;
        var denied = await gate.GuardThenRunAsync("room@x", user,
            onDenied: _ => true,
            onAllowed: serviced => { captured = serviced; return Task.FromResult(false); });

        return (denied, captured);
    }

    private ConferenceRoomProtectionGate CreateGate(
        string[]? servicerGroups,
        string[]? otherModuleServicerGroups = null)
    {
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = _tempDir,
        }).Build();

        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var catalog = new Modules.ModuleCatalog();
        var store = TestConfigStore.Create(_tempDir);
        var sectionAccessRepo = new SectionAccessRepository(store);

        var rows = new Dictionary<string, string[]>();
        if (servicerGroups is { Length: > 0 })
            rows[ProtectedPrincipalServicerService.SectionKeyFor("ConferenceRooms")] = servicerGroups;
        if (otherModuleServicerGroups is { Length: > 0 })
            rows[ProtectedPrincipalServicerService.SectionKeyFor("BlockedSenders")] = otherModuleServicerGroups;
        // Always write something, so the store counts as CONFIGURED and the legacy AllowedGroups
        // fallback is out of the picture (see ppsvc-1).
        rows["ConferenceRooms"] = ["S-1-5-21-1-2-3-500"];
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

        var pp = new ProtectedFakePpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea);
        return new ConferenceRoomProtectionGate(pp, servicers, NullLogger<ConferenceRoomProtectionGate>.Instance);
    }

    /// <summary>Resolves any identity and reports it protected, so the servicer path is reachable.</summary>
    private sealed class ProtectedFakePpService : ProtectedPrincipalService
    {
        public ProtectedFakePpService(Microsoft.AspNetCore.Hosting.IWebHostEnvironment env, IConfiguration config,
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
