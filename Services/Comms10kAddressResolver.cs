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

    /// <summary>One directory row, with its immutable identity and address keys.</summary>
    public sealed record Candidate(Guid ObjectGuid, string? DistinguishedName, string? UserPrincipalName, string? Mail);

    /// <summary>One resolved person. Keep the immutable identity and descriptive DN together.</summary>
    public sealed record ResolvedMember(Guid ObjectGuid, string DistinguishedName);

    /// <summary>One rejected address, as the operator wrote it, and why it was rejected.</summary>
    /// <remarks>
    /// <para>
    /// <b>The address is kept EXACTLY as supplied</b> - not trimmed, not lowercased, not
    /// normalised. The operator has to find this row in the file they uploaded, and a
    /// "corrected" address they never typed is unfindable. A trailing space that broke the
    /// match is itself the finding.
    /// </para>
    /// <para>
    /// <b>Reason is a short constant, never a sentence.</b> It is written to a CSV column an
    /// operator sorts and filters on, so it has to be stable text rather than prose; see the
    /// <c>Reasons</c> constants.
    /// </para>
    /// </remarks>
    public sealed record Rejection(string Address, string Reason);

    /// <summary>
    /// The rejection reasons, as written to the errors CSV. Owner ruling 2026-10-12: every
    /// non-matching address is reported with the reason it was rejected.
    /// </summary>
    /// <remarks>
    /// Stable, sortable, no editorializing. <see cref="Ambiguous"/> is a prefix - the match
    /// count is appended, because which it is changes what the operator does about it.
    /// </remarks>
    public static class Reasons
    {
        /// <summary>The row carried no address at all.</summary>
        public const string Blank = "Blank address";

        /// <summary>Nothing in the directory matched on UPN or primary SMTP address.</summary>
        public const string NoMatch = "No match in Active Directory";

        /// <summary>More than one directory object matched. The count is appended.</summary>
        public const string Ambiguous = "Ambiguous";

        /// <summary>A directory object matched but carried no distinguished name to write.</summary>
        public const string NoDistinguishedName = "Matched but has no distinguished name";
    }

    /// <summary>The assignment of every input address to resolved or rejected.</summary>
    /// <param name="ResolvedMembers">
    /// People in the order their addresses first resolved, each immutable identity appearing
    /// once however many rows or different addresses matched it.
    /// </param>
    /// <param name="Rejections">
    /// Every address that produced no DN, in input order, each with the reason it was rejected.
    /// <b>This is the whole list, never a sample</b> - it is what the errors CSV is built from,
    /// and the view's ten-address preview is a view concern that must not reach back into here.
    /// </param>
    public sealed record Outcome(IReadOnlyList<ResolvedMember> ResolvedMembers, List<Rejection> Rejections)
    {
        /// <summary>The existing write contract, derived from the paired member identities.</summary>
        public List<string> ResolvedDns => ResolvedMembers.Select(m => m.DistinguishedName).ToList();
    }

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
    /// Assigns every input address to a resolved DN or to a <see cref="Rejection"/>, from the
    /// candidates the directory returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Directory rows are indexed by address and immutable object identity before assignment.
    /// The same person can be returned by several batches; repeated rows are not ambiguity.
    /// </para>
    /// <list type="bullet">
    /// <item>One match resolves.</item>
    /// <item>More than one is AMBIGUOUS and is rejected with the count, never resolved to an
    /// arbitrary one of them.</item>
    /// <item>None is <see cref="Reasons.NoMatch"/>.</item>
    /// <item>A repeat of an already-resolved person is written once and is not reported as
    /// rejected - the address did resolve, it just resolved to an identity already in the list.</item>
    /// </list>
    /// <para>
    /// <b>An exact match is the only match</b> (owner ruling 2026-10-12, `.agents/decisions.md`).
    /// An address that matches nothing is a rejection, not a near miss: the keys are not widened
    /// to <c>proxyAddresses</c> or anything else to make an alias resolve. What the operator gets
    /// instead is every rejection, with its reason, in a downloadable CSV.
    /// </para>
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

        var matchesByAddress = new Dictionary<string, Dictionary<Guid, Candidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (candidate.ObjectGuid == Guid.Empty)
                throw new ArgumentException("Every directory candidate must have a nonempty ObjectGUID.", nameof(candidates));

            IndexCandidate(matchesByAddress, candidate.UserPrincipalName, candidate);
            IndexCandidate(matchesByAddress, candidate.Mail, candidate);
        }

        var resolved = new List<ResolvedMember>();
        var resolvedSet = new HashSet<Guid>();
        var rejected = new List<Rejection>();

        foreach (var address in addresses)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                rejected.Add(new Rejection(address ?? "", Reasons.Blank));
                continue;
            }

            if (!matchesByAddress.TryGetValue(address, out var matches))
            {
                rejected.Add(new Rejection(address, Reasons.NoMatch));
                continue;
            }

            if (matches.Count > 1)
            {
                rejected.Add(new Rejection(address, $"{Reasons.Ambiguous}: {matches.Count} matches"));
                continue;
            }

            var match = matches.Values.First();
            var dn = match.DistinguishedName;
            if (string.IsNullOrWhiteSpace(dn))
            {
                // A row that matched but carries no DN cannot be written. Reported as rejected
                // rather than dropped: the write removes everyone not in the resolved list, so a
                // silent drop here unsubscribes a real person.
                rejected.Add(new Rejection(address, Reasons.NoDistinguishedName));
                continue;
            }

            if (resolvedSet.Add(match.ObjectGuid))
                resolved.Add(new ResolvedMember(match.ObjectGuid, dn));
        }

        return new Outcome(resolved, rejected);
    }

    /// <summary>
    /// The one-line summary of a resolution, naming its two quantities separately:
    /// "2 addresses resolved to 1 distinct member; 0 rejected".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Review follow-up cdr-followup-1. The two counts differ by design - <see cref="Resolve"/>
    /// writes a person once however many of their addresses were uploaded - and the resolve
    /// stage's old message put one over the other: two aliases of one person read "1 of 2 matched
    /// in AD", which tells whoever reads it that an address missed when none did.
    /// </para>
    /// <para>
    /// <paramref name="resolvedAddresses"/> counts input addresses that resolved. Every input is
    /// exactly one of resolved or rejected, so that is the input count less the rejections.
    /// <paramref name="distinctMembers"/> counts people. The rejected clause appears only when a
    /// count is given: a caller that lists its rejections elsewhere leaves it out rather than show
    /// two different "rejected" numbers.
    /// </para>
    /// </remarks>
    public static string DescribeResolved(int resolvedAddresses, int distinctMembers, int? rejected = null)
    {
        var addresses = resolvedAddresses == 1 ? "address" : "addresses";
        var members = distinctMembers == 1 ? "member" : "members";
        var summary = $"{resolvedAddresses:N0} {addresses} resolved to {distinctMembers:N0} distinct {members}";
        return rejected is { } count ? $"{summary}; {count:N0} rejected" : summary;
    }

    /// <summary>
    /// Indexes every returned address key, including keys changed between batch queries. Only
    /// distinct GUIDs count as matches; the first row for that address/GUID supplies its DN.
    /// </summary>
    private static void IndexCandidate(Dictionary<string, Dictionary<Guid, Candidate>> index, string? address, Candidate candidate)
    {
        if (string.IsNullOrWhiteSpace(address))
            return;
        if (!index.TryGetValue(address, out var matches))
        {
            matches = new Dictionary<Guid, Candidate>();
            index.Add(address, matches);
        }
        matches.TryAdd(candidate.ObjectGuid, candidate);
    }

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
