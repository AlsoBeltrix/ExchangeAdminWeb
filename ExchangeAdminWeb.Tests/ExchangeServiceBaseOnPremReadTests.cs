using System.Reflection;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23o. The shared base's on-premises reads. (1) <c>GetOnPremMailboxSizeAsync</c> read a TotalItemSize it
/// could not parse as 0 and any failure of the archive read as no archive, so the move-to-Exchange-Online quota check could
/// pass a mailbox whose size could not be read; any other failure was null, which the quota check filed as Ineligible with
/// a cause it guessed, "(on-prem connection unavailable)". (2) <c>HasCloudMailboxAsync</c> read Get-Mailbox under
/// -ErrorAction Ignore and <c>OnPremRecipientExists</c> under SilentlyContinue, so a read that failed was "no mailbox", and a
/// location nobody could ask about was "Unknown" - which the pages word as "No cloud mailbox, on-prem mailbox, or on-prem
/// remote mailbox was found."
/// </summary>
/// <remarks>
/// The real services over <see cref="MigrationStubExchange"/>: its Exchange Online stand-in in the pool, and its on-premises
/// stand-in defined in the service's own on-premises runspace through the <c>ConnectOnPrem</c> seam (an Invoke-Command that
/// runs the remote script locally, on-premises Get-MailboxStatistics, Get-Mailbox and Get-RemoteMailbox logged as "OnPrem
/// ..."). No Exchange, no Delinea, no session.
/// </remarks>
public sealed class ExchangeServiceBaseOnPremReadTests : IDisposable
{
    private const string Address = "alice@contoso.test";
    private const string Unparseable = "the reported TotalItemSize 'Unlimited' is not a size this app can read";

    private readonly MigrationStubExchange _exchange = new() { OnPremConfigured = true };

    public void Dispose() => _exchange.Dispose();

    // A move to Exchange Online of a mailbox not there yet: Eligible before the size step, which reads on-premises.
    private Task<MigrationEligibilityResult> ToCloud() =>
        _exchange.Service().CheckMigrationEligibilityAsync(Address, MigrationDirection.ToCloud);

    private static string Undetermined(string what, string reason) =>
        $"Could not be determined: the on-premises {what} could not be read: {reason}";

    // ---- the size read: a size that could not be read is never 0 ---------------------------------

    // The finding. Each way the mailbox's size can fail to be read. Before: a size that does not parse was 0 - Eligible,
    // under any quota - and a failed read was null, Ineligible "(on-prem connection unavailable)".
    [Theory]
    [InlineData("thrown", "Get-MailboxStatistics failed: the mailbox database is offline")]
    [InlineData("written", "Get-MailboxStatistics failed: the mailbox database is offline")]
    [InlineData("no row", "on-premises Exchange returned no mailbox statistics")]
    [InlineData("no size", "Exchange reported no TotalItemSize")]
    [InlineData("does not parse", Unparseable)]
    public async Task AnOnPremMailboxSizeThatCannotBeRead_IsUndetermined_NeverZero(string how, string reason)
    {
        switch (how)
        {
            case "thrown": _exchange.Failures["OnPrem Get-MailboxStatistics"] = "the mailbox database is offline"; break;
            case "written": _exchange.Failures["OnPrem Get-MailboxStatistics"] = Refused("the mailbox database is offline"); break;
            case "no row": _exchange.Behaviour["OnPremMailboxSize"] = null; break;
            case "no size": _exchange.Behaviour["OnPremMailboxSize"] = ""; break;
            default: _exchange.Behaviour["OnPremMailboxSize"] = "Unlimited"; break;
        }

        var result = await ToCloud();

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal(reason, result.MailboxSizeUnreadable);
        Assert.Null(result.ArchiveSizeUnreadable);
        Assert.Equal([Undetermined("mailbox size", reason)], result.IneligibilityReasons);
    }

    // The finding's archive half. Before: any failure of the archive read was no archive, and the check went on Eligible.
    [Theory]
    [InlineData("thrown", "Get-MailboxStatistics -Archive failed: the archive database is offline")]
    [InlineData("written", "Get-MailboxStatistics -Archive failed: the archive database is offline")]
    [InlineData("no row", "on-premises Exchange returned no archive statistics")]
    [InlineData("does not parse", Unparseable)]
    public async Task AnOnPremArchiveThatCannotBeRead_IsUndetermined_NotNoArchive(string how, string reason)
    {
        switch (how)
        {
            case "thrown": _exchange.Failures["OnPrem Get-MailboxStatistics -Archive"] = "the archive database is offline"; break;
            case "written": _exchange.Failures["OnPrem Get-MailboxStatistics -Archive"] = Refused("the archive database is offline"); break;
            case "no row": _exchange.Behaviour["OnPremArchiveSize"] = null; break;
            default: _exchange.Behaviour["OnPremArchiveSize"] = "Unlimited"; break;
        }

        var result = await ToCloud();

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Null(result.MailboxSizeUnreadable);
        Assert.Equal(2.5, result.MailboxSizeGB);
        Assert.Equal(reason, result.ArchiveSizeUnreadable);
        Assert.Equal([Undetermined("archive size", reason)], result.IneligibilityReasons);
    }

    // Exchange saying there is no archive, read from the error's structure in each shape aud-a23h recognises, is no
    // archive: nothing to measure, nothing unread.
    [Theory]
    [InlineData("category")]
    [InlineData("type")]
    [InlineData("identifier")]
    public async Task NoOnPremArchive_IsNoArchive(string shape)
    {
        _exchange.Behaviour["OnPremNoArchive"] = true;
        _exchange.NotFound = shape switch
        {
            "category" => NotFoundByCategory,
            "type" => NotFoundByType,
            _ => NotFoundByIdentifier,
        };

        var result = await ToCloud();

        Assert.Equal(MigrationStatus.Eligible, result.Status);
        Assert.Empty(result.IneligibilityReasons);
        Assert.Equal((2.5, 0.0), (result.MailboxSizeGB, result.ArchiveSizeGB));
        Assert.Null(result.MailboxSizeUnreadable);
        Assert.Null(result.ArchiveSizeUnreadable);
    }

    // A read that could not be made at all, in the base's own words - never the cause "(on-prem connection unavailable)",
    // which the quota check named whatever the base had failed on.
    [Theory]
    [InlineData("not configured")]
    [InlineData("no credentials")]
    [InlineData("the session cannot be opened")]
    public async Task ASizeReadThatCannotBeMade_IsUndetermined_InTheBasesWords(string how)
    {
        using var exchange = new MigrationStubExchange { OnPremConfigured = how != "not configured", OnPremCredentials = how != "no credentials" };
        if (how == "the session cannot be opened")
            exchange.Failures["OnPrem Connect"] = "the remote session could not be opened";

        var result = await exchange.Service().CheckMigrationEligibilityAsync(Address, MigrationDirection.ToCloud);

        var reason = how switch
        {
            "not configured" => ExchangeServiceBase.OnPremNotConfigured,
            "no credentials" => ExchangeServiceBase.OnPremCredentialsUnavailable,
            _ => "the remote session could not be opened",
        };
        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal((reason, reason), (result.MailboxSizeUnreadable, result.ArchiveSizeUnreadable));
        Assert.Equal([Undetermined("mailbox and archive sizes", reason)], result.IneligibilityReasons);
    }

    // The controls: readable sizes decide as before - under the quota Eligible with the sizes, over it Ineligible.
    [Fact]
    public async Task ReadableSizesUnderTheQuota_AreEligible_WithTheSizes()
    {
        var result = await ToCloud();

        Assert.Equal(MigrationStatus.Eligible, result.Status);
        Assert.Empty(result.IneligibilityReasons);
        Assert.Equal((2.5, 0.5), (result.MailboxSizeGB, result.ArchiveSizeGB));
    }

    [Fact]
    public async Task AReadableSizeOverTheQuota_IsIneligible()
    {
        _exchange.Behaviour["OnPremMailboxSize"] = "120 GB (128,849,018,880 bytes)";

        var result = await ToCloud();

        Assert.Equal(MigrationStatus.Ineligible, result.Status);
        Assert.Equal(["Primary mailbox size (120.00 GB) exceeds the per-mailbox limit (99 GB)"], result.IneligibilityReasons);
    }

    // A known reason stands (the aud-a23h rule): over the quota is Ineligible whatever the archive would have read, and the
    // archive that could not be read is named beside it.
    [Fact]
    public async Task AnIneligibleSizeAnswer_WithAnArchiveThatCannotBeRead_StaysIneligible_WithBothReasons()
    {
        _exchange.Behaviour["OnPremMailboxSize"] = "120 GB (128,849,018,880 bytes)";
        _exchange.Failures["OnPrem Get-MailboxStatistics -Archive"] = Refused("the archive database is offline");

        var result = await ToCloud();

        Assert.Equal(MigrationStatus.Ineligible, result.Status);
        Assert.Equal(
            ["Primary mailbox size (120.00 GB) exceeds the per-mailbox limit (99 GB)",
             Undetermined("archive size", "Get-MailboxStatistics -Archive failed: the archive database is offline")],
            result.IneligibilityReasons);
    }

    // Through the page's own check: the operator is told it could not be determined, and why, with the size the page shows
    // as not read - not "IS NOT eligible", and not a size of 0.
    [Fact]
    public async Task ThePage_ShowsASizeThatCouldNotBeRead_AsUndetermined()
    {
        _exchange.Behaviour["OnPremMailboxSize"] = "Unlimited";
        var page = _exchange.Page(_exchange.Service());
        Field("singleEmail").SetValue(page, Address);
        Field("singleTicketNumber").SetValue(page, "INC0001");
        Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToCloud);

        await Call(page, "CheckSingleEligibility");
        var rendered = page.RenderText();

        Assert.Contains($"Whether {Address} can migrate to Exchange Online could not be determined.", rendered, StringComparison.Ordinal);
        Assert.Contains(Undetermined("mailbox size", Unparseable), rendered, StringComparison.Ordinal);
        Assert.Contains($"could not be read ({Unparseable})", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("IS NOT eligible", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("0.00 GB", rendered, StringComparison.Ordinal);
    }

    // ---- the recipient reads: a failed read is not "no mailbox" ----------------------------------

    [Fact]
    public async Task ACloudMailbox_IsCloud_AndNothingOnPremisesIsAsked()
    {
        _exchange.Mailboxes[Address] = CloudMailbox(Address);

        var location = await _exchange.Service().GetMailboxLocationAsync(Address, TestContext.Current.CancellationToken);

        Assert.Equal("Cloud", location);
        Assert.DoesNotContain(_exchange.CallList(), call => call.StartsWith("OnPrem", StringComparison.Ordinal));
    }

    // The finding. Before: under Ignore the failed read was "no cloud mailbox", on-premises was asked, and nothing found
    // there was "Unknown" - a mailbox nobody could ask about, reported as one that does not exist.
    [Theory]
    [InlineData("thrown")]
    [InlineData("written")]
    public async Task ACloudReadThatFails_IsAFailedRead_NeverNoMailbox(string how)
    {
        _exchange.Failures[$"Get-Mailbox {Address}"] = how == "thrown" ? "the directory is busy" : Refused("the directory is busy");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => _exchange.Service().GetMailboxLocationAsync(Address, TestContext.Current.CancellationToken));

        Assert.Equal("Exchange Online could not be asked: the directory is busy", failure.Message);
        Assert.DoesNotContain(_exchange.CallList(), call => call.StartsWith("OnPrem", StringComparison.Ordinal));
    }

    // Exchange Online's not-found is an answer, and on-premises decides: its mailbox, its remote mailbox (in the cloud), or
    // neither - "Unknown", now meaning both sides said there is no such mailbox.
    [Theory]
    [InlineData("on-premises mailbox", "OnPrem")]
    [InlineData("remote mailbox", "Cloud")]
    [InlineData("neither", "Unknown")]
    public async Task NotInTheCloud_OnPremisesDecides(string held, string expected)
    {
        if (held == "on-premises mailbox")
            _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));
        if (held == "remote mailbox")
            _exchange.OnPremRemoteMailboxes[Address] = Row(("Identity", Address));

        var location = await _exchange.Service().GetMailboxLocationAsync(Address, TestContext.Current.CancellationToken);

        Assert.Equal(expected, location);
    }

    // The finding's on-premises half. Before: under SilentlyContinue each failed read was "no such mailbox", and both
    // failing was "Unknown".
    [Theory]
    [InlineData("Get-Mailbox", "thrown")]
    [InlineData("Get-Mailbox", "written")]
    [InlineData("Get-RemoteMailbox", "thrown")]
    [InlineData("Get-RemoteMailbox", "written")]
    public async Task AnOnPremReadThatFails_IsAFailedRead(string cmdlet, string how)
    {
        _exchange.Failures[$"OnPrem {cmdlet}"] = how == "thrown" ? "the recipient service is unavailable" : Refused("the recipient service is unavailable");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => _exchange.Service().GetMailboxLocationAsync(Address, TestContext.Current.CancellationToken));

        Assert.Equal($"on-premises Exchange could not be asked: {cmdlet} failed: the recipient service is unavailable", failure.Message);
    }

    // A cmdlet the on-premises session does not have says nothing about the mailbox (aud-a23h's rule).
    [Fact]
    public async Task AnOnPremCmdletTheSessionLacks_IsAFailedRead()
    {
        _exchange.Missing.Add("OnPrem Get-RemoteMailbox");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => _exchange.Service().GetMailboxLocationAsync(Address, TestContext.Current.CancellationToken));

        Assert.StartsWith("on-premises Exchange could not be asked: Get-RemoteMailbox failed: ", failure.Message, StringComparison.Ordinal);
    }

    // On-premises that could not be asked at all. Before: null, so "Unknown", so "No ... on-prem mailbox ... was found."
    [Theory]
    [InlineData("not configured")]
    [InlineData("no credentials")]
    [InlineData("the session cannot be opened")]
    public async Task OnPremisesThatCannotBeAsked_IsAFailedRead(string how)
    {
        using var exchange = new MigrationStubExchange { OnPremConfigured = how != "not configured", OnPremCredentials = how != "no credentials" };
        if (how == "the session cannot be opened")
            exchange.Failures["OnPrem Connect"] = "the remote session could not be opened";

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => exchange.Service().GetMailboxLocationAsync(Address, TestContext.Current.CancellationToken));

        var reason = how switch
        {
            "not configured" => ExchangeServiceBase.OnPremNotConfigured,
            "no credentials" => ExchangeServiceBase.OnPremCredentialsUnavailable,
            _ => "the remote session could not be opened",
        };
        Assert.Equal($"on-premises Exchange could not be asked: {reason}", failure.Message);
    }

    [Fact]
    public void ALocationReadThatFailed_IsRefusedSayingNothingWasWritten() =>
        Assert.Equal(
            "Could not determine mailbox location for shared@contoso.test: Exchange Online could not be asked: the directory is busy. Nothing was written.",
            ExchangeServiceBase.LocationReadFailure("shared@contoso.test", new InvalidOperationException("Exchange Online could not be asked: the directory is busy.")));

    // Pinned on the line, as ValidatedWriteCallSiteTests pins the same call: a failed location read is refused there, before
    // any write, through LocationReadFailure. It reached each page's catch, "Operation may have succeeded but audit logging
    // failed: ...". aud-a70 (5): the refusal goes through the page's Refused, which records it -
    // PermissionPagesSelfGrantTests.ARefusalBeforeTheWrite_IsRecordedAsFailed_NothingWasWritten drives it end to end. kd-81: the
    // read takes the activity's token, and the operator's cancel there is not a read that failed - it goes on to the page's
    // cancel catch (PermissionCloudChangeCancellationTests drives it).
    [Theory]
    [InlineData("CalendarPermissions.razor", "CalendarService")]
    [InlineData("MailboxPermissions.razor", "MailboxService")]
    public void APermissionsPage_RefusesALocationReadThatFailed_BeforeAnyWrite(string page, string service)
    {
        var source = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", page));

        Assert.Matches(new Regex(
            $@"string location; try \{{ location = await {service}\.GetMailboxLocationAsync\(validation\.Mailbox, activity\.CancellationToken\); \}} "
            + @"catch \(Exception locationEx\) when \(!PermissionChangeFailure\.CancelledBeforeItWasSent\(locationEx, step, activity\.CancellationToken\)\) "
            + @"\{ result = PermissionResult\.Fail\(Refused\(ExchangeServiceBase\.LocationReadFailure\(target, locationEx\)\)\); return; \}"),
            source);
        Assert.DoesNotMatch(new Regex(@"var location = await"), source);
    }

    // ---- Recipient Lookup ---------------------------------------------------------------------

    [Fact]
    public async Task RecipientLookup_AnOnPremSizeThatCannotBeRead_IsAWarning_NotZero()
    {
        OnPremRecipient();
        _exchange.Behaviour["OnPremMailboxSize"] = "Unlimited";

        var result = await _exchange.RecipientLookup().GetRecipientInfoAsync(Address, TestContext.Current.CancellationToken);

        Assert.Equal("On-Premises", result.MailboxLocation);
        Assert.Null(result.MailboxSizeGB);
        Assert.Equal(0.5, result.ArchiveSizeGB);
        Assert.Contains($"The on-premises mailbox size could not be read: {Unparseable}.", result.Warnings);
    }

    [Fact]
    public async Task RecipientLookup_AnOnPremArchiveReadThatFails_KeepsTheMailboxSize_AndSaysWhy()
    {
        OnPremRecipient();
        _exchange.Failures["OnPrem Get-MailboxStatistics -Archive"] = Refused("the archive database is offline");

        var result = await _exchange.RecipientLookup().GetRecipientInfoAsync(Address, TestContext.Current.CancellationToken);

        Assert.Equal(2.5, result.MailboxSizeGB);
        Assert.Null(result.ArchiveSizeGB);
        Assert.Contains("The on-premises archive size could not be read: Get-MailboxStatistics -Archive failed: the archive database is offline.", result.Warnings);
    }

    [Fact]
    public async Task RecipientLookup_ReadableOnPremSizes_AreShown()
    {
        OnPremRecipient();

        var result = await _exchange.RecipientLookup().GetRecipientInfoAsync(Address, TestContext.Current.CancellationToken);

        Assert.Equal((2.5, 0.5), (result.MailboxSizeGB, result.ArchiveSizeGB));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("size", StringComparison.OrdinalIgnoreCase));
    }

    // Its location read already catches a throw as "Unknown" with a warning naming it: a failed read now reaches it.
    [Fact]
    public async Task RecipientLookup_ACloudLocationReadThatFails_IsUnknown_SayingWhy()
    {
        OnPremRecipient();
        _exchange.Failures[$"Get-Mailbox {Address}"] = Refused("the directory is busy");

        var result = await _exchange.RecipientLookup().GetRecipientInfoAsync(Address, TestContext.Current.CancellationToken);

        Assert.Equal("Unknown", result.MailboxLocation);
        Assert.Contains("Could not determine mailbox location: Exchange Online could not be asked: the directory is busy", result.Warnings);
    }

    private void OnPremRecipient()
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "MailUser"));
        _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));
    }

    // ---- what went ----------------------------------------------------------------------------

    // The substring group check aud-a23j replaced, and the size reader that turned an unreadable size into 0.
    [Theory]
    [InlineData("CheckAdGroupMembership")]
    [InlineData("ParseExchangeSize")]
    public void TheBaseNoLongerCarries(string member) =>
        Assert.Null(typeof(ExchangeServiceBase).GetMethod(member, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
}
