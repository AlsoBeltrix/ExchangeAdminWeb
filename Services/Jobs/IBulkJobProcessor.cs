namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The seam between the module-agnostic <see cref="BulkJobService"/> runner and a module's
/// per-row work. A module (ConferenceRooms is the first caller) implements this to turn a job's
/// opaque <see cref="BulkJob.PayloadJson"/> into a row count and to process one row at a time.
/// The runner owns lifecycle, queueing, persistence and cancellation; the processor owns only
/// "what does one row do", so the runner can be unit-tested against a substitute with no live EXO.
///
/// Registered per module id (see <see cref="BulkJobProcessorRegistry"/>). Resolved inside a fresh
/// DI scope per job by the runner, so a processor may safely depend on scoped services
/// (e.g. ConferenceRoomService) even though the runner itself is a singleton.
/// </summary>
public interface IBulkJobProcessor
{
    /// <summary>The module id this processor handles (matches <see cref="BulkJob.ModuleId"/>).</summary>
    string ModuleId { get; }

    /// <summary>
    /// Parses the job's payload and returns the total number of rows to process. Called once when
    /// the job starts, before any row runs, so the runner can stamp the row count and drive
    /// progress. Throwing here fails the whole job (payload could not be understood).
    /// </summary>
    int CountRows(BulkJob job);

    /// <summary>
    /// Processes a single row (0-based <paramref name="rowIndex"/>) of the job and returns its
    /// durable outcome. The processor performs its own per-row authorization, protected-principal
    /// gate, Exchange/AD work, audit and trace - all using the captured submission context on
    /// <paramref name="job"/> (submitter, ip, ticket, auth snapshot), NOT any ambient circuit,
    /// which no longer exists off-connection.
    ///
    /// <paramref name="cancellationToken"/> is the cooperative cancel signal. The runner checks it
    /// between rows; a long-running processor may also observe it, but an in-flight Exchange call
    /// cannot be aborted mid-cmdlet (documented limitation - see docs/BulkJobRunner-Plan.md).
    /// A processor should let a genuine failure surface as a Failed/Partial <see cref="BulkJobRowOutcome"/>
    /// rather than throwing; a throw is caught by the runner and recorded as a Failed row so one bad
    /// row never aborts the batch.
    /// </summary>
    Task<BulkJobRowOutcome> ProcessRowAsync(BulkJob job, int rowIndex, CancellationToken cancellationToken);

    /// <summary>
    /// Called once by the runner when a job reaches a terminal state (Completed / Cancelled /
    /// Interrupted). This is where a module sends its completion admin notification, moving the
    /// email off the (now-closed) browser circuit into the job. <paramref name="job"/> carries the
    /// final aggregated counts. Default no-op so processors that need no completion hook opt out.
    /// Per the fail-safe rule, a notification failure here must not change the job result - the
    /// runner has already persisted the terminal state before calling this, and swallows throws.
    /// </summary>
    Task OnJobCompletedAsync(BulkJob job) => Task.CompletedTask;
}

/// <summary>The outcome of one processed row, returned by <see cref="IBulkJobProcessor.ProcessRowAsync"/>.</summary>
public sealed class BulkJobRowOutcome
{
    /// <summary>The row's target (room email / identity) for the durable row record and the UI table.</summary>
    public required string Target { get; init; }

    public required BulkJobRowStatus Status { get; init; }

    /// <summary>Operator-facing outcome message (same text the live page shows today).</summary>
    public string? Message { get; init; }
}
