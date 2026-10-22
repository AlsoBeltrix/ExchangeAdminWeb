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
        // Through the one reader, as the service reads it (aud-a23d).
        CompleteAfter = ReportedTime.Read(completeAfter),
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
        Assert.False(batch.CompleteAfterZoneNotReported);
    }

    [Fact]
    public void NoCompleteAfterIsNeitherScheduledNorAutoCompleting()
    {
        var batch = Batch(null);

        Assert.Null(batch.ScheduledCompletionUtc);
        Assert.False(batch.AutoComplete);
        Assert.False(batch.CompleteAfterZoneNotReported);
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

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void ACompleteAfterThatNamesNoZone_IsNotASchedule_AndSaysSo(DateTimeKind kind)
    {
        // aud-a23g. This was ALocalTimeIsComparedInUtcRatherThanAsWritten, which took "Exchange returns local
        // times" as given and converted them through this server's zone. Nothing proves a reported Local clock
        // is this server's, and an Unspecified one names no zone at all, so either was a guess at the instant -
        // one that could call a batch scheduled for a moment nobody set. Neither is a schedule now, and the batch
        // says why; it is still set to finalise.
        //
        // A day ahead, further than any zone's offset: read as a time in any zone, it would be a schedule.
        var batch = Batch(DateTime.SpecifyKind(DateTime.UtcNow.AddDays(1), kind));

        Assert.Null(batch.ScheduledCompletionUtc);
        Assert.True(batch.CompleteAfterZoneNotReported);
        Assert.False(batch.CompleteAfterUnreadable);
        Assert.True(batch.AutoComplete);
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
