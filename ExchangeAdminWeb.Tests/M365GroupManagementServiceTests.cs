using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the in-service protected-principal gate on M365 group member/owner writes.
/// The Constitution requires the check "immediately before the write" and forbids
/// relying on UI hiding, so the service must enforce protection independently and fail
/// closed when the directory resolver is unavailable. The gate runs before any Graph
/// client is constructed, so these refusal-path tests need no Graph seam: when the
/// resolver is unavailable the write is denied before any outward call.
/// (Plan: docs/M365MemberOwnerManagement-Plan.md, slice 1.)
/// </summary>
public class M365GroupManagementServiceTests : IDisposable
{
    private readonly string _tempDir;

    public M365GroupManagementServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"m365grpmgmt_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { }
    }

    // Builds a service whose ProtectedPrincipalService has NO directory-read secret
    // configured. ResolveWithStatusAsync therefore returns Unavailable, which the
    // in-service gate must treat as fail-closed (deny before any Graph write).
    private M365GroupManagementService CreateServiceWithUnavailableResolver()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delinea:SecretServerUrl"] = "https://fake.local"
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
        var audit = new AuditService(jsonlLog, operationTrace);
        var protectedPrincipals = new ProtectedPrincipalService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea, Substitute.For<ILogger<ProtectedPrincipalService>>());

        return new M365GroupManagementService(
            moduleConfig, delinea, httpClientFactory, operationTrace, audit, protectedPrincipals,
            Substitute.For<ILogger<M365GroupManagementService>>());
    }

    [Theory]
    [InlineData("user@contoso.com")]
    [InlineData("sAMName")]
    [InlineData("CONTOSO\\sAMName")]
    public async Task AddMemberAsync_ResolverUnavailable_FailsClosed(string identity)
    {
        var service = CreateServiceWithUnavailableResolver();

        var result = await service.AddMemberAsync("00000000-0000-0000-0000-000000000001", identity);

        Assert.False(result.Success);
        Assert.Contains("Protection check unavailable", result.Message);
    }

    [Theory]
    [InlineData("user@contoso.com")]
    [InlineData("sAMName")]
    [InlineData("CONTOSO\\sAMName")]
    public async Task AddOwnerAsync_ResolverUnavailable_FailsClosed(string identity)
    {
        var service = CreateServiceWithUnavailableResolver();

        var result = await service.AddOwnerAsync("00000000-0000-0000-0000-000000000001", identity);

        Assert.False(result.Success);
        Assert.Contains("Protection check unavailable", result.Message);
    }

    [Theory]
    [InlineData("user@contoso.com")]
    [InlineData("sAMName")]
    [InlineData("CONTOSO\\sAMName")]
    public async Task RemoveMemberAsync_ResolverUnavailable_FailsClosed(string identity)
    {
        var service = CreateServiceWithUnavailableResolver();

        var result = await service.RemoveMemberAsync(
            "00000000-0000-0000-0000-000000000001",
            "00000000-0000-0000-0000-000000000002",
            identity);

        Assert.False(result.Success);
        Assert.Contains("Protection check unavailable", result.Message);
    }

    [Theory]
    [InlineData("user@contoso.com")]
    [InlineData("sAMName")]
    [InlineData("CONTOSO\\sAMName")]
    public async Task RemoveOwnerAsync_ResolverUnavailable_FailsClosed(string identity)
    {
        var service = CreateServiceWithUnavailableResolver();

        var result = await service.RemoveOwnerAsync(
            "00000000-0000-0000-0000-000000000001",
            "00000000-0000-0000-0000-000000000002",
            identity);

        Assert.False(result.Success);
        Assert.Contains("Protection check unavailable", result.Message);
    }
}
