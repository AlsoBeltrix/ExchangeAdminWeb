using System.Globalization;
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
    public void ExceedsNinetyDays_ReturnsError()
    {
        var result = MessageTrace.ValidateDateRange(
            DateTime.Today.AddDays(-90), DateTime.Today);
        Assert.Contains("90 days", result);
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
