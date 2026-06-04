using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Modules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class TestAccountPoolServiceTests : IDisposable
{
    private readonly string _tempDir;

    public TestAccountPoolServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tap-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Theory]
    [InlineData(false, null, "Available")]
    [InlineData(true, null, "CheckedOut")]
    public void DetermineStatus_UsesEnabledState(bool enabled, int? minutesFromNow, string expected)
    {
        var now = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);
        DateTime? expires = minutesFromNow.HasValue ? now.AddMinutes(minutesFromNow.Value) : null;

        var result = TestAccountPoolService.DetermineStatus(enabled, expires, now);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void DetermineStatus_Expired_WhenEnabledAndExpiryPassedOrNow(int minutesFromNow)
    {
        var now = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);

        var result = TestAccountPoolService.DetermineStatus(true, now.AddMinutes(minutesFromNow), now);

        Assert.Equal("Expired", result);
    }

    [Fact]
    public void GeneratePassword_ReturnsRequestedLength()
    {
        var password = TestAccountPoolService.GeneratePassword(28);

        Assert.Equal(28, password.Length);
    }

    [Fact]
    public void GeneratePassword_ProducesDifferentValues()
    {
        var first = TestAccountPoolService.GeneratePassword(28);
        var second = TestAccountPoolService.GeneratePassword(28);

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("entra-exol-teams", false, true, true, true, false)]
    [InlineData("ad-exol-teams", true, false, true, true, false)]
    [InlineData("ad-onprem-mailbox-teams", true, false, false, true, true)]
    [InlineData("basic-ad", true, false, false, false, false)]
    public void BuildTemplateRequest_MapsPresetOptions(
        string templateId,
        bool expectedAd,
        bool expectedEntra,
        bool expectedExo,
        bool expectedTeams,
        bool expectedOnPremMailbox)
    {
        var request = TestAccountPoolService.BuildTemplateRequest(templateId, "tap", "Test Account", 1, "INC1");

        Assert.Equal(expectedAd, request.CreateOnPremAd);
        Assert.Equal(expectedEntra, request.CreateEntra);
        Assert.Equal(expectedExo, request.ExchangeOnline);
        Assert.Equal(expectedTeams, request.Teams);
        Assert.Equal(expectedOnPremMailbox, request.OnPremMailbox);
    }

    [Fact]
    public void PreviewCreate_BasicAd_RequiresOuUpnPoolAndCredentialConfig()
    {
        var service = CreateService();
        var request = TestAccountPoolService.BuildTemplateRequest("basic-ad", "tap", "Test Account", 1, "INC1");

        var preview = service.PreviewCreate(request);

        Assert.False(preview.Success);
        Assert.Contains("On-Prem Create OU", preview.Message);
    }

    [Fact]
    public void PreviewCreate_BasicAd_GeneratesDerivedRows()
    {
        var service = CreateService(new Dictionary<string, string>
        {
            ["OnPremCreateOU"] = "OU=Test,DC=example,DC=com",
            ["OnPremUPNSuffix"] = "example.com",
            ["OnPremPoolGroup"] = "TestAccounts",
            ["DelineaSecretId"] = "123"
        });
        var request = TestAccountPoolService.BuildTemplateRequest("basic-ad", "tap", "Test Account", 2, "INC1");

        var preview = service.PreviewCreate(request);

        Assert.True(preview.Success);
        Assert.Equal(2, preview.Rows.Count);
        Assert.Equal("tap01", preview.Rows[0].SamAccountName);
        Assert.Equal("tap01@example.com", preview.Rows[0].UserPrincipalName);
        Assert.Equal("Test Account 01", preview.Rows[0].DisplayName);
    }

    [Fact]
    public void PreviewCreate_RejectsUnsafePrefix()
    {
        var service = CreateService();
        var request = TestAccountPoolService.BuildTemplateRequest("basic-ad", "bad prefix!", "Test Account", 1, "INC1");

        var preview = service.PreviewCreate(request);

        Assert.False(preview.Success);
        Assert.Contains("Account name prefix", preview.Message);
    }

    private TestAccountPoolService CreateService(Dictionary<string, string>? moduleValues = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Email:SmtpHost"] = "localhost"
            })
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, Substitute.For<ILogger<ModuleConfigService>>());
        if (moduleValues != null)
            moduleConfig.SaveModuleConfig("TestAccountPool", moduleValues);

        var jsonl = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var trace = new OperationTraceService(config, jsonl);
        var extLog = new ExtendedLogService(config, env, Substitute.For<ILogger<ExtendedLogService>>());
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var delinea = new DelineaService(httpFactory, config, Substitute.For<ILogger<DelineaService>>(), extLog, trace);
        var credentials = new ModuleCredentialService(moduleConfig, delinea, Substitute.For<ILogger<ModuleCredentialService>>());
        var audit = new AuditService(jsonl, trace);
        var email = new EmailService(config, Substitute.For<ILogger<EmailService>>());

        return new TestAccountPoolService(
            moduleConfig,
            credentials,
            delinea,
            httpFactory,
            audit,
            email,
            trace,
            Substitute.For<ILogger<TestAccountPoolService>>());
    }
}
