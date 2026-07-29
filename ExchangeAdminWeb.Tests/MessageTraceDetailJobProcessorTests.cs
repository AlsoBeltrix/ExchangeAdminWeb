using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    private ServiceProvider? _provider;

    public MessageTraceDetailJobProcessorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mtdj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _provider?.Dispose();
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
        public required BulkJobRepository Repository { get; init; }
        public required MessageTraceExportStore Store { get; init; }
        public required MessageTraceDetailJobProcessor Processor { get; init; }
    }

    private Fixture CreateFixture(string? logRoot = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = logRoot ?? _tempDir,
            ["Email:AdminNotificationEmail"] = "admin@contoso.com",
            ["Application:PublicBaseUrl"] = "https://apps.contoso.com/ExchangeAdminWeb",
        }).Build();

        var jsonlLog = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, jsonlLog);
        var audit = Substitute.ForPartsOf<AuditService>(jsonlLog, trace);
        var email = Substitute.ForPartsOf<EmailService>(config, NullLogger<EmailService>.Instance);
        var details = new FakeDetailSource();

        // A real jobs store: the save-failure branch writes the marker through it, and asserting on
        // a substitute would prove only that a method was called, not that the record now says so.
        var factory = new SqliteConnectionFactory(Path.Combine(_tempDir, $"jobs-{Guid.NewGuid():N}.db"));
        new JobStoreMigrator(factory).Migrate();
        var repository = new BulkJobRepository(factory);
        _provider = new ServiceCollection().BuildServiceProvider();
        var jobs = new BulkJobService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            repository,
            new BulkJobProcessorRegistry(Array.Empty<KeyValuePair<string, Type>>()),
            config,
            NullLogger<BulkJobService>.Instance);

        var store = new MessageTraceExportStore(config);
        var processor = new MessageTraceDetailJobProcessor(details, email, audit, store, jobs,
            NullLogger<MessageTraceDetailJobProcessor>.Instance);

        return new Fixture
        {
            Details = details,
            Email = email,
            Audit = audit,
            Repository = repository,
            Store = store,
            Processor = processor,
        };
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
            // A real GUID "N", as BulkJobRepository assigns at enqueue. The export store validates
            // this shape before touching the filesystem, so a placeholder id here would not exercise
            // the production path.
            Id = Guid.NewGuid().ToString("N"),
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

        // Emailed the ready-and-linked notification - no attachment, and the admin address is not a
        // recipient even though one is configured (the owner ruled admins never get the results).
        await f.Email.Received(1).SendMessageTraceResultAsync(
            Arg.Is<IReadOnlyList<string>>(r => r.Count == 1 && r[0] == "user@contoso.com"),
            2, "INC1", "jdoe", Arg.Any<DateTime>());
        await f.Email.DidNotReceiveWithAnyArgs().SendMessageTraceFailureAsync(default!, default, default!, default!);
    }

    [Fact]
    public async Task OnJobCompleted_ReadyEmail_CarriesTheRetentionExpiryDate()
    {
        var f = CreateFixture();
        var job = MakeJob("user@contoso.com", Message("m1"));

        await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);
        await f.Processor.OnJobCompletedAsync(job);

        var expected = f.Store.ExpiresAtUtc(job.SubmittedAtUtc);
        await f.Email.Received(1).SendMessageTraceResultAsync(
            Arg.Any<IReadOnlyList<string>>(), 1, "INC1", "jdoe", expected);
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

    // -------------------------------------------------------------------------
    // Save failure (openreview F1) - the export is now the sole delivery, so a failed
    // save must produce a failure notice and a Failed row, never a "ready" mail.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Makes the export directory unwritable by occupying its path with a FILE, so
    /// Directory.CreateDirectory throws. Cheaper and more portable in CI than ACL manipulation, and
    /// it exercises the same swallowed-catch branch in SaveToLogPath.
    /// </summary>
    private Fixture CreateFixtureWithUnwritableExportDir()
    {
        var logRoot = Path.Combine(_tempDir, $"blocked-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(logRoot, "ExchangeAdminWeb", "MessageTraceExports");
        Directory.CreateDirectory(Path.GetDirectoryName(exportDir)!);
        File.WriteAllText(exportDir, "not a directory");
        return CreateFixture(logRoot);
    }

    [Fact]
    public async Task OnJobCompleted_SaveFails_SendsFailureNotice_NotTheReadyEmail()
    {
        var f = CreateFixtureWithUnwritableExportDir();
        var job = MakeJob("user@contoso.com", Message("m1"), Message("m2"));

        await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);
        await f.Processor.OnJobCompletedAsync(job);

        // Assert WHICH email was sent. A test that only counts sends passes with the defect present.
        await f.Email.Received(1).SendMessageTraceFailureAsync(
            Arg.Is<IReadOnlyList<string>>(r => r.Count == 1 && r[0] == "user@contoso.com"),
            2, "INC1", "jdoe");
        await f.Email.DidNotReceiveWithAnyArgs().SendMessageTraceResultAsync(
            default!, default, default!, default!, default);
    }

    /// <summary>
    /// The marker must reach the job record, because that is what the reports page reads to render
    /// Failed instead of Expired. Without it the operator sees ordinary retention and concludes they
    /// waited too long, when in fact the file was never written.
    /// </summary>
    [Fact]
    public async Task OnJobCompleted_SaveFails_StampsTheJobRecordSoThePageCanShowFailed()
    {
        var f = CreateFixtureWithUnwritableExportDir();
        var job = MakeJob("user@contoso.com", Message("m1"));
        job.Status = BulkJobStatus.Completed;
        f.Repository.Insert(job);

        await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);
        await f.Processor.OnJobCompletedAsync(job);

        var stored = f.Repository.Get(job.Id);
        Assert.NotNull(stored);
        Assert.Contains(MessageTraceExportListing.SaveFailedMarker, stored!.Message ?? "",
            StringComparison.OrdinalIgnoreCase);

        // And the page classifies it as Failed - not Expired - from that record alone.
        var listing = new MessageTraceExportListing(
            new BulkJobService(
                _provider!.GetRequiredService<IServiceScopeFactory>(),
                f.Repository,
                new BulkJobProcessorRegistry(Array.Empty<KeyValuePair<string, Type>>()),
                new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Audit:LogRoot"] = _tempDir }).Build(),
                NullLogger<BulkJobService>.Instance),
            f.Store,
            NullLogger<MessageTraceExportListing>.Instance);

        Assert.Equal(MessageTraceExportState.Failed, listing.ClassifyState(stored, out _));
    }

    /// <summary>A delivery failure is not a job failure: the job result must be untouched.</summary>
    [Fact]
    public async Task OnJobCompleted_SaveFails_DoesNotChangeTheJobResult()
    {
        var f = CreateFixtureWithUnwritableExportDir();
        var job = MakeJob("user@contoso.com", Message("m1"));
        job.Status = BulkJobStatus.Completed;
        var finishedAt = job.FinishedAtUtc;
        f.Repository.Insert(job);

        await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);
        await f.Processor.OnJobCompletedAsync(job);

        var stored = f.Repository.Get(job.Id);
        Assert.NotNull(stored);
        Assert.Equal(BulkJobStatus.Completed, stored!.Status);
        Assert.Equal(finishedAt, stored.FinishedAtUtc);
    }

    /// <summary>The save-failure path must not throw out of the fail-safe completion hook.</summary>
    [Fact]
    public async Task OnJobCompleted_SaveFails_JobNotInStore_DoesNotThrow()
    {
        var f = CreateFixtureWithUnwritableExportDir();
        // The job was never inserted, so the marker write matches no row.
        var job = MakeJob("user@contoso.com", Message("m1"));

        await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);
        await f.Processor.OnJobCompletedAsync(job);

        await f.Email.Received(1).SendMessageTraceFailureAsync(
            Arg.Any<IReadOnlyList<string>>(), 1, "INC1", "jdoe");
    }

    [Fact]
    public async Task OnJobCompleted_SaveSucceeds_DoesNotStampTheSaveFailedMarker()
    {
        var f = CreateFixture();
        var job = MakeJob("user@contoso.com", Message("m1"));
        job.Status = BulkJobStatus.Completed;
        f.Repository.Insert(job);

        await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);
        await f.Processor.OnJobCompletedAsync(job);

        var stored = f.Repository.Get(job.Id);
        Assert.DoesNotContain(MessageTraceExportListing.SaveFailedMarker, stored!.Message ?? "",
            StringComparison.OrdinalIgnoreCase);
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
