using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

public class TestAccountPoolServiceTests
{
    [Theory]
    [InlineData(false, null, "Available")]
    [InlineData(true, null, "CheckedOut")]
    public void DetermineStatus_UsesEnabledState(bool enabled, int? minutesFromNow, string expected)
    {
        var now = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);
        DateTime? expires = minutesFromNow.HasValue ? now.AddMinutes(minutesFromNow.Value) : null;

        var result = TestAccountPoolService.DetermineStatus(enabled, expires, now);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void DetermineStatus_Expired_WhenEnabledAndExpiryPassedOrNow(int minutesFromNow)
    {
        var now = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);

        var result = TestAccountPoolService.DetermineStatus(true, now.AddMinutes(minutesFromNow), now);

        Assert.Equal("Expired", result);
    }

    [Fact]
    public void GeneratePassword_ReturnsRequestedLength()
    {
        var password = TestAccountPoolService.GeneratePassword(28);

        Assert.Equal(28, password.Length);
    }

    [Fact]
    public void GeneratePassword_ProducesDifferentValues()
    {
        var first = TestAccountPoolService.GeneratePassword(28);
        var second = TestAccountPoolService.GeneratePassword(28);

        Assert.NotEqual(first, second);
    }
}
