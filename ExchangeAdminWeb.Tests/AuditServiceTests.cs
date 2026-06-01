using System.Text.Json;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class AuditServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AuditService _audit;
    private readonly OperationTraceService _trace;

    public AuditServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"audit_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = CreateConfig(_tempDir);
        (_audit, _trace) = CreateAudit(config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { }
    }

    private static IConfiguration CreateConfig(string logRoot, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = logRoot,
            ["Audit:RotationPeriod"] = "daily"
        };

        if (extra != null)
        {
            foreach (var item in extra)
                settings[item.Key] = item.Value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    private static (AuditService Audit, OperationTraceService Trace) CreateAudit(IConfiguration config)
    {
        var log = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var trace = new OperationTraceService(config, log);
        return (new AuditService(log, trace), trace);
    }

    private string GetCurrentAuditLogPath()
    {
        var dir = Path.Combine(_tempDir, "ExchangeAdminWeb");
        var files = Directory.GetFiles(dir, "exchangeadmin_*.jsonl")
            .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Single(files);
        return files[0];
    }

    private string GetCurrentTraceLogPath()
    {
        var dir = Path.Combine(_tempDir, "ExchangeAdminWeb");
        var files = Directory.GetFiles(dir, "exchangeadmin_*_trace.jsonl");
        Assert.Single(files);
        return files[0];
    }

    private static JsonDocument[] ReadEvents(string path)
    {
        return File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l))
            .ToArray();
    }

    private JsonDocument[] ReadAuditEvents() => ReadEvents(GetCurrentAuditLogPath())
        .Where(e => e.RootElement.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
        .ToArray();

    private JsonDocument[] ReadOperationEvents() => ReadEvents(GetCurrentTraceLogPath())
        .Where(e => e.RootElement.TryGetProperty("eventType", out var type) && type.GetString()!.StartsWith("operation.", StringComparison.Ordinal))
        .ToArray();

    [Fact]
    public void LogMailboxPermission_CreatesJsonlFile()
    {
        _audit.LogMailboxPermission("admin", "10.0.0.1", "AddFullAccess", "target@co.com",
            "user@co.com", "FullAccess", true, "INC001");

        var events = ReadAuditEvents();
        Assert.Single(events);
        Assert.Equal(3, ReadOperationEvents().Length);
        Assert.NotEqual(GetCurrentAuditLogPath(), GetCurrentTraceLogPath());
    }

    [Fact]
    public void LogMailboxPermission_WritesCorrectFields()
    {
        _audit.LogMailboxPermission("DOMAIN\\admin", "192.168.1.1", "AddFullAccess",
            "target@co.com", "user@co.com", "FullAccess", true, "INC001", autoMapping: true);

        var events = ReadAuditEvents();
        var root = events[0].RootElement;
        Assert.Equal("audit", root.GetProperty("eventType").GetString());
        Assert.Equal("admin", root.GetProperty("user").GetString());
        Assert.Equal("192.168.1.1", root.GetProperty("ip").GetString());
        Assert.Equal("INC001", root.GetProperty("ticket").GetString());
        Assert.Equal("AddFullAccess", root.GetProperty("action").GetString());
        Assert.Equal("target@co.com", root.GetProperty("target").GetString());
        Assert.Equal("user@co.com", root.GetProperty("affectedUser").GetString());
        Assert.Equal("FullAccess", root.GetProperty("permissionType").GetString());
        Assert.Equal("MailboxPermission", root.GetProperty("category").GetString());
        Assert.Equal("Success", root.GetProperty("result").GetString());
        Assert.True(root.GetProperty("autoMapping").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("operationId").GetString()));
    }

    [Fact]
    public void LogMailboxPermission_FailedResult_ContainsErrorDetail()
    {
        _audit.LogMailboxPermission("admin", "10.0.0.1", "AddSendAs", "target@co.com",
            "user@co.com", "SendAs", false, "INC002", errorDetail: "Mailbox not found");

        var events = ReadAuditEvents();
        var root = events[0].RootElement;
        Assert.Equal("Failed", root.GetProperty("result").GetString());
        Assert.Equal("Mailbox not found", root.GetProperty("error").GetString());
    }

    [Fact]
    public void LogMailboxPermission_StripsDomainPrefix()
    {
        _audit.LogMailboxPermission(@"DOMAIN\jdoe", "10.0.0.1", "AddFullAccess",
            "target@co.com", "user@co.com", "FullAccess", true, "INC001");

        var events = ReadAuditEvents();
        var root = events[0].RootElement;
        Assert.Equal("jdoe", root.GetProperty("user").GetString());
    }

    [Fact]
    public void LogMailboxPermission_SuccessOmitsErrorField()
    {
        _audit.LogMailboxPermission("admin", "10.0.0.1", "AddFullAccess", "target@co.com",
            "user@co.com", "FullAccess", true, "INC001");

        var events = ReadAuditEvents();
        var root = events[0].RootElement;
        Assert.False(root.TryGetProperty("error", out _));
    }

    [Fact]
    public void LogMailboxPermission_EmptyTicketOmitsField()
    {
        _audit.LogMailboxPermission("admin", "10.0.0.1", "AddFullAccess", "target@co.com",
            "user@co.com", "FullAccess", true, "");

        var events = ReadAuditEvents();
        var root = events[0].RootElement;
        Assert.False(root.TryGetProperty("ticket", out _));
    }

    [Fact]
    public void LogCalendarPermission_WritesCorrectFields()
    {
        _audit.LogCalendarPermission("admin", "10.0.0.1", "SetCalendar", "target@co.com",
            "user@co.com", "Reviewer", true, "REQ001");

        var events = ReadAuditEvents();
        var root = events[0].RootElement;
        Assert.Equal("CalendarPermission", root.GetProperty("category").GetString());
        Assert.Equal("Reviewer", root.GetProperty("accessRight").GetString());
        Assert.Equal("Success", root.GetProperty("result").GetString());
    }

    [Fact]
    public void MultipleWrites_AppendToSameFile()
    {
        _audit.LogMailboxPermission("admin", "10.0.0.1", "Add", "t@co.com",
            "u@co.com", "FullAccess", true, "INC001");
        _audit.LogMailboxPermission("admin", "10.0.0.1", "Add", "t2@co.com",
            "u2@co.com", "SendAs", true, "INC002");

        var events = ReadAuditEvents();
        Assert.Equal(2, events.Length);
        Assert.Equal(6, ReadOperationEvents().Length);
    }

    [Fact]
    public void LogMailboxPermission_ThrowsOnWriteFailure()
    {
        var config = CreateConfig(@"Z:\nonexistent\path\that\does\not\exist");

        Assert.ThrowsAny<Exception>(() => CreateAudit(config));
    }

    [Fact]
    public void LogMigrationCheck_WritesCorrectFields()
    {
        _audit.LogMigrationCheck("admin", "10.0.0.1", "user@co.com", "Eligible", "REQ001");

        var events = ReadAuditEvents();
        var root = events[0].RootElement;
        Assert.Equal("CheckMigrationEligibility", root.GetProperty("action").GetString());
        Assert.Equal("MigrationCheck", root.GetProperty("category").GetString());
        Assert.Equal("Eligible", root.GetProperty("status").GetString());
        Assert.Equal("Success", root.GetProperty("result").GetString());
    }

    [Fact]
    public void LogMigrationCheck_IneligibleStatus()
    {
        _audit.LogMigrationCheck("admin", "10.0.0.1", "user@co.com", "Ineligible", "REQ001",
            reasons: "Mailbox too large");

        var events = ReadAuditEvents();
        var root = events[0].RootElement;
        Assert.Equal("Ineligible", root.GetProperty("result").GetString());
        Assert.Equal("Mailbox too large", root.GetProperty("reasons").GetString());
    }

    [Fact]
    public void LogMigrationBatch_WritesCorrectFields()
    {
        _audit.LogMigrationBatch("admin", "10.0.0.1", "batch-2026", "ToCloud",
            5, true, false, "INC001", true);

        var events = ReadAuditEvents();
        var root = events[0].RootElement;
        Assert.Equal("CreateMigrationBatch_ToCloud", root.GetProperty("action").GetString());
        Assert.Equal("MigrationBatch", root.GetProperty("category").GetString());
        Assert.Equal("batch-2026", root.GetProperty("batchName").GetString());
        Assert.Equal(5, root.GetProperty("userCount").GetInt32());
        Assert.True(root.GetProperty("autoStart").GetBoolean());
        Assert.False(root.GetProperty("autoComplete").GetBoolean());
        Assert.Equal("Success", root.GetProperty("result").GetString());
    }

    [Fact]
    public void LogMigrationAction_WritesCorrectFields()
    {
        _audit.LogMigrationAction("admin", "10.0.0.1", "CompleteMigrationBatch",
            "batch-2026", true, "INC001");

        var events = ReadAuditEvents();
        var root = events[0].RootElement;
        Assert.Equal("CompleteMigrationBatch", root.GetProperty("action").GetString());
        Assert.Equal("MigrationAction", root.GetProperty("category").GetString());
        Assert.Equal("batch-2026", root.GetProperty("target").GetString());
    }

    [Fact]
    public void AuditTransaction_WritesCorrelatedOperationTrace()
    {
        _audit.LogMigrationAction("admin", "10.0.0.1", "StartMigrationBatch", "batch-1", true, "INC123");

        var auditEvent = ReadAuditEvents().Single().RootElement;
        var operationId = auditEvent.GetProperty("operationId").GetString();
        var operationEvents = ReadOperationEvents().Select(e => e.RootElement).ToArray();

        Assert.NotNull(operationId);
        Assert.All(operationEvents, e => Assert.Equal(operationId, e.GetProperty("operationId").GetString()));
        Assert.Contains(operationEvents, e => e.GetProperty("eventType").GetString() == "operation.start");
        Assert.Contains(operationEvents, e => e.GetProperty("eventType").GetString() == "operation.step" && e.GetProperty("stage").GetString() == "AuditWritten");
        Assert.Contains(operationEvents, e => e.GetProperty("eventType").GetString() == "operation.complete");
    }

    [Fact]
    public void BackendStepWithoutActiveOperation_WritesStandaloneTraceWithoutContaminatingAudit()
    {
        _trace.Step("VaultCredentialRequested", backend: "Delinea", command: "GET /api/v1/secrets/{secretId}", details: new Dictionary<string, object?> { ["secretId"] = 123 });
        _audit.LogMigrationAction("admin", "10.0.0.1", "CompleteMigrationBatch", "batch-2026", true, "INC001");

        var auditEvent = ReadAuditEvents().Single().RootElement;
        var auditOperationId = auditEvent.GetProperty("operationId").GetString();
        var operationEvents = ReadOperationEvents().Select(e => e.RootElement).ToArray();
        var backendOperationId = operationEvents.Single(e => e.GetProperty("stage").GetString() == "VaultCredentialRequested").GetProperty("operationId").GetString();

        Assert.NotEqual(auditOperationId, backendOperationId);
        Assert.Contains(operationEvents, e => e.GetProperty("operationId").GetString() == backendOperationId && e.GetProperty("eventType").GetString() == "operation.start");
        Assert.Contains(operationEvents, e => e.GetProperty("operationId").GetString() == backendOperationId && e.GetProperty("eventType").GetString() == "operation.complete");
        Assert.Contains(operationEvents, e => e.GetProperty("operationId").GetString() == auditOperationId && e.GetProperty("stage").GetString() == "AuditWritten");
        Assert.Contains(operationEvents, e => e.GetProperty("operationId").GetString() == auditOperationId && e.GetProperty("eventType").GetString() == "operation.complete");
    }

    [Fact]
    public void OperationTrace_DoesNotWriteRawExceptionMessages()
    {
        _trace.Step(
            "VaultCredentialRequested",
            result: "Failed",
            backend: "Delinea",
            command: "oauth2/token",
            details: new Dictionary<string, object?> { ["secretId"] = 123 },
            exception: new InvalidOperationException("raw oauth response body"));

        var operationEvents = ReadOperationEvents().Select(e => e.RootElement).ToArray();
        var failedStep = operationEvents.Single(e => e.GetProperty("stage").GetString() == "VaultCredentialRequested");

        Assert.Equal("InvalidOperationException", failedStep.GetProperty("errorType").GetString());
        Assert.False(failedStep.TryGetProperty("error", out _));
        Assert.Equal("***", failedStep.GetProperty("details").GetProperty("secretId").GetString());
        Assert.DoesNotContain(operationEvents, e => e.GetRawText().Contains("raw oauth response body", StringComparison.Ordinal));
    }
}
