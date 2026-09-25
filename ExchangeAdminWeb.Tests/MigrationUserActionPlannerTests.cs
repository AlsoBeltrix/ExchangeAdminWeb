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
}
