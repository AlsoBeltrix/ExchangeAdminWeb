namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A29: Admin Bulk Jobs decided authorization once, in
/// <c>OnInitializedAsync</c>, and a Blazor circuit outlives that decision. Revoke an
/// operator's access and their already-open tab kept working - including cancelling and
/// deleting <b>other modules'</b> jobs, which is the reach that makes this page fail-closed in
/// the first place.
/// </summary>
/// <remarks>
/// Source scans, and that is a real limit rather than a formality. This page has no test
/// harness - the reflection harness that drives a live component exists only for Comms-10k -
/// so nothing here proves a revoked operator is actually refused at runtime. What these pin is
/// the ordering the fix depends on: no mutating call reaches the service before the
/// authorization re-check.
///
/// Review of aud-a31 showed scans of this kind passing against deliberately broken code, so
/// these check the ORDER of specific calls inside each handler rather than the mere presence
/// of a string anywhere in the file.
/// </remarks>
public class AdminBulkJobsRevocationTests
{
    private static string PageCode() =>
        RazorSyntax.CommentsBlanked(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "AdminBulkJobs.razor")));

    private static string Handler(string signature)
    {
        var code = PageCode();
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start > 0, $"{signature} not found - this guard is stale.");

        // Bounded by the next member declaration at the same indent.
        var end = code.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not bound {signature} - this guard is stale.");

        return code[start..end];
    }

    [Theory]
    [InlineData("private async Task CancelJob(BulkJob job)", "BulkJobs.CancelJob(")]
    [InlineData("private async Task RemoveJob(BulkJob job)", "BulkJobs.DeleteJob(")]
    public void NoJobIsTouchedBeforeAccessIsRecheckedAtTheMoment(string signature, string mutation)
    {
        var body = Handler(signature);

        var check = body.IndexOf("await StillAuthorized(", StringComparison.Ordinal);
        Assert.True(check > 0, "The handler must re-check access before acting.");

        var acts = body.IndexOf(mutation, StringComparison.Ordinal);
        Assert.True(acts > check,
            $"{mutation} runs before the access re-check, so a revoked operator still reaches it.");

        // The check is worthless if it does not stop the handler.
        var bail = body.IndexOf("return;", check, StringComparison.Ordinal);
        Assert.True(bail > check && bail < acts,
            "The re-check must return out of the handler, not merely be evaluated.");
    }

    [Fact]
    public void TheRecheckAsksThePagesOwnPolicyAndFailsClosed()
    {
        var helper = Handler("private async Task<bool> StillAuthorized(");

        Assert.Contains("AuthorizeAsync(authState.User, \"AdminBulkJobs\")", helper, StringComparison.Ordinal);
        Assert.Contains("return true;", helper, StringComparison.Ordinal);
        Assert.Contains("return false;", helper, StringComparison.Ordinal);

        // A refusal that leaves no record looks exactly like a bug to whoever investigates it.
        Assert.Contains("WriteAudit(action, job, success: false);", helper, StringComparison.Ordinal);
    }
}
