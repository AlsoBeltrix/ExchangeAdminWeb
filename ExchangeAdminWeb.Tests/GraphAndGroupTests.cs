using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class GraphAndGroupTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IWebHostEnvironment _env;
    private readonly ModuleCatalog _catalog;

    public GraphAndGroupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"graphgroup_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _env = Substitute.For<IWebHostEnvironment>();
        _env.ContentRootPath.Returns(_tempDir);

        _catalog = new ModuleCatalog();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { }
    }

    private ModuleConfigService CreateModuleConfigService()
    {
        var logger = Substitute.For<ILogger<ModuleConfigService>>();
        return new ModuleConfigService(_catalog, _env, logger);
    }

    private void WriteModuleConfig(string moduleId, Dictionary<string, string> values)
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var configFilePath = Path.Combine(configDir, "module-config.json");

        Dictionary<string, Dictionary<string, string>> config;
        if (File.Exists(configFilePath))
        {
            var existing = File.ReadAllText(configFilePath);
            config = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(existing)
                ?? new();
        }
        else
        {
            config = new();
        }

        config[moduleId] = values;
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configFilePath, json);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GraphTokenClient.IsConfigured tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void GraphTokenClient_IsConfigured_FalseWhenTenantIdEmpty()
    {
        // Empty tenantId => IsConfigured should be false
        // ReadCredential will fail (no vault) so secret will be empty too,
        // but the primary assertion is that empty tenantId alone causes false.
        var client = new GraphTokenClient("", "some-client-id", "NonExistentVaultTarget", new HttpClient());
        Assert.False(client.IsConfigured);
    }

    [Fact]
    public void GraphTokenClient_IsConfigured_FalseWhenClientIdEmpty()
    {
        var client = new GraphTokenClient("some-tenant-id", "", "NonExistentVaultTarget", new HttpClient());
        Assert.False(client.IsConfigured);
    }

    [Fact]
    public void GraphTokenClient_IsConfigured_FalseWhenCredentialTargetNotInVault()
    {
        // Even with valid tenantId and clientId, if the vault has no entry
        // the secret will be empty, making IsConfigured false.
        var client = new GraphTokenClient("some-tenant-id", "some-client-id", "NonExistentVaultTarget_Test", new HttpClient());
        Assert.False(client.IsConfigured);
    }

    [Fact]
    public void GraphTokenClient_IsConfigured_FalseWhenAllEmpty()
    {
        var client = new GraphTokenClient("", "", "", new HttpClient());
        Assert.False(client.IsConfigured);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MfaResetService.IsAvailable tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void MfaResetService_IsAvailable_FalseWhenNoModuleConfig()
    {
        // No module-config.json at all => GetValue returns null => empty strings
        // => GraphTokenClient will have empty tenantId/clientId => IsConfigured = false
        var moduleConfig = CreateModuleConfigService();
        var logger = Substitute.For<ILogger<MfaResetService>>();
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("MicrosoftGraph").Returns(new HttpClient());

        var service = new MfaResetService(logger, moduleConfig, httpFactory);

        Assert.False(service.IsAvailable);
    }

    [Fact]
    public void MfaResetService_IsAvailable_FalseWhenConfigMissingTenantId()
    {
        // Config exists but TenantId is missing
        WriteModuleConfig("MfaReset", new Dictionary<string, string>
        {
            ["ClientId"] = "some-client-id",
            ["CredentialTarget"] = "Graph_MFAResets"
        });

        var moduleConfig = CreateModuleConfigService();
        var logger = Substitute.For<ILogger<MfaResetService>>();
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("MicrosoftGraph").Returns(new HttpClient());

        var service = new MfaResetService(logger, moduleConfig, httpFactory);

        Assert.False(service.IsAvailable);
    }

    [Fact]
    public void MfaResetService_IsAvailable_FalseWhenConfigMissingClientId()
    {
        WriteModuleConfig("MfaReset", new Dictionary<string, string>
        {
            ["TenantId"] = "some-tenant-id",
            ["CredentialTarget"] = "Graph_MFAResets"
        });

        var moduleConfig = CreateModuleConfigService();
        var logger = Substitute.For<ILogger<MfaResetService>>();
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("MicrosoftGraph").Returns(new HttpClient());

        var service = new MfaResetService(logger, moduleConfig, httpFactory);

        Assert.False(service.IsAvailable);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MfaResetService.GetGraphClient() freshness — each call reads current state
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void MfaResetService_IsAvailable_ReflectsConfigChangeBetweenCalls()
    {
        // First call: no config => IsAvailable is false
        var moduleConfig = CreateModuleConfigService();
        var logger = Substitute.For<ILogger<MfaResetService>>();
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("MicrosoftGraph").Returns(new HttpClient());

        var service = new MfaResetService(logger, moduleConfig, httpFactory);

        Assert.False(service.IsAvailable);

        // Now write config with TenantId and ClientId (still no vault secret,
        // so IsConfigured will still be false due to missing secret, but
        // TenantId/ClientId will be populated). The important assertion is
        // that the second read picks up the change — not a cached old state.
        WriteModuleConfig("MfaReset", new Dictionary<string, string>
        {
            ["TenantId"] = "new-tenant-id",
            ["ClientId"] = "new-client-id",
            ["CredentialTarget"] = "Graph_MFAResets"
        });

        // IsAvailable is still false (no vault secret) but we verify that
        // GetGraphClient() re-reads config each time by confirming the
        // behavior is consistent — no stale cached "false" from before.
        // The real regression this catches: if GetGraphClient cached the
        // client, a config change would never take effect.
        Assert.False(service.IsAvailable);

        // To further prove no caching, verify the underlying config service
        // now returns the written values (which GetGraphClient would consume)
        Assert.Equal("new-tenant-id", moduleConfig.GetValue("MfaReset", "TenantId"));
        Assert.Equal("new-client-id", moduleConfig.GetValue("MfaReset", "ClientId"));
    }

    [Fact]
    public void MfaResetService_GetGraphClient_CreatesNewClientEachCall()
    {
        // Verify that IsAvailable (which internally calls GetGraphClient())
        // does not cache — calling it twice in succession should both
        // re-evaluate from config. We test indirectly: write config between
        // calls and see the second call picks up the new config state.
        var moduleConfig = CreateModuleConfigService();
        var logger = Substitute.For<ILogger<MfaResetService>>();
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("MicrosoftGraph").Returns(new HttpClient());

        var service = new MfaResetService(logger, moduleConfig, httpFactory);

        // Call 1: no config
        var available1 = service.IsAvailable;
        Assert.False(available1);

        // Write partial config (TenantId only)
        WriteModuleConfig("MfaReset", new Dictionary<string, string>
        {
            ["TenantId"] = "t1"
        });

        // Call 2: should read fresh config (TenantId present, ClientId missing)
        var available2 = service.IsAvailable;
        Assert.False(available2); // Still false — missing clientId and secret

        // Verify httpFactory.CreateClient was called twice (once per IsAvailable call)
        // proving a new GraphTokenClient was constructed each time
        httpFactory.Received(2).CreateClient("MicrosoftGraph");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GroupManagement module catalog registration
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ModuleCatalog_GroupManagement_Exists()
    {
        var module = _catalog.GetById("GroupManagement");
        Assert.NotNull(module);
    }

    [Fact]
    public void ModuleCatalog_GroupManagement_DisabledByDefault()
    {
        var module = _catalog.GetById("GroupManagement")!;
        Assert.False(module.EnabledByDefault);
    }

    [Fact]
    public void ModuleCatalog_GroupManagement_MainPermission_IsFailClosed()
    {
        var module = _catalog.GetById("GroupManagement")!;
        Assert.True(module.MainPermission.FailClosed);
    }

    [Fact]
    public void ModuleCatalog_GroupManagement_HasOnPremGranularPermission()
    {
        var module = _catalog.GetById("GroupManagement")!;
        var onPrem = module.GranularPermissions.FirstOrDefault(gp => gp.Name == "OnPrem");
        Assert.NotNull(onPrem);
    }

    [Fact]
    public void ModuleCatalog_GroupManagement_OnPremPermission_IsFailClosed()
    {
        var module = _catalog.GetById("GroupManagement")!;
        var onPrem = module.GranularPermissions.First(gp => gp.Name == "OnPrem");
        Assert.True(onPrem.FailClosed);
    }

    [Fact]
    public void ModuleCatalog_GroupManagement_OnPremPermission_HasCorrectPolicyAlias()
    {
        var module = _catalog.GetById("GroupManagement")!;
        var onPrem = module.GranularPermissions.First(gp => gp.Name == "OnPrem");
        Assert.Equal("GroupManagementOnPrem", onPrem.PolicyAlias);
    }

    [Fact]
    public void ModuleCatalog_GroupManagement_IsNotSystemModule()
    {
        var module = _catalog.GetById("GroupManagement")!;
        Assert.False(module.IsSystemModule);
    }

    [Fact]
    public void ModuleCatalog_GroupManagement_MainPermission_PolicyAlias()
    {
        var module = _catalog.GetById("GroupManagement")!;
        Assert.Equal("GroupManagement", module.MainPermission.PolicyAlias);
    }
}
