using ExchangeAdminWeb.Services.Progress;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Behaviour of the global progress channel (docs/GlobalProgressSystem-Plan.md section 5.1).
///
/// These are service-level tests on purpose. This repo has no bUnit harness, so nothing can
/// render the display component; everything that must be provably correct therefore lives in
/// the service, and the component is kept to markup over these values.
/// </summary>
public class ActivityProgressTests
{
    [Fact]
    public void BeginMakesTheActivityVisibleAndRaisesChanged()
    {
        using var progress = new ActivityProgressService();
        var raised = 0;
        progress.Changed += () => raised++;

        using var handle = progress.Begin("Migration", "Loading batches", ActivitySize.Unknown);

        Assert.True(progress.HasActive);
        var activity = Assert.Single(progress.Active);
        Assert.Equal("Migration", activity.ModuleId);
        Assert.Equal("Loading batches", activity.Label);
        Assert.Equal(handle.Id, activity.Id);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void AnIndeterminateActivityHasNoPercentage()
    {
        using var progress = new ActivityProgressService();
        using var handle = progress.Begin("TrueLastLogon", "Querying domain controllers", ActivitySize.Unknown);

        handle.Report(50);

        var activity = Assert.Single(progress.Active);
        Assert.False(activity.IsDeterminate);

        // The structural guarantee, not merely the current behaviour: an indeterminate activity
        // cannot yield a number for a caller to render, so a fake bar has to be written by hand
        // rather than falling out of the API.
        Assert.Null(activity.PercentComplete);
    }

    [Fact]
    public void ReportIsIgnoredByAnIndeterminateActivityButTheDetailStillLands()
    {
        using var progress = new ActivityProgressService();
        using var handle = progress.Begin("ServiceHealth", "Reading service health", ActivitySize.Unknown);

        handle.Report(7, "contacting Graph");

        var activity = Assert.Single(progress.Active);
        Assert.Equal(0, activity.Done);
        Assert.Equal("contacting Graph", activity.Detail);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 50)]
    [InlineData(10, 100)]
    public void AnItemsActivityReportsATruePercentage(int done, int expected)
    {
        using var progress = new ActivityProgressService();
        using var handle = progress.Begin("Comms10k", "Writing members", ActivitySize.Items(10));

        handle.Report(done);

        Assert.Equal(expected, Assert.Single(progress.Active).PercentComplete);
    }

    [Fact]
    public void ReportNeverExceedsTheDeclaredTotal()
    {
        using var progress = new ActivityProgressService();
        using var handle = progress.Begin("Comms10k", "Writing members", ActivitySize.Items(100));

        handle.Report(10_000);

        var activity = Assert.Single(progress.Active);
        Assert.Equal(100, activity.Done);
        Assert.Equal(100, activity.PercentComplete);
    }

    [Fact]
    public void ReportNeverGoesNegative()
    {
        using var progress = new ActivityProgressService();
        using var handle = progress.Begin("Comms10k", "Writing members", ActivitySize.Items(100));

        handle.Report(-5);

        Assert.Equal(0, Assert.Single(progress.Active).Done);
    }

    [Fact]
    public void StepAdvancesOneStageAtATime()
    {
        using var progress = new ActivityProgressService();
        using var handle = progress.Begin("Comms10k", "Replacing membership", ActivitySize.Steps(4));

        handle.Step("resolving addresses");
        handle.Step("clearing the list");

        var activity = Assert.Single(progress.Active);
        Assert.Equal(ActivityShape.Steps, activity.Shape);
        Assert.Equal(2, activity.Done);
        Assert.Equal(4, activity.Total);
        Assert.Equal("clearing the list", activity.Detail);
    }

    [Fact]
    public void ASizeWithoutARealTotalDegradesToIndeterminateRatherThanRenderingStepOneOfZero()
    {
        Assert.Equal(ActivityShape.Indeterminate, ActivitySize.Steps(0).Shape);
        Assert.Equal(ActivityShape.Indeterminate, ActivitySize.Items(-3).Shape);
    }

    [Fact]
    public void CompleteRemovesTheActivityAndRetainsNothing()
    {
        using var progress = new ActivityProgressService();
        var handle = progress.Begin("GroupManagement", "Adding members", ActivitySize.Items(3));

        handle.Complete(true, "3 added");

        // THE FRAME IS LIVE ONLY (owner ruling 2026-10-02). An ended activity leaves the channel
        // and is not held anywhere, so there is nothing left to go stale on screen.
        Assert.False(progress.HasActive);
        Assert.Empty(progress.Active);
    }

    [Fact]
    public void DisposeWithoutCompleteStillEndsTheActivity()
    {
        using var progress = new ActivityProgressService();

        // Mirrors OperationTraceService.OperationScope: falling out of scope must still close
        // the activity, or the frame shows work that is no longer running.
        using (progress.Begin("MailboxPermissions", "Granting access", ActivitySize.Unknown))
        {
        }

        Assert.Empty(progress.Active);
        Assert.False(progress.HasActive);
    }

    [Fact]
    public void CompletingTwiceEndsTheActivityOnce()
    {
        using var progress = new ActivityProgressService();
        var handle = progress.Begin("Migration", "Loading", ActivitySize.Unknown);
        var raised = 0;
        progress.Changed += () => raised++;

        handle.Complete(true);
        handle.Complete(false, "second call");

        // First call wins, so a `using` around an explicit Complete does not re-fire on dispose.
        Assert.Equal(1, raised);
        Assert.Empty(progress.Active);
    }

    [Fact]
    public void ReportAfterCompleteIsIgnored()
    {
        using var progress = new ActivityProgressService();
        var handle = progress.Begin("Migration", "Loading", ActivitySize.Items(10));

        handle.Complete(true);
        handle.Report(5);

        Assert.Empty(progress.Active);
    }

    [Fact]
    public void CancelSetsTheTokenTheModuleMustHonour()
    {
        using var progress = new ActivityProgressService();
        using var handle = progress.Begin("Migration", "Loading", ActivitySize.Unknown);

        Assert.False(handle.CancellationToken.IsCancellationRequested);

        progress.Cancel(handle.Id);

        Assert.True(handle.CancellationToken.IsCancellationRequested);
        Assert.True(Assert.Single(progress.Active).CancellationRequested);
    }

    [Fact]
    public void CancelAllCancelsEveryActivityInFlight()
    {
        using var progress = new ActivityProgressService();
        using var first = progress.Begin("Migration", "Loading", ActivitySize.Unknown);
        using var second = progress.Begin("GroupManagement", "Adding", ActivitySize.Items(2));

        progress.CancelAll();

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.True(second.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CancellingAnUnknownIdIsHarmless()
    {
        using var progress = new ActivityProgressService();
        using var handle = progress.Begin("Migration", "Loading", ActivitySize.Unknown);

        progress.Cancel("not-a-real-id");

        Assert.False(handle.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void ConcurrentActivitiesAreListedIndependentlyAndOldestFirst()
    {
        using var progress = new ActivityProgressService();
        using var first = progress.Begin("Migration", "Loading batches", ActivitySize.Unknown);
        using var second = progress.Begin("ServiceHealth", "Reading health", ActivitySize.Items(4));

        second.Report(2);

        Assert.Equal(2, progress.Active.Count);
        Assert.Equal(first.Id, progress.Active[0].Id);
        Assert.Equal(second.Id, progress.Active[1].Id);
        Assert.Null(progress.Active[0].PercentComplete);
        Assert.Equal(50, progress.Active[1].PercentComplete);
    }

    [Fact]
    public void TheProgressBridgeReportsThroughToTheActivity()
    {
        using var progress = new ActivityProgressService();
        using var handle = progress.Begin("Comms10k", "Writing members", ActivitySize.Items(200));

        // This is the shape a page passes into a singleton service, since the service may not
        // take IActivityProgress itself. Progress<T> marshals, so give it a moment to land.
        IProgress<ActivityUpdate> sink = handle.AsProgress();
        sink.Report(new ActivityUpdate(50, "batch 1"));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (progress.Active[0].Done == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);

        var activity = Assert.Single(progress.Active);
        Assert.Equal(50, activity.Done);
        Assert.Equal("batch 1", activity.Detail);
    }

    [Fact]
    public void NothingIsRetainedAfterActivitiesEnd()
    {
        using var progress = new ActivityProgressService();

        for (var i = 0; i < 12; i++)
            progress.Begin("Migration", $"Job {i}", ActivitySize.Unknown).Complete(true);

        // The frame said "Resolving ... finished" for half a minute while the real work was
        // still running, because the service kept the last five outcomes and the frame rendered
        // the newest until it was dismissed by hand. Owner ruling 2026-10-02 removed retention
        // outright: the bar shows what is running and says Idle when nothing is.
        Assert.Empty(progress.Active);
        Assert.False(progress.HasActive);
    }

    [Fact]
    public void TheProgressChannelExposesNoWayToRetainAFinishedActivity()
    {
        // A TRIPWIRE, not a unit test. Retention was never asked for - it came from a paragraph
        // in docs/GlobalProgressSystem-Plan.md, and a plan body does not carry owner authority
        // (`.agents/decisions.md` 2026-10-01). It is the kind of thing a future agent re-adds as
        // a kindness, so the interface's shape is asserted rather than trusted.
        var members = typeof(IActivityProgress)
            .GetMembers()
            .Select(m => m.Name)
            .Where(n => n.Contains("Outcome", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Dismiss", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Recent", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(members.Count == 0,
            "IActivityProgress is live only and must not retain finished work. Found: "
            + string.Join(", ", members));
    }

    [Fact]
    public void DisposingTheCircuitCancelsStillRunningWorkRatherThanLeavingItHeadless()
    {
        var progress = new ActivityProgressService();
        var handle = progress.Begin("Migration", "Loading batches", ActivitySize.Unknown);
        var token = handle.CancellationToken;

        progress.Dispose();

        // Completing without cancelling closes the activity while the work carries on with
        // nowhere to report: the backend keeps running and any result it produces is discarded
        // because Complete already won. That is the orphaned-work defect the whole system
        // exists to remove.
        Assert.True(token.IsCancellationRequested,
            "Disposing the circuit must cancel the token it asked modules to honour, not just "
            + "close the activity.");
        Assert.Empty(progress.Active);
        Assert.False(progress.HasActive);
    }

    [Fact]
    public void ASubscriberThatThrowsDoesNotFailTheReportingModule()
    {
        using var progress = new ActivityProgressService();
        progress.Changed += () => throw new InvalidOperationException("broken display");

        // A broken progress display must never take down the operation it was only watching.
        var handle = progress.Begin("Migration", "Loading", ActivitySize.Items(2));
        handle.Report(1);
        handle.Complete(true);

        Assert.Empty(progress.Active);
    }

    [Fact]
    public void ABlankModuleOrLabelStillRendersSomethingMeaningful()
    {
        using var progress = new ActivityProgressService();
        using var handle = progress.Begin("  ", "   ", ActivitySize.Unknown);

        var activity = Assert.Single(progress.Active);
        Assert.False(string.IsNullOrWhiteSpace(activity.ModuleId));
        Assert.False(string.IsNullOrWhiteSpace(activity.Label));
    }
}
