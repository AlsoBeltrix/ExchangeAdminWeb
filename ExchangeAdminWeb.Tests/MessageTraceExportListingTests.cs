using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Slice-2 coverage for the Downloadable Reports page logic
/// (docs/MessageTraceDownloadLink-Plan.md). The markup is a thin shell over this class because the
/// repo has no bUnit harness, so the load-bearing behavior is asserted here: the module/type filter
/// that keeps another busy module from emptying the page, the Failed-vs-Expired split that stops a
/// write error being mislabelled as retention (openreview F1), and the required ticket that must be
/// enforced before the filesystem is touched (openreview F2).
/// </summary>
public sealed class MessageTraceExportListingTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly string _logRoot;
    private readonly BulkJobRepository _repository;
    private readonly BulkJobService _jobs;
    private readonly MessageTraceExportStore _store;
    private readonly ServiceProvider _provider;

    public MessageTraceExportListingTests()
    {
        _logRoot = Path.Combine(_temp.Path, "logs");
        Directory.CreateDirectory(_logRoot);

        var factory = new SqliteConnectionFactory(Path.Combine(_temp.Path, "exchangeadmin-jobs.db"));
        new JobStoreMigrator(factory).Migrate();
        _repository = new BulkJobRepository(factory);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _logRoot,
            // Deliberately tiny: the page must not inherit this bound (see the unfiltered-limit test).
            ["BulkJobs:RecentJobLimit"] = "2",
        }).Build();

        _provider = new ServiceCollection().BuildServiceProvider();
        _jobs = new BulkJobService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _repository,
            new BulkJobProcessorRegistry(Array.Empty<KeyValuePair<string, Type>>()),
            config,
            NullLogger<BulkJobService>.Instance);
        _store = new MessageTraceExportStore(config);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _temp.Dispose();
    }

    private MessageTraceExportListing CreateListing() =>
        new(_jobs, _store, NullLogger<MessageTraceExportListing>.Instance);

    // Records whether the file read was reached, so a guard that is supposed to refuse BEFORE
    // touching the filesystem can be proven to do so rather than merely to show a message.
    private sealed class ReadSpyListing : MessageTraceExportListing
    {
        public int Reads;

        public ReadSpyListing(BulkJobService jobs, MessageTraceExportStore store)
            : base(jobs, store, NullLogger<MessageTraceExportListing>.Instance) { }

        public override Task<byte[]> ReadFileAsync(string fullPath)
        {
            Interlocked.Increment(ref Reads);
            return base.ReadFileAsync(fullPath);
        }
    }

    private static string NewJobId() => Guid.NewGuid().ToString("N");

    private static string PayloadFor(params MessageTraceResult[] messages) =>
        JsonSerializer.Serialize(new MessageTraceDetailJobPayload
        {
            Messages = messages.ToList(),
            UserEmail = "user@contoso.com"
        });

    private static MessageTraceResult Message(string sender = "s@contoso.com",
        string recipient = "r@contoso.com", string subject = "quarterly report") => new()
        {
            Received = DateTime.UtcNow,
            SenderAddress = sender,
            RecipientAddress = recipient,
            Subject = subject,
            Status = "Delivered",
            MessageId = "m1",
            MessageTraceId = "m1-trace",
            Backend = "ExchangeOnline",
        };

    /// <summary>Inserts a terminal export job and returns its id. Does not create the file.</summary>
    private string SeedExport(
        BulkJobStatus status = BulkJobStatus.Completed,
        string? message = null,
        string? payloadJson = null,
        DateTime? submittedAtUtc = null,
        DateTime? finishedAtUtc = null,
        string moduleId = MessageTraceDetailJobProcessor.ModuleName,
        string jobType = MessageTraceDetailJobPayload.JobType,
        int totalRows = 1)
    {
        var id = NewJobId();
        var submitted = submittedAtUtc ?? new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
        var job = new BulkJob
        {
            Id = id,
            ModuleId = moduleId,
            JobType = jobType,
            Status = status,
            SubmittedBy = "jdoe",
            SubmittedByDisplay = "Jane Doe",
            SubmittedIp = "10.0.0.5",
            Ticket = "INC42",
            PayloadJson = payloadJson ?? PayloadFor(Message()),
            SubmittedAtUtc = submitted,
            TotalRows = totalRows,
            FinishedAtUtc = finishedAtUtc ?? submitted.AddMinutes(5),
            Message = message,
        };
        // Insert persists every column including status, finished_at and message, so a terminal job
        // can be seeded directly without driving it through the runner.
        _repository.Insert(job);
        return id;
    }

    /// <summary>Writes the export file for a seeded job, so it resolves as Available.</summary>
    private string WriteExportFile(string jobId, DateTime? submittedAtUtc = null)
    {
        var submitted = submittedAtUtc ?? new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(_store.DirectoryPath);
        var path = _store.PathFor(jobId, submitted);
        File.WriteAllText(path, "Message 1 of 1\r\n");
        return path;
    }

    // -------------------------------------------------------------------------
    // Enumeration: module + type filtering, and its own limit
    // -------------------------------------------------------------------------

    [Fact]
    public void GetExports_ReturnsOnlyMessageTraceDetailExports()
    {
        var mine = SeedExport();
        SeedExport(moduleId: "ConferenceRooms", jobType: "SetMetadata_Bulk");
        SeedExport(jobType: "MessageTrace_Historical");

        var items = CreateListing().GetExports();

        Assert.Equal([mine], items.Select(i => i.JobId));
    }

    [Fact]
    public void GetExports_IsNotBoundedByRecentJobLimit()
    {
        // BulkJobs:RecentJobLimit is 2 in this fixture. Reusing GetRecentFinished would return 2 rows
        // (and, being unfiltered, could return two OTHER modules' jobs and none of these).
        var expected = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            expected.Add(SeedExport(
                submittedAtUtc: new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                finishedAtUtc: new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc).AddMinutes(i)));
        }

        var items = CreateListing().GetExports();

        Assert.Equal(6, items.Count);
        Assert.Equal(expected.OrderBy(x => x), items.Select(i => i.JobId).OrderBy(x => x));
    }

    [Fact]
    public void GetExports_NewestFinishedFirst()
    {
        var older = SeedExport(finishedAtUtc: new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc));
        var newer = SeedExport(finishedAtUtc: new DateTime(2026, 7, 2, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal([newer, older], CreateListing().GetExports().Select(i => i.JobId));
    }

    // -------------------------------------------------------------------------
    // State classification: Failed must never be reported as Expired (openreview F1)
    // -------------------------------------------------------------------------

    [Fact]
    public void SavedFileOnDisk_IsAvailable_AndCarriesItsPath()
    {
        var id = SeedExport();
        var path = WriteExportFile(id);

        var item = Assert.Single(CreateListing().GetExports());

        Assert.Equal(MessageTraceExportState.Available, item.State);
        Assert.True(item.CanDownload);
        Assert.Equal(path, item.FullPath);
    }

    [Fact]
    public void SaveSucceededButFileGone_IsExpired_NotFailed()
    {
        SeedExport(); // no file written: the host retention task removed it

        var item = Assert.Single(CreateListing().GetExports());

        Assert.Equal(MessageTraceExportState.Expired, item.State);
        Assert.False(item.CanDownload);
        Assert.Null(item.FullPath);
    }

    [Fact]
    public void SaveFailed_IsFailed_NotExpired_EvenThoughBothHaveNoFile()
    {
        // The F1 defect is collapsing these two into one "file missing" state: a disk-full or
        // permissions fault would then read as ordinary 30-day retention and the operator would
        // conclude they simply waited too long.
        var expiredId = SeedExport();
        var failedId = SeedExport(message: $"1 message(s); {MessageTraceExportListing.SaveFailedMarker}");

        var items = CreateListing().GetExports().ToDictionary(i => i.JobId);

        Assert.Equal(MessageTraceExportState.Expired, items[expiredId].State);
        Assert.Equal(MessageTraceExportState.Failed, items[failedId].State);
        Assert.NotEqual(items[expiredId].State, items[failedId].State);
        Assert.False(items[failedId].CanDownload);
    }

    [Fact]
    public void FailedAndExpired_ReadDifferentlyToTheOperator()
    {
        // Distinct enum values are not enough: the page shows text, and identical text would leave
        // the operator with the same wrong conclusion the enum split exists to prevent.
        Assert.NotEqual(
            MessageTraceExportListing.Describe(MessageTraceExportState.Failed),
            MessageTraceExportListing.Describe(MessageTraceExportState.Expired));
        Assert.NotEqual(
            MessageTraceExportListing.ShortStatus(MessageTraceExportState.Failed),
            MessageTraceExportListing.ShortStatus(MessageTraceExportState.Expired));
    }

    [Theory]
    [InlineData(BulkJobStatus.Cancelled)]
    [InlineData(BulkJobStatus.Interrupted)]
    public void JobThatNeverCompleted_IsNotProduced(BulkJobStatus status)
    {
        SeedExport(status: status);

        var item = Assert.Single(CreateListing().GetExports());

        Assert.Equal(MessageTraceExportState.NotProduced, item.State);
        Assert.False(item.CanDownload);
    }

    [Fact]
    public void ExpiresAtUtc_ComesFromTheStoresRetentionWindow()
    {
        var submitted = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
        SeedExport(submittedAtUtc: submitted);

        var item = Assert.Single(CreateListing().GetExports());

        Assert.Equal(submitted.AddDays(MessageTraceExportStore.RetentionDays), item.ExpiresAtUtc);
    }

    // -------------------------------------------------------------------------
    // Row metadata
    // -------------------------------------------------------------------------

    [Fact]
    public void MalformedPayload_RendersUnavailable_DoesNotThrowOrDropTheRow()
    {
        var id = SeedExport(payloadJson: "{ this is not json");

        var item = Assert.Single(CreateListing().GetExports());

        Assert.Equal(id, item.JobId);
        Assert.Equal("(unavailable)", item.Descriptor);
    }

    [Fact]
    public void Descriptor_NamesSenderRecipientAndSubject_AndCountsTheRest()
    {
        SeedExport(payloadJson: PayloadFor(Message()), totalRows: 3);

        var item = Assert.Single(CreateListing().GetExports());

        Assert.Contains("s@contoso.com", item.Descriptor);
        Assert.Contains("r@contoso.com", item.Descriptor);
        Assert.Contains("quarterly report", item.Descriptor);
        Assert.Contains("+2 more", item.Descriptor);
    }

    [Fact]
    public void Descriptor_LongValuesAreTruncatedWithAsciiEllipsis()
    {
        var longSubject = new string('x', 200);
        SeedExport(payloadJson: PayloadFor(Message(subject: longSubject)));

        var item = Assert.Single(CreateListing().GetExports());

        Assert.DoesNotContain(longSubject, item.Descriptor);
        Assert.Contains("...", item.Descriptor);
    }

    [Fact]
    public void SubmittedBy_PrefersDisplayName()
    {
        SeedExport();

        Assert.Equal("Jane Doe", Assert.Single(CreateListing().GetExports()).SubmittedBy);
    }

    // -------------------------------------------------------------------------
    // Resolve: a mismatched id must not reach another module's job
    // -------------------------------------------------------------------------

    [Fact]
    public void Resolve_OtherModulesJob_ReturnsNull()
    {
        var foreignId = SeedExport(moduleId: "ConferenceRooms", jobType: "SetMetadata_Bulk");

        Assert.Null(CreateListing().Resolve(foreignId));
    }

    [Fact]
    public void Resolve_UnknownId_ReturnsNull()
    {
        Assert.Null(CreateListing().Resolve(NewJobId()));
    }

    // -------------------------------------------------------------------------
    // Download: ticket presence (openreview F2) and click-time re-resolution
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankTicket_DoesNotDownload_AndNeverReadsTheFile(string? ticket)
    {
        var id = SeedExport();
        WriteExportFile(id);
        var listing = new ReadSpyListing(_jobs, _store);

        var result = await listing.TryDownloadAsync(id, ticket);

        Assert.False(result.Succeeded);
        Assert.Equal(MessageTraceExportListing.TicketRequiredMessage, result.Error);
        // Assert the filesystem was never touched, not merely that a message came back: a guard that
        // reads first and refuses afterwards would satisfy a message-only assertion.
        Assert.Equal(0, listing.Reads);
    }

    [Fact]
    public async Task TicketProvided_DownloadsTheFileBytesAsWritten()
    {
        var id = SeedExport();
        var path = WriteExportFile(id);
        var expected = File.ReadAllBytes(path);

        var result = await CreateListing().TryDownloadAsync(id, "INC42");

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.Bytes);
        Assert.Equal(Path.GetFileName(path), result.FileName);
    }

    [Fact]
    public async Task FileRemovedAfterRender_DownloadReportsExpired_DoesNotThrow()
    {
        var id = SeedExport();
        var path = WriteExportFile(id);
        var rendered = Assert.Single(CreateListing().GetExports());
        Assert.True(rendered.CanDownload);

        File.Delete(path); // the host retention task runs between render and click

        var result = await CreateListing().TryDownloadAsync(id, "INC42");

        Assert.False(result.Succeeded);
        Assert.Equal(MessageTraceExportState.Expired, result.Item!.State);
    }

    [Fact]
    public async Task FailedExport_DoesNotDownload_AndSaysSoRatherThanBlamingRetention()
    {
        var id = SeedExport(message: $"1 message(s); {MessageTraceExportListing.SaveFailedMarker}");
        var listing = new ReadSpyListing(_jobs, _store);

        var result = await listing.TryDownloadAsync(id, "INC42");

        Assert.False(result.Succeeded);
        Assert.Equal(MessageTraceExportState.Failed, result.Item!.State);
        Assert.Equal(MessageTraceExportListing.Describe(MessageTraceExportState.Failed), result.Error);
        Assert.Equal(0, listing.Reads);
    }

    [Fact]
    public async Task DownloadOfAnotherModulesJob_IsRefused()
    {
        var foreignId = SeedExport(moduleId: "ConferenceRooms", jobType: "SetMetadata_Bulk");
        var listing = new ReadSpyListing(_jobs, _store);

        var result = await listing.TryDownloadAsync(foreignId, "INC42");

        Assert.False(result.Succeeded);
        Assert.Equal(0, listing.Reads);
    }
}
