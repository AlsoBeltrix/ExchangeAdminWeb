using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.SelfServiceGroups;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the fail-closed contract of the delegated-Entra settings provider (plan
/// docs/SelfServiceGroupManagement-Plan.md section 6.8): the delegated confidential-client
/// credential is all-or-nothing. An unconfigured secret id, an unreadable secret, or any missing
/// field must yield null - never a partial settings object a caller could act on. These are the
/// security-critical paths and need no live Entra/Delinea backend.
/// </summary>
public class DelegatedEntraSettingsProviderTests
{
    private static ModuleConfigService ConfigWith(string dir, string? secretIdValue)
    {
        var catalog = new ModuleCatalog();
        var repo = TestConfigStore.CreateModuleConfig(dir);
        var svc = new ModuleConfigService(catalog, FakeEnv(dir), repo, Substitute.For<ILogger<ModuleConfigService>>());
        if (secretIdValue != null)
        {
            svc.SaveModuleConfig(DelegatedEntraSettings.ModuleId, new Dictionary<string, string>
            {
                [DelegatedEntraSettings.SecretConfigKey] = secretIdValue
            });
        }
        return svc;
    }

    private static Microsoft.AspNetCore.Hosting.IWebHostEnvironment FakeEnv(string dir)
    {
        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.ContentRootPath.Returns(dir);
        return env;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ssg_delegated_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Unconfigured_secret_id_fails_closed()
    {
        var dir = NewTempDir();
        try
        {
            var moduleConfig = ConfigWith(dir, null);
            var delinea = Substitute.For<ISecretFieldsReader>();
            var provider = new DelegatedEntraSettingsProvider(moduleConfig, delinea, Substitute.For<ILogger<DelegatedEntraSettingsProvider>>());

            Assert.False(provider.IsConfigured);
            Assert.Null(await provider.GetSettingsAsync());
            await delinea.DidNotReceiveWithAnyArgs().GetSecretFieldsAsync(default);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Unreadable_secret_fails_closed()
    {
        var dir = NewTempDir();
        try
        {
            var moduleConfig = ConfigWith(dir, "42");
            var delinea = Substitute.For<ISecretFieldsReader>();
            delinea.GetSecretFieldsAsync(42).Returns((Dictionary<string, string>?)null);
            var provider = new DelegatedEntraSettingsProvider(moduleConfig, delinea, Substitute.For<ILogger<DelegatedEntraSettingsProvider>>());

            Assert.True(provider.IsConfigured);
            Assert.Null(await provider.GetSettingsAsync());
        }
        finally { TryDelete(dir); }
    }

    [Theory]
    [InlineData("", "cid", "secret")]
    [InlineData("tid", "", "secret")]
    [InlineData("tid", "cid", "")]
    public async Task Incomplete_fields_fail_closed(string tenant, string client, string secret)
    {
        var dir = NewTempDir();
        try
        {
            var moduleConfig = ConfigWith(dir, "42");
            var delinea = Substitute.For<ISecretFieldsReader>();
            delinea.GetSecretFieldsAsync(42).Returns(new Dictionary<string, string>
            {
                [DelegatedEntraSettings.TenantIdField] = tenant,
                [DelegatedEntraSettings.ClientIdField] = client,
                [DelegatedEntraSettings.ClientSecretField] = secret
            });
            var provider = new DelegatedEntraSettingsProvider(moduleConfig, delinea, Substitute.For<ILogger<DelegatedEntraSettingsProvider>>());

            Assert.Null(await provider.GetSettingsAsync());
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Complete_secret_yields_settings()
    {
        var dir = NewTempDir();
        try
        {
            var moduleConfig = ConfigWith(dir, "42");
            var delinea = Substitute.For<ISecretFieldsReader>();
            delinea.GetSecretFieldsAsync(42).Returns(new Dictionary<string, string>
            {
                [DelegatedEntraSettings.TenantIdField] = "contoso-tenant",
                [DelegatedEntraSettings.ClientIdField] = "app-123",
                [DelegatedEntraSettings.ClientSecretField] = "s3cr3t"
            });
            var provider = new DelegatedEntraSettingsProvider(moduleConfig, delinea, Substitute.For<ILogger<DelegatedEntraSettingsProvider>>());

            var settings = await provider.GetSettingsAsync();
            Assert.NotNull(settings);
            Assert.Equal("contoso-tenant", settings!.TenantId);
            Assert.Equal("app-123", settings.ClientId);
            Assert.Equal("s3cr3t", settings.ClientSecret);
            Assert.Equal("https://login.microsoftonline.com/contoso-tenant/v2.0", settings.Authority);
        }
        finally { TryDelete(dir); }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }
}
