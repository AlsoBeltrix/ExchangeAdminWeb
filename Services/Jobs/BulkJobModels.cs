namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// Lifecycle state of a bulk job. Only these values are persisted. "Stalled" is intentionally
/// NOT here: it is a display-only classification derived from a stale <see cref="BulkJob.HeartbeatAtUtc"/>
/// (see <c>BulkJobService</c>), never a stored terminal state — a job whose worker is wedged
/// mid-call is still <see cref="Running"/> in the store and only surfaces as stalled in the UI.
/// See docs/BulkJobRunner-Plan.md (Anti-brittleness).
/// </summary>
public enum BulkJobStatus
{
    /// <summary>Submitted, waiting behind the single active job (FIFO). Payload persisted.</summary>
    Queued,

    /// <summary>Currently executing on the singleton runner. Exactly one job is Running at a time.</summary>
    Running,

    /// <summary>Ran to the end of its rows and reached a terminal state (rows may individually have failed).</summary>
    Completed,

    /// <summary>Cancelled by an operator (running job) or removed while Queued.</summary>
    Cancelled,

    /// <summary>
    /// Non-terminal at shutdown (Running OR Queued) and flipped by startup orphan reconciliation.
    /// There is no resume-after-recycle (owner direction); an interrupted job is a truthful record
    /// an operator can inspect and re-submit, never auto-run.
    /// </summary>
    Interrupted
}

/// <summary>Outcome of a single row within a job. Mirrors the module's Success/Partial/Failed result shape.</summary>
public enum BulkJobRowStatus
{
    Success,
    Partial,
    Failed
}

/// <summary>
/// A durable bulk job. Module-agnostic: <see cref="ModuleId"/> and <see cref="JobType"/> identify
/// the caller, and <see cref="PayloadJson"/> carries the caller's opaque per-row input (so a queued
/// job is a real, inspectable record and the row input is auditable). The runner and per-row work
/// live behind the module's own processor seam; this record knows nothing about rooms.
/// </summary>
public sealed class BulkJob
{
    /// <summary>Opaque unique id (GUID "N"). Assigned at enqueue.</summary>
    public required string Id { get; init; }

    /// <summary>Owning module (e.g. "ConferenceRooms"). Used for display and audit routing.</summary>
    public required string ModuleId { get; init; }

    /// <summary>Caller-defined job kind (e.g. "SetMetadata_Bulk", "SetType_Bulk"). Drives trace action + per-row dispatch.</summary>
    public required string JobType { get; init; }

    public BulkJobStatus Status { get; set; }

    // --- Captured submission context (option (a) authorization — see plan). The live circuit is
    // gone once the job runs, so who/ip/ticket and the authorization snapshot are captured at
    // submit time and re-used per row. ---

    /// <summary>Submitter SAM account (authoritative actor for per-row audit).</summary>
    public required string SubmittedBy { get; init; }

    /// <summary>Friendly display name for the UI job list (falls back to SAM).</summary>
    public string? SubmittedByDisplay { get; init; }

    /// <summary>Submitter IP captured on the circuit (per-row audit fidelity off-connection).</summary>
    public required string SubmittedIp { get; init; }

    /// <summary>ServiceNow ticket (or other change reference) captured at submit.</summary>
    public string? Ticket { get; init; }

    /// <summary>
    /// JSON snapshot of the submitter's authorization decision + role claims, captured on the
    /// circuit where the principal is present. Re-evaluated per row against the section's group set
    /// (option (a)). Does not detect mid-job group-membership revocation — matches today's model.
    /// </summary>
    public string? AuthSnapshotJson { get; init; }

    /// <summary>Opaque caller payload (parsed CSV rows + parameters), deserialized by the module processor.</summary>
    public required string PayloadJson { get; init; }

    // --- Timestamps (ISO-8601 "O", UTC). ---
    public required DateTime SubmittedAtUtc { get; init; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Stamped as each row completes; staleness beyond a threshold surfaces the job as Stalled in the UI.</summary>
    public DateTime? HeartbeatAtUtc { get; set; }

    // --- Progress / aggregation. ---
    public int TotalRows { get; set; }
    public int ProcessedRows { get; set; }
    public int SuccessCount { get; set; }
    public int PartialCount { get; set; }
    public int FailedCount { get; set; }

    /// <summary>Cooperative cancel flag; the runner checks it before each row.</summary>
    public bool CancelRequested { get; set; }

    /// <summary>Optional terminal-state message (e.g. reconciliation note).</summary>
    public string? Message { get; set; }

    /// <summary>True for any state a job cannot leave on its own (no resume — owner direction).</summary>
    public bool IsTerminal => Status is BulkJobStatus.Completed or BulkJobStatus.Cancelled or BulkJobStatus.Interrupted;
}

/// <summary>A single persisted per-row outcome. Only row-level outcome is durable; live grey step
/// detail stays in memory on the watching circuit (keeps the durable footprint small — see plan).</summary>
public sealed class BulkJobRow
{
    public required string JobId { get; init; }
    public required int RowIndex { get; init; }
    public required string Target { get; init; }
    public required BulkJobRowStatus Status { get; init; }
    public string? Message { get; init; }
    public DateTime RecordedAtUtc { get; init; }
}
