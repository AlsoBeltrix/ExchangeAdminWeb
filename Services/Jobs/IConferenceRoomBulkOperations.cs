using System.Security.Claims;
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
///
/// aud-a2e. The Room Finder operation also takes the acting user, for the servicer grant on a room
/// list protected as a write target. The bulk processor passes null - a row runs off-circuit, under no
/// operator, and never services - as it does to the room gate.
/// </remarks>
public interface IConferenceRoomBulkOperations
{
    Task<RoomOperationResult> SetRoomMetadataAndListAsync(
        string roomEmail, ValidatedMailbox? room, ClaimsPrincipal? actingUser, string city, string building, int capacity,
        string floor, string floorLabel, string displayDevice, string videoDevice,
        string countryOrRegion, string state, string timezone);

    Task<RoomOperationResult> SetRoomTypeAsync(
        string roomEmail, ValidatedMailbox? room, RoomType roomType, string timezone,
        string site = "none", string? arbiter = null,
        bool removeExistingPermissions = false);
}
