using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2ad: the Room Finder preview marks a building with no room list "new", because the apply creates that list when
/// Exchange affirmatively reports none and then adds the room. This is the module's behaviour before kd-66 and aud-a2ab,
/// restored by the owner's ruling of 2026-10-26: the preview no longer says such a row will be refused.
/// </summary>
/// <remarks>
/// Source-level, because this repo has no bUnit harness and the page's markup cannot be rendered in a test - the same
/// reason <see cref="ValidatedWriteCallSiteTests"/> and the ClickGate and Progress registries scan the page's text. The
/// page is line-keyed, so the restore was made in place: <c>ClickGateTests</c> holds the line count (1758) and fails if it
/// moved.
/// </remarks>
public sealed class ConferenceRoomMissingListPreviewTests
{
    private static string Page() => File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ConferenceRooms.razor"));

    [Fact]
    public void AMissingRoomList_IsLabelledNew()
    {
        // The row will create the list, so the badge says so.
        var page = Page();

        Assert.Contains("@if (!row.RoomListExists && row.RoomResolved) { <span class=\"badge bg-info\">new</span> }", page, StringComparison.Ordinal);
        Assert.DoesNotContain("badge bg-danger\">does not exist", page, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRoomList_IsNotPreviewedAsARefusal()
    {
        // No warning tells the operator the row will be refused or sends them to create the list themselves.
        var page = Page();

        Assert.DoesNotMatch(new Regex(@"if \(!listExists\) preview\.Warnings\.Add"), page);
        Assert.DoesNotContain("This row will be refused", page, StringComparison.Ordinal);
    }
}
