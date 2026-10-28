using System.Management.Automation;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// RecipientLookupAccuracy S1. Recipient Lookup's classifier alone - <c>docs/RecipientLookupAccuracy-Plan.md</c> 3.3's and
/// 3.4's tables read as data, and the rule of 3.1: a type is named only when Exchange reports it from the side that holds
/// the object; every disagreement and every side that could not be asked is "Could not be determined". The reads that feed
/// it are driven in <see cref="RecipientLookupClassificationTests"/>.
/// </summary>
/// <remarks>
/// The labels are written out here rather than read from the classifier's tables, so a label that changes, or one the
/// classifier invents, fails a test. Pure: no Exchange, no stand-in.
/// </remarks>
public sealed class RecipientClassificationTests
{
    private const string Address = "alice@contoso.test";

    private const string Unnamed = "FutureRecipientKind";

    private static readonly string[] CloudMailboxTypes =
        ["UserMailbox", "SharedMailbox", "RoomMailbox", "EquipmentMailbox", "SchedulingMailbox", "LinkedMailbox", "DiscoveryMailbox"];

    private static readonly string[] OnPremMailboxTypes =
        ["UserMailbox", "SharedMailbox", "RoomMailbox", "EquipmentMailbox", "LinkedMailbox", "LinkedRoomMailbox", "DiscoveryMailbox"];

    private static readonly string[] RemoteMailboxTypes =
        ["RemoteUserMailbox", "RemoteSharedMailbox", "RemoteRoomMailbox", "RemoteEquipmentMailbox", "RemoteTeamMailbox"];

    private static readonly string[] MailboxLabels =
        ["Mailbox", "Shared mailbox", "Room mailbox", "Equipment mailbox", "Scheduling mailbox", "Linked mailbox", "Discovery mailbox", "Linked room mailbox"];

    // T10's fixed list: every label the classifier may return, outside the raw-quoting form.
    private static readonly HashSet<string> Vocabulary = new(StringComparer.Ordinal)
    {
        "Mailbox", "Shared mailbox", "Room mailbox", "Equipment mailbox", "Scheduling mailbox", "Linked mailbox",
        "Discovery mailbox", "Linked room mailbox",
        "Microsoft 365 group", "Site mailbox", "Public folder mailbox",
        "System mailbox (ArbitrationMailbox)", "System mailbox (AuditLogMailbox)", "System mailbox (AuxAuditLogMailbox)",
        "System mailbox (MonitoringMailbox)", "System mailbox (SupervisoryReviewPolicyMailbox)",
        "Mail user", "Guest", "Contact", "Contact (another forest)", "Distribution list", "Mail-enabled security group",
        "Distribution group (non-universal)", "Dynamic distribution list", "Room list", "Mail-enabled public folder",
        "Exchange reported no type", "Could not be determined",
    };

    private static RecipientClassificationResult Classified(string? cloud, bool? synced, string mailbox = "NotAsked",
        string? mailboxType = null, string onPrem = "NotAsked", string? onPremType = null) =>
        RecipientClassification.Classify(new RecipientFacts(Address, cloud, synced, Enum.Parse<CloudMailboxAnswer>(mailbox),
            mailboxType, Enum.Parse<OnPremRecipientAnswer>(onPrem), onPremType));

    private static bool In(string[] set, string? word) => word is not null && set.Contains(word);

    private static bool? Synced(string state) => state switch
    {
        "synced" => true,
        "cloud-only" => false,
        _ => null,
    };

    // ---- 3.3: Exchange Online's own types ---------------------------------------------------------

    // The seven mailbox rows. Get-Mailbox is asked, on-premises never; found - or a read that failed, N1's own answer standing
    // (3.8, change 1) - is the label and "Cloud"; Exchange's not-found is D4.
    [Theory]
    [InlineData("UserMailbox", "Mailbox")]
    [InlineData("SharedMailbox", "Shared mailbox")]
    [InlineData("RoomMailbox", "Room mailbox")]
    [InlineData("EquipmentMailbox", "Equipment mailbox")]
    [InlineData("SchedulingMailbox", "Scheduling mailbox")]
    [InlineData("LinkedMailbox", "Linked mailbox")]
    [InlineData("DiscoveryMailbox", "Discovery mailbox")]
    public void AnExchangeOnlineMailboxType_IsNamedInTheCloud_UnlessGetMailboxFindsNone(string cloud, string label)
    {
        Assert.True(RecipientClassification.AsksExchangeOnlineMailbox(cloud));
        foreach (var answer in new[] { "Found", "Failed" })
        {
            var named = Classified(cloud, synced: true, mailbox: answer);
            Assert.Equal((label, RecipientLocationClass.Cloud, $"Exchange Online: {cloud}", (string?)null),
                (named.Label, named.Location, named.SmallPrint, named.Warning));
            Assert.False(RecipientClassification.AsksOnPremises(cloud, true, Enum.Parse<CloudMailboxAnswer>(answer)));
        }

        var none = Classified(cloud, synced: true, mailbox: "NotFound");
        Assert.Equal((RecipientClassification.Undetermined, RecipientLocationClass.Undetermined), (none.Label, none.Location));
        Assert.Equal($"Exchange Online: {cloud}; Get-Mailbox: no mailbox", none.SmallPrint);
        Assert.Equal($"Exchange Online reports {Address} as {cloud} but returned no mailbox for it.", none.Warning);
        Assert.False(RecipientClassification.AsksOnPremises(cloud, true, CloudMailboxAnswer.NotFound));
    }

    // A mailbox Exchange Online holds and this module does not read: named, "Cloud" (the class the rows read "Not read for
    // this recipient type" by), Get-Mailbox not asked (A5), on-premises never.
    [Theory]
    [InlineData("GroupMailbox", "Microsoft 365 group")]
    [InlineData("TeamMailbox", "Site mailbox")]
    [InlineData("PublicFolderMailbox", "Public folder mailbox")]
    [InlineData("ArbitrationMailbox", "System mailbox (ArbitrationMailbox)")]
    [InlineData("AuditLogMailbox", "System mailbox (AuditLogMailbox)")]
    [InlineData("AuxAuditLogMailbox", "System mailbox (AuxAuditLogMailbox)")]
    [InlineData("MonitoringMailbox", "System mailbox (MonitoringMailbox)")]
    [InlineData("SupervisoryReviewPolicyMailbox", "System mailbox (SupervisoryReviewPolicyMailbox)")]
    public void AMailboxExchangeOnlineHoldsAndThisModuleDoesNotRead_IsNamed_AndNotRead(string cloud, string label)
    {
        Assert.False(RecipientClassification.AsksExchangeOnlineMailbox(cloud));
        foreach (var state in new[] { "synced", "cloud-only", "not reported" })
        {
            Assert.False(RecipientClassification.AsksOnPremises(cloud, Synced(state), CloudMailboxAnswer.NotAsked));
            var named = Classified(cloud, Synced(state));
            Assert.Equal((label, RecipientLocationClass.ExchangeHeldNotRead, $"Exchange Online: {cloud}", (string?)null),
                (named.Label, named.Location, named.SmallPrint, named.Warning));
        }
        Assert.Equal("Cloud", RecipientClassification.LocationWord(RecipientLocationClass.ExchangeHeldNotRead));
    }

    // A recipient with no mailbox, synced or not: named, no mailbox, and neither Get-Mailbox nor on-premises is asked.
    [Theory]
    [InlineData("GuestMailUser", "Guest")]
    [InlineData("MailContact", "Contact")]
    [InlineData("MailForestContact", "Contact (another forest)")]
    [InlineData("MailUniversalDistributionGroup", "Distribution list")]
    [InlineData("MailUniversalSecurityGroup", "Mail-enabled security group")]
    [InlineData("MailNonUniversalGroup", "Distribution group (non-universal)")]
    [InlineData("DynamicDistributionGroup", "Dynamic distribution list")]
    [InlineData("RoomList", "Room list")]
    [InlineData("PublicFolder", "Mail-enabled public folder")]
    public void ARecipientWithNoMailbox_IsNamed_AndNothingMoreIsAsked(string cloud, string label)
    {
        Assert.False(RecipientClassification.AsksExchangeOnlineMailbox(cloud));
        foreach (var state in new[] { "synced", "cloud-only", "not reported" })
        {
            Assert.False(RecipientClassification.AsksOnPremises(cloud, Synced(state), CloudMailboxAnswer.NotAsked));
            var named = Classified(cloud, Synced(state));
            Assert.Equal((label, RecipientLocationClass.NotApplicable, $"Exchange Online: {cloud}", (string?)null),
                (named.Label, named.Location, named.SmallPrint, named.Warning));
        }
    }

    // ---- 3.4: a mail user, reconciled with on-premises ------------------------------------------

    // T4's rule. Exchange Online reports it cloud-only: nothing on-premises backs it, so on-premises is not asked.
    [Fact]
    public void ACloudOnlyMailUser_IsAMailUser_AndOnPremisesIsNotAsked()
    {
        Assert.False(RecipientClassification.AsksExchangeOnlineMailbox("MailUser"));
        Assert.False(RecipientClassification.AsksOnPremises("MailUser", false, CloudMailboxAnswer.NotAsked));

        var named = Classified("MailUser", synced: false);

        Assert.Equal(("Mail user", RecipientLocationClass.NotApplicable, "Exchange Online: MailUser", (string?)null),
            (named.Label, named.Location, named.SmallPrint, named.Warning));
    }

    // The owner's case, and its siblings: only on-premises Exchange's own mailbox types place a mailbox on-premises.
    [Theory]
    [InlineData("UserMailbox", "Mailbox")]
    [InlineData("SharedMailbox", "Shared mailbox")]
    [InlineData("RoomMailbox", "Room mailbox")]
    [InlineData("EquipmentMailbox", "Equipment mailbox")]
    [InlineData("LinkedMailbox", "Linked mailbox")]
    [InlineData("LinkedRoomMailbox", "Linked room mailbox")]
    [InlineData("DiscoveryMailbox", "Discovery mailbox")]
    public void ASyncedMailUserThatOnPremisesHoldsAsAMailbox_IsThatMailbox_OnPremises(string onPrem, string label)
    {
        foreach (var state in new[] { "synced", "not reported" })
        {
            Assert.True(RecipientClassification.AsksOnPremises("MailUser", Synced(state), CloudMailboxAnswer.NotAsked));
            var named = Classified("MailUser", Synced(state), onPrem: "Found", onPremType: onPrem);
            Assert.Equal((label, RecipientLocationClass.OnPremises, $"Exchange Online: MailUser; on-premises: {onPrem}", (string?)null),
                (named.Label, named.Location, named.SmallPrint, named.Warning));
        }
    }

    [Fact]
    public void ASyncedMailUserThatOnPremisesHoldsAsAMailUser_IsAMailUser()
    {
        var named = Classified("MailUser", synced: true, onPrem: "Found", onPremType: "MailUser");

        Assert.Equal(("Mail user", RecipientLocationClass.NotApplicable, "Exchange Online: MailUser; on-premises: MailUser", (string?)null),
            (named.Label, named.Location, named.SmallPrint, named.Warning));
    }

    // D1, D2, D3, U1 and U2: each "Could not be determined", the small print giving both sides' words, the warning saying why
    // - except U1, whose warning is the read's own (today's words, added where the read failed).
    [Theory]
    [InlineData("synced", "Found", "RemoteUserMailbox", "Exchange Online: MailUser; on-premises: RemoteUserMailbox",
        "Exchange Online and on-premises Exchange disagree about alice@contoso.test: on-premises Exchange holds it as RemoteUserMailbox, a mailbox in Exchange Online, and Exchange Online holds no mailbox for it (MailUser).")]
    [InlineData("synced", "Found", "RemoteTeamMailbox", "Exchange Online: MailUser; on-premises: RemoteTeamMailbox",
        "Exchange Online and on-premises Exchange disagree about alice@contoso.test: on-premises Exchange holds it as RemoteTeamMailbox, a mailbox in Exchange Online, and Exchange Online holds no mailbox for it (MailUser).")]
    [InlineData("not reported", "Found", "MailContact", "Exchange Online: MailUser; on-premises: MailContact",
        "Exchange Online and on-premises Exchange disagree about alice@contoso.test: Exchange Online holds it as MailUser, on-premises Exchange as MailContact.")]
    [InlineData("synced", "Found", Unnamed, "Exchange Online: MailUser; on-premises: FutureRecipientKind",
        "Exchange Online and on-premises Exchange disagree about alice@contoso.test: Exchange Online holds it as MailUser, on-premises Exchange as FutureRecipientKind.")]
    [InlineData("synced", "Found", null, "Exchange Online: MailUser; on-premises: no type",
        "Exchange Online and on-premises Exchange disagree about alice@contoso.test: Exchange Online holds it as MailUser, and on-premises Exchange reports no type for it.")]
    [InlineData("synced", "NotFound", null, "Exchange Online: MailUser; on-premises: no such recipient",
        "On-premises Exchange holds no recipient at alice@contoso.test, though Exchange Online reports it as synced from an on-premises directory; it may be held by a directory this module's on-premises server does not serve.")]
    [InlineData("not reported", "NotFound", null, "Exchange Online: MailUser; on-premises: no such recipient",
        "On-premises Exchange holds no recipient at alice@contoso.test, though Exchange Online did not report whether it is synced; it may be held by a directory this module's on-premises server does not serve.")]
    [InlineData("synced", "CouldNotBeAsked", null, "Exchange Online: MailUser; on-premises: not read", null)]
    [InlineData("synced", "MoreThanOne", null, "Exchange Online: MailUser; on-premises: more than one recipient",
        "On-premises Exchange returned more than one recipient for alice@contoso.test.")]
    public void AMailUserTheSidesDoNotSettle_CouldNotBeDetermined(string state, string onPrem, string? onPremType, string smallPrint, string? warning)
    {
        var unsettled = Classified("MailUser", Synced(state), onPrem: onPrem, onPremType: onPremType);

        Assert.Equal((RecipientClassification.Undetermined, RecipientLocationClass.Undetermined, smallPrint, warning),
            (unsettled.Label, unsettled.Location, unsettled.SmallPrint, unsettled.Warning));
    }

    // ---- 3.3: a type this app does not name, or none (T9) ----------------------------------------

    [Theory]
    [InlineData(Unnamed, "Not a type this app names: FutureRecipientKind")]
    [InlineData(null, "Exchange reported no type")]
    [InlineData("  ", "Exchange reported no type")]
    public void AnUnnamedType_IsShownRaw_AndGetMailboxAndThenOnPremisesDecide(string? cloud, string label)
    {
        var word = string.IsNullOrWhiteSpace(cloud) ? "no type" : cloud;
        Assert.True(RecipientClassification.AsksExchangeOnlineMailbox(cloud));

        // Get-Mailbox found a mailbox whose own type the table holds: named from that, never from the unknown word.
        var named = Classified(cloud, synced: true, mailbox: "Found", mailboxType: "SharedMailbox");
        Assert.Equal(("Shared mailbox", RecipientLocationClass.Cloud, $"Exchange Online: {word}; Get-Mailbox: SharedMailbox"),
            (named.Label, named.Location, named.SmallPrint));

        // ... and one whose own type it does not: "Cloud", the label raw.
        var raw = Classified(cloud, synced: true, mailbox: "Found", mailboxType: Unnamed);
        Assert.Equal((label, RecipientLocationClass.Cloud), (raw.Label, raw.Location));

        // No Exchange Online mailbox, and cloud-only: nothing on-premises behind it.
        Assert.False(RecipientClassification.AsksOnPremises(cloud, false, CloudMailboxAnswer.NotFound));
        var cloudOnly = Classified(cloud, synced: false, mailbox: "NotFound");
        Assert.Equal((label, RecipientLocationClass.NotApplicable, $"Exchange Online: {word}; Get-Mailbox: no mailbox", (string?)null),
            (cloudOnly.Label, cloudOnly.Location, cloudOnly.SmallPrint, cloudOnly.Warning));

        // No Exchange Online mailbox, synced or not reported: on-premises is asked, and only its mailbox types settle it.
        foreach (var state in new[] { "synced", "not reported" })
        {
            Assert.True(RecipientClassification.AsksOnPremises(cloud, Synced(state), CloudMailboxAnswer.NotFound));
            var onPrem = Classified(cloud, Synced(state), mailbox: "NotFound", onPrem: "Found", onPremType: "UserMailbox");
            Assert.Equal(("Mailbox", RecipientLocationClass.OnPremises), (onPrem.Label, onPrem.Location));
            Assert.Equal($"Exchange Online: {word}; Get-Mailbox: no mailbox; on-premises: UserMailbox", onPrem.SmallPrint);

            foreach (var other in new[] { "MailUser", "RemoteUserMailbox", Unnamed })
            {
                var unsettled = Classified(cloud, Synced(state), mailbox: "NotFound", onPrem: "Found", onPremType: other);
                Assert.Equal((RecipientClassification.Undetermined, RecipientLocationClass.Undetermined), (unsettled.Label, unsettled.Location));
                Assert.Contains(other, unsettled.Warning, StringComparison.Ordinal);
            }
        }

        // Get-Mailbox failed: what it is is not settled, and on-premises is not asked.
        Assert.False(RecipientClassification.AsksOnPremises(cloud, true, CloudMailboxAnswer.Failed));
        var unread = Classified(cloud, synced: true, mailbox: "Failed");
        Assert.Equal((RecipientClassification.Undetermined, RecipientLocationClass.Undetermined, $"Exchange Online: {word}; Get-Mailbox: not read"),
            (unread.Label, unread.Location, unread.SmallPrint));
        Assert.EndsWith("its mailbox could not be read.", unread.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnnamedTypesWarnings_SayWhatEachSideReported()
    {
        Assert.Equal(
            "Exchange Online reports alice@contoso.test as FutureRecipientKind, a type this app does not name, and holds no mailbox for it; on-premises Exchange holds it as MailUser.",
            Classified(Unnamed, true, mailbox: "NotFound", onPrem: "Found", onPremType: "MailUser").Warning);
        Assert.Equal(
            "Exchange Online reported no type for alice@contoso.test and holds no mailbox for it; on-premises Exchange reports no type for it.",
            Classified(null, true, mailbox: "NotFound", onPrem: "Found", onPremType: null).Warning);
        Assert.Equal(
            "Exchange Online reports alice@contoso.test as FutureRecipientKind, a type this app does not name, and its mailbox could not be read.",
            Classified(Unnamed, true, mailbox: "Failed").Warning);
        Assert.Equal(
            "Exchange Online reported no type for alice@contoso.test, and its mailbox could not be read.",
            Classified(null, true, mailbox: "Failed").Warning);
    }

    // ---- T10: the vocabulary is closed --------------------------------------------------------------

    // Over every table value, an unknown word and none on each side, by each sync state and each read outcome: every label
    // is on the fixed list or is the raw-quoting form of the word Exchange Online gave; no mailbox label appears unless a side
    // that holds the object reported a mailbox type; and only on-premises Exchange's own mailbox types place it on-premises.
    [Fact]
    public void TheVocabularyIsClosed_AndAMailboxIsNamedOnlyFromTheSideThatHoldsIt()
    {
        string?[] cloudTypes =
        [
            .. CloudMailboxTypes, "GroupMailbox", "TeamMailbox", "PublicFolderMailbox", "ArbitrationMailbox", "AuditLogMailbox",
            "AuxAuditLogMailbox", "MonitoringMailbox", "SupervisoryReviewPolicyMailbox", "GuestMailUser", "MailContact",
            "MailForestContact", "MailUniversalDistributionGroup", "MailUniversalSecurityGroup", "MailNonUniversalGroup",
            "DynamicDistributionGroup", "RoomList", "PublicFolder", "MailUser", "RemoteUserMailbox", "LinkedRoomMailbox", Unnamed, null,
        ];
        string?[] mailboxTypes = [null, "UserMailbox", "SharedMailbox", "MailUser", "GroupMailbox", Unnamed];
        string?[] onPremTypes = [null, .. OnPremMailboxTypes, .. RemoteMailboxTypes, "MailUser", "MailContact", "MailUniversalDistributionGroup", Unnamed];
        var syncStates = new bool?[] { true, false, null };
        var combinations = 0;

        foreach (var cloud in cloudTypes)
            foreach (var synced in syncStates)
                foreach (var mailbox in Enum.GetValues<CloudMailboxAnswer>())
                    foreach (var mailboxType in mailboxTypes)
                        foreach (var onPrem in Enum.GetValues<OnPremRecipientAnswer>())
                            foreach (var onPremType in onPremTypes)
                            {
                                combinations++;
                                var facts = new RecipientFacts(Address, cloud, synced, mailbox, mailboxType, onPrem, onPremType);
                                var named = RecipientClassification.Classify(facts);
                                var because = $"{facts} -> {named}";

                                Assert.True(Vocabulary.Contains(named.Label) || named.Label == "Not a type this app names: " + cloud, because);
                                Assert.Equal(named.Label == RecipientClassification.Undetermined, named.Location == RecipientLocationClass.Undetermined);
                                Assert.Equal(named.Location == RecipientLocationClass.NotApplicable, named.LocationNote is not null); // S2
                                Assert.StartsWith($"Exchange Online: {cloud ?? "no type"}", named.SmallPrint, StringComparison.Ordinal);

                                if (MailboxLabels.Contains(named.Label))
                                {
                                    var fromTheCloud = named.Location == RecipientLocationClass.Cloud
                                        && (In(CloudMailboxTypes, cloud)
                                            || (mailbox == CloudMailboxAnswer.Found && In(CloudMailboxTypes, mailboxType)));
                                    var fromOnPremises = named.Location == RecipientLocationClass.OnPremises
                                        && onPrem == OnPremRecipientAnswer.Found && In(OnPremMailboxTypes, onPremType);
                                    Assert.True(fromTheCloud || fromOnPremises, because);
                                }

                                if (named.Location == RecipientLocationClass.OnPremises)
                                {
                                    Assert.True(onPrem == OnPremRecipientAnswer.Found && In(OnPremMailboxTypes, onPremType), because);
                                    Assert.NotEqual(false, synced);
                                }
                            }

        Assert.True(combinations > 50_000, $"{combinations} combinations");
    }

    // ---- 3.2, N1: IsDirSynced ----------------------------------------------------------------------

    public static TheoryData<object?, bool?> SyncStates => new()
    {
        { true, true },
        { false, false },
        { "True", true },
        { " false ", false },
        { new PSObject(true), true },
        { new PSObject("False"), false },
        { null, null },
        { "", null },
        { "yes", null },
        { 1, null },
    };

    [Theory]
    [MemberData(nameof(SyncStates))]
    public void IsDirSynced_IsTrueFalseOrNotReported(object? reported, bool? read) =>
        Assert.Equal(read, RecipientClassification.ReadIsDirSynced(reported));
}
