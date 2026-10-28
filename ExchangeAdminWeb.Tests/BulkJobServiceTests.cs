using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        /// <summary>
        /// The ambient usage session observed on the pump thread, one entry per row (review
        /// finding utei-1). Must be null: the pump has no browser circuit behind it.
        /// </summary>
        public readonly List<string?> SessionsSeen = new();

        public FakeProcessor(ScopeMarker marker)
        {
            ScopeMarker = marker.Id;
        }

        public int CountRows(BulkJob job) => job.PayloadJson.Length;

        public Task<BulkJobRowOutcome> ProcessRowAsync(BulkJob job, int rowIndex, CancellationToken ct)
        {
            Interlocked.Increment(ref RowsProcessed);
            lock (SessionsSeen)
                SessionsSeen.Add(UsageSession.Current.Value);
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

        /// <param name="settings">
        /// Configuration the runner is built over; empty unless a test is about a setting (aud-a64).
        /// </param>
        public Harness(params (string Key, string Value)[] settings)
            : this(NullLogger<BulkJobService>.Instance, settings)
        {
        }

        /// <param name="logger">The runner's log; a null logger unless a test is about what the runner says (kd-82).</param>
        /// <param name="settings">As the other constructor's.</param>
        public Harness(ILogger<BulkJobService> logger, params (string Key, string Value)[] settings)
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
            // kd-83: a processor that cannot be built, served under a module of its own, so only a job of that module meets it.
            services.AddScoped<UnbuildableDependency>(_ => throw new InvalidOperationException(UnbuildableDependency.Failure));
            services.AddScoped<UnbuildableProcessor>();
            Provider = services.BuildServiceProvider();

            var registry = new BulkJobProcessorRegistry(new[]
            {
                new KeyValuePair<string, Type>("Test", typeof(FakeProcessor)),
                new KeyValuePair<string, Type>(UnbuildableProcessor.Module, typeof(UnbuildableProcessor)),
            });
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
                .Build();
            Service = new BulkJobService(
                Provider.GetRequiredService<IServiceScopeFactory>(),
                Repository, registry, config, logger);
        }

        public BulkJob NewJob(string id, string payload, string status = "Queued", string moduleId = "Test") => new()
        {
            Id = id,
            ModuleId = moduleId,
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

    // -------------------------------------------------------------------------
    // Module-scoped reads and removal
    // (docs/ConferenceRoomsBulkJobPanel-Plan.md F1, D2)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetActiveJobsByModule_ExcludesAnotherModulesRunningJob()
    {
        using var h = new Harness();
        h.Repository.Insert(h.NewJob("foreign", "S", "Running", moduleId: "MessageTrace"));
        h.Repository.Insert(h.NewJob("mine", "S"));

        await Task.CompletedTask;

        Assert.Equal(["mine"], h.Service.GetActiveJobsByModule("Test").Select(j => j.Id));
    }

    [Fact]
    public async Task GetRecentJobsByModule_ExcludesAnotherModulesFinishedJob()
    {
        using var h = new Harness();
        h.Repository.Insert(h.NewJob("mine", "S"));
        await h.Service.DrainQueueAsync(CancellationToken.None);

        var foreign = h.NewJob("foreign", "S", "Completed", moduleId: "MessageTrace");
        foreign.FinishedAtUtc = DateTime.UtcNow;
        h.Repository.Insert(foreign);

        Assert.Equal(["mine"], h.Service.GetRecentJobsByModule("Test").Select(j => j.Id));
    }

    [Fact]
    public async Task DeleteJob_RemovesAFinishedJobOfThatModule()
    {
        using var h = new Harness();
        h.Repository.Insert(h.NewJob("j1", "S"));
        await h.Service.DrainQueueAsync(CancellationToken.None);

        Assert.True(h.Service.DeleteJob("Test", "j1"));
        Assert.Null(h.Repository.Get("j1"));
    }

    [Fact]
    public async Task DeleteJob_RefusesAJobBelongingToAnotherModule()
    {
        // Without the module check the page would hand an arbitrary id to a delete - the same
        // cross-module hole as the unscoped Cancel button, in a more destructive form.
        using var h = new Harness();
        var foreign = h.NewJob("foreign", "S", "Completed", moduleId: "MessageTrace");
        foreign.FinishedAtUtc = DateTime.UtcNow;
        h.Repository.Insert(foreign);

        await Task.CompletedTask;

        Assert.False(h.Service.DeleteJob("Test", "foreign"));
        Assert.NotNull(h.Repository.Get("foreign"));
    }

    [Fact]
    public async Task DeleteJob_RefusesAStillActiveJob()
    {
        using var h = new Harness();
        h.Repository.Insert(h.NewJob("queued", "S"));

        await Task.CompletedTask;

        Assert.False(h.Service.DeleteJob("Test", "queued"));
        Assert.NotNull(h.Repository.Get("queued"));
    }

    [Fact]
    public async Task DeleteJob_ReturnsFalseForAnUnknownId()
    {
        using var h = new Harness();

        await Task.CompletedTask;

        Assert.False(h.Service.DeleteJob("Test", "nope"));
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

    [Theory]
    [InlineData("Queued", true)]
    [InlineData("Running", true)]
    [InlineData("Completed", false)]
    [InlineData("Cancelled", false)]
    [InlineData("Interrupted", false)]
    [InlineData(null, false)]
    public void CancelJob_SaysWhetherItTookEffect(string? status, bool expected)
    {
        // kd-5. A caller that audits the cancel records whether it happened: a queued job finished as Cancelled or a running
        // job's cancellation requested is true; a finished job, or none (null - not there), is false and changes nothing.
        using var h = new Harness();
        if (status is not null)
            h.Repository.Insert(h.NewJob("j1", "S", status));

        Assert.Equal(expected, h.Service.CancelJob("j1"));

        var after = h.Repository.Get("j1");
        if (status is null)
            Assert.Null(after);
        else if (!expected)
            Assert.Equal(Enum.Parse<BulkJobStatus>(status), after!.Status);
    }

    [Theory]
    [InlineData("Completed", true, BulkJobRemoval.Removed)]
    [InlineData("Cancelled", true, BulkJobRemoval.Removed)]
    [InlineData("Interrupted", true, BulkJobRemoval.Removed)]
    [InlineData("Running", false, BulkJobRemoval.NotFinished)]
    [InlineData("Queued", false, BulkJobRemoval.NotFinished)]
    [InlineData(null, false, BulkJobRemoval.NotFound)]
    public void DeleteJob_SaysWhyItDidNotHappen(string? status, bool expected, BulkJobRemoval outcome)
    {
        // kd-62. A caller that audits the removal has to name the refusal, and this method knows it: the page read the job
        // back to work it out, which left no row at all when that second read threw.
        using var h = new Harness();
        if (status is not null)
            h.Repository.Insert(h.NewJob("j1", "S", status));

        Assert.Equal(expected, h.Service.DeleteJob("Test", "j1", out var reported));

        Assert.Equal(outcome, reported);
        // Still there exactly when it existed and was not removed.
        Assert.Equal(status is not null && !expected, h.Repository.Get("j1") is not null);
    }

    [Fact]
    public void DeleteJob_AnotherModulesJob_IsRefusedAsSuch_AndKept()
    {
        using var h = new Harness();
        h.Repository.Insert(h.NewJob("j1", "S", "Completed", moduleId: "MessageTrace"));

        Assert.False(h.Service.DeleteJob("Test", "j1", out var outcome));

        Assert.Equal(BulkJobRemoval.NotThisModule, outcome);
        Assert.NotNull(h.Repository.Get("j1"));
    }

    [Theory]
    [InlineData("Completed", true)]
    [InlineData("Running", false)]
    [InlineData(null, false)]
    public void DeleteJob_TheOldOverload_AnswersWhatItAlwaysDid(string? status, bool expected)
    {
        // kd-62. ConferenceRooms' own Remove and every existing caller keep the bool-only call.
        using var h = new Harness();
        if (status is not null)
            h.Repository.Insert(h.NewJob("j1", "S", status));

        Assert.Equal(expected, h.Service.DeleteJob("Test", "j1"));
    }

    [Fact]
    public async Task DeleteJob_ConcurrentRemovalsOfOneFinishedJob_RemoveItOnce_AndTellTheRestItIsGone()
    {
        // kd-77. Removals of one finished job, released together: exactly one removes it, and every other is told it no longer
        // exists. With a read and a separate delete, a removal that read the job before another deleted it was told it had not
        // finished. Each on a thread of its own, so the release does not wait on the thread pool.
        using var h = new Harness();
        h.Repository.Insert(h.NewJob("j1", "S", "Completed"));
        const int Removals = 8;
        using var start = new Barrier(Removals);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, Removals).Select(_ => Task.Factory.StartNew(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(30)), "the removals were never released together");
            h.Service.DeleteJob("Test", "j1", out var outcome);
            return outcome;
        }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)));

        Assert.Single(outcomes, o => o == BulkJobRemoval.Removed);
        Assert.Equal(Removals - 1, outcomes.Count(o => o == BulkJobRemoval.NotFound));
        Assert.Null(h.Repository.Get("j1"));
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

    /// <summary>
    /// aud-a64: a negative BulkJobs:RetentionDays put the start-up prune's cutoff in the future, so every finished job
    /// record was deleted at start-up (the audit rows survive, but the operator's job history did not). A retention
    /// outside its range is the default 30 days, so a job finished yesterday is kept.
    /// </summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    public void Initialize_GivenARetentionOutsideItsRange_KeepsAFinishedJob(string retention)
    {
        using var h = new Harness(("BulkJobs:RetentionDays", retention));
        var done = h.NewJob("wasDone", "SS", "Completed");
        done.FinishedAtUtc = DateTime.UtcNow.AddDays(-1);
        h.Repository.Insert(done);

        h.Service.InitializeAsync();

        Assert.NotNull(h.Repository.Get("wasDone"));
        Assert.Equal(30, h.Service.RetentionDays);
    }

    /// <summary>
    /// aud-a64: a negative BulkJobs:RecentJobLimit reached SQLite as LIMIT -n, which means no limit, so the page listed
    /// every finished job there had ever been. A limit outside its range is the default 25.
    /// </summary>
    [Fact]
    public void GetRecentJobs_GivenALimitOutsideItsRange_ListsTheDefaultTwentyFive()
    {
        using var h = new Harness(("BulkJobs:RecentJobLimit", "-1"));
        for (var i = 0; i < 30; i++)
        {
            var job = h.NewJob($"j{i:00}", "S", "Completed");
            job.FinishedAtUtc = DateTime.UtcNow.AddMinutes(-i);
            h.Repository.Insert(job);
        }

        Assert.Equal(25, h.Service.GetRecentJobs().Count);
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

    // -------------------------------------------------------------------------
    // Ambient state must not leak into the shared pump (review finding utei-1)
    // -------------------------------------------------------------------------

    /// <summary>
    /// utei-1: the pump is started by whichever browser circuit enqueues first and then drains
    /// EVERY operator's jobs. If its Task.Run captured that circuit's execution context, every
    /// job the pump ever runs would see the first operator's <see cref="UsageSession"/>, and the
    /// audited rows of jobs submitted by other operators would be stamped with a visit they had
    /// nothing to do with. This goes through Enqueue on purpose: the other tests in this file
    /// call DrainQueueAsync directly and so never exercise how the pump task is started.
    /// </summary>
    [Fact]
    public async Task Pump_DoesNotInheritTheEnqueueingCircuitsUsageSession()
    {
        using var h = new Harness();
        UsageSession.Current.Value = "circuit-A";
        try
        {
            h.Service.Enqueue(h.NewJob("j1", "SS"));

            await WaitUntil(() => h.Repository.Get("j1")?.Status == BulkJobStatus.Completed);

            // The circuit that enqueued still has its own session; only the pump is clean.
            Assert.Equal("circuit-A", UsageSession.Current.Value);

            string?[] seen;
            lock (h.CreatedProcessors)
                seen = h.CreatedProcessors.SelectMany(p => { lock (p.SessionsSeen) return p.SessionsSeen.ToArray(); }).ToArray();

            Assert.Equal(2, seen.Length);
            Assert.All(seen, Assert.Null);
        }
        finally
        {
            UsageSession.Current.Value = null;
        }
    }

    // -------------------------------------------------------------------------
    // kd-82: a read of the queue that fails is made again, a bounded number of times
    // -------------------------------------------------------------------------
    // The runner stopped at its first failed read of the queue, and only the next Enqueue started it again: a job just
    // queued - kd-80's case, the store failing a read just after the job's write - stayed Queued until someone submitted
    // another job in any module, or was marked Interrupted at the next restart. The read is now made again after a wait,
    // up to QueueReadAttempts reads, and a store that keeps failing stops the runner with one line saying so.

    [Fact]
    public async Task AQueueReadThatFails_IsMadeAgainAfterAWait_AndTheQueuedJobRunsWithoutAnotherSubmit()
    {
        var log = new CapturingLogger();
        using var h = new Harness(log);
        h.Repository.Insert(h.NewJob("j1", "SS"));
        // The store fails the runner's first read, and can be read again by the time the runner has waited.
        RenameJobsTable(h, "bulk_job", "bulk_job_held");
        var waits = new List<TimeSpan>();
        h.Service.WaitBeforeQueueRead = (delay, _) =>
        {
            waits.Add(delay);
            if (waits.Count == 1)
                RenameJobsTable(h, "bulk_job_held", "bulk_job");
            return Task.CompletedTask;
        };

        // One drive of the runner, as the job's own submit starts it; nothing else drives it.
        await h.Service.DrainQueueAsync(CancellationToken.None);

        // The runner waited once, the back-off's first wait of a second, and read the queue again.
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, waits);
        var job = h.Repository.Get("j1")!;
        Assert.Equal(BulkJobStatus.Completed, job.Status);
        Assert.Equal(2, job.SuccessCount);
        // Said once, with the failed read's own exception: the queue could not be read once, and was then.
        var said = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, said.Level);
        Assert.Equal("The bulk job queue could not be read 1 time(s); it was read on the next attempt and the runner carries on", said.Message);
        Assert.IsType<SqliteException>(said.Exception);
    }

    [Fact]
    public async Task AQueueThatCannotBeRead_IsReadABoundedNumberOfTimes_ThenTheRunnerStopsAndSaysSoOnce()
    {
        var log = new CapturingLogger();
        using var h = new Harness(log);
        var waits = new List<TimeSpan>();
        h.Service.WaitBeforeQueueRead = (delay, _) =>
        {
            lock (waits)
            {
                waits.Add(delay);
                // A runner that never stops would read for ever; end it here, so that it fails this test rather than hangs it.
                if (waits.Count > 50)
                    throw new InvalidOperationException("The runner is still reading the queue.");
            }

            return Task.CompletedTask;
        };
        // kd-80's sequence, through a submit: the store takes the job's write and fails every read after it. Enqueue raises
        // JobChanged once its insert has committed and before the runner reads, so the first event renames the table away.
        var unreadable = false;
        h.Service.JobChanged += _ =>
        {
            if (unreadable)
                return;
            RenameJobsTable(h, "bulk_job", "bulk_job_held");
            unreadable = true;
        };

        h.Service.Enqueue(h.NewJob("j1", "SS"));
        await log.FirstError.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(unreadable, "the store was not made unreadable after the write");
        // The bound: QueueReadAttempts reads with a wait between each two, doubling from a second, and then no more.
        TimeSpan[] waited;
        lock (waits)
            waited = waits.ToArray();
        Assert.Equal(BulkJobService.QueueReadAttempts - 1, waited.Length);
        Assert.Equal(new[] { 1, 2, 4, 8, 16 }.Select(seconds => TimeSpan.FromSeconds(seconds)), waited);
        // Said once, with the last read's own exception.
        var said = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, said.Level);
        Assert.Equal(
            $"The bulk job queue could not be read in {BulkJobService.QueueReadAttempts} attempts; the runner has stopped and starts again when the next job is submitted",
            said.Message);
        Assert.IsType<SqliteException>(said.Exception);
        Assert.Equal("Queued", HeldStatus(h, "j1"));

        // Stopped, not wedged: once the store can be read again, the next submit starts the runner, and it runs the job that
        // waited as well as its own.
        RenameJobsTable(h, "bulk_job_held", "bulk_job");
        h.Service.Enqueue(h.NewJob("j2", "S"));
        await WaitUntil(() => h.Repository.Get("j1")?.Status == BulkJobStatus.Completed
            && h.Repository.Get("j2")?.Status == BulkJobStatus.Completed, timeoutMs: 30000);
        Assert.Single(log.Entries);
        lock (waits)
            Assert.Equal(BulkJobService.QueueReadAttempts - 1, waits.Count);
    }

    // kd-82's stand-in for a store that cannot be read: the jobs table renamed away, so a read of the queue meets "no such
    // table", and renamed back when the store recovers. The rows stay where they were written.
    private static void RenameJobsTable(Harness h, string from, string to)
    {
        using var connection = new SqliteConnectionFactory(h.Repository.DatabasePath).Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {from} RENAME TO {to};";
        command.ExecuteNonQuery();
    }

    // A job's status while the jobs table is renamed away, read where the stand-in keeps it.
    private static string? HeldStatus(Harness h, string id)
    {
        using var connection = new SqliteConnectionFactory(h.Repository.DatabasePath).Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM bulk_job_held WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    // The runner's log, kept: each line's level, its rendered message and its exception; and the first error, to wait on.
    private sealed class CapturingLogger : ILogger<BulkJobService>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new();
        private readonly TaskCompletionSource _firstError = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstError => _firstError.Task;

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, exception), exception));
            if (logLevel >= LogLevel.Error)
                _firstError.TrySetResult();
        }
    }

    // -------------------------------------------------------------------------
    // kd-83: a job whose processor cannot be built stops the runner no more
    // -------------------------------------------------------------------------
    // RunJobAsync resolved the job's processor above its try, so a processor that could not be built - a scoped dependency
    // whose construction throws - escaped to the loop's outer catch and stopped the runner with the job still Queued; being
    // the oldest, it stopped the next submit's runner the same way, and every job behind it waited for a restart. Such a
    // job is now Interrupted with the reason, and the runner goes on to the next job.

    [Fact]
    public async Task AJobWhoseProcessorCannotBeBuilt_IsInterruptedWithTheReason_AndTheJobBehindItRuns()
    {
        var log = new CapturingLogger();
        using var h = new Harness(log);
        h.Repository.Insert(WithSubmitted(h.NewJob("unbuildable", "SS", moduleId: UnbuildableProcessor.Module),
            new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));
        h.Repository.Insert(WithSubmitted(h.NewJob("good", "SS"), new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc)));

        // One drive of the runner, as a submit starts it.
        await h.Service.DrainQueueAsync(CancellationToken.None);

        // The job behind it ran, in the same drive, and its completion hook fired once.
        var good = h.Repository.Get("good")!;
        Assert.Equal(BulkJobStatus.Completed, good.Status);
        Assert.Equal(2, good.SuccessCount);
        Assert.Equal(1, Assert.Single(h.CreatedProcessors).CompletionCalls);
        // The job whose processor could not be built is finished, with the reason, and ran nothing.
        var unbuildable = h.Repository.Get("unbuildable")!;
        Assert.Equal(BulkJobStatus.Interrupted, unbuildable.Status);
        Assert.Equal($"The processor for module '{UnbuildableProcessor.Module}' could not be built: {UnbuildableDependency.Failure}",
            unbuildable.Message);
        Assert.Equal(0, unbuildable.ProcessedRows);
        Assert.Empty(h.Repository.GetRows("unbuildable"));
        // Said: the processor could not be built; and its completion hook - the processor's own - could not be run either,
        // which is a notification failure, not the end of the runner.
        Assert.Collection(log.Entries,
            said =>
            {
                Assert.Equal(LogLevel.Error, said.Level);
                Assert.Equal(
                    $"The bulk job processor for module {UnbuildableProcessor.Module} could not be built; marking job unbuildable Interrupted",
                    said.Message);
                Assert.Equal(UnbuildableDependency.Failure, Assert.IsType<InvalidOperationException>(said.Exception).Message);
            },
            said =>
            {
                Assert.Equal(LogLevel.Error, said.Level);
                Assert.Equal("Completion notification for bulk job unbuildable failed (non-fatal)", said.Message);
                Assert.Equal(UnbuildableDependency.Failure, Assert.IsType<InvalidOperationException>(said.Exception).Message);
            });
    }

    // kd-83's stand-in for a processor that cannot be built: its one dependency throws as it is constructed, as a scoped
    // service that cannot be built does, so the container throws for the processor itself.
    private sealed class UnbuildableDependency
    {
        public const string Failure = "The processor's dependency could not be built.";
    }

    private sealed class UnbuildableProcessor : IBulkJobProcessor
    {
        public const string Module = "Unbuildable";

        public UnbuildableProcessor(UnbuildableDependency dependency) => ArgumentNullException.ThrowIfNull(dependency);

        public string ModuleId => Module;

        public int CountRows(BulkJob job) => throw new InvalidOperationException("The processor was built after all.");

        public Task<BulkJobRowOutcome> ProcessRowAsync(BulkJob job, int rowIndex, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The processor was built after all.");
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
