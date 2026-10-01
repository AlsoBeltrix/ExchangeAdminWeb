namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The serialized payload for a Comms-10k membership replace, carried as the job's opaque
/// <see cref="BulkJob.PayloadJson"/> and deserialized by <see cref="Comms10kReplaceProcessor"/>.
/// </summary>
/// <remarks>
/// It captures the UPLOADED ADDRESSES, not the resolved directory names. Resolution happens
/// inside the job, as its first stage, for two reasons: the resolved set is only valid at the
/// moment it is read, and a payload holding ten thousand distinguished names would be a large
/// durable record of something the job can derive in seconds.
///
/// Why this module runs as a job at all (owner, 2026-10-01, "comms-10k should use the background
/// jobs runner"): the replace clears the group's member attribute and refills it in batches, so
/// for several seconds the list is EMPTY and then partial. Run from a page, the operator could
/// navigate away mid-write and the result would land nowhere - and the frame reporting progress
/// on work whose answer gets discarded is, in the owner's words, "just protracted doom". As a
/// job the result is durable, leaving is safe, and there is nothing to cancel.
/// </remarks>
public sealed class Comms10kReplaceJobPayload
{
    public const string JobType = "Comms10k_MembershipReplace";

    /// <summary>
    /// The addresses the operator uploaded, exactly as parsed from their file. The job resolves
    /// these against the directory itself.
    /// </summary>
    public required List<string> Emails { get; init; }

    /// <summary>
    /// The uploaded file's name, so the durable record says WHICH list was applied. Without it a
    /// finished job is "the membership was replaced" with no way to tell with what.
    /// </summary>
    public string? SourceFileName { get; init; }
}
