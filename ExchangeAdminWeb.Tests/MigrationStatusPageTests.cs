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
    [InlineData("StageRemoveCompletedSelected", "MigrationBatchAction.RemoveCompleted")]
    [InlineData("StageResumeSelected", "MigrationBatchAction.Resume")]
    public void BulkStagingRoutesThroughThePlanner(string method, string action)
    {
        var body = GetMethodBody(method);

        Assert.Contains(action, body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolbarOffersAllThreeActionsWithDistinctNames()
    {
        // D3: three actions, three targets. And the naming half of the owner's complaint - "Clear
        // Completed" sat beside "Clear selection" sharing a word, one irreversible and one
        // harmless. No two buttons in this toolbar may share a leading verb.
        var toolbar = ExtractBlock(ReadPage(), "@if (canManage && selectedBatches.Count > 0)");

        Assert.Contains("StageDeleteSelected", toolbar, StringComparison.Ordinal);
        Assert.Contains("StageRemoveCompletedSelected", toolbar, StringComparison.Ordinal);
        Assert.Contains("StageResumeSelected", toolbar, StringComparison.Ordinal);
        Assert.Contains("Untick all", toolbar, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear selection", toolbar, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStandaloneClearCompletedSweepIsGone()
    {
        // D6. It removed every completed batch in the table regardless of selection - an
        // all-or-nothing destructive sweep, which is precisely what the checkbox request replaced.
        var page = ReadPage();

        Assert.DoesNotContain("StageClearCompleted", page, StringComparison.Ordinal);
        Assert.DoesNotContain("CompletedOrEmptyBatchNames", page, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteConfirmsWithAPerStatusBreakdown()
    {
        // D5: Delete is the only action that accepts in-progress batches, so the operator must see
        // how many of those they are about to destroy before typing a ticket. One step, in the
        // ticket bar - the ticket entry IS the confirmation.
        var body = GetMethodBody("StageDeleteSelected");

        Assert.Contains("This will remove", body, StringComparison.Ordinal);
        Assert.Contains("MigrationBatchActionPlanner.DescribeSelectionByStatus", body, StringComparison.Ordinal);
    }

    [Fact]
    public void NoStatusAllowlistSurvivesInThePageMarkup()
    {
        // The defect that caused round 2: every status comparison was an exact match against a
        // hardcoded list, and CompletedWithErrors was in none of them - so such a batch had no
        // Delete button, no Resume button, was not swept, and drew the unknown-status badge.
        //
        // Scoped to the batch row, where the action buttons live. The batch's OWN status still
        // drives Complete and Stop, which are not part of this work; what must not come back is a
        // row-level allowlist deciding Delete or Resume.
        var row = GetBatchRowMarkup();

        Assert.Contains(
            "MigrationBatchActionPlanner.Applies(MigrationBatchAction.Delete, batch.Status)",
            row,
            StringComparison.Ordinal);
        Assert.Contains(
            "MigrationBatchActionPlanner.Applies(MigrationBatchAction.Resume, batch.Status)",
            row,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"Corrupted\"", row, StringComparison.Ordinal);
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
    [InlineData("DeleteSelectedBatches")]
    [InlineData("RemoveCompletedSelectedBatches")]
    [InlineData("ResumeSelectedBatches")]
    public void EveryBulkPathUsesTheOneAggregatingExecutor(string method)
    {
        // One set of aggregation rules for all three actions: per-batch audit inside the loop,
        // audit failures as warnings, per-item failures aggregated, one notification per run. A
        // fourth path that forked its own loop is how those rules come to differ per button.
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

    [Theory]
    [InlineData("RemoveMigrationBatchAsync", "queued for removal")]
    [InlineData("StopMigrationBatchAsync", "queued to stop")]
    [InlineData("StartMigrationBatchAsync", "queued to start")]
    public void SingleBatchServiceMessagesAlsoSayQueued(string method, string expected)
    {
        // The per-row buttons carry the service's own message straight to the operator, so fixing
        // only the bulk wording would leave the same lie on the row that reported it. Guarded here
        // rather than in a service test because the string is a lambda passed to RunAsync, which
        // needs a live EXO session to invoke.
        var source = ReadServiceSource("MigrationService.cs");

        var start = source.IndexOf($"public Task<PermissionResult> {method}(", StringComparison.Ordinal);
        Assert.True(start > 0, $"{method} not found");

        var end = source.IndexOf("\n    public ", start + 1, StringComparison.Ordinal);
        var body = end > start ? source[start..end] : source[start..];

        Assert.Contains(expected, body, StringComparison.Ordinal);
        Assert.DoesNotContain("' removed.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("' started.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("' stopped.", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedBatchesAreDeselectedAfterTheRun()
    {
        // Owner, on dev: selected two, clicked Remove Completed, "it says it removed but didn't
        // deselect". Pruning alone cannot do this - it only drops batches that have LEFT the table,
        // and removal is asynchronous, so the row is still listed at status Removing and stays
        // ticked. A ticked row over in-flight work invites a second click on the same batch.
        var body = GetMethodBody("ExecuteBulkBatchAction");

        Assert.Contains("selectedBatches.Remove(name)", body, StringComparison.Ordinal);

        // Only the accepted ones. A blanket Clear() would also drop the failures and the skips,
        // which are exactly the rows the operator needs to still have selected.
        Assert.DoesNotContain("selectedBatches.Clear()", body, StringComparison.Ordinal);
        Assert.Contains("accepted.Add(batchName)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedBatchesStaySelected()
    {
        // The counterweight. A batch Exchange refused had nothing queued for it, so retrying is the
        // likely next move and unticking it would make the operator find it again among 50 rows.
        // Guarded by placement: the add sits on the success branch, beside the counter.
        var body = GetMethodBody("ExecuteBulkBatchAction");

        var accept = body.IndexOf("accepted.Add(batchName)", StringComparison.Ordinal);
        var error = body.IndexOf("errors.Add(", StringComparison.Ordinal);
        Assert.True(accept > 0 && error > 0, "expected both branches in the bulk executor");
        Assert.True(accept < error, "the accepted-list add must sit on the success branch, before the error branch");
    }

    [Theory]
    [InlineData("DeleteSelectedBatches")]
    [InlineData("RemoveCompletedSelectedBatches")]
    [InlineData("ResumeSelectedBatches")]
    public void BulkResultsSayQueuedNotDone(string method)
    {
        // Remove-MigrationBatch and Start-MigrationBatch return when Exchange ACCEPTS the request.
        // The batch then sits at Removing or Starting for a while, so "Removed 1 batch(es)" renders
        // over a row the operator can still see - which reads as the app being broken.
        var body = GetMethodBody(method);

        Assert.Contains("Queued", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Removed\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Resumed\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResultExplainsThatExchangeFinishesInTheBackground()
    {
        // "Queued" alone still leaves the operator wondering why the row is still there.
        var body = GetMethodBody("ExecuteBulkBatchAction");

        Assert.Contains("background", body, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("StageRemoveCompletedSelected", toolbar, StringComparison.Ordinal);
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
    public void TheReportButtonIsOfferedOnEveryUserRow()
    {
        // Owner, on dev: "Report is only a button on subrows in CompletedWithErrors migrations, and
        // it should be available on every user row." It was gated on Failed / NeedsApproval / a
        // non-empty ErrorSummary, so it appeared only where the row already looked broken.
        //
        // Third status allowlist on this page to hide something the operator wanted, after Delete
        // and Resume. Report is READ-ONLY - it fetches a diagnostic report and writes nothing - so
        // there is no case for gating it at all.
        var userLoop = GetUserRowMarkup();

        Assert.Contains("LoadUserReport", userLoop, StringComparison.Ordinal);

        // Anchored to the Report button's own block, not the file: the surrounding row still has
        // legitimate status conditions for Complete / Approve / Pause / Resume.
        var button = ExtractSpan(userLoop, "<button class=\"btn btn-sm btn-outline-danger\"", "</button>");
        Assert.Contains("LoadUserReport", button, StringComparison.Ordinal);

        var beforeButton = userLoop[..userLoop.IndexOf("LoadUserReport", StringComparison.Ordinal)];
        var lastConditionalBeforeIt = beforeButton.LastIndexOf("@if (", StringComparison.Ordinal);
        var lastCloseBeforeIt = beforeButton.LastIndexOf('}');
        Assert.True(lastCloseBeforeIt > lastConditionalBeforeIt,
            "the Report button sits inside a conditional; it must render unconditionally for every user row");
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

    [Fact]
    public void EveryDiscardOfBatchUsersAlsoClosesTheOpenReport()
    {
        // The reported defect: a batch deleted and recreated under the same name is a DIFFERENT
        // migration with the same key, but the open report panel is keyed on the email address
        // alone (":771"), so the snapshot fetched for the old migration kept rendering under the
        // new one's row. See docs/MigrationStaleReport-Plan.md.
        //
        // Anchored per OCCURRENCE, not per method: ToggleBatchDetails and SearchUser each discard
        // batchUsers on two independent paths, and a method-anchored assertion passes while only
        // one of them is guarded.
        var page = ReadPage();

        var discards = Regex.Matches(page, @"batchUsers = null;[ \t]*\r?\n[ \t]*(?<next>[^\r\n]+)");
        Assert.True(discards.Count >= 5,
            $"expected every batchUsers discard site to still be present, found {discards.Count}");

        foreach (Match discard in discards)
        {
            Assert.StartsWith(
                "CloseUserReport();",
                discard.Groups["next"].Value.Trim(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BothBranchesOfToggleBatchDetailsCloseTheReport()
    {
        // Collapse discards the rows; expand replaces them with another batch's. Both leave a
        // rendered report pointing at rows that are gone or belong to someone else.
        var body = GetMethodBody("ToggleBatchDetails");

        var collapse = ExtractBlock(body, "if (expandedBatch == batchName)");
        Assert.Contains("CloseUserReport();", collapse, StringComparison.Ordinal);

        var expand = body[(body.IndexOf(collapse, StringComparison.Ordinal) + collapse.Length)..];
        Assert.Contains("CloseUserReport();", expand, StringComparison.Ordinal);
        Assert.Contains("GetMigrationBatchUsersAsync", expand, StringComparison.Ordinal);
    }

    [Fact]
    public void BothBranchesOfSearchUserCloseTheReport()
    {
        // Search jumps the operator to a different expanded batch. Same two-branch shape: one path
        // matches a batch name, the other resolves a user to their batch.
        var body = GetMethodBody("SearchUser");

        var batchMatch = ExtractBlock(body, "if (matchedBatch != null)");
        Assert.Contains("CloseUserReport();", batchMatch, StringComparison.Ordinal);

        var userMatch = body[(body.IndexOf(batchMatch, StringComparison.Ordinal) + batchMatch.Length)..];
        Assert.Contains("CloseUserReport();", userMatch, StringComparison.Ordinal);
        Assert.Contains("GetMigrationBatchUsersAsync", userMatch, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshBatchUsersClosesTheReportBeforeRefetching()
    {
        // This one never nulls batchUsers - it refetches over the top - so the discard guard above
        // cannot see it. The report still describes the pre-refresh state of the row.
        var body = GetMethodBody("RefreshBatchUsers");

        var close = body.IndexOf("CloseUserReport();", StringComparison.Ordinal);
        var fetch = body.IndexOf("GetMigrationBatchUsersAsync", StringComparison.Ordinal);
        Assert.True(close > 0, "RefreshBatchUsers must close the open report");
        Assert.True(fetch > close, "the close must precede the refetch");
    }
    [Fact]
    public void NoCodePathAssignsBatchUsersWithoutClosingTheReport()
    {
        // msr-1. The three assertions above are all satisfied by a page that still leaks: they
        // require the close to happen BEFORE the refetch, and closing before an await is not
        // enough. The refresh-in-place paths leave the old rows - and their enabled Report button
        // - rendered for the whole Exchange call, so a report opened inside that window captures
        // the already-bumped generation and survives the swap.
        //
        // The structural fix is that exactly one place may replace the rendered rows, and it
        // closes the report itself. Anchored per OCCURRENCE so that an eighth reload path added
        // later cannot quietly reintroduce a bare assignment.
        var page = ReadPage();

        var assignments = Regex.Matches(page, @"(?<![A-Za-z])batchUsers\s*=\s*(?<value>[^\r\n]+)");
        Assert.True(assignments.Count >= 6,
            $"expected every batchUsers assignment site to still be present, found {assignments.Count}");

        var replacements = assignments
            .Select(a => a.Groups["value"].Value.Trim())
            .Where(v => v != "null;")
            .ToList();

        Assert.Equal(new[] { "users;" }, replacements);
    }

    [Fact]
    public void TheRowReplacementHelperClosesTheReportBeforeAssigning()
    {
        // The single permitted assignment site. The close must land on the same side of the await
        // as the new rows: the caller awaits, then calls this, so the generation moves after the
        // fetch returns and any report opened mid-reload is discarded.
        var body = GetMethodBody("ReplaceBatchUsers");

        var close = body.IndexOf("CloseUserReport();", StringComparison.Ordinal);
        var assign = body.IndexOf("batchUsers = users;", StringComparison.Ordinal);
        Assert.True(close > 0, "ReplaceBatchUsers must close the open report");
        Assert.True(assign > close, "the close must precede the assignment");
    }

    [Fact]
    public void EveryRowRefetchIsHandedStraightToTheHelper()
    {
        // Routing the awaited fetch directly into the helper is what puts the close after the
        // await. Assigning to a local first and replacing later would satisfy the test above and
        // still leave a window; every fetch occurrence has to be wrapped.
        var page = ReadPage();

        var fetches = Regex.Matches(page, @"GetMigrationBatchUsersAsync\(");
        var wrapped = Regex.Matches(page, @"ReplaceBatchUsers\(await MigrationSvc\.GetMigrationBatchUsersAsync\(");
        Assert.True(fetches.Count >= 5,
            $"expected every row refetch site to still be present, found {fetches.Count}");
        Assert.Equal(fetches.Count, wrapped.Count);
    }

    [Fact]
    public void EverySetterOfTheRowLoadingFlagClearsItInAFinally()
    {
        // loadingBatchUsers gates the two refresh arrows today, and is about to gate the whole
        // page. A setter that clears it only on the success path leaves it stuck for the life of
        // the circuit the first time Exchange throws - SearchUser did exactly that, clearing it
        // after each await while its finally cleared only isSearching. Anchored per occurrence
        // rather than per method so a fourth setter added later cannot skip the finally.
        var page = ReadPage();

        var setters = Regex.Matches(page, @"(?<![A-Za-z])loadingBatchUsers\s*=(?!=)\s*(?<value>[^\r\n]+)")
            .Where(m => m.Groups["value"].Value.Trim() != "null;")
            .ToList();

        Assert.True(setters.Count >= 4,
            $"expected every row-loading setter to still be present, found {setters.Count}");

        foreach (var setter in setters)
        {
            var method = EnclosingMethodName(page, setter.Index);
            var body = GetMethodBody(method);

            Assert.True(body.Contains("finally", StringComparison.Ordinal),
                $"{method} sets loadingBatchUsers but has no finally to clear it in");

            var cleanup = ExtractBlock(body, "finally");
            Assert.True(cleanup.Contains("loadingBatchUsers = null;", StringComparison.Ordinal),
                $"{method} sets loadingBatchUsers but does not clear it in its finally; a throw "
                + "there leaves the flag stuck and the controls it gates dead");
        }
    }

    [Fact]
    public void AUserActionThatReloadsTheRowsClosesTheReport()
    {
        // The site the first draft of the plan missed (review finding MSR-A). A per-user action -
        // Complete, Approve, Pause, Resume - reloads the batch's users on success, and the report
        // panel is describing the state that action just changed.
        var body = GetMethodBody("ExecuteUserAction");

        var reload = ExtractBlock(body, "if (result.Success && expandedBatch != null)");
        Assert.Contains("CloseUserReport();", reload, StringComparison.Ordinal);
        Assert.Contains("GetMigrationBatchUsersAsync", reload, StringComparison.Ordinal);
    }

    [Fact]
    public void ReloadingTheBatchTableClosesTheReport()
    {
        var body = GetMethodBody("LoadMigrationStatus");

        Assert.Contains("CloseUserReport();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ClosingTheReportClearsEveryFieldAndBumpsTheGeneration()
    {
        // One helper owns the clear, so a new reload path has one call to make rather than four
        // fields to remember. The generation bump is what makes the clear hold: without it a fetch
        // already in flight lands afterwards and repopulates the panel the reload just emptied.
        var body = GetMethodBody("CloseUserReport");

        Assert.Contains("reportGeneration++", body, StringComparison.Ordinal);
        Assert.Contains("reportUser = null;", body, StringComparison.Ordinal);
        Assert.Contains("userReport = null;", body, StringComparison.Ordinal);
        Assert.Contains("loadingReport = null;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void NoReportTextIsWrittenByASupersededFetch()
    {
        // Get-MigrationUserStatistics -IncludeReport is slow. The operator can click Report, then
        // collapse or search, and the continuation resumes against a table that has moved on -
        // rendering a report under whichever row now holds that address.
        //
        // Guarded by ORDER, not presence: the generation check must be the last thing between the
        // await and every write to userReport, on the success path and the error path alike.
        var body = GetMethodBody("LoadUserReport");

        Assert.Contains("var generation = reportGeneration;", body, StringComparison.Ordinal);

        var writes = Regex.Matches(body, @"userReport = ");
        Assert.True(writes.Count >= 2,
            $"expected the success and error writes, found {writes.Count}");

        foreach (Match write in writes)
        {
            var preceding = body[..write.Index];
            var guard = preceding.LastIndexOf(
                "if (generation != reportGeneration) return;", StringComparison.Ordinal);
            var resumption = preceding.LastIndexOf("await ", StringComparison.Ordinal);

            Assert.True(guard > resumption,
                "every report write after the await must sit behind the generation check");
        }
    }

    [Fact]
    public void ASupersededFetchDoesNotResurrectTheSpinner()
    {
        // finally runs on the superseded continuation too. An unguarded loadingReport = null there
        // is harmless today, but the symmetric bug - writing loadingReport back - is not, and the
        // guard is what documents that the whole continuation is void, not just its assignments.
        var body = GetMethodBody("LoadUserReport");

        Assert.Matches(
            new Regex(@"if \(generation == reportGeneration\)\s*\r?\n\s*loadingReport = null;"),
            body);
    }

    [Fact]
    public void TheCloseButtonCallsTheSharedHelper()
    {
        // It was an inline lambda nulling two of the four fields. An inline reset cannot bump the
        // generation, so a manual Close during a slow fetch reopened the panel by itself.
        var panel = ExtractSpan(
            GetUserRowMarkup(),
            "@if (reportUser == user.EmailAddress && userReport != null)",
            "@userReport");

        Assert.Contains("@onclick=\"CloseUserReport\"", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("userReport = null", panel, StringComparison.Ordinal);
    }

    /// <summary>Every flag that means "an async operation is running on this page right now".</summary>
    private static readonly string[] InFlightFlags =
    {
        "isLoading", "isCreating", "isLoadingStatus", "isSearching",
        "loadingBatchUsers", "actionInProgress", "loadingReport", "isDownloadingCsv",
    };

    /// <summary>
    /// The buttons that must stay live while the page is busy, each with the reason it is safe.
    /// </summary>
    private static readonly Dictionary<string, string> ExemptButtons = new(StringComparer.Ordinal)
    {
        ["DownloadSampleCsv"] =
            "serves a constant; touches no page state and no in-flight operation",
        ["() => batchActionResult = null"] =
            "dismisses a result banner; gating it would trap the message on screen",
        ["CloseUserReport"] =
            "rendered only once a report has landed, so there is no pull for it to interrupt",
        ["CancelPendingAction"] =
            "the operator must always be able to back out of a staged action; it keeps its own "
            + "actionInProgress guard instead",
    };

    [Fact]
    public void EveryButtonConsultsTheBusyPredicate()
    {
        // The owner's ruling: it must not be possible to click a button until the system is ready
        // for that click to be definitively executed. A Blazor circuit stays interactive across
        // every await, so a button that guards only against re-entering itself can still be
        // clicked while some other operation is in flight - and the click is then accepted and
        // silently discarded, or lands on a table being replaced underneath it. One page-level
        // predicate answers that for every control. This walks the markup rather than a list of
        // known buttons, so a button added later cannot quietly skip the gate.
        var ungated = GetButtonTags(ReadPage())
            .Where(tag => !tag.Contains("IsBusy", StringComparison.Ordinal))
            .Select(tag => Regex.Match(tag, @"@onclick=""(?<handler>[^""]*)""").Groups["handler"].Value)
            .OrderBy(handler => handler, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ExemptButtons.Keys.OrderBy(handler => handler, StringComparer.Ordinal).ToList(),
            ungated);
    }

    [Fact]
    public void TheBusyPredicateNamesEveryInFlightFlag()
    {
        // The gate is only as wide as the predicate. A new long-running operation that adds its
        // own flag and forgets to name it here leaves every control on the page live for the
        // whole of it, and every button still looks correctly guarded.
        var predicate = GetMemberBody("IsBusy");

        foreach (var flag in InFlightFlags)
            Assert.True(Regex.IsMatch(predicate, WholeWord(flag)),
                $"IsBusy does not consult {flag}; the page stays clickable while it is set");
    }

    [Fact]
    public void TheBusyPredicateExcludesStagedConfirmationState()
    {
        // The one mistake that would make this page unusable, and the reason the plan was reviewed
        // before it was written. pendingActionLabel means "waiting for the operator to type a
        // ticket", not "busy". The Confirm button is rendered ONLY while pendingActionLabel is
        // non-null, so folding that field into the shared predicate disables Confirm at the only
        // moment it is ever shown - no destructive action could be executed again - while every
        // other guard on the page still reads as correct.
        var predicate = GetMemberBody("IsBusy");

        foreach (var staged in new[]
                 { "pendingActionLabel", "pendingActionTicket", "pendingActionTarget", "pendingActionCallback" })
        {
            Assert.False(predicate.Contains(staged, StringComparison.Ordinal),
                $"IsBusy names {staged}, which is staged state, not in-flight state; Confirm would "
                + "be disabled at the only moment it is rendered");
        }

        // And Confirm really does live inside that conditional fragment, which is what makes the
        // exclusion load-bearing rather than a matter of taste.
        var confirm = Assert.Single(GetButtonTags(GetMemberBody("PendingActionConfirm")),
            tag => tag.Contains("@onclick=\"ConfirmPendingAction\"", StringComparison.Ordinal));
        Assert.Contains("IsBusy", confirm, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExemptButtonsAreExemptOnPurpose()
    {
        // Exemptions are the part of a blanket rule that rots. Each of these is safe for a
        // specific reason; this pins the reason rather than the name, so an exemption cannot be
        // widened by accident and a fifth cannot appear without an edit here.
        var tags = GetButtonTags(ReadPage());

        foreach (var (handler, reason) in ExemptButtons)
        {
            Assert.True(
                tags.Any(tag => tag.Contains($"@onclick=\"{handler}\"", StringComparison.Ordinal)),
                $"exempt button '{handler}' is gone; drop it from the exemption list ({reason})");
        }

        // Cancel is the only exemption that still needs a guard of its own: it must refuse while
        // the action it would cancel is already executing.
        var cancel = Assert.Single(tags,
            tag => tag.Contains("@onclick=\"CancelPendingAction\"", StringComparison.Ordinal));
        Assert.Contains("disabled=\"@(actionInProgress != null)\"", cancel, StringComparison.Ordinal);

        // Close is rendered only after a report has landed.
        var panel = ExtractSpan(GetUserRowMarkup(),
            "@if (reportUser == user.EmailAddress && userReport != null)", "@userReport");
        Assert.Contains("@onclick=\"CloseUserReport\"", panel, StringComparison.Ordinal);

        // Sample CSV serves a constant; it must not grow a call into Exchange.
        Assert.DoesNotContain("MigrationSvc", GetMethodBody("DownloadSampleCsv"), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryInFlightFlagIsClearedInAFinally()
    {
        // A flag cleared only on the success path sticks for the life of the circuit the first
        // time Exchange throws - and under a page-wide gate a stuck flag deadens the entire page,
        // not just the one control it used to grey. SearchUser did exactly this. Anchored per
        // occurrence rather than per method, so a new setter cannot skip the finally.
        var page = StripLineComments(ReadPage());

        foreach (var flag in InFlightFlags)
        {
            var setters = NonClearingSetters(page, flag).ToList();
            Assert.True(setters.Count > 0, $"{flag} is never set; is it still an in-flight flag?");

            foreach (var setter in setters)
            {
                var method = EnclosingMethodName(page, setter.Index);
                var body = StripLineComments(GetMethodBody(method));

                Assert.True(body.Contains("finally", StringComparison.Ordinal),
                    $"{method} sets {flag} but has no finally to clear it in");

                var cleanup = ExtractBlock(body, "finally");
                Assert.True(Regex.IsMatch(cleanup, WholeWord(flag) + @"\s*=\s*(null|false);"),
                    $"{method} sets {flag} but does not clear it in its finally; a throw there "
                    + "leaves the flag stuck and every control on the page dead");
            }
        }
    }

    [Fact]
    public void SingleFlightHandlersSetTheirFlagBeforeTheFirstAwait()
    {
        // A flag set after the authorization round-trip is false for the whole of that round-trip.
        // The circuit stays interactive across it, so the gate never closed and the operator can
        // click the same button again, or any other. Five handlers had this shape. Asserted per
        // handler rather than per setter: a handler may well learn which row to load only after an
        // await, so what matters is that SOME in-flight flag is set before the first one.
        var page = StripLineComments(ReadPage());

        var handlers = InFlightFlags
            .SelectMany(flag => NonClearingSetters(page, flag))
            .Select(setter => EnclosingMethodName(page, setter.Index))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var handler in handlers)
        {
            var body = StripLineComments(GetMethodBody(handler));
            var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
            if (firstAwait < 0)
                continue;

            var closedEarly = InFlightFlags
                .SelectMany(flag => NonClearingSetters(body, flag))
                .Any(setter => setter.Index < firstAwait);

            Assert.True(closedEarly,
                $"{handler} sets no in-flight flag before its first await; the page gate is open "
                + "for the whole of that await and the click can be repeated");
        }
    }

    [Fact]
    public void TheTabStripRefusesClicksWhileThePageIsBusy()
    {
        // An <a> ignores the disabled attribute entirely, so the button sweep cannot reach the tab
        // strip - and the Migration Status tab calls CloseUserReport, so clicking it mid-pull
        // discards a report that takes minutes to fetch. The gate has to be in the handler; the
        // greying is styling only and must never be mistaken for the guard.
        var strip = ExtractSpan(ReadPage(), "<ul class=\"nav nav-tabs mb-3\">", "</ul>");

        var handlers = Regex.Matches(strip, @"@onclick=""(?<handler>[^""]*)""")
            .Select(match => match.Groups["handler"].Value)
            .ToList();

        Assert.Equal(3, handlers.Count);
        Assert.Equal(3, Regex.Matches(strip, @"IsBusy \? ""disabled""").Count);

        foreach (var handler in handlers)
        {
            var call = Regex.Match(handler, @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(");
            var method = call.Success ? call.Groups["name"].Value : handler;

            Assert.True(GetMethodBody(method).Contains("if (IsBusy) return;", StringComparison.Ordinal),
                $"tab handler {method} does not refuse while the page is busy, and the anchor it "
                + "hangs off cannot be disabled in markup");
        }
    }

    /// <summary>
    /// <paramref name="source"/> with line comments removed. The scans below look for words that
    /// also occur in prose - "await", "finally", the flag names themselves - so a comment
    /// explaining one of these rules would otherwise be read as code breaking it.
    /// </summary>
    private static string StripLineComments(string source) =>
        Regex.Replace(source, @"//[^\r\n]*", "");

    /// <summary>A regex matching <paramref name="identifier"/> only as a whole identifier.</summary>
    private static string WholeWord(string identifier) =>
        $@"(?<![A-Za-z0-9_]){Regex.Escape(identifier)}(?![A-Za-z0-9_])";

    /// <summary>
    /// Every assignment of a non-clearing value to <paramref name="flag"/>: the places that put the
    /// page into a busy state, excluding the clears and the field declaration itself.
    /// </summary>
    private static IEnumerable<Match> NonClearingSetters(string source, string flag) =>
        Regex.Matches(source, WholeWord(flag) + @"\s*=(?!=)\s*(?<value>[^\r\n]+)")
            .Where(match => match.Groups["value"].Value.Trim() is not ("null;" or "false;"));

    /// <summary>
    /// Every &lt;button&gt; tag in <paramref name="markup"/>, from "&lt;button" through its closing
    /// "&gt;".
    /// </summary>
    /// <remarks>
    /// A naive "&lt;button.*?&gt;" stops at the first "&gt;" it meets, and several handlers on this
    /// page are lambdas - @onclick="() =&gt; DeleteBatch(...)" - whose arrow ends the match inside
    /// the attribute list, hiding every attribute after it. This walks the tag and ignores any
    /// "&gt;" sitting inside a quoted attribute value.
    /// </remarks>
    private static List<string> GetButtonTags(string markup)
    {
        var tags = new List<string>();

        for (var start = markup.IndexOf("<button", StringComparison.Ordinal); start >= 0;
             start = markup.IndexOf("<button", start + 1, StringComparison.Ordinal))
        {
            var quote = '\0';
            for (var i = start; i < markup.Length; i++)
            {
                var c = markup[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    tags.Add(markup[start..(i + 1)]);
                    break;
                }
            }
        }

        return tags;
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

    /// <summary>The text from <paramref name="opener"/> through the first <paramref name="closer"/>.</summary>
    private static string ExtractSpan(string source, string opener, string closer)
    {
        var start = source.IndexOf(opener, StringComparison.Ordinal);
        Assert.True(start >= 0, $"span '{opener}' not found");

        var end = source.IndexOf(closer, start, StringComparison.Ordinal);
        Assert.True(end > start, $"no '{closer}' after '{opener}'");

        return source[start..(end + closer.Length)];
    }

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

    /// <summary>
    /// The name of the method whose body contains <paramref name="index"/>, found by walking back
    /// to the nearest member signature. Lets an assertion start from an occurrence rather than
    /// from a hard-coded list of method names.
    /// </summary>
    private static string EnclosingMethodName(string source, int index)
    {
        var signatures = Regex.Matches(source[..index],
            @"\n    private\s+(?:async\s+)?[A-Za-z][^\r\n=]*?\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(");

        Assert.True(signatures.Count > 0, $"no enclosing method found for offset {index}");
        return signatures[^1].Groups["name"].Value;
    }

    private static string ReadPage() =>
        File.ReadAllText(Path.Combine(GetPagesDirectory(), "Migration.razor"));

    private static string ReadServiceSource(string fileName)
    {
        var pages = new DirectoryInfo(GetPagesDirectory());
        var repoRoot = pages.Parent?.Parent
            ?? throw new DirectoryNotFoundException("could not walk up from Components/Pages");

        return File.ReadAllText(Path.Combine(repoRoot.FullName, "Services", fileName));
    }

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
