using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The serialized payload for a ConferenceRooms bulk job - the opaque <see cref="BulkJob.PayloadJson"/>
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

    /// <summary>
    /// Operator-facing label for a job type. An unrecognised type returns the RAW type rather than
    /// being folded into a known kind.
    ///
    /// The page previously used a two-way ternary over an open string ("is it Finder? no? then it
    /// is Room Type"), which rendered a Message Analysis export as "Room Type (bulk)" and so
    /// disguised the cross-module leak that put it there. Had it shown MessageTrace_DetailExport,
    /// the defect would have been obvious on sight. Scoping the reads makes foreign types
    /// unreachable here, but a future Conference Rooms job type would inherit the same silent
    /// mislabel, so the fallback must be honest regardless.
    ///
    /// Lives here rather than in the .razor file because there is no bUnit harness in this repo -
    /// same reason as MessageTraceExportListing and AdminPageDirtyState.
    /// </summary>
    public static string KindLabel(string jobType) => jobType switch
    {
        FinderJobType => "Room Finder (bulk)",
        TypeJobType => "Room Type (bulk)",
        _ => jobType
    };
}
