using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards that MfaResetService reads the Graph credential under the new key ONLY, with no
/// fallback to the retired DelineaSecretId key (docs/GraphSecretKeyMigration-Plan.md). The
/// one-time migration moves any stranded value to the new key at startup, so the service must
/// not also read the old one.
/// </summary>
public class MfaResetServiceConfigTests
{
    [Fact]
    public void IsAvailable_True_WhenNewKeySet()
    {
        using var temp = new TempDir();
        var moduleConfig = ConfigWith(temp.Path, new() { ["GraphDelineaSecretId"] = "123" });
        var service = CreateService(temp.Path, moduleConfig);

        Assert.True(service.IsAvailable);
    }

    [Fact]
    public void IsAvailable_False_WhenOnlyOldKeySet()
    {
        // A value left under the retired old key must NOT make the module appear configured;
        // the fallback is gone. (The startup migration is what moves such a value to the new key.)
        using var temp = new TempDir();
        var moduleConfig = ConfigWith(temp.Path, new() { ["DelineaSecretId"] = "123" });
        var service = CreateService(temp.Path, moduleConfig);

        Assert.False(service.IsAvailable);
    }

    private static ModuleConfigService ConfigWith(string contentRoot, Dictionary<string, string> values)
    {
        var store = TestConfigStore.Create(contentRoot);
        new ModuleConfigRepository(store).SaveModule("MfaReset", values);

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(contentRoot);
        return new ModuleConfigService(new ModuleCatalog(), env,
            new ModuleConfigRepository(store), Substitute.For<ILogger<ModuleConfigService>>());
    }

    private static MfaResetService CreateService(string contentRoot, ModuleConfigService moduleConfig)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Audit:LogRoot"] = contentRoot
            })
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(contentRoot);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var operationTrace = new OperationTraceService(config, jsonlLog);
        var extendedLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(contentRoot), Substitute.For<ILogger<ExtendedLogService>>());
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extendedLog, operationTrace);

        return new MfaResetService(Substitute.For<ILogger<MfaResetService>>(), moduleConfig, delinea, httpClientFactory);
    }
}
