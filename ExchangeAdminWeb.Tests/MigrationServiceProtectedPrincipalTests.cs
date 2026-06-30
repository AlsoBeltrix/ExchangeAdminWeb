using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the in-service protected-principal gate added to MigrationService (GAP 2 from the
/// 2026-06-29 protected-principal sweep). Owner decision (2026-06-30): protected targets are
/// filtered out of a batch and reported clearly; one protected target never blocks the whole
/// batch; if every target is protected, nothing is created.
///
/// The PowerShell New-MigrationBatch hop is not exercised here (no EXO session); these tests
/// assert the partition decision and the "create nothing" path, which is where the security
/// logic lives. The real-gate fail-closed behaviour is exercised via an unavailable resolver.
/// </summary>
public class MigrationServiceProtectedPrincipalTests : IDisposable
{
    private readonly string _tempDir;

    public MigrationServiceProtectedPrincipalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"migration-protected-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // --- PartitionByProtectionAsync: decision logic via the checker seam ---

    [Fact]
    public async Task Partition_MixOfProtectedAndClean_SplitsAndReportsReasons()
    {
        var service = CreateService();

        var (allowed, excluded) = await service.PartitionByProtectionAsync(
            new[] { "clean1@contoso.com", "ceo@contoso.com", "clean2@contoso.com" },
            checker: id => Task.FromResult<PermissionResult?>(
                id == "ceo@contoso.com"
                    ? PermissionResult.Fail("This mailbox is a protected principal. Operation not permitted.")
                    : null));

        Assert.Equal(new[] { "clean1@contoso.com", "clean2@contoso.com" }, allowed);
        var only = Assert.Single(excluded);
        Assert.Contains("ceo@contoso.com", only);
        Assert.Contains("protected principal", only);
    }

    [Fact]
    public async Task Partition_AllClean_AllAllowed()
    {
        var service = CreateService();

        var (allowed, excluded) = await service.PartitionByProtectionAsync(
            new[] { "a@contoso.com", "b@contoso.com" },
            checker: _ => Task.FromResult<PermissionResult?>(null));

        Assert.Equal(2, allowed.Count);
        Assert.Empty(excluded);
    }

    // --- CreateMigrationBatchAsync: real gate, no directory-read secret => Unavailable
    //     => every target excluded (fail-closed) => nothing created, no EXO call. ---

    [Fact]
    public async Task CreateBatch_ToCloud_AllTargetsProtected_CreatesNothing_FailsClosed()
    {
        var service = CreateService();

        var result = await service.CreateMigrationBatchAsync(
            MigrationDirection.ToCloud,
            new List<string> { "user@contoso.com" },
            "batch-1",
            autoStart: false,
            autoComplete: false);

        Assert.False(result.Success);
        Assert.Contains("protected principal", result.Message);
        var excluded = Assert.Single(result.ExcludedTargets!);
        Assert.Contains("user@contoso.com", excluded);
        // Unavailable resolver => fail-closed exclusion, not a clean "not protected".
        Assert.Contains("Protection check unavailable", excluded);
    }

    [Fact]
    public async Task CreateBatch_ToOnPrem_AllTargetsProtected_CreatesNothing_FailsClosed()
    {
        // ToOnPrem resolves target databases before the gate; configure one so the method
        // reaches the partition step rather than failing on missing databases.
        var service = CreateService(onPremTargetDatabases: "db-a");

        var result = await service.CreateMigrationBatchAsync(
            MigrationDirection.ToOnPrem,
            new List<string> { "u1@contoso.com", "u2@contoso.com" },
            "batch-2",
            autoStart: false,
            autoComplete: false);

        Assert.False(result.Success);
        Assert.Contains("protected principals", result.Message);
        Assert.Equal(2, result.ExcludedTargets!.Count);
    }

    private MigrationService CreateService(string? onPremTargetDatabases = null)
    {
        var configData = new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Email:AdminNotificationEmail"] = "",
            // No directory-read secret configured => ProtectedPrincipalService.ResolveWithStatusAsync
            // returns Unavailable => the gate fails closed.
        };
        if (onPremTargetDatabases != null)
            configData["Migration:OnPremTargetDatabases"] = onPremTargetDatabases;

        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

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
        var enablement = new ModuleEnablementService(catalog, env, moduleConfig, TestConfigStore.CreateModuleEnablement(_tempDir), config, Substitute.For<ILogger<ModuleEnablementService>>());
        var exoPool = new ExoConnectionPool(config, moduleConfig, enablement, Substitute.For<ILogger<ExoConnectionPool>>(), operationTrace);

        return new MigrationService(config, exoPool, delinea, Substitute.For<ILogger<MigrationService>>(), moduleConfig, moduleCredentials, operationTrace, protectedPrincipals);
    }
}
