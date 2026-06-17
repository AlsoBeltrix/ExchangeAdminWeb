using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Covers the Room Finder synced-attribute write helpers. Root cause (confirmed from logs +
/// live tenant reproduction, docs/ConferenceRooms-RoomFinderMetadataApply-Plan.md): City,
/// StateOrProvince and CountryOrRegion are on-prem-AD-mastered and dir-synced, so EXO
/// Set-User rejects them ("object is being synchronized from your on-premises organization").
/// The fix writes them on-prem with Set-ADUser, which means mapping CountryOrRegion to the
/// three AD attributes EXO writes as a unit (c / co / countryCode) and building the AD
/// attribute set.
///
/// The Set-ADUser/runspace call itself has no injection seam (same constraint as Comms10k),
/// so these tests cover the pure mapping/build helpers, not the live call.
/// </summary>
public class ConferenceRoomSyncedAttributeTests
{
    // --- Country mapping: CountryOrRegion -> c / co / countryCode ---

    // co (country name) must match what ADUC / on-prem Set-User already wrote on existing
    // rooms — the Windows short name (RegionInfo.EnglishName), NOT the ISO 3166 long name.
    // Verified against live directory data (rooms_with_c_co_countrycode.csv): US rooms carry
    // co="United States" (not "United States of America"); GB carry "United Kingdom".
    // countryCode is the ISO 3166-1 numeric code, which .NET cannot supply (RegionInfo.GeoId
    // is a Microsoft GeoId, e.g. 68 for IE, not the ISO 372) — it comes from a static table.
    [Theory]
    [InlineData("IE", "IE", "Ireland", 372)]
    [InlineData("US", "US", "United States", 840)]
    [InlineData("GB", "GB", "United Kingdom", 826)]
    [InlineData("DE", "DE", "Germany", 276)]
    [InlineData("IN", "IN", "India", 356)]
    [InlineData("PH", "PH", "Philippines", 608)]
    public void BuildCountryAttributes_FromIso2_MapsAllThree(string input, string c, string co, int code)
    {
        var result = ConferenceRoomService.BuildCountryAttributes(input);

        Assert.NotNull(result);
        Assert.Equal(c, result!.Value.c);
        Assert.Equal(co, result.Value.co);
        Assert.Equal(code, result.Value.countryCode);
    }

    [Fact]
    public void BuildCountryAttributes_IsCaseInsensitive()
    {
        var result = ConferenceRoomService.BuildCountryAttributes("ie");

        Assert.NotNull(result);
        Assert.Equal("IE", result!.Value.c);
        Assert.Equal(372, result.Value.countryCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildCountryAttributes_Blank_ReturnsNull(string input)
    {
        // Blank country is "nothing to write", not an error.
        Assert.Null(ConferenceRoomService.BuildCountryAttributes(input));
    }

    [Theory]
    [InlineData("Narnia")] // not a 2-letter code at all
    [InlineData("ZZ")]     // well-formed but unassigned
    [InlineData("XK")]     // Kosovo: RegionInfo ACCEPTS this (user-assigned), but it is NOT an
                           // officially-assigned ISO 3166-1 code, so it is absent from the
                           // IsoCountryCodes table. This case specifically guards the table
                           // lookup — RegionInfo alone would let it through. If the table
                           // guard is removed, this row fails (proven non-vacuous).
    public void BuildCountryAttributes_Unmappable_Throws(string input)
    {
        // An unmappable country must fail loudly, never write a partial/inconsistent country.
        Assert.Throws<ArgumentException>(() => ConferenceRoomService.BuildCountryAttributes(input));
    }

    // --- AD attribute set build: l / st / country triple ---

    [Fact]
    public void BuildSyncedUserAttributes_AllPresent_SetsLStAndCountryTriple()
    {
        var attrs = ConferenceRoomService.BuildSyncedUserAttributes("Limerick", "Limerick", "IE");

        Assert.Equal("Limerick", attrs["l"]);
        Assert.Equal("Limerick", attrs["st"]);
        Assert.Equal("IE", attrs["c"]);
        Assert.Equal("Ireland", attrs["co"]);
        Assert.Equal(372, attrs["countryCode"]);
    }

    [Fact]
    public void BuildSyncedUserAttributes_AllBlank_ReturnsEmpty()
    {
        // All-blank location => skip the Set-ADUser call entirely; never call it with no attrs.
        var attrs = ConferenceRoomService.BuildSyncedUserAttributes("", "  ", "");
        Assert.Empty(attrs);
    }

    [Fact]
    public void BuildSyncedUserAttributes_OnlyCity_SetsOnlyL()
    {
        var attrs = ConferenceRoomService.BuildSyncedUserAttributes("Limerick", "", "");

        Assert.Equal("Limerick", attrs["l"]);
        Assert.False(attrs.ContainsKey("st"));
        Assert.False(attrs.ContainsKey("c"));
        Assert.False(attrs.ContainsKey("co"));
        Assert.False(attrs.ContainsKey("countryCode"));
    }

    [Fact]
    public void BuildSyncedUserAttributes_TrimsValues()
    {
        var attrs = ConferenceRoomService.BuildSyncedUserAttributes("  Limerick  ", "  Limerick  ", "  IE  ");

        Assert.Equal("Limerick", attrs["l"]);
        Assert.Equal("Limerick", attrs["st"]);
        Assert.Equal("IE", attrs["c"]);
    }

    [Fact]
    public void BuildSyncedUserAttributes_UnmappableCountry_Throws()
    {
        // Propagates the country failure so the row fails closed rather than writing l/st only.
        Assert.Throws<ArgumentException>(
            () => ConferenceRoomService.BuildSyncedUserAttributes("Limerick", "Limerick", "Narnia"));
    }
}
