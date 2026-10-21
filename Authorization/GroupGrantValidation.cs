using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Authorization;

/// <summary>Why a new group grant may not be saved.</summary>
public enum GroupGrantRefusal
{
    /// <summary>Not the shape of a usable group SID (<see cref="SectionAccessGroupIdentity.IsUsableGroupSid"/>).</summary>
    NotAGroupSid,

    /// <summary>The directory answered and holds no object with this SID.</summary>
    NoSuchObject,

    /// <summary>The SID belongs to an object that is not a group - a user or a computer.</summary>
    NotAGroup,

    /// <summary>The directory could not be asked. Not evidence about the SID either way.</summary>
    DirectoryUnavailable,
}

/// <summary>
/// Decides whether NEW section-access, servicer and module-admin grants may be saved. aud-a1b.
/// </summary>
/// <remarks>
/// The add path tested a SID's SHAPE, which cannot tell a group's SID from a user's or from one
/// that resolves to nothing. Such a row authorized nobody, the grant list looked populated, and
/// nothing said so. The save now asks the directory about each grant it is adding, and refuses
/// the save if any is not a group the directory holds.
///
/// Pure over the lookup so the decision is testable; the page supplies
/// <c>ADDirectorySearchService.ValidateGroupSid</c>. Grants already stored are not re-checked:
/// that keeps an outage from blocking the save of a removal, which is when an administrator is
/// most likely to be repairing access.
/// </remarks>
public static class GroupGrantValidation
{
    /// <summary>The directory's answer about one SID, as a refusal - or null when it may be saved.</summary>
    public static GroupGrantRefusal? Decide(DirectoryValidationResult result) => result.Outcome switch
    {
        DirectoryLookupOutcome.Found when string.Equals(result.Match?.ObjectType, "Group", StringComparison.OrdinalIgnoreCase)
            => null,
        DirectoryLookupOutcome.Found => GroupGrantRefusal.NotAGroup,
        DirectoryLookupOutcome.NotFound => GroupGrantRefusal.NoSuchObject,
        _ => GroupGrantRefusal.DirectoryUnavailable,
    };

    /// <summary>
    /// Every new grant that may not be saved, in the order given. <paramref name="lookup"/> is
    /// asked once per usable SID; a value that is not one is refused without a lookup.
    /// </summary>
    public static IReadOnlyList<(string Sid, GroupGrantRefusal Refusal)> Refusals(
        IEnumerable<string> newGrants, Func<string, DirectoryValidationResult> lookup)
    {
        var refusals = new List<(string, GroupGrantRefusal)>();
        foreach (var grant in newGrants.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!SectionAccessGroupIdentity.IsUsableGroupSid(grant))
            {
                refusals.Add((grant, GroupGrantRefusal.NotAGroupSid));
                continue;
            }

            if (Decide(lookup(grant)) is { } refusal)
                refusals.Add((grant, refusal));
        }
        return refusals;
    }

    /// <summary>The operator-facing refusal for a save, naming each grant and why.</summary>
    public static string Message(IReadOnlyList<(string Sid, GroupGrantRefusal Refusal)> refusals, Func<string, string> label)
    {
        var lines = refusals.Select(r => $"{label(r.Sid)}: " + r.Refusal switch
        {
            GroupGrantRefusal.NotAGroupSid => "not a group identifier.",
            GroupGrantRefusal.NoSuchObject => "no object with this identifier exists in Active Directory.",
            GroupGrantRefusal.NotAGroup => "this identifier belongs to a user or computer, not a group.",
            _ => "Active Directory could not be reached to confirm it is a group. Try again.",
        });

        return "Not saved - remove or correct these entries first. " + string.Join(" ", lines);
    }
}
