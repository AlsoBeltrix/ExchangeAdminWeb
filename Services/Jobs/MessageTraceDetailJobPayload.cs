using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The serialized payload for a Message Analysis detail-export bulk job - the opaque
/// <see cref="BulkJob.PayloadJson"/> the runner carries and the
/// <see cref="MessageTraceDetailJobProcessor"/> deserializes. It captures the exact set of summary
/// rows the operator selected (each carries its backend + trace id, all the per-message detail
/// fetch needs), plus the requesting user's mailbox address, so the completion step can email the
/// zipped export to the authenticated identity + admins - never an operator-typed address
/// (docs/MessageTraceDetail-Plan.md, decision 6).
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

    /// <summary>The logged-in user's mailbox address (a recipient of the emailed export, with admins).</summary>
    public string? UserEmail { get; init; }
}
