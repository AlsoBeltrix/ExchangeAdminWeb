using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

public sealed class Comms10kOwnedJobTests
{
    private const string Module = Comms10kReplaceProcessor.ModuleName;
    private const string Type = Comms10kReplaceJobPayload.JobType;
    private const string Owner = "EXAMPLE\\Op\u00e9rateur";

    [Theory]
    [InlineData("module")]
    [InlineData("type")]
    [InlineData("owner")]
    [InlineData("domain")]
    [InlineData("stripped")]
    [InlineData("display")]
    [InlineData("blank")]
    [InlineData("spaces")]
    public void C5EveryPersistedScopeComponentGuardsEveryReadAndControl(string mismatch)
    {
        using var h = new OwnedHarness();
        var storedOwner = mismatch switch
        {
            "owner" => "EXAMPLE\\other",
            "domain" => "OTHER\\Op\u00e9rateur",
            "stripped" => "Op\u00e9rateur",
            "display" => "EXAMPLE\\other",
            "blank" => "",
            "spaces" => "  ",
            _ => Owner,
        };
        var requestedOwner = mismatch is "blank" or "spaces" ? storedOwner : Owner;
        var module = mismatch == "module" ? "ConferenceRooms" : Module;
        var type = mismatch == "type" ? "AnotherCommsJob" : Type;
        foreach (var terminal in new[] { false, true })
        {
            var id = terminal ? "finished" : "active";
            h.Repository.Insert(OwnedHarness.Job(id, terminal ? BulkJobStatus.Completed : BulkJobStatus.Queued, storedOwner, module, type));
            h.Row(id);
            Assert.Null(h.Repository.GetOwned(Module, Type, requestedOwner, id));
            Assert.Null(h.Service.GetOwnedJob(Module, Type, requestedOwner, id));
            Assert.Empty(h.Service.GetOwnedRows(Module, Type, requestedOwner, id));
            Assert.False(h.Service.CancelOwnedJob(Module, Type, requestedOwner, id, out var cancelled));
            Assert.Null(cancelled);
            Assert.False(h.Service.DeleteOwnedJob(Module, Type, requestedOwner, id, out var deleted));
            Assert.Null(deleted);
            Assert.NotNull(h.Repository.Get(id));
            Assert.False(h.Repository.IsCancelRequested(id));
            Assert.Single(h.Repository.GetRows(id));
        }
        Assert.Empty(h.Repository.GetActiveOwned(Module, Type, requestedOwner));
        Assert.Empty(h.Repository.GetFinishedOwned(Module, Type, requestedOwner, 10, 0));
        Assert.Empty(h.Service.GetActiveOwnedJobs(Module, Type, requestedOwner));
        Assert.Empty(h.Service.GetFinishedOwnedJobs(Module, Type, requestedOwner, 10, 0));
        Assert.Equal(0, h.Processor.Completions);
    }

    [Fact]
    public void C5FullIdentityIgnoresCasingWithoutConflatingDomainsOrDisplayNames()
    {
        using var h = new OwnedHarness();
        h.Repository.Insert(OwnedHarness.Job("mine", owner: Owner));
        h.Repository.Insert(OwnedHarness.Job("other-domain", owner: "OTHER\\Op\u00e9rateur"));
        h.Repository.Insert(OwnedHarness.Job("legacy-sam", owner: "Op\u00e9rateur"));
        h.Repository.Insert(OwnedHarness.Job("display-only", owner: "somebody-else"));
        h.Row("mine");
        const string differentlyCased = "example\\OP\u00c9RATEUR";
        Assert.Equal(["mine"], h.Service.GetActiveOwnedJobs(Module, Type, differentlyCased).Select(j => j.Id));
        Assert.Equal(Owner, h.Service.GetOwnedJob(Module, Type, differentlyCased, "mine")!.SubmittedBy);
        Assert.Single(h.Service.GetOwnedRows(Module, Type, differentlyCased, "mine"));
        Assert.True(h.Service.CancelOwnedJob(Module, Type, differentlyCased, "mine", out var metadata));
        Assert.Equal(Owner, metadata!.SubmittedBy);
        Assert.Equal(["mine"], h.Service.GetFinishedOwnedJobs(Module, Type, differentlyCased, 10, 0).Select(j => j.Id));
        Assert.True(h.Service.DeleteOwnedJob(Module, Type, differentlyCased, "mine", out _));
    }

    [Fact]
    public void C5OwnHistoryIsFilteredBeforeLimitAndPagesDeterministically()
    {
        using var h = new OwnedHarness();
        for (var index = 0; index < 13; index++)
            h.Repository.Insert(OwnedHarness.Job($"mine-{index:D2}", BulkJobStatus.Completed));
        // All have equal timestamps. Foreign jobs must never consume the limit or offset.
        for (var index = 0; index < 30; index++)
        {
            h.Repository.Insert(OwnedHarness.Job($"aaa-foreign-{index}", BulkJobStatus.Completed, "other"));
            h.Repository.Insert(OwnedHarness.Job($"active-{index:D2}"));
        }
        Assert.Equal(["mine-00", "mine-01"], h.Service.GetFinishedOwnedJobs(Module, Type, Owner, 2, 0).Select(j => j.Id));
        Assert.Equal(["mine-02", "mine-03"], h.Service.GetFinishedOwnedJobs(Module, Type, Owner, 2, 2).Select(j => j.Id));
        Assert.Single(h.Service.GetFinishedOwnedJobs(Module, Type, Owner, 2, 12));
        Assert.Empty(h.Service.GetFinishedOwnedJobs(Module, Type, Owner, 2, 14));
        Assert.Equal(30, h.Service.GetActiveOwnedJobs(Module, Type, Owner).Count);
    }

    [Fact]
    public void C5HistoryPagesRemainBoundedForAnyRequestedLimit()
    {
        using var h = new OwnedHarness();
        for (var index = 0; index < 110; index++)
            h.Repository.Insert(OwnedHarness.Job($"job-{index:D3}", BulkJobStatus.Completed));
        Assert.Equal(100, h.Service.GetFinishedOwnedJobs(Module, Type, Owner, int.MaxValue, 0).Count);
        Assert.Equal("job-000", Assert.Single(h.Service.GetFinishedOwnedJobs(Module, Type, Owner, 0, -10)).Id);
        Assert.Equal(10, h.Service.GetFinishedOwnedJobs(Module, Type, Owner, 100, 100).Count);
    }

    [Theory]
    [InlineData(BulkJobStatus.Queued, false)]
    [InlineData(BulkJobStatus.Running, false)]
    [InlineData(BulkJobStatus.Completed, true)]
    [InlineData(BulkJobStatus.Cancelled, true)]
    [InlineData(BulkJobStatus.Interrupted, true)]
    public void C5DeletionIsTerminalOnlyAndReturnsLegitimateMetadata(BulkJobStatus status, bool expected)
    {
        using var h = new OwnedHarness();
        h.Repository.Insert(OwnedHarness.Job("job", status));
        h.Row("job");
        Assert.Equal(expected, h.Service.DeleteOwnedJob(Module, Type, Owner, "job", out var metadata));
        Assert.Equal("INC123", metadata!.Ticket);
        Assert.Equal(status, metadata.Status);
        Assert.Equal(expected, h.Repository.Get("job") is null);
        Assert.Equal(expected ? 0 : 1, h.Repository.GetRows("job").Count);
        if (expected)
        {
            Assert.False(h.Service.DeleteOwnedJob(Module, Type, Owner, "job", out var missing));
            Assert.Null(missing);
        }
    }

    [Theory]
    [InlineData(BulkJobStatus.Completed)]
    [InlineData(BulkJobStatus.Cancelled)]
    [InlineData(BulkJobStatus.Interrupted)]
    public void C5TerminalJobsCannotBeCancelled(BulkJobStatus status)
    {
        using var h = new OwnedHarness();
        h.Repository.Insert(OwnedHarness.Job("job", status));
        Assert.False(h.Service.CancelOwnedJob(Module, Type, Owner, "job", out var metadata));
        Assert.Equal(status, metadata!.Status);
        Assert.False(h.Repository.IsCancelRequested("job"));
        Assert.Equal(0, h.Processor.Completions);
    }

    [Theory]
    [InlineData(BulkJobStatus.Queued)]
    [InlineData(BulkJobStatus.Running)]
    public void C5CancellationCannotReportSuccessAfterATerminalRace(BulkJobStatus status)
    {
        using var h = new OwnedHarness();
        h.Repository.Insert(OwnedHarness.Job("race", status));
        using var connection = h.Factory.Open();
        using var trigger = connection.CreateCommand();
        trigger.CommandText = $"""
            CREATE TRIGGER terminal_race BEFORE UPDATE OF {(status == BulkJobStatus.Queued ? "status" : "cancel_requested")} ON bulk_job
            WHEN OLD.id = 'race'
            BEGIN
                UPDATE bulk_job SET status = 'Completed' WHERE id = 'race';
                SELECT RAISE(IGNORE);
            END;
            """;
        trigger.ExecuteNonQuery();
        Assert.False(h.Service.CancelOwnedJob(Module, Type, Owner, "race", out _));
        Assert.Equal(BulkJobStatus.Completed, h.Repository.Get("race")!.Status);
        Assert.False(h.Repository.IsCancelRequested("race"));
        Assert.Equal(0, h.Processor.Completions);
    }

    [Fact]
    public async Task C5QueuedCancellationNotifiesExactlyOnceAndNeverRunsTheJob()
    {
        using var h = new OwnedHarness();
        h.Repository.Insert(OwnedHarness.Job("queued"));
        Assert.True(h.Service.CancelOwnedJob(Module, Type, Owner, "queued", out _));
        Assert.False(h.Service.CancelOwnedJob(Module, Type, Owner, "queued", out _));
        await h.Service.DrainQueueAsync(CancellationToken.None);
        Assert.Equal(BulkJobStatus.Cancelled, h.Repository.Get("queued")!.Status);
        Assert.Equal(1, h.Processor.Completions);
        Assert.Equal(0, h.Processor.Rows);
    }

    [Fact]
    public async Task C5RunningCancellationSignalsTheExistingWorkerToken()
    {
        using var h = new OwnedHarness();
        h.Service.Enqueue(OwnedHarness.Job("running"));
        await h.Processor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // Stalled is display-only, so it must use the same Running cancellation path.
        var running = h.Repository.Get("running")!;
        running.HeartbeatAtUtc = DateTime.UtcNow.AddHours(-1);
        Assert.Equal("Stalled", h.Service.DisplayStatus(running, DateTime.UtcNow));
        Assert.True(h.Service.CancelOwnedJob(Module, Type, Owner, "running", out _));
        Assert.True(h.Processor.SeenToken.IsCancellationRequested);
        await h.Processor.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(h.Processor.TokenCancelled);
        Assert.True(h.Repository.IsCancelRequested("running"));
        Assert.Equal(BulkJobStatus.Cancelled, h.Repository.Get("running")!.Status);
        Assert.Equal(1, h.Processor.Completions);
    }

    private sealed class OwnedHarness : IDisposable
    {
        private readonly TempDir _temp = new();
        private readonly ServiceProvider _provider;
        public SqliteConnectionFactory Factory { get; }
        public BulkJobRepository Repository { get; }
        public BulkJobService Service { get; }
        public CancelProcessor Processor { get; } = new();

        public OwnedHarness()
        {
            Factory = new SqliteConnectionFactory(Path.Combine(_temp.Path, "jobs.db"));
            new JobStoreMigrator(Factory).Migrate();
            Repository = new BulkJobRepository(Factory);
            _provider = new ServiceCollection().AddSingleton(Processor).BuildServiceProvider();
            Service = new BulkJobService(_provider.GetRequiredService<IServiceScopeFactory>(), Repository,
                new BulkJobProcessorRegistry([new(Module, typeof(CancelProcessor))]),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["BulkJobs:RecentJobLimit"] = "1" }).Build(),
                NullLogger<BulkJobService>.Instance);
        }

        public static BulkJob Job(string id, BulkJobStatus status = BulkJobStatus.Queued,
            string owner = Owner, string module = Module, string type = Type) => new()
            {
                Id = id,
                ModuleId = module,
                JobType = type,
                SubmittedBy = owner,
                SubmittedByDisplay = Owner,
                SubmittedIp = "192.0.2.5",
                Ticket = "INC123",
                PayloadJson = "{}",
                Status = status,
                SubmittedAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                FinishedAtUtc = status is BulkJobStatus.Completed or BulkJobStatus.Cancelled or BulkJobStatus.Interrupted
                    ? new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc) : null,
            };

        public void Row(string id) => Repository.RecordRow(new BulkJobRow
        {
            JobId = id,
            RowIndex = 0,
            Target = "members.csv",
            Status = BulkJobRowStatus.Success,
            Message = "Resolved",
            RecordedAtUtc = DateTime.UtcNow,
        }, DateTime.UtcNow);

        public void Dispose()
        {
            Processor.Stop.Cancel();
            if (Processor.Rows > 0) Processor.Finished.Task.Wait(TimeSpan.FromSeconds(5));
            _provider.Dispose();
            Processor.Stop.Dispose();
            using var pool = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Factory.DatabasePath }.ToString());
            SqliteConnection.ClearPool(pool);
            _temp.Dispose();
        }
    }

    private sealed class CancelProcessor : IBulkJobProcessor
    {
        public string ModuleId => Module;
        public int Completions;
        public int Rows;
        public bool TokenCancelled;
        public CancellationToken SeenToken;
        public CancellationTokenSource Stop { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CountRows(BulkJob job) => 1;
        public async Task<BulkJobRowOutcome> ProcessRowAsync(BulkJob job, int rowIndex, CancellationToken cancellationToken)
        {
            Rows++;
            SeenToken = cancellationToken;
            Entered.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Stop.Token);
            try { await Task.Delay(Timeout.Infinite, linked.Token); }
            catch (OperationCanceledException) { TokenCancelled = cancellationToken.IsCancellationRequested; throw; }
            return new BulkJobRowOutcome { Target = "unused", Status = BulkJobRowStatus.Success };
        }
        public Task OnJobCompletedAsync(BulkJob job)
        {
            Completions++;
            Finished.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
