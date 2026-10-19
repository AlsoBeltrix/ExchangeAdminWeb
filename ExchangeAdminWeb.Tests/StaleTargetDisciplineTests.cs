namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit findings A13 and A16, two instances of one discipline: an action must act on the
/// subject it was prepared for, not on whatever the editable state says when it finally runs.
/// A7 was the Wave 1 instance of the same class.
/// </summary>
/// <remarks>
/// Source scans. Neither of these pages has a harness - the reflection harness that drives a
/// live component exists only for Comms-10k - so nothing here proves the behaviour in a
/// browser. What they pin is the specific property each fix rests on, and after the aud-a31
/// review showed scans of this shape passing against broken code, each one checks the
/// ORDER or ABSENCE of a named expression rather than that some string appears somewhere.
///
/// Both read <see cref="RazorSyntax.CommentsBlanked"/>: comments keep, string literals keep,
/// comments blanked - the comments on these fixes name the very fields being asserted on.
/// </remarks>
public class StaleTargetDisciplineTests
{
    private static string Page(params string[] parts) =>
        RazorSyntax.CommentsBlanked(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(parts)));

    // ---- A13: MFA Reset ----------------------------------------------------------------------

    [Fact]
    public void MfaReset_ActsOnTheUserWhoseMethodsAreListed_NotTheEditableBox()
    {
        // The defect: ExecuteReset re-read userPrincipalName, so listing A, editing the box to
        // B and resetting wiped B's methods while the operator looked at A's list - and the
        // audit recorded A's method types against B.
        var page = Page("Components", "Pages", "MfaReset.razor");

        var start = page.IndexOf("private async Task ExecuteReset()", StringComparison.Ordinal);
        Assert.True(start > 0, "ExecuteReset not found - this guard is stale.");
        // ExecuteReset is the last member in the file, so there may be no following
        // declaration to bound on.
        var end = page.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        var body = page[start..(end > start ? end : page.Length)];

        Assert.DoesNotContain("userPrincipalName", body, StringComparison.Ordinal);
        Assert.Contains("listedUpn", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MfaReset_RefusesWhenNothingIsListed()
    {
        // Falling back to the box when nothing is listed would reinstate the defect by the back
        // door, so the empty case must refuse rather than default.
        var page = Page("Components", "Pages", "MfaReset.razor");

        var guard = page.IndexOf("if (string.IsNullOrWhiteSpace(upn))", StringComparison.Ordinal);
        Assert.True(guard > 0, "ExecuteReset must refuse when no user is listed.");

        var returns = page.IndexOf("return;", guard, StringComparison.Ordinal);
        var firstAwait = page.IndexOf("await ", guard, StringComparison.Ordinal);
        Assert.True(returns > 0 && returns < firstAwait,
            "The refusal must return before anything is awaited.");
    }

    [Fact]
    public void MfaReset_NamesTheListedUserOnScreen_NotTheBox()
    {
        // Both headings used to render the live box over another user's results.
        var page = Page("Components", "Pages", "MfaReset.razor");

        Assert.Contains("Confirm MFA Reset for @listedUpn", page, StringComparison.Ordinal);
        Assert.Contains("Authentication Methods for @listedUpn", page, StringComparison.Ordinal);
    }

    // ---- A16: the country picker -------------------------------------------------------------

    [Fact]
    public void CountryPicker_ResyncsWhenItsBoundValueChanges()
    {
        // The defect: a one-shot _initialized latch. A picker reused for a different record
        // kept the previous record's selection, so applying it wrote A's countries onto B.
        var component = Page("Components", "Shared", "CountryCodePicker.razor");

        Assert.DoesNotContain("_initialized", component, StringComparison.Ordinal);

        var start = component.IndexOf("protected override void OnParametersSet()", StringComparison.Ordinal);
        Assert.True(start > 0, "OnParametersSet not found - this guard is stale.");
        var end = component.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound OnParametersSet - this guard is stale.");

        var body = component[start..end];

        // Re-sync is gated on the value having CHANGED, not on a first-time flag.
        Assert.Contains("_syncedFrom, Value", body, StringComparison.Ordinal);
        Assert.Contains("_syncedFrom = Value;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void CountryPicker_DoesNotTreatItsOwnOutputAsARecordChange()
    {
        // Emitting without recording what was emitted means the parameter set that echoes our
        // own value looks like the record changing, and the selection is rebuilt mid-edit.
        var component = Page("Components", "Shared", "CountryCodePicker.razor");

        foreach (var emitter in new[] { "private async Task Apply()", "private async Task ClearAll()" })
        {
            var start = component.IndexOf(emitter, StringComparison.Ordinal);
            Assert.True(start > 0, $"{emitter} not found - this guard is stale.");
            var end = component.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
            var body = component[start..(end > start ? end : component.Length)];

            var record = body.IndexOf("_syncedFrom =", StringComparison.Ordinal);
            var emit = body.IndexOf("ValueChanged.InvokeAsync", StringComparison.Ordinal);
            Assert.True(record > 0 && record < emit,
                $"{emitter} must record what it emits before emitting it.");
        }
    }
}
