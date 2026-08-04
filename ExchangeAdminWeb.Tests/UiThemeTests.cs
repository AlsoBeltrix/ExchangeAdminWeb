using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The theme catalog (docs/ThemeSupport-Plan.md).
/// </summary>
public class UiThemeCatalogTests
{
    [Fact]
    public void DefaultIsPresentInTheCatalog()
    {
        // Default is resolved out of All, so a typo in DefaultId would throw at
        // runtime on the first page load rather than fail here.
        Assert.Equal(UiThemeCatalog.DefaultId, UiThemeCatalog.Default.Id);
    }

    [Fact]
    public void DefaultIsLight()
    {
        // Owner ruling: adding themes must not change what an existing user
        // sees. The default stays Light.
        Assert.False(UiThemeCatalog.Default.IsDark);
    }

    [Fact]
    public void ShipsTenThemes()
    {
        Assert.Equal(10, UiThemeCatalog.All.Count);
    }

    [Fact]
    public void ThemeIdsAreUnique()
    {
        // Two themes with one id means the picker cannot distinguish them and
        // the CSS selector for the loser never fires.
        var duplicates = UiThemeCatalog.All
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "Duplicate theme ids: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void ThemeIdsAreCssAndAttributeSafe()
    {
        // The id goes into an HTML attribute and a CSS attribute selector. A
        // quote or space there breaks the selector silently -- the theme simply
        // never applies.
        foreach (var theme in UiThemeCatalog.All)
        {
            Assert.Matches("^[a-z0-9-]+$", theme.Id);
        }
    }

    [Fact]
    public void EveryThemeHasADisplayNameAndGroup()
    {
        foreach (var theme in UiThemeCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(theme.Name), $"{theme.Id} has no name");
            Assert.False(string.IsNullOrWhiteSpace(theme.Group), $"{theme.Id} has no group");
        }
    }

    [Fact]
    public void ResolvesAKnownId()
    {
        Assert.Equal("dracula", UiThemeCatalog.Resolve("dracula").Id);
    }

    [Fact]
    public void ResolvesCaseInsensitively()
    {
        Assert.Equal("dracula", UiThemeCatalog.Resolve("Dracula").Id);
    }

    [Fact]
    public void TrimsWhitespaceWhenResolving()
    {
        Assert.Equal("nord", UiThemeCatalog.Resolve("  nord  ").Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-such-theme")]
    public void FallsBackToDefaultRatherThanFailing(string? stored)
    {
        // An unstyled page is a worse outcome than the wrong palette, so this
        // path must never throw or return null.
        Assert.Equal(UiThemeCatalog.DefaultId, UiThemeCatalog.Resolve(stored).Id);
    }

    [Fact]
    public void MigratesTheLegacyDarkValueToOled()
    {
        // Before the picker existed the toggle stored "dark". That names the
        // same palette now called "oled", so a user who had dark mode on must
        // land on OLED Black -- NOT on Light, which is what an unrecognised
        // value would give them.
        Assert.Equal("oled", UiThemeCatalog.Resolve("dark").Id);
    }

    [Fact]
    public void LegacyDarkResolvesToADarkTheme()
    {
        Assert.True(UiThemeCatalog.Resolve(UiThemeCatalog.LegacyDarkId).IsDark);
    }

    [Fact]
    public void ResolvesLightThemesAsReadilyAsDarkOnes()
    {
        // Regression guard. The JS twin of this method looked its theme up in a
        // map whose VALUE is the isDark flag, and tested that value for truth --
        // so every light theme read as "unknown" and fell back to the default.
        // Solarized Light was unselectable and nothing failed. Membership, not
        // truthiness, on both sides.
        foreach (var theme in UiThemeCatalog.All.Where(t => !t.IsDark))
        {
            Assert.Equal(theme.Id, UiThemeCatalog.Resolve(theme.Id).Id);
        }
    }

    [Fact]
    public void EveryCatalogIdRoundTripsThroughResolve()
    {
        foreach (var theme in UiThemeCatalog.All)
        {
            Assert.Equal(theme.Id, UiThemeCatalog.Resolve(theme.Id).Id);
        }
    }

    [Fact]
    public void GroupingCoversEveryTheme()
    {
        var grouped = UiThemeCatalog.Grouped().SelectMany(g => g).ToList();

        Assert.Equal(UiThemeCatalog.All.Count, grouped.Count);
    }
}

/// <summary>
/// The JS catalog literal handed to the pre-paint script in App.razor.
///
/// The script cannot call into C#, so it carries its own copy of the theme
/// table. These tests are what stop that copy drifting: a stale copy would send
/// every user of a newly added theme to the default, with nothing logged and
/// nothing failing.
/// </summary>
public class UiThemeJsTests
{
    [Fact]
    public void EmitsEveryThemeInTheCatalog()
    {
        var js = UiThemeJs.CatalogLiteral();

        foreach (var theme in UiThemeCatalog.All)
        {
            Assert.Contains($"\"{theme.Id}\":", js, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EmitsTheCorrectDarkFlagPerTheme()
    {
        var js = UiThemeJs.CatalogLiteral();

        foreach (var theme in UiThemeCatalog.All)
        {
            var expected = $"\"{theme.Id}\":{(theme.IsDark ? "true" : "false")}";
            Assert.Contains(expected, js, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EmitsAWellFormedObjectLiteral()
    {
        var js = UiThemeJs.CatalogLiteral();

        Assert.StartsWith("{", js, StringComparison.Ordinal);
        Assert.EndsWith("}", js, StringComparison.Ordinal);
        // One separator fewer than there are entries.
        Assert.Equal(UiThemeCatalog.All.Count - 1, js.Count(c => c == ','));
    }

    [Fact]
    public void ContainsNothingNeedingEscaping()
    {
        // The literal is emitted into a <script> block. A quote or angle bracket
        // in an id would break out of it.
        var js = UiThemeJs.CatalogLiteral();

        Assert.DoesNotContain('<', js);
        Assert.DoesNotContain('\\', js);
        Assert.DoesNotContain('\'', js);
    }
}
