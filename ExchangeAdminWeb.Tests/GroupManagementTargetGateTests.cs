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
/// S2 of docs/ProtectedGroupWriteTarget-Plan.md: the admin group module gates the TARGET GROUP
/// on both write paths (add AND remove, T3), on a full resolved snapshot (pgwt-2), before any
/// write. Behavioural where the refusal returns before the AD closure; the allowed/serviced
/// path necessarily enters a live runspace, so its wiring is pinned by ordering tripwires and
/// its decision semantics by the S1 gate tests (ProtectedGroupWriteTargetTests) - the same
/// evidence split the gmn loop used.
///
/// The existing member-protection suites pass UNMODIFIED alongside these; that is the plan's
/// "refusal quietly became an allow" stop-condition, deliberately re-asserted by running them.
/// </summary>
public sealed class GroupManagementTargetGateTests : IDisposable
{
    private const string GroupDn = "CN=Domain Admins,CN=Users,DC=ad,DC=analog,DC=com";
    private const string ServicerSid = "S-1-5-21-1-2-3-4001";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"gm-tgt-{Guid.NewGuid():N}");

    public GroupManagementTargetGateTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Add_IntoAProtectedTargetGroup_IsRefusedForAnOrdinaryOperator()
    {
        var service = CreateService(targetVerdict: ProtectedPrincipalResult.Protected(
            "Target group is protected against membership changes.", "Target:" + GroupDn));

        var result = await service.AddMemberAsync(GroupDn, "someone@contoso.com", UserIn("S-1-5-21-1-2-3-9999"), "DomainAdmins");

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.ServicedNote);
    }

    [Fact]
    public async Task Remove_FromAProtectedTargetGroup_IsRefused_RemovalIsNotExempt()
    {
        // AC2 / T3: an attacker's first move against a protected group is often a removal.
        var service = CreateService(targetVerdict: ProtectedPrincipalResult.Protected(
            "Target group is protected against membership changes.", "Target:" + GroupDn));

        var result = await service.RemoveMemberAsync(GroupDn, "someone@contoso.com", UserIn("S-1-5-21-1-2-3-9999"), "DomainAdmins");

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Add_WhenTheTargetCheckFails_FailsClosed()
    {
        // AC5: an unavailable or errored check denies - it never reads as "not protected".
        var service = CreateService(targetVerdict: ProtectedPrincipalResult.Failed("store unreadable"));

        var result = await service.AddMemberAsync(GroupDn, "someone@contoso.com", UserIn(ServicerSid), "DomainAdmins");

        Assert.False(result.Success);
        Assert.Contains("Protection check failed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_WhenTheGroupCannotBeResolved_Refuses()
    {
        // pgwt-2's precondition: no snapshot, no protection answer, no write.
        var service = CreateService(
            targetVerdict: ProtectedPrincipalResult.NotProtected(),
            groupResolves: false);

        var result = await service.AddMemberAsync(GroupDn, "someone@contoso.com", UserIn(ServicerSid), "DomainAdmins");

        Assert.False(result.Success);
        Assert.Contains("AD group not found", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_ProtectedTarget_NullActingUser_IsRefused()
    {
        var service = CreateService(targetVerdict: ProtectedPrincipalResult.Protected(
            "Target group is protected against membership changes.", "Target:" + GroupDn),
            servicerGroups: [ServicerSid]);

        var result = await service.AddMemberAsync(GroupDn, "someone@contoso.com", actingUser: null, "DomainAdmins");

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ----- CombineNotes: both serviced notes must reach the one audit slot -----

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("member note", null, "member note")]
    [InlineData(null, "target note", "target note")]
    [InlineData("member note", "target note", "member note; target note")]
    [InlineData("  ", "target note", "target note")]
    public void CombineNotes_JoinsWhatWasServiced(string? memberNote, string? targetNote, string? expected)
    {
        Assert.Equal(expected, GroupManagementService.CombineNotes(memberNote, targetNote));
    }

    // ----- Tripwires: ordering, snapshot DN, and the retired bare-DN resolver -----

    [Fact]
    public void BothWritePaths_GateTheResolvedGroup_BeforeTheWrite_AndWriteItsSnapshotDn()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "GroupManagementService.cs"));

        foreach (var (sig, endSig, writeCmd) in new[]
        {
            ("public async Task<PermissionResult> AddMemberAsync(",
             "public async Task<PermissionResult> RemoveMemberAsync(",
             "AddCommand(\"Add-ADGroupMember\")"),
            ("public async Task<PermissionResult> RemoveMemberAsync(",
             "// --- Helpers ---",
             "AddCommand(\"Remove-ADGroupMember\")"),
        })
        {
            var start = text.IndexOf(sig, StringComparison.Ordinal);
            Assert.True(start >= 0, sig + " not found - tripwire is stale.");
            var end = text.IndexOf(endSig, start + 1, StringComparison.Ordinal);
            Assert.True(end > start, "Could not bound " + sig + " - update the tripwire.");
            var body = text[start..end];

            var iMemberGate = body.IndexOf("return resolvedGate.Denial;", StringComparison.Ordinal);
            var iGroupResolve = body.IndexOf("ResolveGroupForWrite(creds.Value, samAccountName, groupIdentity)", StringComparison.Ordinal);
            var iTargetGate = body.IndexOf("ProtectedPrincipalServicing.ForWriteTarget(", StringComparison.Ordinal);
            var iRefusal = body.IndexOf("if (!targetGate.Allowed)", StringComparison.Ordinal);
            var iSnapshotDn = body.IndexOf("var resolvedGroupDn = resolvedGroup.Principal!.DistinguishedName!;", StringComparison.Ordinal);
            var iWrite = body.IndexOf(writeCmd, StringComparison.Ordinal);

            Assert.True(iGroupResolve > iMemberGate, "Group resolution must follow the member gate in " + sig);
            Assert.True(iTargetGate > iGroupResolve, "The target gate must run on the RESOLVED group in " + sig);
            Assert.True(iRefusal > iTargetGate && iRefusal < iSnapshotDn, "The target refusal must return before the closure in " + sig);
            Assert.True(iWrite > iSnapshotDn, "The write must use the gated snapshot's DN in " + sig);

            // pgwt-2: the bare-DN resolver is retired from the write paths - a DN is enough to
            // WRITE to a group and not enough to ASK whether it is protected.
            Assert.DoesNotContain("ResolveAdGroupIdentity(ps", body, StringComparison.Ordinal);
            // Both success messages carry the COMBINED note, so a serviced target is audited.
            Assert.Contains("combinedNote", body, StringComparison.Ordinal);
        }
    }

    // ---- harness ------------------------------------------------------------------------------

    private static ClaimsPrincipal UserIn(string groupSid) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ANALOG\\tester"), new Claim(ClaimTypes.Role, groupSid)],
            "TestAuth"));

    private GroupManagementService CreateService(
        ProtectedPrincipalResult targetVerdict,
        bool groupResolves = true,
        string[]? servicerGroups = null)
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

        var pp = new TargetScriptedPpService(env, config, moduleConfig,
            TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea)
        { TargetVerdict = targetVerdict };

        return new SeamedService(
            moduleConfig, moduleCredentials, pp, servicers, NullLogger<GroupManagementService>.Instance)
        { GroupResolves = groupResolves };
    }

    /// <summary>
    /// Member is resolvable and NOT protected (the member gate is the gmn suites' job); the
    /// TARGET verdict is scripted. Both credential fetch and directory resolutions are seamed
    /// so the flow reaches the target gate without a live directory.
    /// </summary>
    private sealed class SeamedService : GroupManagementService
    {
        public SeamedService(ModuleConfigService mc, ModuleCredentialService cred,
            ProtectedPrincipalService pp, ProtectedPrincipalServicerService servicers,
            Microsoft.Extensions.Logging.ILogger<GroupManagementService> logger)
            : base(mc, cred, pp, servicers, logger)
        { }

        public bool GroupResolves { get; init; } = true;

        internal override Task<(string username, string password, string domain)?> GetCredentialsAsync(string purpose)
            => Task.FromResult<(string, string, string)?>(("svc", "pw", "ANALOG"));

        internal override ResolvedMember ResolveMemberForWrite(
            (string username, string password, string domain) creds,
            string memberIdentity, string? memberDn, string? memberObjectGuid)
            => new(new ResolvedDirectoryPrincipal("Test-AD", memberIdentity, memberIdentity,
                "someone", memberIdentity, "CN=Some One,OU=Users,DC=ad,DC=analog,DC=com", "member-guid", null),
                IsGroup: false, Error: null);

        internal override ResolvedMember ResolveGroupForWrite(
            (string username, string password, string domain) creds,
            string? samAccountName, string groupIdentity)
            => GroupResolves
                ? new(new ResolvedDirectoryPrincipal("Test-AD", "Domain Admins", string.Empty,
                    "DomainAdmins", null, GroupDn, "group-guid", null), IsGroup: true, Error: null)
                : ResolvedMember.Failed($"AD group not found. Tried: {samAccountName}, {groupIdentity}");
    }

    /// <summary>Member checks report not-protected; the write-target verdict is scripted.</summary>
    private sealed class TargetScriptedPpService : ProtectedPrincipalService
    {
        public TargetScriptedPpService(IWebHostEnvironment env, IConfiguration config,
            ModuleConfigService moduleConfig, ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
        { }

        public required ProtectedPrincipalResult TargetVerdict { get; init; }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity)
            => Task.FromResult<(ResolvedDirectoryPrincipal?, ResolutionStatus)>((null, ResolutionStatus.NotFound));

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
            => Task.FromResult(ProtectedPrincipalResult.NotProtected());

        public override ProtectedPrincipalResult CheckWriteTarget(ResolvedDirectoryPrincipal target)
            => TargetVerdict;
    }
}
