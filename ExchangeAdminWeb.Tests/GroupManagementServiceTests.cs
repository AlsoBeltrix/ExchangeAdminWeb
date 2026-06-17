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
    // configured. ResolveWithStatusAsync therefore returns Unavailable, which the
    // in-service gate must treat as fail-closed (deny before any AD write). No real
    // runspace or AD module is touched because the gate aborts first.
    private GroupManagementService CreateServiceWithUnavailableResolver()
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
        var moduleConfig = new ModuleConfigService(catalog, env, Substitute.For<ILogger<ModuleConfigService>>());

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, Substitute.For<ILogger<ExtendedLogService>>());
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var operationTrace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extLog, operationTrace);
        var moduleCredentials = new ModuleCredentialService(moduleConfig, delinea, Substitute.For<ILogger<ModuleCredentialService>>());
        var protectedPrincipals = new ProtectedPrincipalService(env, config, moduleConfig, delinea, Substitute.For<ILogger<ProtectedPrincipalService>>());

        return new GroupManagementService(
            moduleConfig, moduleCredentials, protectedPrincipals,
            Substitute.For<ILogger<GroupManagementService>>());
    }

    [Theory]
    [InlineData("user@contoso.com")]
    [InlineData("sAMName")]          // no '@' — the page's old gate skipped this entirely
    [InlineData("CONTOSO\\sAMName")] // DOMAIN\user — also skipped by the old gate
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
}
