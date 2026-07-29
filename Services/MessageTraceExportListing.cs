using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services.Jobs;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Why an export cannot be downloaded. The distinction matters: a write that never happened must
/// never be reported as retention, or the operator concludes they waited too long and stops looking
/// for the real fault (docs/MessageTraceDownloadLink-Plan.md, openreview F1).
/// </summary>
public enum MessageTraceExportState
{
    /// <summary>The file is on disk and can be downloaded.</summary>
    Available,

    /// <summary>Written successfully, since removed by the host retention task. The ordinary end state.</summary>
    Expired,

    /// <summary>The save failed when the job completed, so the file never existed.</summary>
    Failed,

    /// <summary>The job did not reach its completion step (cancelled or interrupted), so no export was produced.</summary>
    NotProduced
}

/// <summary>One row of the Downloadable Reports page.</summary>
public sealed class MessageTraceExportListItem
{
    public required string JobId { get; init; }
    public required DateTime SubmittedAtUtc { get; init; }
    public required string SubmittedBy { get; init; }
    public required string Ticket { get; init; }
    public required int MessageCount { get; init; }

    /// <summary>Short human descriptor of what was traced. Never throws; "(unavailable)" if unparseable.</summary>
    public required string Descriptor { get; init; }

    public required MessageTraceExportState State { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }

    /// <summary>Resolved path when <see cref="State"/> is Available; otherwise null.</summary>
    public string? FullPath { get; init; }

    public bool CanDownload => State == MessageTraceExportState.Available;
}

/// <summary>
/// Outcome of a download attempt. Carries the bytes on success and a display message otherwise, so
/// the page never has to branch on exception types and every attempt has one auditable shape.
/// </summary>
public sealed class MessageTraceExportDownloadResult
{
    public byte[]? Bytes { get; init; }
    public string? FileName { get; init; }

    /// <summary>Operator-facing reason the download did not happen. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>The re-resolved row, when the job was found. Lets the page refresh a stale state.</summary>
    public MessageTraceExportListItem? Item { get; init; }

    public bool Succeeded => Bytes is not null;
}

/// <summary>
/// Page logic for the Downloadable Reports page, kept out of the markup so it is unit-testable -
/// the repo has no bUnit harness (docs/MessageTraceDownloadLink-Plan.md slice 2).
///
/// Enumerates job RECORDS filtered to this module and job type, then resolves each one's file. Not
/// a directory listing: the metadata the page shows (who ran the trace, the ticket, what was
/// searched) exists only in the job record, and a directory listing would also surface any file a
/// future feature happens to write into that folder. The cost, accepted in the plan: an export
/// whose job record has aged out of the jobs DB is invisible here even if the file survives.
///
/// Not sealed, and <see cref="ReadFileAsync"/> is virtual, so the F2 guard is provable: a test can
/// assert the file read was never reached for a blank ticket rather than merely that a message was
/// shown.
/// </summary>
public class MessageTraceExportListing
{
    /// <summary>
    /// How many terminal export jobs the page lists. Independent of BulkJobs:RecentJobLimit, which
    /// bounds the cross-module recent-jobs view and would let another busy module empty this page.
    /// </summary>
    public const int ListLimit = 200;

    /// <summary>
    /// Marker on a job whose export could not be written. Chosen over re-reading the audit log:
    /// <see cref="JsonlLogService"/> exposes only <c>GetAuditLogPaths</c>, so "did this job's save
    /// fail?" would mean scanning append-only day files once per rendered row, on the render path.
    /// The job record is already loaded for every row, so the marker costs nothing to read.
    ///
    /// NOT YET WRITTEN BY ANYTHING. <see cref="MessageTraceExportState.Failed"/> is therefore
    /// unreachable in production until slice 3 lands the processor's save-failure branch, which owns
    /// the write. It cannot be stamped through the existing runner path: the terminal state is
    /// persisted by a compare-and-swap from a NON-terminal status (BulkJobService.cs:401,
    /// BulkJobRepository.TryFinish) BEFORE OnJobCompletedAsync runs (BulkJobService.cs:441), so by
    /// the time the processor knows the save failed the job is already Completed and TryFinish will
    /// not fire. Slice 3 must add an unconditional message write to BulkJobRepository for this.
    /// </summary>
    public const string SaveFailedMarker = "log save failed";

    /// <summary>Refusal text for an empty ticket (openreview F2). Recorded, never validated (D2).</summary>
    public const string TicketRequiredMessage =
        "Enter a ticket number before downloading. It is recorded with the download for audit.";

    private readonly BulkJobService _jobs;
    private readonly MessageTraceExportStore _store;
    private readonly ILogger<MessageTraceExportListing> _logger;

    public MessageTraceExportListing(BulkJobService jobs, MessageTraceExportStore store,
        ILogger<MessageTraceExportListing> logger)
    {
        _jobs = jobs;
        _store = store;
        _logger = logger;
    }

    public IReadOnlyList<MessageTraceExportListItem> GetExports()
    {
        var jobs = _jobs.GetFinishedByType(
            MessageTraceDetailJobProcessor.ModuleName, MessageTraceDetailJobPayload.JobType, ListLimit);

        var items = new List<MessageTraceExportListItem>(jobs.Count);
        foreach (var job in jobs)
        {
            try
            {
                items.Add(ToItem(job));
            }
            catch (ArgumentException ex)
            {
                // The store rejects any id that is not a GUID "N" - a record this app's enqueue path
                // did not write. Skip that row rather than let it take the whole page down; one bad
                // record must never hide every other operator's exports.
                _logger.LogWarning(ex, "Skipping Message Analysis export row for job {Job}: unusable job id", job.Id);
            }
        }
        return items;
    }

    /// <summary>
    /// Re-resolves a single job at click time. A row rendered minutes ago may point at a file the
    /// host retention task has since removed, so the download path must never trust the rendered
    /// state. Returns null when the job is not a Message Analysis export at all - a mismatched id
    /// must not be able to read some other module's job.
    /// </summary>
    public MessageTraceExportListItem? Resolve(string jobId)
    {
        var job = _jobs.GetJob(jobId);
        if (job is null
            || !string.Equals(job.ModuleId, MessageTraceDetailJobProcessor.ModuleName, StringComparison.Ordinal)
            || !string.Equals(job.JobType, MessageTraceDetailJobPayload.JobType, StringComparison.Ordinal))
        {
            return null;
        }
        return ToItem(job);
    }

    /// <summary>
    /// The whole download decision in one place: ticket presence, re-resolution, and the read.
    ///
    /// Ordering is load-bearing and is what the F2 test asserts - the ticket check runs BEFORE the
    /// job is resolved or the file is touched, so a blank ticket cannot reach the filesystem. The
    /// ticket is recorded by the caller's audit entry and never validated (plan D2): this is a
    /// presence check, not a ServiceNow lookup.
    ///
    /// Constitution note (plan, "Constitution Conflict To Record"): the export file is a convenience
    /// artifact, not durable state. The audit event and the job record are the authoritative durable
    /// records and neither depends on the file surviving, so its absence is rendered as an ordinary
    /// outcome here - never an error, and never as retention when the write is what failed.
    /// </summary>
    public async Task<MessageTraceExportDownloadResult> TryDownloadAsync(string jobId, string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return new MessageTraceExportDownloadResult { Error = TicketRequiredMessage };

        var item = Resolve(jobId);
        if (item is null)
            return new MessageTraceExportDownloadResult { Error = "That export is no longer listed." };

        if (!item.CanDownload)
            return new MessageTraceExportDownloadResult { Item = item, Error = Describe(item.State) };

        try
        {
            // Read the bytes as written. The CSV is UTF-8 without BOM
            // (MessageTraceDetailJobProcessor.SaveToLogPath), and a round-trip through string could
            // change that.
            var bytes = await ReadFileAsync(item.FullPath!);
            return new MessageTraceExportDownloadResult
            {
                Item = item,
                Bytes = bytes,
                FileName = Path.GetFileName(item.FullPath!)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The host retention task can remove the file between render and click. That is an
            // ordinary outcome, not a fault: report the expired state and let the page refresh.
            _logger.LogWarning(ex, "Message Analysis export for job {Job} could not be read", jobId);
            return new MessageTraceExportDownloadResult
            {
                Item = item,
                Error = ex is FileNotFoundException or DirectoryNotFoundException
                    ? Describe(MessageTraceExportState.Expired)
                    : $"The export could not be read: {ex.Message}"
            };
        }
    }

    /// <summary>Reads the export bytes. Virtual purely as the seam the F2 non-read assertion needs.</summary>
    public virtual Task<byte[]> ReadFileAsync(string fullPath) => File.ReadAllBytesAsync(fullPath);

    /// <summary>Operator-facing text for a non-downloadable state.</summary>
    public static string Describe(MessageTraceExportState state) => state switch
    {
        MessageTraceExportState.Available => "Available",
        MessageTraceExportState.Expired => "Expired - the file has passed its retention window and been removed.",
        MessageTraceExportState.Failed => "Failed - the export could not be saved, so no file exists. Re-run the trace.",
        _ => "Not produced - the job did not finish, so no export was written."
    };

    /// <summary>Short status word for the table cell.</summary>
    public static string ShortStatus(MessageTraceExportState state) => state switch
    {
        MessageTraceExportState.Available => "Available",
        MessageTraceExportState.Expired => "Expired",
        MessageTraceExportState.Failed => "Failed",
        _ => "Not produced"
    };

    private MessageTraceExportListItem ToItem(BulkJob job)
    {
        var payload = TryDeserialize(job.PayloadJson);
        var state = ClassifyState(job, out var fullPath);

        return new MessageTraceExportListItem
        {
            JobId = job.Id,
            SubmittedAtUtc = job.SubmittedAtUtc,
            SubmittedBy = string.IsNullOrWhiteSpace(job.SubmittedByDisplay) ? job.SubmittedBy : job.SubmittedByDisplay,
            Ticket = job.Ticket ?? "",
            MessageCount = job.TotalRows,
            Descriptor = DescribeTrace(payload, job.TotalRows),
            State = state,
            ExpiresAtUtc = _store.ExpiresAtUtc(job.SubmittedAtUtc),
            FullPath = state == MessageTraceExportState.Available ? fullPath : null
        };
    }

    /// <summary>
    /// Available / Expired / Failed / NotProduced. Expired means the file was written and is now
    /// gone; Failed means it never existed. Collapsing the two would let a disk-full or permissions
    /// fault masquerade as ordinary retention (openreview F1).
    /// </summary>
    internal MessageTraceExportState ClassifyState(BulkJob job, out string? fullPath)
    {
        fullPath = null;

        // A job that never reached its completion step produced no export at all.
        if (job.Status != BulkJobStatus.Completed)
            return MessageTraceExportState.NotProduced;

        if (SaveFailed(job))
            return MessageTraceExportState.Failed;

        // An invalid job id is a caller bug, not an expired export. The store throws for it, and
        // this page must not convert that into a misleading "expired" row; GetExports drops the row.
        if (_store.TryResolve(job.Id, job.SubmittedAtUtc, out var resolved))
        {
            fullPath = resolved;
            return MessageTraceExportState.Available;
        }

        return MessageTraceExportState.Expired;
    }

    /// <summary>
    /// True when the completion step could not write the export. See <see cref="SaveFailedMarker"/>
    /// for which source this reads and why, and for the slice-3 write that makes it reachable.
    /// </summary>
    private static bool SaveFailed(BulkJob job) =>
        job.Message is not null && job.Message.Contains(SaveFailedMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A short "what was traced" descriptor from the captured selection. Deserialization is wrapped
    /// and a payload that will not parse renders "(unavailable)" - one malformed record must never
    /// break the row or the page.
    /// </summary>
    internal static string DescribeTrace(MessageTraceDetailJobPayload? payload, int messageCount)
    {
        var first = payload?.Messages?.FirstOrDefault();
        if (first is null)
            return "(unavailable)";

        var sender = Shorten(first.SenderAddress, 40);
        var recipient = Shorten(first.RecipientAddress, 40);
        var subject = Shorten(first.Subject, 60);

        var who = $"{sender} -> {recipient}";
        var what = string.IsNullOrWhiteSpace(subject) ? who : $"{who}: {subject}";
        return messageCount > 1 ? $"{what} (+{messageCount - 1} more)" : what;
    }

    private static MessageTraceDetailJobPayload? TryDeserialize(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<MessageTraceDetailJobPayload>(payloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Shorten(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..(max - 3)] + "...";
    }
}
