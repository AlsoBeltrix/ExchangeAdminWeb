using System.Text.RegularExpressions;
using ExchangeAdminWeb.Components.Layout;
using ExchangeAdminWeb.Modules;

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
    public void TheOpenBatchIsAddressedByAQueryParameterAndNotAPathSegment()
    {
        // docs/MigrationInterfaceRedesign-Plan.md S1, test obligation 1. A path segment was ruled
        // out on evidence, and the evidence is asserted below rather than restated: this test
        // guards the FORM of the address, the next one guards what that form costs if it changes.
        var page = ReadPage();

        var routes = Regex.Matches(page, @"^@page\s+""(?<route>[^""]+)""", RegexOptions.Multiline)
            .Select(match => match.Groups["route"].Value)
            .ToList();
        Assert.Equal(new[] { "/migration" }, routes);

        Assert.Contains(
            "private const string BatchQueryParameter = \"batch\";",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "[SupplyParameterFromQuery(Name = BatchQueryParameter)]",
            page,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheModuleStillResolvesWithTheBatchQueryParameterPresent()
    {
        // The reason the address is a query parameter. ModuleVersion.razor and UsageTracker.razor
        // both identify the running module by handing Catalog.GetByRoute the base-relative path
        // with the query cut off; a /migration/batch/<name> form would miss the catalog's exact
        // "migration" route and the version badge and usage telemetry would both go silent with no
        // error anywhere. Behavioural, not a source scan: the real derivation, the real catalog.
        var catalog = new ModuleCatalog();

        Assert.NotNull(catalog.GetByRoute(UsageTracker.RouteOf("migration")));
        Assert.NotNull(catalog.GetByRoute(UsageTracker.RouteOf("migration?batch=Wave%2012")));

        // The counterweight. Without it the two assertions above would still pass under the very
        // design they exist to refuse, and would read as coverage of a decision they never test.
        Assert.Null(catalog.GetByRoute(UsageTracker.RouteOf("migration/batch/Wave%2012")));
    }

    [Fact]
    public void TheOpenBatchIsKeyedOnBatchNameNotRowIndex()
    {
        // Same defect as the selection guard above, one surface further out: the batch list
        // re-sorts on every header click and reloads after every action, so an index in the
        // address would resolve to a different batch after a refresh - and the address is the part
        // that gets pasted into a ticket and reopened tomorrow.
        var page = StripLineComments(ReadPage());

        // S3 replaced OpenBatch(name) with membership of selectedBatches - under R9 the open batch
        // IS the selection when the selection has one member - so what to check is what goes INTO
        // the selection. Every write must be a batch name or a captured copy of one; an index or a
        // computed position anywhere here retargets the address on the next reorder.
        var writes = Regex.Matches(page, @"selectedBatches\.Add\((?<value>[^)]*)\);")
            .Select(match => match.Groups["value"].Value.Trim())
            .ToList();
        Assert.True(writes.Count >= 4,
            $"expected every selection write to still be present, found {writes.Count}");

        foreach (var write in writes)
        {
            Assert.True(
                Regex.IsMatch(write, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*!?$"),
                $"selectedBatches.Add({write}) is not a plain batch-name expression; an index or "
                + "a computed position here retargets the address on the next reorder");
        }

        // And the selection is still a name-keyed set, not a set of positions.
        Assert.Contains(
            "private readonly HashSet<string> selectedBatches = new(StringComparer.OrdinalIgnoreCase);",
            ReadPage(),
            StringComparison.Ordinal);

        var row = GetBatchRowMarkup();
        // The captured per-iteration copy, not batch.BatchName read inside a lambda: the whole row
        // is the click target and the lambda must close over THIS row name.
        Assert.Contains("SelectOnlyBatch(batchName)", row, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAddressAndTheRenderedPageAreWrittenTogether()
    {
        // The page renders against expandedBatch and the operator's browser remembers the URL. If
        // any other code writes the field, those two disagree and the URL is the one that wins a
        // reconnect - the operator comes back to a different batch than the one they left open,
        // with no error. Two writers, both of which move the pair at once.
        var page = StripLineComments(ReadPage());

        var writers = Regex.Matches(page, @"(?<![A-Za-z0-9_])expandedBatch\s*=(?!=)")
            .Select(match => EnclosingMethodName(page, match.Index))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // S3 renamed the outbound writer. OpenBatch set the open batch directly; under R9 there
        // is no such act - AdoptSelectionAsOpenBatch DERIVES it from the selection, which is why
        // ticking, clicking a row, searching and Back all end in the same place and cannot
        // disagree. Still exactly two writers, which is the property.
        Assert.Equal(new[] { "AdoptSelectionAsOpenBatch", "SyncOpenBatchFromUrl" }, writers);

        var open = GetMethodBody("AdoptSelectionAsOpenBatch");
        Assert.Contains("GetUriWithQueryParameter(BatchQueryParameter, open)", open, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo(uri)", open, StringComparison.Ordinal);

        // The derivation itself: one ticked batch is the open batch, any other count is none (R2).
        Assert.Contains("selectedBatches.Count == 1", open, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUrlSyncRefusesToFetchBeforeTheCircuitExists()
    {
        // The page prerenders, so its whole lifecycle runs once on the server with no circuit and
        // again on a fresh instance when it goes interactive. Get-MigrationBatchUser is a real
        // Exchange call; unguarded, a pasted ?batch= link would make it twice and throw the first
        // result away. The authorization latch is here for a sharper reason: the access-denied
        // bounce is a full-page load, so this component finishes its lifecycle on the way out and
        // would otherwise read migration data for someone who was just refused the page.
        var body = GetMethodBody("SyncOpenBatchFromUrl");

        var guard = body.IndexOf("if (!RendererInfo.IsInteractive || accessDenied)", StringComparison.Ordinal);
        var fetch = body.IndexOf("GetMigrationBatchUsersAsync", StringComparison.Ordinal);
        Assert.True(guard >= 0, "SyncOpenBatchFromUrl must refuse a non-interactive or denied pass");
        Assert.True(fetch > guard, "the guard must precede the Exchange call");

        Assert.Contains(
            "accessDenied = true;",
            ExtractBlock(ReadPage(), "protected override async Task OnInitializedAsync()"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionIsPrunedWhenTheTableReloads()
    {
        // A batch removed by another operator, or by Exchange, must not stay ticked.
        //
        // Anchored on LoadBatchList, not on LoadMigrationStatus. S1 split the catalogue reload out
        // of the collapse-and-reload handler so a pasted ?batch= link can load the list it needs
        // without closing the batch it came for, which gave the page a SECOND path to a reloaded
        // table. LoadBatchList is the one both go through, so it is the one that has to prune;
        // leaving this on the wrapper would pass while the deep-link path pruned nothing.
        var body = GetMethodBody("LoadBatchList");

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
    public void TheToolbarOffersEveryBatchActionWithDistinctNames()
    {
        // D3, plus the naming half of the owner's complaint: "Clear Completed" sat beside "Clear
        // selection" sharing a word, one irreversible and one harmless.
        //
        // FIVE actions now, not three. S3 moved Complete and Stop here from the per-row buttons S2
        // removed, which is the debt S2 recorded. Comments are stripped first because this block's
        // own prose explains the "Clear selection" adjacency and would otherwise trip the check
        // that the phrase is absent - a guard failing on the comment that documents it.
        var toolbar = StripLineComments(
            ExtractBlock(StripRazorComments(ReadPage()), "@if (canManage && selectedBatches.Count > 0)"));

        Assert.Contains("StageDeleteSelected", toolbar, StringComparison.Ordinal);
        Assert.Contains("StageRemoveCompletedSelected", toolbar, StringComparison.Ordinal);
        Assert.Contains("StageResumeSelected", toolbar, StringComparison.Ordinal);
        Assert.Contains("StageCompleteSelected", toolbar, StringComparison.Ordinal);
        Assert.Contains("StageStopSelected", toolbar, StringComparison.Ordinal);

        Assert.Contains("Untick all", toolbar, StringComparison.Ordinal);

        // "Clear selection" belongs to the right pane, which has no tick boxes to untick (R10).
        // The two must never appear in the same bar, which is the adjacency that was rejected.
        Assert.DoesNotContain("Clear selection", toolbar, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteSaysWhatToDoAboutABatchWithErrors()
    {
        // Owner, 2026-09-25. A CompletedWithErrors batch is skipped by Complete and always will be
        // - the errors have to be remediated or removed before there is anything to finalise - but
        // the operator was left to infer that from a skip line. The control says it.
        //
        // On the control, not as a banner or a caption: one Complete button exists, so the text
        // is not repeated per row, and the per-batch answer already lives in the confirm step,
        // which names every skipped batch with the status that skipped it.
        var toolbar = ExtractBlock(
            StripRazorComments(ReadPage()), "@if (canManage && selectedBatches.Count > 0)");

        var complete = GetButtonTags(toolbar)
            .Single(tag => tag.Contains("StageCompleteSelected", StringComparison.Ordinal));

        var title = Regex.Match(complete, @"title=""(?<text>[^""]*)""").Groups["text"].Value;

        Assert.Contains("errors", title, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"fixed or removed|remediated or removed", title);

        // Short enough to be a tooltip. The owner's standing rule is that a control carries what
        // differs and nothing more; a paragraph on a button is context leaking into the product.
        Assert.True(title.Length <= 160,
            $"the Complete tooltip is {title.Length} characters; it is a hint, not a help page");
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
        // Was scoped to the batch row. S2 removed the row's action buttons (see
        // NoBatchActionControlDecidesEligibilityOutsideThePlanner for why and for what that cost),
        // so the scope widens to the whole page: the named status must not reappear as a literal
        // anywhere, and the planner stays the single definition in the C# that feeds every
        // batch-scoped decision.
        var page = ReadPage();

        Assert.DoesNotContain("\"Corrupted\"", page, StringComparison.Ordinal);

        // The planner is still the only thing that answers "may this action run on this status".
        Assert.Contains("MigrationBatchActionPlanner.Plan(", page, StringComparison.Ordinal);
        Assert.Contains("MigrationBatchActionPlanner.PruneSelection", page, StringComparison.Ordinal);
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
        // Owner ruling D2(a): a skip is never a failure, and a wholly ineligible selection is
        // "nothing to do".
        //
        // Skips used to travel in the banner text via the planner's DescribeSkipped. They now
        // land on the ROWS (R25), after the owner's 2026-09-28 ruling that a banner is for a
        // glance and not an error log. What must hold either way is that every skipped row is
        // REPORTED somewhere, and that none of them is counted as an error.
        var body = GetMethodBody("ExecuteBulkBatchAction");

        Assert.Contains("actionOutcomes[earlySkip.BatchName]", body, StringComparison.Ordinal);
        Assert.Contains("actionOutcomes[skip.BatchName]", body, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"errors\.Add\([^)]*[Ss]kip"), body);

        // Nor marked as one on the row, or D2(a) would hold in the counts and fail on the screen.
        Assert.DoesNotMatch(new Regex(@"actionFailures\.Add\([^)]*[Ss]kip"), body);
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
    public void EveryBatchActionInTheToolbarRoutesThroughThePlanner()
    {
        // The debt S2 recorded, paid. When the per-row Delete and Resume buttons went, the
        // assertion that a batch action reads MigrationBatchActionPlanner lost its subject; S3's
        // toolbar gives it one back, and this is the positive half restored.
        //
        // Five actions now. Each must reach the planner through StageSelectionAction rather than
        // carrying its own status rule - two copies of "which statuses may be stopped" is how a
        // toolbar button and the executor come to disagree about the same batch.
        var page = ReadPage();

        string[] stagers =
        [
            "StageDeleteSelected", "StageRemoveCompletedSelected", "StageResumeSelected",
            "StageCompleteSelected", "StageStopSelected",
        ];

        foreach (var stager in stagers)
        {
            var body = GetMemberSource(
                $@"private\s+void\s+{Regex.Escape(stager)}\s*\(", stager);

            Assert.Contains("StageSelectionAction(", body, StringComparison.Ordinal);
            Assert.Contains("MigrationBatchAction.", body, StringComparison.Ordinal);
        }

        // And the one place they all land still asks the planner, twice: once to describe the
        // staged target and once more inside the callback, because the operator can sit at the
        // ticket field while the table reloads underneath them.
        var stage = GetMethodBody("StageSelectionAction");
        Assert.Equal(2, Regex.Matches(stage, @"MigrationBatchActionPlanner\.Plan\(").Count);
    }

    [Fact]
    public void ConfirmCarriesTheEligibleCountAndNamesWhatWillBeSkipped()
    {
        // S4, R12, and the plan's test obligation 4. Known Failure Class 2 is success aggregation:
        // a loop over N items reporting blanket success. This answers it BEFORE the fact - the
        // operator agreeing to an action sees how many rows it will actually touch, on the button
        // they press, and which rows it will not.
        //
        // The count must be of the ELIGIBLE rows, not the ticked ones. Ticking twelve, agreeing to
        // "12 batches" and having three run is precisely the surprise this removes.
        var confirm = ExtractSpan(ReadPage(), "private RenderFragment PendingActionConfirm", "</div>;");

        Assert.Contains("pendingActionPlan.Eligible.Count", confirm, StringComparison.Ordinal);
        Assert.Contains("MigrationBatchActionPlanner.DescribeSkipped", confirm, StringComparison.Ordinal);

        // DescribeSkipped names each skipped batch and its status; a bare count cannot be acted on.
        var describe = ReadServiceSource("MigrationBatchActionPlanner.cs");
        Assert.Contains("s.BatchName} ({s.Status}", describe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStagedPreviewIsNeverTheAuthorityOnWhatRuns()
    {
        // The trap this slice could have walked into. The operator can sit at the ticket field
        // indefinitely while the table reloads underneath them, so a plan captured at staging time
        // is stale by the time Confirm is pressed - the same staleness class as pps-1(b), where an
        // on-prem write used a protection verdict computed before a confirmation dialog.
        //
        // pendingActionPlan therefore feeds the PREVIEW only. The callback re-plans.
        var stage = GetMethodBody("StageSelectionAction");

        Assert.Contains("pendingActionPlan = staged;", stage, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(stage, @"MigrationBatchActionPlanner\.Plan\(").Count);

        // And the preview is torn down when the action starts, not left over rows it is changing.
        Assert.Contains("ClearStagedPreview();", GetMethodBody("CancelPendingAction"), StringComparison.Ordinal);
        Assert.Contains("CancelPendingAction();", GetMethodBody("ConfirmPendingAction"), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAffectedRowCarriesItsOwnOutcomeBeforeTheTicketIsTyped()
    {
        // R12: a per-row outcome written against every affected row. Written, not implied by a
        // colour - R17 refuses an unlabelled value, and a skip the operator cannot explain is one
        // they cannot act on, so the status that caused it travels with the word.
        var row = GetBatchRowMarkup();

        Assert.Contains("StagedOutcomeFor(batchName)", row, StringComparison.Ordinal);
        Assert.Contains("mig-outcome", row, StringComparison.Ordinal);

        var outcome = GetMethodBody("StagedOutcomeFor");
        Assert.Contains("Will run", outcome, StringComparison.Ordinal);
        Assert.Contains("Skipped ({skip.Status})", outcome, StringComparison.Ordinal);

        // A row that is not part of the staged plan says nothing. Annotating an unticked row would
        // claim an outcome for an action that will never look at it.
        Assert.Contains("return null;", outcome, StringComparison.Ordinal);

        // R12: the ticket field is visibly tied to ONE named action rather than floating beneath
        // a row of them. This used to be asserted as a highlighted button - BatchActionButtonClass
        // switched the picked one from outline to solid - and that is gone with the button row
        // itself (owner, 2026-09-29: one line, and six labelled buttons do not fit on one line).
        // The guarantee did not go with it; it moved. The confirm bar states the staged action in
        // bold, which is strictly more legible than a tint and survives a menu that has already
        // closed.
        var confirm = GetMemberSource(
            @"private\s+RenderFragment\s+PendingActionConfirm", "PendingActionConfirm");
        Assert.Contains("<strong>@pendingActionLabel</strong>", confirm, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOpenBatchIsDerivedFromTheSelectionAndNeverSetBesideIt()
    {
        // R9, and the plan's test obligation 3. There is ONE concept: ticked, highlighted, shown
        // in the right pane and acted on are always the same set. The failure this prevents is the
        // one the owner named - "tick vs select vs left vs right is confusing and annoying" - and
        // it returns the moment any handler sets the open batch without going through the
        // derivation, because then the pane can show one batch while the tick boxes show another.
        var page = StripLineComments(ReadPage());

        // Every handler that changes the selection must end at the derivation.
        string[] selectionHandlers =
        [
            "SelectOnlyBatch", "ToggleBatchSelected", "ToggleSelectAllBatches",
            "ClearBatchSelection", "RemoveFromSelection",
        ];

        foreach (var handler in selectionHandlers)
        {
            var body = StripLineComments(GetMethodBody(handler));

            Assert.Contains("selectedBatches", body, StringComparison.Ordinal);
            Assert.Contains("AdoptSelectionAsOpenBatch()", body, StringComparison.Ordinal);
        }

        // And the inbound direction: an address arriving from Back or a pasted link sets the
        // SELECTION, not a second value beside it. Without this, Back moves the pane while the
        // tick boxes stay where they were.
        var sync = StripLineComments(GetMethodBody("SyncOpenBatchFromUrl"));
        Assert.Contains("selectedBatches.Clear();", sync, StringComparison.Ordinal);
        Assert.Contains("selectedBatches.Add(requested);", sync, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMailboxTableFiltersAndSortsTheWholeSetBeforeItPages()
    {
        // R21, and the ordering is the whole assertion. Filtering the rendered PAGE would mean a
        // mailbox the operator is searching for is absent because it happens to sit on page 7 -
        // which makes a filter worse than no filter, because absence then looks like proof.
        //
        // Slice() takes FilteredSortedMailboxes(), so the narrowing happens first by construction.
        var page = ReadPage();

        // OtherMailboxes() is FilteredSortedMailboxes() minus the pinned rows, so the narrowing
        // still happens before the windowing - which is the property. S5 step 2b inserted the
        // pinned split between the two.
        Assert.Contains("ListWindow.Slice(OtherMailboxes(), mailboxPage, MailboxPageSize)",
            page, StringComparison.Ordinal);
        Assert.Contains("FilteredSortedMailboxes().Where(u => !selectedMailboxes.Contains(u.EmailAddress))",
            page, StringComparison.Ordinal);
        Assert.Contains("ListWindow.Label(mailboxPage, MailboxTotalCount, MailboxPageSize, \"mailboxes\"",
            page, StringComparison.Ordinal);

        // The count the pager reports is the count AFTER filtering: paging through 340 matches of
        // 2000 is what the operator is doing, so 2000 would be the wrong number to show.
        Assert.Contains("private int MailboxTotalCount => OtherMailboxes().Count();",
            page, StringComparison.Ordinal);

        var filtering = StripLineComments(GetMethodBody("FilteredSortedMailboxes"));
        Assert.Contains("rows.Where(", filtering, StringComparison.Ordinal);

        // Sorting was split into SortMailboxes so the PINNED block can sort without filtering
        // (R11). One ordering definition, or the pinned rows and the rows below them would sort by
        // different rules and read as two unrelated lists.
        Assert.Contains("SortMailboxes(rows)", filtering, StringComparison.Ordinal);
        Assert.Contains("OrderBy", StripLineComments(GetMethodBody("SortMailboxes")), StringComparison.Ordinal);
        Assert.Contains("SortMailboxes(", StripLineComments(GetMethodBody("PinnedMailboxes")), StringComparison.Ordinal);

        // The loop renders the page, never the set.
        Assert.Contains("@foreach (var user in GetPagedMailboxes())", page, StringComparison.Ordinal);
        Assert.DoesNotContain("@foreach (var user in batchUsers)", page, StringComparison.Ordinal);

        // S5 step 2b: the pinned block pages separately, and BOTH loops render a window.
        Assert.Contains("foreach (var user in GetPinnedMailboxesPage())", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ATickedBatchIsNeverHiddenByTheBatchNameFilter()
    {
        // R11, batch side - the half that was missing while its mailbox twin below was carefully
        // asserted. R5 forbids pinning selected batches into the left list precisely BECAUSE the
        // right pane shows them, so that pane is the only surface a ticked batch appears on.
        //
        // It read GetSortedBatches(), which applies the batch-name filter. Tick four batches,
        // type in the filter, and the header still said "4 batches selected" above however many
        // survived the filter - with the rest still armed for whatever the Actions menu ran next.
        // A selection you cannot see is what R11 exists to prevent, and the mailbox test below
        // even states "batches have a whole pane showing the selection" as its premise.
        //
        // Found by sweeping for the same shape after the pinned-mailbox defect turned up on dev:
        // a count or a list that silently drops selected rows.
        var selection = StripLineComments(GetMethodBody("SelectedBatchesInListOrder"));

        Assert.Contains("selectedBatches.Contains", selection, StringComparison.Ordinal);
        Assert.Contains("SortBatches(", selection, StringComparison.Ordinal);

        // The whole fix: order without filtering. GetSortedBatches is the filtered one.
        Assert.DoesNotContain("GetSortedBatches", selection, StringComparison.Ordinal);

        // And the ordering is still ONE definition, or the pane and the list beside it drift into
        // two different sort orders.
        var sorted = StripLineComments(GetMethodBody("GetSortedBatches"));
        Assert.Contains("SortBatches(rows)", sorted, StringComparison.Ordinal);
    }

    [Fact]
    public void ATickedMailboxIsNeverHiddenByTheFilterOrByAPageChange()
    {
        // R11, and R5a is how it is met for mailboxes. Batches have a whole pane showing the
        // selection; mailboxes do not, so ticked ones are pinned above an "OTHER MAILBOXES"
        // divider instead.
        //
        // The load-bearing detail: the pinned block is SORTED but NOT filtered. A ticked mailbox
        // that disappears when the operator types would leave them acting on a selection they
        // cannot see, which is precisely what R11 forbids.
        var page = ReadPage();

        var pinned = StripLineComments(GetMethodBody("PinnedMailboxes"));
        Assert.Contains("selectedMailboxes.Contains", pinned, StringComparison.Ordinal);
        Assert.Contains("SortMailboxes(", pinned, StringComparison.Ordinal);
        Assert.DoesNotContain("FilteredSortedMailboxes", pinned, StringComparison.Ordinal);

        Assert.Contains("OTHER MAILBOXES", page, StringComparison.Ordinal);

        // R5b. The pinned block pages, or ticking 2000 mailboxes pins 2000 rows and R5a, R8 and
        // R20 cannot all hold at once - which is the Defender failure wearing a different hat.
        Assert.Contains("ListWindow.Slice(PinnedMailboxes(), pinnedMailboxPage, PinnedMailboxPageSize)",
            page, StringComparison.Ordinal);
        Assert.Contains("\"ticked\"", page, StringComparison.Ordinal);

        // And the two lists are disjoint, or a ticked mailbox renders twice.
        Assert.Contains("!selectedMailboxes.Contains(u.EmailAddress)", page, StringComparison.Ordinal);
    }

    [Fact]
    public void BothActionBarsAreTheSameControl()
    {
        // R12: both action bars are identical in look and behaviour. Nothing checked the LOOK
        // half, and that is exactly how they came apart: the batch bar became a single Actions
        // menu on 2026-09-29 and the mailbox bar stayed eight buttons in five colours, which is
        // the layout the owner had just rejected on the other pane.
        //
        // Asserted as a shape, not a pixel: one Actions (n) toggle per pane, one closing sheet
        // per pane, one menu per pane, and exactly one red item in each.
        var page = StripRazorComments(ReadPage());

        Assert.Equal(2, CountOf(page, "dropdown-toggle"));
        Assert.Equal(2, CountOf(page, "Actions (@selectedBatches.Count)")
                        + CountOf(page, "Actions (@selectedMailboxes.Count)"));
        Assert.Equal(2, CountOf(page, "mig-menu-sheet"));
        Assert.Equal(2, CountOf(page, "mig-actions-menu"));

        // One coloured item per menu. A bar where several controls are tinted teaches the
        // operator to ignore the tint, which is the complaint that started this.
        Assert.Equal(2, CountOf(page, "dropdown-item text-danger"));

        // And neither bar keeps a row of tinted buttons any more.
        Assert.DoesNotContain("BatchActionButtonClass", page, StringComparison.Ordinal);
        Assert.DoesNotContain("MailboxActionButtonClass", page, StringComparison.Ordinal);
    }

    private static int CountOf(string haystack, string needle)
    {
        var n = 0;
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            n++;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
        }

        return n;
    }

    [Fact]
    public void BothActionBarsReachAPlannerAndNeitherLoopsTheSelectionItself()
    {
        // R12: both bars identical in BEHAVIOUR, not just in look. A mailbox bar that looped the
        // selection directly would have no eligible/skipped split, no per-row preview and no named
        // skips, and would resemble the batch bar only from a distance.
        var page = ReadPage();

        foreach (var stager in new[]
        {
            "StageCompleteMailboxes", "StageApproveMailboxes", "StagePauseMailboxes",
            "StageResumeMailboxes", "StageRemoveMailboxes",
        })
        {
            Assert.Contains("StageMailboxAction(",
                GetMemberSource($@"private\s+void\s+{Regex.Escape(stager)}\s*\(", stager),
                StringComparison.Ordinal);
        }

        // Planned twice, for the same staleness reason the batch bar plans twice: the operator can
        // sit at the ticket field while the rows reload underneath them.
        var stage = GetMethodBody("StageMailboxAction");
        Assert.Equal(2, Regex.Matches(stage, @"MigrationUserActionPlanner\.Plan\(").Count);

        // The executor acts on the plan's ELIGIBLE list only - nothing is sent for a skipped row.
        var exec = GetMethodBody("ExecuteBulkMailboxAction");
        Assert.Contains("foreach (var email in plan.Eligible)", exec, StringComparison.Ordinal);
        Assert.Contains("actionOutcomes[skip.EmailAddress]", exec, StringComparison.Ordinal);

        // Per-item outcomes, never a blanket banner (Known Failure Class 2).
        Assert.Contains("errors.Add(", exec, StringComparison.Ordinal);
        Assert.Contains("auditWarnings.Add(", exec, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingTheFilterOrTheSortSendsTheMailboxListBackToPageOne()
    {
        // The failure: the operator is on page 7, types a filter that leaves two pages, and the
        // table renders nothing. An empty page and an empty batch look identical, so it reads as
        // "this batch has no mailboxes" - which is why ListWindow.ClampPage exists and why every
        // control that changes the membership or the order has to use it.
        foreach (var handler in new[] { "OnMailboxFilterChanged", "OnMailboxSortColumnChanged", "ToggleMailboxSortDirection" })
        {
            Assert.Contains("ResetMailboxPage();", GetMethodBody(handler), StringComparison.Ordinal);
        }

        // Opening a different batch is a different list again: carrying the filter would show "no
        // mailbox matches" for a batch just opened, and carrying the page would land on row 350 of
        // a 40-row batch.
        var adopt = GetMethodBody("AdoptSelectionAsOpenBatch");
        Assert.Contains("mailboxFilter = \"\";", adopt, StringComparison.Ordinal);
        Assert.Contains("ResetMailboxPage();", adopt, StringComparison.Ordinal);

        // A refresh of the SAME batch clamps instead of resetting, so the operator is not thrown
        // back to page one every time the rows reload.
        Assert.Contains("mailboxPage = ListWindow.ClampPage(mailboxPage, MailboxTotalCount, MailboxPageSize);",
            GetMethodBody("ReplaceBatchUsers"), StringComparison.Ordinal);
    }

    [Fact]
    public void ExportReportsIsReadOnlyAndTakesNoTicket()
    {
        // R31d. The one control in the mailbox bar that changes nothing, sitting beside four that
        // do. It must not stage a ticket, and it must not run at the mutating permission -
        // requiring MigrationManage to READ a report would be a quiet privilege escalation of the
        // page's read surface.
        var body = StripLineComments(GetMethodBody("StartReportExport"));

        Assert.DoesNotContain("StageBatchAction", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StageMailboxAction", body, StringComparison.Ordinal);
        Assert.Contains("Ticket = null", body, StringComparison.Ordinal);

        // R31a: it goes to the background runner, not the circuit. Twenty minutes a report means
        // fifty ticked mailboxes is potentially a whole day.
        Assert.Contains("BulkJobs.Enqueue(job)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("await MigrationSvc", body, StringComparison.Ordinal);

        // And the operator is told the cost before it starts.
        Assert.Contains("twenty minutes", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDownloadBuildsTheZipFromTheStoreAndIsAuditedSeparately()
    {
        // R31e and R31f. The zip is assembled at CLICK time from the same store the dialog reads,
        // because the runner persists row outcomes and not files - so the file and the text on
        // screen cannot be different documents.
        var body = StripLineComments(GetMethodBody("DownloadReportExportAsync"));

        Assert.Contains("ReportStore.BuildExportZip(", body, StringComparison.Ordinal);

        // Nothing to package says so rather than delivering an empty archive that looks like a
        // successful export of nothing.
        Assert.Contains("No report is held", body, StringComparison.Ordinal);

        // Audited separately from the fetches that produced the reports: this is a second,
        // distinct read - diagnostics leaving for a workstation.
        Assert.Contains("DownloadUserReports", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBannerCountsAndTheRowsCarryTheDetail()
    {
        // R25, and the owner's 2026-09-28 ruling: "banners are for quick, 10ft info, not for
        // error logs."
        //
        // Both executors used to join every refusal reason and every skipped row into ONE alert
        // string. Over fifty batches that is an error log wedged into an alert box - unreadable
        // at the moment the operator most needs to read it, and it scrolls the page.
        //
        // The banner now states counts. Which row, and why, is written on the row.
        foreach (var executor in new[] { "ExecuteBulkBatchAction", "ExecuteBulkMailboxAction" })
        {
            var body = GetMethodBody(executor);

            // No joined error list and no joined skip list in the operator-facing message.
            Assert.DoesNotMatch(
                new Regex(@"Message\s*=[^;]*string\.Join\(""; "", errors\)"), body);
            Assert.DoesNotContain("skippedSuffix", body, StringComparison.Ordinal);

            // Audit warnings are logged, not appended to a sentence the operator is reading.
            Assert.DoesNotContain("warningSuffix", body, StringComparison.Ordinal);
            Assert.Contains("Logger.LogWarning(\"Audit warning during", body, StringComparison.Ordinal);

            // The per-row record exists and is what carries the reason.
            Assert.Contains("actionOutcomes[", body, StringComparison.Ordinal);
            Assert.Contains("actionFailures.Add(", body, StringComparison.Ordinal);
        }

        // Rendered on both row kinds, next to the preview chip so the prediction and the result
        // appear in the same place.
        Assert.Contains("OutcomeFor(batchName)", GetBatchRowMarkup(), StringComparison.Ordinal);
        Assert.Contains("OutcomeFor(user.EmailAddress)", GetUserRowMarkup(), StringComparison.Ordinal);

        // And last run's outcomes are cleared when a new action is staged, or an operator would
        // read "Refused" from the previous attempt as the result of this one.
        Assert.Contains("ClearActionOutcomes();", GetMethodBody("StageBatchAction"), StringComparison.Ordinal);
    }

    [Fact]
    public void FindingAPersonAndFilteringBatchNamesAreTwoDifferentControls()
    {
        // R26. Conflating them was the defect. The page had ONE box that searched for a mailbox
        // across every batch and jumped to it; it never narrowed the list, so an operator looking
        // for "all the Finance waves" had no way to ask.
        //
        // Both must exist and they must not be the same control: one takes you somewhere, the
        // other changes what is listed.
        var page = ReadPage();

        Assert.Contains("placeholder=\"Search batch or user email...\"", page, StringComparison.Ordinal);
        Assert.Contains("placeholder=\"Filter batch names\"", page, StringComparison.Ordinal);

        // The finder navigates; it must not narrow.
        var search = StripLineComments(GetMethodBody("SearchUser"));
        Assert.Contains("AdoptSelectionAsOpenBatch()", search, StringComparison.Ordinal);
        Assert.DoesNotContain("batchFilter", search, StringComparison.Ordinal);

        // The filter narrows over the WHOLE catalogue (R21), never the rendered page, and resets
        // to page one so the operator is not left staring at an empty page 9.
        Assert.Contains("b.BatchName?.Contains(filter", GetMethodBody("GetSortedBatches"), StringComparison.Ordinal);
        Assert.Contains("ResetBatchPage();", GetMethodBody("OnBatchFilterChanged"), StringComparison.Ordinal);
    }

    [Fact]
    public void SelectAllTakesTheFilteredSetAndNotTheHiddenRest()
    {
        // R8 says select-all means all, with no cap. Once a filter exists, "all" has to mean all
        // MATCHING - a tick box above a filtered list that silently swept up the hidden 760 would
        // act on rows the operator cannot see, which is the worst reading of select-all.
        var body = StripLineComments(GetMethodBody("ToggleSelectAllBatches"));

        Assert.Contains("foreach (var batch in GetSortedBatches())", body, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var batch in migrationBatches)", body, StringComparison.Ordinal);

        // And the tick box's own checked state agrees with what it would do.
        Assert.Contains("GetSortedBatches()", GetMemberSource(
            @"private\s+bool\s+AllBatchesSelected", "AllBatchesSelected"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheExportSaysWhatItExports()
    {
        // R29. "Download CSV" became ambiguous the moment a selection and a filter existed: all
        // batches, the ticked ones, or the open batch's mailboxes? The label and the tooltip both
        // name the scope, and the count makes the claim checkable against the list beside it.
        var page = StripRazorComments(ReadPage());

        var button = GetButtonTags(page)
            .Single(tag => tag.Contains("DownloadCsvAsync", StringComparison.Ordinal));

        var title = Regex.Match(button, @"title=""(?<text>[^""]*)""").Groups["text"].Value;
        Assert.Contains("filtered and sorted", title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not the selection", title, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Export batch list (@BatchTotalCount)", page, StringComparison.Ordinal);

        // And the claim is true: the export writes the same filtered, sorted set the pane lists.
        Assert.Contains("GetSortedBatches().ToList()", GetMethodBody("DownloadCsvAsync"), StringComparison.Ordinal);
    }

    [Fact]
    public void TickingEveryMailboxDoesNotEmptyThePane()
    {
        // Found on dev, 2026-09-29, on a one-mailbox batch whose mailbox was ticked: the pane
        // rendered "No mailbox in this batch matches ." - with a blank filter name - and drew no
        // rows at all, while Get-CMigrationUser confirmed the mailbox was there and the header
        // beside it said Total: 1.
        //
        // The cause is that the pinned rows (R5a) render INSIDE the table, and the empty state
        // that short-circuits that table asked MailboxTotalCount, which counts the UNPINNED rows
        // only. Tick everything - select-all does it in one click, and a one-mailbox batch does
        // it by accident - and the count is zero while every row sits in the pinned block. So the
        // pane hid exactly the rows the operator had selected, which is the R11 failure that R5a
        // pinning was introduced to prevent, and it hid them at the moment they mattered most.
        //
        // A source tripwire, not a render: no test in this repo can render a Blazor component,
        // which is the whole reason this defect reached a browser to be found.
        var page = StripRazorComments(ReadPage());

        Assert.DoesNotContain("else if (MailboxTotalCount == 0)", page, StringComparison.Ordinal);
        Assert.Contains("else if (!AnyMailboxRowRenders)", page, StringComparison.Ordinal);

        var predicate = GetMemberSource(
            @"private\s+bool\s+AnyMailboxRowRenders", "AnyMailboxRowRenders");
        Assert.Contains("PinnedMailboxTotalCount > 0", predicate, StringComparison.Ordinal);
        Assert.Contains("MailboxTotalCount > 0", predicate, StringComparison.Ordinal);

        // The filtered wording is only honest when a filter is set. Naming an empty filter is how
        // the defect announced itself.
        Assert.Contains("@if (MailboxFilterIsSet)", page, StringComparison.Ordinal);

        // And the pinned block is counted by the rows it renders, not by the selection set: a
        // selection holding a mailbox from another batch drew a divider over nothing and a pager
        // promising pages that could not render.
        var pinned = GetMemberSource(
            @"private\s+int\s+PinnedMailboxTotalCount", "PinnedMailboxTotalCount");
        Assert.Contains("PinnedMailboxes().Count()", pinned, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryColumnTheSortOffersIsAColumnYouCanSee()
    {
        // Owner, 2026-09-29, on a screenshot of the batch list: "where is the total?"
        //
        // It was nowhere. R16 named Batch / Synced / Failed / Status and the table was built to
        // that list - while the sort dropdown offered "Total" and SortBatches ordered by
        // TotalCount. The list could be sorted by a column that was not on screen, and Synced
        // and Failed were fractions with no denominator: "0 synced" reads the same for a batch
        // of one and a batch of five hundred.
        //
        // Asserted as the general rule rather than as "Total exists", because the specific
        // omission was never the interesting part - a sort option with no matching column is.
        var page = StripRazorComments(ReadPage());
        var sorted = StripLineComments(GetMethodBody("SortBatches"));

        // DERIVED from the dropdown, never listed here. The first version of this test hardcoded
        // five of the seven options and passed while two of them still had no column - which is
        // the same mistake it was written to catch, one level up: a check seeded from memory
        // rather than from the thing it checks. If a sort option is added later, this test sees
        // it without anyone remembering to come back.
        var sortable = Regex.Matches(
                page[page.IndexOf("id=\"batchSortColumn\"", StringComparison.Ordinal)..],
                @"<option value=""(?<v>[A-Za-z]+)""")
            .Select(m => m.Groups["v"].Value)
            .TakeWhile(v => v != "Email")
            .ToArray();

        Assert.True(sortable.Length >= 7,
            $"expected to find every batch sort option, found {sortable.Length}");

        // Two are sortable without a column of their own, on purpose. Written down with the
        // reason, in the spirit of ClickGateRegistry exemptions: the gap is fine, the silence is
        // not. Any OTHER option must be a visible column.
        var exempt = new Dictionary<string, string>
        {
            ["Name"] = "the batch name IS the first column; it carries no separate count header",
            ["Direction"] = "shown on every row as an icon inside the name cell, not as a "
                + "column (owner, 2026-09-29). A seventh column would squeeze the name column "
                + "and R15 forbids clipping the trailing identifier in a batch name, so the "
                + "direction rides in the cell it describes. See TheDirectionIconIsLabelled.",
            ["Created"] = "shown per batch in the right-hand pane header (Created: ...), and the "
                + "sort control itself names the ordering on screen. Same width argument as "
                + "Direction: the fixed columns already take most of a narrow pane. UNLIKE those "
                + "two, Total earned a column because it is a DENOMINATOR - Synced and Failed "
                + "mean nothing without it, and it is compared row to row while scanning",
        };
        foreach (var column in sortable)
        {
            // Every option must actually sort, or it is a control that does nothing.
            Assert.True(
                sorted.Contains($"\"{column}\" =>", StringComparison.Ordinal)
                || column == "Created",
                $"the batch sort offers {column} but SortBatches does not order by it");
            // Created is the switch default rather than a named case, which is why it is the one
            // option allowed to be absent from the case list above.

            if (exempt.ContainsKey(column))
                continue;

            // Substring, not an exact cell, because Synced and Total share one header - they are
            // a fraction, and three fixed columns holding single digits were eating the width
            // the batch name needed (owner, 2026-09-29: "batch names are cutoff ... not enough
            // space where it is actually useful"). What matters is that the word appears in a
            // heading, not that it owns a cell of its own.
            var headers = Regex.Matches(page, @"<span(?: [^>]*)?>([A-Za-z/ ]+)</span>")
                .Select(m => m.Groups[1].Value)
                .ToArray();

            Assert.True(
                headers.Any(h => h.Contains(column, StringComparison.OrdinalIgnoreCase)),
                $"the batch sort offers {column} but no column header names it, so the list can "
                + "be ordered by something the operator cannot see - which is exactly what "
                + "'where is the total?' turned out to mean");
        }

        // Both batch views show it - the selection pane lists the same batches, and a total that
        // appears in one and not the other is the same omission wearing a different hat.
        Assert.Equal(2, CountOf(page, "@batch.SyncedCount</span><span class=\"text-muted\">/@batch.TotalCount"));
    }

    [Fact]
    public void EveryColumnTheMailboxSortOffersIsAColumnYouCanSee()
    {
        // The same guard as the batch list, on the other list. It passes today - the mailbox
        // table renders Email, Status, Synced and Last Sync, which is exactly what its sort
        // offers - so this is preventive, and deliberately so.
        //
        // The batch list drifted from its own sort because nothing checked it, and the R12
        // action bars came apart because one was guarded and the other was not. A guard on one
        // of two symmetric surfaces is how the unguarded one is discovered later, by an owner,
        // on a screenshot. Both lists now carry it.
        var page = StripRazorComments(ReadPage());
        var sorted = StripLineComments(GetMethodBody("SortMailboxes"));

        var start = page.IndexOf("id=\"mailboxSortColumn\"", StringComparison.Ordinal);
        Assert.True(start > 0, "the mailbox sort control is gone; this test needs rewriting");

        var sortable = Regex.Matches(page[start..], @"<option value=""(?<v>[A-Za-z]+)""")
            .Select(m => m.Groups["v"].Value)
            .Take(4)
            .ToArray();

        Assert.Equal(["Email", "Status", "Synced", "LastSync"], sortable);

        foreach (var column in sortable)
        {
            // Email is the switch default rather than a named case, same shape as Created on the
            // batch side.
            Assert.True(
                sorted.Contains($"\"{column}\" =>", StringComparison.Ordinal) || column == "Email",
                $"the mailbox sort offers {column} but SortMailboxes does not order by it");

            // The header text differs from the option value in one place, and that is fine - what
            // matters is that a column exists for it.
            var header = column == "LastSync" ? "Last Sync" : column;
            Assert.True(
                page.Contains($"<th>{header}</th>", StringComparison.Ordinal),
                $"the mailbox sort offers {column} but no column header shows it, so the list "
                + "can be ordered by something the operator cannot see");
        }
    }

    [Fact]
    public void TheDirectionIconIsLabelledAndSaysSomethingDifferentForEachDirection()
    {
        // Owner, 2026-09-29: direction as an icon in the list, and do not clog the columns. An
        // icon is the one place R17 - no unlabelled values - is easiest to break, because the
        // shape reads as self-evident to whoever chose it and as nothing to everyone else.
        var page = StripRazorComments(ReadPage());

        // Labelled twice over: a title for a pointer, an aria-label for a screen reader.
        Assert.Contains("<title>@DirectionLabel(direction)</title>", page, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"@DirectionLabel(direction)\"", page, StringComparison.Ordinal);

        // And the label actually differs by direction, rather than one word for both.
        var label = StripLineComments(GetMethodBody("DirectionLabel"));
        Assert.Contains("Exchange Online", label, StringComparison.Ordinal);
        Assert.Contains("on-premises", label, StringComparison.Ordinal);

        // Neither is the raw enum name, which is a developer word and not an operator one.
        Assert.DoesNotContain("\"ToCloud\"", label, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ToOnPrem\"", label, StringComparison.Ordinal);

        // The two directions draw different arrows. A single shared glyph would be a decoration
        // that claims to carry information.
        //
        // Matched as the RENDERED path element, not as the identifier. The first version of this
        // asserted Contains("DownArrowPath"), which the const DECLARATION satisfies on its own -
        // so pointing both branches at the up arrow left it green. Caught by the probe, which is
        // the only reason it is written correctly now.
        Assert.Contains("<path d=\"@UpArrowPath\" />", page, StringComparison.Ordinal);
        Assert.Contains("<path d=\"@DownArrowPath\" />", page, StringComparison.Ordinal);

        // One definition, rendered in both batch views - the main list and the selection pane
        // show the same batches, and a direction visible in one and not the other is the gap
        // the Total column had.
        Assert.Equal(2, CountOf(page, "@DirectionIcon(batch.Direction)"));

        // Inline SVG rather than a Bootstrap Icons class: a webfont that has not loaded leaves
        // an unlabelled row instead of a visibly broken one.
        Assert.DoesNotContain("bi-cloud", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBatchGridHasTheSameColumnsInTheHeaderAndTheRows()
    {
        // R14 asks for fixed columns that align on every row, and a permission is not an
        // exception to that. The header drew an unconditional spacer for the tick-box column
        // while the row only drew a tick box when canManage, so an operator WITHOUT
        // MigrationManage read every value one column left of its heading: the batch name under
        // the blank, Status under "Batch", and Failed under a heading that was not rendered.
        //
        // Invisible to everyone who built it, because they all had the permission. The mailbox
        // table beside it already handled this - colspan="@(canManage ? 7 : 6)" - which is what
        // makes the batch grid an oversight rather than an open question.
        var page = StripRazorComments(ReadPage());

        // The spacer renders on exactly the condition the tick box does. Matched inside the
        // header block rather than as one indented literal: the indentation here is not the
        // thing under test, and pinning it makes the test fail on a reformat.
        var headAt = page.IndexOf("mig-batch-head", StringComparison.Ordinal);
        Assert.True(headAt > 0, "the batch header row is gone");

        var head = page[headAt..(headAt + page[headAt..].IndexOf("<span>Status</span>",
            StringComparison.Ordinal))];

        Assert.Contains("@if (canManage)", head, StringComparison.Ordinal);
        Assert.Contains("<span></span>", head, StringComparison.Ordinal);
        Assert.True(
            head.IndexOf("@if (canManage)", StringComparison.Ordinal)
                < head.IndexOf("<span></span>", StringComparison.Ordinal),
            "the tick-box spacer is not inside the canManage guard, so a read-only operator "
            + "reads every value one column left of its heading");

        // Header and rows take the grid from ONE property, so they cannot disagree again.
        Assert.Equal(2, CountOf(page, "@BatchGridClass"));

        // BOTH batch grids: a header cell for every track the CSS declares. Counting only the
        // main list is what let the selection pane header lose a cell in the very change that
        // fixed the main one - the icon went into its rows and not its header, and every
        // heading there sat one column left of its data.
        AssertHeaderFillsItsGrid(page, "mig-batch-row mig-batch-head", ".mig-batch-row {");
        AssertHeaderFillsItsGrid(page, "mig-batch-row mig-selected-row mig-batch-head",
            ".mig-selected-row {");

        var cls = StripLineComments(GetMemberSource(
            @"private\s+string\s+BatchGridClass", "BatchGridClass"));
        Assert.Contains("canManage", cls, StringComparison.Ordinal);
        Assert.Contains("mig-no-tick", cls, StringComparison.Ordinal);

        // And the read-only variant actually drops a track rather than only renaming the class.
        var css = File.ReadAllText(Path.Combine(GetPagesDirectory(), "Migration.razor.css"));

        Assert.Equal(TrackCount(css, ".mig-batch-row {") - 1,
            TrackCount(css, ".mig-batch-row.mig-no-tick {"));
    }

    // A header row must supply one cell per declared grid track, or every label sits over the
    // wrong value. The conditional tick-box spacer is counted once, which is correct: the
    // read-only grid drops a track to match.
    private static void AssertHeaderFillsItsGrid(string page, string headClass, string cssRule)
    {
        var css = File.ReadAllText(Path.Combine(GetPagesDirectory(), "Migration.razor.css"));
        var tracks = TrackCount(css, cssRule);

        var at = page.IndexOf(headClass, StringComparison.Ordinal);
        Assert.True(at >= 0, $"the header row '{headClass}' is gone");

        var head = page[at..(at + page[at..].IndexOf("</div>", StringComparison.Ordinal))];
        var cells = CountOf(head, "<span");

        Assert.True(cells == tracks,
            $"'{headClass}' supplies {cells} header cells for the {tracks} tracks in "
            + $"'{cssRule}', so every heading after the missing one sits over the wrong value");
    }

    // The tracks in the grid-template-columns of one CSS rule. minmax(0, 1fr) holds a comma and
    // a space, so it is collapsed before splitting or it would count as two.
    private static int TrackCount(string css, string selector)
    {
        var at = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{selector} is gone from Migration.razor.css");

        var rule = css[at..(at + css[at..].IndexOf('}'))];
        var decl = rule[rule.IndexOf("grid-template-columns:", StringComparison.Ordinal)..];
        decl = decl[..decl.IndexOf(';')]
            .Replace("grid-template-columns:", "")
            .Replace("minmax(0, 1fr)", "minmax");

        return decl.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    [Fact]
    public void TheMailboxDetailRowSpansEveryColumnOfItsTable()
    {
        // The last hand-maintained count on this page. The mailbox table is an HTML table, so it
        // sizes itself and cannot suffer the header/row mismatch the two CSS grids beside it
        // both did - a grid declares its track count separately from its cells, which is exactly
        // why both of those defects were grids. But the colspan on the detail row is still a number
        // someone has to remember, and a column added to the table without touching it leaves
        // the detail row short.
        //
        // Checked against the header rather than asserted as 7 and 6, so adding a column fixes
        // this test by construction instead of failing it.
        var page = StripRazorComments(ReadPage());

        var at = page.IndexOf("mig-mailboxes", StringComparison.Ordinal);
        Assert.True(at >= 0, "the mailbox table is gone");

        var thead = page[at..(at + page[at..].IndexOf("</thead>", StringComparison.Ordinal))];

        // "<th" also matches "<thead", which counted one column too many on the first run. The
        // character after the tag name is what separates them.
        var columns = Regex.Matches(thead, @"<th[ >]").Count;
        var withoutTickBox = columns - CountOf(thead, "@if (canManage)");

        Assert.Contains($"colspan=\"@(canManage ? {columns} : {withoutTickBox})\"",
            page, StringComparison.Ordinal);
    }

    [Fact]
    public void AllThreeEmptyStatesSayWhichOneApplies()
    {
        // R28. "Nothing matches" for all three hides whether the filter or the data is the
        // problem, and the operator's next move differs in each: create a batch, clear the batch
        // filter, or clear the mailbox filter.
        var page = StripRazorComments(ReadPage());

        Assert.Contains("No migration batches found", page, StringComparison.Ordinal);
        Assert.Contains("No batch name matches", page, StringComparison.Ordinal);
        Assert.Contains("No mailbox in this batch matches", page, StringComparison.Ordinal);
        Assert.Contains("This batch contains no mailboxes.", page, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyBatchAndAnEmptyFilterResultSayDifferentThings()
    {
        // R28. "Nothing matches" for both hides whether the filter or the data is the problem, and
        // the operator's next move is different in each case - clear the filter, or go and look at
        // why the batch is empty.
        var page = StripRazorComments(ReadPage());

        Assert.Contains("This batch contains no mailboxes.", page, StringComparison.Ordinal);
        Assert.Contains("No mailbox in this batch matches", page, StringComparison.Ordinal);

        // The filter-result state is reached only when there IS data, or it would swallow the
        // empty-batch case and the distinction would exist in the source and not on screen.
        //
        // This assertion USED to name "else if (MailboxTotalCount == 0)" and passed for months
        // while that very condition blanked the pane for any batch with every mailbox ticked
        // (see TickingEveryMailboxDoesNotEmptyThePane). It pinned the defect in place and read as
        // coverage. Pinning a condition is not the same as checking what the condition decides,
        // and a test that quotes the implementation back to itself can only ever agree with it.
        Assert.Contains("else if (!AnyMailboxRowRenders)", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSelectionPaneUsesItsOwnColumnWidthsAndItsOwnHeader()
    {
        // Reported from dev at v1.15.0: the "Open" button in the selection pane wrapped to three
        // lines, one letter each. The pane reused the batch-list grid, whose first column is
        // 1.25rem - sized for a checkbox. The selection pane has a text button there instead.
        //
        // Same defect at the other end: the batch header's "Failed" label sat over a Remove
        // button, which is R17 (no unlabelled value, and no value under the wrong label).
        var page = ReadPage();
        var css = File.ReadAllText(Path.Combine(GetPagesDirectory(), "Migration.razor.css"));

        Assert.Contains(".mig-selected-row", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 3.5rem", css, StringComparison.Ordinal);

        // Applied to BOTH the header and the rows, or they align with each other and with
        // nothing else.
        Assert.Equal(2, Regex.Matches(page, @"mig-batch-row mig-selected-row").Count);

        // The selection header does not claim a Failed column it does not have.
        var header = ExtractSpan(page, "mig-batch-row mig-selected-row mig-batch-head", "</div>");
        Assert.DoesNotContain("Failed", header, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSelectionPaneIsBoundedAndSaysWhatItIsPaging()
    {
        // R4 and R5b's reasoning applied to batches. R5 forbids pinning ticked rows into the batch
        // list, so this pane is where the selection is visible - and a pane with no bound renders
        // 2000 rows on a select-all, which is the Defender failure R20 exists for.
        //
        // It reuses ListWindow rather than repeating the arithmetic: a second copy of the
        // off-by-one is a second chance to render a blank page that reads as an empty selection.
        var page = ReadPage();

        Assert.Contains("ListWindow.Slice(SelectedBatchesInListOrder(), selectionPage, SelectionPageSize)",
            page, StringComparison.Ordinal);
        Assert.Contains("ListWindow.PageCount(SelectionTotalCount, SelectionPageSize)",
            page, StringComparison.Ordinal);

        // The label names the selection, so it cannot be mistaken for the catalogue's pager.
        var label = GetMemberBody("SelectionPagerLabel");
        Assert.Contains("\"selected batches\"", label, StringComparison.Ordinal);

        // Its own page index, not the catalogue's: paging what you are choosing from and paging
        // what you have chosen are different movements, and one index would jump both panes.
        Assert.Contains("private int selectionPage;", page, StringComparison.Ordinal);
        Assert.Contains("private int batchPage;", page, StringComparison.Ordinal);
    }

    [Fact]
    public void NoBatchActionControlDecidesEligibilityOutsideThePlanner()
    {
        // Replaces PerRowButtonsReadThePlannersStatusRulesRatherThanTheirOwn, and says plainly
        // what S2 cost.
        //
        // That test asserted the per-row Delete and Resume buttons called
        // MigrationBatchActionPlanner.Applies. S2 removed those buttons: R14 gives the batch row
        // one line and R16 names its four columns, so an action cell does not fit, and R3 and R6
        // put batch operations in a toolbar at the top of the batch pane - which is S3's slice.
        //
        // **The positive half of that guard therefore has no subject until S3 lands**, and
        // pretending otherwise with a page-wide Contains would have passed on the planner call
        // that still exists in the C# helpers while the markup grew a fresh allowlist. What is
        // asserted here is the half that still has one: no batch-status allowlist anywhere in the
        // markup. The moment S3 adds a batch-action control gated on a status string rather than
        // on the planner, this fails.
        var page = StripLineComments(ReadPage());

        var allowlisted = Regex.Matches(page, @"batch\.Status\.Equals\(")
            .Select(match => match.Value)
            .ToList();
        Assert.True(allowlisted.Count == 0,
            $"found {allowlisted.Count} batch-status allowlist tests in the markup; batch action "
            + "eligibility has one definition and it is MigrationBatchActionPlanner");
    }

    [Fact]
    public void TheMailboxesRenderInTheirOwnPaneAndNotInsideTheBatchList()
    {
        // Replaces TheExpandedDetailsRowSpansTheSelectionColumn, whose subject S2 deleted. That
        // test guarded the colspan of the nested row that injected a second table INSIDE the batch
        // list - defect 1 in the plan's "What is wrong with the page today", and the reason
        // opening a batch pushed everything below it down the page. There is no such row now, so
        // the colspan cannot be wrong; what can regress is the separation itself.
        var page = ReadPage();

        Assert.Contains("mig-pane mig-pane-left", page, StringComparison.Ordinal);
        Assert.Contains("mig-pane mig-pane-right", page, StringComparison.Ordinal);

        // The mailbox table must be outside the batch loop. Anchored by containment rather than by
        // a marker: the whole point is that one list is not nested in the other.
        var batchLoop = GetBatchRowMarkup();
        Assert.DoesNotContain("batchUsers", batchLoop, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", batchLoop, StringComparison.Ordinal);
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

        // Floor lowered from 5 to 4 in S3, and openly. The load paths consolidated into
        // LoadMailboxesFor, so there are genuinely fewer discard sites. A floor exists to catch a
        // site vanishing unnoticed, so it is re-derived from the new structure - left high the
        // suite fails forever, dropped it guards nothing. Sites now: AdoptSelectionAsOpenBatch,
        // SyncOpenBatchFromUrl, and both branches of SearchUser.
        var discards = Regex.Matches(page, @"batchUsers = null;[ \t]*\r?\n[ \t]*(?<next>[^\r\n]+)");
        Assert.True(discards.Count >= 4,
            $"expected every batchUsers discard site to still be present, found {discards.Count}");

        foreach (Match discard in discards)
        {
            Assert.StartsWith(
                "InvalidateStoredReports();",
                discard.Groups["next"].Value.Trim(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ChangingTheOpenBatchClosesTheReportBeforeLoadingTheNewRows()
    {
        // Was BothBranchesOfToggleBatchDetailsCloseTheReport, which had two branches to check
        // because ToggleBatchDetails collapsed on one path and expanded on the other. R9 removed
        // that method: there is no collapse/expand pair any more, only a selection that the pane
        // follows, and AdoptSelectionAsOpenBatch is the single place it changes.
        //
        // Fewer branches, same property, and the ordering matters as much as the presence: the
        // rows and the report must both be discarded BEFORE the new batch is loaded, or the
        // operator reads the old batch report under the new batch name.
        var body = GetMethodBody("AdoptSelectionAsOpenBatch");

        var discard = body.IndexOf("batchUsers = null;", StringComparison.Ordinal);
        var close = body.IndexOf("InvalidateStoredReports();", StringComparison.Ordinal);
        var load = body.IndexOf("LoadMailboxesFor(", StringComparison.Ordinal);

        Assert.True(discard > 0, "the open batch changing must discard the rows");
        Assert.True(close > discard, "the discard must be followed by the report close");
        Assert.True(load > close, "both must precede the load of the new batch");
    }

    [Fact]
    public void BothBranchesOfSearchUserCloseTheReport()
    {
        // Search jumps the operator to a different expanded batch. Same two-branch shape: one path
        // matches a batch name, the other resolves a user to their batch.
        var body = GetMethodBody("SearchUser");

        var batchMatch = ExtractBlock(body, "if (matchedBatch != null)");
        Assert.Contains("InvalidateStoredReports();", batchMatch, StringComparison.Ordinal);

        var userMatch = body[(body.IndexOf(batchMatch, StringComparison.Ordinal) + batchMatch.Length)..];
        Assert.Contains("InvalidateStoredReports();", userMatch, StringComparison.Ordinal);

        // S3: neither branch fetches for itself any more. Under R9 arriving at a batch IS
        // selecting it, so both hand off to AdoptSelectionAsOpenBatch, which navigates and loads.
        // Asserting the handoff is what keeps this guard pointed at the behaviour rather than at a
        // call that has moved one level down.
        Assert.Contains("AdoptSelectionAsOpenBatch()", batchMatch, StringComparison.Ordinal);
        Assert.Contains("AdoptSelectionAsOpenBatch()", userMatch, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshBatchUsersClosesTheReportBeforeRefetching()
    {
        // This one never nulls batchUsers - it refetches over the top - so the discard guard above
        // cannot see it. The report still describes the pre-refresh state of the row.
        var body = GetMethodBody("RefreshBatchUsers");

        var close = body.IndexOf("InvalidateStoredReports();", StringComparison.Ordinal);
        var fetch = body.IndexOf("LoadMailboxesFor(", StringComparison.Ordinal);
        Assert.True(close > 0, "RefreshBatchUsers must close the open report");
        Assert.True(fetch > close, "the close must precede the refetch");

        // The refetch itself is LoadMailboxesFor, which closes the report AGAIN after its await -
        // see ReplaceBatchUsers for why closing before the await is not enough on a path that
        // leaves the old rows, and their enabled Report button, rendered for the whole call.
        Assert.Contains("InvalidateStoredReports();", GetMethodBody("ReplaceBatchUsers"), StringComparison.Ordinal);
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

        // =(?!=) so a comparison is not read as an assignment. S2's right pane asks
        // `batchUsers == null` to choose its empty state, and without the lookahead that matched
        // here and reported the page as assigning rows in markup.
        var assignments = Regex.Matches(page, @"(?<![A-Za-z])batchUsers\s*=(?!=)\s*(?<value>[^\r\n]+)");
        // 4 discards plus the single permitted replacement; see the discard guard above for why
        // this floor moved in S3.
        Assert.True(assignments.Count >= 5,
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

        var close = body.IndexOf("InvalidateStoredReports();", StringComparison.Ordinal);
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

        // The generation argument mir-1 added sits before the await, so the pattern names it
        // rather than allowing anything: `ReplaceBatchUsers(x, await ...)` must still hand the
        // awaited fetch straight in, with exactly one captured token in front of it.
        var fetches = Regex.Matches(page, @"GetMigrationBatchUsersAsync\(");
        var wrapped = Regex.Matches(page, @"ReplaceBatchUsers\([A-Za-z_][A-Za-z0-9_]*, await MigrationSvc\.GetMigrationBatchUsersAsync\(");
        // Three now, not five: LoadMailboxesFor owns the refetch for every caller that used to
        // write its own. Re-derived, not relaxed - the assertion that every fetch is wrapped is
        // unchanged and is the part that bites.
        Assert.True(fetches.Count >= 3,
            $"expected every row refetch site to still be present, found {fetches.Count}");
        Assert.Equal(fetches.Count, wrapped.Count);
    }

    [Fact]
    public void ARowLoadThatLostTheRaceIsDiscardedRatherThanPublished()
    {
        // mir-1. Two batch-user loads can overlap now that the URL is an entry point: open A, open
        // B, press browser Back before B's Exchange call returns. If B's lands last, publishing it
        // renders A's row over B's mailboxes, whose per-mailbox action buttons then act on B. The
        // refusal must come before the assignment AND before the report close, or a superseded load
        // still discards a report it has no business touching.
        var body = GetMethodBody("ReplaceBatchUsers");

        var guard = body.IndexOf("if (generation != batchUsersGeneration)", StringComparison.Ordinal);
        var bail = body.IndexOf("return;", StringComparison.Ordinal);
        var close = body.IndexOf("InvalidateStoredReports();", StringComparison.Ordinal);
        var assign = body.IndexOf("batchUsers = users;", StringComparison.Ordinal);

        Assert.True(guard >= 0, "ReplaceBatchUsers must refuse a result from an overtaken load");
        Assert.True(bail > guard, "the guard must bail out");
        Assert.True(close > bail, "the guard must precede the report close");
        Assert.True(assign > close, "the guard must precede the row assignment");
    }

    [Fact]
    public void EveryChangeOfTheOpenBatchBumpsTheRowGeneration()
    {
        // The counter is only a guard if it actually counts. Both writers of expandedBatch - the
        // outbound OpenBatch and the inbound SyncOpenBatchFromUrl - have to bump it, or a load
        // started before the change still compares equal and publishes into the wrong batch.
        var page = StripLineComments(ReadPage());

        var writers = Regex.Matches(page, @"(?<![A-Za-z0-9_])expandedBatch\s*=(?!=)")
            .Select(match => EnclosingMethodName(page, match.Index))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(writers);

        foreach (var writer in writers)
        {
            Assert.Contains(
                "batchUsersGeneration++",
                StripLineComments(GetMethodBody(writer)),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryRowLoadCapturesTheGenerationBeforeItsAwait()
    {
        // The half of mir-1 that a signature change alone does not buy. Capturing AFTER the await -
        // `ReplaceBatchUsers(batchUsersGeneration, users)` on a value read once the fetch returned -
        // compiles, reads almost identically, and compares the counter with itself, so the guard
        // can never fire. Every call site must pass a local read earlier in the same method.
        var page = StripLineComments(ReadPage());

        var calls = Regex.Matches(page, @"(?<![A-Za-z0-9_])ReplaceBatchUsers\((?<token>[A-Za-z_][A-Za-z0-9_]*),")
            .ToList();
        // Five now, not eight, for the same consolidation. Each is still checked individually.
        Assert.True(calls.Count >= 5,
            $"expected every row replacement call site to still be present, found {calls.Count}");

        foreach (var call in calls)
        {
            var method = EnclosingMethodName(page, call.Index);
            var body = GetMethodBody(method);
            var token = call.Groups["token"].Value;

            var capture = body.IndexOf($"var {token} = batchUsersGeneration;", StringComparison.Ordinal);
            Assert.True(capture >= 0,
                $"{method} passes {token} to ReplaceBatchUsers but never captures it from "
                + "batchUsersGeneration; the guard compares the counter with itself and cannot fire");

            var callInBody = body.IndexOf($"ReplaceBatchUsers({token},", StringComparison.Ordinal);
            Assert.True(callInBody > capture,
                $"{method} captures {token} after it uses it");

            var awaitAt = body.IndexOf("await ", capture, StringComparison.Ordinal);
            Assert.True(awaitAt > capture,
                $"{method} captures {token} with no await after it; a capture that cannot be "
                + "overtaken is a guard that proves nothing");
        }
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

        // TWO now, not four. S3 consolidated every batch-user load into LoadMailboxesFor, so only
        // it and SyncOpenBatchFromUrl raise this flag - verified by reading the file, not guessed
        // down until the suite went green. Every setter is still required to clear in a finally,
        // which is the assertion that bites; the floor only catches a setter disappearing.
        Assert.True(setters.Count >= 2,
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
        Assert.Contains("InvalidateStoredReports();", reload, StringComparison.Ordinal);
        Assert.Contains("GetMigrationBatchUsersAsync", reload, StringComparison.Ordinal);
    }

    [Fact]
    public void ReloadingTheBatchTableClosesTheReport()
    {
        // S3 changed HOW, not whether. Under R9 a catalogue refresh is no longer a selection act,
        // so LoadMigrationStatus stops collapsing the open batch by hand and routes through
        // AdoptSelectionAsOpenBatch, which discards the rows and closes the report when the
        // selection no longer names one batch - and through LoadMailboxesFor, which closes it
        // again after the refetch lands, when it still does.
        var body = GetMethodBody("LoadMigrationStatus");

        Assert.Contains("AdoptSelectionAsOpenBatch()", body, StringComparison.Ordinal);
        Assert.Contains("LoadMailboxesFor(", body, StringComparison.Ordinal);

        // Both of those close it; neither may stop doing so.
        Assert.Contains("InvalidateStoredReports();", GetMethodBody("AdoptSelectionAsOpenBatch"), StringComparison.Ordinal);
        Assert.Contains("InvalidateStoredReports();", GetMethodBody("ReplaceBatchUsers"), StringComparison.Ordinal);
    }

    [Fact]
    public void ClosingTheReportClearsEveryFieldAndBumpsTheGeneration()
    {
        // One helper owns the clear, so a new reload path has one call to make rather than four
        // fields to remember. The generation bump is what makes the clear hold: without it a fetch
        // already in flight lands afterwards and repopulates the panel the reload just emptied.
        var body = GetMethodBody("InvalidateStoredReports");

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
        //
        // Anchored on FetchUserReport, not LoadUserReport. S6 split them: opening a report now
        // consults the store first and only calls the fetch on a miss, so the slow await - and
        // the staleness it creates - live in the fetch.
        var body = GetMethodBody("FetchUserReport");

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
        var body = GetMethodBody("FetchUserReport");

        Assert.Matches(
            new Regex(@"if \(generation == reportGeneration\)\s*\r?\n\s*loadingReport = null;"),
            body);
    }

    [Fact]
    public void CloseDismissesTheDialogAndDoesNotThrowTheReportAway()
    {
        // R24e, and the point of the whole slice. Dismissing and invalidating used to be ONE
        // method, so Close bumped the generation and deleted the text. That makes R24a impossible:
        // every close guarantees a re-fetch, and a re-fetch is twenty minutes of
        // Get-MigrationUserStatistics.
        //
        // Close must call the hiding one. The invalidating one belongs to the row-reload paths.
        var dialog = ExtractSpan(
            ReadPage(), "@if (reportUser != null && userReport != null)", "</div>\n}");

        Assert.Contains("@onclick=\"DismissReportModal\"", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidateStoredReports", dialog, StringComparison.Ordinal);

        // And the two really are different: only one bumps the generation and deletes.
        var dismiss = StripLineComments(GetMethodBody("DismissReportModal"));
        Assert.DoesNotContain("reportGeneration++", dismiss, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete", dismiss, StringComparison.Ordinal);

        var invalidate = StripLineComments(GetMethodBody("InvalidateStoredReports"));
        Assert.Contains("reportGeneration++", invalidate, StringComparison.Ordinal);
        Assert.Contains("ReportStore.DeleteAll()", invalidate, StringComparison.Ordinal);

        // No inline field reset in the markup: an inline reset cannot bump the generation, which
        // is how a manual Close during a slow fetch used to reopen the panel by itself.
        Assert.DoesNotContain("userReport = null", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void ReopeningAReportDoesNotRefetchItAndFetchAgainSaysWhatItCosts()
    {
        // R24a. Get-MigrationUserStatistics can take twenty minutes or more, so opening the
        // dialog a second time must never run it again. That is a correctness requirement about
        // operator time, not a caching optimisation.
        var open = StripLineComments(GetMethodBody("LoadUserReport"));

        // The stored copy is consulted, and a hit returns WITHOUT reaching the fetch.
        var read = open.IndexOf("ReportStore.TryRead(", StringComparison.Ordinal);
        var fetch = open.IndexOf("FetchUserReport(", StringComparison.Ordinal);
        Assert.True(read > 0, "opening a report must consult the store first");
        Assert.True(fetch > read, "the fetch must come after the store read, not before it");
        Assert.Contains("return;", open[read..fetch], StringComparison.Ordinal);

        // Re-fetching is its own deliberate control, and its tooltip states the cost.
        var dialog = ExtractSpan(
            ReadPage(), "@if (reportUser != null && userReport != null)", "</div>\n}");
        Assert.Contains("RefetchUserReport", dialog, StringComparison.Ordinal);
        Assert.Matches(@"twenty minutes", dialog);

        // An error is NOT stored: keeping it would serve the failure back on the next open as
        // though it were the answer.
        var fetchBody = StripLineComments(GetMethodBody("FetchUserReport"));
        var write = fetchBody.IndexOf("ReportStore.Write(", StringComparison.Ordinal);
        var errorBranch = fetchBody.IndexOf("$\"Error: {ex.Message}\"", StringComparison.Ordinal);
        Assert.True(write > 0 && errorBranch > write,
            "the store write must be on the success path only");
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
        ["DismissReportModal"] =
            "rendered only once a report has landed, so there is no pull for it to interrupt - "
            + "and under R24e it must stay live for a stronger reason than convenience: it now "
            + "only HIDES the dialog, the report survives, and an operator who cannot close a "
            + "dialog while the page is busy is trapped behind a report they have finished with",
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

        // Close is rendered only once a report has landed, and it lives in the dialog now rather
        // than in a drawer under the row (R24).
        var dialog = ExtractSpan(
            ReadPage(), "@if (reportUser != null && userReport != null)", "mig-report-text");
        Assert.Contains("@onclick=\"DismissReportModal\"", dialog, StringComparison.Ordinal);

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
        // strip - and the Migration Status tab invalidates the stored reports, so clicking it mid-pull
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

    /// <summary>
    /// <paramref name="source"/> with Razor <c>@* *@</c> comments removed.
    /// </summary>
    /// <remarks>
    /// The markup's prose explains the very rules these guards assert - which phrase must not sit
    /// beside which - so a comment documenting a rule would otherwise be read as code breaking it.
    /// Same reasoning as <see cref="StripLineComments"/>, for the other comment syntax on the page.
    /// </remarks>
    private static string StripRazorComments(string source) =>
        Regex.Replace(source, @"@\*.*?\*@", "", RegexOptions.Singleline);

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
        ExtractBlock(ReadPage(), "@foreach (var batch in GetPagedBatches())");

    /// <summary>The markup emitted per user row inside an expanded batch.</summary>
    /// <summary>The markup emitted per mailbox row.</summary>
    /// <remarks>
    /// Anchored on the MailboxRow fragment, not on a loop. S5 step 2b defined the row ONCE and
    /// renders it in two places - the pinned block above the "OTHER MAILBOXES" divider and the
    /// list below it - so a loop-anchored slice would cover one of the two and report the other
    /// as absent.
    /// </remarks>
    private static string GetUserRowMarkup() =>
        ExtractSpan(ReadPage(), "private RenderFragment<MigrationUserInfo> MailboxRow", "</text>;");

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
