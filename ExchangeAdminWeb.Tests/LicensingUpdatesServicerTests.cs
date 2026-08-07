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
/// Protected-principal servicing in Licensing Updates.
/// </summary>
/// <remarks>
/// Drives EvaluateProtectionAsync directly. ApplyCsvAsync returns at the credential gate before the
/// servicer decision runs, so the path is not reachable through the public method without a live
/// directory; the decision is exposed internally for exactly this reason.
///
/// This module applies licence changes from a CSV, so a batch naturally mixes protected and
/// ordinary users. The decision is per row: an authorised servicer can update one protected user
/// while another remains refused, and each override is recorded against its own user.
///
/// The two returned views are both asserted. The apply loop keys on PrincipalKey (ObjectGuid when
/// present) and the audit loop only has the UPN, so a note present in one view and missing from the
/// other would either skip the write or - worse - make the write with no record of who permitted
/// it.
/// </remarks>
public sealed class LicensingUpdatesServicerTests : IDisposable
{
    private const string ServicerSid = "S-1-5-21-1-2-3-4001";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"lic-svc-{Guid.NewGuid():N}");

    public LicensingUpdatesServicerTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task AProtectedUser_IsNotServicedForAnOrdinaryOperator()
    {
        var service = CreateService(servicerGroups: null);

        var (protectionResults, notes, notesByUpn) = await service.EvaluateProtectionAsync(
            [Row("vip@contoso.com")], UserIn(ServicerSid));

        Assert.True(Assert.Single(protectionResults).Value.IsProtected);
        Assert.Empty(notes);
        Assert.Empty(notesByUpn);
    }

    [Fact]
    public async Task AProtectedUser_IsNotServicedForSomeoneOutsideTheServicerGroup()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var (_, notes, _) = await service.EvaluateProtectionAsync(
            [Row("vip@contoso.com")], UserIn("S-1-5-21-1-2-3-9999"));

        Assert.Empty(notes);
    }

    [Fact]
    public async Task AnAuthorisedServicer_IsAllowed_AndSaysWhy()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var (_, notes, notesByUpn) = await service.EvaluateProtectionAsync(
            [Row("vip@contoso.com")], UserIn(ServicerSid));

        var note = Assert.Single(notes).Value;
        // The note must name the authorising group AND the user: a batch override that cannot say
        // which account it covered is not an audit record.
        Assert.Contains(ServicerSid, note, StringComparison.Ordinal);
        Assert.Contains("vip@contoso.com", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("matched rules", note, StringComparison.OrdinalIgnoreCase);

        // Both views must carry it: the apply loop reads one, the audit loop the other.
        Assert.Equal(note, notesByUpn["vip@contoso.com"]);
    }

    [Fact]
    public async Task TheAuditView_IsKeyedByUpn_NotByObjectGuid()
    {
        // The specific trap: PrincipalKey prefers ObjectGuid, so a UPN lookup against the apply-loop
        // view would silently find nothing and drop the override from the audit record.
        var service = CreateService(servicerGroups: [ServicerSid]);

        var (_, notes, notesByUpn) = await service.EvaluateProtectionAsync(
            [Row("vip@contoso.com", objectGuid: "guid-1234")], UserIn(ServicerSid));

        Assert.True(notes.ContainsKey("guid-1234"));
        Assert.True(notesByUpn.ContainsKey("vip@contoso.com"));
    }

    [Fact]
    public async Task OnlyTheServicedUserGetsANote_TheRestOfTheBatchIsUnaffected()
    {
        // Per-row decision: an ordinary user in the same CSV produces no override record.
        var service = CreateService(servicerGroups: [ServicerSid], protectedUsers: ["vip@contoso.com"]);

        var (_, notes, notesByUpn) = await service.EvaluateProtectionAsync(
            [Row("vip@contoso.com"), Row("ordinary@contoso.com")], UserIn(ServicerSid));

        Assert.Single(notes);
        Assert.True(notesByUpn.ContainsKey("vip@contoso.com"));
        Assert.False(notesByUpn.ContainsKey("ordinary@contoso.com"));
    }

    [Fact]
    public async Task AGrantInAnotherModule_ConfersNothingHere()
    {
        // The containment that stops per-module scoping collapsing into a global bypass.
        var service = CreateService(servicerGroups: null, otherModuleServicerGroups: [ServicerSid]);

        var (_, notes, _) = await service.EvaluateProtectionAsync(
            [Row("vip@contoso.com")], UserIn(ServicerSid));

        Assert.Empty(notes);
    }

    [Fact]
    public async Task ANullActingUser_IsNotServicedEvenWithAServicerGroupConfigured()
    {
        var service = CreateService(servicerGroups: [ServicerSid]);

        var (_, notes, _) = await service.EvaluateProtectionAsync(
            [Row("vip@contoso.com")], actingUser: null);

        Assert.Empty(notes);
    }

    [Fact]
    public async Task AFailedProtectionCheck_IsNotServiced()
    {
        // Fail-closed outranks servicing: a check that could not complete does not know whether the
        // user is protected, so there is no refusal for a servicer to override. ApplyChanges keeps
        // treating this as CheckFailed and skips the write.
        var service = CreateService(servicerGroups: [ServicerSid], checkFails: true);

        var (protectionResults, notes, _) = await service.EvaluateProtectionAsync(
            [Row("vip@contoso.com")], UserIn(ServicerSid));

        Assert.True(Assert.Single(protectionResults).Value.CheckFailed);
        Assert.Empty(notes);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static LicensePreviewRow Row(string upn, string? objectGuid = null) =>
        new(upn,
            new ResolvedDirectoryPrincipal("AD", upn, upn, upn, upn,
                $"CN={upn},OU=Users,DC=contoso,DC=com", objectGuid, null),
            "OldValue", "NewValue", null);

    private static ClaimsPrincipal UserIn(string groupSid) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ANALOG\\tester"), new Claim(ClaimTypes.Role, groupSid)],
            "TestAuth"));

    private LicensingUpdatesService CreateService(
        string[]? servicerGroups,
        string[]? otherModuleServicerGroups = null,
        bool checkFails = false,
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
            rows[ProtectedPrincipalServicerService.SectionKeyFor("LicensingUpdates")] = servicerGroups;
        if (otherModuleServicerGroups is { Length: > 0 })
            rows[ProtectedPrincipalServicerService.SectionKeyFor("GroupManagement")] = otherModuleServicerGroups;
        // Always write something, so the store counts as CONFIGURED and the legacy AllowedGroups
        // fallback is out of the picture (see ppsvc-1).
        rows["LicensingUpdates"] = ["S-1-5-21-1-2-3-500"];
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
        var audit = new AuditService(jsonlLog, trace);

        var pp = new FakePpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea)
        {
            CheckFails = checkFails,
            ProtectedUsers = protectedUsers,
        };

        return new LicensingUpdatesService(
            moduleCredentials, moduleConfig, pp, servicers, trace, audit,
            NullLogger<LicensingUpdatesService>.Instance);
    }

    /// <summary>Reports a scripted verdict so the protected, clean and check-failed paths are reachable.</summary>
    private sealed class FakePpService : ProtectedPrincipalService
    {
        public FakePpService(IWebHostEnvironment env, IConfiguration config,
            ModuleConfigService moduleConfig, ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
        { }

        /// <summary>When true the check cannot complete, which must fail closed ahead of servicing.</summary>
        public bool CheckFails { get; init; }

        /// <summary>Null means every user is protected; otherwise only these are.</summary>
        public string[]? ProtectedUsers { get; init; }

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
        {
            if (CheckFails)
                return Task.FromResult(ProtectedPrincipalResult.Failed("check unavailable for test"));

            var isProtected = ProtectedUsers == null
                || ProtectedUsers.Contains(target.UserPrincipalName, StringComparer.OrdinalIgnoreCase);

            return Task.FromResult(isProtected
                ? ProtectedPrincipalResult.Protected("protected for test", "ceo-user-rule")
                : ProtectedPrincipalResult.NotProtected());
        }
    }
}
