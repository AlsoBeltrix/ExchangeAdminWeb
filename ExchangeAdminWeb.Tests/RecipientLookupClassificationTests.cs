using System.Globalization;
using System.Reflection;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using Page = ExchangeAdminWeb.Components.Pages.RecipientLookup;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// RecipientLookupAccuracy S1, the owner's report of 2026-10-27: "an on-prem mailbox shows as 'MailUser' and OnPremises, but
/// it should show Mailbox." The type row printed Exchange Online's RecipientTypeDetails alone, and for a mailbox held
/// on-premises that is MailUser; the on-premises read that placed it there selected Identity and nothing else. The row now
/// names what the recipient is from Exchange's own answers on the side that holds it (<see cref="RecipientClassification"/>),
/// with Exchange's raw words in small print beneath. Exchange Online's Get-Mailbox is asked only when the type leaves the
/// answer open, on-premises only about a mail user Exchange Online does not report as cloud-only, and the shared location
/// read - which repeated Get-Mailbox and asked on-premises about every list and contact - is no longer called.
/// </summary>
/// <remarks>
/// The real <see cref="RecipientLookupService"/> over <see cref="MigrationStubExchange"/>: Exchange Online in the pool, and
/// on-premises Exchange in its seams, whose Get-Recipient answers from <see cref="MigrationStubExchange.OnPremRecipients"/>.
/// And the page's compiled render tree showing the result. No Exchange.
/// </remarks>
public sealed class RecipientLookupClassificationTests : IDisposable
{
    private const string Address = "alice@contoso.test";

    private readonly MigrationStubExchange _exchange = new() { OnPremConfigured = true };

    public void Dispose() => _exchange.Dispose();

    // Exchange Online's Get-Recipient row: the type, the sync state when given, and the primary address.
    private void Recipient(string? type, bool? synced, string typed = Address, string? primary = Address)
    {
        var fields = new List<(string Name, object? Value)> { ("DisplayName", "Alice") };
        if (type is not null)
            fields.Add(("RecipientTypeDetails", type));
        if (synced is { } reported)
            fields.Add(("IsDirSynced", reported));
        if (primary is not null)
            fields.Add(("PrimarySmtpAddress", primary));
        _exchange.Recipients[typed] = Row([.. fields]);
    }

    private static bool? Synced(string state) => state switch
    {
        "synced" => true,
        "cloud-only" => false,
        _ => null,
    };

    private Task<RecipientInfoResult> LookedUp(string typed = Address) =>
        _exchange.RecipientLookup().GetRecipientInfoAsync(typed, TestContext.Current.CancellationToken);

    private string[] OnPremCalls() =>
        [.. _exchange.CallList().Where(call => call.StartsWith("OnPrem", StringComparison.Ordinal))];

    private int CloudMailboxReads() =>
        _exchange.CallList().Count(call => call.StartsWith("Get-Mailbox ", StringComparison.Ordinal));

    private bool OnPremSizeWasRead() =>
        _exchange.CallList().Any(call => call.StartsWith("OnPrem Get-MailboxStatistics", StringComparison.Ordinal));

#pragma warning disable BL0006 // the page's compiled render tree is read directly; no renderer is involved
    internal static string Rendered(RecipientInfoResult result)
    {
        var page = new Page();
        typeof(Page).GetField("result", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, result);
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            using var builder = new RenderTreeBuilder();
            typeof(Page).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [builder]);
            var frames = builder.GetFrames();
            return string.Concat(frames.Array.Take(frames.Count).Select(frame => frame.FrameType switch
            {
                RenderTreeFrameType.Text => frame.TextContent,
                RenderTreeFrameType.Markup => frame.MarkupContent,
                _ => "",
            }));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
#pragma warning restore BL0006

    // ---- T1: Exchange Online's mailboxes --------------------------------------------------------

    [Theory]
    [InlineData("UserMailbox", "Mailbox")]
    [InlineData("SharedMailbox", "Shared mailbox")]
    [InlineData("RoomMailbox", "Room mailbox")]
    [InlineData("EquipmentMailbox", "Equipment mailbox")]
    [InlineData("SchedulingMailbox", "Scheduling mailbox")]
    [InlineData("LinkedMailbox", "Linked mailbox")]
    [InlineData("DiscoveryMailbox", "Discovery mailbox")]
    public async Task AnExchangeOnlineMailbox_IsNamed_InTheCloud_AndNothingOnPremisesIsAsked(string type, string label)
    {
        Recipient(type, synced: true);
        _exchange.Mailboxes[Address] = Row(("PrimarySmtpAddress", Address), ("RecipientTypeDetails", type));

        var result = await LookedUp();

        Assert.Null(result.Error);
        Assert.Equal((label, "Cloud", RecipientLocationClass.Cloud), (result.RecipientTypeLabel, result.MailboxLocation, result.LocationClass));
        Assert.Equal(1, CloudMailboxReads());
        Assert.Empty(OnPremCalls());
        Assert.Equal(0, _exchange.OnPremSessions);

        var page = Rendered(result);
        Assert.Contains($"Recipient Type</td>{label}", page, StringComparison.Ordinal);
        Assert.Contains($"Exchange Online: {type}", page, StringComparison.Ordinal);
    }

    // ---- T2, T3: types whose Exchange Online answer is already known ------------------------------

    [Theory]
    [InlineData("MailUniversalDistributionGroup", "Distribution list", "synced")]
    [InlineData("MailUniversalDistributionGroup", "Distribution list", "cloud-only")]
    [InlineData("MailUniversalDistributionGroup", "Distribution list", "not reported")]
    [InlineData("MailUniversalSecurityGroup", "Mail-enabled security group", "synced")]
    [InlineData("MailUniversalSecurityGroup", "Mail-enabled security group", "cloud-only")]
    [InlineData("MailNonUniversalGroup", "Distribution group (non-universal)", "synced")]
    [InlineData("DynamicDistributionGroup", "Dynamic distribution list", "cloud-only")]
    [InlineData("DynamicDistributionGroup", "Dynamic distribution list", "not reported")]
    [InlineData("RoomList", "Room list", "synced")]
    [InlineData("RoomList", "Room list", "cloud-only")]
    [InlineData("MailContact", "Contact", "synced")]
    [InlineData("MailContact", "Contact", "cloud-only")]
    [InlineData("MailForestContact", "Contact (another forest)", "not reported")]
    [InlineData("GuestMailUser", "Guest", "cloud-only")]
    [InlineData("GuestMailUser", "Guest", "not reported")]
    [InlineData("PublicFolder", "Mail-enabled public folder", "synced")]
    public async Task ARecipientWithNoMailbox_IsNamed_AndNeitherGetMailboxNorOnPremisesIsAsked(string type, string label, string state)
    {
        Recipient(type, Synced(state));

        var result = await LookedUp();

        Assert.Null(result.Error);
        Assert.Equal((label, RecipientLocationClass.NotApplicable), (result.RecipientTypeLabel, result.LocationClass));
        Assert.Equal(0, CloudMailboxReads());
        Assert.Empty(OnPremCalls());
        Assert.Equal(0, _exchange.OnPremSessions);
        Assert.Empty(result.Warnings);
        Assert.Contains($"Recipient Type</td>{label}", Rendered(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GroupMailbox", "Microsoft 365 group")]
    [InlineData("TeamMailbox", "Site mailbox")]
    [InlineData("PublicFolderMailbox", "Public folder mailbox")]
    [InlineData("ArbitrationMailbox", "System mailbox (ArbitrationMailbox)")]
    public async Task AMailboxExchangeOnlineHoldsAndThisModuleDoesNotRead_IsNamed_InTheCloud_NotRead(string type, string label)
    {
        Recipient(type, synced: false);

        var result = await LookedUp();

        Assert.Equal((label, "Cloud", RecipientLocationClass.ExchangeHeldNotRead), (result.RecipientTypeLabel, result.MailboxLocation, result.LocationClass));
        Assert.Equal(0, CloudMailboxReads());
        Assert.Empty(OnPremCalls());
        Assert.Empty(result.Warnings);
    }

    // ---- T4, T5, T6: a mail user ----------------------------------------------------------------

    // Exchange Online reports it cloud-only: on-premises is not asked, even configured and holding a mailbox at the address.
    [Fact]
    public async Task ACloudOnlyMailUser_IsAMailUser_AndOnPremisesIsNotAsked()
    {
        Recipient("MailUser", synced: false);
        _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));

        var result = await LookedUp();

        Assert.Equal(("Mail user", RecipientLocationClass.NotApplicable), (result.RecipientTypeLabel, result.LocationClass));
        Assert.Empty(OnPremCalls());
        Assert.Equal(0, _exchange.OnPremSessions);
        Assert.Equal(0, CloudMailboxReads());
        Assert.Empty(result.Warnings);
    }

    // T5, the owner's case: Exchange Online's mail user, on-premises Exchange's mailbox, synced or not reported as cloud-only.
    [Theory]
    [InlineData("UserMailbox", "Mailbox", "synced")]
    [InlineData("UserMailbox", "Mailbox", "not reported")]
    [InlineData("SharedMailbox", "Shared mailbox", "synced")]
    [InlineData("SharedMailbox", "Shared mailbox", "not reported")]
    [InlineData("RoomMailbox", "Room mailbox", "synced")]
    [InlineData("EquipmentMailbox", "Equipment mailbox", "not reported")]
    [InlineData("LinkedMailbox", "Linked mailbox", "synced")]
    [InlineData("LinkedRoomMailbox", "Linked room mailbox", "synced")]
    [InlineData("DiscoveryMailbox", "Discovery mailbox", "not reported")]
    public async Task AMailUserOnPremisesHoldsAsAMailbox_IsThatMailbox_OnPremises(string onPremType, string label, string state)
    {
        Recipient("MailUser", Synced(state));
        _exchange.OnPremRecipients[Address] = Row(("RecipientType", "UserMailbox"), ("RecipientTypeDetails", onPremType));

        var result = await LookedUp();

        Assert.Null(result.Error);
        Assert.Equal((label, "On-Premises", RecipientLocationClass.OnPremises), (result.RecipientTypeLabel, result.MailboxLocation, result.LocationClass));
        Assert.Equal(onPremType, result.OnPremRecipientType);
        Assert.Equal([$"OnPrem Get-Recipient {Address}"], OnPremCalls().Where(call => !call.StartsWith("OnPrem Get-MailboxStatistics", StringComparison.Ordinal)).ToArray());
        Assert.True(OnPremSizeWasRead());
        Assert.Equal(0, CloudMailboxReads());
        Assert.Empty(result.Warnings);

        var page = Rendered(result);
        Assert.Contains($"Recipient Type</td>{label}", page, StringComparison.Ordinal);
        Assert.Contains($"Exchange Online: MailUser; on-premises: {onPremType}", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Recipient Type</td>MailUser", page, StringComparison.Ordinal);
    }

    // T5 on the stand-in's older table: a fixture that put the address among the on-premises mailboxes means the same.
    [Fact]
    public async Task TheOwnersCase_OnTheOnPremisesMailboxTable_IsAMailbox_OnPremises()
    {
        Recipient("MailUser", synced: null);
        _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));

        var result = await LookedUp();

        Assert.Equal(("Mailbox", "On-Premises"), (result.RecipientTypeLabel, result.MailboxLocation));
        Assert.Equal("Exchange Online: MailUser; on-premises: UserMailbox", result.RecipientTypeSmallPrint);
    }

    [Fact]
    public async Task ASyncedMailUserOnPremisesHoldsAsAMailUser_IsAMailUser()
    {
        Recipient("MailUser", synced: true);
        _exchange.OnPremRecipients[Address] = Row(("RecipientType", "MailUser"), ("RecipientTypeDetails", "MailUser"));

        var result = await LookedUp();

        Assert.Equal(("Mail user", RecipientLocationClass.NotApplicable), (result.RecipientTypeLabel, result.LocationClass));
        Assert.Equal("Exchange Online: MailUser; on-premises: MailUser", result.RecipientTypeSmallPrint);
        Assert.False(OnPremSizeWasRead());
        Assert.Empty(result.Warnings);
    }

    // ---- T7: the sides disagree -----------------------------------------------------------------

    // D1. The shared location read answered "Cloud" here (on-premises' remote mailbox), though Exchange Online holds no mailbox.
    [Fact]
    public async Task ARemoteMailboxOnPremisesThatExchangeOnlineHoldsAsAMailUser_CouldNotBeDetermined()
    {
        Recipient("MailUser", synced: true);
        _exchange.OnPremRemoteMailboxes[Address] = Row(("Identity", Address));

        var result = await LookedUp();

        AssertUndetermined(result, "Exchange Online: MailUser; on-premises: RemoteUserMailbox");
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("RemoteUserMailbox", warning, StringComparison.Ordinal);
        Assert.Contains("(MailUser)", warning, StringComparison.Ordinal);
        Assert.StartsWith("Exchange Online and on-premises Exchange disagree about alice@contoso.test", warning, StringComparison.Ordinal);
    }

    // D2.
    [Theory]
    [InlineData("MailContact")]
    [InlineData("FutureRecipientKind")]
    public async Task AMailUserOnPremisesHoldsAsSomethingElse_CouldNotBeDetermined(string onPremType)
    {
        Recipient("MailUser", synced: null);
        _exchange.OnPremRecipients[Address] = Row(("RecipientTypeDetails", onPremType));

        var result = await LookedUp();

        AssertUndetermined(result, $"Exchange Online: MailUser; on-premises: {onPremType}");
        Assert.Equal([$"Exchange Online and on-premises Exchange disagree about {Address}: Exchange Online holds it as MailUser, on-premises Exchange as {onPremType}."],
            result.Warnings);
    }

    // D3. Read before as "both sides hold no such mailbox"; it may be held by a directory the configured server does not serve.
    [Theory]
    [InlineData("synced", "reports it as synced from an on-premises directory")]
    [InlineData("not reported", "did not report whether it is synced")]
    public async Task AMailUserOnPremisesDoesNotHold_CouldNotBeDetermined_NeverAMailUser(string state, string said)
    {
        Recipient("MailUser", Synced(state));

        var result = await LookedUp();

        AssertUndetermined(result, "Exchange Online: MailUser; on-premises: no such recipient");
        Assert.Equal([$"On-premises Exchange holds no recipient at {Address}, though Exchange Online {said}; it may be held by a "
            + "directory this module's on-premises server does not serve."], result.Warnings);
    }

    // D4. Exchange Online typed it as its own mailbox, and its Get-Mailbox says there is none.
    [Fact]
    public async Task AnExchangeOnlineMailboxGetMailboxDoesNotFind_CouldNotBeDetermined()
    {
        Recipient("UserMailbox", synced: false);

        var result = await LookedUp();

        AssertUndetermined(result, "Exchange Online: UserMailbox; Get-Mailbox: no mailbox");
        Assert.Equal([$"Exchange Online reports {Address} as UserMailbox but returned no mailbox for it."], result.Warnings);
        Assert.Empty(OnPremCalls());
    }

    private void AssertUndetermined(RecipientInfoResult result, string smallPrint)
    {
        Assert.Null(result.Error);
        Assert.Equal((RecipientClassification.Undetermined, RecipientLocationClass.Undetermined), (result.RecipientTypeLabel, result.LocationClass));
        Assert.Equal(smallPrint, result.RecipientTypeSmallPrint);
        Assert.False(OnPremSizeWasRead());
        var page = Rendered(result);
        Assert.Contains("Recipient Type</td>Could not be determined", page, StringComparison.Ordinal);
        Assert.Contains(smallPrint, page, StringComparison.Ordinal);
    }

    // ---- T8: a side that could not be asked -----------------------------------------------------

    // U1, in today's words: on-premises not configured, no credentials, a session that cannot be opened, a read that fails,
    // a cmdlet the session lacks. Never a type.
    [Theory]
    [InlineData("not configured")]
    [InlineData("no credentials")]
    [InlineData("the session cannot be opened")]
    [InlineData("the read throws")]
    [InlineData("the read writes an error")]
    [InlineData("the cmdlet is missing")]
    public async Task AMailUserOnPremisesCouldNotBeAskedAbout_CouldNotBeDetermined_InTodaysWords(string how)
    {
        Recipient("MailUser", synced: true);
        _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));
        _exchange.OnPremConfigured = how != "not configured";
        _exchange.OnPremCredentials = how != "no credentials";
        switch (how)
        {
            case "the session cannot be opened":
                _exchange.Failures["OnPrem Connect"] = "the remote session could not be opened";
                break;
            case "the read throws":
                _exchange.Failures["OnPrem Get-Recipient"] = "the recipient service is unavailable";
                break;
            case "the read writes an error":
                _exchange.Failures["OnPrem Get-Recipient"] = Refused("the recipient service is unavailable");
                break;
            case "the cmdlet is missing":
                _exchange.Missing.Add("OnPrem Get-Recipient");
                break;
        }

        var result = await LookedUp();

        AssertUndetermined(result, "Exchange Online: MailUser; on-premises: not read");
        var warning = Assert.Single(result.Warnings);
        const string prefix = "Could not determine mailbox location: on-premises Exchange could not be asked: ";
        switch (how)
        {
            case "not configured":
                Assert.Equal(prefix + ExchangeServiceBase.OnPremNotConfigured, warning);
                break;
            case "no credentials":
                Assert.Equal(prefix + ExchangeServiceBase.OnPremCredentialsUnavailable, warning);
                break;
            case "the session cannot be opened":
                Assert.Equal(prefix + "the remote session could not be opened", warning);
                break;
            case "the cmdlet is missing":
                Assert.StartsWith(prefix + "Get-Recipient failed: ", warning, StringComparison.Ordinal);
                break;
            default:
                Assert.Equal(prefix + "Get-Recipient failed: the recipient service is unavailable", warning);
                break;
        }
    }

    // U2: on-premises Exchange answered with more than one recipient for the address.
    [Fact]
    public async Task AMailUserOnPremisesAnswersTwiceFor_CouldNotBeDetermined()
    {
        Recipient("MailUser", synced: true);
        _exchange.OnPremRecipients[Address] = new List<object>
        {
            Row(("RecipientType", "UserMailbox"), ("RecipientTypeDetails", "UserMailbox")),
            Row(("RecipientType", "MailUser"), ("RecipientTypeDetails", "MailUser")),
        };

        var result = await LookedUp();

        AssertUndetermined(result, "Exchange Online: MailUser; on-premises: more than one recipient");
        Assert.Equal([$"On-premises Exchange returned more than one recipient for {Address}."], result.Warnings);
    }

    // ---- T9: a type this app does not name, or none ---------------------------------------------

    [Theory]
    [InlineData("FutureRecipientKind", "Not a type this app names: FutureRecipientKind")]
    [InlineData(null, "Exchange reported no type")]
    public async Task AnUnnamedType_IsShownRaw_AndGetMailboxDecides(string? type, string label)
    {
        // Get-Mailbox finds a mailbox whose own type the table holds: named from it, in the cloud, its statistics read.
        Recipient(type, synced: true);
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
        var named = await LookedUp();
        Assert.Equal(("Mailbox", RecipientLocationClass.Cloud), (named.RecipientTypeLabel, named.LocationClass));
        Assert.Equal(1, CloudMailboxReads());
        Assert.Contains(_exchange.CallList(), call => call.StartsWith("Get-MailboxStatistics", StringComparison.Ordinal));
        Assert.Empty(OnPremCalls());

        // One whose own type it does not hold: still in the cloud, the label raw.
        _exchange.Mailboxes[Address] = Row(("PrimarySmtpAddress", Address), ("RecipientTypeDetails", "FutureMailboxKind"));
        var raw = await LookedUp();
        Assert.Equal((label, RecipientLocationClass.Cloud), (raw.RecipientTypeLabel, raw.LocationClass));

        // No Exchange Online mailbox and cloud-only: no mailbox, and on-premises is not asked.
        _exchange.Mailboxes.Clear();
        _exchange.Calls.Clear();
        Recipient(type, synced: false);
        var cloudOnly = await LookedUp();
        Assert.Equal((label, RecipientLocationClass.NotApplicable), (cloudOnly.RecipientTypeLabel, cloudOnly.LocationClass));
        Assert.Equal(1, CloudMailboxReads());
        Assert.Empty(OnPremCalls());

        // No Exchange Online mailbox, synced: on-premises decides - its mailbox type names it; anything else does not.
        Recipient(type, synced: true);
        _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));
        var onPrem = await LookedUp();
        Assert.Equal(("Mailbox", RecipientLocationClass.OnPremises), (onPrem.RecipientTypeLabel, onPrem.LocationClass));

        _exchange.OnPremRecipients[Address] = Row(("RecipientTypeDetails", "MailUser"));
        var unsettled = await LookedUp();
        Assert.Equal((RecipientClassification.Undetermined, RecipientLocationClass.Undetermined), (unsettled.RecipientTypeLabel, unsettled.LocationClass));
    }

    // ---- T11: the address reaches on-premises as data -------------------------------------------

    // An apostrophe and a subexpression, carried by the primary address Exchange Online returned: spliced into the remote
    // script text, the first breaks it and the second runs Get-RemoteMailbox in the privileged session.
    [Fact]
    public async Task TheAddressReachesOnPremisesAsData_NeverAsScript()
    {
        const string tricky = "o'neil$(Get-RemoteMailbox probe)@contoso.test";
        Recipient("MailUser", synced: true, typed: "oneil", primary: tricky);
        _exchange.OnPremRecipients[tricky] = Row(("RecipientType", "MailUser"), ("RecipientTypeDetails", "MailUser"));

        var result = await LookedUp("oneil");

        Assert.Equal([$"OnPrem Get-Recipient {tricky}"], OnPremCalls());
        Assert.Equal(("Mail user", RecipientLocationClass.NotApplicable), (result.RecipientTypeLabel, result.LocationClass));
        Assert.Empty(result.Warnings);
    }

    // ---- T12: fewer reads -----------------------------------------------------------------------

    // The shared location read asked Exchange Online's Get-Mailbox a second time.
    [Fact]
    public async Task ACloudMailboxLookup_ReadsItsMailboxOnce()
    {
        Recipient("UserMailbox", synced: true);
        _exchange.Mailboxes[Address] = CloudMailbox(Address);

        await LookedUp();

        Assert.Equal(1, CloudMailboxReads());
        Assert.Equal(0, _exchange.OnPremSessions);
    }

    // And asked on-premises about a list, at a credential fetch and a session each time.
    [Fact]
    public async Task AListLookup_NeverReadsAMailbox_AndOpensNoOnPremisesSession()
    {
        Recipient("MailUniversalDistributionGroup", synced: true);

        await LookedUp();

        Assert.Equal(0, CloudMailboxReads());
        Assert.Empty(OnPremCalls());
        Assert.Equal(0, _exchange.OnPremSessions);
    }
}
