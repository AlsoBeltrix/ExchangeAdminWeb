using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The narrow room-mutation seam the bulk job processor calls, implemented by
/// <see cref="Services.ConferenceRoomService"/>. It exposes exactly the two per-row operations the
/// bulk paths use, unchanged from the live page. Extracting it lets the processor be unit-tested
/// with a substitute (no live Exchange Online / AD), as the plan requires
/// (docs/BulkJobRunner-Plan.md — "keep per-row Exchange/AD work behind the existing
/// ConferenceRoomService seam so the runner is tested with a substituted service").
/// </summary>
public interface IConferenceRoomBulkOperations
{
    Task<RoomOperationResult> SetRoomMetadataAndListAsync(
        string roomEmail, string city, string building, int capacity,
        string floor, string floorLabel, string displayDevice, string videoDevice,
        string countryOrRegion, string state, string timezone);

    Task<RoomOperationResult> SetRoomTypeAsync(
        string roomEmail, RoomType roomType, string timezone,
        string site = "none", string? arbiter = null,
        bool removeExistingPermissions = false);
}
