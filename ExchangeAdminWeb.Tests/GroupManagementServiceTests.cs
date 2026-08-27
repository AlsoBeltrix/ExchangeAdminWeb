using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the in-service protected-principal gate added to GroupManagementService.
/// The Constitution requires the check "immediately before the write" and forbids
/// relying on UI hiding; the page check was '@'-gated and skipped non-page callers,
/// so the service must enforce it independently and fail closed when the directory
/// resolver is unavailable.
/// </summary>
public class GroupManagementServiceTests : IDisposable
{
    private readonly string _tempDir;

    public GroupManagementServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"groupmgmt_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { }
    }

    // Builds a service whose ProtectedPrincipalService has NO directory-read secret
    // configured. Resolution therefore returns Unavailable before it can reach the
    // Exchange fallback, and the in-service gate must treat that as fail-closed (deny
    // before any AD write). No real runspace or AD module is touched because the gate
    // aborts first.
    private GroupManagementService CreateServiceWithUnavailableResolver()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Audit:LogRoot"] = _tempDir
            })
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, env, TestConfigStore.CreateModuleConfig(_tempDir), Substitute.For<ILogger<ModuleConfigService>>());

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), Substitute.For<ILogger<ExtendedLogService>>());
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var operationTrace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extLog, operationTrace);
        var moduleCredentials = new ModuleCredentialService(moduleConfig, delinea, Substitute.For<ILogger<ModuleCredentialService>>());
        var protectedPrincipals = new ProtectedPrincipalService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea, Substitute.For<ILogger<ProtectedPrincipalService>>());

        return new GroupManagementService(
            moduleConfig, moduleCredentials, protectedPrincipals, CreateDenyingServicers(config, env),
            Substitute.For<ILogger<GroupManagementService>>());
    }

    [Theory]
    [InlineData("user@contoso.com")]
    [InlineData("sAMName")]          // no '@' - the page's old gate skipped this entirely
    [InlineData("CONTOSO\\sAMName")] // DOMAIN\user - also skipped by the old gate
    public async Task AddMemberAsync_ResolverUnavailable_FailsClosed(string member)
    {
        var service = CreateServiceWithUnavailableResolver();

        var result = await service.AddMemberAsync("CN=Some Group,OU=Groups,DC=contoso,DC=com", member, actingUser: null, "SomeGroup");

        Assert.False(result.Success);
        Assert.Contains("Protection check unavailable", result.Message);
    }

    [Theory]
    [InlineData("user@contoso.com")]
    [InlineData("sAMName")]
    [InlineData("CONTOSO\\sAMName")]
    public async Task RemoveMemberAsync_ResolverUnavailable_FailsClosed(string member)
    {
        var service = CreateServiceWithUnavailableResolver();

        var result = await service.RemoveMemberAsync("CN=Some Group,OU=Groups,DC=contoso,DC=com", member, actingUser: null, "SomeGroup");

        Assert.False(result.Success);
        Assert.Contains("Protection check unavailable", result.Message);
    }

    /// <summary>
    /// Scripts only the Exchange-fallback seam. ResolveWithStatusAsync is left as the real
    /// method, which returns Unavailable here (no directory-read credential) - so a gate still
    /// calling the AD-only method denies, and one calling the fallback sees the scripted verdict.
    /// That difference is what makes this test able to detect the wrong seam.
    /// </summary>
    private sealed class FallbackOnlyPpService : ProtectedPrincipalService
    {
        public ProtectedPrincipalService.ResolutionStatus FallbackStatus = ResolutionStatus.Resolved;
        public ProtectedPrincipalResult Verdict = ProtectedPrincipalResult.NotProtected();

        public FallbackOnlyPpService(IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
            ExchangeAdminWeb.Services.Storage.ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, Substitute.For<ILogger<ProtectedPrincipalService>>())
        { }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity)
        {
            ResolvedDirectoryPrincipal? p = FallbackStatus == ResolutionStatus.Resolved
                ? new ResolvedDirectoryPrincipal("Test", identity, identity, "sam", identity, "CN=x,DC=y", "guid", null)
                : null;
            return Task.FromResult((p, FallbackStatus));
        }

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
            => Task.FromResult(Verdict);
    }

    private GroupManagementService CreateServiceWith(FallbackOnlyPpService pp, out ModuleConfigService moduleConfig)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Audit:LogRoot"] = _tempDir
            }).Build();

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_tempDir), Substitute.For<ILogger<ModuleConfigService>>());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), Substitute.For<ILogger<ExtendedLogService>>());
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var trace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extLog, trace);
        var moduleCredentials = new ModuleCredentialService(moduleConfig, delinea, Substitute.For<ILogger<ModuleCredentialService>>());

        return new GroupManagementService(moduleConfig, moduleCredentials, pp, CreateDenyingServicers(config, env),
            Substitute.For<ILogger<GroupManagementService>>());
    }

    // Real servicer service over a store with no ProtectedServicer row, so it denies.
    // Every assertion in this class is about the PROTECTION decision and must keep
    // passing unchanged: a servicer path that made one of them pass would be a
    // refusal quietly turned into an allow.
    private ProtectedPrincipalServicerService CreateDenyingServicers(IConfiguration config, IWebHostEnvironment env)
    {
        var sectionAccess = new SectionAccessService(
            config, Substitute.For<ILogger<SectionAccessService>>(), env, new ModuleCatalog(),
            new ExchangeAdminWeb.Services.Storage.SectionAccessRepository(TestConfigStore.Create(_tempDir)));
        return new ProtectedPrincipalServicerService(
            sectionAccess, Substitute.For<ILogger<ProtectedPrincipalServicerService>>());
    }

    private FallbackOnlyPpService CreateFallbackPp()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Audit:LogRoot"] = _tempDir
            }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_tempDir), Substitute.For<ILogger<ModuleConfigService>>());
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), Substitute.For<ILogger<ExtendedLogService>>());
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var trace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extLog, trace);
        return new FallbackOnlyPpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea);
    }

    [Fact]
    public async Task AddMemberAsync_ResolvesThroughTheExchangeFallback_NotAdAlone()
    {
        // The alias bypass this module carried: protected rows are stored as primary SMTP
        // addresses, so a protected member supplied by a secondary alias missed AD entirely,
        // resolved NotFound, and was allowed straight through. The gate must consult the
        // Exchange-backed seam, which returns the canonical identity that then matches.
        var pp = CreateFallbackPp();
        pp.FallbackStatus = ProtectedPrincipalService.ResolutionStatus.Resolved;
        pp.Verdict = ProtectedPrincipalResult.Protected("matched", "User:vip@contoso.com");
        var service = CreateServiceWith(pp, out _);

        var result = await service.AddMemberAsync("CN=Some Group,OU=Groups,DC=contoso,DC=com", "VIPalias@o365.contoso.com", actingUser: null, "SomeGroup");

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveMemberAsync_ResolvesThroughTheExchangeFallback_NotAdAlone()
    {
        var pp = CreateFallbackPp();
        pp.FallbackStatus = ProtectedPrincipalService.ResolutionStatus.Resolved;
        pp.Verdict = ProtectedPrincipalResult.Protected("matched", "User:vip@contoso.com");
        var service = CreateServiceWith(pp, out _);

        var result = await service.RemoveMemberAsync("CN=Some Group,OU=Groups,DC=contoso,DC=com", "VIPalias@o365.contoso.com", actingUser: null, "SomeGroup");

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ----- Nesting plan S5b (gmn-1): a GROUP member must REACH CheckAsync on the RESOLVED
    // principal before any write path. Resolution needs a live directory, so the internal
    // virtual ResolveMemberForWrite seam scripts it; the recording ProtectedPrincipalService
    // captures what the gate was consulted with - a test that only sees the refusal passes for
    // the wrong reason when the target was silently dropped before the gate.

    private sealed class RecordingPpService : ProtectedPrincipalService
    {
        public readonly List<ResolvedDirectoryPrincipal> CheckedTargets = new();
        public ProtectedPrincipalResult Verdict = ProtectedPrincipalResult.NotProtected();

        public RecordingPpService(IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
            ExchangeAdminWeb.Services.Storage.ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, Substitute.For<ILogger<ProtectedPrincipalService>>())
        { }

        // A GROUP identity resolves NotFound on the user-shaped fallback - the exact
        // pass-through gmn-1 exploited; the class-aware gate downstream must still fire.
        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity)
            => Task.FromResult(((ResolvedDirectoryPrincipal?)null, ResolutionStatus.NotFound));

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
        {
            CheckedTargets.Add(target);
            return Task.FromResult(Verdict);
        }
    }

    private sealed class ScriptedResolverService : GroupManagementService
    {
        public ResolvedMember Scripted;

        public ScriptedResolverService(ModuleConfigService moduleConfig, ModuleCredentialService creds,
            ProtectedPrincipalService pp, ProtectedPrincipalServicerService servicers)
            : base(moduleConfig, creds, pp, servicers, Substitute.For<ILogger<GroupManagementService>>())
        { }

        internal override ResolvedMember ResolveMemberForWrite(
            (string username, string password, string domain) creds,
            string memberIdentity, string? memberDn, string? memberObjectGuid) => Scripted;

        internal override Task<(string username, string password, string domain)?> GetCredentialsAsync(string purpose)
            => Task.FromResult<(string username, string password, string domain)?>(("u", "p", "d"));
    }

    private RecordingPpService CreateRecordingPp()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Audit:LogRoot"] = _tempDir
            }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_tempDir), Substitute.For<ILogger<ModuleConfigService>>());
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), Substitute.For<ILogger<ExtendedLogService>>());
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var trace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extLog, trace);
        return new RecordingPpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea);
    }

    private ScriptedResolverService CreateScriptedService(RecordingPpService pp)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Audit:LogRoot"] = _tempDir
            }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_tempDir), Substitute.For<ILogger<ModuleConfigService>>());
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), Substitute.For<ILogger<ExtendedLogService>>());
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var trace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extLog, trace);
        var moduleCredentials = new ModuleCredentialService(moduleConfig, delinea, Substitute.For<ILogger<ModuleCredentialService>>());
        return new ScriptedResolverService(moduleConfig, moduleCredentials, pp, CreateDenyingServicers(config, env));
    }

    [Fact]
    public async Task AddMemberAsync_GroupMember_ReachesCheckAsync_OnTheResolvedPrincipal_AndIsRefused()
    {
        var pp = CreateRecordingPp();
        pp.Verdict = ProtectedPrincipalResult.Protected("matched", "Group:Domain Admins");
        var service = CreateScriptedService(pp);
        service.Scripted = new GroupManagementService.ResolvedMember(
            new ResolvedDirectoryPrincipal("Test", "Ops Team", string.Empty, "OpsTeam",
                null, "CN=Ops Team,OU=Groups,DC=contoso,DC=com", "guid-1", null),
            IsGroup: true, Error: null);

        var result = await service.AddMemberAsync("CN=Target,OU=Groups,DC=contoso,DC=com", "Ops Team", actingUser: null, "Target");

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
        // gmn-1's exact failure mode reversed: the gate is consulted, ON the resolved GROUP
        // principal (AC14) - whose UPN is empty so it cannot false-match a protected USER.
        var target = Assert.Single(pp.CheckedTargets);
        Assert.Equal("CN=Ops Team,OU=Groups,DC=contoso,DC=com", target.DistinguishedName);
        Assert.Equal(string.Empty, target.UserPrincipalName);
    }

    [Fact]
    public async Task AddMemberAsync_UnresolvableMember_IsRefused_NotAllowedThrough()
    {
        // AC14: a member that cannot be resolved is refused, never dropped through as
        // not-found the way the old user-only resolver dropped groups.
        var pp = CreateRecordingPp();
        var service = CreateScriptedService(pp);
        service.Scripted = GroupManagementService.ResolvedMember.Failed("'ghost' was not found in AD as a user or group.");

        var result = await service.AddMemberAsync("CN=Target,OU=Groups,DC=contoso,DC=com", "ghost", actingUser: null, "Target");

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(pp.CheckedTargets);
    }

    [Fact]
    public async Task RemoveMemberAsync_GroupByGuid_ReachesCheckAsync_AndIsRefusedWhenProtected()
    {
        // AC14b rides S1's self-match: a group that IS a configured protected group refuses
        // here through the same CheckAsync consultation this test proves happens.
        var pp = CreateRecordingPp();
        pp.Verdict = ProtectedPrincipalResult.Protected("matched", "Group:Domain Admins");
        var service = CreateScriptedService(pp);
        service.Scripted = new GroupManagementService.ResolvedMember(
            new ResolvedDirectoryPrincipal("Test", "Ops Team", string.Empty, "OpsTeam",
                null, "CN=Ops Team,OU=Groups,DC=contoso,DC=com", "guid-1", null),
            IsGroup: true, Error: null);

        var result = await service.RemoveMemberAsync("CN=Target,OU=Groups,DC=contoso,DC=com", "Ops Team", actingUser: null, "Target", memberObjectGuid: "guid-1");

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(pp.CheckedTargets);
    }

    // ----- gmn-6: the resolved-principal gate is UNCONDITIONAL - users too -----

    [Fact]
    public async Task AddMemberAsync_ResolvedUser_IsGated_WhenThePreGateMisses()
    {
        // The pre-gate resolves NotFound by construction (RecordingPpService); the resolved
        // USER is protected. The conditional gate this finding removed would have skipped it.
        var pp = CreateRecordingPp();
        pp.Verdict = ProtectedPrincipalResult.Protected("matched", "Group:Protected Ops");
        var service = CreateScriptedService(pp);
        service.Scripted = new GroupManagementService.ResolvedMember(
            new ResolvedDirectoryPrincipal("Test", "Pat Protected", "pat@contoso.com", "pat",
                null, "CN=Pat Protected,OU=Users,DC=contoso,DC=com", "guid-u1", null),
            IsGroup: false, Error: null);

        var result = await service.AddMemberAsync("CN=Target,OU=Groups,DC=contoso,DC=com", "Pat Protected", actingUser: null, "Target");

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
        var target = Assert.Single(pp.CheckedTargets);
        Assert.Equal("CN=Pat Protected,OU=Users,DC=contoso,DC=com", target.DistinguishedName);
    }

    [Fact]
    public async Task RemoveMemberAsync_ResolvedUserByGuid_IsGated_DespiteANonBlankLabel()
    {
        // gmn-6's exact bypass shape: a mail-less listed member arrives with its DISPLAY NAME
        // as the label - non-blank, so the old "gate when blank" condition skipped the gate -
        // while the GUID resolves the real, protected user.
        var pp = CreateRecordingPp();
        pp.Verdict = ProtectedPrincipalResult.Protected("matched", "Group:Protected Ops");
        var service = CreateScriptedService(pp);
        service.Scripted = new GroupManagementService.ResolvedMember(
            new ResolvedDirectoryPrincipal("Test", "Pat Protected", "pat@contoso.com", "pat",
                null, "CN=Pat Protected,OU=Users,DC=contoso,DC=com", "guid-u1", null),
            IsGroup: false, Error: null);

        var result = await service.RemoveMemberAsync("CN=Target,OU=Groups,DC=contoso,DC=com", "Pat Protected", actingUser: null, "Target", memberObjectGuid: "guid-u1");

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
        var target = Assert.Single(pp.CheckedTargets);
        Assert.Equal("CN=Pat Protected,OU=Users,DC=contoso,DC=com", target.DistinguishedName);
    }
}
