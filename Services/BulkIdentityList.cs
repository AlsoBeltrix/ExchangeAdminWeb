using ExchangeAdminWeb.Services.SelfServiceGroups;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Pure, directory-free half of the paste-list bulk add for the two on-prem group modules
/// (docs/GroupBulkActions-Plan.md, S1). Parses the operator's pasted identities, builds the
/// batched LDAP filter the services run, and matches the objects that come back to the lines
/// that asked for them. Nothing here touches AD, so every rule is unit-testable: the services
/// own only the query (an internal virtual seam each) and hand its rows in as
/// <see cref="Candidate"/> values.
///
/// Matching keys (AC10/AC9): a USER matches a line on userPrincipalName, mail or
/// sAMAccountName; a GROUP additionally on its <c>name</c> (gba-3: the group clause of the
/// filter matches on name, so the matcher must see it) and only when the caller allows
/// groups at all - self-service never does (nesting plan D1). A user's <c>name</c> is never a
/// key: it is not an identity a user is addressed by, and matching it would widen user
/// resolution beyond the single-member path's filter.
/// </summary>
public static class BulkIdentityList
{
    /// <summary>Hard cap on identities resolved or written per batch (plan section 1).</summary>
    public const int MaxBatch = 200;

    /// <summary>Lines per LDAP filter (plan section 6): keeps each filter well inside AD's limits.</summary>
    public const int ChunkSize = 50;

    /// <summary>One kept input line: its 1-based position in the operator's paste and its trimmed text.</summary>
    public sealed record Line(int Number, string Text);

    /// <summary>
    /// The parsed paste: lines to resolve, lines dropped as case-insensitive duplicates of an
    /// earlier line, and lines past <see cref="MaxBatch"/> that are never attempted.
    /// </summary>
    public sealed record ParsedList(
        IReadOnlyList<Line> Kept,
        IReadOnlyList<(Line Line, int DuplicateOf)> Duplicates,
        IReadOnlyList<Line> OverCap);

    /// <summary>
    /// One directory object as a batch query returned it. Every attribute is nullable because
    /// the matcher must be total over whatever AD projected (AC11). <paramref name="Name"/> is
    /// the AD <c>name</c> (RDN) attribute, a GROUP-only matching key (gba-3).
    /// </summary>
    public sealed record Candidate(
        string? DistinguishedName,
        string? ObjectClass,
        string? Name,
        string? DisplayName,
        string? UserPrincipalName,
        string? SamAccountName,
        string? Mail,
        string? ObjectGuid)
    {
        /// <summary>True when the object class is <c>group</c> (case-insensitive).</summary>
        public bool IsGroup => string.Equals(ObjectClass?.Trim(), "group", StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the object class is <c>user</c> (case-insensitive). A computer is a user subclass in AD but reports its own class and is neither.</summary>
        public bool IsUser => string.Equals(ObjectClass?.Trim(), "user", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Per-line resolution status shown in the page's resolution table (AC7).</summary>
    public enum Status
    {
        Resolved,
        NotFound,
        Ambiguous,
        Duplicate,
        NotAttempted,
    }

    /// <summary>
    /// One line's resolution. <paramref name="Match"/> is set only for <see cref="Status.Resolved"/>.
    /// <paramref name="Reason"/> is the operator-facing explanation for every other status.
    /// </summary>
    public sealed record Resolution(Line Line, Status Status, Candidate? Match, string Reason);

    /// <summary>
    /// Splits the pasted text on CR, LF, comma and semicolon; trims; drops blanks; numbers the
    /// surviving lines from 1 in input order. A later line equal (ordinal-ignore-case) to an
    /// earlier one is a Duplicate of that line. Lines past <see cref="MaxBatch"/> kept lines are
    /// OverCap and never attempted. Null or blank input yields three empty lists.
    /// </summary>
    public static ParsedList Parse(string? text)
    {
        var kept = new List<Line>();
        var duplicates = new List<(Line, int)>();
        var overCap = new List<Line>();
        if (string.IsNullOrWhiteSpace(text))
            return new ParsedList(kept, duplicates, overCap);

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var number = 0;
        foreach (var raw in text.Split(['\r', '\n', ',', ';']))
        {
            var t = raw.Trim();
            if (t.Length == 0)
                continue;
            number++;
            var line = new Line(number, t);
            if (seen.TryGetValue(t, out var firstNumber))
            {
                duplicates.Add((line, firstNumber));
                continue;
            }
            if (kept.Count >= MaxBatch)
            {
                overCap.Add(line);
                continue;
            }
            seen[t] = number;
            kept.Add(line);
        }

        return new ParsedList(kept, duplicates, overCap);
    }

    /// <summary>Splits the kept lines into filter-sized chunks, in order.</summary>
    public static IEnumerable<IReadOnlyList<Line>> Chunk(IReadOnlyList<Line> kept, int size = ChunkSize)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        for (var i = 0; i < kept.Count; i += size)
            yield return kept.Skip(i).Take(size).ToList();
    }

    /// <summary>
    /// The OR filter for one chunk. Every value is RFC 4515-escaped through
    /// <see cref="AdOwnershipFilter.EscapeLdapFilterValue"/>, so a metacharacter in a pasted
    /// line cannot alter the filter. The user clause mirrors
    /// <see cref="AdOwnershipFilter.BuildUserByIdentityFilter"/>; the group clause (name,
    /// sAMAccountName, mail) is emitted only when <paramref name="allowGroups"/> is true and
    /// mirrors the admin module's typed-path resolver.
    /// </summary>
    public static string BuildBatchFilter(IReadOnlyList<Line> chunk, bool allowGroups)
    {
        if (chunk.Count == 0)
            throw new ArgumentException("A chunk must contain at least one line.", nameof(chunk));

        var sb = new System.Text.StringBuilder("(|");
        foreach (var line in chunk)
        {
            var v = AdOwnershipFilter.EscapeLdapFilterValue(line.Text);
            sb.Append("(&(objectCategory=person)(objectClass=user)(|(userPrincipalName=").Append(v)
              .Append(")(mail=").Append(v)
              .Append(")(sAMAccountName=").Append(v).Append(")))");
            if (allowGroups)
            {
                sb.Append("(&(objectCategory=group)(|(name=").Append(v)
                  .Append(")(sAMAccountName=").Append(v)
                  .Append(")(mail=").Append(v).Append(")))");
            }
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Assigns exactly one <see cref="Status"/> per kept line (AC11). A line's candidates are
    /// those whose UPN, mail or sAMAccountName equal its text (ordinal-ignore-case), plus - for
    /// GROUP candidates, and only when <paramref name="allowGroups"/> - those whose name equals
    /// it. A group candidate never matches when groups are not allowed. Exactly one candidate
    /// is Resolved; none is NotFound; more is Ambiguous. A candidate already claimed by an
    /// earlier line (same DN, ordinal-ignore-case; falls back to objectGUID, then reference)
    /// makes the later line a Duplicate of that earlier line. Total: never throws on null
    /// attributes.
    /// </summary>
    public static IReadOnlyList<Resolution> Match(
        IReadOnlyList<Line> kept, IReadOnlyList<Candidate> candidates, bool allowGroups)
    {
        var results = new List<Resolution>(kept.Count);
        var claimed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in kept)
        {
            var hits = candidates.Where(c => Matches(c, line.Text, allowGroups)).ToList();
            if (hits.Count == 0)
            {
                results.Add(new Resolution(line, Status.NotFound, null,
                    allowGroups
                        ? $"'{line.Text}' was not found in AD as a user or group."
                        : $"'{line.Text}' did not match exactly one user."));
                continue;
            }
            if (hits.Count > 1)
            {
                results.Add(new Resolution(line, Status.Ambiguous, null,
                    $"Ambiguous: '{line.Text}' matches {hits.Count} directory objects."));
                continue;
            }

            var hit = hits[0];
            var key = KeyOf(hit);
            if (claimed.TryGetValue(key, out var earlier))
            {
                results.Add(new Resolution(line, Status.Duplicate, null,
                    $"Duplicate of line {earlier}: same directory object."));
                continue;
            }
            claimed[key] = line.Number;
            results.Add(new Resolution(line, Status.Resolved, hit, string.Empty));
        }

        return results;
    }

    private static bool Matches(Candidate c, string text, bool allowGroups)
    {
        if (c.IsGroup && !allowGroups)
            return false;
        if (Eq(c.UserPrincipalName, text) || Eq(c.Mail, text) || Eq(c.SamAccountName, text))
            return true;
        return c.IsGroup && Eq(c.Name, text);
    }

    private static bool Eq(string? attribute, string text)
        => !string.IsNullOrWhiteSpace(attribute) && string.Equals(attribute.Trim(), text, StringComparison.OrdinalIgnoreCase);

    private static string KeyOf(Candidate c)
        => !string.IsNullOrWhiteSpace(c.DistinguishedName) ? "dn:" + c.DistinguishedName
         : !string.IsNullOrWhiteSpace(c.ObjectGuid) ? "guid:" + c.ObjectGuid
         : "ref:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c).ToString();
}

/// <summary>One member's outcome in a bulk add or remove (docs/GroupBulkActions-Plan.md AC4).</summary>
public sealed record BulkRowOutcome(string Label, bool Done, string Message);

/// <summary>
/// The batch-level facts derived from per-row outcomes (plan AC5). <c>Success</c> is true ONLY
/// when every row is Done - Known Failure Class 2, a batch never reports blanket success over a
/// refused or failed row.
/// </summary>
public static class BulkOutcomeSummary
{
    public sealed record Summary(
        bool Success, int Requested, int Done, int NotDone,
        IReadOnlyList<string> MemberLines, string? ErrorDetail);

    public static Summary Of(IReadOnlyList<BulkRowOutcome> rows)
    {
        var done = rows.Count(r => r.Done);
        var notDone = rows.Count - done;
        var lines = rows
            .Select(r => $"{r.Label}: {(r.Done ? "Done" : "Not done")}{(string.IsNullOrWhiteSpace(r.Message) ? "" : " - " + r.Message)}")
            .ToList();
        return new Summary(
            Success: notDone == 0,
            Requested: rows.Count,
            Done: done,
            NotDone: notDone,
            MemberLines: lines,
            ErrorDetail: notDone == 0 ? null : $"{notDone} of {rows.Count} not done");
    }
}
