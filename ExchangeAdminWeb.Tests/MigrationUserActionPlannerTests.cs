using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Behavioural coverage for the mailbox bulk-selection logic.
/// </summary>
/// <remarks>
/// The mailbox counterpart of MigrationBatchActionPlannerTests, and it exists for the same reason:
/// this repo has no bUnit harness, so anything left in Migration.razor is reachable only by a
/// source-level tripwire. R12 says both action bars are identical in behaviour, and these tests
/// are what "identical" means - the same eligible/skipped split, the same pruning, the same
/// named skips.
/// </remarks>
public class MigrationUserActionPlannerTests
{
    private static List<MigrationUserInfo> Loaded(params (string Email, string Status)[] rows) =>
        rows.Select(r => new MigrationUserInfo { EmailAddress = r.Email, Status = r.Status }).ToList();

    [Theory]
    [InlineData(MigrationUserAction.Complete, "Synced", true)]
    [InlineData(MigrationUserAction.Complete, "synced", true)]
    [InlineData(MigrationUserAction.Complete, "Syncing", false)]
    [InlineData(MigrationUserAction.Complete, "Completed", false)]
    [InlineData(MigrationUserAction.Approve, "NeedsApproval", true)]
    [InlineData(MigrationUserAction.Approve, "Synced", false)]
    [InlineData(MigrationUserAction.Pause, "Syncing", true)]
    [InlineData(MigrationUserAction.Pause, "Synced", false)]
    [InlineData(MigrationUserAction.Pause, "Stopped", false)]
    [InlineData(MigrationUserAction.Resume, "Stopped", true)]
    [InlineData(MigrationUserAction.Resume, "Failed", true)]
    [InlineData(MigrationUserAction.Resume, "Syncing", false)]
    [InlineData(MigrationUserAction.Clear, "Completed", true)]
    [InlineData(MigrationUserAction.Clear, "CompletedWithErrors", false)]
    [InlineData(MigrationUserAction.Clear, "Synced", false)]
    public void Applies_MatchesTheStatusesTheRowButtonsUsed(
        MigrationUserAction action, string status, bool expected)
    {
        // Ported character for character from the per-row buttons. S5 relocates controls; it does
        // not change which mailboxes an action will run on.
        Assert.Equal(expected, MigrationUserActionPlanner.Applies(action, status));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Applies_RefusesEveryActionOnAMissingStatus(string? status)
    {
        // Fail closed, and here it matters more than on batches. Every action in this planner
        // changes ONE person's mailbox mid-migration - completing cuts them over, pausing halts
        // their move, clearing drops them from the batch. An unreadable status cannot rule out
        // that the mailbox is in flight, and acting on one that is, is the harmful direction.
        //
        // This pins the BEHAVIOUR, not a guard. A mutation probe showed the explicit early return
        // was dead - every arm refuses null anyway, because each asks whether the status equals a
        // named value - so the guard was deleted and this test kept. It fails the moment an arm is
        // rewritten by exclusion, which is the change that would start accepting a missing status.
        foreach (var action in Enum.GetValues<MigrationUserAction>())
        {
            Assert.False(MigrationUserActionPlanner.Applies(action, status),
                $"{action} accepted a missing status");
        }
    }

    [Fact]
    public void Applies_RefusesAStatusThisCodebaseHasNeverSeen()
    {
        // The deliberate difference from the batch planner's Delete and Resume, which take
        // exclusion so an unanticipated status still gets a button. These are allowlists on
        // purpose: betting that Exchange will refuse a Complete on an unknown status risks cutting
        // a person over. Recorded so a later reader does not "fix" it into exclusion by symmetry.
        foreach (var action in Enum.GetValues<MigrationUserAction>())
        {
            Assert.False(MigrationUserActionPlanner.Applies(action, "QuarantinedPendingReview"));
        }
    }

    [Fact]
    public void Plan_SplitsAMixedSelectionIntoEligibleAndSkipped()
    {
        var loaded = Loaded(
            ("a@x.com", "Synced"),
            ("b@x.com", "Syncing"),
            ("c@x.com", "Completed"));

        var plan = MigrationUserActionPlanner.Plan(
            loaded, ["a@x.com", "b@x.com", "c@x.com"], MigrationUserAction.Complete);

        Assert.Equal(["a@x.com"], plan.Eligible);
        Assert.Equal(["b@x.com", "c@x.com"], plan.Skipped.Select(s => s.EmailAddress));
        Assert.Equal(["Syncing", "Completed"], plan.Skipped.Select(s => s.Status));
    }

    [Fact]
    public void Plan_OrdersTheReportByTheTableAndNotBySelection()
    {
        // The report reads in the same order as the rows the operator is looking at. Ordering by
        // the selection set would list them in hash order, which is arbitrary and looks like a bug.
        var loaded = Loaded(("a@x.com", "Synced"), ("b@x.com", "Synced"), ("c@x.com", "Synced"));

        var plan = MigrationUserActionPlanner.Plan(
            loaded, ["c@x.com", "a@x.com", "b@x.com"], MigrationUserAction.Complete);

        Assert.Equal(["a@x.com", "b@x.com", "c@x.com"], plan.Eligible);
    }

    [Fact]
    public void Plan_DropsASelectedMailboxThatHasLeftTheBatch()
    {
        // Neither acted on nor reported as skipped. It is gone: acting on it produces a failure
        // that reads as an app bug, and naming it as skipped points at a row that is not there.
        var loaded = Loaded(("a@x.com", "Synced"));

        var plan = MigrationUserActionPlanner.Plan(
            loaded, ["a@x.com", "vanished@x.com"], MigrationUserAction.Complete);

        Assert.Equal(["a@x.com"], plan.Eligible);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Plan_CountsADuplicateInTheSelectionOnlyOnce()
    {
        // A selection is a set, and the addresses differ only by case. This pins ToAddressSet.
        var loaded = Loaded(("a@x.com", "Synced"));

        var plan = MigrationUserActionPlanner.Plan(
            loaded, ["a@x.com", "A@X.COM"], MigrationUserAction.Complete);

        Assert.Equal(["a@x.com"], plan.Eligible);
    }

    [Fact]
    public void Plan_CountsADuplicateInTheLOADEDROWSOnlyOnce()
    {
        // A DIFFERENT duplicate, and the one the `seen` set in Plan actually guards: the same
        // mailbox appearing twice in the rows Exchange returned. The first version of the
        // selection-duplicate test above was written believing it covered this, and a mutation
        // probe proved otherwise - deleting `seen` left it green, because ToAddressSet had already
        // collapsed the selection. Two duplicates, two sources, two tests.
        //
        // What it prevents: one Complete call issued twice for one person.
        var loaded = Loaded(("a@x.com", "Synced"), ("A@X.COM", "Synced"));

        var plan = MigrationUserActionPlanner.Plan(loaded, ["a@x.com"], MigrationUserAction.Complete);

        Assert.Equal(["a@x.com"], plan.Eligible);
    }

    [Fact]
    public void Plan_ReportsADuplicateSkipOnlyOnce()
    {
        // The same guard on the other branch. A mailbox listed twice and ineligible must be named
        // once in the skipped list, not twice - a report that double-counts is a report the
        // operator cannot reconcile against the table.
        var loaded = Loaded(("a@x.com", "Syncing"), ("A@X.COM", "Syncing"));

        var plan = MigrationUserActionPlanner.Plan(loaded, ["a@x.com"], MigrationUserAction.Complete);

        Assert.Single(plan.Skipped);
        Assert.Equal("a@x.com", plan.Skipped[0].EmailAddress);
    }

    [Fact]
    public void PruneSelection_KeepsOnlyMailboxesStillInTheBatch()
    {
        var loaded = Loaded(("a@x.com", "Synced"), ("b@x.com", "Syncing"));

        var kept = MigrationUserActionPlanner.PruneSelection(loaded, ["b@x.com", "gone@x.com"]);

        Assert.Equal(["b@x.com"], kept);
    }

    [Fact]
    public void DescribeSkipped_NamesEveryMailboxAndItsStatus()
    {
        // A bare count cannot be acted on: "3 skipped" tells the operator nothing about which
        // three or what to do about them.
        var text = MigrationUserActionPlanner.DescribeSkipped(
        [
            new MigrationUserSkip("a@x.com", "Syncing"),
            new MigrationUserSkip("b@x.com", "Completed"),
        ]);

        Assert.Contains("a@x.com (Syncing)", text, StringComparison.Ordinal);
        Assert.Contains("b@x.com (Completed)", text, StringComparison.Ordinal);
        Assert.Contains("Skipped 2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeSkipped_SaysNothingWhenNothingWasSkipped()
    {
        Assert.Equal("", MigrationUserActionPlanner.DescribeSkipped([]));
        Assert.Equal("", MigrationUserActionPlanner.DescribeSkipped(null));
    }

    // ---- NarrowToSelection: the owner's 2026-10-09 narrowing ruling ----------------------------
    // docs/MigrationRemoveCompleted-Plan.md. A control that does not require a selection acts on
    // everything in view with nothing ticked, and ticking NARROWS it rather than enabling it.

    [Fact]
    public void NarrowToSelection_WithNothingTicked_IsEverythingInView()
    {
        var inView = Loaded(("a@x.com", "Completed"), ("b@x.com", "Syncing"));

        var candidates = MigrationUserActionPlanner.NarrowToSelection(inView, []);

        Assert.Equal(["a@x.com", "b@x.com"], candidates);
    }

    [Fact]
    public void NarrowToSelection_WithSomethingTicked_IsOnlyTheTickedOnes()
    {
        // The half the first revision of the plan contradicted: it recorded the ruling and then
        // described the scope as the in-view set regardless, which is a different set the moment
        // anything is ticked.
        var inView = Loaded(("a@x.com", "Completed"), ("b@x.com", "Completed"));

        var candidates = MigrationUserActionPlanner.NarrowToSelection(inView, ["b@x.com"]);

        Assert.Equal(["b@x.com"], candidates);
    }

    [Fact]
    public void NarrowToSelection_KeepsATickedMailboxThatIsNotInView()
    {
        // The case that proves the two sets are genuinely different rather than usually equal,
        // and the reason the selection is passed WHOLE instead of intersected with what is in
        // view. The page pins ticked mailboxes above the list precisely so a filter cannot hide
        // them (R11), so a ticked row the filter excludes is still on screen and still ticked;
        // intersecting would act on fewer mailboxes than the pinned block is showing.
        var inView = Loaded(("a@x.com", "Completed"));

        var candidates = MigrationUserActionPlanner.NarrowToSelection(inView, ["z@x.com"]);

        Assert.Equal(["z@x.com"], candidates);
    }

    [Fact]
    public void NarrowToSelection_DoesNotApplyEligibility()
    {
        // The candidate set is everything in scope, not everything eligible. Pre-filtering here
        // would make an ineligible row vanish from the preview instead of being named as skipped
        // on its own row, which is the whole difference between this control and a silent sweep.
        var inView = Loaded(("done@x.com", "Completed"), ("moving@x.com", "Syncing"));

        var candidates = MigrationUserActionPlanner.NarrowToSelection(inView, []);
        var plan = MigrationUserActionPlanner.Plan(inView, candidates, MigrationUserAction.Clear);

        Assert.Equal(["done@x.com", "moving@x.com"], candidates);
        Assert.Equal(["done@x.com"], plan.Eligible);
        Assert.Equal(["moving@x.com"], plan.Skipped.Select(s => s.EmailAddress));
    }

    [Fact]
    public void NarrowToSelection_DropsBlanksAndDuplicatesFromBothSides()
    {
        var inView = Loaded(("a@x.com", "Completed"), ("A@X.com", "Completed"), ("", "Completed"));

        Assert.Equal(["a@x.com"], MigrationUserActionPlanner.NarrowToSelection(inView, []));
        Assert.Equal(["b@x.com"],
            MigrationUserActionPlanner.NarrowToSelection(inView, [" b@x.com ", "B@x.com", "  "]));
    }

    [Fact]
    public void ASelectionLeftOverFromAnotherBatchDoesNotNarrowThisOne()
    {
        // Review finding rc-2. Nothing on the Migration page clears selectedMailboxes when the
        // open batch changes, so an address ticked in batch A is still in the set while batch B
        // is open and no row in B shows a tick. Unpruned, that address takes the narrowing branch
        // in a batch where the operator ticked nothing.
        var batchB = Loaded(("b1@x.com", "Completed"), ("b2@x.com", "Completed"));
        string[] tickedInBatchA = ["a1@x.com"];

        var live = MigrationUserActionPlanner.PruneSelection(batchB, tickedInBatchA);
        Assert.Empty(live);

        var candidates = MigrationUserActionPlanner.NarrowToSelection(batchB, live);
        Assert.Equal(["b1@x.com", "b2@x.com"], candidates);

        // Not vacuous: unpruned, the stale address wins and the control is scoped to a mailbox
        // that is not in this batch at all, so it counts and removes nothing.
        var unpruned = MigrationUserActionPlanner.NarrowToSelection(batchB, tickedInBatchA);

        Assert.Equal(["a1@x.com"], unpruned);
        Assert.Empty(
            MigrationUserActionPlanner.Plan(batchB, unpruned, MigrationUserAction.Clear).Eligible);
    }

    [Fact]
    public void PruningTheSelectionStillKeepsATickedMailboxTheFilterHides()
    {
        // The prune reads the LOADED rows, not the in-view ones, and that is load-bearing: R11
        // says a ticked mailbox is never hidden by the filter, and pruning against the filtered
        // set instead would silently drop exactly those rows from the narrowing.
        var loaded = Loaded(("shown@x.com", "Completed"), ("hidden@x.com", "Completed"));
        var inView = loaded.Where(u => u.EmailAddress == "shown@x.com");

        var live = MigrationUserActionPlanner.PruneSelection(loaded, ["hidden@x.com"]);
        var candidates = MigrationUserActionPlanner.NarrowToSelection(inView, live);

        Assert.Equal(["hidden@x.com"], candidates);
    }

    [Fact]
    public void TheLabelsCountIsTheSetTheActionStages()
    {
        // The assertion that matters most on this control: a count derived beside the set it
        // describes rather than FROM it is how a destructive label promises one number and sends
        // another. Both the page's RemoveCompletedCount and its staged plan are this expression.
        var loaded = Loaded(
            ("a@x.com", "Completed"), ("b@x.com", "Syncing"), ("c@x.com", "Completed"));

        var candidates = MigrationUserActionPlanner.NarrowToSelection(loaded, []);
        var plan = MigrationUserActionPlanner.Plan(loaded, candidates, MigrationUserAction.Clear);

        Assert.Equal(2, plan.Eligible.Count);
        Assert.Equal(["a@x.com", "c@x.com"], plan.Eligible);
    }

    [Fact]
    public void AStagedScopeCannotGrowWhenTheFilterAndTheSelectionChangeUnderIt()
    {
        // docs/MigrationRemoveCompleted-Plan.md test 7, the test review finding HIGH 2 asked for.
        // The mailbox filter box is gated on IsBusy only, not on pendingActionLabel, so it stays
        // typeable while the ticket field is open - and the tick boxes are deliberately ungated.
        // Staging therefore captures the candidate ADDRESSES, and the callback re-plans from that
        // capture rather than recomputing the scope.
        var loaded = Loaded(
            ("a@x.com", "Completed"),
            ("b@x.com", "Completed"),
            ("c@x.com", "Syncing"),
            ("d@x.com", "Completed"),
            ("e@x.com", "Completed"));

        // Staged with nothing ticked and a filter leaving a, b and c in view.
        var inViewAtStaging = loaded.Where(u => u.EmailAddress is "a@x.com" or "b@x.com" or "c@x.com");
        var scope = MigrationUserActionPlanner.NarrowToSelection(inViewAtStaging, []);
        var staged = MigrationUserActionPlanner.Plan(loaded, scope, MigrationUserAction.Clear);

        Assert.Equal(["a@x.com", "b@x.com"], staged.Eligible);
        Assert.Equal(["c@x.com"], staged.Skipped.Select(s => s.EmailAddress));

        // The operator now clears the filter AND ticks two mailboxes that were never in scope.
        // What the callback actually does: re-plan from the capture against live rows.
        var executed = MigrationUserActionPlanner.Plan(loaded, scope, MigrationUserAction.Clear);

        Assert.Equal(staged.Eligible, executed.Eligible);
        Assert.True(executed.Eligible.Count <= staged.Eligible.Count);
        Assert.DoesNotContain("d@x.com", executed.Eligible);
        Assert.DoesNotContain("e@x.com", executed.Eligible);

        // Not vacuous: the recompute the design forbids - revision 1 of the plan specified it -
        // reaches a strictly larger set from the same page state, so the assertions above
        // distinguish the two implementations rather than holding for either.
        var recomputed = MigrationUserActionPlanner.Plan(
            loaded,
            MigrationUserActionPlanner.NarrowToSelection(loaded, ["d@x.com", "e@x.com"]),
            MigrationUserAction.Clear);

        Assert.Equal(["d@x.com", "e@x.com"], recomputed.Eligible);
    }

    [Fact]
    public void ACandidateThatWasSkippedAtStagingCannotBePromotedByTheTimeOfConfirm()
    {
        // Review finding rc-1, and the reason the snapshot is the ELIGIBLE list rather than the
        // candidate list. The candidates deliberately include in-scope mailboxes that are not
        // completed, so the preview can name each one as skipped on its own row - but the mailbox
        // pane's Refresh is gated on IsBusy only, so rows can reload while the ticket field is
        // open. Re-planning from the candidates would then act on a mailbox that finished syncing
        // in that window, and "Remove completed (2)" would remove three.
        var atStaging = Loaded(
            ("a@x.com", "Completed"), ("b@x.com", "Completed"), ("c@x.com", "Syncing"));

        var candidates = MigrationUserActionPlanner.NarrowToSelection(atStaging, []);
        var staged = MigrationUserActionPlanner.Plan(atStaging, candidates, MigrationUserAction.Clear);

        Assert.Equal(["a@x.com", "b@x.com"], staged.Eligible);
        Assert.Equal(["c@x.com"], staged.Skipped.Select(s => s.EmailAddress));

        // The pane is refreshed while the operator types the ticket and c has finished syncing.
        var atConfirm = Loaded(
            ("a@x.com", "Completed"), ("b@x.com", "Completed"), ("c@x.com", "Completed"));

        // What the page does: re-plan from the ELIGIBLE snapshot.
        var executed = MigrationUserActionPlanner.Plan(
            atConfirm, staged.Eligible, MigrationUserAction.Clear);

        Assert.Equal(["a@x.com", "b@x.com"], executed.Eligible);
        Assert.DoesNotContain("c@x.com", executed.Eligible);

        // Not vacuous: re-planning from the CANDIDATES - which is what this change shipped before
        // the finding - removes three against a label that said two.
        var fromCandidates = MigrationUserActionPlanner.Plan(
            atConfirm, candidates, MigrationUserAction.Clear);

        Assert.Equal(3, fromCandidates.Eligible.Count);
        Assert.True(fromCandidates.Eligible.Count > staged.Eligible.Count);
    }

    [Fact]
    public void AStagedScopeStillShrinksWhenMailboxesChangeStatusOrLeaveTheBatch()
    {
        // The other half of the snapshot, and the reason it is a snapshot of ADDRESSES rather
        // than of the plan: everything that should still be noticed between staging and Confirm
        // is, because the re-plan runs against the live rows.
        string[] scope = ["a@x.com", "b@x.com", "c@x.com"];

        var atStaging = Loaded(
            ("a@x.com", "Completed"), ("b@x.com", "Completed"), ("c@x.com", "Completed"));
        Assert.Equal(3,
            MigrationUserActionPlanner.Plan(atStaging, scope, MigrationUserAction.Clear).Eligible.Count);

        // By Confirm b has gone back to Syncing and c has left the batch entirely.
        var atConfirm = Loaded(("a@x.com", "Completed"), ("b@x.com", "Syncing"));
        var executed = MigrationUserActionPlanner.Plan(atConfirm, scope, MigrationUserAction.Clear);

        Assert.Equal(["a@x.com"], executed.Eligible);

        // b is NAMED as skipped; c is dropped silently, because naming a row the operator can no
        // longer see is worse than saying nothing about it.
        Assert.Equal(["b@x.com"], executed.Skipped.Select(s => s.EmailAddress));
    }
}
