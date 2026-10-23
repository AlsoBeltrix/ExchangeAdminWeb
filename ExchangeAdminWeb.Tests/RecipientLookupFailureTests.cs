using System.Management.Automation;
using System.Reflection;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using Page = ExchangeAdminWeb.Components.Pages.RecipientLookup;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a68, Recipient Lookup's own failure texts. (2) The mailbox statistics warning was built from
/// <c>ex.Message</c>, so it carried PowerShell's "The running command stopped because the preference variable ..." prefix
/// (the aud-a23l class); its catch took a cancellation too; and a statistics read that answered no row showed "N/A" for
/// every figure with nothing said. (3) The lookup's own error was <c>ex.Message</c> as well: a missing recipient read in
/// that prefix, and the lookup's "Recipient '&lt;x&gt;' not found." was reached only by an empty answer. (4) The page's
/// <c>RunLookup</c> caught a lookup that threw with no audit row - though the page says all lookups are logged - and left
/// its activity to end "This did not finish."; and its one catch also took a failed audit write as the lookup's error.
/// Finding kd-14: the location read's and the on-premises size read's catches took a cancellation and worded from
/// <c>ex.Message</c>, as (2) had fixed for the statistics read only.
/// </summary>
/// <remarks>
/// The real <see cref="RecipientLookupService"/> over <see cref="MigrationStubExchange"/>'s Exchange Online stand-ins
/// (Get-Recipient, Get-Mailbox, Get-MailboxStatistics), and for part 4 the page's own handler with its injected services
/// set by hand, a progress channel recording how the activity ended, and the audit file read back. No Exchange.
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

    private static Func<string, ErrorRecord> NotFoundIn(string shape) => shape switch
    {
        "category" => NotFoundByCategory,
        "type" => NotFoundByType,
        _ => NotFoundByIdentifier,
    };

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

    // ---- part 3: the lookup's own error ----------------------------------------------------------

    // The finding. An error Get-Recipient writes under Stop: before, "The running command stopped because the preference
    // variable ... is set to Stop: the directory is busy".
    [Fact]
    public async Task ARecipientReadThatFails_IsTheErrorInTheCmdletsOwnWords()
    {
        _exchange.Failures[$"Get-Recipient {Address}"] = Refused("the directory is busy");

        var result = await LookedUp();

        Assert.Equal("the directory is busy", result.Error);
    }

    // Exchange's own "no such object", in each shape it is read from: the lookup's own words, which before only an empty
    // answer reached, and nothing more is read.
    [Theory]
    [InlineData("category")]
    [InlineData("type")]
    [InlineData("identifier")]
    public async Task ARecipientThatDoesNotExist_IsNotFound(string shape)
    {
        _exchange.NotFound = NotFoundIn(shape);

        var result = await LookedUp();

        Assert.Equal($"Recipient '{Address}' not found.", result.Error);
        Assert.Equal([$"Get-Recipient {Address}"], _exchange.CallList());
    }

    // ---- part 4: the page's own lookup -----------------------------------------------------------

    private readonly RecordingProgress _progress = new();

    private Page LookupPage(RecipientLookupService service)
    {
        var page = new Page();
        Inject(page, "LookupService", service);
        Inject(page, "Audit", _exchange.Audit);
        Inject(page, "Progress", _progress);
        Inject(page, "Logger", NullLogger<Page>.Instance);
        PageField("emailAddress").SetValue(page, $" {Address} ");
        PageField("currentUser").SetValue(page, "CONTOSO\\operator");
        PageField("clientIpAddress").SetValue(page, "192.0.2.10");
        return page;
    }

    private static void Inject(Page page, string property, object? value) =>
        typeof(Page).GetProperty(property, Members)!.SetValue(page, value);

    private static FieldInfo PageField(string name) => typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static Task RunLookup(Page page) =>
        (Task)typeof(Page).GetMethod("RunLookup", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [])!;

    private static RecipientInfoResult Shown(Page page) => Assert.IsType<RecipientInfoResult>(PageField("result").GetValue(page));

    // The finding. The Exchange Online connection cannot be opened, so the lookup throws before any cmdlet runs. Before: no
    // audit row, though the page says all lookups are logged, and the activity ended "This did not finish.".
    [Fact]
    public async Task ALookupThatThrows_IsAuditedAsFailed_AndItsActivityEndsWithTheError()
    {
        var page = LookupPage(_exchange.RecipientLookup(connects: false));

        await RunLookup(page);

        const string failure = "the Exchange Online sign-in could not be completed";
        Assert.Equal(failure, Shown(page).Error);
        var row = Assert.Single(_exchange.AuditEvents());
        Assert.Equal("RecipientLookup", row.GetProperty("action").GetString());
        Assert.Equal("Lookup", row.GetProperty("category").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal(Address, row.GetProperty("target").GetString());
        Assert.Equal(failure, row.GetProperty("error").GetString());
        Assert.Equal((false, failure), Assert.Single(_progress.Handles).Outcome);
        Assert.False((bool)PageField("isLoading").GetValue(page)!);
    }

    // The third wording. A live Connect-ExchangeOnline that fails under -ErrorAction Stop reaches this catch as PowerShell's
    // ActionPreferenceStopException (the pool rethrows it as it is); before, the page showed and audited "The running command
    // stopped because the preference variable ... is set to Stop: " in front of the error.
    [Fact]
    public async Task ALookupThatThrowsUnderStop_IsShownInTheCmdletsOwnWords()
    {
        const string failure = "the certificate is not in the store";
        _exchange.ConnectFailure = StopFailure(failure);
        var page = LookupPage(_exchange.RecipientLookup(connects: false));

        await RunLookup(page);

        Assert.Equal(failure, Shown(page).Error);
        Assert.Equal(failure, Assert.Single(_exchange.AuditEvents()).GetProperty("error").GetString());
        Assert.Equal((false, failure), Assert.Single(_progress.Handles).Outcome);
    }

    // The controls: a lookup that answers - with the recipient, or with Exchange saying there is none - is audited and ends
    // its activity with that outcome, as before.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALookupThatAnswers_IsAuditedWithItsOutcome(bool found)
    {
        if (found)
            ACloudMailbox();
        var page = LookupPage(_exchange.RecipientLookup());

        await RunLookup(page);

        var error = found ? null : $"Recipient '{Address}' not found.";
        Assert.Equal(error, Shown(page).Error);
        var row = Assert.Single(_exchange.AuditEvents());
        Assert.Equal(found ? "Success" : "Failed", row.GetProperty("result").GetString());
        Assert.Equal((found, error), Assert.Single(_progress.Handles).Outcome);
    }

    // An audit write that throws is logged, and the lookup that was made keeps its result; before, the shared catch made the
    // audit's exception the lookup's error (the Constitution: an audit failure must not make a completed operation read as
    // failed). The audit service's methods are not virtual and its writer swallows its own failures (aud-a28q), so a
    // throwing audit is stood in for by none at all, as the Migration tests do.
    [Fact]
    public async Task AnAuditWriteThatThrows_LeavesTheLookupsResult()
    {
        ACloudMailbox();
        var page = LookupPage(_exchange.RecipientLookup());
        Inject(page, "Audit", null);

        await RunLookup(page);

        Assert.Null(Shown(page).Error);
        Assert.Equal((true, (string?)null), Assert.Single(_progress.Handles).Outcome);
    }

    // ---- kd-14: the location and on-premises size reads' catches -----------------------------------

    // A recipient Exchange Online holds as a mail user, its mailbox on-premises: the location read asks on-premises, and the
    // on-premises size read follows.
    private void AnOnPremMailbox()
    {
        _exchange.OnPremConfigured = true;
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "MailUser"));
        _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));
    }

    private static bool IsACancellation(Exception thrown)
    {
        for (Exception? cause = thrown; cause != null; cause = cause.InnerException)
        {
            if (cause is OperationCanceledException or PipelineStoppedException)
                return true;
        }
        return false;
    }

    // The finding. A cancellation is not a read that failed (aud-a23g's rule): it leaves the lookup, which the page shows as
    // the lookup's error (aud-a68 (4)). Before, either catch filed it as a warning beside a lookup that carried on.
    [Theory]
    [InlineData("the location read")]
    [InlineData("the on-premises size read")]
    public async Task ACancelledRead_LeavesTheLookup_AndIsNoWarning(string read)
    {
        AnOnPremMailbox();
        if (read == "the location read")
            _exchange.Failures["OnPrem Credentials on-prem mailbox location check"] = new OperationCanceledException("the location read was cancelled");
        else
            _exchange.Behaviour["OnPremStatisticsCancelled"] = true;

        var thrown = await Assert.ThrowsAnyAsync<Exception>(LookedUp);

        Assert.True(IsACancellation(thrown), thrown.ToString());
    }

    // What reaches either catch is in the error's own words: here the read's credential lookup fails with PowerShell's Stop
    // text, which before stood in front of the error in the warning.
    [Theory]
    [InlineData("on-prem mailbox location check", "Could not determine mailbox location: ")]
    [InlineData("on-prem mailbox size check", "Could not retrieve on-premises mailbox size: ")]
    public async Task AReadThatFailsUnderStop_IsAWarningInTheErrorsOwnWords(string purpose, string warning)
    {
        AnOnPremMailbox();
        const string failure = "the credential store refused the request";
        _exchange.Failures[$"OnPrem Credentials {purpose}"] = StopFailure(failure);

        var result = await LookedUp();

        Assert.Null(result.Error);
        Assert.Contains(warning + failure, result.Warnings);
        Assert.DoesNotContain(result.Warnings, shown => shown.Contains("The running command stopped", StringComparison.Ordinal));
    }
}
