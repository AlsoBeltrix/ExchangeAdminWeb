using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23i, parts 3 and 4: what <c>RunAsync</c> tells a caller when a write fails. Under
/// <c>-ErrorAction Stop</c> an error a cmdlet writes arrives as an <c>ActionPreferenceStopException</c>, and the message
/// every caller showed was its "The running command stopped because..." text rather than the cmdlet's own words; and a
/// call PowerShell flagged as having errors without writing one was "An unknown error occurred.".
/// </summary>
/// <remarks>
/// Driven through real callers on a real pool over stub cmdlets: one outside Migration
/// (<see cref="BlockedSenderService"/>) and one inside. No Exchange.
/// </remarks>
public sealed class ExchangeServiceBaseRunFailureTests : IDisposable
{
    private const string Sender = "spam@fabrikam.test";
    private const string StopPrefix = "The running command stopped";

    private readonly MigrationStubExchange _exchange = new();

    public void Dispose() => _exchange.Dispose();

    private BlockedSenderService BlockedSenders() =>
        new(_exchange.Pool(), null!, NullLogger<BlockedSenderService>.Instance, _exchange.Trace);

    [Fact]
    public async Task OutsideMigration_AnErrorTheCmdletWroteUnderStop_IsReportedInItsOwnWords()
    {
        _exchange.Failures["Remove-BlockedSenderAddress"] = Refused("the sender is not on the blocked-senders list");

        var result = await BlockedSenders().UnblockSenderAsync(Sender, "INC0001", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("the sender is not on the blocked-senders list", result.Message);
    }

    // A terminating error never carried the prefix; it is unchanged.
    [Fact]
    public async Task OutsideMigration_AThrownError_IsReportedAsBefore()
    {
        _exchange.Failures["Remove-BlockedSenderAddress"] = "the request was throttled";

        var result = await BlockedSenders().UnblockSenderAsync(Sender, "INC0001", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("the request was throttled", result.Message);
    }

    // Part 3: an error a nested command suppressed sets HadErrors and writes nothing. Still a failure, now said as it is.
    [Fact]
    public async Task OutsideMigration_AnErrorThatWasSuppressed_IsReportedAsSuch_NotAsUnknown()
    {
        _exchange.Behaviour["NestedSuppressedError"] = true;

        var result = await BlockedSenders().UnblockSenderAsync(Sender, "INC0001", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ExchangeServiceBase.ErrorNotSurfaced, result.Message);
        Assert.DoesNotContain("unknown error", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InsideMigration_AnErrorTheCmdletWroteUnderStop_IsReportedInItsOwnWords()
    {
        _exchange.Failures["Stop-MigrationUser"] = Refused("the migration user cannot be stopped while it is completing");

        var result = await _exchange.Service().StopMigrationUserAsync("alice@contoso.test");

        Assert.False(result.Success);
        Assert.Equal("the migration user cannot be stopped while it is completing", result.Message);
        Assert.DoesNotContain(StopPrefix, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACleanWrite_StillSucceeds()
    {
        var result = await BlockedSenders().UnblockSenderAsync(Sender, "INC0001", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Unblocked sender {Sender}.", result.Message);
    }
}
