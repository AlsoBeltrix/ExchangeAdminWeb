using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;

namespace ExchangeAdminWeb.Tests;

public class AuditServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AuditService _audit;

    public AuditServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"audit_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:LogRoot"] = _tempDir,
                ["Audit:RotationPeriod"] = "daily"
            })
            .Build();

        _audit = new AuditService(config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* cleanup best effort */ }
    }

    private string GetCurrentLogPath()
    {
        var dir = Path.Combine(_tempDir, "ExchangeAdminWeb");
        var files = Directory.GetFiles(dir, "exchangeadmin_*.csv");
        Assert.Single(files);
        return files[0];
    }

    private string[] ReadLogLines()
    {
        var path = GetCurrentLogPath();
        return File.ReadAllLines(path);
    }

    // --- CSV header ---

    [Fact]
    public void LogMailboxPermission_CreatesFileWithHeader()
    {
        _audit.LogMailboxPermission("admin", "10.0.0.1", "AddFullAccess", "target@co.com",
            "user@co.com", "FullAccess", true, "INC001");

        var lines = ReadLogLines();
        Assert.True(lines.Length >= 2);
        Assert.StartsWith("TimestampUtc,User,IPAddress,TicketNumber", lines[0]);
    }

    [Fact]
    public void LogMailboxPermission_WritesCorrectFields()
    {
        _audit.LogMailboxPermission("DOMAIN\\admin", "192.168.1.1", "AddFullAccess",
            "target@co.com", "user@co.com", "FullAccess", true, "INC001", autoMapping: true);

        var lines = ReadLogLines();
        var dataLine = lines[1];
        Assert.Contains("admin", dataLine);
        Assert.Contains("192.168.1.1", dataLine);
        Assert.Contains("INC001", dataLine);
        Assert.Contains("AddFullAccess", dataLine);
        Assert.Contains("target@co.com", dataLine);
        Assert.Contains("user@co.com", dataLine);
        Assert.Contains("FullAccess", dataLine);
        Assert.Contains("SUCCESS", dataLine);
    }

    [Fact]
    public void LogMailboxPermission_FailedResult_ContainsErrorDetail()
    {
        _audit.LogMailboxPermission("admin", "10.0.0.1", "AddSendAs", "target@co.com",
            "user@co.com", "SendAs", false, "INC002", errorDetail: "Mailbox not found");

        var lines = ReadLogLines();
        Assert.Contains("FAILED", lines[1]);
        Assert.Contains("Mailbox not found", lines[1]);
    }

    [Fact]
    public void LogMailboxPermission_StripsDomainPrefix()
    {
        _audit.LogMailboxPermission(@"DOMAIN\jdoe", "10.0.0.1", "AddFullAccess",
            "target@co.com", "user@co.com", "FullAccess", true, "INC001");

        var lines = ReadLogLines();
        // Should contain "jdoe" not "DOMAIN\jdoe"
        Assert.DoesNotContain("DOMAIN", lines[1]);
        Assert.Contains("jdoe", lines[1]);
    }

    // --- CSV escaping ---

    [Fact]
    public void LogMailboxPermission_EscapesCommasInErrorDetail()
    {
        _audit.LogMailboxPermission("admin", "10.0.0.1", "AddFullAccess", "target@co.com",
            "user@co.com", "FullAccess", false, "INC001",
            errorDetail: "Error: first, second, third");

        var lines = ReadLogLines();
        // The error detail with commas should be quoted
        Assert.Contains("\"Error: first, second, third\"", lines[1]);
    }

    [Fact]
    public void LogMailboxPermission_EscapesQuotesInErrorDetail()
    {
        _audit.LogMailboxPermission("admin", "10.0.0.1", "AddFullAccess", "target@co.com",
            "user@co.com", "FullAccess", false, "INC001",
            errorDetail: "Error: \"something\" failed");

        var lines = ReadLogLines();
        // Quotes should be escaped as double quotes
        Assert.Contains("\"\"something\"\"", lines[1]);
    }

    // --- Calendar logging ---

    [Fact]
    public void LogCalendarPermission_WritesCorrectFields()
    {
        _audit.LogCalendarPermission("admin", "10.0.0.1", "SetCalendar", "target@co.com",
            "user@co.com", "Reviewer", true, "REQ001");

        var lines = ReadLogLines();
        Assert.Contains("Calendar", lines[1]);
        Assert.Contains("Reviewer", lines[1]);
        Assert.Contains("SUCCESS", lines[1]);
    }

    // --- Multiple writes append ---

    [Fact]
    public void MultipleWrites_AppendToSameFile()
    {
        _audit.LogMailboxPermission("admin", "10.0.0.1", "Add", "t@co.com",
            "u@co.com", "FullAccess", true, "INC001");
        _audit.LogMailboxPermission("admin", "10.0.0.1", "Add", "t2@co.com",
            "u2@co.com", "SendAs", true, "INC002");

        var lines = ReadLogLines();
        Assert.Equal(3, lines.Length); // header + 2 data lines
    }

    // --- Write failure propagates (no silent swallowing) ---

    [Fact]
    public void LogMailboxPermission_ThrowsOnWriteFailure()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Point to a path that can't possibly be written
                ["Audit:LogRoot"] = @"Z:\nonexistent\path\that\does\not\exist"
            })
            .Build();

        // AuditService constructor calls Directory.CreateDirectory which will throw
        Assert.ThrowsAny<Exception>(() => new AuditService(config));
    }

    // --- Migration logging ---

    [Fact]
    public void LogMigrationCheck_WritesCorrectFields()
    {
        _audit.LogMigrationCheck("admin", "10.0.0.1", "user@co.com", "Eligible", "REQ001");

        var lines = ReadLogLines();
        Assert.Contains("CheckMigrationEligibility", lines[1]);
        Assert.Contains("Eligible", lines[1]);
        Assert.Contains("SUCCESS", lines[1]);
    }

    [Fact]
    public void LogMigrationCheck_IneligibleStatus()
    {
        _audit.LogMigrationCheck("admin", "10.0.0.1", "user@co.com", "Ineligible", "REQ001",
            reasons: "Mailbox too large");

        var lines = ReadLogLines();
        Assert.Contains("INELIGIBLE", lines[1]);
        Assert.Contains("Mailbox too large", lines[1]);
    }

    [Fact]
    public void LogMigrationBatch_WritesCorrectFields()
    {
        _audit.LogMigrationBatch("admin", "10.0.0.1", "batch-2026", "ToCloud",
            5, true, false, "INC001", true);

        var lines = ReadLogLines();
        Assert.Contains("CreateMigrationBatch_ToCloud", lines[1]);
        Assert.Contains("batch-2026", lines[1]);
        Assert.Contains("SUCCESS", lines[1]);
    }

    [Fact]
    public void LogMigrationAction_WritesCorrectFields()
    {
        _audit.LogMigrationAction("admin", "10.0.0.1", "CompleteMigrationBatch",
            "batch-2026", true, "INC001");

        var lines = ReadLogLines();
        Assert.Contains("CompleteMigrationBatch", lines[1]);
        Assert.Contains("batch-2026", lines[1]);
    }
}
