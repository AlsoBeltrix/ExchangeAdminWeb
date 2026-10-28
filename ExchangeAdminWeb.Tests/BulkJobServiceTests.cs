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

        /// <summary>kd-85. The job each completion hook was given - its id and its status - in the order given.</summary>
        public readonly List<(string Id, BulkJobStatus Status)> CompletedJobs = new();

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
            lock (CompletedJobs)
                CompletedJobs.Add((job.Id, job.Status));
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

        /// <summary>kd-84. Called after each line is kept, on the logging thread; null unless a test sets it.</summary>
        public Action? AfterEachLine { get; set; }

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
            AfterEachLine?.Invoke();
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

    // -------------------------------------------------------------------------
    // kd-83 (2): a final status write that fails is made again, a bounded number of times
    // -------------------------------------------------------------------------
    // A store that failed a job's final status write threw out of the runner's terminal tail: from the job's own try into
    // the backstop, which wrote again, and from the backstop out of the runner, which stopped until the next submit with the
    // job left Running or Queued. The write is now made again after a wait, up to FinalWriteAttempts writes; a store that
    // keeps failing leaves the job for the next start, with one line saying so, and the runner goes on to the next job.

    [Fact]
    public async Task AFinalStatusWriteThatFails_IsMadeAgainAfterAWait_AndTheJobBehindItRuns()
    {
        var log = new CapturingLogger();
        using var h = new Harness(log);
        h.Repository.Insert(WithSubmitted(h.NewJob("j1", "SS"), new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));
        h.Repository.Insert(WithSubmitted(h.NewJob("j2", "S"), new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc)));
        // The store fails j1's final status write, and takes it by the time the runner has waited.
        FailFinalStatusWrites(h, "j1");
        var waits = new List<TimeSpan>();
        h.Service.WaitBeforeFinalWrite = delay =>
        {
            waits.Add(delay);
            if (waits.Count == 1)
                RecoverFinalStatusWrites(h);
            return Task.CompletedTask;
        };

        // One drive of the runner, as a submit starts it.
        await h.Service.DrainQueueAsync(CancellationToken.None);

        // The runner waited once, the back-off's first wait of a second, and wrote the status again: Completed, as the job ran.
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, waits);
        var j1 = h.Repository.Get("j1")!;
        Assert.Equal(BulkJobStatus.Completed, j1.Status);
        Assert.Equal(2, j1.SuccessCount);
        Assert.Null(j1.Message);
        // The job behind it ran, and each job's completion hook fired once - j1's once its status had landed.
        Assert.Equal(BulkJobStatus.Completed, h.Repository.Get("j2")!.Status);
        Assert.Equal(new[] { 1, 1 }, h.CreatedProcessors.Select(p => p.CompletionCalls));
        // Said once, with the failed write's own exception.
        var said = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, said.Level);
        Assert.Equal(
            "The final status (Completed) of bulk job j1 could not be written 1 time(s); the next attempt succeeded and the runner carries on",
            said.Message);
        Assert.IsType<SqliteException>(said.Exception);
    }

    [Fact]
    public async Task AFinalStatusWriteThatKeepsFailing_IsMadeABoundedNumberOfTimes_ThenTheJobIsLeftForTheNextStart_AndTheJobBehindItRuns()
    {
        var log = new CapturingLogger();
        using var h = new Harness(log);
        h.Repository.Insert(WithSubmitted(h.NewJob("j1", "SS"), new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));
        h.Repository.Insert(WithSubmitted(h.NewJob("j2", "S"), new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc)));
        // The store fails every final status write of j1, and nothing else.
        FailFinalStatusWrites(h, "j1");
        var waits = RecordWaitsBeforeFinalWrite(h);

        await h.Service.DrainQueueAsync(CancellationToken.None);

        // The bound: FinalWriteAttempts writes with a wait between each two, doubling from a second, and then no more.
        TimeSpan[] waited;
        lock (waits)
            waited = waits.ToArray();
        Assert.Equal(BulkJobService.FinalWriteAttempts - 1, waited.Length);
        Assert.Equal(new[] { 1, 2, 4, 8, 16 }.Select(seconds => TimeSpan.FromSeconds(seconds)), waited);
        // Said once, with the last write's own exception.
        var said = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, said.Level);
        Assert.Equal(
            $"The final status (Completed) of bulk job j1 could not be written in {BulkJobService.FinalWriteAttempts} attempts; the job is left as it is, for the next start to mark Interrupted, and the runner carries on",
            said.Message);
        Assert.IsType<SqliteException>(said.Exception);
        // Left as it is - Running, its rows recorded, no fault written over it - for the next start's sweep; and no completion
        // hook for a result the store does not hold.
        var j1 = h.Repository.Get("j1")!;
        Assert.Equal(BulkJobStatus.Running, j1.Status);
        Assert.Equal(2, j1.ProcessedRows);
        Assert.Null(j1.Message);
        // The runner carried on: the job behind it ran, and its hook fired.
        Assert.Equal(BulkJobStatus.Completed, h.Repository.Get("j2")!.Status);
        Assert.Equal(new[] { 0, 1 }, h.CreatedProcessors.Select(p => p.CompletionCalls));
    }

    [Fact]
    public async Task AJobLeftQueuedByAFinalStatusWriteThatKeepsFailing_IsNotTakenAgain_AndTheJobsBehindItRun()
    {
        // A job finished before it starts - here one of a module no processor is registered for - is written from Queued, so a
        // write the store keeps failing leaves it Queued, the oldest job in the queue. Taken again, it would be written for
        // ever, and the jobs behind it would wait.
        var log = new CapturingLogger();
        using var h = new Harness(log);
        h.Repository.Insert(WithSubmitted(h.NewJob("orphan", "SS", moduleId: "Unknown"),
            new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));
        h.Repository.Insert(WithSubmitted(h.NewJob("good", "S"), new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc)));
        FailFinalStatusWrites(h, "orphan");
        var waits = RecordWaitsBeforeFinalWrite(h);

        await h.Service.DrainQueueAsync(CancellationToken.None);

        // The job behind it ran in the same drive; the orphan had one run of writes and was not taken again.
        Assert.Equal(BulkJobStatus.Completed, h.Repository.Get("good")!.Status);
        lock (waits)
            Assert.Equal(new[] { 1, 2, 4, 8, 16 }.Select(seconds => TimeSpan.FromSeconds(seconds)), waits);
        Assert.Equal(BulkJobStatus.Queued, h.Repository.Get("orphan")!.Status);
        Assert.Collection(log.Entries,
            said => Assert.Equal("No bulk job processor registered for module Unknown; marking job orphan Interrupted", said.Message),
            said =>
            {
                Assert.Equal(LogLevel.Error, said.Level);
                Assert.Equal(
                    $"The final status (Interrupted) of bulk job orphan could not be written in {BulkJobService.FinalWriteAttempts} attempts; the job is left as it is, for the next start to mark Interrupted, and the runner carries on",
                    said.Message);
                Assert.IsType<SqliteException>(said.Exception);
            });

        // Left for the next start, not the next submit: the runner a later submit starts runs its own job and passes the
        // orphan over as well.
        h.Service.Enqueue(h.NewJob("later", "S"));
        await WaitUntil(() => h.Repository.Get("later")?.Status == BulkJobStatus.Completed, timeoutMs: 30000);
        Assert.Equal(BulkJobStatus.Queued, h.Repository.Get("orphan")!.Status);
        lock (waits)
            Assert.Equal(BulkJobService.FinalWriteAttempts - 1, waits.Count);
        Assert.Equal(2, log.Entries.Count);
    }

    // kd-83's stand-in for a store that fails a job's final status write: a trigger that refuses that one job's move to a
    // finished status, so the runner's write meets a SqliteException at the call a locked, full or read-only store fails,
    // while every other read and write - the job's start and its rows, the queue, the jobs behind it - is made as before.
    // Dropped when the store recovers.
    private static void FailFinalStatusWrites(Harness h, string jobId) => ExecuteOnJobsStore(h,
        $"""
        CREATE TRIGGER kd83_fail_final_status BEFORE UPDATE OF status ON bulk_job
        WHEN NEW.id = '{jobId}' AND NEW.status IN ('Completed', 'Cancelled', 'Interrupted')
        BEGIN SELECT RAISE(ABORT, 'The store did not take the final status write.'); END;
        """);

    private static void RecoverFinalStatusWrites(Harness h) => ExecuteOnJobsStore(h, "DROP TRIGGER kd83_fail_final_status;");

    private static void ExecuteOnJobsStore(Harness h, string sql)
    {
        using var connection = new SqliteConnectionFactory(h.Repository.DatabasePath).Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // Each wait before a final status write, recorded and made at once. A runner past 50 waits is ended here, so that a
    // runner that never stops writing fails its test rather than hangs it.
    private static List<TimeSpan> RecordWaitsBeforeFinalWrite(Harness h)
    {
        var waits = new List<TimeSpan>();
        h.Service.WaitBeforeFinalWrite = delay =>
        {
            lock (waits)
            {
                waits.Add(delay);
                if (waits.Count > 50)
                    throw new InvalidOperationException("The runner is still writing the final status.");
            }

            return Task.CompletedTask;
        };
        return waits;
    }

    // -------------------------------------------------------------------------
    // kd-84: a read of the finished job that fails is a notification failure
    // -------------------------------------------------------------------------
    // FinishAndNotify read the job back for its completion hook outside any try. A read the store failed, after it took the
    // job's final write, threw out of the tail: from the job's own try into the backstop - "faulted unexpectedly", for a job
    // that had finished - and from the backstop out of the runner, which stopped until the next submit with the jobs behind
    // it Queued. The read is now in one try with the hook: its failure is logged as a notification failure, the job's status
    // stands, and the runner goes on to the next job.

    [Fact]
    public async Task AReadOfTheFinishedJobThatFails_IsANotificationFailure_ItsStatusStands_AndTheJobBehindItRuns()
    {
        var log = new CapturingLogger();
        using var h = new Harness(log);
        h.Repository.Insert(WithSubmitted(h.NewJob("j1", "SS"), new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));
        h.Repository.Insert(WithSubmitted(h.NewJob("j2", "S"), new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc)));
        // The store takes j1's final write, and fails the read of j1 right after it, once.
        FailTheReadOfTheFinishedJobOnce(h, log, "j1");
        var waits = RecordEveryWait(h);

        // One drive of the runner, as a submit starts it.
        await h.Service.DrainQueueAsync(CancellationToken.None);

        // Said once, as a notification failure, with the failed read's own exception.
        var said = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, said.Level);
        Assert.Equal("Completion notification for bulk job j1 failed (non-fatal)", said.Message);
        Assert.Contains("no such table: bulk_job", Assert.IsType<SqliteException>(said.Exception).Message);
        // j1's status stands as its final write left it: Completed, its rows, no message - nothing written over it. Its hook
        // did not run, the job it is given not having been read; the job behind it ran in the same drive, and its hook did.
        var j1 = h.Repository.Get("j1")!;
        Assert.Equal(BulkJobStatus.Completed, j1.Status);
        Assert.Equal(2, j1.SuccessCount);
        Assert.Null(j1.Message);
        Assert.Equal(BulkJobStatus.Completed, h.Repository.Get("j2")!.Status);
        Assert.Equal(new[] { 0, 1 }, h.CreatedProcessors.Select(p => p.CompletionCalls));
        // The store failed that one read and nothing else: the runner waited before no read of the queue and no write.
        lock (waits)
            Assert.Empty(waits);
    }

    [Fact]
    public async Task AReadOfAFaultedJobThatFails_AfterItsInterruptedStatusLands_DoesNotStopTheRunner()
    {
        var log = new CapturingLogger();
        using var h = new Harness(log);
        h.Repository.Insert(WithSubmitted(h.NewJob("j1", "SS"), new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));
        h.Repository.Insert(WithSubmitted(h.NewJob("j2", "S"), new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc)));
        // j1 faults - the store refuses its first row's record, which nothing in the job's own try takes - so the runner's
        // backstop finishes it Interrupted; the store takes that write, and fails the read of j1 right after it, once.
        FailRowWrites(h, "j1");
        FailTheReadOfTheFinishedJobOnce(h, log, "j1");
        var waits = RecordEveryWait(h);

        await h.Service.DrainQueueAsync(CancellationToken.None);

        // The runner carried on: the job behind it ran in the same drive, and its hook fired; j1's could not.
        Assert.Equal(BulkJobStatus.Completed, h.Repository.Get("j2")!.Status);
        Assert.Equal(new[] { 0, 1 }, h.CreatedProcessors.Select(p => p.CompletionCalls));
        // Said: the fault, and then the read, as a notification failure - each with the store's own exception.
        Assert.Collection(log.Entries,
            said =>
            {
                Assert.Equal(LogLevel.Error, said.Level);
                Assert.Equal("Bulk job j1 faulted unexpectedly", said.Message);
                Assert.Contains(RowWriteRefused, Assert.IsType<SqliteException>(said.Exception).Message);
            },
            said =>
            {
                Assert.Equal(LogLevel.Error, said.Level);
                Assert.Equal("Completion notification for bulk job j1 failed (non-fatal)", said.Message);
                Assert.Contains("no such table: bulk_job", Assert.IsType<SqliteException>(said.Exception).Message);
            });
        // j1's status stands as the backstop's write left it: Interrupted, with the fault, and no row recorded.
        var j1 = h.Repository.Get("j1")!;
        Assert.Equal(BulkJobStatus.Interrupted, j1.Status);
        Assert.Equal($"Job faulted: {log.Entries[0].Exception!.Message}", j1.Message);
        Assert.Equal(0, j1.ProcessedRows);
        lock (waits)
            Assert.Empty(waits);
    }

    // kd-84's stand-in for a store that fails one read: the read of a job just after its final status has landed. The jobs
    // table is renamed away (kd-82's stand-in) when JobChanged announces that status - the runner raises it between the
    // write and the read - and renamed back at the next line the runner logs, whatever that line says: so that read meets
    // "no such table", and every read and write after it is made as before.
    private static void FailTheReadOfTheFinishedJobOnce(Harness h, CapturingLogger log, string jobId)
    {
        var engaged = false;
        var away = false;
        h.Service.JobChanged += id =>
        {
            if (engaged || id != jobId || h.Repository.Get(jobId)?.IsTerminal != true)
                return;
            RenameJobsTable(h, "bulk_job", "bulk_job_held");
            engaged = away = true;
        };
        log.AfterEachLine = () =>
        {
            if (!away)
                return;
            RenameJobsTable(h, "bulk_job_held", "bulk_job");
            away = false;
        };
    }

    // A fault the job's own try does not take: a trigger that refuses one job's row records, so the runner's backstop
    // finishes the job, Interrupted with the store's message.
    private const string RowWriteRefused = "The store did not take the row.";

    private static void FailRowWrites(Harness h, string jobId) => ExecuteOnJobsStore(h,
        $"""
        CREATE TRIGGER kd84_fail_row BEFORE INSERT ON bulk_job_row
        WHEN NEW.job_id = '{jobId}'
        BEGIN SELECT RAISE(ABORT, '{RowWriteRefused}'); END;
        """);

    // Every wait the runner makes - before a read of the queue or a final status write - recorded and made at once, in
    // kd-83's recorder, which ends a runner past 50 waits so that one that never stops fails its test rather than hangs it.
    private static List<TimeSpan> RecordEveryWait(Harness h)
    {
        var waits = RecordWaitsBeforeFinalWrite(h);
        var record = h.Service.WaitBeforeFinalWrite;
        h.Service.WaitBeforeQueueRead = (delay, _) => record(delay);
        return waits;
    }

    // -------------------------------------------------------------------------
    // kd-85 (1): a queued job's cancel whose read of the job fails is answered as its write landed
    // -------------------------------------------------------------------------
    // TryCancelJob read the cancelled job back for its completion hook outside any try. A read the store failed, after it took
    // the cancel, threw out of CancelJob and CancelOwnedJob though the job was cancelled, and every caller files a throw as a
    // cancel that failed: Admin Bulk Jobs and Conference Rooms audited it Failed with the store's message and threw on to the
    // circuit, and Comms-10k said the job action failed. The read is now in one try with the hook, as the runner's tail has
    // it (kd-84): its failure is logged as a notification failure, the hook is skipped, and the cancel is answered as its
    // write landed.

    [Theory]
    [InlineData(nameof(BulkJobService.CancelJob))]
    [InlineData(nameof(BulkJobService.CancelOwnedJob))]
    public void ACancelWhoseReadOfTheJobFails_IsAnsweredAsItsWriteLanded_AndIsANotificationFailure(string entryPoint)
    {
        var log = new CapturingLogger();
        using var h = new Harness(log);
        h.Repository.Insert(h.NewJob("j1", "SS"));
        // The store takes the cancel - j1 finished Cancelled from Queued - and fails the read of j1 right after it, once.
        FailTheReadOfTheFinishedJobOnce(h, log, "j1");

        // Admin Bulk Jobs and Conference Rooms cancel through CancelJob, Comms-10k through CancelOwnedJob, and each files
        // and shows the cancel as this answers it.
        var cancelled = entryPoint == nameof(BulkJobService.CancelJob)
            ? h.Service.CancelJob("j1")
            : h.Service.CancelOwnedJob("Test", "T", "u", "j1", out _);

        // Answered as the write landed: the job was cancelled.
        Assert.True(cancelled);
        // Said once, as a notification failure, with the failed read's own exception.
        var said = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, said.Level);
        Assert.Equal("Completion notification for bulk job j1 failed (non-fatal)", said.Message);
        Assert.Contains("no such table: bulk_job", Assert.IsType<SqliteException>(said.Exception).Message);
        // The cancel stands as its write left it, and the job never ran.
        var j1 = h.Repository.Get("j1")!;
        Assert.Equal(BulkJobStatus.Cancelled, j1.Status);
        Assert.Equal("Cancelled before it started.", j1.Message);
        Assert.Equal(0, j1.ProcessedRows);
        // The hook was skipped, the job it is given not having been read: no processor was built for it.
        lock (h.CreatedProcessors)
            Assert.Empty(h.CreatedProcessors);
    }

    // -------------------------------------------------------------------------
    // kd-85 (2): the start-up sweep's notifications go on past a read that fails
    // -------------------------------------------------------------------------
    // InitializeAsync marks every unfinished job Interrupted and then, off the start-up path, reads each back for its
    // module's completion hook. The read was outside any try, so the first one the store failed ended the loop: the jobs
    // after it got no completion mail, and the task's exception was never observed - nothing was logged. Each job's read and
    // hook now have a try of their own: a read that fails is logged once, as a failed completion notification, and the sweep
    // goes on to the next job.

    [Fact]
    public async Task AStartUpSweepReadThatFails_IsANotificationFailure_AndTheNextInterruptedJobIsStillNotified()
    {
        var log = new CapturingLogger();
        using var h = new Harness(log);
        // Two jobs a restart finds unfinished; j1 is the older, so the sweep reads it back first.
        h.Repository.Insert(WithSubmitted(h.NewJob("j1", "SS", "Running"), new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));
        h.Repository.Insert(WithSubmitted(h.NewJob("j2", "S"), new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc)));
        // The store takes the sweep's Interrupted write, and fails its read of j1 right after it, once.
        FailTheSweepsFirstReadOnce(h, log);

        h.Service.InitializeAsync();

        // The notifications run off the start-up path, with no handle to await: wait up to 30 s for a hook to run - j2's, the
        // only one the sweep can run - and then look.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (HookedJobs(h).Length == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        // j2 was notified, given the job as the sweep read it back: Interrupted. j1 was not, its read having failed, and no
        // processor was built for it.
        Assert.Equal(new[] { ("j2", BulkJobStatus.Interrupted) }, HookedJobs(h));
        lock (h.CreatedProcessors)
            Assert.Single(h.CreatedProcessors);
        // Said: the sweep's own line, then the failed read, once, as a notification failure with its own exception.
        Assert.Collection(log.Entries,
            said =>
            {
                Assert.Equal(LogLevel.Warning, said.Level);
                Assert.Equal("Reconciled 2 orphaned bulk job(s) to Interrupted on startup", said.Message);
            },
            said =>
            {
                Assert.Equal(LogLevel.Error, said.Level);
                Assert.Equal("Completion notification for bulk job j1 failed (non-fatal)", said.Message);
                Assert.Contains("no such table: bulk_job", Assert.IsType<SqliteException>(said.Exception).Message);
            });
        // Both marked Interrupted, as the sweep always has: the failed read changed nothing in the store.
        Assert.All(new[] { "j1", "j2" }, id =>
        {
            var job = h.Repository.Get(id)!;
            Assert.Equal(BulkJobStatus.Interrupted, job.Status);
            Assert.Equal("Interrupted by application restart.", job.Message);
        });
    }

    // kd-85's stand-in for a store that fails the start-up sweep's first read back: kd-84's, engaged at the sweep's own line
    // rather than at JobChanged, which the sweep does not raise - it says how many jobs it marked Interrupted once it has,
    // and before it reads any back. The jobs table is renamed away at that line and renamed back at the next, whatever it
    // says: so the first read back meets "no such table", and every read after it is made as before.
    private static void FailTheSweepsFirstReadOnce(Harness h, CapturingLogger log)
    {
        var engaged = false;
        var away = false;
        log.AfterEachLine = () =>
        {
            if (away)
            {
                RenameJobsTable(h, "bulk_job_held", "bulk_job");
                away = false;
            }
            else if (!engaged && log.Entries.Last().Message.StartsWith("Reconciled ", StringComparison.Ordinal))
            {
                RenameJobsTable(h, "bulk_job", "bulk_job_held");
                engaged = away = true;
            }
        };
    }

    // The job each completion hook was given, across every processor the harness built, in the order they were built.
    private static (string Id, BulkJobStatus Status)[] HookedJobs(Harness h)
    {
        lock (h.CreatedProcessors)
        {
            return h.CreatedProcessors.SelectMany(p =>
            {
                lock (p.CompletedJobs)
                    return p.CompletedJobs.ToArray();
            }).ToArray();
        }
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
