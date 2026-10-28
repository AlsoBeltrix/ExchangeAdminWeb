using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The narrow room-mutation seam the bulk job processor calls, implemented by
/// <see cref="Services.ConferenceRoomService"/>. It exposes exactly the two per-row operations the
/// bulk paths use, unchanged from the live page. Extracting it lets the processor be unit-tested
/// with a substitute (no live Exchange Online / AD), as the plan requires
/// (docs/BulkJobRunner-Plan.md - "keep per-row Exchange/AD work behind the existing
/// ConferenceRoomService seam so the runner is tested with a substituted service").
/// </summary>
/// <remarks>
/// aud-a2d. Each operation takes the room the protection gate validated as well as the row's typed
/// address: the writes are bound to <c>room</c>, the address is for messages and the audit target.
/// Null refuses.
/// </remarks>
public interface IConferenceRoomBulkOperations
{
    /// <param name="cancellationToken">
    /// kd-58 A9. The job's token - the operator's cancel request, or the host stopping - for the row's steps before
    /// Set-Place, as the page's single-room setup takes its activity's (A8). Once Set-Place is sent the row runs on.
    /// </param>
    Task<RoomOperationResult> SetRoomMetadataAndListAsync(
        string roomEmail, ValidatedMailbox? room, string city, string building, int capacity,
        string floor, string floorLabel, string displayDevice, string videoDevice,
        string countryOrRegion, string state, string timezone, CancellationToken cancellationToken);

    Task<RoomOperationResult> SetRoomTypeAsync(
        string roomEmail, ValidatedMailbox? room, RoomType roomType, string timezone,
        string site = "none", string? arbiter = null,
        bool removeExistingPermissions = false);
}
