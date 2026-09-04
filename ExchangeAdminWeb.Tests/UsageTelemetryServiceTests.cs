using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Tests for the usage-telemetry service, the per-circuit session id and the AuditService hook
/// (docs/UsageTelemetry-Plan.md S2: AC3, AC4, AC5, AC10).
/// </summary>
/// <remarks>
/// Module config is a real SQLite store over a temp directory (the TicketValidationServiceTests
/// fixture shape) because the kill-switch behaviors under test - default-on, off, and unreadable
/// - are exactly the ones a mocked config would assume away. The write path is captured through
/// the internal Enqueue seam so the assertions do not race a background task; the one test that
/// exercises the real Enqueue waits for the warning it must log.
/// </remarks>
public sealed class UsageTelemetryServiceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"usage-tel-{Guid.NewGuid():N}");

    public UsageTelemetryServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Temp-dir cleanup is best-effort.
        }
    }

    // --- fixtures ----------------------------------------------------------

    private ModuleConfigService Config(string? switchValue = null, bool corrupt = false)
    {
        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.ContentRootPath.Returns(_dir);

        var repository = corrupt
            ? new ModuleConfigRepository(new ThrowingConfigStore())
            : TestConfigStore.CreateModuleConfig(_dir);

        var moduleConfig = new ModuleConfigService(
            new ModuleCatalog(),
            env,
            repository,
            NullLogger<ModuleConfigService>.Instance);

        if (!corrupt && switchValue != null)
        {
            moduleConfig.SaveModuleConfig(
                UsageTelemetryService.ConfigModuleId,
                new Dictionary<string, string>
                {
                    [UsageTelemetryService.ConfigKey] = switchValue,
                });
        }

        return moduleConfig;
    }

    private UsageEventRepository Repo() =>
        new(new SqliteConnectionFactory(Path.Combine(_dir, "usage.db")));

    private CapturingTelemetry Service(string? switchValue = null, bool corruptConfig = false) =>
        new(Repo(),
            Config(switchValue, corruptConfig),
            new ModuleCatalog(),
            NullLogger<UsageTelemetryService>.Instance);

    // --- AC3: what a recorded row carries ----------------------------------

    [Fact]
    public void RecordOpen_QueuesOneOpenRow_WithSessionAndRoute()
    {
        var service = Service();

        service.RecordOpen("sess-1", "MailboxPermissions", "mailbox-permissions");

        var e = Assert.Single(service.Events);
        Assert.Equal("open", e.Kind);
        Assert.Equal("sess-1", e.Session);
        Assert.Equal("MailboxPermissions", e.Module);
        Assert.Equal("mailbox-permissions", e.Detail);
        Assert.Null(e.Category);
        Assert.Null(e.Success);
    }

    [Fact]
    public void RecordSessionStartAndTheme_CarryTheirOwnKinds()
    {
        var service = Service();

        service.RecordSessionStart("sess-2");
        service.RecordTheme("sess-2", "midnight");

        Assert.Equal(2, service.Events.Count);
        Assert.Equal("session", service.Events[0].Kind);
        Assert.Null(service.Events[0].Detail);
        Assert.Equal("theme", service.Events[1].Kind);
        Assert.Equal("midnight", service.Events[1].Detail);
    }

    [Fact]
    public void RecordAction_KeepsTheRawCategory()
    {
        var service = Service();

        service.RecordAction("NotAModule", "SomeAction", success: false);

        var e = Assert.Single(service.Events);
        Assert.Equal("action", e.Kind);
        Assert.Equal("NotAModule", e.Category);
        Assert.Null(e.Module);
        Assert.Equal("SomeAction", e.Detail);
        Assert.False(e.Success);
    }

    [Theory]
    [InlineData("GroupManagement", "GroupManagement")]
    [InlineData("MailboxPermission", "MailboxPermissions")]
    [InlineData("CalendarPermission", "CalendarPermissions")]
    [InlineData("MigrationBatch", "Migration")]
    [InlineData("MigrationCheck", "Migration")]
    [InlineData("MigrationAction", "Migration")]
    [InlineData("Bogus", null)]
    [InlineData("", null)]
    public void ModuleOf_MapsCatalogIds_LegacyCategories_AndUnknown(string category, string? expected)
    {
        Assert.Equal(expected, UsageTelemetryService.ModuleOf(category, new ModuleCatalog()));
    }

    // --- AC4: the ambient per-circuit session ------------------------------

    [Fact]
    public void RecordAction_CarriesTheAmbientSession()
    {
        var service = Service();
        var prior = UsageSession.Current.Value;
        try
        {
            UsageSession.Current.Value = "ambient-7";
            service.RecordAction("GroupManagement", "AddMember", success: true);
        }
        finally
        {
            UsageSession.Current.Value = prior;
        }

        var e = Assert.Single(service.Events);
        Assert.Equal("ambient-7", e.Session);
        Assert.Equal("GroupManagement", e.Module);
    }

    [Fact]
    public void RecordAction_OutsideACircuit_HasNoSession()
    {
        var service = Service();
        var prior = UsageSession.Current.Value;
        try
        {
            UsageSession.Current.Value = null;
            service.RecordAction("GroupManagement", "AddMember", success: true);
        }
        finally
        {
            UsageSession.Current.Value = prior;
        }

        Assert.Null(Assert.Single(service.Events).Session);
    }

    [Fact]
    public async Task CircuitHandler_SetsCurrentForTheActivity_AndDoesNotLeakIt()
    {
        var session = new UsageSession();
        var handler = new UsageSessionCircuitHandler(session);

        string? seen = null;
        var wrapped = handler.CreateInboundActivityHandler(_ =>
        {
            seen = UsageSession.Current.Value;
            return Task.CompletedTask;
        });

        var prior = UsageSession.Current.Value;
        try
        {
            UsageSession.Current.Value = "outer";
            await wrapped(null!);

            // The activity, and everything it calls, runs with this circuit's id...
            Assert.Equal(session.Id, seen);
            // ...and that id never escapes into the flow that invoked the activity.
            Assert.Equal("outer", UsageSession.Current.Value);

            // Nor does a failing activity leave the ambient value behind.
            var throwing = handler.CreateInboundActivityHandler(
                _ => throw new InvalidOperationException("boom"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => throwing(null!));
            Assert.Equal("outer", UsageSession.Current.Value);
        }
        finally
        {
            UsageSession.Current.Value = prior;
        }
    }

    // --- AC10: the kill switch ---------------------------------------------

    [Fact]
    public void DefaultOn_WhenNeverConfigured()
    {
        var service = Service();

        Assert.True(service.Enabled());
        service.RecordOpen("s", "GroupManagement", "group-management");
        Assert.Single(service.Events);
    }

    [Fact]
    public void Disabled_RecordsNothing()
    {
        var service = Service("false");

        Assert.False(service.Enabled());
        service.RecordSessionStart("s");
        service.RecordOpen("s", "GroupManagement", "group-management");
        service.RecordTheme("s", "midnight");
        service.RecordAction("GroupManagement", "AddMember", success: true);

        Assert.Empty(service.Events);
    }

    [Fact]
    public void UnreadableSwitch_IsDisabled()
    {
        var service = Service(corruptConfig: true);

        Assert.False(service.Enabled());
        service.RecordOpen("s", "GroupManagement", "group-management");
        Assert.Empty(service.Events);
    }

    // --- AC3: a telemetry fault never reaches the caller -------------------

    [Fact]
    public async Task Enqueue_SwallowsRepositoryExceptions()
    {
        // mustExist over a file that is not there: every Insert throws on open.
        var broken = new UsageEventRepository(
            new SqliteConnectionFactory(Path.Combine(_dir, "absent", "usage.db"), mustExist: true));
        var logger = new CapturingLogger<UsageTelemetryService>();
        var service = new UsageTelemetryService(broken, Config(), new ModuleCatalog(), logger);

        var ex = Record.Exception(() => service.RecordOpen("s", "GroupManagement", "group-management"));
        Assert.Null(ex);

        // The insert runs off the caller's path, so wait for the warning it must log.
        for (var i = 0; i < 100 && logger.WarningCount == 0; i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(1, logger.WarningCount);
    }

    // --- AC5: every public audit method feeds telemetry --------------------

    [Fact]
    public void AuditService_EveryPublicAuditMethod_CallsRecordAction()
    {
        var (audit, usage) = CreateAudit();

        audit.LogMailboxPermission("u", "1.1.1.1", "Add", "t", "a", "FullAccess", true, "T1");
        audit.LogCalendarPermission("u", "1.1.1.1", "Add", "t", "a", "Editor", true, "T1");
        audit.LogMigrationCheck("u", "1.1.1.1", "a@b.c", "Ready", "T1");
        audit.LogMigrationBatch("u", "1.1.1.1", "batch", "Onboarding", 1, false, false, "T1", true);
        audit.LogMigrationAction("u", "1.1.1.1", "Start", "batch", true);
        audit.LogModuleAction("u", "1.1.1.1", "Do", "GroupManagement", "t", true);
        audit.LogLookupAction("u", "1.1.1.1", "Lookup", "t", true);
        audit.LogMfaResetAction("u", "1.1.1.1", "Reset", "t", true);
        audit.LogConferenceRoomAction("u", "1.1.1.1", "Update", "t", true);
        audit.LogADAttributeEdit(
            "u", "1.1.1.1", "t", [new AttributeChange("title", "Title", "a", "b")], true, "T1");
        audit.LogSettingsChange("u", "1.1.1.1", "Admins", [], ["g"]);

        Assert.Equal(11, usage.Events.Count);
        Assert.All(usage.Events, e => Assert.Equal("action", e.Kind));
        Assert.All(usage.Events, e => Assert.False(string.IsNullOrWhiteSpace(e.Category)));
        Assert.All(usage.Events, e => Assert.NotNull(e.Success));

        // A failed operation must be recorded as a failure, not merely counted.
        audit.LogModuleAction("u", "1.1.1.1", "Do", "GroupManagement", "t", false);
        Assert.False(usage.Events[^1].Success);
    }

    [Fact]
    public void AuditService_NullSink_IsNoOp()
    {
        var config = AuditConfig();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var audit = new AuditService(log, new OperationTraceService(config, log));

        var ex = Record.Exception(() =>
            audit.LogModuleAction("u", "1.1.1.1", "Do", "GroupManagement", "t", true));

        Assert.Null(ex);
    }

    /// <summary>
    /// AC5, the ordering half: the telemetry hook must be the LAST statement of
    /// WriteAuditEvent, after the audit row is written. A source guard because the ordering is
    /// the point and no runtime seam can observe "came after" - a mock records both calls
    /// whichever way round they are.
    /// </summary>
    [Fact]
    public void AuditService_HookIsLastInWriteAuditEvent()
    {
        var source = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Services", "AuditService.cs"));
        var body = MethodBody(source, "private void WriteAuditEvent(");

        var write = body.IndexOf("_log.Write(evt);", StringComparison.Ordinal);
        var hook = body.IndexOf("_usage?.RecordAction(", StringComparison.Ordinal);

        Assert.True(write >= 0, "WriteAuditEvent no longer writes the audit row.");
        Assert.True(hook > write, "The telemetry hook must run after the audit row is written.");

        var afterCall = body.IndexOf(';', hook);
        Assert.True(afterCall > 0, "The telemetry hook call is not terminated.");
        Assert.True(
            string.IsNullOrWhiteSpace(body[(afterCall + 1)..].Replace("}", string.Empty)),
            "The telemetry hook is not the last statement of WriteAuditEvent.");
    }

    /// <summary>Brace-matched body of the method whose declaration starts with the signature.</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {signature} in AuditService.cs.");

        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[open..i];
            }
        }

        throw new InvalidOperationException($"Unbalanced braces after {signature}.");
    }


    /// <summary>
    /// AC10, the switch half: the field the service reads must exist on the module the service
    /// names, as a Boolean defaulting to on. Both sides come from the same constants, so a
    /// rename cannot half-land and silently leave the switch unreachable.
    /// </summary>
    [Fact]
    public void KillSwitch_IsABooleanConfigFieldOnTheEventLogModule()
    {
        var module = new ModuleCatalog().GetById(UsageTelemetryService.ConfigModuleId);
        Assert.NotNull(module);

        var field = Assert.Single(
            module.ConfigFields,
            f => f.Key == UsageTelemetryService.ConfigKey);

        Assert.Equal(ConfigFieldType.Boolean, field.FieldType);
        Assert.Equal("true", field.DefaultValue);
        Assert.False(field.Required);
        Assert.False(field.IsSecret);
    }
    private IConfiguration AuditConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:LogRoot"] = Path.Combine(_dir, "logs"),
                ["Audit:RotationPeriod"] = "daily",
            })
            .Build();

    private (AuditService Audit, CapturingTelemetry Usage) CreateAudit()
    {
        var config = AuditConfig();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, log);
        var usage = Service();
        return (new AuditService(log, trace, usage), usage);
    }

    // --- helpers -----------------------------------------------------------

    /// <summary>Captures events at the write seam so assertions do not race the insert task.</summary>
    private sealed class CapturingTelemetry : UsageTelemetryService
    {
        public CapturingTelemetry(
            UsageEventRepository repository,
            ModuleConfigService moduleConfig,
            ModuleCatalog catalog,
            ILogger<UsageTelemetryService> logger)
            : base(repository, moduleConfig, catalog, logger)
        {
        }

        public List<UsageEvent> Events { get; } = [];

        internal override void Enqueue(UsageEvent e)
        {
            lock (Events)
                Events.Add(e);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private int _warnings;

        public int WarningCount => Volatile.Read(ref _warnings);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Interlocked.Increment(ref _warnings);
        }
    }

    private sealed class ThrowingConfigStore : IConfigStore
    {
        public long GetChangeToken() => throw new InvalidOperationException("store unreadable");

        public T Read<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> read) =>
            throw new InvalidOperationException("store unreadable");

        public T Write<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, T> write) =>
            throw new InvalidOperationException("store unreadable");

        public void Write(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction> write) =>
            throw new InvalidOperationException("store unreadable");
    }
}
