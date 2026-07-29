using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The Message Analysis detail-export per-row work behind the bulk job runner
/// (docs/MessageTraceDetail-Plan.md, slice 5). Fetching a message's full delivery detail from the
/// cloud costs one Get-MessageTraceDetailV2 call each, so above the live threshold (decision 4) the
/// export is produced off the browser circuit as a bulk job: one row per selected message, then a
/// single completion step that assembles the CSV, saves it under the audit log root, and emails a
/// LINK to the Downloadable Reports page - never the data itself
/// (docs/MessageTraceDownloadLink-Plan.md, superseding MessageTraceDetail-Plan decisions 5 and 6).
///
/// Because the mail no longer carries the export, the saved file is the sole delivery, so the save
/// result branches the notification: a failed save sends an explicit failure notice and stamps the
/// job record, never a "ready" mail pointing at a file that does not exist (openreview F1).
///
/// Per row it fetches one message's detail via the narrow <see cref="IMessageTraceDetailSource"/>
/// seam - fail-soft: a fetch that fails is recorded as a Failed row but its (Error-carrying) detail
/// is still retained so the emailed export never silently drops a requested message (Known Failure
/// Class #2). The runner persists only the row outcome (<see cref="BulkJobRow"/>), NOT the fetched
/// detail, so this processor retains each fetched <see cref="MessageTraceDetail"/> in an instance
/// field for the completion step. The runner resolves the processor ONCE per job in a fresh DI scope
/// (see <see cref="IBulkJobProcessor"/>), so an instance field is valid for the job's lifetime and
/// re-fetching in the completion step (which would double the cloud cost) is avoided.
///
/// Registered scoped so <see cref="MessageTraceService"/> (scoped) can back the detail seam; the
/// runner resolves this from a fresh scope per job.
/// </summary>
public sealed class MessageTraceDetailJobProcessor : IBulkJobProcessor
{
    private readonly IMessageTraceDetailSource _details;
    private readonly EmailService _email;
    private readonly AuditService _audit;
    private readonly MessageTraceExportStore _exports;
    private readonly BulkJobService _jobs;
    private readonly ILogger<MessageTraceDetailJobProcessor> _logger;

    public const string ModuleName = "MessageTrace";
    private const string AuditAction = "MessageTrace_DetailExport";

    /// <summary>
    /// Per-row fetched details, keyed by row index. Populated in <see cref="ProcessRowAsync"/> and
    /// consumed in <see cref="OnJobCompletedAsync"/>. Concurrent for safety, though the runner drives
    /// rows sequentially. Valid for the job's lifetime because the processor is resolved once per job.
    /// </summary>
    private readonly ConcurrentDictionary<int, MessageTraceDetail> _fetched = new();

    public string ModuleId => ModuleName;

    public MessageTraceDetailJobProcessor(
        IMessageTraceDetailSource details,
        EmailService email,
        AuditService audit,
        MessageTraceExportStore exports,
        BulkJobService jobs,
        ILogger<MessageTraceDetailJobProcessor> logger)
    {
        _details = details;
        _email = email;
        _audit = audit;
        _exports = exports;
        _jobs = jobs;
        _logger = logger;
    }

    public int CountRows(BulkJob job)
    {
        var payload = Deserialize(job);
        return payload.Messages?.Count ?? 0;
    }

    public async Task<BulkJobRowOutcome> ProcessRowAsync(BulkJob job, int rowIndex, CancellationToken cancellationToken)
    {
        var payload = Deserialize(job);
        var message = payload.Messages![rowIndex];
        var target = string.IsNullOrWhiteSpace(message.MessageId) ? message.MessageTraceId : message.MessageId;

        // Fail-soft fetch: GetMessageDetailAsync never throws (sets Error on failure). Retain the
        // detail either way so the completion report includes every requested message, then map a
        // fetch error to a Failed row so the per-message failure is aggregated, never a batch abort.
        var detail = await _details.GetMessageDetailAsync(message, cancellationToken);
        _fetched[rowIndex] = detail;

        if (!string.IsNullOrEmpty(detail.Error))
            return new BulkJobRowOutcome { Target = target, Status = BulkJobRowStatus.Failed, Message = detail.Error };

        return new BulkJobRowOutcome { Target = target, Status = BulkJobRowStatus.Success };
    }

    public async Task OnJobCompletedAsync(BulkJob job)
    {
        // Fail-safe: the runner has already persisted the terminal state and swallows any throw here,
        // so a report/zip/email failure never changes the job result.
        var payload = Deserialize(job);
        var messages = payload.Messages ?? new List<MessageTraceResult>();

        // Assemble in the operator's selected order from the retained per-row details. A message that
        // was never processed (e.g. the job was cancelled before its row) still appears, with an
        // explanatory error, so the export reflects the full selection (Known Failure Class #2).
        var details = new List<MessageTraceDetail>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            details.Add(_fetched.TryGetValue(i, out var d)
                ? d
                : new MessageTraceDetail { Summary = messages[i], Error = "Not processed (job did not reach this message)." });
        }

        var csv = MessageTraceDetailReport.BuildCsv(details);
        var ticket = job.Ticket ?? "";

        // The saved file is now the SOLE delivery - the export is no longer attached to the mail
        // (docs/MessageTraceDownloadLink-Plan.md). So the save result decides which mail is sent.
        var savedPath = SaveToLogPath(job.Id, job.SubmittedAtUtc, csv);

        var recipients = ResolveRecipients(payload);

        if (savedPath is null)
        {
            // openreview F1: never a "ready" mail when there is nothing to download. Stamp the job
            // record too, so the reports page can render this as Failed rather than blaming the
            // 30-day retention window for what was actually a write error.
            MarkSaveFailed(job);
            await _email.SendMessageTraceFailureAsync(recipients, messages.Count, ticket, job.SubmittedBy);
        }
        else
        {
            await _email.SendMessageTraceResultAsync(recipients, messages.Count, ticket, job.SubmittedBy,
                _exports.ExpiresAtUtc(job.SubmittedAtUtc));
        }

        Audit(job, savedPath, messages.Count, ticket);
    }

    /// <summary>
    /// The notification recipient set: exactly what the operator left in the recipient box (plan
    /// D4), carried on the payload. The configured admin address is NOT added - the owner ruled that
    /// all admins must not receive the actual results, and now that the mail carries a link rather
    /// than the data, adding them would also be pointless.
    ///
    /// A null <c>Recipients</c> means a job enqueued before the box existed, so it falls back to the
    /// submitter's address - the behaviour that job was submitted under. An EMPTY list is not the
    /// same thing: the operator cleared the box, and clearing it must mean "notify nobody" rather
    /// than quietly reinstating their address (D4: the pre-fill is a default, not a floor).
    /// </summary>
    private static IReadOnlyList<string> ResolveRecipients(MessageTraceDetailJobPayload payload) =>
        payload.Recipients is null
            ? EmailService.NormalizeRecipients([payload.UserEmail])
            : EmailService.NormalizeRecipients(payload.Recipients);

    /// <summary>
    /// Records the save failure in the job's own record, which is what the Downloadable Reports page
    /// reads to distinguish Failed from Expired.
    ///
    /// Uses the additive <see cref="BulkJobService.AppendJobMessage"/> rather than the terminal
    /// transition: by the time this hook runs the runner has already persisted the terminal state
    /// via a compare-and-swap from a non-terminal status, so a TryFinish here would match no row.
    /// Fail-safe like everything else in the completion step - the job result never changes because
    /// a note could not be written; the audit event remains the durable record either way.
    /// </summary>
    private void MarkSaveFailed(BulkJob job)
    {
        try
        {
            _jobs.AppendJobMessage(job.Id, MessageTraceExportListing.SaveFailedMarker);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record the export save failure on job {Job}", job.Id);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Persists the CSV export under the directory owned by <see cref="MessageTraceExportStore"/>
    /// (rooted at the required audit log root; fail-loud if unset). Returns the written path, or null
    /// if the save failed. Errors are logged and swallowed so a save failure never faults the job -
    /// the caller is fail-safe by contract.
    ///
    /// A null return is NOT nothing: the caller must branch on it. Once the export stops travelling
    /// in the mail as an attachment, this file is the only copy, so swallowing a failure here and
    /// still sending a "ready" notification would point the operator at a file that does not exist.
    /// </summary>
    private string? SaveToLogPath(string jobId, DateTime submittedAtUtc, string csv)
    {
        try
        {
            Directory.CreateDirectory(_exports.DirectoryPath);
            var path = _exports.PathFor(jobId, submittedAtUtc);
            File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Message Analysis detail export for job {Job} to the log path", jobId);
            return null;
        }
    }

    private void Audit(BulkJob job, string? savedPath, int messageCount, string ticket)
    {
        try
        {
            var detail = savedPath is null
                ? $"{messageCount} message(s); log save failed"
                : $"{messageCount} message(s); saved {savedPath}";
            _audit.LogLookupAction(job.SubmittedBy, job.SubmittedIp, AuditAction,
                target: $"job {job.Id}", success: savedPath is not null, errorDetail: savedPath is null ? detail : null,
                ticketNumber: ticket);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit failed for Message Analysis detail export job {Job}", job.Id);
        }
    }

    private static MessageTraceDetailJobPayload Deserialize(BulkJob job)
    {
        var payload = JsonSerializer.Deserialize<MessageTraceDetailJobPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException("MessageTrace detail job payload is empty or invalid.");
        return payload;
    }
}
