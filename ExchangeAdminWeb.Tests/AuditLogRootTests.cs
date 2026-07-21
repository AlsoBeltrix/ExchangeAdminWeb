using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the required-log-root contract (docs/RemoveHardcodedLogRoot-Plan.md): there is no
/// baked-in default, so an unset/blank Audit:LogRoot must throw rather than fall through to any
/// path. This is the non-vacuity home for the fail-fast decision - the startup guard in
/// Program.cs shares AuditLogRoot.UnsetMessage and the same null/whitespace test.
/// </summary>
public class AuditLogRootTests
{
    private static IConfiguration Config(string? logRoot) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = logRoot,
        }).Build();

    [Fact]
    public void Require_ReturnsValue_WhenConfigured()
    {
        Assert.Equal(@"D:\logs\eaw", AuditLogRoot.Require(Config(@"D:\logs\eaw")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Require_Throws_WhenUnsetOrBlank(string? logRoot)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AuditLogRoot.Require(Config(logRoot)));
        Assert.Equal(AuditLogRoot.UnsetMessage, ex.Message);
    }
}
