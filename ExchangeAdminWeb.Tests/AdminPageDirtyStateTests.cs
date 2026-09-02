using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Dirty tracking for the redesigned admin pages (docs/AdminUIRedesign-Plan.md, bug B2).
///
/// This is the logic that decides whether an operator gets warned before losing an edit, on the
/// pages that control who can reach every module, and how many pending edits the save bar claims
/// there are. It lives in a service precisely so it can be tested -- page fields could not be.
///
/// The counting rule changed on 2026-09-02 (owner ruling): the number in the save bar is the
/// number of EDITS, not the number of dirty sections. Ten added protected groups used to read
/// "1 unsaved change" because one section was dirty, which an operator reads as nine edits lost.
/// </summary>
public class AdminPageDirtyStateTests
{
    [Fact]
    public void StartsClean()
    {
        var s = new AdminPageDirtyState();

        Assert.False(s.IsDirty);
        Assert.Equal(0, s.DirtyCount);
        Assert.Equal(0, s.DirtySectionCount);
        Assert.Empty(s.Summary());
    }

    [Fact]
    public void TracksASectionAsDirty()
    {
        var s = new AdminPageDirtyState();
        s.Increment("Access");

        Assert.True(s.IsDirty);
        Assert.True(s.IsSectionDirty("Access"));
        Assert.False(s.IsSectionDirty("Configuration"));
    }

    [Fact]
    public void SectionNamesAreCaseInsensitive()
    {
        // The tab and the save handler must not disagree because one wrote "access".
        var s = new AdminPageDirtyState();
        s.Increment("Access");

        Assert.True(s.IsSectionDirty("access"));
        Assert.Equal(1, s.SectionCount("ACCESS"));

        s.Increment("ACCESS");
        Assert.Equal(1, s.DirtySectionCount);
        Assert.Equal(2, s.DirtyCount);

        s.ClearSection("access");
        Assert.False(s.IsDirty);
    }

    // ---------------------------------------------------------------- Counting edits

    [Fact]
    public void TwoEditsInOneSectionCountTwo()
    {
        // The defect this replaces: the count was the number of dirty SECTIONS, so a second edit
        // in the same section changed nothing an operator could see.
        var s = new AdminPageDirtyState();
        s.Increment("Access");
        s.Increment("Access");

        Assert.Equal(2, s.DirtyCount);
        Assert.Equal(1, s.DirtySectionCount);
        Assert.Equal(2, s.SectionCount("Access"));
    }

    [Fact]
    public void EditsAcrossSectionsSumAndNameBothSections()
    {
        var s = new AdminPageDirtyState();
        s.Increment("Modules");
        s.SetCount("Protected", 4);

        Assert.Equal(5, s.DirtyCount);
        Assert.Equal(2, s.DirtySectionCount);
        Assert.Equal("5 unsaved changes in Modules, Protected", s.Summary());
    }

    [Fact]
    public void SetCountReplacesASectionsCountRatherThanAddingToIt()
    {
        // SetCount exists for a page that counts its own pending edits by diffing what it holds
        // against what it loaded. Adding would double-count every recount.
        var s = new AdminPageDirtyState();
        s.SetCount("Modules", 3);
        s.SetCount("Modules", 2);

        Assert.Equal(2, s.DirtyCount);
    }

    [Fact]
    public void SetCountToZeroClearsTheSection()
    {
        // The whole reason a diffing page uses SetCount: a module toggled on and back off, or a
        // group added and then removed, is no longer a pending change and must stop counting.
        var s = new AdminPageDirtyState();
        s.SetCount("Modules", 1);
        s.SetCount("Protected", 2);

        s.SetCount("Modules", 0);

        Assert.False(s.IsSectionDirty("Modules"));
        Assert.Equal(0, s.SectionCount("Modules"));
        Assert.Equal(2, s.DirtyCount);
        Assert.Equal(1, s.DirtySectionCount);
    }

    [Fact]
    public void SetCountBelowZeroClearsRatherThanSubtractingFromTheTotal()
    {
        // A page whose diff went negative through a bug must not be able to hide another
        // section's pending edits by dragging the total down.
        var s = new AdminPageDirtyState();
        s.SetCount("Modules", 3);
        s.SetCount("Protected", 2);

        s.SetCount("Modules", -5);

        Assert.Equal(2, s.DirtyCount);
        Assert.False(s.IsSectionDirty("Modules"));
    }

    [Fact]
    public void IncrementBuildsOnACountThatWasSetOutright()
    {
        var s = new AdminPageDirtyState();
        s.SetCount("Attributes", 2);
        s.Increment("Attributes");

        Assert.Equal(3, s.DirtyCount);
    }

    // ---------------------------------------------------------------- Clearing

    [Fact]
    public void ClearingOneSectionLeavesTheOthersDirtyAndDropsOnlyItsEdits()
    {
        // The failure this prevents: saving one tab and having the page report itself clean while
        // another tab still holds edits - which is what the eight separate Save buttons did.
        var s = new AdminPageDirtyState();
        s.SetCount("Access", 3);
        s.SetCount("Configuration", 2);

        s.ClearSection("Access");

        Assert.True(s.IsDirty);
        Assert.Equal(2, s.DirtyCount);
        Assert.Equal(1, s.DirtySectionCount);
        Assert.True(s.IsSectionDirty("Configuration"));
        Assert.Equal("2 unsaved changes in Configuration", s.Summary());
    }

    [Fact]
    public void ClearWipesEveryEdit()
    {
        var s = new AdminPageDirtyState();
        s.SetCount("Access", 4);
        s.Increment("Configuration");

        s.Clear();

        Assert.False(s.IsDirty);
        Assert.Equal(0, s.DirtyCount);
        Assert.Equal(0, s.DirtySectionCount);
        Assert.Empty(s.Summary());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSectionNamesAreIgnored(string section)
    {
        // Tracking under an empty key would make the count right and the summary useless.
        var s = new AdminPageDirtyState();
        s.Increment(section);
        s.SetCount(section, 7);

        Assert.False(s.IsDirty);
        Assert.Equal(0, s.DirtyCount);
    }

    // ---------------------------------------------------------------- Change notification

    [Fact]
    public void NotifiesOnEveryCountChange()
    {
        // The save bar re-renders on this event, and the number it shows now changes on each
        // edit, so each edit has to raise it.
        var s = new AdminPageDirtyState();
        var fired = 0;
        s.Changed += () => fired++;

        s.Increment("Access");
        s.Increment("Access");
        s.Increment("Access");

        Assert.Equal(3, fired);
    }

    [Fact]
    public void ReCountingToTheSameTotalIsSilent()
    {
        // Diffing pages recount on every input event, including ones that change nothing. Firing
        // then would thrash the circuit for no visible difference.
        var s = new AdminPageDirtyState();
        s.SetCount("Configuration", 1);

        var fired = 0;
        s.Changed += () => fired++;
        s.SetCount("Configuration", 1);
        s.SetCount("Configuration", 1);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void NotifiesWhenBecomingClean()
    {
        var s = new AdminPageDirtyState();
        s.Increment("Access");

        var fired = 0;
        s.Changed += () => fired++;
        s.ClearSection("Access");

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ClearingAnAlreadyCleanSectionIsSilent()
    {
        var s = new AdminPageDirtyState();
        var fired = 0;
        s.Changed += () => fired++;

        s.ClearSection("Access");
        s.Clear();

        Assert.Equal(0, fired);
    }

    // ---------------------------------------------------------------- Summary text

    [Fact]
    public void SummaryNamesTheSingleOffendingSection()
    {
        // "1 unsaved change" alone would leave an operator hunting across four tabs.
        var s = new AdminPageDirtyState();
        s.Increment("Access");

        Assert.Equal("1 unsaved change in Access", s.Summary());
    }

    [Fact]
    public void SummaryCountsEditsWithinOneSectionRatherThanTheSection()
    {
        // The owner ruling of 2026-09-02: three toggles in one section read as three.
        var s = new AdminPageDirtyState();
        s.SetCount("Modules", 3);

        Assert.Equal("3 unsaved changes in Modules", s.Summary());
    }

    [Fact]
    public void SummaryStaysSingularOnlyForExactlyOneEdit()
    {
        var s = new AdminPageDirtyState();
        s.Increment("Modules");
        Assert.Equal("1 unsaved change in Modules", s.Summary());

        s.Increment("Protected");
        Assert.Equal("2 unsaved changes in Modules, Protected", s.Summary());
    }

    [Fact]
    public void SummaryOrderIsStable()
    {
        // Two pages marking sections in different orders must render the same text, or the bar
        // appears to flicker between equivalent states.
        var a = new AdminPageDirtyState();
        a.Increment("Access");
        a.SetCount("Modules", 2);

        var b = new AdminPageDirtyState();
        b.SetCount("Modules", 2);
        b.Increment("Access");

        Assert.Equal(a.Summary(), b.Summary());
        Assert.Equal("3 unsaved changes in Access, Modules", a.Summary());
    }
}
