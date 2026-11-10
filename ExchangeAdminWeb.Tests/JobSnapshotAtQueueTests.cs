using System.Reflection;
using System.Security.Claims;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using MessageTracePage = ExchangeAdminWeb.Components.Pages.MessageTrace;
using MigrationPage = ExchangeAdminWeb.Components.Pages.Migration;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The Message Analysis detail export and the Migration report export save a snapshot of the
/// operator's access on the job they queue, and it is one the runner's check accepts: each page's
/// own handler is run, and the job it queued is read back from a real job store.
/// </summary>
public sealed class JobSnapshotAtQueueTests : IDisposable
{
    private const string OtherSid = "S-1-5-21-100-200-300-4002";

    private readonly TempDir _temp = new();
    private readonly BulkJobRepository _repository;
    private readonly BulkJobService _queue;
    private readonly AuditService _audit;

    public JobSnapshotAtQueueTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Audit:LogRoot"] = _temp.Path }).Build();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        _audit = new AuditService(log, new OperationTraceService(config, log));
        var factory = new SqliteConnectionFactory(Path.Combine(_temp.Path, "exchangeadmin-jobs.db"));
        new JobStoreMigrator(factory).Migrate();
        _repository = new BulkJobRepository(factory);
        _queue = new BulkJobService(null!, _repository, new BulkJobProcessorRegistry([]), new StubJobAuthorization(), config,
            NullLogger<BulkJobService>.Instance);
        // The job is only queued: no runner is started for it.
        typeof(BulkJobService).GetField("_pumpRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_queue, true);
    }

    public void Dispose() => _temp.Dispose();

    private static ClaimsPrincipal Operator(string groupSid) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "EXAMPLE\\jdoe"), new Claim(ClaimTypes.GroupSid, groupSid)], "test"));

    private BulkJob QueueMessageTraceExport(JobAuthorizationStores stores, ClaimsPrincipal? principal)
    {
        var page = new MessageTracePage();
        Set(page, "BulkJobs", _queue);
        Set(page, "SectionAccess", stores.Sections);
        Set(page, "Audit", _audit);
        Set(page, "Progress", new ActivityProgressService());
        SetField(page, "operatorPrincipal", principal);
        SetField(page, "currentUser", "EXAMPLE\\jdoe");
        SetField(page, "ticketNumber", "INC1");
        SetField(page, "recipientInput", "jdoe@contoso.test");
        SetField(page, "recipientTouched", true);
        SetField(page, "response", new MessageTraceResponse
        {
            Results =
            [
                new MessageTraceResult
                {
                    Received = DateTime.UtcNow,
                    SenderAddress = "s@contoso.test",
                    RecipientAddress = "r@contoso.test",
                    Subject = "hi",
                    Status = "Delivered",
                    MessageId = "m1",
                    MessageTraceId = "m1-trace",
                    Backend = "ExchangeOnline",
                },
            ],
        });
        Field<HashSet<int>>(page, "selectedIndices")!.Add(0);

        Assert.True(Call(page, "EmailSelectedDetails").IsCompletedSuccessfully);

        return _repository.GetActive().SingleOrDefault()!;
    }

    private BulkJob QueueMigrationReportExport(JobAuthorizationStores stores, ClaimsPrincipal? principal)
    {
        var page = new MigrationPage();
        Set(page, "BulkJobs", _queue);
        Set(page, "SectionAccess", stores.Sections);
        Set(page, "Audit", _audit);
        Set(page, "Logger", NullLogger<MigrationPage>.Instance);
        SetField(page, "operatorPrincipal", principal);
        SetField(page, "currentUser", "EXAMPLE\\jdoe");
        SetField(page, "expandedBatch", "Wave1-Finance");
        SetField(page, "batchUsers", new List<MigrationUserInfo> { new() { EmailAddress = "a@x.com", Status = "Synced" } });
        Field<HashSet<string>>(page, "selectedMailboxes")!.Add("a@x.com");

        typeof(MigrationPage).GetMethod("StartReportExport", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);

        return _repository.GetActive().SingleOrDefault()!;
    }

    private BulkJob Queue(string module, JobAuthorizationStores stores, ClaimsPrincipal? principal) =>
        module == "MessageTrace" ? QueueMessageTraceExport(stores, principal) : QueueMigrationReportExport(stores, principal);

    [Theory]
    [InlineData("MessageTrace", "MessageTrace")]
    [InlineData("Migration", "MigrationCheck")]
    public void TheJobAnAuthorizedOperatorQueues_CarriesASnapshotTheRunnerAccepts(string module, string section)
    {
        using var stores = new JobAuthorizationStores(module, section);

        var job = Queue(module, stores, Operator(JobAuthorizationStores.GroupSid));

        var snapshot = Assert.IsType<JobAuthorizationSnapshot>(JobAuthorizationSnapshot.FromJson(job.AuthSnapshotJson));
        Assert.Equal(module, job.ModuleId);
        Assert.Equal(section, snapshot.Section);
        Assert.Equal([JobAuthorizationStores.GroupSid], snapshot.AuthorizedGroups);
        Assert.Null(stores.Authorization.Check(job));
    }

    [Theory]
    [InlineData("MessageTrace", "MessageTrace")]
    [InlineData("Migration", "MigrationCheck")]
    public void TheJobOfAnOperatorNoAllowedGroupAuthorizes_CarriesASnapshotTheRunnerRefuses(string module, string section)
    {
        using var stores = new JobAuthorizationStores(module, section);

        var job = Queue(module, stores, Operator(OtherSid));

        var snapshot = Assert.IsType<JobAuthorizationSnapshot>(JobAuthorizationSnapshot.FromJson(job.AuthSnapshotJson));
        Assert.Empty(snapshot.AuthorizedGroups);
        Assert.Equal(JobRefusal.AccessRemoved, stores.Authorization.Check(job));
    }

    [Theory]
    [InlineData("MessageTrace", "MessageTrace")]
    [InlineData("Migration", "MigrationCheck")]
    public void APageThatHoldsNoAuthorizedPrincipal_QueuesNothing(string module, string section)
    {
        using var stores = new JobAuthorizationStores(module, section);

        Assert.Null(Queue(module, stores, null));
    }
}
