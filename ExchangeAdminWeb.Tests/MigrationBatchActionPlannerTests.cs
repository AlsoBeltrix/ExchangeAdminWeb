using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Behavioural coverage for the Migration Status bulk-selection logic.
/// </summary>
/// <remarks>
/// This logic lives in a service class precisely so it can be tested: the repo has no bUnit
/// harness, so anything left in Migration.razor is reachable only by a source-level tripwire. The
/// page-side guards in MigrationStatusPageTests prove the page CALLS this; these tests prove it is
/// right.
/// </remarks>
public class MigrationBatchActionPlannerTests
{
    [Theory]
    [InlineData("Completed")]
    [InlineData("CompletedWithErrors")]
    [InlineData("Failed")]
    [InlineData("Stopped")]
    [InlineData("Corrupted")]
    [InlineData("Syncing")]
    [InlineData("Synced")]
    [InlineData("Starting")]
    [InlineData("NeedsApproval")]
    [InlineData("SomeStatusNobodyHasSeenYet")]
    [InlineData(null)]
    [InlineData("")]
    public void Applies_Delete_AcceptsAnyStatusWhatsoever(string? status)
    {
        // D3: Delete carries no status list. Exchange is the authority on what it will remove, and
        // a refusal returns as that row's own per-item failure, which the executor names. A
        // client-side allowlist can only be wrong in the direction that hides a row the operator
        // explicitly ticked - which is exactly what happened to CompletedWithErrors.
        Assert.True(MigrationBatchActionPlanner.Applies(MigrationBatchAction.Delete, status));
    }

    [Theory]
    [InlineData("Completed", true)]
    [InlineData("completed", true)]
    [InlineData(" Completed ", true)]
    [InlineData("CompletedWithErrors", false)]
    [InlineData("Failed", false)]
    [InlineData("Stopped", false)]
    [InlineData("Syncing", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Applies_RemoveCompleted_MatchesExactlyCompleted(string? status, bool expected)
    {
        // D3, owner: "Completed is the only valid target for Clear Completed." CompletedWithErrors
        // is deliberately NOT swept - a batch that finished with errors is not a batch that
        // finished, and folding it into a bulk removal destroys the evidence of what went wrong.
        Assert.Equal(expected, MigrationBatchActionPlanner.Applies(MigrationBatchAction.RemoveCompleted, status));
    }

    [Theory]
    [InlineData("CompletedWithErrors", true)]
    [InlineData("Stopped", true)]
    [InlineData("Failed", true)]
    [InlineData("Paused", true)]
    [InlineData("Corrupted", true)]
    [InlineData("Synced", true)]
    [InlineData("NeedsApproval", true)]
    [InlineData("Completed", false)]
    [InlineData("Syncing", false)]
    [InlineData("Starting", false)]
    [InlineData("Stopping", false)]
    [InlineData("Completing", false)]
    [InlineData("Removing", false)]
    public void Applies_Resume_ExcludesTheWorkingStatusesAndCompleted(string status, bool expected)
    {
        // D4. Two separate reasons for the two excluded groups: the working statuses have nothing
        // to resume because the batch is already moving; Completed is idle but DONE.
        // Idle-and-restartable is not the same as idle.
        Assert.Equal(expected, MigrationBatchActionPlanner.Applies(MigrationBatchAction.Resume, status));
    }

    [Fact]
    public void Applies_Resume_OffersAStatusThisCodebaseHasNeverSeen()
    {
        // The whole reason D4 is an exclusion list. CompletedWithErrors is a real Exchange status
        // that appeared NOWHERE in this codebase, so a batch in that state had no buttons at all
        // and no test could have caught it - every test used the same status list the code did.
        // An unanticipated status must default to VISIBLE and let Exchange refuse it.
        Assert.True(MigrationBatchActionPlanner.Applies(MigrationBatchAction.Resume, "QuarantinedPendingReview"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Applies_Resume_RefusesAMissingStatus(string? status)
    {
        // Fail closed HERE specifically: an unreadable status cannot rule out that the batch is
        // mid-flight, and restarting one that is, is the harmful direction. Delete has no such
        // hazard - Exchange refuses a running batch itself.
        Assert.False(MigrationBatchActionPlanner.Applies(MigrationBatchAction.Resume, status));
    }

    [Fact]
    public void Plan_SplitsAMixedSelectionIntoEligibleAndSkipped()
    {
        // The normal case at 50+ in-flight batches, and the whole point of owner ruling D2(a).
        // Uses RemoveCompleted because Delete no longer skips anything.
        var loaded = Batches(
            ("done-1", "Completed"),
            ("running-1", "Syncing"),
            ("dead-1", "Failed"));

        var plan = MigrationBatchActionPlanner.Plan(
            loaded, ["done-1", "running-1", "dead-1"], MigrationBatchAction.RemoveCompleted);

        Assert.Equal(["done-1"], plan.Eligible);
        Assert.Equal(2, plan.Skipped.Count);
        Assert.Contains(plan.Skipped, s => s.BatchName == "running-1" && s.Status == "Syncing");
        Assert.Contains(plan.Skipped, s => s.BatchName == "dead-1" && s.Status == "Failed");
    }

    [Fact]
    public void Plan_Delete_SkipsNothingWhateverTheSelectionHolds()
    {
        // The owner's reported failure, inverted into a guard. Ticking two batches and asking to
        // act returned "No batches to act on. Skipped 2: ... (Completed), ... (CompletedWithErrors)."
        // Delete must never produce that: every ticked row is a target.
        var loaded = Batches(
            ("done-1", "Completed"),
            ("errored-1", "CompletedWithErrors"),
            ("running-1", "Syncing"),
            ("odd-1", "SomethingUnheardOf"));

        var plan = MigrationBatchActionPlanner.Plan(
            loaded, ["done-1", "errored-1", "running-1", "odd-1"], MigrationBatchAction.Delete);

        Assert.Equal(["done-1", "errored-1", "running-1", "odd-1"], plan.Eligible);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Plan_Resume_ActsOnCompletedWithErrorsAndSkipsCompleted()
    {
        // The exact selection the owner tried on dev. Before D3/D4 this returned nothing to do.
        var loaded = Batches(
            ("james", "Completed"),
            ("apriljoy", "CompletedWithErrors"));

        var plan = MigrationBatchActionPlanner.Plan(
            loaded, ["james", "apriljoy"], MigrationBatchAction.Resume);

        Assert.Equal(["apriljoy"], plan.Eligible);
        var skip = Assert.Single(plan.Skipped);
        Assert.Equal("james", skip.BatchName);
        Assert.Equal("Completed", skip.Status);
    }

    [Fact]
    public void Plan_RemoveCompleted_LeavesCompletedWithErrorsAlone()
    {
        // The inverse of the guard above, and the one that protects evidence: a bulk "remove the
        // finished ones" must not quietly destroy the batches that finished BADLY.
        var loaded = Batches(
            ("done-1", "Completed"),
            ("errored-1", "CompletedWithErrors"));

        var plan = MigrationBatchActionPlanner.Plan(
            loaded, ["done-1", "errored-1"], MigrationBatchAction.RemoveCompleted);

        Assert.Equal(["done-1"], plan.Eligible);
        Assert.Equal("errored-1", Assert.Single(plan.Skipped).BatchName);
    }

    [Fact]
    public void DescribeSelectionByStatus_CountsEachStatusInTableOrder()
    {
        // D5: Delete's confirmation must show what the operator is about to destroy, because Delete
        // is the one action that accepts in-progress batches.
        var loaded = Batches(
            ("a", "Completed"),
            ("b", "Syncing"),
            ("c", "Completed"),
            ("d", "CompletedWithErrors"));

        var text = MigrationBatchActionPlanner.DescribeSelectionByStatus(loaded, ["a", "b", "c", "d"]);

        Assert.Equal("2 Completed, 1 Syncing, 1 CompletedWithErrors", text);
    }

    [Fact]
    public void DescribeSelectionByStatus_CountsOnlyWhatIsSelected()
    {
        var loaded = Batches(("a", "Completed"), ("b", "Syncing"));

        Assert.Equal("1 Syncing", MigrationBatchActionPlanner.DescribeSelectionByStatus(loaded, ["b"]));
        Assert.Equal("", MigrationBatchActionPlanner.DescribeSelectionByStatus(loaded, []));
        Assert.Equal("", MigrationBatchActionPlanner.DescribeSelectionByStatus(null, ["a"]));
    }

    [Fact]
    public void Plan_ConsidersOnlySelectedBatches()
    {
        var loaded = Batches(("done-1", "Completed"), ("done-2", "Completed"));

        var plan = MigrationBatchActionPlanner.Plan(loaded, ["done-2"], MigrationBatchAction.Delete);

        Assert.Equal(["done-2"], plan.Eligible);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Plan_DropsASelectedNameThatIsNoLongerLoaded()
    {
        // The batch is gone - removed by another operator, or by Exchange. Acting on it would fail
        // in a way that reads as an app bug; reporting it as skipped would name a row the operator
        // can no longer see. It is neither.
        var loaded = Batches(("done-1", "Completed"));

        var plan = MigrationBatchActionPlanner.Plan(
            loaded, ["done-1", "vanished"], MigrationBatchAction.Delete);

        Assert.Equal(["done-1"], plan.Eligible);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Plan_OrdersResultsByTheLoadedTableNotTheSelection()
    {
        // So the report reads in the same order as the rows on screen.
        var loaded = Batches(("a", "Completed"), ("b", "Completed"), ("c", "Completed"));

        var plan = MigrationBatchActionPlanner.Plan(loaded, ["c", "a", "b"], MigrationBatchAction.Delete);

        Assert.Equal(["a", "b", "c"], plan.Eligible);
    }

    [Fact]
    public void Plan_MatchesNamesCaseInsensitively()
    {
        var loaded = Batches(("Batch-Alpha", "Stopped"));

        var plan = MigrationBatchActionPlanner.Plan(loaded, ["batch-alpha"], MigrationBatchAction.Resume);

        Assert.Equal(["Batch-Alpha"], plan.Eligible);
    }

    [Fact]
    public void Plan_ReturnsTheLoadedSpellingNotTheSelectedOne()
    {
        // The name is passed to Remove-MigrationBatch as an identity, so it must be the one
        // Exchange gave us.
        var loaded = Batches(("Batch-Alpha", "Completed"));

        var plan = MigrationBatchActionPlanner.Plan(loaded, ["BATCH-ALPHA"], MigrationBatchAction.Delete);

        Assert.Equal("Batch-Alpha", Assert.Single(plan.Eligible));
    }

    [Fact]
    public void Plan_AnEntirelyIneligibleSelectionIsAllSkipsAndNoError()
    {
        // D2(a): "nothing to do" is not a failure. The planner reports it as skips with no
        // eligible rows, and it is the caller's job not to dress that up as an error.
        //
        // Retargeted at RemoveCompleted for round 2: Delete no longer skips anything (D3), so it
        // can no longer produce this shape at all.
        var loaded = Batches(("running-1", "Syncing"), ("running-2", "Starting"));

        var plan = MigrationBatchActionPlanner.Plan(
            loaded, ["running-1", "running-2"], MigrationBatchAction.RemoveCompleted);

        Assert.Empty(plan.Eligible);
        Assert.Equal(2, plan.Skipped.Count);
    }

    [Fact]
    public void Plan_EmptySelectionPlansNothing()
    {
        var loaded = Batches(("done-1", "Completed"));

        var plan = MigrationBatchActionPlanner.Plan(loaded, [], MigrationBatchAction.Delete);

        Assert.Empty(plan.Eligible);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Plan_ToleratesNoBatchesLoaded()
    {
        var plan = MigrationBatchActionPlanner.Plan(null, ["done-1"], MigrationBatchAction.Delete);

        Assert.Empty(plan.Eligible);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Plan_IgnoresBlankSelectedNames()
    {
        var loaded = Batches(("done-1", "Completed"));

        var plan = MigrationBatchActionPlanner.Plan(
            loaded, ["", "   ", "done-1"], MigrationBatchAction.Delete);

        Assert.Equal(["done-1"], plan.Eligible);
    }

    [Fact]
    public void Plan_CountsADuplicatedBatchNameOnce()
    {
        // Two rows with one name should not produce two Remove-MigrationBatch calls; the second
        // would fail against a batch the first already removed.
        var loaded = Batches(("done-1", "Completed"), ("done-1", "Completed"));

        var plan = MigrationBatchActionPlanner.Plan(loaded, ["done-1"], MigrationBatchAction.Delete);

        Assert.Single(plan.Eligible);
    }

    [Fact]
    public void PruneSelection_KeepsOnlyBatchesStillLoaded()
    {
        var loaded = Batches(("done-1", "Completed"), ("running-1", "Syncing"));

        var kept = MigrationBatchActionPlanner.PruneSelection(
            loaded, ["done-1", "running-1", "vanished"]);

        Assert.Equal(["done-1", "running-1"], kept);
    }

    [Fact]
    public void PruneSelection_NeverAddsABatchThatWasNotSelected()
    {
        // The direction that matters. Pruning runs on every reload and writes back the selection,
        // so a prune that returns unticked rows silently ADDS them - and the next Delete removes
        // batches the operator never chose. A mutation probe found this uncovered: every other
        // prune test happens to select every loaded row, so disabling the membership check
        // entirely left them all green.
        var loaded = Batches(("done-1", "Completed"), ("untouched", "Completed"));

        var kept = MigrationBatchActionPlanner.PruneSelection(loaded, ["done-1"]);

        Assert.Equal(["done-1"], kept);
        Assert.DoesNotContain("untouched", kept);
    }

    [Fact]
    public void PruneSelection_EmptySelectionStaysEmpty()
    {
        // The extreme of the same rule: nothing ticked must not come back as everything ticked.
        var loaded = Batches(("done-1", "Completed"), ("done-2", "Completed"));

        Assert.Empty(MigrationBatchActionPlanner.PruneSelection(loaded, []));
        Assert.Empty(MigrationBatchActionPlanner.PruneSelection(loaded, null));
    }

    [Fact]
    public void PruneSelection_KeepsASelectedBatchWhateverItsStatus()
    {
        // Pruning is about existence, not eligibility. A Syncing batch stays ticked so the operator
        // can Resume it later without re-finding it among fifty rows.
        var loaded = Batches(("running-1", "Syncing"));

        var kept = MigrationBatchActionPlanner.PruneSelection(loaded, ["running-1"]);

        Assert.Equal(["running-1"], kept);
    }

    [Fact]
    public void PruneSelection_EmptiesWhenNothingIsLoaded()
    {
        Assert.Empty(MigrationBatchActionPlanner.PruneSelection(null, ["done-1"]));
        Assert.Empty(MigrationBatchActionPlanner.PruneSelection([], ["done-1"]));
    }

    [Fact]
    public void PruneSelection_ReturnsTheLoadedSpelling()
    {
        var loaded = Batches(("Batch-Alpha", "Completed"));

        var kept = MigrationBatchActionPlanner.PruneSelection(loaded, ["batch-alpha"]);

        Assert.Equal("Batch-Alpha", Assert.Single(kept));
    }

    [Fact]
    public void DescribeSkipped_NamesEverySkippedBatchWithItsStatus()
    {
        // A count alone ("skipped 3") cannot be acted on - the operator has to know WHICH three.
        var text = MigrationBatchActionPlanner.DescribeSkipped(
        [
            new MigrationBatchSkip("running-1", "Syncing"),
            new MigrationBatchSkip("new-1", "Starting")
        ]);

        Assert.Contains("Skipped 2", text);
        Assert.Contains("running-1 (Syncing)", text);
        Assert.Contains("new-1 (Starting)", text);
    }

    [Fact]
    public void DescribeSkipped_SaysNothingWhenNothingWasSkipped()
    {
        Assert.Equal("", MigrationBatchActionPlanner.DescribeSkipped([]));
        Assert.Equal("", MigrationBatchActionPlanner.DescribeSkipped(null));
    }

    [Fact]
    public void DescribeSkipped_DoesNotCallASkipAFailure()
    {
        // D2(a): a skipped row is not an error, and the wording the operator reads must not imply
        // one. A run that skips three and fails none must not read as three failures.
        var text = MigrationBatchActionPlanner.DescribeSkipped(
            [new MigrationBatchSkip("running-1", "Syncing")]);

        Assert.DoesNotContain("fail", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", text, StringComparison.OrdinalIgnoreCase);
    }

    private static List<MigrationBatchInfo> Batches(params (string Name, string Status)[] rows) =>
        rows.Select(r => new MigrationBatchInfo { BatchName = r.Name, Status = r.Status }).ToList();
}
