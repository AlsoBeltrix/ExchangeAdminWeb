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
        bool preventSelfGrant = true,
        string[]? appsettingsExcludedUsers = null)
    {
        // Unique DB dir per validator so seeded module config does not leak across tests.
        var testDir = Path.Combine(Path.GetTempPath(), "eaw-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        var configData = new Dictionary<string, string?>
        {
            ["Security:PreventSelfGrant"] = preventSelfGrant.ToString(),
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = Path.Combine(Path.GetTempPath(), "eaw-test-logs")
        };

        // Retired appsettings source: seeded only by the guard test that proves a value
        // present ONLY under Security:ExcludedUsers is no longer read (retired 2026-07-28).
        if (appsettingsExcludedUsers is not null)
        {
            for (int i = 0; i < appsettingsExcludedUsers.Length; i++)
                configData[$"Security:ExcludedUsers:{i}"] = appsettingsExcludedUsers[i];
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
        env.ContentRootPath.Returns(testDir);
        var moduleConfigLogger = Substitute.For<ILogger<ModuleConfigService>>();
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(testDir), moduleConfigLogger);

        // Exclusions are read from the MailboxPermissions/ExcludedUsers module config
        // (the only source since the appsettings fallback was retired 2026-07-28).
        if (excludedUsers is not null)
        {
            moduleConfig.SaveModuleConfig("MailboxPermissions", new Dictionary<string, string>
            {
                ["ExcludedUsers"] = string.Join(",", excludedUsers)
            });
        }

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
    public async Task ValidateTargetMailbox_AppsettingsOnlyExclusion_IsNotProtected()
    {
        // Guards the retirement of the Security:ExcludedUsers appsettings fallback
        // (2026-07-28): a value present ONLY under appsettings, with the module config
        // empty, must NOT be treated as excluded. Restoring the fallback read fails this.
        var validator = CreateValidator(appsettingsExcludedUsers: new[] { "AppsettingsOnlyGroup" });

        var result = await validator.ValidateTargetMailboxAsync("AppsettingsOnlyGroup");
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

    // --- Resolution outcomes: the operator-facing message must name the real cause ---

    /// <summary>
    /// Scripts the resolution seam so the four outcomes can be driven without a directory. The
    /// gate calls ResolveWithExchangeFallbackAsync, so that is what is overridden.
    /// </summary>
    private sealed class ScriptedPpService : ProtectedPrincipalService
    {
        public ProtectedPrincipalService.ResolutionStatus Status = ResolutionStatus.Resolved;

        /// <summary>
        /// Forces the central-config branch on. Without this the fixture's repository reports
        /// "not configured", ValidateTargetMailboxAsync skips the entire protection block, and
        /// tests of that block pass no matter what it contains - which is exactly what a
        /// non-vacuity probe caught here.
        /// </summary>
        public override bool HasCentralConfig => true;

        /// <summary>Set to script the fail-closed config-load error branch.</summary>
        public string? LoadError;

        /// <summary>Set to script the outcome of the protection check itself.</summary>
        public ProtectedPrincipalResult? CheckResult;

        public ScriptedPpService(IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
            ExchangeAdminWeb.Services.Storage.ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, Substitute.For<ILogger<ProtectedPrincipalService>>())
        { }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity)
        {
            ResolvedDirectoryPrincipal? p = Status == ResolutionStatus.Resolved
                ? new ResolvedDirectoryPrincipal("Test", identity, identity, "sam", identity, "CN=x,DC=y", "guid", null)
                : null;
            return Task.FromResult((p, Status));
        }

        public override (ProtectedPrincipalConfig? config, string[] legacyExclusions, string? error) LoadEffectiveConfig()
            => LoadError is not null
                ? (null, Array.Empty<string>(), LoadError)
                : base.LoadEffectiveConfig();

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
            => CheckResult is not null
                ? Task.FromResult(CheckResult)
                : base.CheckAsync(target);
    }

    /// <summary>
    /// Builds a validator whose protected-principal config has a Group rule, which is what makes
    /// ValidateTargetMailboxAsync require full resolution and reach the scripted seam.
    /// </summary>
    private static (PermissionValidator validator, ScriptedPpService pp) CreateValidatorWithScriptedResolution()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "eaw-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(testDir, "config"));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = testDir
        }).Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(testDir);

        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, env, TestConfigStore.CreateModuleConfig(testDir), Substitute.For<ILogger<ModuleConfigService>>());

        File.WriteAllText(Path.Combine(testDir, "config", "protected-principals.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                ProtectedPrincipals = new
                {
                    Users = Array.Empty<string>(),
                    Groups = new[] { "CN=VIPs,OU=Groups,DC=contoso,DC=com" },
                    OrganizationalUnits = Array.Empty<string>(),
                    SamAccountNamePatterns = Array.Empty<string>()
                }
            }));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(testDir), Substitute.For<ILogger<ExtendedLogService>>());
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var trace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extLog, trace);

        var pp = new ScriptedPpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(testDir), delinea);

        var enablement = new ModuleEnablementService(catalog, env, moduleConfig, TestConfigStore.CreateModuleEnablement(testDir), config, Substitute.For<ILogger<ModuleEnablementService>>());
        var exoPool = new ExoConnectionPool(config, moduleConfig, enablement, Substitute.For<ILogger<ExoConnectionPool>>(), trace);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(Substitute.For<IServiceProvider>());

        var validator = new PermissionValidator(config, moduleConfig, exoPool, pp,
            Substitute.For<ILogger<PermissionValidator>>(), scopeFactory);

        return (validator, pp);
    }

    [Fact]
    public async Task ValidateTargetMailbox_NotFoundInEitherDirectory_SaysCheckTheAddress()
    {
        // The reported defect: a target neither directory knows used to produce
        // "identity resolution is unavailable. Contact your administrator." That reads like an
        // outage and sent L1/L2 support chasing the wrong problem when the cause was a bad
        // address. Both directories answered here, so the message must say so.
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.NotFound;

        var result = await validator.ValidateTargetMailboxAsync("nosuchbox@company.com");

        Assert.NotNull(result);
        Assert.Contains("was not found in Active Directory or Exchange Online", result);
        Assert.Contains("nosuchbox@company.com", result);
        Assert.DoesNotContain("Contact your administrator", result);
    }

    [Fact]
    public async Task ValidateTargetMailbox_ResolutionUnavailable_StillDenies()
    {
        // A directory that could not be reached must keep denying, with the administrator
        // message. This is the fail-closed half of the change (Known Failure Class #3).
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.Unavailable;

        var result = await validator.ValidateTargetMailboxAsync("someone@company.com");

        Assert.NotNull(result);
        Assert.Contains("unavailable", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Contact your administrator", result);
    }

    [Fact]
    public async Task ValidateTargetMailbox_Ambiguous_SaysAmbiguous_NotUnavailable()
    {
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.Ambiguous;

        var result = await validator.ValidateTargetMailboxAsync("dupe@company.com");

        Assert.NotNull(result);
        Assert.Contains("ambiguous", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateTargetMailbox_Resolved_ProceedsToTheProtectionCheck()
    {
        // A resolved target must not be denied by the resolution branch at all. The configured
        // Group rule cannot be evaluated without a directory-read credential, so the protection
        // check fails closed - which proves the resolution branch was passed rather than skipped.
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.Resolved;

        var result = await validator.ValidateTargetMailboxAsync("user@company.com");

        Assert.NotNull(result);
        Assert.DoesNotContain("was not found", result);
        Assert.DoesNotContain("ambiguous", result, StringComparison.OrdinalIgnoreCase);
    }

    // --- Fail-closed denials after resolution succeeds ---
    //
    // These are the branches that decide whether a protected mailbox can be modified. They were
    // unreachable in a test until HasCentralConfig / LoadEffectiveConfig / CheckAsync became
    // virtual seams, so the whole deny path sat uncovered on the code that enforces it.

    [Fact]
    public async Task ValidateTargetMailbox_ConfigLoadFails_DeniesRatherThanAllowing()
    {
        // Known Failure Class #3, fail-closed authorization. A protection config that cannot be
        // READ says nothing about whether the target is protected - so the only safe answer is to
        // refuse. Allowing here would silently un-protect every principal the moment the config
        // store hiccups, which is the worst possible failure for this gate.
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.LoadError = "protected-principals.json is corrupt";

        var result = await validator.ValidateTargetMailboxAsync("vip@company.com");

        Assert.NotNull(result);
        Assert.Contains("Access denied", result);
        Assert.Contains("corrupt", result);
    }

    [Fact]
    public async Task ValidateTargetMailbox_ProtectionCheckFails_DeniesRatherThanAllowing()
    {
        // The other half of the same rule: the check RAN but could not reach the directory to
        // evaluate a Group or OU rule. An unevaluated rule is not a passed rule.
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.CheckResult = ProtectedPrincipalResult.Failed("directory unreachable");

        var result = await validator.ValidateTargetMailboxAsync("vip@company.com");

        Assert.NotNull(result);
        Assert.Contains("Access denied", result);
        Assert.Contains("directory unreachable", result);
    }

    [Fact]
    public async Task ValidateTargetMailbox_ProtectedPrincipal_IsRefused()
    {
        // The gate doing its actual job.
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.CheckResult = ProtectedPrincipalResult.Protected("Target is a protected principal.", "Group:VIPs");

        var result = await validator.ValidateTargetMailboxAsync("ceo@company.com");

        Assert.NotNull(result);
        Assert.Contains("Access denied", result);
        Assert.Contains("ceo@company.com", result);
        Assert.Contains("protected", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateTargetMailbox_NotProtected_IsAllowed()
    {
        // The permissive path, asserted so the deny tests above are not passing merely because
        // this gate denies everything.
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.CheckResult = ProtectedPrincipalResult.NotProtected();

        var result = await validator.ValidateTargetMailboxAsync("ordinary@company.com");

        Assert.Null(result);
    }
}
