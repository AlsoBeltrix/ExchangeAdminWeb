using ExchangeAdminWeb.Models;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a68, Recipient Lookup's own failure texts. (2) The mailbox statistics warning was built from
/// <c>ex.Message</c>, so it carried PowerShell's "The running command stopped because the preference variable ..." prefix
/// (the aud-a23l class); its catch took a cancellation too; and a statistics read that answered no row showed "N/A" for
/// every figure with nothing said.
/// </summary>
/// <remarks>
/// The real <see cref="ExchangeAdminWeb.Services.RecipientLookupService"/> over <see cref="MigrationStubExchange"/>'s
/// Exchange Online stand-ins (Get-Recipient, Get-Mailbox, Get-MailboxStatistics). No Exchange.
/// </remarks>
public sealed class RecipientLookupFailureTests : IDisposable
{
    private const string Address = "alice@contoso.test";
    private const string Refusal = "the mailbox database is offline";

    private readonly MigrationStubExchange _exchange = new();

    public void Dispose() => _exchange.Dispose();

    private void ACloudMailbox()
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "UserMailbox"));
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
    }

    private Task<RecipientInfoResult> LookedUp() => _exchange.RecipientLookup().GetRecipientInfoAsync(Address);

    // ---- part 2: the mailbox statistics read -----------------------------------------------------

    // The finding. An error the cmdlet writes under -ErrorAction Stop: before, "Could not retrieve mailbox statistics: The
    // running command stopped because the preference variable ... is set to Stop: the mailbox database is offline".
    [Fact]
    public async Task AStatisticsReadThatFails_IsAWarningInTheCmdletsOwnWords()
    {
        ACloudMailbox();
        _exchange.Failures[$"Get-MailboxStatistics {Address}"] = Refused(Refusal);

        var result = await LookedUp();

        Assert.Null(result.Error);
        Assert.Equal([$"Could not retrieve mailbox statistics: {Refusal}."], result.Warnings);
        Assert.Null(result.MailboxSizeGB);
    }

    // An answer with no row is not statistics: before, every figure read "N/A" and nothing said why.
    [Fact]
    public async Task AStatisticsReadWithNoRow_IsAWarning()
    {
        ACloudMailbox();
        _exchange.Behaviour["MailboxSize"] = null;

        var result = await LookedUp();

        Assert.Null(result.Error);
        Assert.Equal(["Could not retrieve mailbox statistics: Exchange returned no mailbox statistics."], result.Warnings);
        Assert.Null(result.MailboxSizeGB);
    }

    // A cancellation is not a read that failed (aud-a23g's rule): it reaches the lookup's own catch and fails the lookup,
    // and is not also filed as a statistics warning, as it was.
    [Fact]
    public async Task ACancelledStatisticsRead_IsNotAStatisticsWarning()
    {
        ACloudMailbox();
        _exchange.Behaviour["StatisticsCancelled"] = true;

        var result = await LookedUp();

        Assert.NotNull(result.Error);
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("Could not retrieve mailbox statistics", StringComparison.Ordinal));
    }

    // The control: statistics that read give their figures and no warning.
    [Fact]
    public async Task StatisticsThatRead_GiveTheirFigures_AndNoWarning()
    {
        ACloudMailbox();

        var result = await LookedUp();

        Assert.Null(result.Error);
        Assert.Equal(1.5, result.MailboxSizeGB);
        Assert.Empty(result.Warnings);
    }
}
