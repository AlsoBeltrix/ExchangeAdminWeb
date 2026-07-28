using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The narrow per-message detail seam the Message Analysis bulk processor calls, implemented by
/// <see cref="Services.MessageTraceService"/>. It exposes exactly the one fetch the off-circuit
/// email job needs, unchanged from the live page. Extracting it lets the processor be unit-tested
/// with a substitute (no live Exchange Online / on-prem transport), matching the
/// <see cref="IConferenceRoomBulkOperations"/> pattern.
/// </summary>
public interface IMessageTraceDetailSource
{
    /// <summary>
    /// Fetch the full per-hop delivery trail for one message. Fail-soft: a fetch failure returns a
    /// <see cref="MessageTraceDetail"/> with <see cref="MessageTraceDetail.Error"/> set and empty
    /// events; it never throws (see <see cref="Services.MessageTraceService.GetMessageDetailAsync"/>).
    /// </summary>
    Task<MessageTraceDetail> GetMessageDetailAsync(MessageTraceResult message, CancellationToken cancellationToken = default);
}
