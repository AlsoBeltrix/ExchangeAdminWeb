using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-66: the Room Finder preview says what the apply will do with a building that has no room list. aud-a2ab stopped the
/// module creating one, so a row the preview labelled "new" is now refused; the preview tells the operator that, and what
/// to do about it, before they apply.
/// </summary>
/// <remarks>
/// Source-level, because this repo has no bUnit harness and the page's markup cannot be rendered in a test - the same
/// reason <see cref="ValidatedWriteCallSiteTests"/> and the ClickGate and Progress registries scan the page's text. The
/// page is line-keyed, so the edit was made in place: <c>ClickGateTests</c> holds the line count (1758) and fails if it
/// moved, which is this change's other guard.
/// </remarks>
public sealed class ConferenceRoomMissingListPreviewTests
{
    private static string Page() => File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ConferenceRooms.razor"));

    [Fact]
    public void AMissingRoomList_IsNoLongerLabelledNew()
    {
        // The finding: the badge promised a list the apply will not create.
        var page = Page();

        Assert.DoesNotContain("<span class=\"badge bg-info\">new</span>", page, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"!row\.RoomListExists && row\.RoomResolved.*badge bg-danger.*does not exist"), page);
    }

    [Fact]
    public void AMissingRoomList_IsPreviewedAsARefusal_NamingTheListAndWhatToDo()
    {
        // The visible half, in the warnings column the preview already renders: the list by name, that the row will be
        // refused, and that the operator creates it in Exchange Online and re-runs - the refusal's own advice
        // (ConferenceRoomService.RoomListDoesNotExistRefusal), said before the apply rather than after it.
        var page = Page();

        var warning = Assert.Single(Regex.Matches(page, @"if \(!listExists\) preview\.Warnings\.Add\(\$""([^""]+)""\);")
            .Select(m => m.Groups[1].Value));
        Assert.Contains("{preview.RoomListName}", warning, StringComparison.Ordinal);
        Assert.Contains("does not exist", warning, StringComparison.Ordinal);
        Assert.Contains("refused", warning, StringComparison.Ordinal);
        Assert.Contains("create the list in Exchange Online", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePreviewsOtherWarnings_AreUnchanged()
    {
        // The legacy warning was folded onto one line to keep the page's line count, which the line-keyed registries
        // require - its text did not change, and neither did the stray-city one beside it.
        var page = Page();

        Assert.Contains(
            "if (isLegacy) preview.Warnings.Add($\"Using legacy room list '{listName}'. New naming would be '{ConferenceRoomService.BuildRoomListName(row.Building)}'.\");",
            page, StringComparison.Ordinal);
        Assert.Contains("A city-named list '{strayCityList}' also exists.", page, StringComparison.Ordinal);
    }
}
