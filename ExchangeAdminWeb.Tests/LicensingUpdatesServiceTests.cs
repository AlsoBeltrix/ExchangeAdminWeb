using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class LicensingUpdatesServiceTests : IDisposable
{
    private readonly string _tempDir;

    public LicensingUpdatesServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lic-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private LicensingUpdatesService CreateService(string? allowedTypes = null)
    {
        var configData = new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = _tempDir
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var moduleConfigLogger = Substitute.For<ILogger<ModuleConfigService>>();
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, moduleConfigLogger);

        if (allowedTypes != null)
            moduleConfig.SaveModuleConfig("LicensingUpdates", new Dictionary<string, string> { ["AllowedLicenseTypes"] = allowedTypes });

        var moduleCredentials = Substitute.For<ModuleCredentialService>(moduleConfig,
            new DelineaService(
                Substitute.For<IHttpClientFactory>().Also(f => f.CreateClient(Arg.Any<string>()).Returns(new HttpClient())),
                config, Substitute.For<ILogger<DelineaService>>(),
                new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), Substitute.For<ILogger<ExtendedLogService>>()),
                new OperationTraceService(config, new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>()))),
            Substitute.For<ILogger<ModuleCredentialService>>());

        var protectedPrincipalLogger = Substitute.For<ILogger<ProtectedPrincipalService>>();
        var delineaService = new DelineaService(
            Substitute.For<IHttpClientFactory>().Also(f => f.CreateClient(Arg.Any<string>()).Returns(new HttpClient())),
            config, Substitute.For<ILogger<DelineaService>>(),
            new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), Substitute.For<ILogger<ExtendedLogService>>()),
            new OperationTraceService(config, new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>())));
        var protectedPrincipalService = new ProtectedPrincipalService(env, config, moduleConfig, delineaService, protectedPrincipalLogger);

        var operationTrace = new OperationTraceService(config, new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>()));
        var audit = new AuditService(new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>()), operationTrace);

        return new LicensingUpdatesService(
            new ModuleCredentialService(moduleConfig, delineaService, Substitute.For<ILogger<ModuleCredentialService>>()),
            moduleConfig, protectedPrincipalService, operationTrace, audit,
            Substitute.For<ILogger<LicensingUpdatesService>>());
    }

    // --- GetAllowedLicenseTypes ---

    [Fact]
    public void GetAllowedLicenseTypes_Default_ReturnsFourTypes()
    {
        var service = CreateService();
        var types = service.GetAllowedLicenseTypes();

        Assert.Equal(4, types.Length);
        Assert.Contains("E5", types);
        Assert.Contains("EOP2+SOP2", types);
        Assert.Contains("F3", types);
        Assert.Contains("F3+EOP1", types);
    }

    [Fact]
    public void GetAllowedLicenseTypes_Custom_ReturnsConfigured()
    {
        var service = CreateService(allowedTypes: "SKU1,SKU2,SKU3");
        var types = service.GetAllowedLicenseTypes();

        Assert.Equal(3, types.Length);
        Assert.Contains("SKU1", types);
        Assert.Contains("SKU2", types);
        Assert.Contains("SKU3", types);
    }

    // --- PreviewCsvAsync validation ---

    [Fact]
    public async Task PreviewCsv_InvalidLicenseType_ReturnsError()
    {
        var service = CreateService();
        using var stream = MakeCsvStream("user@test.com");

        var result = await service.PreviewCsvAsync(stream, "test.csv", "INVALID_SKU", "admin", "127.0.0.1");

        Assert.False(result.Success);
        Assert.Contains("Invalid license type", result.Error);
    }

    [Fact]
    public async Task PreviewCsv_CredentialsUnavailable_ReturnsError()
    {
        var service = CreateService();
        using var stream = MakeCsvStream("user@test.com\nanother@test.com");

        var result = await service.PreviewCsvAsync(stream, "test.csv", "E5", "admin", "127.0.0.1");

        Assert.False(result.Success);
        Assert.Contains("credentials unavailable", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // --- CSV parsing (tested directly via internal ParseCsv) ---

    [Theory]
    [InlineData("User", true)]
    [InlineData("email", true)]
    [InlineData("UserPrincipalName", true)]
    [InlineData("samaccountname", true)]
    [InlineData("Name", true)]
    [InlineData("john.doe@test.com", false)]
    [InlineData("jdoe", false)]
    public void ParseCsv_HeaderDetection(string firstLine, bool isHeader)
    {
        var csv = $"{firstLine}\nactual.user@test.com\n";
        using var stream = MakeCsvStream(csv);

        var result = LicensingUpdatesService.ParseCsv(stream);

        if (isHeader)
        {
            Assert.Single(result);
            Assert.Equal("actual.user@test.com", result[0]);
        }
        else
        {
            Assert.Equal(2, result.Count);
            Assert.Equal(firstLine, result[0]);
        }
    }

    [Fact]
    public void ParseCsv_MultipleColumns_UsesFirstColumnOnly()
    {
        var csv = "user@test.com,extra data,more stuff\nanother@test.com,blah,blah\n";
        using var stream = MakeCsvStream(csv);

        var result = LicensingUpdatesService.ParseCsv(stream);

        Assert.Equal(2, result.Count);
        Assert.Equal("user@test.com", result[0]);
        Assert.Equal("another@test.com", result[1]);
    }

    [Fact]
    public void ParseCsv_EmptyLines_AreSkipped()
    {
        var csv = "user1@test.com\n\n\nuser2@test.com\n  \n";
        using var stream = MakeCsvStream(csv);

        var result = LicensingUpdatesService.ParseCsv(stream);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseCsv_QuotedFirstColumn_UnquotesValue()
    {
        var csv = "\"user@test.com\",\"Other Col\"\n";
        using var stream = MakeCsvStream(csv);

        var result = LicensingUpdatesService.ParseCsv(stream);

        Assert.Single(result);
        Assert.Equal("user@test.com", result[0]);
    }

    [Fact]
    public void ParseCsv_HeaderWithMail_IsDetected()
    {
        var csv = "mail,department\nuser@test.com,IT\n";
        using var stream = MakeCsvStream(csv);

        var result = LicensingUpdatesService.ParseCsv(stream);

        Assert.Single(result);
        Assert.Equal("user@test.com", result[0]);
    }

    // --- Target attribute is not configurable ---

    [Fact]
    public void TargetAttribute_IsHardCoded_ExtensionAttribute11()
    {
        // The service has a const TargetAttribute = "extensionAttribute11"
        // Verify it's not in ConfigFields as editable
        var catalog = new ModuleCatalog();
        var module = catalog.GetById("LicensingUpdates")!;
        var configKeys = module.ConfigFields.Select(f => f.Key).ToList();

        Assert.DoesNotContain("LicenseAttribute", configKeys);
        Assert.DoesNotContain("TargetAttribute", configKeys);
        Assert.Contains("AllowedLicenseTypes", configKeys);
    }

    private static MemoryStream MakeCsvStream(string content)
    {
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
    }
}

public static class NSubstituteExtensions
{
    public static T Also<T>(this T substitute, Action<T> configure)
    {
        configure(substitute);
        return substitute;
    }
}
