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
            moduleConfig, moduleCredentials, protectedPrincipals,
            Substitute.For<ILogger<GroupManagementService>>());
    }

    [Theory]
    [InlineData("user@contoso.com")]
    [InlineData("sAMName")]          // no '@' - the page's old gate skipped this entirely
    [InlineData("CONTOSO\\sAMName")] // DOMAIN\user - also skipped by the old gate
    public async Task AddMemberAsync_ResolverUnavailable_FailsClosed(string member)
    {
        var service = CreateServiceWithUnavailableResolver();

        var result = await service.AddMemberAsync("CN=Some Group,OU=Groups,DC=contoso,DC=com", member, "SomeGroup");

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

        var result = await service.RemoveMemberAsync("CN=Some Group,OU=Groups,DC=contoso,DC=com", member, "SomeGroup");

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

        return new GroupManagementService(moduleConfig, moduleCredentials, pp,
            Substitute.For<ILogger<GroupManagementService>>());
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

        var result = await service.AddMemberAsync("CN=Some Group,OU=Groups,DC=contoso,DC=com", "VIPalias@o365.contoso.com", "SomeGroup");

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

        var result = await service.RemoveMemberAsync("CN=Some Group,OU=Groups,DC=contoso,DC=com", "VIPalias@o365.contoso.com", "SomeGroup");

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
