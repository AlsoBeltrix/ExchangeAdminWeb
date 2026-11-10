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
        // The usage sink is passed explicitly as null: the proxy generator binds constructors by
        // exact arity and does not fill in the optional third parameter.
        var audit = Substitute.ForPartsOf<AuditService>(jsonlLog, trace, null);
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
            new BulkJobProcessorRegistry(Array.Empty<KeyValuePair<string, Type>>()), new StubJobAuthorization(),
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

    /// <summary>
    /// A job with no <c>Recipients</c> on its payload - the shape enqueued before the recipient box
    /// existed, which the processor must still honour by falling back to the submitter's address.
    /// </summary>
    private static BulkJob MakeJob(string? userEmail, params MessageTraceResult[] messages) =>
        MakeJob(userEmail, null, messages);

    private static BulkJob MakeJob(string? userEmail, List<string>? recipients, params MessageTraceResult[] messages)
    {
        var payload = new MessageTraceDetailJobPayload
        {
            Messages = messages.ToList(),
            UserEmail = userEmail,
            Recipients = recipients,
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
        // Asserts the export CONTENT, not its layout. This previously matched "Message 1 of 2",
        // a marker of the stacked multi-shape format that made the file unreadable in Excel; the
        // export is now one table with one row per message, and what matters here is that both
        // requested messages are in it.
        var exported = File.ReadAllText(csvFiles[0]);
        Assert.Equal(3, exported.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);

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

    // -------------------------------------------------------------------------
    // Recipient set (slice 4, plan D4)
    // -------------------------------------------------------------------------

    /// <summary>
    /// The operator's edited list is what gets mailed, not their own address. This is the whole
    /// point of D4 (a): the pre-fill is a default, not a floor.
    /// </summary>
    [Fact]
    public async Task OnJobCompleted_MailsThePayloadRecipients_NotTheSubmitter()
    {
        var f = CreateFixture();
        var job = MakeJob("user@contoso.com", ["someone.else@contoso.com", "second@contoso.com"], Message("m1"));

        await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);
        await f.Processor.OnJobCompletedAsync(job);

        await f.Email.Received(1).SendMessageTraceResultAsync(
            Arg.Is<IReadOnlyList<string>>(r =>
                r.Count == 2 && r.Contains("someone.else@contoso.com") && r.Contains("second@contoso.com")
                && !r.Contains("user@contoso.com")),
            1, "INC1", "jdoe", Arg.Any<DateTime>());
    }

    /// <summary>
    /// A CLEARED box means notify nobody. Silently reinstating the submitter would be option (c),
    /// which the owner rejected. The export is still produced and still listed on the reports page -
    /// only the mail is skipped, which the send itself already handles for an empty set.
    /// </summary>
    [Fact]
    public async Task OnJobCompleted_EmptyRecipientList_DoesNotFallBackToTheSubmitter()
    {
        var f = CreateFixture();
        var job = MakeJob("user@contoso.com", [], Message("m1"));

        await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);
        await f.Processor.OnJobCompletedAsync(job);

        await f.Email.Received(1).SendMessageTraceResultAsync(
            Arg.Is<IReadOnlyList<string>>(r => r.Count == 0), 1, "INC1", "jdoe", Arg.Any<DateTime>());

        // The export itself is unaffected: no recipient is not a failure.
        var exportDir = Path.Combine(_tempDir, "ExchangeAdminWeb", "MessageTraceExports");
        Assert.Single(Directory.GetFiles(exportDir, "*.csv"));
    }

    /// <summary>
    /// A job enqueued before the recipient box existed carries no Recipients at all. Null is not the
    /// same as empty: it must behave as it did when submitted, mailing the submitter.
    /// </summary>
    [Fact]
    public async Task OnJobCompleted_NullRecipients_FallsBackToTheSubmitter()
    {
        var f = CreateFixture();
        var job = MakeJob("user@contoso.com", recipients: null, Message("m1"));

        await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);
        await f.Processor.OnJobCompletedAsync(job);

        await f.Email.Received(1).SendMessageTraceResultAsync(
            Arg.Is<IReadOnlyList<string>>(r => r.Count == 1 && r[0] == "user@contoso.com"),
            1, "INC1", "jdoe", Arg.Any<DateTime>());
    }

    [Fact]
    public async Task OnJobCompleted_SaveFails_UsesThePayloadRecipientsToo()
    {
        var f = CreateFixtureWithUnwritableExportDir();
        var job = MakeJob("user@contoso.com", ["someone.else@contoso.com"], Message("m1"));

        await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);
        await f.Processor.OnJobCompletedAsync(job);

        await f.Email.Received(1).SendMessageTraceFailureAsync(
            Arg.Is<IReadOnlyList<string>>(r => r.Count == 1 && r[0] == "someone.else@contoso.com"),
            1, "INC1", "jdoe");
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
        // Both messages present: header plus two rows. (Was "Message 2 of 2", a marker of the
        // old stacked format.)
        Assert.Equal(3, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
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
                new BulkJobProcessorRegistry(Array.Empty<KeyValuePair<string, Type>>()), new StubJobAuthorization(),
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

    // -------------------------------------------------------------------------
    // Through the real runner, over the real authorization check and its stores
    // -------------------------------------------------------------------------

    private const string Disabled = "Message Analysis is currently disabled.";
    private const string Denied = "Authorization denied.";

    /// <summary>The real runner over the fixture's job store, its one processor the fixture's.</summary>
    private BulkJobService Runner(Fixture f, JobAuthorizationStores stores)
    {
        var provider = new ServiceCollection().AddScoped(_ => f.Processor).BuildServiceProvider();
        return new BulkJobService(provider.GetRequiredService<IServiceScopeFactory>(), f.Repository,
            new BulkJobProcessorRegistry([new(MessageTraceDetailJobProcessor.ModuleName, typeof(MessageTraceDetailJobProcessor))]),
            stores.Authorization, new ConfigurationBuilder().Build(), NullLogger<BulkJobService>.Instance);
    }

    private static BulkJob QueuedJob(string? snapshot)
    {
        var job = MakeJob("user@contoso.com", Message("m1"), Message("m2"), Message("m3"));
        return new BulkJob
        {
            Id = job.Id,
            ModuleId = job.ModuleId,
            JobType = job.JobType,
            Status = BulkJobStatus.Queued,
            SubmittedBy = job.SubmittedBy,
            SubmittedIp = job.SubmittedIp,
            Ticket = job.Ticket,
            AuthSnapshotJson = snapshot,
            PayloadJson = job.PayloadJson,
            SubmittedAtUtc = job.SubmittedAtUtc,
        };
    }

    /// <summary>The one audit row of the job's export: whether it is filed a success, and its error.</summary>
    private (bool Success, string? Error) ExportAudit()
    {
        var row = Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".jsonl", StringComparison.Ordinal) && !path.EndsWith("_trace.jsonl", StringComparison.Ordinal))
            .SelectMany(File.ReadAllLines)
            .Single(line => line.Contains("\"MessageTrace_DetailExport\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(row);
        return (document.RootElement.GetProperty("result").GetString() == "Success",
            document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null);
    }

    private string[] ExportFiles()
    {
        var directory = Path.Combine(_tempDir, "ExchangeAdminWeb", "MessageTraceExports");
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.csv") : [];
    }

    private async Task AssertNothingDelivered(Fixture f, BulkJob job, string reason)
    {
        Assert.Empty(ExportFiles());
        await f.Email.DidNotReceiveWithAnyArgs().SendMessageTraceResultAsync(default!, default, default!, default!, default);
        await f.Email.DidNotReceiveWithAnyArgs().SendMessageTraceFailureAsync(default!, default, default!, default!);
        var refused = $"{MessageTraceExportListing.RefusedMarker}: {reason}";
        Assert.Equal(refused, f.Repository.Get(job.Id)!.Message);
        Assert.Equal((false, $"3 message(s); {refused}"), ExportAudit());
    }

    [Theory]
    [InlineData("disabled", Disabled)]
    [InlineData("access-removed", Denied)]
    [InlineData("unreadable-enablement", Disabled)]
    [InlineData("unreadable-sections", Denied)]
    public async Task Runner_AJobThatMayNoLongerRunAfterItsFirstRow_FetchesNoMore_AndDeliversNothing(string mode, string reason)
    {
        using var stores = new JobAuthorizationStores("MessageTrace", "MessageTrace");
        var f = CreateFixture();
        var runner = Runner(f, stores);
        var job = QueuedJob(stores.Snapshot());
        f.Repository.Insert(job);
        var taken = false;
        runner.JobChanged += id =>
        {
            if (taken || f.Repository.Get(id)!.ProcessedRows != 1)
                return;
            taken = true;
            if (mode == "disabled") stores.DisableModule();
            else if (mode == "access-removed") stores.RemoveAccess();
            else stores.BreakStore(enablement: mode == "unreadable-enablement");
        };

        await runner.DrainQueueAsync(TestContext.Current.CancellationToken);

        var done = f.Repository.Get(job.Id)!;
        Assert.True(taken);
        Assert.Equal(["m1"], f.Details.Fetched);
        Assert.Equal((BulkJobStatus.Completed, 3, 1, 2), (done.Status, done.ProcessedRows, done.SuccessCount, done.FailedCount));
        Assert.Equal([null, reason, reason], runner.GetRows(job.Id).Select(row => row.Message));
        await AssertNothingDelivered(f, job, reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{not-json")]
    [InlineData("{\"Section\":\"Migration\",\"RoleClaims\":[\"S-1-5-21-100-200-300-4001\"],\"AuthorizedGroups\":[\"S-1-5-21-100-200-300-4001\"]}")]
    public async Task Runner_AJobWithNoUsableSnapshot_IsRefusedOnEveryRow(string? snapshot)
    {
        using var stores = new JobAuthorizationStores("MessageTrace", "MessageTrace");
        var f = CreateFixture();
        var runner = Runner(f, stores);
        var job = QueuedJob(snapshot);
        f.Repository.Insert(job);

        await runner.DrainQueueAsync(TestContext.Current.CancellationToken);

        var done = f.Repository.Get(job.Id)!;
        Assert.Empty(f.Details.Fetched);
        Assert.Equal((BulkJobStatus.Completed, 3, 0, 3), (done.Status, done.ProcessedRows, done.SuccessCount, done.FailedCount));
        Assert.All(runner.GetRows(job.Id), row => Assert.Equal(Denied, row.Message));
        await AssertNothingDelivered(f, job, Denied);
    }

    [Fact]
    public async Task Runner_AnAllowedJob_FetchesEveryMessage_AndDeliversTheExport()
    {
        using var stores = new JobAuthorizationStores("MessageTrace", "MessageTrace");
        var f = CreateFixture();
        var runner = Runner(f, stores);
        var job = QueuedJob(stores.Snapshot());
        f.Repository.Insert(job);

        await runner.DrainQueueAsync(TestContext.Current.CancellationToken);

        var done = f.Repository.Get(job.Id)!;
        Assert.Equal(["m1", "m2", "m3"], f.Details.Fetched);
        Assert.Equal((BulkJobStatus.Completed, 3, 3, 0), (done.Status, done.ProcessedRows, done.SuccessCount, done.FailedCount));
        Assert.Null(done.Message);
        Assert.Single(ExportFiles());
        await f.Email.Received(1).SendMessageTraceResultAsync(
            Arg.Is<IReadOnlyList<string>>(r => r.Count == 1 && r[0] == "user@contoso.com"), 3, "INC1", "jdoe", Arg.Any<DateTime>());
        Assert.Equal((true, null), ExportAudit());
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
        // The unprocessed message keeps its row rather than being dropped (Known Failure Class
        // #2). Header plus two rows; the second carries the explanation.
        Assert.Equal(3, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("Not processed", csv);
    }
}
