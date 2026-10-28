using ExchangeAdminWeb.Models;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using static ExchangeAdminWeb.Tests.RecipientLookupClassificationTests;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// RecipientLookupAccuracy, the extras the owner took on 2026-10-28 ("add everything free"): each shows what the core's reads
/// already return, and adds no read. E2, directory facts from Exchange Online's Get-Recipient row.
/// </summary>
/// <remarks>
/// The real <see cref="ExchangeAdminWeb.Services.RecipientLookupService"/> over <see cref="MigrationStubExchange"/>, and the
/// page's compiled render tree (<see cref="RecipientLookupClassificationTests.Rendered"/>). No Exchange.
/// </remarks>
public sealed class RecipientLookupExtrasTests : IDisposable
{
    private const string Address = "alice@contoso.test";

    private const string NotReported = "Not reported by Exchange";

    private readonly MigrationStubExchange _exchange = new() { OnPremConfigured = true };

    public void Dispose() => _exchange.Dispose();

    private Task<RecipientInfoResult> LookedUp(string typed = Address) =>
        _exchange.RecipientLookup().GetRecipientInfoAsync(typed, TestContext.Current.CancellationToken);

    private static readonly DateTime ChangedUtc = new(2026, 10, 20, 9, 30, 0, DateTimeKind.Utc);

    // ---- E2: directory facts ----------------------------------------------------------------------

    [Fact]
    public async Task DirectoryFacts_AreShownAsExchangeReportsThem()
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "MailUser"),
            ("PrimarySmtpAddress", Address), ("IsDirSynced", false), ("ExternalDirectoryObjectId", "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b"),
            ("Alias", "alice"), ("HiddenFromAddressListsEnabled", true), ("ExternalEmailAddress", "SMTP:alice@fabrikam.test"),
            ("WhenChanged", ChangedUtc));

        var rows = RowsOf(Rendered(await LookedUp()));

        Assert.Equal("No", rows["Synced from On-Premises"]);
        Assert.Equal("0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b", rows["Entra Object ID"]);
        Assert.Equal("alice", rows["Alias"]);
        Assert.Equal("Yes", rows["Hidden from Address Book"]);
        Assert.Equal("SMTP:alice@fabrikam.test", rows["External Address"]);
        Assert.Equal("2026-10-20 09:30 (UTC, UTC+00:00)", rows["Last Changed"]);
    }

    // A fact Exchange did not return is never a default: not "No", not blank.
    [Fact]
    public async Task DirectoryFactsExchangeDidNotReturn_AreNotReported()
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "GuestMailUser"));

        var rows = RowsOf(Rendered(await LookedUp()));

        foreach (var row in new[] { "Synced from On-Premises", "Entra Object ID", "Alias", "Hidden from Address Book", "External Address", "Last Changed" })
            Assert.Equal((row, NotReported), (row, rows[row]));
    }

    // Where its mail goes is a mail user's, contact's or guest's; every other kind has no external address.
    [Theory]
    [InlineData("MailUser", true)]
    [InlineData("MailContact", true)]
    [InlineData("MailForestContact", true)]
    [InlineData("GuestMailUser", true)]
    [InlineData("MailUniversalDistributionGroup", false)]
    [InlineData("GroupMailbox", false)]
    [InlineData("UserMailbox", false)]
    [InlineData("PublicFolder", false)]
    public async Task ExternalAddress_IsAMailUsersContactsOrGuests_AndNotApplicableOtherwise(string type, bool applies)
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", type), ("PrimarySmtpAddress", Address),
            ("IsDirSynced", false), ("ExternalEmailAddress", "SMTP:alice@fabrikam.test"));
        _exchange.Mailboxes[Address] = CloudMailbox(Address);

        var rows = RowsOf(Rendered(await LookedUp()));

        Assert.Equal(applies ? "SMTP:alice@fabrikam.test" : "Not applicable", rows["External Address"]);
    }

    // An on-premises mailbox's cloud object is a mail user: its external address is where Exchange Online sends its mail.
    [Fact]
    public async Task AnOnPremisesMailbox_ShowsWhereExchangeOnlineSendsItsMail()
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "MailUser"), ("PrimarySmtpAddress", Address),
            ("IsDirSynced", true), ("ExternalEmailAddress", "SMTP:alice@contoso.test"));
        _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));

        var result = await LookedUp();

        Assert.Equal("On-Premises", result.MailboxLocation);
        var rows = RowsOf(Rendered(result));
        Assert.Equal("SMTP:alice@contoso.test", rows["External Address"]);
        Assert.Equal("Yes", rows["Synced from On-Premises"]);
    }

    // Last Changed, like Created, is Exchange Online's copy's for a synced recipient.
    [Theory]
    [InlineData(true, "2026-10-20 09:30 (UTC, UTC+00:00) (Exchange Online's copy)")]
    [InlineData(false, "2026-10-20 09:30 (UTC, UTC+00:00)")]
    public async Task LastChanged_SaysWhenItIsExchangeOnlinesCopy(bool synced, string shown)
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "MailContact"), ("IsDirSynced", synced),
            ("WhenChanged", ChangedUtc));

        Assert.Equal(shown, RowsOf(Rendered(await LookedUp()))["Last Changed"]);
    }

    // No read is added: a contact's directory facts come from the one Get-Recipient.
    [Fact]
    public async Task DirectoryFacts_AddNoRead()
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "MailContact"), ("IsDirSynced", true),
            ("Alias", "alice"));

        await LookedUp();

        Assert.Equal([$"Get-Recipient {Address}"], _exchange.CallList());
    }
}
