using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// S8 of docs/MigrationInterfaceRedesign-Plan.md: "complete now" and "complete at T" stop being
/// the same value.
/// </summary>
/// <remarks>
/// The trap this work exists for. Four call sites pass a CompleteAfter that is already in the
/// PAST - AddHours(-1), AddDays(-1), UtcNow - because that is the Exchange idiom for "finalise at
/// the first opportunity". The batch reader then turned any non-null CompleteAfter into a
/// boolean. So a batch told to complete now and a batch scheduled for 22:00 were indistinguishable
/// to this app, and queue item 12 had nowhere to put a schedule.
/// </remarks>
public class MigrationScheduleTests
{
    private static MigrationBatchInfo Batch(DateTime? completeAfter) => new()
    {
        BatchName = "Wave1",
        Status = "Synced",
        CompleteAfter = completeAfter,
    };

    [Fact]
    public void APastCompleteAfterIsNotASchedule()
    {
        // The whole point. A past value means "complete now" - it is an instruction that has
        // already fired, not an appointment. Reporting it as a schedule would put a time in the
        // interface that nothing is waiting for.
        var batch = Batch(DateTime.UtcNow.AddHours(-1));

        Assert.Null(batch.ScheduledCompletionUtc);

        // But it IS auto-completing, and that distinction is the one the old boolean collapsed.
        Assert.True(batch.AutoComplete);
    }

    [Fact]
    public void AFutureCompleteAfterIsASchedule()
    {
        var when = DateTime.UtcNow.AddHours(6);
        var batch = Batch(when);

        Assert.Equal(when.ToUniversalTime(), batch.ScheduledCompletionUtc);
        Assert.True(batch.AutoComplete);
    }

    [Fact]
    public void NoCompleteAfterIsNeitherScheduledNorAutoCompleting()
    {
        var batch = Batch(null);

        Assert.Null(batch.ScheduledCompletionUtc);
        Assert.False(batch.AutoComplete);
    }

    [Fact]
    public void TheFlagAndTheTimeCannotDisagree()
    {
        // AutoComplete is DERIVED from CompleteAfter rather than stored beside it. Two fields set
        // independently is how a batch comes to claim it is auto-completing with no time, or to
        // hold a time while claiming it is not - and the reader used to set exactly one of them.
        foreach (var value in new DateTime?[]
        {
            null,
            DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(1),
            DateTime.UtcNow.AddDays(30),
        })
        {
            var batch = Batch(value);
            Assert.Equal(value != null, batch.AutoComplete);
        }
    }

    [Fact]
    public void ALocalTimeIsComparedInUtcRatherThanAsWritten()
    {
        // Exchange returns local times, and the "complete now" sites pass DateTime.Now as well as
        // DateTime.UtcNow. Comparing a local value against UtcNow without converting is wrong by
        // the size of the machine's offset.
        //
        // THE MARGIN HAS TO BE SMALLER THAN THAT OFFSET OR THE TEST PROVES NOTHING. The first
        // version used six hours, and a mutation probe showed it passing with the conversion
        // removed: on a host less than six hours from UTC the raw comparison lands on the same
        // side of now, so the bug is invisible. Derived from the running zone rather than
        // assuming one, because no test here may depend on this environment's shape.
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);

        Assert.SkipWhen(offset == TimeSpan.Zero,
            "This host runs at UTC, so a local and a UTC comparison cannot differ and nothing "
            + "here could distinguish them. Not a pass - the case is unreachable on this machine.");

        // Half the offset: far enough from now to be unambiguous, close enough that dropping the
        // conversion moves it across now.
        var margin = TimeSpan.FromTicks(Math.Abs(offset.Ticks) / 2);

        var futureUtc = DateTime.UtcNow.Add(margin);
        var asLocal = DateTime.SpecifyKind(futureUtc, DateTimeKind.Utc).ToLocalTime();

        // Genuinely in the future, so it IS a schedule - whatever the clock on the wall says.
        Assert.NotNull(Batch(asLocal).ScheduledCompletionUtc);

        var pastUtc = DateTime.UtcNow.Subtract(margin);
        var pastAsLocal = DateTime.SpecifyKind(pastUtc, DateTimeKind.Utc).ToLocalTime();

        Assert.Null(Batch(pastAsLocal).ScheduledCompletionUtc);
    }

    [Fact]
    public void TheBoundaryFallsOnTheSideOfNotScheduled()
    {
        // A time that has just passed is not an appointment. Failing this way round means the
        // interface stops showing a countdown the instant it stops meaning anything, rather than
        // displaying a schedule for a moment that is gone.
        Assert.Null(Batch(DateTime.UtcNow).ScheduledCompletionUtc);
    }
}
