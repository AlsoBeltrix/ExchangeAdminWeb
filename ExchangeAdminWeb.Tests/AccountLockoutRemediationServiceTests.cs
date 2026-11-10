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
/// session-query/logoff, 4740 event read, and AD computer enumeration (which run only once
/// credentials resolve against a real Delinea secret) are manual-validation-only and not exercised
/// here - see docs/AccountLockoutRemediation-Incorporation-Plan.md. The protected-principal guard
/// IS covered, via the internal seam, in AccountLockoutRemediationServicerTests.
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
        // Validation precedes authorization - the authz service must not even be consulted.
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

    // ---- Admin notification on executed logoff (Constitution Section Notifications) ----------------

    [Fact]
    public async Task LogoffSources_Executed_NotifiesAdmins()
    {
        // Execute:true reaches (and fails at) the credential gate: no logoff was attempted, but an
        // execute request that got past its ticket still sends the mandatory admin notification.
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
        // Execute:false is a dry run - no state change, so no admin notification.
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

    // ---- Execute refusals that need no directory come first; Executed means a logoff was attempted ----

    private const string LogoffPolicy = "AccountLockoutRemediationLogoff";
    private const string TicketRequired = "Ticket Number is required for logoff execution.";
    private const string LogoffDenied = "You are not authorized to log users off.";

    public enum LogoffPath { Sources, Sweep }

    private static Task<AccountLogoffResult> Run(AccountLockoutRemediationService service, LogoffPath path, bool execute, string ticket)
        => path == LogoffPath.Sources
            ? service.LogoffLockoutSourcesAsync(new AccountLockoutLogoffRequest(["jdoe"], 24, ["dc1"], execute, ticket, 0), Context())
            : service.SweepScopedComputersAsync(new AccountScopedLogoffRequest(["jdoe"], "OU=Test,DC=x", [], execute, ticket, 0), Context());

    private static string MailAction(LogoffPath path)
        => path == LogoffPath.Sources ? "AccountLockout_LogoffSources" : "AccountLockout_SweepScoped";

    // The credential lookup is the first step of directory work on both paths; with no secret configured it
    // fails and leaves this trace step, so its absence shows a request stopped before it.
    private bool CredentialLookupWasReached()
        => ReadLogEvents(trace: true).Any(e => e.GetProperty("stage").GetString() == "CredentialLookup");

    private static Task AssertNoMail(EmailService email)
        => email.DidNotReceiveWithAnyArgs().SendAdminNotificationAsync(default!, default!, default!, default, default!, default!, default);

    [Theory]
    [InlineData(LogoffPath.Sources, "")]
    [InlineData(LogoffPath.Sources, ZeroWidthSpace)]
    [InlineData(LogoffPath.Sweep, "")]
    [InlineData(LogoffPath.Sweep, ZeroWidthSpace)]
    public async Task Execute_WithNoTicket_IsRefusedBeforeDirectoryWork_AndMailsNobody(LogoffPath path, string ticket)
    {
        var service = CreateService(out var authorization, out var email, moduleConfigValues: new Dictionary<string, string>());

        var result = await Run(service, path, execute: true, ticket);

        Assert.False(result.Success);
        Assert.Equal(TicketRequired, result.Message);
        Assert.False(result.Executed);
        Assert.False(CredentialLookupWasReached());
        await authorization.DidNotReceive().AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), LogoffPolicy);
        await AssertNoMail(email);
        Assert.DoesNotContain(ReadLogEvents(trace: false), e => e.GetProperty("eventType").GetString() == "audit");
    }

    [Theory]
    [InlineData(LogoffPath.Sources, "LogoffFromLockoutSources")]
    [InlineData(LogoffPath.Sweep, "SweepScopedComputers")]
    public async Task Execute_ByAnOperatorNotAllowedToLogUsersOff_IsRefusedBeforeDirectoryWork_AuditedAndMailed(LogoffPath path, string auditAction)
    {
        var service = CreateService(out var authorization, out var email, moduleConfigValues: new Dictionary<string, string>());
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), LogoffPolicy)
            .Returns(AuthorizationResult.Failed());

        var result = await Run(service, path, execute: true, "INC123");

        Assert.False(result.Success);
        Assert.Equal(LogoffDenied, result.Message);
        Assert.False(result.Executed);
        Assert.False(CredentialLookupWasReached());

        var audit = Assert.Single(ReadLogEvents(trace: false), e => e.GetProperty("eventType").GetString() == "audit");
        Assert.Equal(auditAction, audit.GetProperty("action").GetString());
        Assert.Equal("Failed", audit.GetProperty("result").GetString());
        Assert.Equal("INC123", audit.GetProperty("ticket").GetString());

        await email.Received(1).SendAdminNotificationAsync(
            "tester", "127.0.0.1", MailAction(path), false, "INC123",
            Arg.Is<IReadOnlyDictionary<string, string>>(d =>
                d["Target Users"] == "jdoe" && d["Machines"] == "0" && d["Sessions Logged Off"] == "0" && d["Failures"] == "0"),
            LogoffDenied);
    }

    [Theory]
    [InlineData(LogoffPath.Sources)]
    [InlineData(LogoffPath.Sweep)]
    public async Task Execute_ThatGetsPastItsTicketAndPermission_ThenFailsBeforeAnyLogoff_IsNotExecuted_AndStillMails(LogoffPath path)
    {
        var service = CreateService(out var authorization, out var email, moduleConfigValues: new Dictionary<string, string>());

        var result = await Run(service, path, execute: true, "INC123");

        Assert.Contains("credentials", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(CredentialLookupWasReached());
        Assert.False(result.Executed);
        await authorization.Received().AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), LogoffPolicy);
        await email.Received(1).SendAdminNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), MailAction(path), false, "INC123",
            Arg.Any<IReadOnlyDictionary<string, string>>(), result.Message);
    }

    [Theory]
    [InlineData(LogoffPath.Sources)]
    [InlineData(LogoffPath.Sweep)]
    public async Task DryRun_NeedsNoTicketAndNoLogoffPermission_AndGoesOnToDirectoryWork(LogoffPath path)
    {
        var service = CreateService(out var authorization, out var email, moduleConfigValues: new Dictionary<string, string>());
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), LogoffPolicy)
            .Returns(AuthorizationResult.Failed());

        var result = await Run(service, path, execute: false, "");

        Assert.Contains("credentials", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(CredentialLookupWasReached());
        Assert.False(result.Executed);
        await authorization.DidNotReceive().AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), LogoffPolicy);
        await AssertNoMail(email);
    }

    [Theory]
    [InlineData(LogoffPath.Sources)]
    [InlineData(LogoffPath.Sweep)]
    public async Task Execute_WithNoModuleAccess_IsToldThatFirst_EvenWithNoTicket_AndStillMails(LogoffPath path)
    {
        var service = CreateService(out var authorization, out var email, moduleConfigValues: new Dictionary<string, string>());
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());

        var result = await Run(service, path, execute: true, "");

        Assert.Equal("You are not authorized to access account lockout remediation.", result.Message);
        Assert.False(result.Executed);
        Assert.False(CredentialLookupWasReached());
        await email.Received(1).SendAdminNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), MailAction(path), false, "",
            Arg.Any<IReadOnlyDictionary<string, string>>(), result.Message);
    }

    [Theory]
    [InlineData(LogoffPath.Sources)]
    [InlineData(LogoffPath.Sweep)]
    public async Task Execute_WithCorruptConfiguration_IsToldThatBeforeTheTicket_AndStillMails(LogoffPath path)
    {
        var service = CreateService(out _, out var email, corruptStore: true);

        var result = await Run(service, path, execute: true, "");

        Assert.Contains("corrupt", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Executed);
        await email.Received(1).SendAdminNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), MailAction(path), false, "",
            Arg.Any<IReadOnlyDictionary<string, string>>(), result.Message);
    }

    // ---- Ticket: sanitized, not refused, for invisible characters (rev-j1, decision 2026-11-09) ----------

    private const string ZeroWidthSpace = "\u200B";
    private const string TypedTicket = "INC\u200B0042";

    [Fact]
    public async Task RunLogoff_Execute_AnInvisibleOnlyTicket_IsRefusedWithTheServicesOwnWords()
    {
        // Through the seam, which keeps its own gate for a caller that skipped the public boundary.
        var service = CreateService(out var authorization);

        var result = await service.RunLogoffAsync(
            "LogoffFromLockoutSources", new Dictionary<string, string[]> { ["PC1"] = ["jdoe"] },
            execute: true, ZeroWidthSpace, 0, Context(), []);

        Assert.False(result.Success);
        Assert.Equal("Ticket Number is required for logoff execution.", result.Message);
        await authorization.DidNotReceive().AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), "AccountLockoutRemediationLogoff");
    }

    [Fact]
    public async Task RunLogoff_Execute_ATicketCarryingAZeroWidthSpace_IsTakenAndRecordedSanitized()
    {
        // Past the gate to the logoff authorization, denied, which writes an audit row carrying the ticket.
        var service = CreateService(out var authorization);
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), "AccountLockoutRemediationLogoff")
            .Returns(AuthorizationResult.Failed());

        var result = await service.RunLogoffAsync(
            "LogoffFromLockoutSources", new Dictionary<string, string[]> { ["PC1"] = ["jdoe"] },
            execute: true, TypedTicket, 0, Context(), []);

        Assert.Equal("You are not authorized to log users off.", result.Message);
        var audit = Assert.Single(ReadLogEvents(trace: false), e => e.GetProperty("eventType").GetString() == "audit");
        Assert.Equal("INC0042", audit.GetProperty("ticket").GetString());
        AssertTraceTicketsAre("INC0042");
    }

    [Fact]
    public async Task Sweep_Execute_ATicketCarryingAZeroWidthSpace_IsTracedSanitized()
    {
        var service = CreateService(out _);

        await service.SweepScopedComputersAsync(
            new AccountScopedLogoffRequest(["jdoe"], "OU=Test,DC=x", [], Execute: true, TypedTicket, 0),
            Context());

        AssertTraceTicketsAre("INC0042");
    }

    [Fact]
    public async Task Execute_TheAdminNotificationCarriesTheTicketSanitized_OnBothPaths()
    {
        // Both are execute requests that reach (and fail at) the credential lookup, so each notifies.
        var service = CreateService(out _, out var email, moduleConfigValues: new Dictionary<string, string>());

        await service.LogoffLockoutSourcesAsync(
            new AccountLockoutLogoffRequest(["jdoe"], 24, ["dc1"], Execute: true, TypedTicket, 0),
            Context());
        await service.SweepScopedComputersAsync(
            new AccountScopedLogoffRequest(["jdoe"], "OU=Test,DC=x", [], Execute: true, TypedTicket, 0),
            Context());

        await email.Received(1).SendAdminNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), "AccountLockout_LogoffSources",
            Arg.Any<bool>(), "INC0042",
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>());
        await email.Received(1).SendAdminNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), "AccountLockout_SweepScoped",
            Arg.Any<bool>(), "INC0042",
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>());
    }

    private void AssertTraceTicketsAre(string expected)
    {
        var withTicket = ReadLogEvents(trace: true)
            .Where(e => e.TryGetProperty("ticket", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String)
            .ToList();
        Assert.NotEmpty(withTicket);
        Assert.All(withTicket, e => Assert.Equal(expected, e.GetProperty("ticket").GetString()));
    }

    private List<System.Text.Json.JsonElement> ReadLogEvents(bool trace)
    {
        var dir = Path.Combine(_tempDir, "ExchangeAdminWeb");
        return Directory.GetFiles(dir, "exchangeadmin_*.jsonl")
            .Where(f => Path.GetFileNameWithoutExtension(f).EndsWith("_trace", StringComparison.OrdinalIgnoreCase) == trace)
            .SelectMany(File.ReadAllLines)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
            .ToList();
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

        // Real servicer service over a store with no ProtectedServicer row, so it denies. Every
        // protection assertion in this class must keep passing unchanged: a servicer path that
        // turned one of these refusals into an allow would be the bug, not the fix.
        var sectionAccess = new SectionAccessService(
            config, Substitute.For<ILogger<SectionAccessService>>(), env, catalog,
            new SectionAccessRepository(TestConfigStore.Create(_tempDir)));
        var servicers = new ProtectedPrincipalServicerService(
            sectionAccess, Substitute.For<ILogger<ProtectedPrincipalServicerService>>());

        return new AccountLockoutRemediationService(
            moduleCredentials,
            moduleConfig,
            protectedPrincipals,
            servicers,
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
