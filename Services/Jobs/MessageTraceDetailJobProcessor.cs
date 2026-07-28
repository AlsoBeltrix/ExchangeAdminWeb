using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The Message Analysis detail-export per-row work behind the bulk job runner
/// (docs/MessageTraceDetail-Plan.md, slice 5). Fetching a message's full delivery detail from the
/// cloud costs one Get-MessageTraceDetailV2 call each, so above the live threshold (decision 4) the
/// export is produced off the browser circuit as a bulk job: one row per selected message, then a
/// single completion step that assembles the CSV, saves it under the audit log root, zips it, and
/// emails the zip to the authenticated user + configured admins (decisions 5 and 6).
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
    private readonly IConfiguration _config;
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
        IConfiguration config,
        ILogger<MessageTraceDetailJobProcessor> logger)
    {
        _details = details;
        _email = email;
        _audit = audit;
        _config = config;
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
        var baseName = $"MessageTraceDetail_{job.Id}_{job.SubmittedAtUtc:yyyyMMdd-HHmmss}";
        var csvName = baseName + ".csv";
        var zipName = baseName + ".zip";
        var ticket = job.Ticket ?? "";

        // Save the CSV under the audit log root (decision 5: the file is also saved to the log path).
        var savedPath = SaveToLogPath(job.Id, csvName, csv);

        // Zip the CSV in-memory (decision B: no server file-share / wwwroot storage) and email it to
        // the authenticated user + admins (decision 6: recipient is never an operator-typed address).
        var zipBytes = ZipSingleFile(csvName, csv);
        await _email.SendMessageTraceResultAsync(payload.UserEmail ?? "", zipBytes, zipName,
            messages.Count, ticket, job.SubmittedBy);

        Audit(job, savedPath, messages.Count, ticket);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Persists the CSV export under <c>&lt;logRoot&gt;\ExchangeAdminWeb\MessageTraceExports\</c>
    /// (the required audit log root; fail-loud if unset). Returns the written path. Errors are logged
    /// and swallowed so a save failure never breaks the email path or (via the fail-safe caller) the
    /// job result.
    /// </summary>
    private string? SaveToLogPath(string jobId, string fileName, string csv)
    {
        try
        {
            var logRoot = AuditLogRoot.Require(_config);
            var dir = Path.Combine(logRoot, "ExchangeAdminWeb", "MessageTraceExports");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Message Analysis detail export for job {Job} to the log path", jobId);
            return null;
        }
    }

    private static byte[] ZipSingleFile(string entryName, string content)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            entryStream.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
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
