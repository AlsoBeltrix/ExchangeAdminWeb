using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Source-level guards for the Migration Status table's bulk selection and ticket entry.
/// </summary>
/// <remarks>
/// THESE ARE TRIPWIRES, NOT BEHAVIOURAL COVERAGE. The repo has no bUnit harness, so no test can
/// render Migration.razor or exercise one of its handlers; the decisions themselves live in
/// MigrationBatchActionPlanner and are tested properly there. What these prove is that the PAGE
/// still calls that logic, and that the aggregating executor has not been forked.
///
/// That is the gap worth guarding here. Every review finding in this repo's recent history has
/// been the same shape - the service was right and the page was wrong - and the page is the one
/// part nothing else can see.
///
/// Every assertion below is anchored to the specific markup or method body it covers, never to the
/// file as a whole. Two of the blr-4 guards were satisfied by a broken page because they matched a
/// spinner that happened to live elsewhere in the same file; a guard a broken page satisfies is
/// worse than no guard, because it reads as coverage.
/// </remarks>
public class MigrationStatusPageTests
{
    [Fact]
    public void BatchRows_CarryASelectionCheckbox()
    {
        // Anchored INSIDE the batch loop's row markup. A checkbox elsewhere on the page - the
        // auto-start options on the Single and Bulk tabs are four of them - must not satisfy this.
        var row = GetBatchRowMarkup();

        Assert.Contains("type=\"checkbox\"", row, StringComparison.Ordinal);
        Assert.Contains("ToggleBatchSelected", row, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchSelection_IsKeyedOnBatchNameNotRowIndex()
    {
        // The defect this prevents is silent and total: the table re-sorts on every header click
        // and reloads after every action, so an index-keyed selection retargets to whatever now
        // sits in that position - and the next Delete removes batches the operator never chose.
        var page = ReadPage();

        Assert.Contains(
            "private readonly HashSet<string> selectedBatches = new(StringComparer.OrdinalIgnoreCase);",
            page,
            StringComparison.Ordinal);

        var row = GetBatchRowMarkup();
        Assert.Contains("ToggleBatchSelected(batchName", row, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionIsPrunedWhenTheTableReloads()
    {
        // A batch removed by another operator, or by Exchange, must not stay ticked.
        var body = GetMethodBody("LoadMigrationStatus");

        Assert.Contains("PruneSelection()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PruneSelection_DelegatesToThePlanner()
    {
        // Not a second implementation of the same rule in the page.
        var body = GetMethodBody("PruneSelection");

        Assert.Contains("MigrationBatchActionPlanner.PruneSelection", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("StageDeleteSelected", "MigrationBatchAction.Delete")]
    [InlineData("StageResumeSelected", "MigrationBatchAction.Resume")]
    public void BulkStagingRoutesThroughThePlanner(string method, string action)
    {
        var body = GetMethodBody(method);

        Assert.Contains(action, body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSelectionPlanIsRecomputedWhenTheTicketIsConfirmed()
    {
        // The operator can sit at the ticket field indefinitely while the table reloads underneath
        // them. A plan computed only at staging time would act on a stale set - the same class of
        // staleness as pps-1(b), where an on-prem write used a protection verdict computed before
        // a confirmation dialog.
        var body = GetMethodBody("StageSelectionAction");

        var planCalls = Regex.Matches(body, @"MigrationBatchActionPlanner\.Plan\(").Count;
        Assert.True(planCalls >= 2,
            $"expected the plan to be computed at staging AND inside the confirm callback, found {planCalls} call(s)");
        Assert.Contains("ticket =>", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ClearCompletedBatches")]
    [InlineData("DeleteSelectedBatches")]
    [InlineData("ResumeSelectedBatches")]
    public void EveryBulkPathUsesTheOneAggregatingExecutor(string method)
    {
        // Clear Completed is included deliberately: it is the code the executor was extracted FROM,
        // and it already got per-item aggregation and audit-failure-as-warning right. If it ever
        // stops routing through the shared executor, the extraction has been undone and there are
        // two sets of aggregation rules again.
        var body = GetMethodBody(method);

        Assert.Contains("ExecuteBulkBatchAction", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBulkExecutorAuditsEveryBatchIndividually()
    {
        // One audit event per batch, INSIDE the loop. A run-level event cannot answer which batch
        // was removed, which is the question the audit log exists to answer.
        var body = GetMethodBody("ExecuteBulkBatchAction");

        var loop = ExtractBlock(body, "foreach (var batchName in batchNames)");
        Assert.Contains("Audit.LogMigrationAction", loop, StringComparison.Ordinal);
        Assert.Contains("ticket", loop, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBulkExecutorReChecksAuthorizationAndAuditsADenial()
    {
        // Hiding the checkbox column is not a security control (Constitution, Never Do).
        var body = GetMethodBody("ExecuteBulkBatchAction");

        Assert.Contains("AuthorizationService.AuthorizeAsync", body, StringComparison.Ordinal);
        Assert.Contains("MigrationManage", body, StringComparison.Ordinal);
        Assert.Contains("_Denied", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBulkExecutorAggregatesPerBatchFailures()
    {
        // A loop over N items must never report blanket success.
        var body = GetMethodBody("ExecuteBulkBatchAction");

        Assert.Contains("errors.Add", body, StringComparison.Ordinal);
        Assert.Contains("errors.Count == 0", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAuditWriteFailureIsAWarningNotAnOperationFailure()
    {
        // An audit failure must not make an already-completed removal look failed - the removal
        // really happened, and reporting it as failed invites a second attempt.
        var body = GetMethodBody("ExecuteBulkBatchAction");

        Assert.Contains("auditWarnings", body, StringComparison.Ordinal);
    }

    [Fact]
    public void SkippedBatchesAreReportedAndAreNotFailures()
    {
        // Owner ruling D2(a). Skips travel in the message via the planner's DescribeSkipped and
        // are never added to the error list - a wholly ineligible selection is "nothing to do".
        var body = GetMethodBody("ExecuteBulkBatchAction");

        Assert.Contains("MigrationBatchActionPlanner.DescribeSkipped", body, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"errors\.Add\([^)]*[Ss]kip"), body);
    }

    [Fact]
    public void NothingEligibleWritesNoAuditEventAndNotifiesNobody()
    {
        // No write was attempted, so there is no security event to record and nothing to announce.
        // The early return must sit BEFORE the loop, the notification, and the audit call.
        var body = GetMethodBody("ExecuteBulkBatchAction");

        var guard = body.IndexOf("batchNames.Count == 0", StringComparison.Ordinal);
        Assert.True(guard > 0, "expected an empty-selection early return in the bulk executor");

        var loop = body.IndexOf("foreach (var batchName in batchNames)", StringComparison.Ordinal);
        var notify = body.IndexOf("SendAdminNotificationAsync", StringComparison.Ordinal);
        Assert.True(guard < loop, "the empty-selection guard must precede the action loop");
        Assert.True(guard < notify, "the empty-selection guard must precede the admin notification");
    }

    [Fact]
    public void OneAdminNotificationPerRunNotPerBatch()
    {
        // Fifty emails for one operator action is a self-inflicted denial of the mailbox.
        var body = GetMethodBody("ExecuteBulkBatchAction");

        var loop = ExtractBlock(body, "foreach (var batchName in batchNames)");
        Assert.DoesNotContain("SendAdminNotificationAsync", loop, StringComparison.Ordinal);
        Assert.Contains("SendAdminNotificationAsync", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PerRowButtonsReadThePlannersStatusRulesRatherThanTheirOwn()
    {
        // The single definition of which statuses each action permits. Two copies is how a row
        // button and a bulk action come to disagree about the same batch - the operator ticks a
        // row whose own Delete button is showing, and the bulk action skips it.
        var row = GetBatchRowMarkup();

        Assert.Contains(
            "MigrationBatchActionPlanner.Applies(MigrationBatchAction.Delete, batch.Status)",
            row,
            StringComparison.Ordinal);
        Assert.Contains(
            "MigrationBatchActionPlanner.Applies(MigrationBatchAction.Resume, batch.Status)",
            row,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheExpandedDetailsRowSpansTheSelectionColumn()
    {
        // A wrong colspan does not fail the build and is invisible until a batch is expanded.
        var page = ReadPage();

        Assert.Contains("colspan=\"@(canManage ? 9 : 8)\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSelectionToolbarIsNotDisabledByAnIneligibleSelection()
    {
        // D2(b) was rejected: disabling the bulk button on a mixed selection sends the operator
        // back to acting one row at a time, which is the reported problem. The buttons gate on an
        // action already running, never on what is ticked.
        var toolbar = ExtractBlock(ReadPage(), "@if (canManage && selectedBatches.Count > 0)");

        Assert.Contains("StageDeleteSelected", toolbar, StringComparison.Ordinal);
        Assert.Contains("StageResumeSelected", toolbar, StringComparison.Ordinal);
        Assert.DoesNotContain("Applies(", toolbar, StringComparison.Ordinal);
        Assert.DoesNotContain("Eligible.Count == 0", toolbar, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePerRowTicketFieldRendersInsideTheBatchLoop()
    {
        // The reported defect: the ticket input lived above the table, so clicking Resume on row 47
        // of 50 put it off-screen while every button in that row went disabled - the only visible
        // feedback was that the buttons had stopped working.
        //
        // This is a tripwire and cannot prove what an operator sees. Manual check 5 is the evidence.
        var row = GetBatchRowMarkup();

        Assert.Contains("pendingActionTarget == batchName", row, StringComparison.Ordinal);
        Assert.Contains("@PendingActionConfirm", row, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePerUserTicketFieldRendersInsideTheUserLoop()
    {
        // mbs-1. StageUserAction sets pendingActionTarget to an EMAIL, which never equals a batch
        // name - so before the fix every per-user action fell through to the top-of-table bar,
        // reproducing the reported off-screen prompt one level down, inside an expanded batch.
        //
        // Anchored inside the user loop specifically: the batch-row confirm added in slice 3 would
        // have satisfied a page-wide match while this half stayed broken, which is precisely the
        // state being fixed.
        var userLoop = GetUserRowMarkup();

        Assert.Contains("pendingActionTarget == user.EmailAddress", userLoop, StringComparison.Ordinal);
        Assert.Contains("@PendingActionConfirm", userLoop, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConfirmBarIsDefinedOnceAndRenderedInEveryPlace()
    {
        // Two copies of the control the operator has to find would drift. One RenderFragment,
        // three render sites: under the acting batch row, under the acting user row, and above the
        // table for an action naming no row at all.
        var page = ReadPage();

        var declarations = Regex.Matches(page, @"private RenderFragment PendingActionConfirm").Count;
        Assert.Equal(1, declarations);

        var uses = Regex.Matches(page, @"@PendingActionConfirm").Count;
        Assert.True(uses >= 3, $"expected the fragment at all three render sites, found {uses}");
    }

    [Fact]
    public void ActionsThatNameNoRowKeepTheTopOfTableBar()
    {
        // Clear Completed and the two bulk actions target "N batch(es)", which matches no row. The
        // counterweight to the two guards above: the move must not have made their confirm bar
        // unreachable. Same reasoning covers a per-row action whose row has since disappeared.
        var page = ReadPage();

        Assert.Contains(
            "@if (pendingActionLabel != null && !PendingActionNamesALoadedRow)",
            page,
            StringComparison.Ordinal);

        // Both row kinds, or the half that is not tested is the half that regresses.
        var body = GetMemberBody("PendingActionNamesALoadedRow");
        Assert.Contains("migrationBatches?.Any(", body, StringComparison.Ordinal);
        Assert.Contains("batchUsers?.Any(", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The markup emitted per batch: the whole body of the batches loop.
    /// </summary>
    /// <remarks>
    /// Brace-balanced rather than delimited by a marker string. The first cut ended the slice at
    /// the first "@if (expandedBatch == batch.BatchName && batchUsers != null)", not realising the
    /// same text appears earlier inside the Details button - so the slice stopped short of the row
    /// markup it was meant to cover, and a guard reported a real change as missing. A marker that
    /// occurs more than once is not a boundary.
    /// </remarks>
    private static string GetBatchRowMarkup() =>
        ExtractBlock(ReadPage(), "@foreach (var batch in GetSortedBatches())");

    /// <summary>The markup emitted per user row inside an expanded batch.</summary>
    private static string GetUserRowMarkup() =>
        ExtractBlock(ReadPage(), "@foreach (var user in batchUsers)");

    /// <summary>
    /// The brace-balanced block introduced by <paramref name="opener"/>, so an assertion can be
    /// scoped to a loop or conditional rather than the whole method.
    /// </summary>
    private static string ExtractBlock(string source, string opener)
    {
        var start = source.IndexOf(opener, StringComparison.Ordinal);
        Assert.True(start >= 0, $"block '{opener}' not found");

        var open = source.IndexOf('{', start + opener.Length);
        Assert.True(open > 0, $"no opening brace after '{opener}'");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }

        Assert.Fail($"unbalanced braces after '{opener}'");
        return "";
    }

    private static string ReadPage() =>
        File.ReadAllText(Path.Combine(GetPagesDirectory(), "Migration.razor"));

    private static string GetMethodBody(string methodName) =>
        GetMemberSource($@"private\s+(async\s+)?[A-Za-z][^\r\n=]*?\b{Regex.Escape(methodName)}\s*\(", methodName);

    /// <summary>Same, for an expression-bodied property, which has no parameter list.</summary>
    private static string GetMemberBody(string memberName) =>
        GetMemberSource($@"private\s+[A-Za-z][^\r\n(]*?\b{Regex.Escape(memberName)}\s*=>", memberName);

    private static string GetMemberSource(string signaturePattern, string memberName)
    {
        var source = ReadPage();

        var signature = Regex.Match(source, signaturePattern);
        Assert.True(signature.Success, $"member '{memberName}' not found");

        var start = signature.Index;
        var next = Regex.Match(source[(start + signature.Length)..],
            @"\n    private\s+(async\s+)?[A-Za-z]");
        return next.Success
            ? source.Substring(start, signature.Length + next.Index)
            : source[start..];
    }

    private static string GetPagesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var pages = Path.Combine(dir.FullName, "Components", "Pages");
            if (Directory.Exists(pages))
                return pages;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Components/Pages from test base directory.");
    }
}
