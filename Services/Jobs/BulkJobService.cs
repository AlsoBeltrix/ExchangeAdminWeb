using Microsoft.Extensions.DependencyInjection;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The self-pumping singleton runner for durable bulk jobs (docs/BulkJobRunner-Plan.md).
///
/// This is NOT a hosted background timer. The 2026-06-17 decision removed the app's only
/// AddHostedService because a clock-driven worker mutated AD unattended under a synthetic actor.
/// This runner is different in kind: it does nothing on a schedule, only in response to an
/// operator submitting a job. Every job carries a real submitter, ticket and IP and is fully
/// audited per row (in the processor). On startup it does exactly one thing — reconcile orphaned
/// jobs — via an explicit <see cref="InitializeAsync"/> call, not a timer.
///
/// Concurrency: exactly one job Running at a time; further submissions are Queued (FIFO) and
/// promoted in-process when the head finishes. All Exchange/AD work funnels through the single
/// <c>ExoConnectionPool</c>, so running two big batches at once would only fight slots and invite
/// throttling. Queue promotion happens ONLY within a live process — never across a restart (there
/// is no resume; startup flips every non-terminal job to Interrupted).
///
/// The runner is module-agnostic: it resolves the per-module <see cref="IBulkJobProcessor"/> from a
/// fresh DI scope per job, so scoped module services (e.g. ConferenceRoomService) live and die with
/// the job even though this runner is a singleton.
/// </summary>
public sealed class BulkJobService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BulkJobRepository _repository;
    private readonly BulkJobProcessorRegistry _registry;
    private readonly ILogger<BulkJobService> _logger;
    private readonly TimeSpan _staleHeartbeat;
    private readonly int _recentJobLimit;
    private readonly TimeSpan _pruneRetention;

    // Guards the pump-running flag and the in-process "start the pump" decision so exactly one pump
    // loop runs at a time (which in turn guarantees exactly one job runs at a time).
    private readonly object _gate = new();
    private bool _pumpRunning;

    // Cancellation tokens for currently-executing jobs, keyed by job id. A cancel from any circuit
    // signals the running job's token; the runner also re-checks the persisted flag per row.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    /// <summary>
    /// Raised (with the affected job id) whenever a job's persisted state changes — enqueue, start,
    /// each row recorded, terminal. A watching Blazor circuit subscribes and re-reads from the store
    /// to refresh its view; no live per-step push is needed (poll-on-event is enough — plan §UI).
    /// </summary>
    public event Action<string>? JobChanged;

    public BulkJobService(
        IServiceScopeFactory scopeFactory,
        BulkJobRepository repository,
        BulkJobProcessorRegistry registry,
        IConfiguration config,
        ILogger<BulkJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _repository = repository;
        _registry = registry;
        _logger = logger;

        _staleHeartbeat = TimeSpan.FromMinutes(config.GetValue<double?>("BulkJobs:StaleHeartbeatMinutes") ?? 5);
        _recentJobLimit = config.GetValue<int?>("BulkJobs:RecentJobLimit") ?? 25;
        _pruneRetention = TimeSpan.FromDays(config.GetValue<double?>("BulkJobs:RetentionDays") ?? 30);
    }

    // -------------------------------------------------------------------------
    // Startup — explicit, one-shot. NOT a timer (see class remarks).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called once at startup from Program.cs (alongside the config migrator / enablement seeding),
    /// before the app serves requests. Migrates the jobs database, prunes old terminal jobs, and —
    /// the load-bearing anti-brittleness rule — flips EVERY non-terminal job (Running OR Queued) to
    /// Interrupted. There is no resume; an interrupted job is a truthful record an operator can
    /// inspect and re-submit, never something that silently resumes or sits stuck "Running".
    /// </summary>
    public void InitializeAsync()
    {
        new JobStoreMigrator(new Storage.SqliteConnectionFactory(_repository.DatabasePath)).Migrate();

        try
        {
            var pruned = _repository.PruneFinishedBefore(DateTime.UtcNow - _pruneRetention);
            if (pruned > 0)
                _logger.LogInformation("Pruned {Count} finished bulk job(s) older than {Days}d", pruned, _pruneRetention.TotalDays);
        }
        catch (Exception ex)
        {
            // Pruning is housekeeping; never block startup on it.
            _logger.LogWarning(ex, "Bulk job prune at startup failed (non-fatal)");
        }

        var interrupted = _repository.GetActive().ToList();
        var flipped = _repository.InterruptAllNonTerminal("Interrupted by application restart.");
        if (flipped > 0)
        {
            _logger.LogWarning("Reconciled {Count} orphaned bulk job(s) to Interrupted on startup", flipped);
            // Fire completion notifications for interrupted jobs off the startup path (fire-and-forget,
            // fully guarded) so a closed tab still yields an email, without adding email latency/
            // failure surface to app startup itself.
            _ = Task.Run(() =>
            {
                foreach (var job in interrupted)
                {
                    var reloaded = _repository.Get(job.Id);
                    if (reloaded is not null)
                        NotifyCompleted(reloaded);
                }
            });
        }
    }

    // -------------------------------------------------------------------------
    // Submission + queue control
    // -------------------------------------------------------------------------

    /// <summary>
    /// Persists a new job as Queued and kicks the pump. Returns immediately with the job id — the
    /// batch runs server-side, independent of the submitting browser connection. If the pump is
    /// idle it is started on a background task; if a job is already running, this job waits its FIFO
    /// turn.
    /// </summary>
    public string Enqueue(BulkJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.Status = BulkJobStatus.Queued;
        _repository.Insert(job);
        RaiseChanged(job.Id);
        EnsurePump();
        return job.Id;
    }

    /// <summary>
    /// Cancels a job whether it is Queued or Running, with one entry point (robust against the race
    /// where a job starts between the operator seeing it queued and clicking). A still-Queued job is
    /// finished immediately as Cancelled and never runs; a Running job's token is signalled so it
    /// stops before its next row (an in-flight Exchange cmdlet cannot be aborted mid-call — see plan).
    /// </summary>
    public void CancelJob(string id)
    {
        // Queued → terminal immediately (compare-and-swap from Queued; no-op if already started).
        // This job never enters the runner, so its completion notification is fired here (a queued
        // cancel is still a terminal state and must notify — plan §Audit/notification fidelity).
        if (_repository.TryFinish(id, BulkJobStatus.Cancelled, "Cancelled before it started.", DateTime.UtcNow, BulkJobStatus.Queued))
        {
            RaiseChanged(id);
            var cancelledJob = _repository.Get(id);
            if (cancelledJob is not null)
                NotifyCompleted(cancelledJob);
            return;
        }

        // Otherwise running (or already terminal): set the persisted flag and signal the token. The
        // runner observes it, stops before its next row, and fires the completion notification from
        // its own terminal tail — so do NOT notify here (would duplicate).
        _repository.RequestCancel(id);
        if (_running.TryGetValue(id, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* finished concurrently */ }
        }
        RaiseChanged(id);
    }

    // -------------------------------------------------------------------------
    // Read API (wraps the repository; adds Stalled display classification)
    // -------------------------------------------------------------------------

    public BulkJob? GetJob(string id) => _repository.Get(id);
    public IReadOnlyList<BulkJob> GetActiveJobs() => _repository.GetActive();
    public IReadOnlyList<BulkJob> GetRecentJobs() => _repository.GetRecentFinished(_recentJobLimit);
    public IReadOnlyList<BulkJobRow> GetRows(string id) => _repository.GetRows(id);

    /// <summary>
    /// Display classification: a Running job whose heartbeat is older than the stale threshold is
    /// surfaced as "Stalled" (its worker may be wedged mid-cmdlet — see plan). Stalled is never a
    /// stored state; it is derived here purely for the UI. All other statuses map to their name.
    /// </summary>
    public string DisplayStatus(BulkJob job, DateTime nowUtc)
    {
        if (job.Status == BulkJobStatus.Running
            && job.HeartbeatAtUtc is { } hb
            && nowUtc - hb > _staleHeartbeat)
        {
            return "Stalled";
        }
        return job.Status.ToString();
    }

    public bool IsStalled(BulkJob job, DateTime nowUtc) => DisplayStatus(job, nowUtc) == "Stalled";

    // -------------------------------------------------------------------------
    // Pump — single loop, drains the FIFO queue one job at a time
    // -------------------------------------------------------------------------

    private void EnsurePump()
    {
        bool start = false;
        lock (_gate)
        {
            if (!_pumpRunning)
            {
                _pumpRunning = true;
                start = true;
            }
        }
        if (start)
            _ = Task.Run(() => DrainQueueAsync(CancellationToken.None));
    }

    /// <summary>
    /// Processes queued jobs to completion, one at a time, oldest first, until none remain. The
    /// pump-running flag is cleared atomically with the "no more work" check so a concurrent
    /// Enqueue either restarts the pump or is picked up by this loop. The flag is ALSO cleared in a
    /// finally so a fault can never leave the pump wedged "running" (which would silently stop every
    /// future job) — anti-brittleness rule. <see cref="RunJobAsync"/> is itself total (never throws),
    /// so the loop's only normal exit is the no-work path. Exposed internally so tests drive it
    /// deterministically instead of racing the background task.
    /// </summary>
    internal async Task DrainQueueAsync(CancellationToken shutdown)
    {
        try
        {
            while (true)
            {
                BulkJob? next;
                lock (_gate)
                {
                    next = _repository.GetActive().FirstOrDefault(j => j.Status == BulkJobStatus.Queued);
                    if (next is null)
                    {
                        _pumpRunning = false;
                        return;
                    }
                }

                await RunJobAsync(next, shutdown);
            }
        }
        catch (Exception ex)
        {
            // Defensive: RunJobAsync is designed not to throw, but if anything escapes (e.g. the
            // repository read at the top of the loop), clear the flag so a later Enqueue can restart
            // the pump rather than the app silently never running another job.
            _logger.LogError(ex, "Bulk job pump loop faulted");
            lock (_gate) { _pumpRunning = false; }
        }
    }

    /// <summary>
    /// Runs one job to a terminal state. TOTAL by contract: it always drives the job to a terminal
    /// transition and fires the completion notification exactly once, and never throws — so the pump
    /// loop cannot die and a job can never be left stuck Running. Every terminal path (normal
    /// completion, cancel, unregistered processor, payload failure, unexpected fault) routes through
    /// the single finish-and-notify tail.
    /// </summary>
    private async Task RunJobAsync(BulkJob job, CancellationToken shutdown)
    {
        using var scope = _scopeFactory.CreateScope();
        var processor = ResolveProcessor(scope, job.ModuleId);
        bool startedThisJob = false;

        try
        {
            if (processor is null)
            {
                _logger.LogError("No bulk job processor registered for module {Module}; marking job {Job} Interrupted",
                    job.ModuleId, job.Id);
                FinishAndNotify(scope, job.Id, BulkJobStatus.Interrupted,
                    $"No processor registered for module '{job.ModuleId}'.",
                    BulkJobStatus.Queued, BulkJobStatus.Running);
                return;
            }

            int total;
            try
            {
                total = processor.CountRows(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk job {Job} payload could not be parsed", job.Id);
                FinishAndNotify(scope, job.Id, BulkJobStatus.Completed,
                    $"Job could not start: {ex.Message}",
                    BulkJobStatus.Queued, BulkJobStatus.Running);
                return;
            }

            if (!_repository.TryStart(job.Id, total, DateTime.UtcNow))
            {
                // Lost the race: the job was cancelled/removed while queued and is already terminal.
                // CancelJob owns that job's notification, so do NOT notify again here.
                RaiseChanged(job.Id);
                return;
            }
            startedThisJob = true;
            RaiseChanged(job.Id);

            var (terminal, message) = await ExecuteRowsAsync(job, processor, total, shutdown);
            FinishAndNotify(scope, job.Id, terminal, message, BulkJobStatus.Running);
        }
        catch (Exception ex)
        {
            // Backstop: nothing above should escape, but if it does, the job must still reach a
            // terminal state (never left Running) and the pump must survive.
            _logger.LogError(ex, "Bulk job {Job} faulted unexpectedly", job.Id);
            if (startedThisJob)
                _running.TryRemove(job.Id, out _);
            FinishAndNotify(scope, job.Id, BulkJobStatus.Interrupted,
                $"Job faulted: {ex.Message}", BulkJobStatus.Queued, BulkJobStatus.Running);
        }
    }

    /// <summary>
    /// Executes the job's rows, aggregating per-row failures (one bad row never aborts the batch),
    /// and returns the terminal status + message. Cancellation is cooperative and checked before
    /// each row; an in-flight cmdlet cannot be aborted mid-call (documented limitation).
    /// </summary>
    private async Task<(BulkJobStatus terminal, string? message)> ExecuteRowsAsync(
        BulkJob job, IBulkJobProcessor processor, int total, CancellationToken shutdown)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        _running[job.Id] = cts;
        bool cancelled = false;
        try
        {
            for (var i = 0; i < total; i++)
            {
                // Cooperative cancel: honor both the in-process token and the persisted flag (a
                // cancel from another circuit sets the flag; the flag is the durable source of truth).
                if (cts.IsCancellationRequested || _repository.IsCancelRequested(job.Id))
                {
                    cancelled = true;
                    break;
                }

                BulkJobRowOutcome outcome;
                try
                {
                    outcome = await processor.ProcessRowAsync(job, i, cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested || _repository.IsCancelRequested(job.Id))
                {
                    // Genuine operator/shutdown cancellation — stop the batch.
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    // Any other exception (including a stray timeout/TaskCanceledException that is NOT
                    // our cancel) is a per-row failure, not a batch abort (Known Failure Class #2).
                    _logger.LogError(ex, "Bulk job {Job} row {Row} threw", job.Id, i);
                    outcome = new BulkJobRowOutcome
                    {
                        Target = $"row {i}",
                        Status = BulkJobRowStatus.Failed,
                        Message = ex.Message
                    };
                }

                _repository.RecordRow(new BulkJobRow
                {
                    JobId = job.Id,
                    RowIndex = i,
                    Target = outcome.Target,
                    Status = outcome.Status,
                    Message = outcome.Message,
                    RecordedAtUtc = DateTime.UtcNow
                }, heartbeatUtc: DateTime.UtcNow);
                RaiseChanged(job.Id);
            }
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
        }

        return cancelled
            ? (BulkJobStatus.Cancelled, "Cancelled by operator.")
            : (BulkJobStatus.Completed, null);
    }

    /// <summary>
    /// Single terminal-transition-and-notify tail. Persists the terminal state (CAS from an allowed
    /// source), signals watchers, then fires the completion notification once with final counts. If
    /// the CAS did not fire (someone else already finished the job), the notification is skipped to
    /// avoid a duplicate — whoever won the transition owns the notification.
    /// </summary>
    private void FinishAndNotify(IServiceScope scope, string jobId, BulkJobStatus terminal,
        string? message, params BulkJobStatus[] allowedFrom)
    {
        var transitioned = _repository.TryFinish(jobId, terminal, message, DateTime.UtcNow, allowedFrom);
        RaiseChanged(jobId);
        if (!transitioned)
            return;

        var finalJob = _repository.Get(jobId);
        if (finalJob is not null)
            NotifyCompletedInScope(scope, finalJob);
    }

    private IBulkJobProcessor? ResolveProcessor(IServiceScope scope, string moduleId)
    {
        var type = _registry.GetProcessorType(moduleId);
        if (type is null)
            return null;
        return scope.ServiceProvider.GetService(type) as IBulkJobProcessor;
    }

    /// <summary>Fires the processor's completion hook in a fresh scope, swallowing errors (fail-safe).</summary>
    private void NotifyCompleted(BulkJob job)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            NotifyCompletedInScope(scope, job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Completion notification for bulk job {Job} failed (non-fatal)", job.Id);
        }
    }

    private void NotifyCompletedInScope(IServiceScope scope, BulkJob job)
    {
        var processor = ResolveProcessor(scope, job.ModuleId);
        if (processor is null)
            return;
        try
        {
            // Fail-safe: a notification failure must never change the (already-persisted) job result.
            processor.OnJobCompletedAsync(job).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Completion notification for bulk job {Job} failed (non-fatal)", job.Id);
        }
    }

    private void RaiseChanged(string jobId)
    {
        try { JobChanged?.Invoke(jobId); }
        catch (Exception ex) { _logger.LogWarning(ex, "A JobChanged subscriber threw (ignored)"); }
    }
}
