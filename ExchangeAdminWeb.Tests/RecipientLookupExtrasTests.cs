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

    // ---- E1: quotas -------------------------------------------------------------------------------

    // A cloud mailbox: Exchange Online's recipient row, and its mailbox row with the quotas given (the rest as reported).
    private void ACloudMailboxWith(params (string Name, object? Value)[] mailbox)
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "UserMailbox"),
            ("PrimarySmtpAddress", Address), ("IsDirSynced", false));
        _exchange.Mailboxes[Address] = Row([("PrimarySmtpAddress", Address), ("RecipientTypeDetails", "UserMailbox"), .. mailbox]);
    }

    private static readonly (string, object?)[] Quotas =
    [
        ("IssueWarningQuota", "49 GB (52,613,349,376 bytes)"),
        ("ProhibitSendQuota", "50 GB (53,687,091,200 bytes)"),
        ("ProhibitSendReceiveQuota", "Unlimited"),
        ("UseDatabaseQuotaDefaults", false),
        ("ArchiveQuota", "100 GB (107,374,182,400 bytes)"),
        ("ArchiveWarningQuota", "90 GB (96,636,764,160 bytes)"),
    ];

    // The quotas the description has always promised, beside the size, and the share of the send quota the size uses
    // (the stand-in's 1.5 GB of 50 GB); the archive's beside the archive size.
    [Fact]
    public async Task ACloudMailboxsQuotas_AreShownBesideItsSize()
    {
        ACloudMailboxWith(Quotas);

        var rows = RowsOf(Rendered(await LookedUp()));

        Assert.Equal("1.50 GB", rows["Mailbox Size"]);
        Assert.Equal("49.00 GB", rows["Warning Quota"]);
        Assert.Equal("50.00 GB", rows["Prohibit Send Quota"]);
        Assert.Equal("Unlimited", rows["Prohibit Send/Receive Quota"]);
        Assert.Equal("3.0% of the send quota", rows["Send Quota Used"]);
        Assert.Equal("100.00 GB", rows["Archive Quota"]);
        Assert.Equal("90.00 GB", rows["Archive Warning Quota"]);
    }

    // A mailbox on its database's defaults: its own three quotas are not the ones that apply, and the database is not read.
    [Fact]
    public async Task AMailboxOnItsDatabasesDefaults_SaysSo_WithoutReadingTheDatabase()
    {
        ACloudMailboxWith([.. Quotas.Where(quota => quota.Item1 != "UseDatabaseQuotaDefaults"), ("UseDatabaseQuotaDefaults", true)]);

        var rows = RowsOf(Rendered(await LookedUp()));

        foreach (var row in new[] { "Warning Quota", "Prohibit Send Quota", "Prohibit Send/Receive Quota" })
            Assert.Equal((row, "The database default (not read)"), (row, rows[row]));
        Assert.Equal("Not read (the database default applies)", rows["Send Quota Used"]);
        Assert.Equal("100.00 GB", rows["Archive Quota"]);
        Assert.DoesNotContain(_exchange.CallList(), call => call.Contains("Database", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task QuotasExchangeDidNotReturn_AreNotReported()
    {
        ACloudMailboxWith();

        var rows = RowsOf(Rendered(await LookedUp()));

        foreach (var row in new[] { "Warning Quota", "Prohibit Send Quota", "Prohibit Send/Receive Quota", "Send Quota Used", "Archive Quota", "Archive Warning Quota" })
            Assert.Equal((row, NotReported), (row, rows[row]));
    }

    [Theory]
    [InlineData("Unlimited", "Unlimited", "Not applicable (no send quota)")]
    [InlineData("a lot", "a lot (not a size this app can read)", "Could not be read")]
    public async Task ASendQuotaThatIsNotASize_IsShownAsReported_AndItsShareIsNot(string quota, string shown, string used)
    {
        ACloudMailboxWith(("ProhibitSendQuota", quota));

        var rows = RowsOf(Rendered(await LookedUp()));

        Assert.Equal((shown, used), (rows["Prohibit Send Quota"], rows["Send Quota Used"]));
    }

    // The share needs the size: a statistics read that failed leaves it unread too.
    [Fact]
    public async Task ASendQuotaShare_CouldNotBeRead_WhenTheSizeCouldNotBe()
    {
        ACloudMailboxWith(Quotas);
        _exchange.Failures[$"Get-MailboxStatistics {Address}"] = Refused("the mailbox database is offline");

        var rows = RowsOf(Rendered(await LookedUp()));

        Assert.Equal(("50.00 GB", "Could not be read"), (rows["Prohibit Send Quota"], rows["Send Quota Used"]));
    }

    // An on-premises mailbox's quotas are on-premises Exchange's, and this module does not ask for them (extra 4 was not
    // taken); a list has none.
    [Fact]
    public async Task AnOnPremisesMailboxsQuotas_AreNotRead_AndAListHasNone()
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "MailUser"), ("IsDirSynced", true));
        _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));
        var onPrem = RowsOf(Rendered(await LookedUp()));
        foreach (var row in new[] { "Warning Quota", "Prohibit Send Quota", "Prohibit Send/Receive Quota", "Send Quota Used", "Archive Quota", "Archive Warning Quota" })
            Assert.Equal((row, "Not read for on-premises mailboxes"), (row, onPrem[row]));

        _exchange.Recipients[Address] = Row(("DisplayName", "List"), ("RecipientTypeDetails", "MailUniversalDistributionGroup"));
        var list = RowsOf(Rendered(await LookedUp()));
        foreach (var row in new[] { "Warning Quota", "Prohibit Send Quota", "Prohibit Send/Receive Quota", "Send Quota Used" })
            Assert.Equal((row, "Not applicable"), (row, list[row]));
    }

    // ---- E3: mailbox settings ---------------------------------------------------------------------

    private static readonly DateTime LogonUtc = new(2026, 10, 27, 7, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ActionUtc = new(2026, 10, 26, 16, 45, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(true, "smtp:bob@fabrikam.test, and keeps a copy")]
    [InlineData(false, "smtp:bob@fabrikam.test, forward only")]
    [InlineData(null, "smtp:bob@fabrikam.test (whether a copy is kept was not reported by Exchange)")]
    public async Task Forwarding_SaysWhetherTheMailboxKeepsACopy(bool? keeps, string shown)
    {
        ACloudMailboxWith(keeps is { } reported
            ? [("ForwardingSmtpAddress", "smtp:bob@fabrikam.test"), ("DeliverToMailboxAndForward", reported)]
            : [("ForwardingSmtpAddress", "smtp:bob@fabrikam.test")]);

        Assert.Equal(shown, RowsOf(Rendered(await LookedUp()))["Forwarding"]);
    }

    [Fact]
    public async Task ForwardingThatIsNotSet_IsStillNone()
    {
        ACloudMailboxWith(("DeliverToMailboxAndForward", false));

        Assert.Equal("None", RowsOf(Rendered(await LookedUp()))["Forwarding"]);
    }

    [Theory]
    [InlineData("two", "Bob; Carol")]
    [InlineData("none", "None")]
    [InlineData("not reported", NotReported)]
    public async Task SendOnBehalf_ListsTheDelegates(string reported, string shown)
    {
        ACloudMailboxWith(reported switch
        {
            "two" => [("GrantSendOnBehalfTo", new[] { "Bob", "Carol" })],
            "none" => [("GrantSendOnBehalfTo", Array.Empty<string>())],
            _ => [],
        });

        Assert.Equal(shown, RowsOf(Rendered(await LookedUp()))["Send on Behalf"]);
    }

    [Fact]
    public async Task AccountDisabledAndArchiveStatus_AreExchangesOwnWords()
    {
        ACloudMailboxWith(("AccountDisabled", true), ("ArchiveStatus", "Active"), ("ArchiveState", "HostedProvisioned"));

        var rows = RowsOf(Rendered(await LookedUp()));

        Assert.Equal(("Yes", "Active (state: HostedProvisioned)"), (rows["Account Disabled"], rows["Archive Status"]));
    }

    [Fact]
    public async Task MailboxSettingsExchangeDidNotReturn_AreNotReported()
    {
        ACloudMailboxWith();

        var rows = RowsOf(Rendered(await LookedUp()));

        foreach (var row in new[] { "Account Disabled", "Archive Status", "Send on Behalf", "Last User Activity" })
            Assert.Equal((row, NotReported), (row, rows[row]));
    }

    // Last Logon moves for background access too (the plan's A11): the last action the user took is its own row.
    [Fact]
    public async Task LastUserActivity_IsTheLastActionTheUserTook_BesideLastLogon()
    {
        ACloudMailboxWith();
        _exchange.Behaviour["StatisticsRow"] = Row(("TotalItemSize", "1.5 GB (1,610,612,736 bytes)"), ("LastLogonTime", LogonUtc),
            ("LastUserActionTime", ActionUtc));

        var rows = RowsOf(Rendered(await LookedUp()));

        Assert.Equal("2026-10-27 07:00 (UTC, UTC+00:00)", rows["Last Logon"]);
        Assert.Equal("2026-10-26 16:45 (UTC, UTC+00:00)", rows["Last User Activity"]);
    }

    // Each setting follows its kind's words: an on-premises mailbox's are not read, a list has none, and a statistics read
    // that failed leaves the last activity unread.
    [Fact]
    public async Task MailboxSettings_FollowTheKindsWords()
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "MailUser"), ("IsDirSynced", true));
        _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));
        var onPrem = RowsOf(Rendered(await LookedUp()));
        foreach (var row in new[] { "Last User Activity", "Archive Status", "Send on Behalf", "Account Disabled" })
            Assert.Equal((row, "Not read for on-premises mailboxes"), (row, onPrem[row]));

        _exchange.Recipients[Address] = Row(("DisplayName", "List"), ("RecipientTypeDetails", "MailUniversalDistributionGroup"));
        var list = RowsOf(Rendered(await LookedUp()));
        foreach (var row in new[] { "Last User Activity", "Archive Status", "Send on Behalf", "Account Disabled" })
            Assert.Equal((row, "Not applicable"), (row, list[row]));

        ACloudMailboxWith(("AccountDisabled", false));
        _exchange.Failures[$"Get-MailboxStatistics {Address}"] = Refused("the mailbox database is offline");
        var unread = RowsOf(Rendered(await LookedUp()));
        Assert.Equal(("Could not be read", "No"), (unread["Last User Activity"], unread["Account Disabled"]));
    }
}
