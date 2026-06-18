using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class EmergencyDisableServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configDir;

    public EmergencyDisableServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emergency-disable-test-{Guid.NewGuid():N}");
        _configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task DisableAsync_BlankTicket_FailsBeforeCredentialLookup()
    {
        var service = CreateService();

        var result = await service.DisableAsync(MakePrincipal(), "  ", "DOMAIN\\admin", "10.0.0.1");

        Assert.False(result.Success);
        Assert.Null(result.Snapshot);
        Assert.Contains("Ticket number is required", result.Error);
        Assert.Contains(result.Steps, s => s.Step == "TicketValidation" && s.Status == "FAILED");
        Assert.DoesNotContain(result.Steps, s => s.Step == "ProtectedPrincipalCheck");
        Assert.DoesNotContain(result.Steps, s => s.Step == "GetADCredentials");
    }

    [Fact]
    public async Task DisableAsync_ProtectedPrincipal_BlocksBeforeCredentialLookup()
    {
        var protectedConfig = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = new[] { "ceo@contoso.com" },
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });
        var service = CreateService(protectedPrincipalsJson: protectedConfig);

        var result = await service.DisableAsync(MakePrincipal("ceo@contoso.com"), "INC001", "DOMAIN\\admin", "10.0.0.1");

        Assert.False(result.Success);
        Assert.Null(result.Snapshot);
        Assert.Contains("protected principal", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Steps, s => s.Step == "TicketValidation" && s.Status == "OK");
        Assert.Contains(result.Steps, s => s.Step == "ProtectedPrincipalCheck" && s.Status == "BLOCKED");
        Assert.DoesNotContain(result.Steps, s => s.Step == "GetADCredentials");
    }

    [Fact]
    public async Task DisableAsync_CorruptProtectedPrincipalConfig_FailsClosedBeforeCredentialLookup()
    {
        File.WriteAllText(Path.Combine(_configDir, "protected-principals.json"), "not valid json {{{");
        var service = CreateService();

        var result = await service.DisableAsync(MakePrincipal(), "INC001", "DOMAIN\\admin", "10.0.0.1");

        Assert.False(result.Success);
        Assert.Null(result.Snapshot);
        Assert.Contains("Protected principal check failed", result.Error);
        Assert.Contains(result.Steps, s => s.Step == "ProtectedPrincipalCheck" && s.Status == "BLOCKED");
        Assert.DoesNotContain(result.Steps, s => s.Step == "GetADCredentials");
    }

    [Fact]
    public async Task DisableAsync_MissingAdCredentialConfig_StopsBeforeGraphAndMutationSteps()
    {
        var service = CreateService();

        var result = await service.DisableAsync(MakePrincipal(), "INC001", "DOMAIN\\admin", "10.0.0.1");

        Assert.False(result.Success);
        Assert.Null(result.Snapshot);
        Assert.Contains("AD credentials unavailable", result.Error);
        Assert.Contains(result.Steps, s => s.Step == "TicketValidation" && s.Status == "OK");
        Assert.Contains(result.Steps, s => s.Step == "ProtectedPrincipalCheck" && s.Status == "OK");
        Assert.Contains(result.Steps, s => s.Step == "GetADCredentials" && s.Status == "FAILED");
        Assert.DoesNotContain(result.Steps, s => s.Step == "GetGraphCredentials");
        Assert.DoesNotContain(result.Steps, s => s.Step is "DisableAD" or "ResetPassword" or "RevokeEntraSessions" or "DisableEntra");
    }

    [Fact]
    public void ModuleCatalog_EmergencyDisable_IsFailClosedAndVersioned()
    {
        var catalog = new ModuleCatalog();

        var module = catalog.GetById("EmergencyDisable");

        Assert.NotNull(module);
        Assert.False(module.EnabledByDefault);
        Assert.True(module.MainPermission.FailClosed);
        Assert.Equal("1.0.4", module.Version);
        Assert.Contains(module.ConfigFields, f => f.Key == "DelineaSecretId");
        Assert.Contains(module.ConfigFields, f => f.Key == "GraphDelineaSecretId");
        Assert.Contains(module.ConfigFields, f => f.Key == "NotifySecurityTeam");
    }

    private EmergencyDisableService CreateService(string? protectedPrincipalsJson = null)
    {
        if (protectedPrincipalsJson != null)
            File.WriteAllText(Path.Combine(_configDir, "protected-principals.json"), protectedPrincipalsJson);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:LogRoot"] = _tempDir,
                ["Audit:RotationPeriod"] = "daily",
                ["OperationTrace:Enabled"] = "true",
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Email:AdminNotificationEmail"] = ""
            })
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, env, TestConfigStore.CreateModuleConfig(_tempDir), Substitute.For<ILogger<ModuleConfigService>>());
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var operationTrace = new OperationTraceService(config, jsonlLog);
        var audit = new AuditService(jsonlLog, operationTrace);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        var extendedLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), Substitute.For<ILogger<ExtendedLogService>>());
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extendedLog, operationTrace);
        var moduleCredentials = new ModuleCredentialService(moduleConfig, delinea, Substitute.For<ILogger<ModuleCredentialService>>());
        var protectedPrincipalService = new ProtectedPrincipalService(env, config, moduleConfig, delinea, Substitute.For<ILogger<ProtectedPrincipalService>>());
        var email = new EmailService(config, Substitute.For<ILogger<EmailService>>());

        return new EmergencyDisableService(
            moduleCredentials,
            moduleConfig,
            protectedPrincipalService,
            operationTrace,
            audit,
            email,
            delinea,
            httpClientFactory,
            env,
            config,
            Substitute.For<ILogger<EmergencyDisableService>>());
    }

    private static ResolvedDirectoryPrincipal MakePrincipal(string upn = "user@contoso.com")
    {
        return new ResolvedDirectoryPrincipal(
            Source: "Test",
            DisplayName: upn.Split('@')[0],
            UserPrincipalName: upn,
            SamAccountName: upn.Split('@')[0],
            PrimarySmtpAddress: upn,
            DistinguishedName: $"CN={upn.Split('@')[0]},OU=Users,DC=contoso,DC=com",
            ObjectGuid: Guid.NewGuid().ToString(),
            EntraObjectId: Guid.NewGuid().ToString());
    }
}
