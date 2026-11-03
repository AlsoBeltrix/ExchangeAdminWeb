namespace ExchangeAdminWeb.Services;

/// <summary>
/// What the admin page should do with one typed protected-principal entry.
/// </summary>
/// <param name="Accepted">True when <paramref name="ValueToAdd"/> should join the list.</param>
/// <param name="ValueToAdd">
/// The value to store - the directory's canonical form where it supplied one, so the saved entry
/// matches what the protection engine will later resolve rather than whatever the operator typed.
/// </param>
/// <param name="ErrorMessage">Operator-facing refusal text; null when accepted.</param>
/// <param name="ClearInput">
/// Whether the input box should be emptied. False on every refusal, so the operator can see and
/// correct what they typed.
/// </param>
/// <param name="ConsultedDirectory">
/// Whether a directory lookup was warranted. False for blank and duplicate input, which are
/// decided without a round-trip.
/// </param>
/// <param name="CompanionValue">
/// For a User, Group or OU entry, the legacy row stored beside the identifier row in
/// <paramref name="ValueToAdd"/>: the value an older build stores and enforces, and the
/// identifier row's label (docs/ProtectedPrincipalIdentifiers-Plan.md A.2, B.1). Null for every
/// other kind and on every refusal.
/// </param>
public sealed record EntryValidationDecision(
    bool Accepted,
    string? ValueToAdd,
    string? ErrorMessage,
    bool ClearInput,
    bool ConsultedDirectory,
    string? CompanionValue = null);

/// <summary>
/// Decides whether a typed protected-principal entry may be saved. Extracted from
/// <c>Components/Pages/AdminSettings.razor</c> because this repo has no bUnit harness, so page
/// markup cannot be tested - the same reason <c>MessageTraceExportListing</c> exists.
///
/// See docs/ProtectedPrincipalInputValidation-Plan.md.
/// </summary>
public static class ProtectedPrincipalEntryValidator
{
    public const string UnavailableMessage = "Active Directory is unreachable. Try again later.";

    /// <summary>
    /// Should this entry be looked up at all? Blank input has nothing to resolve, and a duplicate
    /// is already present - neither is worth a directory round-trip.
    /// </summary>
    public static bool ShouldConsultDirectory(IReadOnlyCollection<string> existing, string raw)
    {
        var v = raw.Trim();
        return !string.IsNullOrWhiteSpace(v)
            && !existing.Contains(v, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns a directory outcome into the page's action.
    /// </summary>
    /// <remarks>
    /// The two refusal messages MUST stay distinct. NotFound means the directory answered and the
    /// operator should check what they typed; Unavailable means the lookup never ran and they
    /// should retry. Collapsing them tells an admin their correct entry was a typo during an
    /// outage, and sends them chasing a mistake they did not make (owner ruling 2026-07-31, plan D1).
    ///
    /// Both refusals leave the entry out of the list. An unvalidated entry is not inert: a bad
    /// user or OU row silently matches nothing, and a bad group row makes group expansion fail
    /// closed, turning every later check into a denial that reads as a directory fault.
    /// </remarks>
    public static EntryValidationDecision Decide(
        IReadOnlyCollection<string> existing,
        string raw,
        string objectKind,
        DirectoryValidationResult result)
    {
        var v = raw.Trim();

        if (string.IsNullOrWhiteSpace(v))
            return new EntryValidationDecision(false, null, null, ClearInput: false, ConsultedDirectory: false);

        if (existing.Contains(v, StringComparer.OrdinalIgnoreCase))
        {
            // Already protected. Not an error - just clear the box.
            return new EntryValidationDecision(false, null, null, ClearInput: true, ConsultedDirectory: false);
        }

        switch (result.Outcome)
        {
            case DirectoryLookupOutcome.Unavailable:
                return new EntryValidationDecision(
                    false, null, UnavailableMessage, ClearInput: false, ConsultedDirectory: true);

            case DirectoryLookupOutcome.NotFound:
                return new EntryValidationDecision(
                    false,
                    null,
                    $"'{v}' was not found in Active Directory. Check the name - note that "
                    + "cloud-only objects cannot be protected.",
                    ClearInput: false,
                    ConsultedDirectory: true);
        }

        if (result.Ambiguous)
        {
            // Every kind stores ONE object's immutable id; accepting an ambiguous name would
            // persist whichever match AD returned first, protecting it and leaving the intended
            // object unprotected (pgwt-4 for targets; docs/ProtectedPrincipalIdentifiers-Plan.md
            // A.3 for users, groups and OUs, which accepted it until then). A picker selection is
            // a DN or a UPN and resolves exactly one.
            return new EntryValidationDecision(
                false,
                null,
                AmbiguousMessage(v, objectKind),
                ClearInput: false,
                ConsultedDirectory: true);
        }

        var identifier = Identifier(objectKind, result.Match);
        if (identifier is null)
        {
            // An entry keys on the object's immutable identifier - objectSid for a user or group,
            // objectGUID for an OU or a target (docs/ProtectedGroupWriteTarget-Plan.md T0,
            // docs/ProtectedPrincipalIdentifiers-Plan.md A.3): accepting without it would store a
            // name-only row that a rename, or another object taking the name, silently changes.
            // Refuse and let the operator retry.
            return new EntryValidationDecision(
                false,
                null,
                $"'{v}' was found, but its directory identifier could not be read. Try again.",
                ClearInput: false,
                ConsultedDirectory: true);
        }

        if (objectKind == "Group"
            && ExchangeAdminWeb.Authorization.SectionAccessGroupIdentity.SidRejectionReason(identifier) is not null)
        {
            // Everyone, BUILTIN\* and the other well-known SIDs name no one domain group; protecting
            // one protects far more than an admin choosing "a group" intends (plan A.3, the
            // section-access rule).
            return new EntryValidationDecision(
                false,
                null,
                $"'{v}' is a built-in or well-known group, which cannot be protected as a group. Choose a domain group.",
                ClearInput: false,
                ConsultedDirectory: true);
        }

        var canonical = CanonicalValue(v, objectKind, result.Match);
        return objectKind == "GroupTarget"
            ? new EntryValidationDecision(true, canonical, null, ClearInput: true, ConsultedDirectory: true)
            : new EntryValidationDecision(
                true,
                ProtectedPrincipalIdEntry.Format(identifier, canonical),
                null,
                ClearInput: true,
                ConsultedDirectory: true,
                CompanionValue: canonical);
    }

    private static string AmbiguousMessage(string v, string objectKind) => objectKind switch
    {
        "Group" or "GroupTarget" => $"'{v}' matches more than one group. Pick the group from the search suggestions.",
        "OU" => $"'{v}' matches more than one OU. Pick the OU from the search suggestions.",
        _ => $"'{v}' matches more than one user. Enter the user's UPN, or pick the user from the search suggestions.",
    };

    /// <summary>
    /// The immutable identifier an entry of <paramref name="objectKind"/> stores, canonical, or
    /// null when the directory did not supply a readable one: objectSid for a user or group,
    /// objectGUID for an OU or a protected target.
    /// </summary>
    internal static string? Identifier(string objectKind, ADSearchResult? match) => objectKind switch
    {
        "GroupTarget" or "OU" => ProtectedPrincipalIdEntry.CanonicalGuid(match?.ObjectGuid),
        _ => ProtectedPrincipalIdEntry.CanonicalSid(match?.ObjectSid),
    };

    public const string SaveBlockedMessage =
        "Still checking an entry against Active Directory. Wait for it to finish, then save.";

    /// <summary>
    /// Whether a save must be refused because a directory lookup is still running.
    /// </summary>
    /// <remarks>
    /// The add path validates on a background task, so the circuit stays free and the operator can
    /// click Save while an Add is mid-flight. Saving then snapshots the list WITHOUT the pending
    /// entry, reports success, and the entry appears in the page moments later - so the store and
    /// the page disagree and nothing says so until a reload. Refusing is a guarantee behind the
    /// disabled button, not a duplicate of it: "UI hiding is not security" is already this repo's
    /// rule for the protection path. Review finding ppv-3.
    /// </remarks>
    public static bool ShouldBlockSave(bool validationInFlight) => validationInFlight;

    /// <summary>
    /// Whether an entry ALREADY SAVED in the store should be flagged as not resolving in AD.
    /// </summary>
    /// <remarks>
    /// Only an affirmative <see cref="DirectoryLookupOutcome.NotFound"/> flags a row. A lookup
    /// that could not run must NOT flag: during an outage every entry would fail at once, and a
    /// page full of warnings reads as "your protection rules have been lost" - alarming and
    /// false. The badge's absence therefore means "not known to be stale", never "verified".
    ///
    /// This is the mirror of the rule in <see cref="Decide"/>. There, a failed lookup is
    /// conservative because it REFUSES a new entry; here it is conservative because it stays
    /// SILENT about an existing one. Both follow from the same principle - a directory that did
    /// not answer is not evidence about the object - but they point in opposite directions, so
    /// the two must not be collapsed into one helper.
    /// </remarks>
    public static bool ShouldFlagAsStale(DirectoryLookupOutcome outcome)
        => outcome == DirectoryLookupOutcome.NotFound;

    /// <summary>
    /// Prefers the directory's own form of the identity over the typed one, so the stored entry is
    /// what the protection engine resolves. Groups and OUs store the DN, which
    /// <c>MatchesDnToProtectedGroup</c> and <c>CheckOuMatches</c> compare directly; users store the
    /// UPN, falling back to mail. Falls back to the typed value when the directory supplied none.
    /// For a User, Group or OU this is the companion legacy row and the identifier row's label
    /// (docs/ProtectedPrincipalIdentifiers-Plan.md A.2): the value an older build stores.
    /// </summary>
    internal static string CanonicalValue(string typed, string objectKind, ADSearchResult? match)
    {
        if (match == null)
            return typed;

        var canonical = objectKind switch
        {
            "Group" => match.DistinguishedName,
            "OU" => match.DistinguishedName,
            // Protected TARGETS store "objectGUID|DN" (pgwt T0): the GUID survives renames,
            // the DN doubles as the display label. Decide refuses a Found match with no GUID
            // before this line, so the bang is guarded.
            "GroupTarget" => ProtectedGroupTargetEntry.Format(match.ObjectGuid!, match.DistinguishedName),
            _ => match.UserPrincipalName ?? match.Email
        };

        return string.IsNullOrWhiteSpace(canonical) ? typed : canonical;
    }
}
