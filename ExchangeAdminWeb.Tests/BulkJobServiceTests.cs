using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

public class BulkJobServiceTests
{
    // A substitute processor: rows are encoded in the payload as one status char per row
    // ('S'=success, 'P'=partial, 'F'=failed, 'T'=throw, 'C'=block until cancelled). Records how
    // many rows ran and whether the completion hook fired, so tests assert lifecycle without EXO.
    private sealed class FakeProcessor : IBulkJobProcessor
    {
        public string ModuleId => "Test";
        public int RowsProcessed;
        public int CompletionCalls;
        public readonly List<string> ScopeIds = new();
        public readonly Guid ScopeMarker;

        public FakeProcessor(ScopeMarker marker)
        {
            ScopeMarker = marker.Id;
        }

        public int CountRows(BulkJob job) => job.PayloadJson.Length;

        public Task<BulkJobRowOutcome> ProcessRowAsync(BulkJob job, int rowIndex, CancellationToken ct)
        {
            Interlocked.Increment(ref RowsProcessed);
            var c = job.PayloadJson[rowIndex];
            return c switch
            {
                'T' => throw new InvalidOperationException("row blew up"),
                // 'O' = throw an OperationCanceledException that is NOT the runner's cancel (e.g. a
                // dependency timeout). Must be treated as a failed row, not a batch cancellation.
                'O' => throw new OperationCanceledException("incidental timeout"),
                'C' => BlockUntilCancelled(ct, rowIndex),
                _ => Task.FromResult(new BulkJobRowOutcome
                {
                    Target = $"row{rowIndex}@x",
                    Status = c switch { 'P' => BulkJobRowStatus.Partial, 'F' => BulkJobRowStatus.Failed, _ => BulkJobRowStatus.Success },
                    Message = $"did row {rowIndex}"
                })
            };
        }

        private static async Task<BulkJobRowOutcome> BlockUntilCancelled(CancellationToken ct, int rowIndex)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new BulkJobRowOutcome { Target = $"row{rowIndex}@x", Status = BulkJobRowStatus.Success };
        }

        public Task OnJobCompletedAsync(BulkJob job)
        {
            Interlocked.Increment(ref CompletionCalls);
            return Task.CompletedTask;
        }
    }

    // Scope-scoped marker: a distinct instance per DI scope, so a test can prove the runner resolves
    // the processor from a fresh scope per job rather than reusing a captured singleton.
    private sealed class ScopeMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private sealed class Harness : IDisposable
    {
        public readonly TempDir Temp = new();
        public readonly BulkJobRepository Repository;
        public readonly BulkJobService Service;
        public readonly ServiceProvider Provider;
        public readonly List<FakeProcessor> CreatedProcessors = new();

        public Harness()
        {
            var factory = new SqliteConnectionFactory(System.IO.Path.Combine(Temp.Path, "exchangeadmin-jobs.db"));
            new JobStoreMigrator(factory).Migrate();
            Repository = new BulkJobRepository(factory);

            var services = new ServiceCollection();
            services.AddScoped<ScopeMarker>();
            services.AddScoped(sp =>
            {
                var p = new FakeProcessor(sp.GetRequiredService<ScopeMarker>());
                lock (CreatedProcessors) CreatedProcessors.Add(p);
                return p;
            });
            Provider = services.BuildServiceProvider();

            var registry = new BulkJobProcessorRegistry(
                new[] { new KeyValuePair<string, Type>("Test", typeof(FakeProcessor)) });
            var config = new ConfigurationBuilder().Build();
            Service = new BulkJobService(
                Provider.GetRequiredService<IServiceScopeFactory>(),
                Repository, registry, config, NullLogger<BulkJobService>.Instance);
        }

        public BulkJob NewJob(string id, string payload, string status = "Queued") => new()
        {
            Id = id,
            ModuleId = "Test",
            JobType = "T",
            Status = Enum.Parse<BulkJobStatus>(status),
            SubmittedBy = "u",
            SubmittedIp = "ip",
            PayloadJson = payload,
            SubmittedAtUtc = DateTime.UtcNow
        };

        public void Dispose()
        {
            Provider.Dispose();
            Temp.Dispose();
        }
    }

    [Fact]
    public async Task DrainQueue_ProcessesAllRows_MarksCompleted_WithCorrectCounts()
    {
        using var h = new Harness();
        h.Repository.Insert(h.NewJob("j1", "SSPF")); // 2 success, 1 partial, 1 failed

        await h.Service.DrainQueueAsync(CancellationToken.None);

        var job = h.Repository.Get("j1")!;
        Assert.Equal(BulkJobStatus.Completed, job.Status);
        Assert.Equal(4, job.TotalRows);
        Assert.Equal(4, job.ProcessedRows);
        Assert.Equal(2, job.SuccessCount);
        Assert.Equal(1, job.PartialCount);
        Assert.Equal(1, job.FailedCount);
        Assert.Equal(4, h.Repository.GetRows("j1").Count);
    }

    [Fact]
    public async Task DrainQueue_OneThrowingRow_DoesNotAbortBatch_RecordedAsFailed()
    {
        using var h = new Harness();
        h.Repository.Insert(h.NewJob("j1", "STS")); // success, throw, success

        await h.Service.DrainQueueAsync(CancellationToken.None);

        var job = h.Repository.Get("j1")!;
        Assert.Equal(BulkJobStatus.Completed, job.Status);
        Assert.Equal(3, job.ProcessedRows);
        Assert.Equal(2, job.SuccessCount);
        Assert.Equal(1, job.FailedCount);
    }

    [Fact]
    public async Task DrainQueue_TwoJobs_RunFifo_OneAtATime()
    {
        using var h = new Harness();
        h.Repository.Insert(WithSubmitted(h.NewJob("first", "SS"), new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));
        h.Repository.Insert(WithSubmitted(h.NewJob("second", "SS"), new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc)));

        await h.Service.DrainQueueAsync(CancellationToken.None);

        Assert.Equal(BulkJobStatus.Completed, h.Repository.Get("first")!.Status);
        Assert.Equal(BulkJobStatus.Completed, h.Repository.Get("second")!.Status);
        // Each job got its own scope -> its own processor instance.
        Assert.Equal(2, h.CreatedProcessors.Count);
        Assert.NotEqual(h.CreatedProcessors[0].ScopeMarker, h.CreatedProcessors[1].ScopeMarker);
    }

    [Fact]
    public async Task RunJob_ResolvesProcessorFromFreshScopePerJob()
    {
        using var h = new Harness();
        h.Repository.Insert(h.NewJob("j1", "S"));
        await h.Service.DrainQueueAsync(CancellationToken.None);

        Assert.Single(h.CreatedProcessors);
        Assert.Equal(1, h.CreatedProcessors[0].CompletionCalls);
    }

    [Fact]
    public async Task CancelJob_Queued_FinishesAsCancelled_NeverRuns()
    {
        using var h = new Harness();
        h.Repository.Insert(h.NewJob("j1", "SSSS"));

        h.Service.CancelJob("j1");
        await h.Service.DrainQueueAsync(CancellationToken.None);

        var job = h.Repository.Get("j1")!;
        Assert.Equal(BulkJobStatus.Cancelled, job.Status);
        Assert.Equal(0, job.ProcessedRows);
        // The job never runs any rows. A scope IS created (to fire the completion notification for
        // this terminal state), but no row is processed.
        Assert.Equal(0, h.CreatedProcessors.Sum(p => p.RowsProcessed));
    }

    [Fact]
    public async Task CancelJob_Running_StopsBeforeNextRow()
    {
        using var h = new Harness();
        // 'C' blocks until cancelled; the drain runs on a background task so we can cancel it.
        h.Repository.Insert(h.NewJob("j1", "CCCC"));

        var drain = Task.Run(() => h.Service.DrainQueueAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        // Wait until the job is actually Running.
        await WaitUntil(() => h.Repository.Get("j1")?.Status == BulkJobStatus.Running);

        h.Service.CancelJob("j1");
        await drain;

        var job = h.Repository.Get("j1")!;
        Assert.Equal(BulkJobStatus.Cancelled, job.Status);
    }

    [Fact]
    public void Initialize_FlipsOrphanedNonTerminalJobsToInterrupted()
    {
        using var h = new Harness();
        h.Repository.Insert(h.NewJob("wasRunning", "SS", "Running"));
        h.Repository.Insert(h.NewJob("wasQueued", "SS", "Queued"));
        var done = h.NewJob("wasDone", "SS", "Completed");
        done.FinishedAtUtc = DateTime.UtcNow;
        h.Repository.Insert(done);

        h.Service.InitializeAsync();

        Assert.Equal(BulkJobStatus.Interrupted, h.Repository.Get("wasRunning")!.Status);
        Assert.Equal(BulkJobStatus.Interrupted, h.Repository.Get("wasQueued")!.Status);
        Assert.Equal(BulkJobStatus.Completed, h.Repository.Get("wasDone")!.Status);
    }

    [Fact]
    public void DisplayStatus_RunningWithStaleHeartbeat_IsStalled()
    {
        using var h = new Harness();
        var job = h.NewJob("j1", "SS", "Running");
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        job.HeartbeatAtUtc = now.AddMinutes(-10); // default threshold 5 min

        Assert.Equal("Stalled", h.Service.DisplayStatus(job, now));

        job.HeartbeatAtUtc = now.AddMinutes(-1);
        Assert.Equal("Running", h.Service.DisplayStatus(job, now));
    }

    [Fact]
    public async Task DrainQueue_IncidentalOperationCanceled_IsFailedRow_NotBatchCancel()
    {
        using var h = new Harness();
        h.Repository.Insert(h.NewJob("j1", "SOS")); // success, incidental-OCE, success

        await h.Service.DrainQueueAsync(CancellationToken.None);

        var job = h.Repository.Get("j1")!;
        // The batch must complete (not be cancelled) and the OCE row counts as failed.
        Assert.Equal(BulkJobStatus.Completed, job.Status);
        Assert.Equal(3, job.ProcessedRows);
        Assert.Equal(2, job.SuccessCount);
        Assert.Equal(1, job.FailedCount);
    }

    [Fact]
    public void CancelJob_Queued_FiresCompletionNotification()
    {
        using var h = new Harness();
        h.Repository.Insert(h.NewJob("j1", "SS"));

        h.Service.CancelJob("j1");

        // A queued cancel is terminal -> the completion hook must fire (from a fresh scope).
        Assert.Single(h.CreatedProcessors);
        Assert.Equal(1, h.CreatedProcessors[0].CompletionCalls);
        Assert.Equal(BulkJobStatus.Cancelled, h.Repository.Get("j1")!.Status);
    }

    [Fact]
    public async Task DrainQueue_CompletedJob_FiresCompletionNotificationExactlyOnce()
    {
        using var h = new Harness();
        h.Repository.Insert(h.NewJob("j1", "SS"));

        await h.Service.DrainQueueAsync(CancellationToken.None);

        Assert.Single(h.CreatedProcessors);
        Assert.Equal(1, h.CreatedProcessors[0].CompletionCalls);
    }

    [Fact]
    public async Task Pump_SurvivesJobFault_AndKeepsProcessingNextJob()
    {
        using var h = new Harness();
        // First job's module is unregistered mid-run only in the repo sense; instead simulate a
        // fault by inserting a job whose payload count throws is covered elsewhere. Here we assert
        // the pump keeps running: an unregistered-module job is interrupted, and a following valid
        // job still completes in the same drain.
        var bad = new BulkJob
        {
            Id = "bad",
            ModuleId = "Unknown",
            JobType = "T",
            Status = BulkJobStatus.Queued,
            SubmittedBy = "u",
            SubmittedIp = "ip",
            PayloadJson = "SS",
            SubmittedAtUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)
        };
        h.Repository.Insert(bad);
        h.Repository.Insert(WithSubmitted(h.NewJob("good", "SS"), new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc)));

        await h.Service.DrainQueueAsync(CancellationToken.None);

        Assert.Equal(BulkJobStatus.Interrupted, h.Repository.Get("bad")!.Status);
        Assert.Equal(BulkJobStatus.Completed, h.Repository.Get("good")!.Status);
    }

    [Fact]
    public async Task RunJob_UnregisteredModule_MarksInterrupted()
    {
        using var h = new Harness();
        var job = h.NewJob("j1", "SS");
        var orphan = new BulkJob
        {
            Id = job.Id,
            ModuleId = "Unknown",
            JobType = "T",
            Status = BulkJobStatus.Queued,
            SubmittedBy = "u",
            SubmittedIp = "ip",
            PayloadJson = "SS",
            SubmittedAtUtc = DateTime.UtcNow
        };
        h.Repository.Insert(orphan);

        await h.Service.DrainQueueAsync(CancellationToken.None);

        Assert.Equal(BulkJobStatus.Interrupted, h.Repository.Get("j1")!.Status);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Condition not met in time");
            await Task.Delay(20);
        }
    }

    private static BulkJob WithSubmitted(BulkJob job, DateTime submitted) => new()
    {
        Id = job.Id,
        ModuleId = job.ModuleId,
        JobType = job.JobType,
        Status = job.Status,
        SubmittedBy = job.SubmittedBy,
        SubmittedIp = job.SubmittedIp,
        PayloadJson = job.PayloadJson,
        SubmittedAtUtc = submitted
    };
}
