using System.Text;
using ExchangeAdminWeb.Services.SelfServiceGroups;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// The pure half of Comms-10k address resolution (docs/Comms10kBulkResolveScale-Plan.md S1):
/// batching, filter construction and match assignment. The live directory query is the service's
/// <c>QueryBatchCandidates</c> seam.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The module asked the directory one question per CSV row -
/// <c>Get-ADUser -Filter</c> inside a <c>foreach</c>. At 5,279 addresses that is 5,279 sequential
/// queries, and ADWS invalidates the enumeration context partway through, which is the error the
/// owner actually hit. One OR-query per 500 addresses turns ten thousand round trips into twenty.
/// </para>
/// <para>
/// <b>It is pure so the security-critical part is testable without a domain.</b> The filter is
/// built from operator-supplied CSV content; RFC 4515 escaping is what stops a pasted
/// metacharacter from changing the filter's structure, and that has to be provable on this
/// machine rather than argued about.
/// </para>
/// </remarks>
public static class Comms10kAddressResolver
{
    /// <summary>
    /// Addresses per directory query.
    /// </summary>
    /// <remarks>
    /// Chosen for this workload rather than copied. <see cref="BulkIdentityList.ChunkSize"/> is 50
    /// because those modules are capped at 200 identities and the number barely matters; here it
    /// decides whether ten thousand addresses cost 20 round trips or 200. Changing it affects
    /// SPEED ONLY - never which addresses resolve - because every batch is matched the same way
    /// and the batches partition the input exactly.
    /// </remarks>
    public const int BatchSize = 500;

    /// <summary>One directory row, reduced to the fields this module matches on.</summary>
    public sealed record Candidate(string? DistinguishedName, string? UserPrincipalName, string? Mail);

    /// <summary>The assignment of every input address to resolved or skipped.</summary>
    /// <param name="ResolvedDns">
    /// Distinguished names to write, in the order their addresses first appeared, each appearing
    /// once however many times its address did.
    /// </param>
    /// <param name="SkippedAddresses">Every address that produced no DN, with the reason where there is one.</param>
    public sealed record Outcome(List<string> ResolvedDns, List<string> SkippedAddresses);

    /// <summary>
    /// Partitions the input into query-sized batches, in order, covering every element exactly
    /// once.
    /// </summary>
    /// <remarks>
    /// The partition is the load-bearing property, not the batch size. The write removes everyone
    /// absent from the resolved list, so an element that fell between two batches would be
    /// silently unsubscribed.
    /// </remarks>
    public static IEnumerable<IReadOnlyList<string>> Batch(IReadOnlyList<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        for (var i = 0; i < addresses.Count; i += BatchSize)
        {
            var take = Math.Min(BatchSize, addresses.Count - i);
            var batch = new List<string>(take);
            for (var j = 0; j < take; j++)
                batch.Add(addresses[i + j]);
            yield return batch;
        }
    }

    /// <summary>
    /// The OR filter for one batch: users only, matched on <c>userPrincipalName</c> or
    /// <c>mail</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately not <see cref="BulkIdentityList.BuildBatchFilter"/>.</b> That one also
    /// matches <c>sAMAccountName</c> and, optionally, groups. This module subscribes PEOPLE from a
    /// list of EMAIL ADDRESSES; matching a sAMAccountName would let a bare login name resolve, and
    /// matching groups would put a group's DN into a membership the page describes as individual
    /// subscribers. Widening the keys here changes who receives the mail.
    /// </para>
    /// <para>
    /// Every value goes through <see cref="AdOwnershipFilter.EscapeLdapFilterValue"/>. Note what
    /// that does NOT need to escape: <c>$</c>. The old code interpolated the address into
    /// <c>-Filter</c>, where PowerShell expands <c>$</c> as a variable and an address containing
    /// one silently queried something else (the lesson recorded at
    /// <c>SectionAccessGroupDirectory.cs:132</c>). <c>-LDAPFilter</c> has no such expansion, so
    /// <c>$</c> survives as the literal it is.
    /// </para>
    /// </remarks>
    /// <param name="batch">A non-empty batch. Blank entries are rejected - see <see cref="Resolve"/>.</param>
    public static string BuildBatchFilter(IReadOnlyList<string> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
            throw new ArgumentException("A batch must contain at least one address.", nameof(batch));

        var sb = new StringBuilder("(|");
        foreach (var address in batch)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                // An empty equality assertion is not valid filter syntax, and this filter is
                // fail-closed for the whole batch - so one blank line in a CSV would abort the
                // resolution of the 499 real addresses beside it. Blanks are screened out before
                // they reach here; this is the guard that says so out loud.
                throw new ArgumentException(
                    "A batch must not contain a blank address; screen them out before building the filter.",
                    nameof(batch));
            }

            var v = AdOwnershipFilter.EscapeLdapFilterValue(address);
            sb.Append("(&(objectCategory=person)(objectClass=user)(|(userPrincipalName=").Append(v)
              .Append(")(mail=").Append(v).Append(")))");
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Assigns every input address to a resolved DN or to skipped, from the candidates the
    /// directory returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outcomes are exactly the ones the per-row loop produced, which is the point: this slice
    /// changes how many questions are asked, never what the answers mean.
    /// </para>
    /// <list type="bullet">
    /// <item>One match resolves.</item>
    /// <item>More than one is AMBIGUOUS and is skipped with the count, never resolved to an
    /// arbitrary one of them.</item>
    /// <item>None is not found.</item>
    /// <item>A repeat of an already-resolved person is written once and is not reported as
    /// skipped - the address did resolve, it just resolved to a DN already in the list.</item>
    /// </list>
    /// <para>
    /// <b>Input order is preserved across batch boundaries</b>, because the assignment walks the
    /// original address list and the candidates are a pooled lookup rather than a per-batch one.
    /// </para>
    /// </remarks>
    /// <param name="addresses">The addresses as the operator supplied them, in order.</param>
    /// <param name="candidates">Every row returned by every batch query, pooled.</param>
    public static Outcome Resolve(IReadOnlyList<string> addresses, IReadOnlyList<Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(candidates);

        var resolved = new List<string>();
        var resolvedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();

        foreach (var address in addresses)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                skipped.Add(address ?? "");
                continue;
            }

            var matches = candidates
                .Where(c => Matches(c, address))
                .ToList();

            if (matches.Count > 1)
            {
                skipped.Add($"{address} (ambiguous: {matches.Count} matches)");
                continue;
            }

            if (matches.Count == 0)
            {
                skipped.Add(address);
                continue;
            }

            var dn = matches[0].DistinguishedName;
            if (string.IsNullOrWhiteSpace(dn))
            {
                // A row that matched but carries no DN cannot be written. Reported as skipped
                // rather than dropped: the write removes everyone not in the resolved list, so a
                // silent drop here unsubscribes a real person.
                skipped.Add(address);
                continue;
            }

            if (resolvedSet.Add(dn))
                resolved.Add(dn);
        }

        return new Outcome(resolved, skipped);
    }

    /// <summary>
    /// Whether one directory row is a match for one address, on the same two keys the filter
    /// asked about and with the same case-insensitivity the directory applies.
    /// </summary>
    private static bool Matches(Candidate candidate, string address) =>
        string.Equals(candidate.UserPrincipalName, address, StringComparison.OrdinalIgnoreCase)
        || string.Equals(candidate.Mail, address, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The addresses worth sending to the directory: non-blank, and each distinct value once.
    /// </summary>
    /// <remarks>
    /// Querying the same address twice costs a filter clause and returns the same row, and a blank
    /// one is not a legal equality assertion at all. Neither is an outcome decision -
    /// <see cref="Resolve"/> still walks the FULL original list, so a blank line is still reported
    /// skipped and a duplicate still resolves.
    /// </remarks>
    public static List<string> QueryableAddresses(IReadOnlyList<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queryable = new List<string>();

        foreach (var address in addresses)
        {
            if (string.IsNullOrWhiteSpace(address))
                continue;
            if (seen.Add(address))
                queryable.Add(address);
        }

        return queryable;
    }
}
