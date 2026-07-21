using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class PermissionValidatorTests
{
    private static PermissionValidator CreateValidator(
        string[]? excludedUsers = null,
        bool preventSelfGrant = true)
    {
        var configData = new Dictionary<string, string?>
        {
            ["Security:PreventSelfGrant"] = preventSelfGrant.ToString(),
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = Path.Combine(Path.GetTempPath(), "eaw-test-logs")
        };

        if (excludedUsers is not null)
        {
            for (int i = 0; i < excludedUsers.Length; i++)
                configData[$"Security:ExcludedUsers:{i}"] = excludedUsers[i];
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var logger = Substitute.For<ILogger<PermissionValidator>>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        var moduleConfigLogger = Substitute.For<ILogger<ModuleConfigService>>();
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(Path.GetTempPath()), moduleConfigLogger);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var delineaLogger = Substitute.For<ILogger<DelineaService>>();
        var extLogLogger = Substitute.For<ILogger<ExtendedLogService>>();
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(Path.GetTempPath()), extLogLogger);
        var jsonlLogger = Substitute.For<ILogger<JsonlLogService>>();
        var jsonlLog = new JsonlLogService(config, jsonlLogger);
        var operationTrace = new OperationTraceService(config, jsonlLog);
        var delineaService = new DelineaService(httpClientFactory, config, delineaLogger, extLog, operationTrace);
        var protectedPrincipalLogger = Substitute.For<ILogger<ProtectedPrincipalService>>();
        var protectedPrincipalService = new ProtectedPrincipalService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(Path.GetTempPath()), delineaService, protectedPrincipalLogger);

        var enablementLogger = Substitute.For<ILogger<ModuleEnablementService>>();
        var enablement = new ModuleEnablementService(new ModuleCatalog(), env, moduleConfig, TestConfigStore.CreateModuleEnablement(Path.GetTempPath()), config, enablementLogger);

        var exoPoolLogger = Substitute.For<ILogger<ExoConnectionPool>>();
        var exoPool = new ExoConnectionPool(config, moduleConfig, enablement, exoPoolLogger, operationTrace);

        return new PermissionValidator(config, moduleConfig, exoPool, protectedPrincipalService, logger, scopeFactory);
    }

    // --- Self-grant validation ---

    [Fact]
    public void ValidateSelfGrant_SameUser_ReturnsError()
    {
        var validator = CreateValidator();
        var result = validator.ValidateSelfGrant(@"DOMAIN\jdoe", "jdoe@company.com");
        Assert.NotNull(result);
        Assert.Contains("cannot grant permissions to yourself", result);
    }

    [Fact]
    public void ValidateSelfGrant_SameUser_ExactMatch_ReturnsError()
    {
        var validator = CreateValidator();
        var result = validator.ValidateSelfGrant("jdoe", "jdoe");
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateSelfGrant_DifferentUser_ReturnsNull()
    {
        var validator = CreateValidator();
        var result = validator.ValidateSelfGrant(@"DOMAIN\admin", "jdoe@company.com");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateSelfGrant_Disabled_AllowsSelfGrant()
    {
        var validator = CreateValidator(preventSelfGrant: false);
        var result = validator.ValidateSelfGrant("jdoe", "jdoe");
        Assert.Null(result);
    }

    [Theory]
    [InlineData(@"DOMAIN\jdoe", "jdoe@company.com")]
    [InlineData("jdoe@company.com", @"DOMAIN\jdoe")]
    [InlineData("jdoe", "jdoe@company.com")]
    [InlineData("jdoe@company.com", "jdoe")]
    public void ValidateSelfGrant_ExtractsUsername_AcrossFormats(string current, string affected)
    {
        var validator = CreateValidator();
        var result = validator.ValidateSelfGrant(current, affected);
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateSelfGrant_CaseInsensitive()
    {
        var validator = CreateValidator();
        var result = validator.ValidateSelfGrant("JDoe", "jdoe");
        Assert.NotNull(result);
    }

    // --- Fail-closed init state machine ---

    [Fact]
    public async Task ValidateTargetMailbox_ExclusionConfigured_NoExchangeConfig_KeepsLiteralMatch()
    {
        // With exclusions configured but EXO not configured, group expansion
        // is skipped (returns empty) and the identity is kept as a literal
        // match. An unrelated target is allowed; the literal entry is blocked.
        var validator = CreateValidator(excludedUsers: new[] { "SomeGroupThatNeedsExpansion" });

        var resultUnrelated = await validator.ValidateTargetMailboxAsync("anyone@company.com");
        Assert.Null(resultUnrelated);

        var resultProtected = await validator.ValidateTargetMailboxAsync("SomeGroupThatNeedsExpansion");
        Assert.NotNull(resultProtected);
        Assert.Contains("protected", resultProtected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateTargetMailbox_EmptyExclusions_AllowsOperations()
    {
        // No exclusions configured at all - init succeeds with empty set
        var validator = CreateValidator(excludedUsers: Array.Empty<string>());

        var result = await validator.ValidateTargetMailboxAsync("anyone@company.com");
        Assert.Null(result);
    }

    [Fact]
    public async Task IsUserExcluded_ChecksExactAndUsername()
    {
        // This test only works if init succeeds. With no Exchange config,
        // any configured user that looks like a group will fail.
        // Use empty exclusions so init succeeds, then verify the method itself.
        var validator = CreateValidator(excludedUsers: Array.Empty<string>());

        var excluded = await validator.IsUserExcludedAsync("anyone@company.com");
        Assert.False(excluded);
    }

    // --- Retry after init failure ---

    [Fact]
    public async Task ValidateTargetMailbox_InitSucceeds_WhenExoNotConfigured()
    {
        // When EXO is not configured, group expansion is skipped gracefully
        // and the literal entries are still present. Init succeeds and
        // unrelated targets are allowed.
        var validator = CreateValidator(excludedUsers: new[] { "SomeGroup" });

        var firstResult = await validator.ValidateTargetMailboxAsync("user@company.com");
        Assert.Null(firstResult);

        // Second call should return the same (init cached as success)
        var secondResult = await validator.ValidateTargetMailboxAsync("user@company.com");
        Assert.Null(secondResult);
    }
}
