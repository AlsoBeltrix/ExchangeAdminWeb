using System.Management.Automation;
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
/// S3 of docs/ProtectedGroupWriteTarget-Plan.md: self-service gates the TARGET GROUP inside the
/// shared executor, AFTER the DACL eligibility check (T2 - protection status is not an oracle
/// for callers who could not manage the group anyway) and before any write. The unserviced
/// refusal names the IT Support Desk (AC4): a protected group is not a self-service object.
///
/// Drives EvaluateTargetGate directly - the executor runs inside a live runspace no unit test
/// can enter (the same reason CheckMemberProtectedAsync is a seam) - and pins the executor
/// wiring with ordering tripwires, the gmn evidence split.
/// </summary>
public sealed class SelfServiceGroupTargetGateTests : IDisposable
{
    private const string GroupDn = "CN=Owned Group,OU=Groups,DC=ad,DC=analog,DC=com";
    private const string ServicerSid = "S-1-5-21-1-2-3-4001";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ssg-tgt-{Guid.NewGuid():N}");

    public SelfServiceGroupTargetGateTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void AProtectedTargetGroup_IsRefused_NamingTheItsd()
    {
        var service = CreateService(servicerGroups: null,
            verdict: ProtectedPrincipalResult.Protected("protected", "Target:" + GroupDn));

        var gate = service.EvaluateTargetGate(Snapshot(), UserIn(ServicerSid));

        Assert.NotNull(gate.Denial);
        Assert.Contains("IT Support Desk", gate.Denial!.Message, StringComparison.Ordinal);
        Assert.Null(gate.ServicedNote);
    }

    [Fact]
    public void AnAuthorisedServicer_IsAllowed_AndTheNoteSaysWriteTarget()
    {
        var service = CreateService(servicerGroups: [ServicerSid],
            verdict: ProtectedPrincipalResult.Protected("protected", "Target:" + GroupDn));

        var gate = service.EvaluateTargetGate(Snapshot(), UserIn(ServicerSid));

        Assert.Null(gate.Denial);
        Assert.NotNull(gate.ServicedNote);
        Assert.Contains(ServicerSid, gate.ServicedNote!, StringComparison.Ordinal);
        Assert.Contains("write target", gate.ServicedNote!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANullActingUser_IsRefused_EvenWithAGrant()
    {
        var service = CreateService(servicerGroups: [ServicerSid],
            verdict: ProtectedPrincipalResult.Protected("protected", "Target:" + GroupDn));

        var gate = service.EvaluateTargetGate(Snapshot(), actingUser: null);

        Assert.NotNull(gate.Denial);
    }

    [Fact]
    public void AGrantInAnotherModule_ConfersNothingHere()
    {
        var service = CreateService(servicerGroups: null, otherModuleServicerGroups: [ServicerSid],
            verdict: ProtectedPrincipalResult.Protected("protected", "Target:" + GroupDn));

        var gate = service.EvaluateTargetGate(Snapshot(), UserIn(ServicerSid));

        Assert.NotNull(gate.Denial);
    }

    [Fact]
    public void AFailedTargetCheck_DeniesWithItsReason_EvenForAServicer()
    {
        var service = CreateService(servicerGroups: [ServicerSid],
            verdict: ProtectedPrincipalResult.Failed("store unreadable"));

        var gate = service.EvaluateTargetGate(Snapshot(), UserIn(ServicerSid));

        Assert.NotNull(gate.Denial);
        Assert.Contains("Protection check failed: store unreadable", gate.Denial!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnprotectedTarget_PassesWithNoNote()
    {
        var service = CreateService(servicerGroups: null, verdict: ProtectedPrincipalResult.NotProtected());

        var gate = service.EvaluateTargetGate(Snapshot(), UserIn(ServicerSid));

        Assert.Null(gate.Denial);
        Assert.Null(gate.ServicedNote);
    }

    // ----- BuildGroupSnapshot: the full identifier set (pgwt-2), UPN empty for a group -----

    [Fact]
    public void BuildGroupSnapshot_CarriesEveryIdentifier_AndKeepsUpnEmpty()
    {
        var group = PSObject.AsPSObject(new
        {
            Name = "Owned Group",
            SamAccountName = "OwnedGroup",
            mail = "owned@analog.com",
            ObjectGUID = "11111111-2222-3333-4444-555555555555",
        });

        var snap = SelfServiceGroupService.BuildGroupSnapshot(group, GroupDn);

        Assert.Equal(string.Empty, snap.UserPrincipalName);
        Assert.Equal("OwnedGroup", snap.SamAccountName);
        Assert.Equal("owned@analog.com", snap.PrimarySmtpAddress);
        Assert.Equal(GroupDn, snap.DistinguishedName);
        Assert.Equal("11111111-2222-3333-4444-555555555555", snap.ObjectGuid);
    }

    // ----- Tripwires: executor wiring - eligibility, then the gate, then the write -----

    [Fact]
    public void Executor_GatesTheTarget_AfterEligibility_BeforeTheWritePlan()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "SelfServiceGroups", "SelfServiceGroupService.cs"));
        var start = text.IndexOf("private async Task<MembershipChangeResult> ApplyMembershipChangeAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ApplyMembershipChangeAsync signature not found - tripwire is stale.");
        var end = text.IndexOf("public async Task<MembershipChangeResult> RemoveListedMemberAsync(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound ApplyMembershipChangeAsync - update the tripwire.");
        var body = text[start..end];

        var iElig = body.IndexOf("CallerCanManageMembers(ps, credential, groupDn, callerSid)", StringComparison.Ordinal);
        var iGate = body.IndexOf("EvaluateTargetGate(BuildGroupSnapshot(group, groupDn), actingUser)", StringComparison.Ordinal);
        var iDenial = body.IndexOf("return MembershipChangeResult.From(targetGate.Denial);", StringComparison.Ordinal);
        var iPlan = body.IndexOf("MembershipChangeReconciler.PlanWrite", StringComparison.Ordinal);

        Assert.True(iElig >= 0, "Eligibility check not found - tripwire is stale.");
        Assert.True(iGate > iElig, "The target gate must run AFTER eligibility (T2 - no protection oracle).");
        Assert.True(iDenial > iGate, "The target denial must RETURN before any write.");
        Assert.True(iPlan > iDenial, "The write plan must come after the target gate.");
        // Serviced member and target notes share the one audit slot.
        Assert.Contains("CombineNotes(protection.ServicedNote, targetGate.ServicedNote)", body, StringComparison.Ordinal);

        // Both public entry points hand the acting user to the executor - a null refuses.
        Assert.Contains("creds.Value, member, operation, protection, actingUser)", text, StringComparison.Ordinal);
        Assert.Contains("creds.Value, member, MembershipOperation.Remove, protection, actingUser)", text, StringComparison.Ordinal);

        // The group re-read fetches mail, so the snapshot's PrimarySmtpAddress is real.
        var props = text.IndexOf("private static readonly string[] GroupProperties =", StringComparison.Ordinal);
        var propsEnd = text.IndexOf(';', props);
        Assert.Contains("\"mail\"", text[props..propsEnd], StringComparison.Ordinal);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static ResolvedDirectoryPrincipal Snapshot() =>
        new("Test-AD", "Owned Group", string.Empty, "OwnedGroup", null, GroupDn, "group-guid", null);

    private static ClaimsPrincipal UserIn(string groupSid) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ANALOG\\tester"), new Claim(ClaimTypes.Role, groupSid)],
            "TestAuth"));

    private SelfServiceGroupService CreateService(
        string[]? servicerGroups,
        ProtectedPrincipalResult verdict,
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
            rows[ProtectedPrincipalServicerService.SectionKeyFor("SelfServiceGroups")] = servicerGroups;
        if (otherModuleServicerGroups is { Length: > 0 })
            rows[ProtectedPrincipalServicerService.SectionKeyFor("GroupManagement")] = otherModuleServicerGroups;
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

        var pp = new TargetScriptedPpService(env, config, moduleConfig,
            TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea)
        { TargetVerdict = verdict };

        return new SelfServiceGroupService(
            moduleCredentials, pp, servicers, NullLogger<SelfServiceGroupService>.Instance);
    }

    private sealed class TargetScriptedPpService : ProtectedPrincipalService
    {
        public TargetScriptedPpService(IWebHostEnvironment env, IConfiguration config,
            ModuleConfigService moduleConfig, ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
        { }

        public required ProtectedPrincipalResult TargetVerdict { get; init; }

        public override ProtectedPrincipalResult CheckWriteTarget(ResolvedDirectoryPrincipal target)
            => TargetVerdict;
    }
}
