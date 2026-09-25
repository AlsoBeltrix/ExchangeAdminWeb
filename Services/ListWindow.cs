namespace ExchangeAdminWeb.Services;

/// <summary>
/// The window of rows a paged list renders, and the label that describes it.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from Migration.razor rather than left as three expressions on the component, and the
/// reason is testability, not tidiness. R20 of docs/MigrationInterfaceRedesign-Plan.md exists
/// because Blazor Server pushes a render diff per row over the circuit and the Defender page died
/// that way at a few thousand rows; the plan's test obligation 2 requires proof that the rendered
/// count stays at the page size even with every row selected. This repo has no bUnit harness, so
/// nothing can render the component and count what came out - a source-anchored tripwire on the
/// markup is all that is available there, and a tripwire cannot answer "how many rows for 2000
/// batches on page 9".
/// </para>
/// <para>
/// Here it can be answered exactly. The page keeps the one line that loops over
/// <see cref="Slice"/>, which the markup tripwire anchors, and every decision about WHICH rows and
/// HOW MANY is a pure function tested against real magnitudes.
/// </para>
/// <para>
/// Deliberately generic: S5 pages the mailbox list and S3 the selection pane, and three copies of
/// an off-by-one is three chances to render a blank page 9 that looks like an empty catalogue.
/// </para>
/// </remarks>
public static class ListWindow
{
    /// <summary>
    /// The rows on <paramref name="page"/>, never more than <paramref name="pageSize"/> of them.
    /// </summary>
    /// <remarks>
    /// A page index past the end returns empty rather than throwing or clamping. Clamping here
    /// would hide the caller's bug; the caller's job is to call <see cref="ClampPage"/> whenever
    /// the list changes under it, and <see cref="PageCount"/> is what tells it where the end is.
    /// </remarks>
    public static IEnumerable<T> Slice<T>(IEnumerable<T> rows, int page, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return page < 0 ? [] : rows.Skip(page * pageSize).Take(pageSize);
    }

    /// <summary>
    /// How many pages <paramref name="totalCount"/> rows fill. Always at least 1, so an empty list
    /// is "page 1 of 1" and not "page 1 of 0".
    /// </summary>
    public static int PageCount(int totalCount, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return totalCount <= 0 ? 1 : (totalCount + pageSize - 1) / pageSize;
    }

    /// <summary>
    /// <paramref name="page"/> brought inside the list's current bounds.
    /// </summary>
    /// <remarks>
    /// The failure this exists for: the operator is on page 9, a reload or a filter leaves three
    /// pages, and the list renders nothing. An empty page is indistinguishable from an empty
    /// catalogue, so it reads as "all my batches are gone".
    /// </remarks>
    public static int ClampPage(int page, int totalCount, int pageSize) =>
        Math.Clamp(page, 0, PageCount(totalCount, pageSize) - 1);

    /// <summary>
    /// "1-40 of 847 batches", or <paramref name="emptyLabel"/> when there is nothing to describe.
    /// </summary>
    /// <remarks>
    /// States the WHOLE size, not just the window. "Page 1 of 22" does not tell the operator that
    /// what they are looking at is a sample of 847; "1-40 of 847" does, and the plan's R4 makes
    /// the same point about the selection pager for the same reason.
    /// </remarks>
    public static string Label(int page, int totalCount, int pageSize, string noun, string emptyLabel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        if (totalCount <= 0)
            return emptyLabel;

        // Describe the page actually being rendered. Asked about page 9 of a 3-page list the
        // honest answer is the last page, because that is what Slice plus ClampPage will show.
        var safePage = ClampPage(page, totalCount, pageSize);
        var from = (safePage * pageSize) + 1;
        var to = Math.Min(from + pageSize - 1, totalCount);

        return $"{from}-{to} of {totalCount} {noun}";
    }
}
