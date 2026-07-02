using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The serialized payload for a ConferenceRooms bulk job — the opaque <see cref="BulkJob.PayloadJson"/>
/// the runner carries and the <see cref="ConferenceRoomBulkProcessor"/> deserializes. Exactly one of
/// <see cref="FinderRows"/> / <see cref="TypeRows"/> is populated, selected by <see cref="Kind"/>.
/// The parsed CSV rows are captured at submit time so a queued job is a real, inspectable record and
/// the per-row input is auditable even after the submitting browser closes (docs/BulkJobRunner-Plan.md).
/// </summary>
public sealed class ConferenceRoomJobPayload
{
    public const string FinderJobType = "SetMetadata_Bulk";
    public const string TypeJobType = "SetType_Bulk";

    public required string Kind { get; init; }

    /// <summary>Room Finder rows (Kind == <see cref="FinderJobType"/>).</summary>
    public List<FinderCsvRow>? FinderRows { get; init; }

    /// <summary>Room Type rows (Kind == <see cref="TypeJobType"/>).</summary>
    public List<TypeCsvRow>? TypeRows { get; init; }
}
