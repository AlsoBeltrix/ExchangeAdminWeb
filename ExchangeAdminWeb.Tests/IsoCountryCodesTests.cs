using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Integrity guard for the hardcoded ISO 3166-1 alpha-2 → numeric table. The table is
/// authoritative for AD's countryCode attribute (which .NET cannot supply). These tests pin
/// the table's completeness and known anchors so accidental corruption or truncation fails
/// CI. They do NOT detect a genuinely new ISO country (that surfaces as a fail-closed
/// "unmappable country" error at apply time); if a new country is added, update both the
/// table and the expected count here. See IsoCountryCodes.cs.
/// </summary>
public class IsoCountryCodesTests
{
    [Fact]
    public void Table_HasAllOfficiallyAssignedEntries()
    {
        // ISO 3166-1 currently has 249 officially assigned alpha-2 codes (as of 2026-06).
        Assert.Equal(249, IsoCountryCodes.Count);
    }

    [Theory]
    [InlineData("IE", 372)]
    [InlineData("US", 840)]
    [InlineData("GB", 826)]
    [InlineData("DE", 276)]
    [InlineData("FR", 250)]
    [InlineData("IN", 356)]
    [InlineData("JP", 392)]
    [InlineData("CN", 156)]
    [InlineData("PH", 608)]
    [InlineData("SG", 702)]
    [InlineData("MY", 458)]
    [InlineData("CA", 124)]
    [InlineData("AT", 40)]
    [InlineData("TH", 764)]
    public void Table_KnownAnchors_AreCorrect(string alpha2, int expected)
    {
        Assert.Equal(expected, IsoCountryCodes.GetNumeric(alpha2));
    }

    [Fact]
    public void GetNumeric_IsCaseInsensitiveAndTrims()
    {
        Assert.Equal(372, IsoCountryCodes.GetNumeric("ie"));
        Assert.Equal(372, IsoCountryCodes.GetNumeric("  IE  "));
    }

    [Theory]
    [InlineData("ZZ")]
    [InlineData("Narnia")]
    [InlineData("")]
    [InlineData("   ")]
    public void GetNumeric_UnknownOrBlank_ReturnsNull(string input)
    {
        Assert.Null(IsoCountryCodes.GetNumeric(input));
    }
}
