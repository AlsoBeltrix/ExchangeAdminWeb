using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/MigrationInterfaceRedesign-Plan.md test obligation 2: no view renders the full set.
/// </summary>
/// <remarks>
/// This is the Defender hazard. Blazor Server pushes a render diff per row over the circuit, and
/// that page died at a few thousand rows; R20 says no view may render the full set, with no
/// exception, including a select-all of every batch. Nothing here can render a component, so the
/// windowing was extracted to <see cref="ListWindow"/> and the count is proved directly. The page
/// keeps one line - the loop over GetPagedBatches - and a markup tripwire in
/// MigrationStatusPageTests anchors it.
/// </remarks>
public class ListWindowTests
{
    private const int PageSize = 40;

    private static List<int> Rows(int count) => Enumerable.Range(1, count).ToList();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(21)]
    [InlineData(49)]
    public void NoPageEverRendersMoreThanThePageSize(int page)
    {
        // The magnitude the plan names: 2000 batches, and 2000 mailboxes in a batch. At 40 a page
        // that is 50 pages, so page 49 is the last one and page 21 is an ordinary middle page.
        var rendered = ListWindow.Slice(Rows(2000), page, PageSize).ToList();

        Assert.True(rendered.Count <= PageSize,
            $"page {page} rendered {rendered.Count} rows; the circuit gets a render diff per row "
            + "and this is exactly how the Defender page died");
    }

    [Fact]
    public void SelectingEveryRowDoesNotChangeWhatIsRendered()
    {
        // R8 says select-all means all, with no cap, and R20 says no view renders the full set.
        // Both hold only because selection and rendering are different things: the window is a
        // function of the page index alone and knows nothing about what is ticked.
        var all = Rows(2000);

        var before = ListWindow.Slice(all, 0, PageSize).ToList();
        var everythingSelected = new HashSet<int>(all);
        var after = ListWindow.Slice(all, 0, PageSize).ToList();

        Assert.Equal(2000, everythingSelected.Count);
        Assert.Equal(before, after);
        Assert.Equal(PageSize, after.Count);
    }

    [Fact]
    public void ThePagesTileTheWholeListExactlyOnce()
    {
        // Off-by-one in either direction is silent: a row that appears on two pages looks like a
        // duplicate batch, and one that appears on none is a batch the operator cannot reach.
        var all = Rows(2000);

        var seen = new List<int>();
        for (var page = 0; page < ListWindow.PageCount(all.Count, PageSize); page++)
            seen.AddRange(ListWindow.Slice(all, page, PageSize));

        Assert.Equal(all, seen);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(40, 1)]
    [InlineData(41, 2)]
    [InlineData(2000, 50)]
    [InlineData(2001, 51)]
    public void PageCountCoversEveryRowAndIsNeverZero(int totalCount, int expected)
    {
        // Never zero: the pager renders "page 1 of N" against it, and 0 would make the Next button
        // enabled on an empty list and the label read as a broken page.
        Assert.Equal(expected, ListWindow.PageCount(totalCount, PageSize));
    }

    [Fact]
    public void APageBeyondTheEndIsClampedBackOntoTheList()
    {
        // The reported shape this prevents: the operator is on page 9, a reload leaves three
        // pages, and the list renders nothing. An empty page and an empty catalogue look
        // identical, so it reads as "all my batches are gone".
        Assert.Equal(2, ListWindow.ClampPage(9, 120, PageSize));
        Assert.Equal(0, ListWindow.ClampPage(9, 0, PageSize));
        Assert.Equal(0, ListWindow.ClampPage(-4, 2000, PageSize));
    }

    [Fact]
    public void TheLabelStatesTheWholeSizeAndNotJustTheWindow()
    {
        // R4's point, applied to the catalogue: "page 1 of 50" does not tell the operator that
        // what they can see is 40 of 2000. The range and the total both have to be in the string.
        Assert.Equal("1-40 of 2000 batches", ListWindow.Label(0, 2000, PageSize, "batches", "No batches"));
        Assert.Equal("1961-2000 of 2000 batches", ListWindow.Label(49, 2000, PageSize, "batches", "No batches"));
        Assert.Equal("1-7 of 7 batches", ListWindow.Label(0, 7, PageSize, "batches", "No batches"));
    }

    [Fact]
    public void TheLabelDescribesThePageThatWillActuallyRender()
    {
        // Asked about page 9 of a 3-page list, Slice plus ClampPage shows the last page. The label
        // has to say the same thing, or the operator reads "361-400 of 120" under 40 visible rows.
        Assert.Equal("81-120 of 120 batches", ListWindow.Label(9, 120, PageSize, "batches", "No batches"));
    }

    [Fact]
    public void AnEmptyListGetsItsOwnWordsRatherThanAZeroRange()
    {
        // "0-0 of 0 batches" is arithmetic leaking into the interface.
        Assert.Equal("No batches", ListWindow.Label(0, 0, PageSize, "batches", "No batches"));
    }

    [Fact]
    public void ANonsensePageSizeIsRefusedRatherThanQuietlyTreatedAsOne()
    {
        // A zero page size would make Slice return nothing on every page and PageCount divide by
        // zero. Failing loudly here is the difference between a bug and an empty screen.
        Assert.Throws<ArgumentOutOfRangeException>(() => ListWindow.Slice(Rows(10), 0, 0).ToList());
        Assert.Throws<ArgumentOutOfRangeException>(() => ListWindow.PageCount(10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ListWindow.Label(0, 10, 0, "batches", "none"));
    }
}
