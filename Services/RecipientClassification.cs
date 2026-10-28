using System.Management.Automation;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

/// <summary>What Exchange Online's Get-Mailbox answered for the recipient Recipient Lookup found (the plan's N2).</summary>
internal enum CloudMailboxAnswer
{
    /// <summary>Not asked: the recipient's Exchange Online type already says whether it has a mailbox there.</summary>
    NotAsked,
    Found,
    /// <summary>Exchange's own not-found, read by structure: no Exchange Online mailbox.</summary>
    NotFound,
    /// <summary>Any other failure: the mailbox could not be read.</summary>
    Failed,
}

/// <summary>What on-premises Exchange's Get-Recipient answered (the plan's N4).</summary>
internal enum OnPremRecipientAnswer
{
    NotAsked,
    Found,
    /// <summary>Exchange's own not-found, read by structure: no such recipient on-premises.</summary>
    NotFound,
    MoreThanOne,
    /// <summary>Not configured, no credentials, or a session or read that failed.</summary>
    CouldNotBeAsked,
}

/// <summary>Everything the classification reads: each side's answer about one recipient.</summary>
/// <param name="Address">The address the on-premises read asked about, for the warnings.</param>
/// <param name="CloudType">Exchange Online's <c>RecipientTypeDetails</c> from Get-Recipient (N1).</param>
/// <param name="IsDirSynced">Exchange Online's <c>IsDirSynced</c>; null when it was not reported.</param>
/// <param name="MailboxType">Exchange Online Get-Mailbox's own <c>RecipientTypeDetails</c>, when it found one.</param>
/// <param name="OnPremType">On-premises Get-Recipient's <c>RecipientTypeDetails</c>, when it found one.</param>
internal sealed record RecipientFacts(
    string Address,
    string? CloudType,
    bool? IsDirSynced,
    CloudMailboxAnswer Mailbox,
    string? MailboxType,
    OnPremRecipientAnswer OnPrem,
    string? OnPremType);

/// <summary>What the recipient is, as Recipient Lookup shows it.</summary>
/// <param name="Label">The plain type: a label of the tables, the raw-quoting form, or "Could not be determined".</param>
/// <param name="SmallPrint">Exchange's own words from each side that answered, shown under the label.</param>
/// <param name="Warning">
/// Why it could not be determined, for the warning box; null when it was, and for a side that could not be asked, whose
/// read's catch already gives today's words ("Could not determine mailbox location: ...").
/// </param>
/// <param name="LocationNote">S2. Why there is no mailbox, under "Not applicable"; null for every other location.</param>
internal sealed record RecipientClassificationResult(
    string Label,
    RecipientLocationClass Location,
    string SmallPrint,
    string? Warning,
    string? LocationNote = null);

/// <summary>
/// RecipientLookupAccuracy S1. What a looked-up recipient is, and where its mailbox is, from Exchange's own answers:
/// Exchange Online's Get-Recipient, its Get-Mailbox when asked, and on-premises Exchange's Get-Recipient when asked
/// (<c>docs/RecipientLookupAccuracy-Plan.md</c> 3.3 and 3.4). The rule (3.1): a type is named only when Exchange reports
/// it, from the side that holds the object; when the two sides disagree, or a side that has to be asked cannot be, it is
/// "Could not be determined", and why. Nothing is inferred from a hint, and a type outside the tables is shown as
/// Exchange gave it, never mapped to the nearest label.
/// </summary>
/// <remarks>
/// Pure: the tables and the reconciliation, with no read of its own. ASSUMPTION, unverified (no live call; the plan's A8):
/// the tables hold Exchange's documented <c>RecipientTypeDetails</c> names, from knowledge. A value outside them is shown
/// raw - visible, never a guess. Exchange's product vocabulary only: nothing here names an environment (Architectural
/// Invariant 7), and an on-premises not-found is undetermined precisely because the recipient may be held by a directory
/// the configured server does not serve.
/// </remarks>
internal static class RecipientClassification
{
    internal const string Undetermined = "Could not be determined";

    internal const string NoTypeReported = "Exchange reported no type";

    internal const string UnnamedTypePrefix = "Not a type this app names: ";

    internal const string MailUserLabel = "Mail user";

    /// <summary>S2 (3.5): a mail user's "Not applicable" note.</summary>
    internal const string MailUserNote = "A mail user has no mailbox. Its mail goes to another address.";

    /// <summary>S2: the note for a type the tables do not hold that Exchange Online reports as cloud-only, with no mailbox.</summary>
    internal const string CloudOnlyNoMailboxNote = "Exchange Online holds no mailbox for it and reports it as cloud-only.";

    private const string MailUser = "MailUser";

    /// <summary>3.3: the Exchange Online mailbox types Get-Mailbox returns. Named from Get-Recipient, corroborated by Get-Mailbox.</summary>
    internal static readonly IReadOnlyDictionary<string, string> CloudMailboxes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["UserMailbox"] = "Mailbox",
        ["SharedMailbox"] = "Shared mailbox",
        ["RoomMailbox"] = "Room mailbox",
        ["EquipmentMailbox"] = "Equipment mailbox",
        ["SchedulingMailbox"] = "Scheduling mailbox",
        ["LinkedMailbox"] = "Linked mailbox",
        ["DiscoveryMailbox"] = "Discovery mailbox",
    };

    /// <summary>
    /// 3.3: mailboxes Exchange Online holds and this module does not read; Get-Mailbox, as the module asks it, does not
    /// return them (A5 for a group).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> CloudHeldNotRead = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GroupMailbox"] = "Microsoft 365 group",
        ["TeamMailbox"] = "Site mailbox",
        ["PublicFolderMailbox"] = "Public folder mailbox",
        ["ArbitrationMailbox"] = "System mailbox (ArbitrationMailbox)",
        ["AuditLogMailbox"] = "System mailbox (AuditLogMailbox)",
        ["AuxAuditLogMailbox"] = "System mailbox (AuxAuditLogMailbox)",
        ["MonitoringMailbox"] = "System mailbox (MonitoringMailbox)",
        ["SupervisoryReviewPolicyMailbox"] = "System mailbox (SupervisoryReviewPolicyMailbox)",
    };

    /// <summary>3.3: recipients that have no mailbox, by Exchange Online's own type.</summary>
    internal static readonly IReadOnlyDictionary<string, string> NoMailbox = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GuestMailUser"] = "Guest",
        ["MailContact"] = "Contact",
        ["MailForestContact"] = "Contact (another forest)",
        ["MailUniversalDistributionGroup"] = "Distribution list",
        ["MailUniversalSecurityGroup"] = "Mail-enabled security group",
        ["MailNonUniversalGroup"] = "Distribution group (non-universal)",
        ["DynamicDistributionGroup"] = "Dynamic distribution list",
        ["RoomList"] = "Room list",
        ["PublicFolder"] = "Mail-enabled public folder",
    };

    /// <summary>3.4: on-premises Exchange's mailbox types. Only these place a mailbox on-premises.</summary>
    internal static readonly IReadOnlyDictionary<string, string> OnPremMailboxes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["UserMailbox"] = "Mailbox",
        ["SharedMailbox"] = "Shared mailbox",
        ["RoomMailbox"] = "Room mailbox",
        ["EquipmentMailbox"] = "Equipment mailbox",
        ["LinkedMailbox"] = "Linked mailbox",
        ["LinkedRoomMailbox"] = "Linked room mailbox",
        ["DiscoveryMailbox"] = "Discovery mailbox",
    };

    /// <summary>3.4 (D1): on-premises Exchange's remote mailboxes - a mailbox it says Exchange Online holds.</summary>
    internal static readonly IReadOnlySet<string> OnPremRemoteMailboxes = new HashSet<string>(StringComparer.Ordinal)
    {
        "RemoteUserMailbox", "RemoteSharedMailbox", "RemoteRoomMailbox", "RemoteEquipmentMailbox", "RemoteTeamMailbox",
    };

    /// <summary>
    /// 3.2, N2: Exchange Online's Get-Mailbox is asked for a mailbox type it returns, a type the tables do not hold, or none.
    /// For a list, contact, guest, mail user, Microsoft 365 group or public folder its answer is known before it is made.
    /// </summary>
    internal static bool AsksExchangeOnlineMailbox(string? cloudType) =>
        Word(cloudType) is not { } cloud || CloudMailboxes.ContainsKey(cloud) || !Named(cloud);

    /// <summary>
    /// 3.2, N4: on-premises Exchange is asked only about a mail user, or a type the tables do not hold that Exchange Online
    /// holds no mailbox for - and never about one Exchange Online reports as cloud-only, which nothing on-premises backs.
    /// </summary>
    internal static bool AsksOnPremises(string? cloudType, bool? isDirSynced, CloudMailboxAnswer mailbox)
    {
        if (isDirSynced == false)
            return false;
        var cloud = Word(cloudType);
        if (cloud == MailUser)
            return true;
        return (cloud is null || !Named(cloud)) && mailbox == CloudMailboxAnswer.NotFound;
    }

    internal static RecipientClassificationResult Classify(RecipientFacts facts)
    {
        var cloud = Word(facts.CloudType);
        var cloudPrint = $"Exchange Online: {cloud ?? "no type"}";

        if (cloud is not null && CloudMailboxes.TryGetValue(cloud, out var mailbox))
        {
            // Corroborated by N2: found stands; a failed read leaves N1's own answer standing (Exchange Online typed it as a
            // mailbox of its own, 3.8 change 1); Exchange's not-found is a disagreement (D4).
            return facts.Mailbox == CloudMailboxAnswer.NotFound
                ? Unsettled($"{cloudPrint}; Get-Mailbox: no mailbox",
                    $"Exchange Online reports {facts.Address} as {cloud} but returned no mailbox for it.")
                : new(mailbox, RecipientLocationClass.Cloud, cloudPrint, null);
        }

        if (cloud is not null && CloudHeldNotRead.TryGetValue(cloud, out var held))
            return new(held, RecipientLocationClass.ExchangeHeldNotRead, cloudPrint, null);

        if (cloud is not null && NoMailbox.TryGetValue(cloud, out var none))
            return new(none, RecipientLocationClass.NotApplicable, cloudPrint, null, $"A {char.ToLowerInvariant(none[0])}{none[1..]} has no mailbox.");

        if (cloud == MailUser)
            return MailUserReconciled(facts, cloudPrint);

        return Unnamed(facts, cloud, cloudPrint);
    }

    /// <summary>
    /// The location word the page shows (3.5). S2: "Unknown" is retired - it meant both "no mailbox anywhere" and "could not
    /// be asked". A recipient with no mailbox reads "Not applicable"; a disagreement or a side that could not be asked, "Could
    /// not be determined".
    /// </summary>
    internal static string LocationWord(RecipientLocationClass location) => location switch
    {
        RecipientLocationClass.Cloud or RecipientLocationClass.ExchangeHeldNotRead => "Cloud",
        RecipientLocationClass.OnPremises => "On-Premises",
        RecipientLocationClass.NotApplicable => "Not applicable",
        _ => Undetermined,
    };

    /// <summary>
    /// 3.2, N1. Exchange Online's IsDirSynced as true, false or not reported (null): a wrapped value unwrapped, a string
    /// parsed, anything else not reported. The module's own parser: <c>ExchangeIdentityResolver</c>'s is private, and no
    /// shared file is edited (the plan's section 6).
    /// </summary>
    internal static bool? ReadIsDirSynced(object? value) => value switch
    {
        PSObject wrapped when !ReferenceEquals(wrapped.BaseObject, wrapped) => ReadIsDirSynced(wrapped.BaseObject),
        bool reported => reported,
        string text when bool.TryParse(text.Trim(), out var parsed) => parsed,
        _ => null,
    };

    // 3.4. Exchange Online's MailUser covers an on-premises mailbox seen from the cloud, a mail user whose mail goes to an
    // outside address, and one caught between states: only the side that holds it can say which.
    private static RecipientClassificationResult MailUserReconciled(RecipientFacts facts, string cloudPrint)
    {
        if (facts.IsDirSynced == false)
            return new(MailUserLabel, RecipientLocationClass.NotApplicable, cloudPrint, null, MailUserNote);

        var print = $"{cloudPrint}; on-premises: {OnPremWords(facts)}";
        switch (facts.OnPrem)
        {
            case OnPremRecipientAnswer.Found:
                var onPrem = Word(facts.OnPremType);
                if (onPrem is not null && OnPremMailboxes.TryGetValue(onPrem, out var label))
                    return new(label, RecipientLocationClass.OnPremises, print, null);
                if (onPrem == MailUser)
                    return new(MailUserLabel, RecipientLocationClass.NotApplicable, print, null, MailUserNote);
                if (onPrem is not null && OnPremRemoteMailboxes.Contains(onPrem))
                {
                    return Unsettled(print, $"Exchange Online and on-premises Exchange disagree about {facts.Address}: on-premises "
                        + $"Exchange holds it as {onPrem}, a mailbox in Exchange Online, and Exchange Online holds no mailbox for "
                        + $"it ({MailUser}).");
                }
                return Unsettled(print, onPrem is null
                    ? $"Exchange Online and on-premises Exchange disagree about {facts.Address}: Exchange Online holds it as "
                        + $"{MailUser}, and on-premises Exchange reports no type for it."
                    : $"Exchange Online and on-premises Exchange disagree about {facts.Address}: Exchange Online holds it as "
                        + $"{MailUser}, on-premises Exchange as {onPrem}.");
            case OnPremRecipientAnswer.NotFound:
                return Unsettled(print, NoSuchRecipientOnPremises(facts));
            case OnPremRecipientAnswer.MoreThanOne:
                return Unsettled(print, MoreThanOneOnPremises(facts));
            default:
                // Could not be asked (U1): the read's catch gave today's warning. Not asked: no answer to settle it either.
                return Unsettled(print, null);
        }
    }

    // 3.3, "any other value" and "none reported": shown raw, never named from the unknown word. Exchange Online's Get-Mailbox
    // decides first; on-premises only when Exchange Online holds no mailbox and does not report the object as cloud-only.
    private static RecipientClassificationResult Unnamed(RecipientFacts facts, string? cloud, string cloudPrint)
    {
        var label = cloud is null ? NoTypeReported : UnnamedTypePrefix + cloud;
        var reported = cloud is null
            ? $"Exchange Online reported no type for {facts.Address}"
            : $"Exchange Online reports {facts.Address} as {cloud}, a type this app does not name,";

        switch (facts.Mailbox)
        {
            case CloudMailboxAnswer.Found:
                var own = Word(facts.MailboxType);
                var foundPrint = $"{cloudPrint}; Get-Mailbox: {own ?? "no type"}";
                return own is not null && CloudMailboxes.TryGetValue(own, out var named)
                    ? new(named, RecipientLocationClass.Cloud, foundPrint, null)
                    : new(label, RecipientLocationClass.Cloud, foundPrint, null);

            case CloudMailboxAnswer.NotFound:
                if (facts.IsDirSynced == false)
                    return new(label, RecipientLocationClass.NotApplicable, $"{cloudPrint}; Get-Mailbox: no mailbox", null, CloudOnlyNoMailboxNote);

                var print = $"{cloudPrint}; Get-Mailbox: no mailbox; on-premises: {OnPremWords(facts)}";
                switch (facts.OnPrem)
                {
                    case OnPremRecipientAnswer.Found:
                        var onPrem = Word(facts.OnPremType);
                        if (onPrem is not null && OnPremMailboxes.TryGetValue(onPrem, out var onPremLabel))
                            return new(onPremLabel, RecipientLocationClass.OnPremises, print, null);
                        return Unsettled(print, onPrem is null
                            ? $"{reported} and holds no mailbox for it; on-premises Exchange reports no type for it."
                            : $"{reported} and holds no mailbox for it; on-premises Exchange holds it as {onPrem}.");
                    case OnPremRecipientAnswer.NotFound:
                        return Unsettled(print, NoSuchRecipientOnPremises(facts));
                    case OnPremRecipientAnswer.MoreThanOne:
                        return Unsettled(print, MoreThanOneOnPremises(facts));
                    default:
                        return Unsettled(print, null);
                }

            case CloudMailboxAnswer.Failed:
                return Unsettled($"{cloudPrint}; Get-Mailbox: not read", cloud is null
                    ? $"{reported}, and its mailbox could not be read."
                    : $"{reported} and its mailbox could not be read.");

            default:
                return Unsettled($"{cloudPrint}; Get-Mailbox: not read", null);
        }
    }

    // D3. Not "Mail user": a recipient synced from a directory the configured on-premises server does not serve is not
    // found there and may still be a mailbox (Architectural Invariant 7).
    private static string NoSuchRecipientOnPremises(RecipientFacts facts) => facts.IsDirSynced == true
        ? $"On-premises Exchange holds no recipient at {facts.Address}, though Exchange Online reports it as synced from an "
            + "on-premises directory; it may be held by a directory this module's on-premises server does not serve."
        : $"On-premises Exchange holds no recipient at {facts.Address}, though Exchange Online did not report whether it is "
            + "synced; it may be held by a directory this module's on-premises server does not serve.";

    private static string MoreThanOneOnPremises(RecipientFacts facts) =>
        $"On-premises Exchange returned more than one recipient for {facts.Address}.";

    private static string OnPremWords(RecipientFacts facts) => facts.OnPrem switch
    {
        OnPremRecipientAnswer.Found => Word(facts.OnPremType) ?? "no type",
        OnPremRecipientAnswer.NotFound => "no such recipient",
        OnPremRecipientAnswer.MoreThanOne => "more than one recipient",
        _ => "not read",
    };

    private static RecipientClassificationResult Unsettled(string smallPrint, string? warning) =>
        new(Undetermined, RecipientLocationClass.Undetermined, smallPrint, warning);

    private static bool Named(string cloud) =>
        CloudMailboxes.ContainsKey(cloud) || CloudHeldNotRead.ContainsKey(cloud) || NoMailbox.ContainsKey(cloud) || cloud == MailUser;

    private static string? Word(string? reported) => string.IsNullOrWhiteSpace(reported) ? null : reported.Trim();
}
