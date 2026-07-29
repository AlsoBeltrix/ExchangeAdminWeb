using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The serialized payload for a Message Analysis detail-export bulk job - the opaque
/// <see cref="BulkJob.PayloadJson"/> the runner carries and the
/// <see cref="MessageTraceDetailJobProcessor"/> deserializes. It captures the exact set of summary
/// rows the operator selected (each carries its backend + trace id, all the per-message detail
/// fetch needs), plus the notification recipients the operator submitted, so the completion step
/// can mail a LINK to the Downloadable Reports page
/// (docs/MessageTraceDownloadLink-Plan.md, superseding MessageTraceDetail-Plan decision 6).
///
/// The selection is captured at submit time so a queued job is a real, inspectable record even
/// after the submitting browser closes. The selection count is capped at
/// <see cref="MessageTraceDetailReport.EmailMax"/> (decision 5) at submit time.
/// </summary>
public sealed class MessageTraceDetailJobPayload
{
    public const string JobType = "MessageTrace_DetailExport";

    /// <summary>The selected summary rows to fetch detail for, in the operator's chosen order.</summary>
    public required List<MessageTraceResult> Messages { get; init; }

    /// <summary>The logged-in user's mailbox address. Retained as the record of who submitted the
    /// export; it is NOT automatically a recipient - see <see cref="Recipients"/>.</summary>
    public string? UserEmail { get; init; }

    /// <summary>
    /// Where the completion notice goes: exactly what the operator left in the recipient box
    /// (plan D4). Pre-filled with their own address by the page, but freely editable and clearable,
    /// so an empty list is a valid submission and means "notify nobody" - the export is still
    /// produced and still listed on the Downloadable Reports page. Never merged with the configured
    /// admin address.
    ///
    /// Null on jobs enqueued before this field existed; the processor falls back to
    /// <see cref="UserEmail"/> for those, which is the behaviour those jobs were submitted under.
    /// </summary>
    public List<string>? Recipients { get; init; }
}
