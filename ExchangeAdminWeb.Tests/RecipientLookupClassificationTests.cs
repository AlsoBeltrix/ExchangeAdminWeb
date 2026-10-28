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

    internal static string Rendered(RecipientInfoResult result) => Rendered(result, withClasses: false);

    /// <summary>S2, T18. The render text with each class attribute in brackets before the content it styles.</summary>
    internal static string RenderedWithClasses(RecipientInfoResult result) => Rendered(result, withClasses: true);

    /// <summary>S2. Each table row's label and the text of its value, up to the value's first element.</summary>
    internal static Dictionary<string, string> RowsOf(string page) =>
        System.Text.RegularExpressions.Regex.Matches(page, "<td class=\"fw-bold\"[^>]*>(?<label>[^<]+)</td>(?<value>[^<]*)")
            .ToDictionary(match => match.Groups["label"].Value, match => match.Groups["value"].Value.Trim(), StringComparer.Ordinal);

#pragma warning disable BL0006 // the page's compiled render tree is read directly; no renderer is involved
    private static string Rendered(RecipientInfoResult result, bool withClasses)
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
                RenderTreeFrameType.Attribute when withClasses && frame.AttributeName == "class" => $"[class={frame.AttributeValue}]",
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

    // ---- S2: "Not applicable", "Could not be determined", and the rows ---------------------------

    // Every row that comes from the mailbox, in the page's order, that every kind shows.
    private static readonly string[] MailboxRows =
        ["Last Logon", "Mailbox Size", "Deleted Item Size", "Total Size", "Item Count", "Deleted Items", "Archive", "Forwarding"];

    // T13, the owner's second case: "a DL shows Mailbox Location Unknown". A list has no mailbox: "Not applicable", saying
    // so, and every mailbox row the same - never "Unknown", "Disabled", "None", or a unit after a missing number.
    [Fact]
    public async Task ADistributionList_IsNotApplicable_AndSoIsEveryMailboxRow()
    {
        Recipient("MailUniversalDistributionGroup", synced: true);

        var result = await LookedUp();

        Assert.Equal(("Not applicable", "A distribution list has no mailbox."), (result.MailboxLocation, result.LocationNote));
        var page = Rendered(result);
        var rows = RowsOf(page);
        Assert.Equal("Not applicable", rows["Mailbox Location"]);
        Assert.Contains("A distribution list has no mailbox.", page, StringComparison.Ordinal);
        foreach (var row in MailboxRows)
            Assert.Equal((row, "Not applicable"), (row, rows[row]));
        Assert.False(rows.ContainsKey("Archive Size"));
        // No row's value states an absence, and no size carries a unit after a missing number (E3: by value, now that a row
        // is labelled "Account Disabled").
        foreach (var value in rows.Values)
            Assert.DoesNotContain(value, new[] { "Unknown", "Disabled", "None" });
        foreach (var stated in new[] { "Unknown", "N/A GB" })
            Assert.DoesNotContain(stated, page, StringComparison.Ordinal);
    }

    // The note says what kind of recipient has no mailbox; a mail user's says where its mail goes instead.
    [Theory]
    [InlineData("MailUniversalSecurityGroup", "synced", "A mail-enabled security group has no mailbox.")]
    [InlineData("DynamicDistributionGroup", "cloud-only", "A dynamic distribution list has no mailbox.")]
    [InlineData("RoomList", "synced", "A room list has no mailbox.")]
    [InlineData("MailContact", "cloud-only", "A contact has no mailbox.")]
    [InlineData("MailForestContact", "synced", "A contact (another forest) has no mailbox.")]
    [InlineData("GuestMailUser", "cloud-only", "A guest has no mailbox.")]
    [InlineData("PublicFolder", "synced", "A mail-enabled public folder has no mailbox.")]
    [InlineData("MailUser", "cloud-only", "A mail user has no mailbox. Its mail goes to another address.")]
    public async Task EachKindWithNoMailbox_SaysSo(string type, string state, string note)
    {
        Recipient(type, Synced(state));

        var result = await LookedUp();

        Assert.Equal(("Not applicable", note), (result.MailboxLocation, result.LocationNote));
        Assert.Contains(note, Rendered(result), StringComparison.Ordinal);
    }

    // T14. Only the size and the archive are read on-premises; every other row says so, where it read "None" and "N/A".
    [Fact]
    public async Task AnOnPremisesMailbox_SaysWhatItDoesNotRead_AndShowsWhatItDoes()
    {
        Recipient("MailUser", synced: true);
        _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));

        var result = await LookedUp();

        var page = Rendered(result);
        var rows = RowsOf(page);
        foreach (var row in new[] { "Last Logon", "Deleted Item Size", "Total Size", "Item Count", "Deleted Items", "Forwarding", "Archive Deleted", "Archive Items" })
            Assert.Equal((row, "Not read for on-premises mailboxes"), (row, rows[row]));
        Assert.Equal(("2.50 GB", "Enabled", "0.50 GB"), (rows["Mailbox Size"], rows["Archive"], rows["Archive Size"]));
        Assert.DoesNotContain("N/A", page[page.IndexOf("Mailbox Size</td>", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.DoesNotContain("Forwarding</td>None", page, StringComparison.Ordinal);
    }

    // S2. A size the on-premises read could not read is "Could not be read", beside its warning - not "N/A".
    [Fact]
    public async Task AnOnPremisesSizeThatCouldNotBeRead_CouldNotBeRead()
    {
        Recipient("MailUser", synced: true);
        _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));
        _exchange.Behaviour["OnPremMailboxSize"] = "Unlimited";

        var result = await LookedUp();

        Assert.Equal("Could not be read", RowsOf(Rendered(result))["Mailbox Size"]);
        Assert.Contains(result.Warnings, warning => warning.StartsWith("The on-premises mailbox size could not be read", StringComparison.Ordinal));
    }

    // T15. The two sides did not settle it: both rows say so, and nothing about a mailbox was asked.
    [Fact]
    public async Task AnUndeterminedRecipient_CouldNotBeDetermined_AndItsMailboxRowsWereNotRead()
    {
        Recipient("MailUser", synced: true);

        var result = await LookedUp();

        var rows = RowsOf(Rendered(result));
        Assert.Equal(("Could not be determined", "Could not be determined"), (rows["Recipient Type"], rows["Mailbox Location"]));
        foreach (var row in MailboxRows)
            Assert.Equal((row, "Not read"), (row, rows[row]));
    }

    // T16. A missing part counted as 0 made an on-premises mailbox's size alone read as its total.
    [Theory]
    [InlineData(1.5, 0.25, "1.75 GB")]
    [InlineData(1.5, null, "N/A")]
    [InlineData(null, 0.25, "N/A")]
    public void TotalSize_IsTheSumOnlyWhenBothSizesWereRead(double? mailbox, double? deleted, string shown)
    {
        var result = new RecipientInfoResult
        {
            EmailAddress = Address,
            LocationClass = RecipientLocationClass.Cloud,
            MailboxLocation = "Cloud",
            MailboxSizeGB = mailbox,
            DeletedItemSizeGB = deleted,
        };

        Assert.Equal(shown, RowsOf(Rendered(result))["Total Size"]);
    }

    // S2 (AC5). A cloud mailbox whose statistics read failed: its figures were not read, so they are not "N/A".
    [Fact]
    public async Task ACloudMailboxWhoseStatisticsCouldNotBeRead_SaysSo()
    {
        Recipient("UserMailbox", synced: false);
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
        _exchange.Failures[$"Get-MailboxStatistics {Address}"] = Refused("the mailbox database is offline");

        var result = await LookedUp();

        var rows = RowsOf(Rendered(result));
        foreach (var row in new[] { "Last Logon", "Mailbox Size", "Deleted Item Size", "Total Size", "Item Count", "Deleted Items" })
            Assert.Equal((row, "Could not be read"), (row, rows[row]));
        Assert.Equal("None", rows["Forwarding"]);
    }

    // S2 (3.8, change 1). A cloud mailbox whose Get-Mailbox failed: every mailbox row could not be read.
    [Fact]
    public async Task ACloudMailboxWhoseMailboxReadFailed_CouldNotBeReadInEveryMailboxRow()
    {
        Recipient("UserMailbox", synced: false);
        _exchange.Failures[$"Get-Mailbox {Address}"] = Refused("the directory is busy");

        var result = await LookedUp();

        var rows = RowsOf(Rendered(result));
        Assert.Equal("Cloud", rows["Mailbox Location"]);
        foreach (var row in MailboxRows)
            Assert.Equal((row, "Could not be read"), (row, rows[row]));
    }

    // T17. Exchange Online holds a Microsoft 365 group's mailbox, and this module does not read it.
    [Fact]
    public async Task AMicrosoft365Group_IsInTheCloud_ItsMailboxNotReadForItsType()
    {
        Recipient("GroupMailbox", synced: false);

        var result = await LookedUp();

        var rows = RowsOf(Rendered(result));
        Assert.Equal(("Microsoft 365 group", "Cloud"), (rows["Recipient Type"], rows["Mailbox Location"]));
        foreach (var row in MailboxRows)
            Assert.Equal((row, "Not read for this recipient type"), (row, rows[row]));
    }

    // ---- S3: Primary Email, and the reads bound to it -------------------------------------------

    private const string Typed = "alias@contoso.test";

    // T19. It showed whatever was typed - another of the recipient's addresses, an alias, a name - as its primary address.
    [Theory]
    [InlineData(Typed)]
    [InlineData("Alice")]
    public async Task ARecipientTypedByAnotherName_ShowsItsPrimaryAddress_AndWhatWasTyped(string typed)
    {
        Recipient("UserMailbox", synced: false, typed: typed);
        _exchange.Mailboxes[Address] = CloudMailbox(Address);

        var result = await LookedUp(typed);

        Assert.Equal($"{Address} (looked up as {typed})", RowsOf(Rendered(result))["Primary Email"]);
    }

    // What was typed, in another case, is the same address: nothing follows it.
    [Fact]
    public async Task ThePrimaryAddressTypedInAnotherCase_IsShownAlone()
    {
        Recipient("UserMailbox", synced: false, typed: "ALICE@contoso.test");
        _exchange.Mailboxes[Address] = CloudMailbox(Address);

        var result = await LookedUp("ALICE@contoso.test");

        Assert.Equal(Address, RowsOf(Rendered(result))["Primary Email"]);
    }

    // T20. Every later read asks about the object Get-Recipient found, not about what was typed: Exchange Online's mailbox and
    // both statistics reads, and on-premises Exchange's recipient and size reads.
    [Theory]
    [InlineData("in the cloud")]
    [InlineData("on-premises")]
    public async Task EveryLaterRead_AsksAboutThePrimaryAddress(string held)
    {
        if (held == "in the cloud")
        {
            Recipient("UserMailbox", synced: false, typed: Typed);
            _exchange.Mailboxes[Address] = CloudMailbox(Address);
        }
        else
        {
            Recipient("MailUser", synced: true, typed: Typed);
            _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));
        }

        var result = await LookedUp(Typed);

        Assert.Null(result.Error);
        Assert.Empty(result.Warnings);
        var calls = _exchange.CallList();
        Assert.Equal($"Get-Recipient {Typed}", calls[0]);
        Assert.All(calls.Skip(1), call => Assert.EndsWith(" " + Address, call, StringComparison.Ordinal));
        string[] later = held == "in the cloud"
            ? [$"Get-Mailbox {Address}", $"Get-MailboxStatistics {Address}", $"Get-MailboxStatistics -Archive {Address}"]
            : [$"OnPrem Get-Recipient {Address}", $"OnPrem Get-MailboxStatistics {Address}", $"OnPrem Get-MailboxStatistics -Archive {Address}"];
        Assert.Equal(later, calls.Skip(1).ToArray());
    }

    // T21. Exchange reported no primary address: the typed text is shown as such, and the reads ask about it.
    [Fact]
    public async Task NoPrimaryAddressReported_TheTypedTextIsShownAsTyped_AndTheReadsUseIt()
    {
        Recipient("UserMailbox", synced: false, typed: Typed, primary: null);
        _exchange.Mailboxes[Typed] = CloudMailbox(Typed);

        var result = await LookedUp(Typed);

        Assert.Equal($"{Typed} (as typed; Exchange reported no primary address)", RowsOf(Rendered(result))["Primary Email"]);
        Assert.All(_exchange.CallList(), call => Assert.EndsWith(" " + Typed, call, StringComparison.Ordinal));
        Assert.Equal(RecipientLocationClass.Cloud, result.LocationClass);
    }

    // ---- S4: Created ----------------------------------------------------------------------------

    // T22. A synced recipient's Exchange Online WhenCreated is its copy's, and says so; a cloud-only one's, and one whose sync
    // state was not reported, read as before (RecipientLookupReportedTimeTests pins the time itself, unchanged).
    [Theory]
    [InlineData("synced", "2026-10-01 20:15 (UTC, UTC+00:00) (Exchange Online's copy)")]
    [InlineData("cloud-only", "2026-10-01 20:15 (UTC, UTC+00:00)")]
    [InlineData("not reported", "2026-10-01 20:15 (UTC, UTC+00:00)")]
    public async Task Created_SaysWhenItIsExchangeOnlinesCopy(string state, string shown)
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "MailUniversalDistributionGroup"),
            ("PrimarySmtpAddress", Address), ("WhenCreated", new DateTime(2026, 10, 1, 20, 15, 0, DateTimeKind.Utc)));
        if (Synced(state) is { } synced)
            ((System.Management.Automation.PSObject)_exchange.Recipients[Address]!).Properties.Add(new System.Management.Automation.PSNoteProperty("IsDirSynced", synced));

        var result = await LookedUp();

        Assert.Equal(shown, RowsOf(Rendered(result))["Created"]);
    }

    // Nothing reported is "N/A", with no note after it.
    [Fact]
    public async Task ASyncedRecipientWithNoCreationTime_IsNA_WithNoNote()
    {
        Recipient("MailUniversalDistributionGroup", synced: true);

        var result = await LookedUp();

        Assert.Equal("N/A", RowsOf(Rendered(result))["Created"]);
    }

    // T18. "Not applicable" is the answer, not a warning; "Could not be determined" keeps the warning colour.
    [Fact]
    public async Task NotApplicable_IsNotInTheWarningColour_ButCouldNotBeDetermined_Is()
    {
        Recipient("MailUniversalDistributionGroup", synced: true);
        var list = RenderedWithClasses(await LookedUp());
        Assert.Contains("[class=badge bg-light text-dark border]Not applicable", list, StringComparison.Ordinal);
        Assert.DoesNotContain("bg-warning", list, StringComparison.Ordinal);

        Recipient("MailUser", synced: true);
        var unsettled = RenderedWithClasses(await LookedUp());
        Assert.Contains("[class=badge bg-warning]Could not be determined", unsettled, StringComparison.Ordinal);
    }
}
