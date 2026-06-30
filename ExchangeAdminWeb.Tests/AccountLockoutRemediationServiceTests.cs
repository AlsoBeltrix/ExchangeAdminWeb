using System.Security.Claims;
using ExchangeAdminWeb.Models.AccountLockoutRemediation;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Deterministic unit coverage for AccountLockoutRemediationService: the guard-clause ordering
/// and config-driven helpers that do not require live AD / WinRM / Delinea. The PowerShell
/// session-query/logoff, 4740 event read, AD computer enumeration, and the protected-principal
/// guard (which run only once credentials resolve against a real Delinea secret) are
/// manual-validation-only and not exercised here — see
/// docs/AccountLockoutRemediation-Incorporation-Plan.md.
/// </summary>
public sealed class AccountLockoutRemediationServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "alr-tests-" + Guid.NewGuid().ToString("N"));

    public AccountLockoutRemediationServiceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ---- Throttle clamp (GetDefaultThrottleLimit) -------------------------------------------

    [Theory]
    [InlineData("50", 50)]   // in range, passthrough
    [InlineData("500", 256)] // above max, clamped to 256
    [InlineData("0", 1)]     // below min, clamped to 1
    [InlineData("-5", 1)]    // negative, clamped to 1
    [InlineData("", 32)]     // unset/unparseable, module default 32
    public void GetDefaultThrottleLimit_ClampsToValidRange(string configured, int expected)
    {
        var config = new Dictionary<string, string> { ["DelineaSecretId"] = "1" };
        if (configured.Length > 0)
            config["DefaultThrottleLimit"] = configured;

        var service = CreateService(out _, moduleConfigValues: config);

        Assert.Equal(expected, service.GetDefaultThrottleLimit());
    }

    // ---- Sweep input validation (runs before any host call) ---------------------------------

    [Fact]
    public async Task Sweep_NoUsers_FailsBeforeAnyWork()
    {
        var service = CreateService(out var authorization);

        var result = await service.SweepScopedComputersAsync(
            new AccountScopedLogoffRequest([], "OU=Test,DC=x", [], Execute: false, "", 0),
            Context());

        Assert.False(result.Success);
        Assert.Contains("at least one target user", result.Message, StringComparison.OrdinalIgnoreCase);
        // Validation precedes authorization — the authz service must not even be consulted.
        await authorization.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!, default, default(string)!);
    }

    [Fact]
    public async Task Sweep_NoScope_FailsBeforeAnyWork()
    {
        var service = CreateService(out _);

        var result = await service.SweepScopedComputersAsync(
            new AccountScopedLogoffRequest(["jdoe"], "", [], Execute: false, "", 0),
            Context());

        Assert.False(result.Success);
        Assert.Contains("search base", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Authorization gate (Discover authorizes first) -------------------------------------

    [Fact]
    public async Task Discover_Unauthorized_IsDenied()
    {
        var service = CreateService(out var authorization);
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());

        var result = await service.DiscoverLockoutSourcesAsync(
            new AccountLockoutSourceRequest(["jdoe"], 24, ["dc1"], 0),
            Context());

        Assert.False(result.Success);
        Assert.Contains("not authorized", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Events);
    }

    // ---- Corrupt config gate ----------------------------------------------------------------

    [Fact]
    public async Task Discover_CorruptConfig_IsUnavailable()
    {
        var service = CreateService(out _, corruptStore: true);

        var result = await service.DiscoverLockoutSourcesAsync(
            new AccountLockoutSourceRequest(["jdoe"], 24, ["dc1"], 0),
            Context());

        Assert.False(result.Success);
        Assert.Contains("corrupt", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Credential gate (no DelineaSecretId configured) ------------------------------------

    [Fact]
    public async Task Discover_NoCredentials_Fails()
    {
        // Authorized, healthy store, but no DelineaSecretId configured -> credentials unavailable.
        var service = CreateService(out _, moduleConfigValues: new Dictionary<string, string>());

        var result = await service.DiscoverLockoutSourcesAsync(
            new AccountLockoutSourceRequest(["jdoe"], 24, ["dc1"], 0),
            Context());

        Assert.False(result.Success);
        Assert.Contains("credentials", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Admin notification on executed logoff (Constitution §Notifications) ----------------

    [Fact]
    public async Task LogoffSources_Executed_NotifiesAdmins()
    {
        // Execute:true reaches (and fails at) the credential gate, but the returned result
        // still carries Executed == true, so the mandatory admin notification must fire.
        var service = CreateService(out _, out var email, moduleConfigValues: new Dictionary<string, string>());

        await service.LogoffLockoutSourcesAsync(
            new AccountLockoutLogoffRequest(["jdoe"], 24, ["dc1"], Execute: true, "INC123", 0),
            Context());

        await email.Received(1).SendAdminNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), "AccountLockout_LogoffSources",
            Arg.Any<bool>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task LogoffSources_DryRun_DoesNotNotifyAdmins()
    {
        // Execute:false is a dry run — no state change, so no admin notification.
        var service = CreateService(out _, out var email, moduleConfigValues: new Dictionary<string, string>());

        await service.LogoffLockoutSourcesAsync(
            new AccountLockoutLogoffRequest(["jdoe"], 24, ["dc1"], Execute: false, "", 0),
            Context());

        await email.DidNotReceive().SendAdminNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task LogoffSources_NotificationThrows_DoesNotChangeResult()
    {
        // Fail-safe: a notification send failure must not change the operation result.
        var service = CreateService(out _, out var email, moduleConfigValues: new Dictionary<string, string>());
        email.SendAdminNotificationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>())
            .Returns(Task.FromException(new InvalidOperationException("smtp down")));

        var result = await service.LogoffLockoutSourcesAsync(
            new AccountLockoutLogoffRequest(["jdoe"], 24, ["dc1"], Execute: true, "INC123", 0),
            Context());

        // Same fail-at-credential-gate result as without the throwing notifier.
        Assert.False(result.Success);
        Assert.Contains("credentials", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Construction helper ----------------------------------------------------------------

    private static AccountLockoutOperatorContext Context()
        => new(new ClaimsPrincipal(new ClaimsIdentity()), "tester", "127.0.0.1");

    private AccountLockoutRemediationService CreateService(
        out IAuthorizationService authorization,
        IDictionary<string, string>? moduleConfigValues = null,
        bool corruptStore = false)
        => CreateService(out authorization, out _, moduleConfigValues, corruptStore);

    private AccountLockoutRemediationService CreateService(
        out IAuthorizationService authorization,
        out EmailService email,
        IDictionary<string, string>? moduleConfigValues = null,
        bool corruptStore = false)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:LogRoot"] = _tempDir,
                ["Delinea:SecretServerUrl"] = "https://fake.local"
            })
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var catalog = new ModuleCatalog();

        // Real ModuleConfigService over either a healthy SQLite store or a throwing one (corrupt).
        ModuleConfigRepository repo = corruptStore
            ? new ModuleConfigRepository(new ThrowingConfigStore())
            : TestConfigStore.CreateModuleConfig(_tempDir);
        var moduleConfig = new ModuleConfigService(catalog, env, repo, Substitute.For<ILogger<ModuleConfigService>>());

        if (!corruptStore && moduleConfigValues is { Count: > 0 })
            moduleConfig.SaveModuleConfig("AccountLockoutRemediation", new Dictionary<string, string>(moduleConfigValues));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());

        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var operationTrace = new OperationTraceService(config, jsonlLog);
        var audit = new AuditService(jsonlLog, operationTrace);
        var extendedLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), Substitute.For<ILogger<ExtendedLogService>>());
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extendedLog, operationTrace);
        var moduleCredentials = new ModuleCredentialService(moduleConfig, delinea, Substitute.For<ILogger<ModuleCredentialService>>());
        var protectedPrincipals = new ProtectedPrincipalService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea, Substitute.For<ILogger<ProtectedPrincipalService>>());

        authorization = Substitute.For<IAuthorizationService>();
        // Default: authorized. Individual tests override to deny.
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());

        // Substitutable EmailService (notification methods are virtual) so notification
        // firing can be observed. Constructed with a minimal config + substitute logger.
        email = Substitute.For<EmailService>(config, Substitute.For<ILogger<EmailService>>());

        return new AccountLockoutRemediationService(
            moduleCredentials,
            moduleConfig,
            protectedPrincipals,
            authorization,
            audit,
            email,
            operationTrace,
            Substitute.For<ILogger<AccountLockoutRemediationService>>());
    }

    private sealed class ThrowingConfigStore : IConfigStore
    {
        public long GetChangeToken() => throw new InvalidOperationException();
        public T Read<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> read) => throw new InvalidOperationException();
        public T Write<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, T> write) => throw new InvalidOperationException();
        public void Write(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction> write) => throw new InvalidOperationException();
    }
}
