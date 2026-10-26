using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Slice-2 coverage for the Downloadable Reports page logic
/// (docs/MessageTraceDownloadLink-Plan.md). The markup is a thin shell over this class because the
/// repo has no bUnit harness, so the load-bearing behavior is asserted here: the module/type filter
/// that keeps another busy module from emptying the page, the Failed-vs-Expired split that stops a
/// write error being mislabelled as retention (openreview F1), the required ticket that must be
/// enforced before the filesystem is touched (openreview F2), and the ownership scope that shows and
/// serves an operator only their own exports unless they are an application administrator (kd-1).
/// </summary>
public sealed class MessageTraceExportListingTests : IDisposable
{
    /// <summary>The SubmittedBy every seeded export carries unless a test says otherwise.</summary>
    private const string Submitter = "jdoe";

    private readonly TempDir _temp = new();
    private readonly string _logRoot;
    private readonly IConfiguration _config;
    private readonly BulkJobRepository _repository;
    private readonly BulkJobService _jobs;
    private readonly MessageTraceExportStore _store;
    private readonly ServiceProvider _provider;
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();

    /// <summary>Names the stand-in authorization grants <see cref="MessageTraceExportListing.EveryExportPolicy"/>.</summary>
    private readonly HashSet<string> _administrators = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, every authorization check throws it.</summary>
    private Exception? _authorizationFailure;

    public MessageTraceExportListingTests()
    {
        _logRoot = Path.Combine(_temp.Path, "logs");
        Directory.CreateDirectory(_logRoot);

        var factory = new SqliteConnectionFactory(Path.Combine(_temp.Path, "exchangeadmin-jobs.db"));
        new JobStoreMigrator(factory).Migrate();
        _repository = new BulkJobRepository(factory);

        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
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
            _config,
            NullLogger<BulkJobService>.Instance);
        _store = new MessageTraceExportStore(_config);

        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(call => _authorizationFailure is { } failure
                ? Task.FromException<AuthorizationResult>(failure)
                : Task.FromResult(
                    call.ArgAt<string>(2) == MessageTraceExportListing.EveryExportPolicy
                    && _administrators.Contains(call.ArgAt<ClaimsPrincipal>(0).Identity?.Name ?? "")
                        ? AuthorizationResult.Success()
                        : AuthorizationResult.Failed()));
    }

    public void Dispose()
    {
        _provider.Dispose();
        _temp.Dispose();
    }

    private MessageTraceExportListing CreateListing() =>
        new(_jobs, _store, _authorization, NullLogger<MessageTraceExportListing>.Instance);

    /// <summary>An authenticated operator, as Negotiate signs one in.</summary>
    private static ClaimsPrincipal Operator(string name) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "Negotiate"));

    private static readonly ClaimsPrincipal TheSubmitter = Operator(Submitter);

    // Records whether the file read was reached, so a guard that is supposed to refuse BEFORE
    // touching the filesystem can be proven to do so rather than merely to show a message.
    private sealed class ReadSpyListing : MessageTraceExportListing
    {
        public int Reads;

        public ReadSpyListing(BulkJobService jobs, MessageTraceExportStore store, IAuthorizationService authorization)
            : base(jobs, store, authorization, NullLogger<MessageTraceExportListing>.Instance) { }

        public override Task<byte[]> ReadFileAsync(string fullPath)
        {
            Interlocked.Increment(ref Reads);
            return base.ReadFileAsync(fullPath);
        }
    }

    private ReadSpyListing CreateReadSpy() => new(_jobs, _store, _authorization);

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
        int totalRows = 1,
        string submittedBy = Submitter)
    {
        var id = NewJobId();
        var submitted = submittedAtUtc ?? new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
        var job = new BulkJob
        {
            Id = id,
            ModuleId = moduleId,
            JobType = jobType,
            Status = status,
            SubmittedBy = submittedBy,
            SubmittedByDisplay = submittedBy == Submitter ? "Jane Doe" : null,
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

    private Task<IReadOnlyList<MessageTraceExportListItem>> ListFor(ClaimsPrincipal viewer) =>
        CreateListing().GetExportsAsync(viewer);

    private Task<IReadOnlyList<MessageTraceExportListItem>> OwnList() => ListFor(TheSubmitter);

    // -------------------------------------------------------------------------
    // Enumeration: module + type filtering, and its own limit
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetExports_ReturnsOnlyMessageTraceDetailExports()
    {
        var mine = SeedExport();
        SeedExport(moduleId: "ConferenceRooms", jobType: "SetMetadata_Bulk");
        SeedExport(jobType: "MessageTrace_Historical");

        var items = await OwnList();

        Assert.Equal([mine], items.Select(i => i.JobId));
    }

    [Fact]
    public async Task GetExports_IsNotBoundedByRecentJobLimit()
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

        var items = await OwnList();

        Assert.Equal(6, items.Count);
        Assert.Equal(expected.OrderBy(x => x), items.Select(i => i.JobId).OrderBy(x => x));
    }

    [Fact]
    public async Task GetExports_NewestFinishedFirst()
    {
        var older = SeedExport(finishedAtUtc: new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc));
        var newer = SeedExport(finishedAtUtc: new DateTime(2026, 7, 2, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal([newer, older], (await OwnList()).Select(i => i.JobId));
    }

    // -------------------------------------------------------------------------
    // State classification: Failed must never be reported as Expired (openreview F1)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SavedFileOnDisk_IsAvailable_AndCarriesItsPath()
    {
        var id = SeedExport();
        var path = WriteExportFile(id);

        var item = Assert.Single(await OwnList());

        Assert.Equal(MessageTraceExportState.Available, item.State);
        Assert.True(item.CanDownload);
        Assert.Equal(path, item.FullPath);
    }

    [Fact]
    public async Task SaveSucceededButFileGone_IsExpired_NotFailed()
    {
        SeedExport(); // no file written: the host retention task removed it

        var item = Assert.Single(await OwnList());

        Assert.Equal(MessageTraceExportState.Expired, item.State);
        Assert.False(item.CanDownload);
        Assert.Null(item.FullPath);
    }

    [Fact]
    public async Task SaveFailed_IsFailed_NotExpired_EvenThoughBothHaveNoFile()
    {
        // The F1 defect is collapsing these two into one "file missing" state: a disk-full or
        // permissions fault would then read as ordinary 30-day retention and the operator would
        // conclude they simply waited too long.
        var expiredId = SeedExport();
        var failedId = SeedExport(message: $"1 message(s); {MessageTraceExportListing.SaveFailedMarker}");

        var items = (await OwnList()).ToDictionary(i => i.JobId);

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
    public async Task AnyTerminalJobsExport_IsServed_NotDeniedAsNeverProduced(BulkJobStatus status)
    {
        // Review of A32 corrected the first fix: all three sources of Interrupted DO run the
        // completion hook, including the startup orphan sweep (BulkJobService.cs:95), so an
        // interrupted job produces and emails an export exactly as a cancelled one does.
        var id = SeedExport(status: status);
        WriteExportFile(id);

        var item = Assert.Single(await OwnList());

        Assert.Equal(MessageTraceExportState.Available, item.State);
        Assert.True(item.CanDownload);
    }

    [Fact]
    public async Task ExpiresAtUtc_ComesFromTheStoresRetentionWindow()
    {
        var submitted = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
        SeedExport(submittedAtUtc: submitted);

        var item = Assert.Single(await OwnList());

        Assert.Equal(submitted.AddDays(MessageTraceExportStore.RetentionDays), item.ExpiresAtUtc);
    }

    // -------------------------------------------------------------------------
    // Row metadata
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MalformedPayload_RendersUnavailable_DoesNotThrowOrDropTheRow()
    {
        var id = SeedExport(payloadJson: "{ this is not json");

        var item = Assert.Single(await OwnList());

        Assert.Equal(id, item.JobId);
        Assert.Equal("(unavailable)", item.Descriptor);
    }

    [Fact]
    public async Task Descriptor_NamesSenderRecipientAndSubject_AndCountsTheRest()
    {
        SeedExport(payloadJson: PayloadFor(Message()), totalRows: 3);

        var item = Assert.Single(await OwnList());

        Assert.Contains("s@contoso.com", item.Descriptor);
        Assert.Contains("r@contoso.com", item.Descriptor);
        Assert.Contains("quarterly report", item.Descriptor);
        Assert.Contains("+2 more", item.Descriptor);
    }

    [Fact]
    public async Task Descriptor_LongValuesAreTruncatedWithAsciiEllipsis()
    {
        var longSubject = new string('x', 200);
        SeedExport(payloadJson: PayloadFor(Message(subject: longSubject)));

        var item = Assert.Single(await OwnList());

        Assert.DoesNotContain(longSubject, item.Descriptor);
        Assert.Contains("...", item.Descriptor);
    }

    [Fact]
    public async Task SubmittedBy_PrefersDisplayName()
    {
        SeedExport();

        Assert.Equal("Jane Doe", Assert.Single(await OwnList()).SubmittedBy);
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
    [InlineData("\u0001")] // kd-45: control only - IsNullOrWhiteSpace let this through
    [InlineData("\u200B")] // kd-45: zero-width only
    public async Task BlankTicket_DoesNotDownload_AndNeverReadsTheFile(string? ticket)
    {
        var id = SeedExport();
        WriteExportFile(id);
        var listing = CreateReadSpy();

        var result = await listing.TryDownloadAsync(id, ticket, TheSubmitter);

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

        var result = await CreateListing().TryDownloadAsync(id, "INC42", TheSubmitter);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.Bytes);
        Assert.Equal(Path.GetFileName(path), result.FileName);
        // The submitter's own download is the ordinary row: nothing to add to it.
        Assert.Null(result.AuditExtra);
    }

    [Fact]
    public async Task FileRemovedAfterRender_DownloadReportsExpired_DoesNotThrow()
    {
        var id = SeedExport();
        var path = WriteExportFile(id);
        var rendered = Assert.Single(await OwnList());
        Assert.True(rendered.CanDownload);

        File.Delete(path); // the host retention task runs between render and click

        var result = await CreateListing().TryDownloadAsync(id, "INC42", TheSubmitter);

        Assert.False(result.Succeeded);
        Assert.Equal(MessageTraceExportState.Expired, result.Item!.State);
    }

    [Fact]
    public async Task FailedExport_DoesNotDownload_AndSaysSoRatherThanBlamingRetention()
    {
        var id = SeedExport(message: $"1 message(s); {MessageTraceExportListing.SaveFailedMarker}");
        var listing = CreateReadSpy();

        var result = await listing.TryDownloadAsync(id, "INC42", TheSubmitter);

        Assert.False(result.Succeeded);
        Assert.Equal(MessageTraceExportState.Failed, result.Item!.State);
        Assert.Equal(MessageTraceExportListing.Describe(MessageTraceExportState.Failed), result.Error);
        Assert.Equal(0, listing.Reads);
    }

    [Fact]
    public async Task DownloadOfAnotherModulesJob_IsRefused()
    {
        var foreignId = SeedExport(moduleId: "ConferenceRooms", jobType: "SetMetadata_Bulk");
        var listing = CreateReadSpy();

        var result = await listing.TryDownloadAsync(foreignId, "INC42", TheSubmitter);

        Assert.False(result.Succeeded);
        Assert.Equal(0, listing.Reads);
    }

    // -------------------------------------------------------------------------
    // kd-1: an operator sees and downloads only the exports they submitted, unless they hold the
    // application administrators' AdminSettings policy
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetExports_ShowsAnOperatorOnlyTheExportsTheySubmitted()
    {
        var mine = SeedExport();
        SeedExport(submittedBy: "asmith");

        Assert.Equal([mine], (await OwnList()).Select(i => i.JobId));
        Assert.Empty(await ListFor(Operator("bwhite")));
    }

    [Fact]
    public async Task GetExports_ComparesTheSubmitterIgnoringCase_AsTheJobsStoreDoes()
    {
        var mine = SeedExport(submittedBy: "CONTOSO\\JDoe");

        Assert.Equal([mine], (await ListFor(Operator("contoso\\jdoe"))).Select(i => i.JobId));
    }

    [Fact]
    public async Task GetExports_AnApplicationAdministratorSeesEveryOperatorsExports()
    {
        var first = SeedExport();
        var second = SeedExport(submittedBy: "asmith");
        _administrators.Add("admin1");

        var items = await ListFor(Operator("admin1"));

        Assert.Equal(new[] { first, second }.OrderBy(x => x), items.Select(i => i.JobId).OrderBy(x => x));
    }

    [Fact]
    public async Task GetExports_AnAdministratorCheckThatThrows_WidensNothing()
    {
        var mine = SeedExport();
        SeedExport(submittedBy: "asmith");
        _administrators.Add(Submitter); // would widen the list, if the check could answer
        _authorizationFailure = new InvalidOperationException("section access could not be read");

        Assert.Equal([mine], (await OwnList()).Select(i => i.JobId));
    }

    [Fact]
    public async Task GetExports_AViewerWithNoAuthenticatedName_OwnsNothing()
    {
        // "Unknown" is what MessageTrace.razor records as the submitter when the operator had no
        // name; a viewer with none must not match it, or anything else.
        SeedExport();
        SeedExport(submittedBy: "Unknown");

        Assert.Empty(await ListFor(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Empty(await ListFor(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Unknown")]))));
    }

    [Fact]
    public async Task GetExports_OtherOperatorsExportsCannotCrowdOutTheViewersOwn()
    {
        // The window is the limit's, so the owner filter must apply before it. Filtering the newest
        // 200 of everyone's after the fact would leave this operator's older export off the page.
        var mine = SeedExport(finishedAtUtc: new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc));
        for (var i = 0; i < MessageTraceExportListing.ListLimit; i++)
            SeedExport(submittedBy: "asmith", finishedAtUtc: new DateTime(2026, 7, 2, 10, 0, 0, DateTimeKind.Utc).AddMinutes(i));

        Assert.Equal([mine], (await OwnList()).Select(i => i.JobId));
    }

    [Fact]
    public async Task GetExports_ReadsTheViewersOwnExportsPastOnePageOfTheOwnershipScope()
    {
        // The ownership scope returns at most 100 rows a call; the list's limit is 200.
        for (var i = 0; i < 150; i++)
            SeedExport(finishedAtUtc: new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc).AddMinutes(i));

        Assert.Equal(150, (await OwnList()).Count);
    }

    [Fact]
    public async Task Download_OfAnotherOperatorsExport_IsRefusedBeforeTheFileIsRead_AndSaysNothingWasAttempted()
    {
        var id = SeedExport(submittedBy: "asmith");
        WriteExportFile(id);
        var listing = CreateReadSpy();

        var result = await listing.TryDownloadAsync(id, "INC42", TheSubmitter);

        Assert.False(result.Succeeded);
        Assert.Equal(MessageTraceExportListing.NotYourExportMessage, result.Error);
        Assert.Null(result.Bytes);
        Assert.Null(result.Item); // not even the row's metadata
        Assert.Equal(0, listing.Reads);
        var extra = Assert.IsType<Dictionary<string, object?>>(result.AuditExtra);
        Assert.Equal(MessageTraceExportListing.RefusedAtOwnership, extra["refusedAt"]);
        Assert.Equal(false, extra["mutationAttempted"]);
        Assert.Equal("asmith", extra["exportSubmittedBy"]);
    }

    [Fact]
    public async Task Download_ByAnAdministrator_OfAnotherOperatorsExport_Succeeds_AndSaysWhoseItWas()
    {
        var id = SeedExport(submittedBy: "asmith");
        WriteExportFile(id);
        _administrators.Add("admin1");

        var result = await CreateListing().TryDownloadAsync(id, "INC42", Operator("admin1"));

        Assert.True(result.Succeeded);
        Assert.Equal("asmith", result.AuditExtra!["exportSubmittedBy"]);
        Assert.Equal(MessageTraceExportListing.EveryExportPolicy, result.AuditExtra["grantedBy"]);
    }

    [Fact]
    public async Task Download_WhenTheAdministratorCheckThrows_ANonSubmitterIsRefused()
    {
        var id = SeedExport(submittedBy: "asmith");
        WriteExportFile(id);
        _administrators.Add(Submitter);
        _authorizationFailure = new InvalidOperationException("section access could not be read");
        var listing = CreateReadSpy();

        var result = await listing.TryDownloadAsync(id, "INC42", TheSubmitter);

        Assert.Equal(MessageTraceExportListing.NotYourExportMessage, result.Error);
        Assert.Equal(0, listing.Reads);
    }

    [Fact]
    public async Task Download_ByTheSubmitter_DoesNotDependOnTheAdministratorCheck()
    {
        var id = SeedExport();
        WriteExportFile(id);
        _authorizationFailure = new InvalidOperationException("section access could not be read");

        var result = await CreateListing().TryDownloadAsync(id, "INC42", TheSubmitter);

        Assert.True(result.Succeeded);
    }

    // -------------------------------------------------------------------------
    // kd-1, the page: the compiled MessageTraceReports.Download handler, driven by reflection with its
    // injected services set (the Comms10k and AttributeEditor page-handler pattern). One audit row per
    // attempt, and a refusal's row says so.
    // -------------------------------------------------------------------------

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private (MessageTraceReports Page, IJSRuntime Js, ActivityProgressService Progress) ReportsPage(ClaimsPrincipal viewer)
    {
        var page = new MessageTraceReports();
        var log = new JsonlLogService(_config, NullLogger<JsonlLogService>.Instance);
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(viewer));
        var js = Substitute.For<IJSRuntime>();
        var progress = new ActivityProgressService();

        SetProperty(page, "Exports", CreateListing());
        SetProperty(page, "Audit", new AuditService(log, new OperationTraceService(_config, log)));
        SetProperty(page, "AuthStateProvider", authState);
        SetProperty(page, "AuthorizationService", _authorization);
        SetProperty(page, "JS", js);
        SetProperty(page, "Progress", progress);
        SetField(page, "currentUser", viewer.Identity?.Name ?? "Unknown");
        SetField(page, "clientIpAddress", "10.0.0.9");
        SetField(page, "ticketNumber", "INC42");
        return (page, js, progress);
    }

    private static void SetProperty(MessageTraceReports page, string name, object value) =>
        typeof(MessageTraceReports).GetProperty(name, PrivateInstance)!.SetValue(page, value);

    private static void SetField(MessageTraceReports page, string name, object? value) =>
        typeof(MessageTraceReports).GetField(name, PrivateInstance)!.SetValue(page, value);

    private static T Field<T>(MessageTraceReports page, string name) =>
        (T)typeof(MessageTraceReports).GetField(name, PrivateInstance)!.GetValue(page)!;

    private static Task Download(MessageTraceReports page, string jobId) =>
        (Task)typeof(MessageTraceReports).GetMethod("Download", PrivateInstance)!.Invoke(page, [jobId])!;

    private List<JsonElement> AuditRows()
    {
        var folder = Path.Combine(_logRoot, "ExchangeAdminWeb");
        if (!Directory.Exists(folder))
            return [];

        return Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(file => !Path.GetFileNameWithoutExtension(file).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(row => row.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
            .ToList();
    }

    private static bool SentTheFile(IJSRuntime js) =>
        js.ReceivedCalls().Any(call => call.GetArguments().FirstOrDefault() as string == "downloadFile");

    [Fact]
    public async Task Page_ARefusedDownloadOfAnotherOperatorsExport_IsOneAuditedDenial_AndSendsNothing()
    {
        var id = SeedExport(submittedBy: "asmith");
        WriteExportFile(id);
        var (page, js, progress) = ReportsPage(TheSubmitter);
        using var activities = progress;

        await Download(page, id);

        var row = Assert.Single(AuditRows());
        Assert.Equal("MessageTrace_ExportDownload", row.GetProperty("action").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal($"job {id}", row.GetProperty("target").GetString());
        Assert.Equal("INC42", row.GetProperty("ticket").GetString());
        Assert.Equal(MessageTraceExportListing.NotYourExportMessage, row.GetProperty("error").GetString());
        Assert.Equal(MessageTraceExportListing.RefusedAtOwnership, row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal("asmith", row.GetProperty("exportSubmittedBy").GetString());
        Assert.False(SentTheFile(js));
        Assert.Equal(MessageTraceExportListing.NotYourExportMessage, Field<string?>(page, "error"));
    }

    [Fact]
    public async Task Page_TheSubmittersDownload_SendsTheFile_AndItsRowIsAnOrdinarySuccess()
    {
        var id = SeedExport();
        WriteExportFile(id);
        var (page, js, progress) = ReportsPage(TheSubmitter);
        using var activities = progress;

        await Download(page, id);

        var row = Assert.Single(AuditRows());
        Assert.Equal("Success", row.GetProperty("result").GetString());
        Assert.False(row.TryGetProperty("refusedAt", out _));
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
        Assert.True(SentTheFile(js));
    }

    [Fact]
    public async Task Page_TheListShowsTheViewersOwnExports()
    {
        var mine = SeedExport();
        SeedExport(submittedBy: "asmith");
        var (page, _, progress) = ReportsPage(TheSubmitter);
        using var activities = progress;

        await (Task)typeof(MessageTraceReports).GetMethod("Refresh", PrivateInstance)!.Invoke(page, null)!;

        Assert.Equal([mine], Field<IReadOnlyList<MessageTraceExportListItem>>(page, "items").Select(i => i.JobId));
    }
}
