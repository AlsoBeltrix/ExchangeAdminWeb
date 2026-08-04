using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Dirty tracking for the redesigned admin pages (docs/AdminUIRedesign-Plan.md, bug B2).
///
/// This is the logic that decides whether an operator gets warned before losing an edit, on the
/// pages that control who can reach every module. It lives in a service precisely so it can be
/// tested -- page fields could not be.
/// </summary>
public class AdminPageDirtyStateTests
{
    [Fact]
    public void StartsClean()
    {
        var s = new AdminPageDirtyState();

        Assert.False(s.IsDirty);
        Assert.Equal(0, s.DirtyCount);
        Assert.Empty(s.Summary());
    }

    [Fact]
    public void TracksASectionAsDirty()
    {
        var s = new AdminPageDirtyState();
        s.Set("Access", true);

        Assert.True(s.IsDirty);
        Assert.True(s.IsSectionDirty("Access"));
        Assert.False(s.IsSectionDirty("Configuration"));
    }

    [Fact]
    public void SectionNamesAreCaseInsensitive()
    {
        // The tab and the save handler must not disagree because one wrote "access".
        var s = new AdminPageDirtyState();
        s.Set("Access", true);

        Assert.True(s.IsSectionDirty("access"));
        s.ClearSection("ACCESS");
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void MarkingTheSameSectionTwiceCountsOnce()
    {
        var s = new AdminPageDirtyState();
        s.Set("Access", true);
        s.Set("Access", true);

        Assert.Equal(1, s.DirtyCount);
    }

    [Fact]
    public void ClearingOneSectionLeavesTheOthersDirty()
    {
        // The failure this prevents: saving one tab and having the page report itself clean while
        // another tab still holds edits - which is what the eight separate Save buttons did.
        var s = new AdminPageDirtyState();
        s.Set("Access", true);
        s.Set("Configuration", true);

        s.ClearSection("Access");

        Assert.True(s.IsDirty);
        Assert.Equal(1, s.DirtyCount);
        Assert.True(s.IsSectionDirty("Configuration"));
    }

    [Fact]
    public void ClearWipesEverything()
    {
        var s = new AdminPageDirtyState();
        s.Set("Access", true);
        s.Set("Configuration", true);

        s.Clear();

        Assert.False(s.IsDirty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSectionNamesAreIgnored(string section)
    {
        // Tracking under an empty key would make the count right and the summary useless.
        var s = new AdminPageDirtyState();
        s.Set(section, true);

        Assert.False(s.IsDirty);
    }

    // ---------------------------------------------------------------- Change notification

    [Fact]
    public void NotifiesOnFirstChangeOnly()
    {
        // The save bar re-renders on this event; firing per keystroke would thrash the circuit.
        var s = new AdminPageDirtyState();
        var fired = 0;
        s.Changed += () => fired++;

        s.Set("Access", true);
        s.Set("Access", true);
        s.Set("Access", true);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void NotifiesWhenBecomingClean()
    {
        var s = new AdminPageDirtyState();
        s.Set("Access", true);

        var fired = 0;
        s.Changed += () => fired++;
        s.ClearSection("Access");

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ClearOnAnAlreadyCleanStateIsSilent()
    {
        var s = new AdminPageDirtyState();
        var fired = 0;
        s.Changed += () => fired++;

        s.Clear();

        Assert.Equal(0, fired);
    }

    // ---------------------------------------------------------------- Summary text

    [Fact]
    public void SummaryNamesTheSingleOffendingSection()
    {
        // "1 unsaved change" alone would leave an operator hunting across four tabs.
        var s = new AdminPageDirtyState();
        s.Set("Access", true);

        Assert.Equal("1 unsaved change in Access", s.Summary());
    }

    [Fact]
    public void SummaryListsSeveralSections()
    {
        var s = new AdminPageDirtyState();
        s.Set("Configuration", true);
        s.Set("Access", true);

        Assert.Equal("2 unsaved changes in Access, Configuration", s.Summary());
    }

    [Fact]
    public void SummaryOrderIsStable()
    {
        // Two pages marking sections in different orders must render the same text, or the bar
        // appears to flicker between equivalent states.
        var a = new AdminPageDirtyState();
        a.Set("Access", true);
        a.Set("Modules", true);

        var b = new AdminPageDirtyState();
        b.Set("Modules", true);
        b.Set("Access", true);

        Assert.Equal(a.Summary(), b.Summary());
    }
}
