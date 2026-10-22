using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26c: the Admin Event Log marked an edit "Reversed" - and hid its Undo button - whenever any
/// record named it as its <c>originalOperationId</c>, a FAILED reversal's included.
/// </summary>
/// <remarks>
/// The undo service writes that field on every outcome, so an undo that was denied, refused or
/// threw told the next reader of the log that the change had been retracted, and took away the
/// button to try again. The rule is tested on plain entries, then on the records the undo service
/// really writes, through <see cref="AttributeEditorHarness"/>. The page is not rendered; its
/// wiring is a source scan.
/// </remarks>
public sealed class ReversedOperationsTests : IDisposable
{
    private readonly AttributeEditorHarness _h = new();

    public void Dispose() => _h.Dispose();

    private sealed record Entry(string? OriginalOperationId, string Result);

    private static HashSet<string> Reversed(params Entry[] entries) =>
        ReversedOperations.From(entries, e => e.OriginalOperationId, e => e.Result);

    [Fact]
    public void ASuccessfulReversal_MarksItsOperation()
    {
        Assert.Contains("op-1", Reversed(new Entry("op-1", "Success")));
    }

    [Fact]
    public void AFailedReversal_DoesNotMarkItsOperation()
    {
        Assert.Empty(Reversed(new Entry("op-1", "Failed")));
    }

    [Fact]
    public void AFailedAttemptFollowedByASuccessfulOne_Marks()
    {
        Assert.Contains("op-1", Reversed(new Entry("op-1", "Failed"), new Entry("op-1", "Success")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEventNamingNoOperation_MarksNothing(string? originalOperationId)
    {
        Assert.Empty(Reversed(new Entry(originalOperationId, "Success")));
    }

    [Fact]
    public void OperationIdsCompareIgnoringCase()
    {
        Assert.Contains("OP-1", Reversed(new Entry("op-1", "Success")));
    }

    [Fact]
    public async Task AReversalThatFailed_LeavesItsEditUndoable()
    {
        // The record a denied Set-ADUser leaves behind, read back as the page reads it.
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        _h.Ad.SetFailure = "Insufficient access rights to perform the operation";

        var result = await Undo(alex);

        Assert.False(result.Success);
        Assert.Contains(_h.AuditEvents(), e => e.TryGetProperty("originalOperationId", out _));
        Assert.DoesNotContain("op-forward-1", FromRecords());
    }

    [Fact]
    public async Task AReversalThatSucceeded_MarksItsEditReversed()
    {
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");

        var result = await Undo(alex);

        Assert.True(result.Success, result.Error);
        Assert.Contains("op-forward-1", FromRecords());
    }

    [Fact]
    public void ThePageTakesItsMarkersFromTheRule()
    {
        var page = RazorSyntax.CommentsBlanked(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "AdminEventLog.razor")));

        Assert.Contains(
            "reversedOperationIds = ReversedOperations.From(allEvents, e => e.OriginalOperationId, e => e.Result);",
            page, StringComparison.Ordinal);

        // The one other writer is the in-session add, made only after a successful undo. A second
        // Add is the old loop come back.
        var adds = Regex.Matches(page, @"reversedOperationIds\.Add\(");
        Assert.True(adds.Count == 1, $"expected only the in-session add after a successful undo; found {adds.Count}");
        Assert.Contains("reversedOperationIds.Add(undoPreview.OriginalOperationId)", page, StringComparison.Ordinal);
    }

    private Task<UndoResult> Undo(LocalAd.User user) =>
        _h.CreateUndoService().ExecuteUndoAsync(
            AttributeEditorHarness.ForwardEdit(user, "title", from: "Analyst", to: "Lead Analyst"),
            "EXAMPLE\\operator", "10.0.0.5", "CHG0300", AttributeEditorHarness.Operator());

    private HashSet<string> FromRecords() =>
        ReversedOperations.From(
            _h.AuditEvents(),
            e => e.TryGetProperty("originalOperationId", out var id) ? id.GetString() : null,
            e => e.TryGetProperty("result", out var outcome) ? outcome.GetString() : null);
}
