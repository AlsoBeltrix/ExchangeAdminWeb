using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using Page = ExchangeAdminWeb.Components.Pages.MessageTraceReports;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-74 (2), Message Trace's Downloadable Reports. <c>Download</c> had no busy guard: every Download button is disabled
/// while <c>busy</c>, but the browser's copy of that is one round trip stale, so a second click sent before the disabled
/// button rendered read the export again, pushed it to the browser again and audited it again. A second click during a
/// download is now ignored, as the Message Trace page's live download has been since kd-72.
/// </summary>
/// <remarks>
/// Driven through the compiled handler over the real <see cref="MessageTraceExportListing"/>, a real job store holding one
/// finished export job and its file on disk. The listing's file read - its seam, <see cref="MessageTraceExportListing.ReadFileAsync"/> -
/// counts every read and holds each until the test lets it go, so the first download is in flight when the second click
/// lands. The browser stand-in records every file pushed; the audit rows are read back from the log folder.
/// </remarks>
public sealed class MessageTraceReportsDownloadBusyGuardTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);
    private static readonly DateTime Submitted = new(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly TempDir _temp = new();
    private readonly string _logRoot;
    private readonly BulkJobRepository _repository;
    private readonly MessageTraceExportStore _store;
    private readonly ServiceProvider _provider;
    private readonly AuditService _audit;
    private readonly HeldReadListing _listing;
    private readonly RecordingProgress _progress = new();
    private readonly RecordingJs _browser = new();

    public MessageTraceReportsDownloadBusyGuardTests()
    {
        _logRoot = Path.Combine(_temp.Path, "logs");
        Directory.CreateDirectory(_logRoot);

        var factory = new SqliteConnectionFactory(Path.Combine(_temp.Path, "exchangeadmin-jobs.db"));
        new JobStoreMigrator(factory).Migrate();
        _repository = new BulkJobRepository(factory);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _logRoot,
            ["OperationTrace:Enabled"] = "false",
        }).Build();

        _provider = new ServiceCollection().BuildServiceProvider();
        var jobs = new BulkJobService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _repository,
            new BulkJobProcessorRegistry(Array.Empty<KeyValuePair<string, Type>>()),
            config,
            NullLogger<BulkJobService>.Instance);
        _store = new MessageTraceExportStore(config);
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        _audit = new AuditService(log, new OperationTraceService(config, log));
        _listing = new HeldReadListing(jobs, _store);
    }

    public void Dispose()
    {
        // Let go first, so a read still held - a test that failed with a download in flight - ends before its folder goes.
        _listing.Release.TrySetResult();
        _provider.Dispose();
        _temp.Dispose();
    }

    /// <summary>The page's listing, its file read counted and held until <see cref="Release"/> is set.</summary>
    private sealed class HeldReadListing(BulkJobService jobs, MessageTraceExportStore store)
        : MessageTraceExportListing(jobs, store, NullLogger<MessageTraceExportListing>.Instance)
    {
        private int _reads;

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Reads => Volatile.Read(ref _reads);

        public override async Task<byte[]> ReadFileAsync(string fullPath)
        {
            Interlocked.Increment(ref _reads);
            await Release.Task;
            return await base.ReadFileAsync(fullPath);
        }
    }

    // The finding: a second click while the first download is in flight returns at once, and one download ran - one read,
    // one activity, one file pushed, one audit row.
    [Fact]
    public async Task ASecondClickWhileADownloadRuns_IsIgnored_NotASecondDownload()
    {
        var jobId = SeedExportWithItsFile();
        var page = PageFor();

        var first = Call(page, "Download", jobId);
        Assert.False(first.IsCompleted, "the first download finished before a second click could land");
        // A click the browser sent before the disabled Download rendered.
        var second = Call(page, "Download", jobId);
        Assert.True(second.IsCompleted, "a second click during a download started a second download");
        _listing.Release.SetResult();
        await first.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(1, _listing.Reads);
        Assert.Single(_progress.Handles);
        Assert.Single(_browser.FileNames);
        var row = Assert.Single(AuditRows(_logRoot));
        Assert.Equal("MessageTrace_ExportDownload", Text(row, "action"));
        Assert.Equal("Success", Text(row, "result"));
        Assert.False(Field<bool>(page, "busy"));
    }

    // The control: the guard ignores a click only while a download runs. Once it has finished, Download runs again as before.
    [Fact]
    public async Task ADownloadAfterTheFirstHasFinished_RunsAsBefore()
    {
        var jobId = SeedExportWithItsFile();
        var page = PageFor();
        _listing.Release.SetResult();

        await Call(page, "Download", jobId).WaitAsync(Bound, TestContext.Current.CancellationToken);
        await Call(page, "Download", jobId).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(2, _listing.Reads);
        Assert.Equal(2, _browser.FileNames.Count);
        Assert.Equal(2, AuditRows(_logRoot).Count);
    }

    private Page PageFor()
    {
        var page = new Page();
        Set(page, "Exports", _listing);
        Set(page, "Audit", _audit);
        Set(page, "Progress", _progress);
        Set(page, "JS", _browser);
        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "ticketNumber", "INC0001");
        return page;
    }

    /// <summary>A finished Message Analysis export job and its file, so the row resolves as Available.</summary>
    private string SeedExportWithItsFile()
    {
        var id = Guid.NewGuid().ToString("N");
        _repository.Insert(new BulkJob
        {
            Id = id,
            ModuleId = MessageTraceDetailJobProcessor.ModuleName,
            JobType = MessageTraceDetailJobPayload.JobType,
            Status = BulkJobStatus.Completed,
            SubmittedBy = "jdoe",
            SubmittedByDisplay = "Jane Doe",
            SubmittedIp = "10.0.0.5",
            Ticket = "INC42",
            PayloadJson = JsonSerializer.Serialize(new MessageTraceDetailJobPayload
            {
                Messages =
                [
                    new MessageTraceResult
                    {
                        Received = Submitted,
                        SenderAddress = "s@contoso.test",
                        RecipientAddress = "r@contoso.test",
                        Subject = "quarterly report",
                        Status = "Delivered",
                        MessageId = "m1",
                        MessageTraceId = "m1-trace",
                        Backend = "ExchangeOnline",
                    },
                ],
                UserEmail = "user@contoso.test",
            }),
            SubmittedAtUtc = Submitted,
            TotalRows = 1,
            FinishedAtUtc = Submitted.AddMinutes(5),
        });
        Directory.CreateDirectory(_store.DirectoryPath);
        File.WriteAllText(_store.PathFor(id, Submitted), "Message 1 of 1\r\n");
        return id;
    }
}
