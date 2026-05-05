using ExchangeAdminWeb.Components.Pages;
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
    public void ExceedsTenDays_ReturnsError()
    {
        var result = MessageTrace.ValidateDateRange(
            DateTime.Today.AddDays(-11), DateTime.Today);
        Assert.Contains("10 days", result);
    }

    [Fact]
    public void FutureStartDate_ReturnsError()
    {
        var result = MessageTrace.ValidateDateRange(
            DateTime.Today.AddDays(1), DateTime.Today.AddDays(2));
        Assert.Contains("future", result);
    }

    [Fact]
    public void ExactlyTenDays_IsValid()
    {
        var result = MessageTrace.ValidateDateRange(
            DateTime.Today.AddDays(-10), DateTime.Today);
        Assert.Null(result);
    }
}

public class OofScheduleValidationTests
{
    [Fact]
    public void NonScheduled_ReturnsNull()
    {
        var result = OutOfOffice.ValidateOofSchedule("Enabled", null, null);
        Assert.Null(result);
    }

    [Fact]
    public void Disabled_ReturnsNull()
    {
        var result = OutOfOffice.ValidateOofSchedule("Disabled", null, null);
        Assert.Null(result);
    }

    [Fact]
    public void Scheduled_MissingTimes_ReturnsError()
    {
        var result = OutOfOffice.ValidateOofSchedule("Scheduled", null, null);
        Assert.Contains("required", result);
    }

    [Fact]
    public void Scheduled_EndBeforeStart_ReturnsError()
    {
        var start = DateTime.Now.AddDays(1);
        var end = DateTime.Now;
        var result = OutOfOffice.ValidateOofSchedule("Scheduled", start, end);
        Assert.Contains("after start", result);
    }

    [Fact]
    public void Scheduled_ValidRange_ReturnsNull()
    {
        var start = DateTime.Now;
        var end = DateTime.Now.AddDays(7);
        var result = OutOfOffice.ValidateOofSchedule("Scheduled", start, end);
        Assert.Null(result);
    }
}

public class ParseSizeToGBTests
{
    [Theory]
    [InlineData("1,073,741,824 bytes", 1.0)]
    [InlineData("536,870,912 bytes", 0.5)]
    [InlineData("0 bytes", 0.0)]
    public void ParsesValidSizeStrings(string input, double expected)
    {
        var result = ExchangeService.ParseSizeToGB(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value, 2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void ReturnsNullForInvalidInput(string? input)
    {
        Assert.Null(ExchangeService.ParseSizeToGB(input));
    }
}
