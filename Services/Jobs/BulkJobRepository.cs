using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// Thin hand-written repository for the operational jobs database (Microsoft.Data.Sqlite, no EF),
/// mirroring the Services/Storage/*Repository pattern. Opens a short-lived connection per operation
/// via <see cref="SqliteConnectionFactory"/>. Constructed with a jobs-specific factory (its own
/// exchangeadmin-jobs.db), NOT the config-store factory — the two databases are deliberately
/// separate (docs/BulkJobRunner-Plan.md).
/// </summary>
public sealed class BulkJobRepository
{
    private readonly SqliteConnectionFactory _factory;

    public BulkJobRepository(SqliteConnectionFactory factory) => _factory = factory;

    public string DatabasePath => _factory.DatabasePath;

    // -------------------------------------------------------------------------
    // Writes
    // -------------------------------------------------------------------------

    /// <summary>Inserts a newly submitted job (status already set on the object, typically Queued).</summary>
    public void Insert(BulkJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO bulk_job (
                id, module_id, job_type, status, submitted_by, submitted_by_display, submitted_ip,
                ticket, auth_snapshot_json, payload_json, submitted_at, started_at, finished_at,
                heartbeat_at, total_rows, processed_rows, success_count, partial_count, failed_count,
                cancel_requested, message)
            VALUES (
                $id, $module, $type, $status, $by, $byDisplay, $ip,
                $ticket, $auth, $payload, $submitted, $started, $finished,
                $heartbeat, $total, $processed, $success, $partial, $failed,
                $cancel, $message);
            """;
        BindFullJob(command, job);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    // Job state changes are deliberately expressed as narrow, guarded operations rather than a
    // single blind "write the whole object back". A job is mutated from more than one place — the
    // runner on its worker thread, an operator's cancel from a different circuit, and startup
    // reconciliation — so overwriting every column from one caller's stale copy could silently drop
    // a concurrent cancel or resurrect a terminal job (codex review 2026-07-02). Every transition
    // below is compare-and-swap on the current status; aggregate counts are derived from the rows
    // table (single source of truth) so they can never drift from the actual row outcomes.

    /// <summary>
    /// Atomically promotes a Queued job to Running, stamping total-rows, started-at and the first
    /// heartbeat. Compare-and-swap: only fires when the job is still Queued, so a job already
    /// started, cancelled, or interrupted is not disturbed. Returns true if this call started it.
    /// </summary>
    public bool TryStart(string id, int totalRows, DateTime nowUtc)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE bulk_job
            SET status = $running, total_rows = $total, started_at = $now, heartbeat_at = $now
            WHERE id = $id AND status = $queued;
            """;
        command.Parameters.AddWithValue("$running", BulkJobStatus.Running.ToString());
        command.Parameters.AddWithValue("$total", totalRows);
        command.Parameters.AddWithValue("$now", Iso(nowUtc));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$queued", BulkJobStatus.Queued.ToString());
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Requests cancellation of a still-active job (Queued or Running). Targeted write of the flag
    /// only — never overwrites status or any other field, so it cannot race with the runner's own
    /// updates. The runner observes the flag and stops before its next row; a Queued job is finished
    /// as Cancelled by the caller via <see cref="TryFinish"/>. No-op on a terminal job.
    /// </summary>
    public void RequestCancel(string id)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE bulk_job SET cancel_requested = 1 WHERE id = $id AND status IN ($queued, $running);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$queued", BulkJobStatus.Queued.ToString());
        command.Parameters.AddWithValue("$running", BulkJobStatus.Running.ToString());
        command.ExecuteNonQuery();
    }

    /// <summary>Reads just the cancel flag (cheap; the runner checks it before each row).</summary>
    public bool IsCancelRequested(string id)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cancel_requested FROM bulk_job WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        var raw = command.ExecuteScalar();
        return raw is not null and not DBNull && Convert.ToInt64(raw) != 0;
    }

    /// <summary>
    /// Records one row's outcome AND the job's aggregate progress in a SINGLE transaction, so a
    /// crash can never leave durable rows inconsistent with processed/success/partial/failed counts
    /// (codex review 2026-07-02). The aggregates are recomputed from the rows table itself, making
    /// the rows the single source of truth; the heartbeat is stamped in the same unit. Row upsert is
    /// idempotent on (job_id, row_index) so re-recording a row is safe.
    /// </summary>
    public void RecordRow(BulkJobRow row, DateTime heartbeatUtc)
    {
        ArgumentNullException.ThrowIfNull(row);
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO bulk_job_row (job_id, row_index, target, status, message, recorded_at)
                VALUES ($job, $index, $target, $status, $message, $recorded)
                ON CONFLICT(job_id, row_index) DO UPDATE SET
                    target = excluded.target,
                    status = excluded.status,
                    message = excluded.message,
                    recorded_at = excluded.recorded_at;
                """;
            upsert.Parameters.AddWithValue("$job", row.JobId);
            upsert.Parameters.AddWithValue("$index", row.RowIndex);
            upsert.Parameters.AddWithValue("$target", row.Target);
            upsert.Parameters.AddWithValue("$status", row.Status.ToString());
            upsert.Parameters.AddWithValue("$message", (object?)row.Message ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$recorded", Iso(row.RecordedAtUtc));
            upsert.ExecuteNonQuery();
        }

        using (var aggregate = connection.CreateCommand())
        {
            aggregate.Transaction = transaction;
            aggregate.CommandText =
                """
                UPDATE bulk_job SET
                    heartbeat_at = $hb,
                    processed_rows = (SELECT COUNT(*) FROM bulk_job_row WHERE job_id = $job),
                    success_count = (SELECT COUNT(*) FROM bulk_job_row WHERE job_id = $job AND status = $success),
                    partial_count = (SELECT COUNT(*) FROM bulk_job_row WHERE job_id = $job AND status = $partial),
                    failed_count  = (SELECT COUNT(*) FROM bulk_job_row WHERE job_id = $job AND status = $failed)
                WHERE id = $job;
                """;
            aggregate.Parameters.AddWithValue("$hb", Iso(heartbeatUtc));
            aggregate.Parameters.AddWithValue("$job", row.JobId);
            aggregate.Parameters.AddWithValue("$success", BulkJobRowStatus.Success.ToString());
            aggregate.Parameters.AddWithValue("$partial", BulkJobRowStatus.Partial.ToString());
            aggregate.Parameters.AddWithValue("$failed", BulkJobRowStatus.Failed.ToString());
            aggregate.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Moves a job to a terminal state (Completed / Cancelled / Interrupted), stamping finished-at
    /// and an optional message. Compare-and-swap on <paramref name="allowedFrom"/>: the transition
    /// only fires from an expected non-terminal state, so it can neither overwrite a state set by a
    /// concurrent actor nor resurrect an already-terminal job. Returns true if it transitioned.
    /// </summary>
    public bool TryFinish(string id, BulkJobStatus terminal, string? message, DateTime finishedUtc,
        params BulkJobStatus[] allowedFrom)
    {
        if (allowedFrom is null || allowedFrom.Length == 0)
            throw new ArgumentException("At least one allowed source status is required.", nameof(allowedFrom));

        using var connection = _factory.Open();
        using var command = connection.CreateCommand();

        var placeholders = new string[allowedFrom.Length];
        for (var i = 0; i < allowedFrom.Length; i++)
        {
            placeholders[i] = $"$from{i}";
            command.Parameters.AddWithValue($"$from{i}", allowedFrom[i].ToString());
        }

        command.CommandText =
            $"""
            UPDATE bulk_job
            SET status = $terminal, finished_at = $now, message = COALESCE($message, message)
            WHERE id = $id AND status IN ({string.Join(", ", placeholders)});
            """;
        command.Parameters.AddWithValue("$terminal", terminal.ToString());
        command.Parameters.AddWithValue("$now", Iso(finishedUtc));
        command.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Flips every non-terminal job (Queued OR Running) to Interrupted in one transaction. Called
    /// once at startup (orphan reconciliation): there is no resume, so any job left mid-flight by a
    /// recycle becomes a truthful Interrupted record. Returns the number of jobs flipped.
    /// </summary>
    public int InterruptAllNonTerminal(string message)
    {
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE bulk_job
            SET status = $interrupted,
                finished_at = COALESCE(finished_at, $now),
                message = COALESCE(message, $message)
            WHERE status IN ($queued, $running);
            """;
        command.Parameters.AddWithValue("$interrupted", BulkJobStatus.Interrupted.ToString());
        command.Parameters.AddWithValue("$now", Iso(DateTime.UtcNow));
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$queued", BulkJobStatus.Queued.ToString());
        command.Parameters.AddWithValue("$running", BulkJobStatus.Running.ToString());
        var affected = command.ExecuteNonQuery();
        transaction.Commit();
        return affected;
    }

    /// <summary>Deletes terminal jobs finished before <paramref name="cutoffUtc"/>. Rows cascade. Returns jobs pruned.</summary>
    public int PruneFinishedBefore(DateTime cutoffUtc)
    {
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM bulk_job
            WHERE status IN ($completed, $cancelled, $interrupted)
              AND finished_at IS NOT NULL
              AND finished_at < $cutoff;
            """;
        command.Parameters.AddWithValue("$completed", BulkJobStatus.Completed.ToString());
        command.Parameters.AddWithValue("$cancelled", BulkJobStatus.Cancelled.ToString());
        command.Parameters.AddWithValue("$interrupted", BulkJobStatus.Interrupted.ToString());
        command.Parameters.AddWithValue("$cutoff", Iso(cutoffUtc));
        var affected = command.ExecuteNonQuery();
        transaction.Commit();
        return affected;
    }

    // -------------------------------------------------------------------------
    // Reads
    // -------------------------------------------------------------------------

    public BulkJob? Get(string id)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    /// <summary>All non-terminal jobs (Queued + Running), oldest submission first (FIFO order).</summary>
    public IReadOnlyList<BulkJob> GetActive()
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns +
            " WHERE status IN ($queued, $running) ORDER BY submitted_at ASC;";
        command.Parameters.AddWithValue("$queued", BulkJobStatus.Queued.ToString());
        command.Parameters.AddWithValue("$running", BulkJobStatus.Running.ToString());
        return ReadAll(command);
    }

    /// <summary>Most recent terminal jobs, newest first, capped at <paramref name="limit"/>.</summary>
    public IReadOnlyList<BulkJob> GetRecentFinished(int limit)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns +
            " WHERE status IN ($completed, $cancelled, $interrupted) ORDER BY finished_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$completed", BulkJobStatus.Completed.ToString());
        command.Parameters.AddWithValue("$cancelled", BulkJobStatus.Cancelled.ToString());
        command.Parameters.AddWithValue("$interrupted", BulkJobStatus.Interrupted.ToString());
        command.Parameters.AddWithValue("$limit", limit);
        return ReadAll(command);
    }

    public IReadOnlyList<BulkJobRow> GetRows(string jobId)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT job_id, row_index, target, status, message, recorded_at " +
            "FROM bulk_job_row WHERE job_id = $job ORDER BY row_index ASC;";
        command.Parameters.AddWithValue("$job", jobId);
        var rows = new List<BulkJobRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new BulkJobRow
            {
                JobId = reader.GetString(0),
                RowIndex = reader.GetInt32(1),
                Target = reader.GetString(2),
                Status = Enum.Parse<BulkJobRowStatus>(reader.GetString(3), ignoreCase: true),
                Message = reader.IsDBNull(4) ? null : reader.GetString(4),
                RecordedAtUtc = ParseIso(reader.GetString(5))
            });
        }
        return rows;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private const string SelectColumns =
        "SELECT id, module_id, job_type, status, submitted_by, submitted_by_display, submitted_ip, " +
        "ticket, auth_snapshot_json, payload_json, submitted_at, started_at, finished_at, heartbeat_at, " +
        "total_rows, processed_rows, success_count, partial_count, failed_count, cancel_requested, message " +
        "FROM bulk_job";

    private static IReadOnlyList<BulkJob> ReadAll(SqliteCommand command)
    {
        var jobs = new List<BulkJob>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            jobs.Add(ReadJob(reader));
        return jobs;
    }

    private static BulkJob ReadJob(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        ModuleId = r.GetString(1),
        JobType = r.GetString(2),
        Status = Enum.Parse<BulkJobStatus>(r.GetString(3), ignoreCase: true),
        SubmittedBy = r.GetString(4),
        SubmittedByDisplay = r.IsDBNull(5) ? null : r.GetString(5),
        SubmittedIp = r.GetString(6),
        Ticket = r.IsDBNull(7) ? null : r.GetString(7),
        AuthSnapshotJson = r.IsDBNull(8) ? null : r.GetString(8),
        PayloadJson = r.GetString(9),
        SubmittedAtUtc = ParseIso(r.GetString(10)),
        StartedAtUtc = r.IsDBNull(11) ? null : ParseIso(r.GetString(11)),
        FinishedAtUtc = r.IsDBNull(12) ? null : ParseIso(r.GetString(12)),
        HeartbeatAtUtc = r.IsDBNull(13) ? null : ParseIso(r.GetString(13)),
        TotalRows = r.GetInt32(14),
        ProcessedRows = r.GetInt32(15),
        SuccessCount = r.GetInt32(16),
        PartialCount = r.GetInt32(17),
        FailedCount = r.GetInt32(18),
        CancelRequested = r.GetInt32(19) != 0,
        Message = r.IsDBNull(20) ? null : r.GetString(20)
    };

    private static void BindFullJob(SqliteCommand command, BulkJob job)
    {
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$module", job.ModuleId);
        command.Parameters.AddWithValue("$type", job.JobType);
        command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue("$by", job.SubmittedBy);
        command.Parameters.AddWithValue("$byDisplay", (object?)job.SubmittedByDisplay ?? DBNull.Value);
        command.Parameters.AddWithValue("$ip", job.SubmittedIp);
        command.Parameters.AddWithValue("$ticket", (object?)job.Ticket ?? DBNull.Value);
        command.Parameters.AddWithValue("$auth", (object?)job.AuthSnapshotJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", job.PayloadJson);
        command.Parameters.AddWithValue("$submitted", Iso(job.SubmittedAtUtc));
        command.Parameters.AddWithValue("$started", (object?)IsoOrNull(job.StartedAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$finished", (object?)IsoOrNull(job.FinishedAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$heartbeat", (object?)IsoOrNull(job.HeartbeatAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$total", job.TotalRows);
        command.Parameters.AddWithValue("$processed", job.ProcessedRows);
        command.Parameters.AddWithValue("$success", job.SuccessCount);
        command.Parameters.AddWithValue("$partial", job.PartialCount);
        command.Parameters.AddWithValue("$failed", job.FailedCount);
        command.Parameters.AddWithValue("$cancel", job.CancelRequested ? 1 : 0);
        command.Parameters.AddWithValue("$message", (object?)job.Message ?? DBNull.Value);
    }

    private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("O");
    private static string? IsoOrNull(DateTime? utc) => utc is null ? null : Iso(utc.Value);
    private static DateTime ParseIso(string s) =>
        DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
}
