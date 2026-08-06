using ExchangeAdminWeb.Components.Shared;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The two decisions extracted from the AD identity picker
/// (docs/AdIdentityPickerLegibility-Plan.md).
/// </summary>
/// <remarks>
/// The reported defect was that four groups sharing the prefix "ExchangeWeb" rendered
/// identically - the row was one end-truncated string of domain + name + secondary text, so every
/// candidate died at the same character. The picker writes a SID that grants module access, so an
/// operator who cannot tell two rows apart can grant the wrong group.
///
/// The layout half of that fix is CSS (the identity wraps; the secondary line truncates instead)
/// and is verified visually - the repo has no bUnit harness. What IS testable is the two decisions
/// beside it, and they are here.
/// </remarks>
public class AdIdentityAutocompleteTests
{
    // ---- Badge suppression -------------------------------------------------------------------
    //
    // In a group-only picker every badge reads "Group", so the column is pure width cost in
    // exactly the narrow panes where names are already being cut.

    [Theory]
    [InlineData("Any")]
    [InlineData("any")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheTypeBadgeShowsWhenResultsCanBeMixed(string? objectKind)
    {
        Assert.True(ADIdentityAutocomplete.ShouldShowTypeBadge(objectKind));
    }

    [Theory]
    [InlineData("Group")]
    [InlineData("User")]
    [InlineData("OU")]
    public void TheTypeBadgeIsHiddenWhenEveryResultIsTheSameKind(string objectKind)
    {
        Assert.False(ADIdentityAutocomplete.ShouldShowTypeBadge(objectKind));
    }

    // ---- Hover text --------------------------------------------------------------------------
    //
    // The safety net: the secondary line still truncates and an OU distinguished name can be very
    // long, so hovering must always disclose the whole value.

    [Fact]
    public void TheHoverTextQualifiesTheNameWithItsDomain()
    {
        // The domain is what distinguishes same-named groups across a multi-domain forest, so it
        // must survive into the tooltip rather than only the visible row.
        var title = ADIdentityAutocomplete.BuildRowTitle("ANALOG", "ExchangeWebAdmins", "ExchangeWebAdmins");

        Assert.StartsWith(@"ANALOG\ExchangeWebAdmins", title);
    }

    [Fact]
    public void TheHoverTextCarriesTheSecondaryValueOnItsOwnLine()
    {
        var title = ADIdentityAutocomplete.BuildRowTitle("ANALOG", "jdoe", "jdoe@analog.com");

        Assert.Equal("ANALOG\\jdoe\njdoe@analog.com", title);
    }

    [Fact]
    public void TheHoverTextDoesNotRepeatAValueThatMatchesTheName()
    {
        // A group's secondary text is often its sAMAccountName, which is frequently the display
        // name too. Repeating it would push the useful half out of a tooltip.
        var title = ADIdentityAutocomplete.BuildRowTitle(null, "ExchangeWebPerms", "ExchangeWebPerms");

        Assert.Equal("ExchangeWebPerms", title);
    }

    [Fact]
    public void TheHoverTextSurvivesAMissingDomain()
    {
        // A local-domain result carries no NetBIOS prefix; the tooltip must not render a stray
        // leading backslash.
        var title = ADIdentityAutocomplete.BuildRowTitle("", "IAM", "IAM");

        Assert.Equal("IAM", title);
        Assert.DoesNotContain(@"\", title);
    }

    [Fact]
    public void TheHoverTextSurvivesAMissingSecondaryValue()
    {
        var title = ADIdentityAutocomplete.BuildRowTitle("ANALOG", "IAM", null);

        Assert.Equal(@"ANALOG\IAM", title);
        Assert.DoesNotContain("\n", title);
    }

    [Fact]
    public void ShortPrefixSharingCandidatesRemainDistinguishableInTheHoverText()
    {
        // The reported case: these two differ only after 11 characters. Whatever the CSS does,
        // the tooltip must never collapse them onto the same string.
        var a = ADIdentityAutocomplete.BuildRowTitle("ANALOG", "ExchangeWebAdmins", "ExchangeWebAdmins");
        var b = ADIdentityAutocomplete.BuildRowTitle("ANALOG", "ExchangeWebPerms", "ExchangeWebPerms");

        Assert.NotEqual(a, b);
    }
}
