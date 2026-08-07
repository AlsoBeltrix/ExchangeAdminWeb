using System.Security.Claims;
using ExchangeAdminWeb.Models.AccountLockoutRemediation;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Protected-principal servicing in Account Lockout Remediation.
/// </summary>
/// <remarks>
/// Drives GuardTargetUsersAsync directly. RunLogoffAsync fetches an AD credential and reaches a
/// live directory before the guard runs, so no test can get to the servicer path through the public
/// methods; the guard is exposed internally for exactly this reason.
///
/// This module forcibly logs a user off their sessions, so a protected principal being logged off
/// is precisely the disruption protection exists to prevent - and precisely what an exec support
/// operator may legitimately need to do during a lockout incident. The guard filters per user, so a
/// sweep can log off ordinary users while refusing a protected one in the same run.
///
/// This module audits its own decisions inside the guard rather than handing a note back to a page,
/// so the serviced override is recorded here, one row per user, as a SUCCESS carrying the note in
/// extra. It cannot ride errorDetail: that field is written as null on success.
/// </remarks>
public sealed class AccountLockoutRemediationServicerTests : IDisposable
{
    private const string ServicerSid = "S-1-5-21-1-2-3-4001";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"alr-svc-{Guid.NewGuid():N}");

    public AccountLockoutRemediationServicerTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task AProtectedUser_IsBlockedForAnOrdinaryOperator()
    {
        var service = CreateService(servicerGroups: null);

        var (filtered, rows) = await service.GuardTargetUsersAsync(
            MachineMap("vip"), Context(UserIn(ServicerSid)), "INC123", "Logoff");

        Assert.Empty(filtered);
        var row = Assert.Single(rows);
        Assert.Equal("protected-principal", row.Action);
        Assert.False(row.Success);
    }

    [Fact]
    public async Task AProtectedUser_IsBlockedForSomeoneOutsideTheServicerGroup()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var (filtered, rows) = await service.GuardTargetUsersAsync(
            MachineMap("vip"), Context(UserIn("S-1-5-21-1-2-3-9999")), "INC123", "Logoff");

        Assert.Empty(filtered);
        var row = Assert.Single(rows);
        Assert.Equal("protected-principal", row.Action);
        Assert.False(row.Success);
    }

    [Fact]
    public async Task AnAuthorisedServicer_MayLogOffTheProtectedUser()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var (filtered, rows) = await service.GuardTargetUsersAsync(
            MachineMap("vip"), Context(UserIn(ServicerSid)), "INC123", "Logoff");

        // The user survives the filter, so the logoff will reach them.
        var machine = Assert.Single(filtered);
        Assert.Equal(["vip"], machine.Value);
        // No BLOCKED row: the override is recorded through the audit, not as a refusal row.
        Assert.Empty(rows);
    }

    [Fact]
    public async Task AGrantInAnotherModule_ConfersNothingHere()
    {
        // The containment that stops per-module scoping collapsing into a global bypass.
        var service = CreateService(servicerGroups: null, otherModuleServicerGroups: [ServicerSid]);

        var (filtered, rows) = await service.GuardTargetUsersAsync(
            MachineMap("vip"), Context(UserIn(ServicerSid)), "INC123", "Logoff");

        Assert.Empty(filtered);
        Assert.Single(rows);
    }

    [Fact]
    public async Task AnAnonymousOperator_IsBlockedEvenWithAServicerGroupConfigured()
    {
        // No role claims at all: nothing to match the grant against, so it must refuse.
        var service = CreateService(servicerGroups: [ServicerSid]);

        var (filtered, rows) = await service.GuardTargetUsersAsync(
            MachineMap("vip"), Context(new ClaimsPrincipal(new ClaimsIdentity())), "INC123", "Logoff");

        Assert.Empty(filtered);
        Assert.Single(rows);
    }

    [Fact]
    public async Task AnUnresolvableUser_IsBlockedEvenForAnAuthorisedServicer()
    {
        // Fail-closed outranks servicing: an identity that could not be resolved is not known to be
        // protected, so there is no refusal for a servicer to override.
        var service = CreateService(servicerGroups: [ServicerSid], resolutionUnavailable: true);

        var (filtered, rows) = await service.GuardTargetUsersAsync(
            MachineMap("vip"), Context(UserIn(ServicerSid)), "INC123", "Logoff");

        Assert.Empty(filtered);
        var row = Assert.Single(rows);
        Assert.Equal("identity-blocked", row.Action);
    }

    [Fact]
    public async Task OnlyTheProtectedUserIsFiltered_TheRestOfTheSweepProceeds()
    {
        // Per-user filtering: an ordinary user in the same sweep is unaffected by the refusal.
        var service = CreateService(servicerGroups: null, protectedUsers: ["vip"]);

        var (filtered, rows) = await service.GuardTargetUsersAsync(
            MachineMap("vip", "ordinary"), Context(UserIn(ServicerSid)), "INC123", "Logoff");

        var machine = Assert.Single(filtered);
        Assert.Equal(["ordinary"], machine.Value);
        var row = Assert.Single(rows);
        Assert.Equal("protected-principal", row.Action);
        Assert.False(row.Success);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static Dictionary<string, string[]> MachineMap(params string[] users) =>
        new(StringComparer.OrdinalIgnoreCase) { ["WS-001"] = users };

    private static AccountLockoutOperatorContext Context(ClaimsPrincipal principal) =>
        new(principal, "tester", "127.0.0.1");

    private static ClaimsPrincipal UserIn(string groupSid) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ANALOG\\tester"), new Claim(ClaimTypes.Role, groupSid)],
            "TestAuth"));

    private AccountLockoutRemediationService CreateService(
        string[]? servicerGroups,
        string[]? otherModuleServicerGroups = null,
        bool resolutionUnavailable = false,
        string[]? protectedUsers = null)
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
            rows[ProtectedPrincipalServicerService.SectionKeyFor("AccountLockoutRemediation")] = servicerGroups;
        if (otherModuleServicerGroups is { Length: > 0 })
            rows[ProtectedPrincipalServicerService.SectionKeyFor("GroupManagement")] = otherModuleServicerGroups;
        // Always write something, so the store counts as CONFIGURED and the legacy AllowedGroups
        // fallback is out of the picture (see ppsvc-1).
        rows["AccountLockoutRemediation"] = ["S-1-5-21-1-2-3-500"];
        sectionAccessRepo.SaveAll(rows);

        var sectionAccess = new SectionAccessService(
            config, NullLogger<SectionAccessService>.Instance, env, catalog, sectionAccessRepo);
        var servicers = new ProtectedPrincipalServicerService(
            sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance);

        var moduleConfig = new ModuleConfigService(
            catalog, env, TestConfigStore.CreateModuleConfig(_tempDir), NullLogger<ModuleConfigService>.Instance);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var jsonlLog = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, jsonlLog);
        var audit = new AuditService(jsonlLog, trace);
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), NullLogger<ExtendedLogService>.Instance);
        var delinea = new DelineaService(httpClientFactory, config, NullLogger<DelineaService>.Instance, extLog, trace);
        var moduleCredentials = new ModuleCredentialService(moduleConfig, delinea, NullLogger<ModuleCredentialService>.Instance);

        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());

        var email = Substitute.For<EmailService>(config, Substitute.For<ILogger<EmailService>>());

        var pp = new FakePpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea)
        {
            Unavailable = resolutionUnavailable,
            ProtectedUsers = protectedUsers,
        };

        return new AccountLockoutRemediationService(
            moduleCredentials, moduleConfig, pp, servicers, authorization, audit, email, trace,
            NullLogger<AccountLockoutRemediationService>.Instance);
    }

    /// <summary>Resolves any identity and reports it protected, so the servicer path is reachable.</summary>
    private sealed class FakePpService : ProtectedPrincipalService
    {
        public FakePpService(IWebHostEnvironment env, IConfiguration config,
            ModuleConfigService moduleConfig, ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
        { }

        /// <summary>When true the resolver cannot answer, which must fail closed ahead of servicing.</summary>
        public bool Unavailable { get; init; }

        /// <summary>Null means every user is protected; otherwise only these are.</summary>
        public string[]? ProtectedUsers { get; init; }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity)
            => Task.FromResult<(ResolvedDirectoryPrincipal?, ResolutionStatus)>(
                Unavailable
                    ? (null, ResolutionStatus.Unavailable)
                    : (new ResolvedDirectoryPrincipal("AD", identity, identity, identity, identity, null, $"guid-{identity}", null),
                       ResolutionStatus.Resolved));

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
        {
            var isProtected = ProtectedUsers == null
                || ProtectedUsers.Contains(target.UserPrincipalName, StringComparer.OrdinalIgnoreCase);

            return Task.FromResult(isProtected
                ? ProtectedPrincipalResult.Protected("protected for test", "ceo-user-rule")
                : ProtectedPrincipalResult.NotProtected());
        }
    }
}
