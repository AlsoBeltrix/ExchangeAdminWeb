using System.Globalization;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

public class MessageTraceDateValidationTests
{
    [Fact]
    public void ValidRange_ReturnsNull()
    {
        var result = MessageTrace.ValidateDateRange(
            DateTime.Today.AddDays(-3), DateTime.Today);
        Assert.Null(result);
    }

    [Fact]
    public void EndBeforeStart_ReturnsError()
    {
        var result = MessageTrace.ValidateDateRange(
            DateTime.Today, DateTime.Today.AddDays(-1));
        Assert.Contains("after start", result);
    }

    [Fact]
    public void ExactlyNinetyDays_IsValid()
    {
        // Changed deliberately. This test previously asserted that a 90-day range was REFUSED,
        // which was true only because the app could not serve one - a single Get-MessageTraceV2
        // call caps at a 10-day window. Chunking removed that limitation, and 90 days is precisely
        // Exchange Online's retention, so the full window is now the supported maximum rather than
        // an error. The boundary itself is proven in MessageTraceWindowPlannerTests: 90 accepted,
        // 91 refused, both measured against the live service.
        Assert.Null(MessageTrace.ValidateDateRange(
            DateTime.Today.AddDays(-90), DateTime.Today));
    }

    [Fact]
    public void BeyondRetention_ReturnsError()
    {
        var result = MessageTrace.ValidateDateRange(
            DateTime.Today.AddDays(-91), DateTime.Today);

        Assert.Contains("90 days of message trace data", result);
    }

    [Fact]
    public void FutureStartDate_ReturnsError()
    {
        var result = MessageTrace.ValidateDateRange(
            DateTime.Today.AddDays(1), DateTime.Today.AddDays(2));
        Assert.Contains("future", result);
    }

    [Fact]
    public void ThirtyDays_IsValid()
    {
        var result = MessageTrace.ValidateDateRange(
            DateTime.Today.AddDays(-30), DateTime.Today);
        Assert.Null(result);
    }

    [Fact]
    public void EightyNineDays_IsValid()
    {
        var result = MessageTrace.ValidateDateRange(
            DateTime.Today.AddDays(-89), DateTime.Today);
        Assert.Null(result);
    }
}

public class OofScheduleValidationTests
{
    [Fact]
    public void NonScheduled_ReturnsNull()
    {
        var result = OutOfOffice.ValidateOofSchedule("Enabled", null, null, null);
        Assert.Null(result);
    }

    [Fact]
    public void Disabled_ReturnsNull()
    {
        var result = OutOfOffice.ValidateOofSchedule("Disabled", null, null, null);
        Assert.Null(result);
    }

    [Fact]
    public void Scheduled_MissingTimes_ReturnsError()
    {
        var result = OutOfOffice.ValidateOofSchedule("Scheduled", null, null, TimeZoneInfo.Utc);
        Assert.Contains("required", result);
    }

    [Fact]
    public void Scheduled_EndBeforeStart_ReturnsError()
    {
        var start = DateTime.Now.AddDays(1);
        var end = DateTime.Now;
        var result = OutOfOffice.ValidateOofSchedule("Scheduled", start, end, TimeZoneInfo.Utc);
        Assert.Contains("after start", result);
    }

    [Fact]
    public void Scheduled_ValidRange_ReturnsNull()
    {
        var start = DateTime.Now;
        var end = DateTime.Now.AddDays(7);
        var result = OutOfOffice.ValidateOofSchedule("Scheduled", start, end, TimeZoneInfo.Utc);
        Assert.Null(result);
    }

    // aud-a7e. A schedule is wall-clock time in a zone; without the zone, or at a time the zone skips,
    // it names no moment, and the page refuses it before anything is audited or written.
    [Fact]
    public void Scheduled_WithNoTimeZone_ReturnsError()
    {
        var result = OutOfOffice.ValidateOofSchedule("Scheduled", new DateTime(2026, 11, 2, 9, 0, 0),
            new DateTime(2026, 11, 6, 17, 0, 0), null);
        Assert.Contains("time zone", result);
    }

    [Fact]
    public void Scheduled_AtATimeTheZoneSkips_ReturnsError()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var result = OutOfOffice.ValidateOofSchedule("Scheduled", new DateTime(2026, 3, 8, 2, 30, 0),
            new DateTime(2026, 3, 9, 9, 0, 0), newYork);
        Assert.Contains("does not exist in America/New_York", result);
    }

    // aud-a7g. A time the clocks repeat when they go back (New York, 1 November 2026, 01:00 to 01:59)
    // names two instants. It is refused - start or end - naming the time and the zone, with a reason of
    // its own rather than the skipped-time one, and no instant is chosen for it.
    [Theory]
    [InlineData("2026-11-01T01:30:00", "2026-11-02T09:00:00", "2026-11-01T01:30:00")]
    [InlineData("2026-10-31T09:00:00", "2026-11-01T01:00:00", "2026-11-01T01:00:00")]
    public void Scheduled_AtATimeTheZoneRepeats_ReturnsError(string start, string end, string repeated)
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var time = DateTime.Parse(repeated, CultureInfo.InvariantCulture);

        var result = OutOfOffice.ValidateOofSchedule("Scheduled", DateTime.Parse(start, CultureInfo.InvariantCulture),
            DateTime.Parse(end, CultureInfo.InvariantCulture), newYork);

        Assert.NotNull(result);
        Assert.Contains(time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), result, StringComparison.Ordinal);
        Assert.Contains("America/New_York", result, StringComparison.Ordinal);
        var reason = OutOfOfficeRequest.NoInstantReason(time, newYork);
        Assert.NotNull(reason);
        Assert.Contains(reason, result, StringComparison.Ordinal);
        Assert.NotEqual(OutOfOfficeRequest.NoInstantReason(new DateTime(2026, 3, 8, 2, 30, 0), newYork), reason);
        Assert.Null(OutOfOfficeRequest.ToUtc(time, newYork));
    }

    [Fact]
    public void Scheduled_OnTheDayTheClocksGoBack_OutsideTheRepeatedHour_ReturnsNull()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var result = OutOfOffice.ValidateOofSchedule("Scheduled", new DateTime(2026, 11, 1, 0, 30, 0),
            new DateTime(2026, 11, 1, 2, 30, 0), newYork);
        Assert.Null(result);
    }

    // aud-a7h. The repeated-time reason said the clocks "repeat that hour", but the check is
    // IsAmbiguousTime, which holds for whatever amount they go back. This zone goes back thirty minutes,
    // from 02:00 to 01:30 on 1 November (Lord Howe Island's clocks go back by that much), and is built
    // here so the test does not depend on this host's time-zone data. 01:30 to 01:59 repeat; 01:15 and
    // 02:00 are each one instant.
    private static TimeZoneInfo ThirtyMinutesBack()
    {
        var forward = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 8);
        var back = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date,
            TimeSpan.FromMinutes(30), forward, back);
        return TimeZoneInfo.CreateCustomTimeZone("Test/Back_Thirty_Minutes", TimeSpan.Zero, "Back thirty minutes",
            "Standard", "Daylight", [rule]);
    }

    [Theory]
    [InlineData("2026-11-01T01:30:00")]
    [InlineData("2026-11-01T01:59:00")]
    public void Scheduled_AtATimeAZoneRepeatsForThirtyMinutes_ReturnsError_InWordsThatNameNoHour(string repeated)
    {
        var zone = ThirtyMinutesBack();
        var time = DateTime.Parse(repeated, CultureInfo.InvariantCulture);
        Assert.True(zone.IsAmbiguousTime(time), "precondition: the zone repeats this time");

        var result = OutOfOffice.ValidateOofSchedule("Scheduled", time, time.AddDays(1), zone);

        Assert.NotNull(result);
        Assert.Contains(time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), result, StringComparison.Ordinal);
        Assert.Contains("Test/Back_Thirty_Minutes", result, StringComparison.Ordinal);
        var reason = OutOfOfficeRequest.NoInstantReason(time, zone);
        Assert.NotNull(reason);
        Assert.Contains(reason, result, StringComparison.Ordinal);
        Assert.DoesNotContain("hour", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(OutOfOfficeRequest.ToUtc(time, zone));
    }

    [Theory]
    [InlineData("2026-11-01T01:15:00")]
    [InlineData("2026-11-01T02:00:00")]
    public void Scheduled_OnTheDayAZoneGoesBackThirtyMinutes_OutsideTheRepeat_ReturnsNull(string value)
    {
        var zone = ThirtyMinutesBack();
        var time = DateTime.Parse(value, CultureInfo.InvariantCulture);

        Assert.Null(OutOfOffice.ValidateOofSchedule("Scheduled", time, time.AddDays(1), zone));
        Assert.NotNull(OutOfOfficeRequest.ToUtc(time, zone));
    }
}

public class ParseSizeToGBTests
{
    [Theory]
    [InlineData("1,073,741,824 bytes", 1.0)]
    [InlineData("536,870,912 bytes", 0.5)]
    [InlineData("0 bytes", 0.0)]
    [InlineData("1.234 GB (1,325,039,752 bytes)", 1.23)]
    [InlineData("500.1 MB (524,396,544 bytes)", 0.49)]
    [InlineData("2.5 GB", 2.5)]
    [InlineData("512 MB", 0.5)]
    [InlineData("256 KB", 0.0002)]
    public void ParsesValidSizeStrings(string input, double expected)
    {
        var result = ExchangeServiceBase.ParseSizeToGB(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value, 2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void ReturnsNullForInvalidInput(string? input)
    {
        Assert.Null(ExchangeServiceBase.ParseSizeToGB(input));
    }

    [Theory]
    [InlineData("2.5 GB", 2.5)]
    [InlineData("500.1 MB", 0.49)]
    [InlineData("1.234 GB (1,325,039,752 bytes)", 1.23)]
    public void ParsesCorrectlyUnderNonUsCulture(string input, double expected)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-BR");
            var result = ExchangeServiceBase.ParseSizeToGB(input);
            Assert.NotNull(result);
            Assert.Equal(expected, result!.Value, 2);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
