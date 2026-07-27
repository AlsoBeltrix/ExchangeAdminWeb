namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// Pure, UI-free in-list filter over an already-loaded manageable-group list (plan
/// docs/SelfServiceGroupManagement-Plan.md AC9). The list is already in memory, so this is
/// client-side filtering only - no directory round-trip. Kept separate from the razor page so the
/// match rule is unit-testable.
///
/// AC9 requires a NON-PREFIX term to find a group: a word from the MIDDLE of the name, or a word from
/// the description - not just a prefix. So the match is a case-insensitive SUBSTRING test (which
/// subsumes prefix and mid-string) across the group's name, sAMAccountName, and description. A
/// blank/whitespace term matches everything (the unfiltered list).
/// </summary>
public static class ManageableGroupFilter
{
    /// <summary>
    /// True when the group matches the filter term. A null/blank term matches every group. The term is
    /// matched as a case-insensitive substring against Name, SamAccountName, and Description, so a
    /// mid-name word or a description word finds the group (AC9), not only a name prefix.
    /// </summary>
    public static bool Matches(ManageableGroup group, string? term)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (string.IsNullOrWhiteSpace(term))
            return true;

        var t = term.Trim();
        return Contains(group.Name, t)
            || Contains(group.SamAccountName, t)
            || Contains(group.Description, t);
    }

    /// <summary>
    /// Returns the groups matching the term, preserving input order. A null/blank term returns the list
    /// unchanged. Never returns null.
    /// </summary>
    public static IReadOnlyList<ManageableGroup> Filter(IEnumerable<ManageableGroup> groups, string? term)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (string.IsNullOrWhiteSpace(term))
            return groups.ToList();

        return groups.Where(g => Matches(g, term)).ToList();
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack)
        && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
