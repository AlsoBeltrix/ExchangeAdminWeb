using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Covers the Conference Rooms room-list naming rule. The central regression these guard
/// against: the room list was named/matched from the <c>City</c> field, ignoring
/// <c>Building</c>. In Microsoft Room Finder the room list IS the building (city is derived
/// from each room's Place metadata), so a correct file with City=Durham, Building=RTP must
/// target "RTP Conference Rooms", NOT "Durham Conference Rooms" (Shaun Hogan's report).
/// See docs/ConferenceRooms-BuildingRoomList-Plan.md.
///
/// The async resolve/add methods hit live EXO via the connection pool and are not
/// unit-testable here; the naming rule is extracted into pure statics specifically so it
/// can be covered.
/// </summary>
public class ConferenceRoomNamingTests
{
    [Fact]
    public void BuildRoomListName_UsesBuilding_NotCity()
    {
        // The bug: City=Durham, Building=RTP must produce the building list.
        const string building = "RTP";
        const string city = "Durham";

        var name = ConferenceRoomService.BuildRoomListName(building);

        Assert.Equal("RTP Conference Rooms", name);
        Assert.NotEqual(ConferenceRoomService.BuildRoomListName(city), name);
    }

    [Theory]
    [InlineData("RTP", "RTP Conference Rooms")]
    [InlineData("Link Square", "Link Square Conference Rooms")]
    [InlineData("  RTP  ", "RTP Conference Rooms")] // surrounding whitespace trimmed
    public void BuildRoomListName_FormatsAndTrims(string building, string expected)
    {
        Assert.Equal(expected, ConferenceRoomService.BuildRoomListName(building));
    }

    [Theory]
    [InlineData("RTP", "RoomList-RTP")]
    [InlineData("  Durham  ", "RoomList-Durham")]
    public void BuildLegacyRoomListName_FormatsAndTrims(string key, string expected)
    {
        Assert.Equal(expected, ConferenceRoomService.BuildLegacyRoomListName(key));
    }
}
