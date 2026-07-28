using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Slice-5 coverage for the Message Analysis detail-export bulk processor
/// (docs/MessageTraceDetail-Plan.md). Exercises the deterministic seams without live EXO/on-prem:
/// CountRows = selection; per-row fail-soft (a fetch error becomes a Failed row, never a throw);
/// OnJobCompletedAsync assembles the report from the retained per-row details, saves it under the
/// audit log path, zips it, and calls the (virtual) email seam - verified via NSubstitute.
/// </summary>
public sealed class MessageTraceDetailJobProcessorTests : IDisposable
{
    private readonly string _tempDir;

    public MessageTraceDetailJobProcessorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mtdj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // A fake detail source: records each requested message and returns a scripted detail (or error).
    private sealed class FakeDetailSource : IMessageTraceDetailSource
    {
        public readonly List<string> Fetched = new();
        public Func<MessageTraceResult, MessageTraceDetail> Result = m =>
            new MessageTraceDetail { Summary = m, Events = { new MessageTraceDetailEvent { Event = "DELIVER" } } };

        public Task<MessageTraceDetail> GetMessageDetailAsync(MessageTraceResult message, CancellationToken cancellationToken = default)
        {
            Fetched.Add(message.MessageId);
            return Task.FromResult(Result(message));
        }
    }

    private sealed class Fixture
    {
        public required FakeDetailSource Details { get; init; }
        public required EmailService Email { get; init; }
        public required AuditService Audit { get; init; }
        public required MessageTraceDetailJobProcessor Processor { get; init; }
    }

    private Fixture CreateFixture()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _tempDir,
            ["Email:AdminNotificationEmail"] = "admin@contoso.com",
        }).Build();

        var jsonlLog = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, jsonlLog);
        var audit = Substitute.ForPartsOf<AuditService>(jsonlLog, trace);
        var email = Substitute.ForPartsOf<EmailService>(config, NullLogger<EmailService>.Instance);
        var details = new FakeDetailSource();

        var processor = new MessageTraceDetailJobProcessor(details, email, audit, config,
            NullLogger<MessageTraceDetailJobProcessor>.Instance);

        return new Fixture { Details = details, Email = email, Audit = audit, Processor = processor };
    }

    private static MessageTraceResult Message(string id, string backend = "ExchangeOnline") => new()
    {
        Received = DateTime.UtcNow,
        SenderAddress = "s@contoso.com",
        RecipientAddress = "r@contoso.com",
        Subject = "hi",
        Status = "Delivered",
        MessageId = id,
        MessageTraceId = id + "-trace",
        Backend = backend,
    };

    private static BulkJob MakeJob(string? userEmail, params MessageTraceResult[] messages)
    {
        var payload = new MessageTraceDetailJobPayload
        {
            Messages = messages.ToList(),
            UserEmail = userEmail,
        };
        return new BulkJob
        {
            Id = "jt1",
            ModuleId = MessageTraceDetailJobProcessor.ModuleName,
            JobType = MessageTraceDetailJobPayload.JobType,
            Status = BulkJobStatus.Running,
            SubmittedBy = "jdoe",
            SubmittedIp = "10.0.0.9",
            Ticket = "INC1",
            PayloadJson = JsonSerializer.Serialize(payload),
            SubmittedAtUtc = DateTime.UtcNow,
        };
    }

    [Fact]
    public void CountRows_EqualsSelectionCount()
    {
        var f = CreateFixture();
        var job = MakeJob("user@contoso.com", Message("m1"), Message("m2"), Message("m3"));

        Assert.Equal(3, f.Processor.CountRows(job));
    }

    [Fact]
    public async Task ProcessRow_HappyPath_FetchesDetail_ReturnsSuccess()
    {
        var f = CreateFixture();
        var job = MakeJob("user@contoso.com", Message("m1"));

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Success, outcome.Status);
        Assert.Equal("m1", outcome.Target);
        Assert.Equal(["m1"], f.Details.Fetched);
    }

    [Fact]
    public async Task ProcessRow_FetchError_IsFailedRow_NotThrow()
    {
        var f = CreateFixture();
        f.Details.Result = m => new MessageTraceDetail { Summary = m, Error = "cloud fetch failed" };
        var job = MakeJob("user@contoso.com", Message("m1"));

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Equal("cloud fetch failed", outcome.Message);
    }

    [Fact]
    public async Task OnJobCompleted_BuildsReport_SavesToLogPath_AndEmails()
    {
        var f = CreateFixture();
        var job = MakeJob("user@contoso.com", Message("m1"), Message("m2"));

        await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);
        await f.Processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await f.Processor.OnJobCompletedAsync(job);

        // Saved under <logRoot>\ExchangeAdminWeb\MessageTraceExports\.
        var exportDir = Path.Combine(_tempDir, "ExchangeAdminWeb", "MessageTraceExports");
        var csvFiles = Directory.GetFiles(exportDir, "*.csv");
        Assert.Single(csvFiles);
        Assert.Contains("Message 1 of 2", File.ReadAllText(csvFiles[0]));

        // Emailed the zip to the resolved recipients, gated to the authenticated user (never operator-typed).
        await f.Email.Received(1).SendMessageTraceResultAsync(
            "user@contoso.com", Arg.Any<byte[]>(), Arg.Is<string>(n => n.EndsWith(".zip")),
            2, "INC1", "jdoe");
    }

    [Fact]
    public async Task OnJobCompleted_RetainsFailedDetail_NotDropped()
    {
        var f = CreateFixture();
        // First message fetches an error; it must still appear in the export (Known Failure Class #2).
        f.Details.Result = m => m.MessageId == "m1"
            ? new MessageTraceDetail { Summary = m, Error = "deferred lookup failed" }
            : new MessageTraceDetail { Summary = m, Events = { new MessageTraceDetailEvent { Event = "DELIVER" } } };
        var job = MakeJob("user@contoso.com", Message("m1"), Message("m2"));

        await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);
        await f.Processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await f.Processor.OnJobCompletedAsync(job);

        var exportDir = Path.Combine(_tempDir, "ExchangeAdminWeb", "MessageTraceExports");
        var csv = File.ReadAllText(Directory.GetFiles(exportDir, "*.csv").Single());
        Assert.Contains("deferred lookup failed", csv);
        Assert.Contains("Message 2 of 2", csv);
    }

    [Fact]
    public async Task OnJobCompleted_UnprocessedMessage_StillInReport()
    {
        var f = CreateFixture();
        var job = MakeJob("user@contoso.com", Message("m1"), Message("m2"));

        // Only the first row runs (e.g. the job was cancelled); the second must still be emitted.
        await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);
        await f.Processor.OnJobCompletedAsync(job);

        var exportDir = Path.Combine(_tempDir, "ExchangeAdminWeb", "MessageTraceExports");
        var csv = File.ReadAllText(Directory.GetFiles(exportDir, "*.csv").Single());
        Assert.Contains("Message 2 of 2", csv);
        Assert.Contains("Not processed", csv);
    }
}
