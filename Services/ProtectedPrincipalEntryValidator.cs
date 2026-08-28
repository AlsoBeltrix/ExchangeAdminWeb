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
public sealed record EntryValidationDecision(
    bool Accepted,
    string? ValueToAdd,
    string? ErrorMessage,
    bool ClearInput,
    bool ConsultedDirectory);

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

        if (objectKind == "GroupTarget" && string.IsNullOrWhiteSpace(result.Match?.ObjectGuid))
        {
            // A target entry keys on the immutable objectGUID (docs/ProtectedGroupWriteTarget-
            // Plan.md T0): accepting without it would store a DN-only row that a rename
            // silently un-protects. Refuse and let the operator retry.
            return new EntryValidationDecision(
                false,
                null,
                $"'{v}' was found, but its directory identifier could not be read. Try again.",
                ClearInput: false,
                ConsultedDirectory: true);
        }

        return new EntryValidationDecision(
            true, CanonicalValue(v, objectKind, result.Match), null, ClearInput: true, ConsultedDirectory: true);
    }

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
