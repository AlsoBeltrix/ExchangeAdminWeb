using System.Security.Principal;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// One identifier row of kind <c>user_sid</c>, <c>group_sid</c> or <c>ou_guid</c>
/// (docs/ProtectedPrincipalIdentifiers-Plan.md A.2). Stored as <c>identifier|label</c>, the
/// <see cref="ProtectedGroupTargetEntry"/> shape: the identifier - an objectSid, or an OU's objectGUID - is what a
/// decision compares; the label is what the page shows and the value of the companion legacy row an older build
/// enforces. Pure, so the format and the pairing rule are testable without a store.
/// </summary>
/// <remarks>
/// Split on the FIRST separator: neither a SID nor a GUID contains one, and a DN or an address after it may.
/// </remarks>
public sealed record ProtectedPrincipalIdEntry(string Identifier, string Label)
{
    public const char Separator = '|';

    /// <summary>The stored value for <paramref name="identifier"/> labelled <paramref name="label"/>.</summary>
    public static string Format(string identifier, string? label)
        => $"{identifier.Trim()}{Separator}{(label ?? string.Empty).Trim()}";

    /// <summary>
    /// Parses a stored value; null for a blank one. A value without a separator is all identifier and has no label, so a
    /// hand-repaired row still compares by what it holds rather than by nothing.
    /// </summary>
    public static ProtectedPrincipalIdEntry? Parse(string? stored)
    {
        var v = (stored ?? string.Empty).Trim();
        if (v.Length == 0)
            return null;

        var sep = v.IndexOf(Separator);
        if (sep < 0)
            return new(v, string.Empty);

        var id = v[..sep].Trim();
        return id.Length == 0 ? null : new(id, v[(sep + 1)..].Trim());
    }

    /// <summary>What the page shows: the label where there is one, else the identifier.</summary>
    public string Display => Label.Length > 0 ? Label : Identifier;

    /// <summary>
    /// The canonical form of a SID written as one, or null. The framework parser also accepts SDDL aliases
    /// (<c>"BA"</c> is BUILTIN\Administrators), so only a value already in <c>S-</c> form is read as a SID.
    /// </summary>
    public static string? CanonicalSid(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (!v.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return new SecurityIdentifier(v).Value;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A GUID in "D" form, or null when <paramref name="value"/> is not one.</summary>
    public static string? CanonicalGuid(string? value)
        => Guid.TryParse((value ?? string.Empty).Trim(), out var guid) ? guid.ToString("D") : null;

    /// <summary>
    /// Whether <paramref name="legacyValue"/> is the companion of one of <paramref name="idRows"/>: its value equals
    /// that row's label (plan B.2). A companion is not evaluated on its own and not shown as its own entry. Compared
    /// ignoring case, as the table's key compares.
    /// </summary>
    public static bool IsCompanion(string legacyValue, IEnumerable<string> idRows)
    {
        var v = (legacyValue ?? string.Empty).Trim();
        return v.Length > 0 && idRows.Any(row =>
            Parse(row) is { Label.Length: > 0 } entry
            && string.Equals(entry.Label, v, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The legacy rows that are no identifier row's companion - the unmigrated rows, enforced exactly as before
    /// (plan B.2, D1).
    /// </summary>
    public static string[] Unpaired(IEnumerable<string> legacyRows, IReadOnlyCollection<string> idRows)
        => idRows.Count == 0
            ? legacyRows.ToArray()
            : legacyRows.Where(v => !IsCompanion(v, idRows)).ToArray();

    /// <summary>
    /// The admin page's add (plan A.2, B.1): <paramref name="idRow"/> into <paramref name="ids"/> and its companion into
    /// <paramref name="legacy"/>. The same object added again - under another name, or after a rename - replaces its
    /// identifier row, refreshing the label, rather than accumulating two rows for one object (the pgwt-6 rule for
    /// targets); the old label's companion goes with it unless another row still pairs with it. True when either list
    /// changed.
    /// </summary>
    public static bool AddWithCompanion(List<string> ids, List<string> legacy, string idRow, string? companion)
    {
        var identifier = Parse(idRow)?.Identifier;
        var changed = false;
        foreach (var old in ids.Where(r =>
                     string.Equals(Parse(r)?.Identifier, identifier, StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(r, idRow, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            ids.Remove(old);
            RemoveOrphanedCompanion(ids, legacy, Parse(old)?.Label);
            changed = true;
        }

        if (!ids.Contains(idRow, StringComparer.OrdinalIgnoreCase))
        {
            ids.Add(idRow);
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(companion) && !legacy.Contains(companion, StringComparer.OrdinalIgnoreCase))
        {
            legacy.Add(companion);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// The admin page's remove: <paramref name="row"/> out of <paramref name="ids"/> and its companion out of
    /// <paramref name="legacy"/>, unless another identifier row still pairs with that companion. Removing only the
    /// identifier row would leave the companion unpaired - an unmigrated row, enforced by name - so the entry would look
    /// removed and still protect. True when the row was there.
    /// </summary>
    public static bool RemoveWithCompanion(List<string> ids, List<string> legacy, string row)
    {
        if (!ids.Remove(row))
            return false;
        RemoveOrphanedCompanion(ids, legacy, Parse(row)?.Label);
        return true;
    }

    private static void RemoveOrphanedCompanion(List<string> ids, List<string> legacy, string? label)
    {
        if (string.IsNullOrEmpty(label) || IsCompanion(label, ids))
            return;
        legacy.RemoveAll(v => string.Equals((v ?? string.Empty).Trim(), label, StringComparison.OrdinalIgnoreCase));
    }
}
