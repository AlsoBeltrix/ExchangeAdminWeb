using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Covers the fail-closed configuration preflight for Conference Rooms. The module no longer
/// hardcodes ADI group/OU defaults; required group addresses must come from module config.
/// FindMissingRequiredGroups is the pure decision the Room Type operation uses to abort
/// before any EXO mutation when the module is not configured.
/// See docs/ConferenceRooms-ConfigExtraction-Plan.md.
/// </summary>
public class ConferenceRoomConfigPreflightTests
{
    // A fully-populated config: every required key maps to a non-blank value.
    private static string? AllConfigured(string key) => $"{key}@example.com";

    [Fact]
    public void FindMissingRequiredGroups_AllConfigured_ReturnsNone()
    {
        var missing = ConferenceRoomService.FindMissingRequiredGroups(AllConfigured);
        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissingRequiredGroups_AllUnset_ReturnsEveryRequiredKey()
    {
        var missing = ConferenceRoomService.FindMissingRequiredGroups(_ => null);

        // Exactly the declared required keys, nothing more.
        Assert.Equal(
            ConferenceRoomService.RequiredGroupConfigKeys.OrderBy(k => k),
            missing.OrderBy(k => k));
    }

    [Fact]
    public void FindMissingRequiredGroups_OneBlank_ReportsOnlyThatKey()
    {
        // Whitespace counts as missing; everything else is set.
        var missing = ConferenceRoomService.FindMissingRequiredGroups(
            key => key == "ConfAdminsGroup" ? "   " : "set@example.com");

        Assert.Equal(["ConfAdminsGroup"], missing);
    }

    [Fact]
    public void FindMissingRequiredGroups_EmptyString_CountsAsMissing()
    {
        var missing = ConferenceRoomService.FindMissingRequiredGroups(
            key => key == "DefaultArbiterGroup" ? "" : "set@example.com");

        Assert.Equal(["DefaultArbiterGroup"], missing);
    }

    [Fact]
    public void RequiredGroupConfigKeys_DoesNotIncludeOptionalKeys()
    {
        // RoomListOU and the contact-email keys are intentionally NOT required.
        Assert.DoesNotContain("RoomListOU", ConferenceRoomService.RequiredGroupConfigKeys);
        Assert.DoesNotContain("RestrictedContactEmail", ConferenceRoomService.RequiredGroupConfigKeys);
        Assert.DoesNotContain("ExecContactEmail", ConferenceRoomService.RequiredGroupConfigKeys);
        Assert.DoesNotContain("ADGTContactEmail", ConferenceRoomService.RequiredGroupConfigKeys);
    }
}
