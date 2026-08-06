using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The date rules for cloud message trace (docs/MessageTraceAccuracy-Plan.md).
/// </summary>
/// <remarks>
/// These pin behaviour measured against the live tenant 2026-08-06, not documentation. Commit
/// 72b8047 shipped the opposite belief - that one call serves 90 days - passed 1386 unit tests, and
/// broke every search wider than 10 days, because no test could see the service's rules. These are
/// the tests that would have caught it.
///
/// A fixed "today" is used throughout so the boundaries are exact rather than clock-dependent.
/// </remarks>
public class MessageTraceWindowPlannerTests
{
    private static readonly DateTime Today = new(2026, 8, 6);

    // ---- Retention: the data either exists or it does not ------------------------------------

    [Fact]
    public void NinetyDaysBackIsTheOldestAcceptedStart()
    {
        // Measured: a window starting exactly 90 days back returns rows.
        Assert.Null(MessageTraceWindowPlanner.ValidateRange(Today.AddDays(-90), Today, Today));
    }

    [Fact]
    public void NinetyOneDaysBackIsRefused()
    {
        var error = MessageTraceWindowPlanner.ValidateRange(Today.AddDays(-91), Today, Today);

        // Named as retention, not width: the fix is to move the start date forward, and a
        // "range too wide" message would send the operator to shrink a window that can never work.
        Assert.Contains("90 days of message trace data", error);
    }

    [Fact]
    public void AnOldNarrowWindowIsRefusedForBeingOld()
    {
        // The gap the age check closes. A ONE-DAY search 180 days ago passed the old width-only
        // rule and failed at Exchange with a raw cmdlet error.
        var error = MessageTraceWindowPlanner.ValidateRange(Today.AddDays(-180), Today.AddDays(-179), Today);

        Assert.Contains("90 days of message trace data", error);
    }

    [Theory]
    [InlineData(1, 0)]    // end before start
    [InlineData(-5, 5)]   // end in the future
    public void ObviouslyInvalidRangesAreRefused(int startOffset, int endOffset)
    {
        Assert.NotNull(MessageTraceWindowPlanner.ValidateRange(
            Today.AddDays(startOffset), Today.AddDays(endOffset), Today));
    }

    [Fact]
    public void AFutureStartIsRefused()
    {
        var error = MessageTraceWindowPlanner.ValidateRange(Today.AddDays(1), Today.AddDays(2), Today);
        Assert.Contains("future", error);
    }

    // ---- A range wider than one call is legal; the planner splits it -------------------------

    [Fact]
    public void AWideRangeIsNoLongerRefused()
    {
        // The whole point. This is what 72b8047 got right in intent and wrong in mechanism: a
        // 90-day search IS legal, it just cannot be a single call.
        Assert.Null(MessageTraceWindowPlanner.ValidateRange(Today.AddDays(-90), Today, Today));
    }

    [Fact]
    public void ARangeInsideOneWindowIsNotSplit()
    {
        var windows = MessageTraceWindowPlanner.Split(Today.AddDays(-3), Today);

        Assert.Single(windows);
        Assert.Equal(Today.AddDays(-3), windows[0].Start);
        Assert.Equal(Today, windows[0].End);
    }

    [Fact]
    public void NoWindowExceedsTheServiceLimit()
    {
        // 11 days is a measured 400 from the service, so every emitted window must be <= 10.
        var windows = MessageTraceWindowPlanner.Split(Today.AddDays(-90), Today);

        Assert.All(windows, w =>
            Assert.True((w.End - w.Start).TotalDays <= MessageTraceWindowPlanner.MaxWindowDays,
                $"window {w.Start:d}..{w.End:d} exceeds the {MessageTraceWindowPlanner.MaxWindowDays}-day limit"));
    }

    [Fact]
    public void AFullNinetyDaySearchBecomesNineWindows()
    {
        var windows = MessageTraceWindowPlanner.Split(Today.AddDays(-90), Today);

        Assert.Equal(9, windows.Count);
    }

    [Fact]
    public void TheWindowsCoverTheWholeRangeWithNoGap()
    {
        // A gap silently drops every message inside it, and nothing in the result would show that.
        // Contiguity is the property that matters most here.
        var start = Today.AddDays(-90);
        var windows = MessageTraceWindowPlanner.Split(start, Today);

        Assert.Equal(Today, windows.First().End);
        Assert.Equal(start, windows.Last().Start);

        for (var i = 0; i < windows.Count - 1; i++)
        {
            // Emitted newest-first, so each window begins exactly where the next one ends.
            Assert.Equal(windows[i].Start, windows[i + 1].End);
        }
    }

    [Fact]
    public void TheWindowsShareEndpointsRatherThanOverlapping()
    {
        // Measured: adjacent windows sharing an endpoint returned 433 rows and 433 distinct
        // MessageTraceIds - zero duplication. So sharing is correct, and "fixing" it by nudging a
        // boundary would open a gap instead.
        var windows = MessageTraceWindowPlanner.Split(Today.AddDays(-30), Today);

        for (var i = 0; i < windows.Count - 1; i++)
            Assert.True(windows[i].Start == windows[i + 1].End);
    }

    [Fact]
    public void WindowsAreEmittedNewestFirst()
    {
        // The caller applies a result cap. Oldest-first would let the cap be consumed by the far
        // end of the range and drop the recent messages an operator usually wants.
        var windows = MessageTraceWindowPlanner.Split(Today.AddDays(-45), Today);

        for (var i = 0; i < windows.Count - 1; i++)
            Assert.True(windows[i].End > windows[i + 1].End);
    }

    [Fact]
    public void ARangeThatIsNotAWholeNumberOfWindowsKeepsItsRemainder()
    {
        // 25 days = two full windows plus a 5-day remainder. The remainder must be a real window,
        // not rounded away.
        var start = Today.AddDays(-25);
        var windows = MessageTraceWindowPlanner.Split(start, Today);

        Assert.Equal(3, windows.Count);
        Assert.Equal(start, windows.Last().Start);
        Assert.Equal(5, (windows.Last().End - windows.Last().Start).TotalDays);
    }

    [Fact]
    public void AZeroLengthRangeStillProducesOneWindow()
    {
        // Degenerate, but it must not return an empty plan - that would run no query at all and
        // render as "no messages found".
        var windows = MessageTraceWindowPlanner.Split(Today, Today);

        Assert.Single(windows);
    }
}
