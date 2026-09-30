using System.Text;
using ExchangeAdminWeb.Services.SelfServiceGroups;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// The pure half of Comms-10k membership LISTING (docs/Comms10kBulkResolveScale-Plan.md S2):
/// turning a group's raw <c>member</c> DNs into display rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately a SECOND resolver, not <see cref="Comms10kAddressResolver"/>.</b> Different
/// input (DNs, not addresses), different filter (<c>distinguishedName</c>, not UPN or mail), and
/// - the reason they must never be merged - the opposite failure rule. Over there a directory
/// row that does not resolve means "this address matched nobody", and the address is reported as
/// skipped and left out of the write. Here a DN that does not resolve is a member who IS in the
/// group; dropping it would under-report the membership on a screen an operator reads to decide
/// who is subscribed. So it falls back to its own CN and still produces a row.
/// </para>
/// <para>
/// Pure so the fallback and the batching are testable without a domain.
/// </para>
/// </remarks>
public static class Comms10kMemberResolver
{
    /// <summary>DNs per detail query. Same reasoning as <see cref="Comms10kAddressResolver.BatchSize"/>.</summary>
    public const int BatchSize = 500;

    /// <summary>One directory row of display detail for a member.</summary>
    public sealed record Detail(string? DistinguishedName, string? SamAccountName, string? Mail, string? DisplayName);

    /// <summary>
    /// Partitions DNs into query-sized batches, in order, covering every element exactly once.
    /// </summary>
    public static IEnumerable<IReadOnlyList<string>> Batch(IReadOnlyList<string> distinguishedNames)
    {
        ArgumentNullException.ThrowIfNull(distinguishedNames);

        for (var i = 0; i < distinguishedNames.Count; i += BatchSize)
        {
            var take = Math.Min(BatchSize, distinguishedNames.Count - i);
            var batch = new List<string>(take);
            for (var j = 0; j < take; j++)
                batch.Add(distinguishedNames[i + j]);
            yield return batch;
        }
    }

    /// <summary>
    /// The OR filter for one batch of DNs. Class-agnostic on purpose: a distribution list can
    /// legitimately contain a contact or a nested group, and this is a LISTING - hiding a member
    /// because it is not a user would misreport who is subscribed.
    /// </summary>
    /// <remarks>
    /// A DN routinely contains commas, and can contain parentheses and backslashes. Every value
    /// goes through <see cref="AdOwnershipFilter.EscapeLdapFilterValue"/>, which is what keeps a
    /// CN like <c>CN=Smith (Contractor),OU=...</c> from closing the clause early and silently
    /// corrupting the whole batch's filter.
    /// </remarks>
    public static string BuildBatchFilter(IReadOnlyList<string> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
            throw new ArgumentException("A batch must contain at least one distinguished name.", nameof(batch));

        var sb = new StringBuilder("(|");
        foreach (var dn in batch)
        {
            if (string.IsNullOrWhiteSpace(dn))
                throw new ArgumentException("A batch must not contain a blank distinguished name.", nameof(batch));

            sb.Append("(distinguishedName=").Append(AdOwnershipFilter.EscapeLdapFilterValue(dn)).Append(')');
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Builds one display row per DN, in the group's own order, falling back to the DN's leading
    /// CN when the directory returned no detail for it.
    /// </summary>
    /// <remarks>
    /// <b>The count of rows out always equals the count of DNs in.</b> That is the property worth
    /// testing: this list is what an operator reads to decide who is subscribed, so a member that
    /// silently vanishes because a detail query missed it is a wrong answer, not a cosmetic gap.
    /// </remarks>
    public static List<Comms10kMember> BuildRows(IReadOnlyList<string> distinguishedNames, IReadOnlyList<Detail> details)
    {
        ArgumentNullException.ThrowIfNull(distinguishedNames);
        ArgumentNullException.ThrowIfNull(details);

        var byDn = new Dictionary<string, Detail>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in details)
        {
            if (!string.IsNullOrWhiteSpace(d.DistinguishedName))
                byDn[d.DistinguishedName] = d;
        }

        var rows = new List<Comms10kMember>(distinguishedNames.Count);
        foreach (var dn in distinguishedNames)
        {
            byDn.TryGetValue(dn ?? "", out var detail);

            var fallback = LeadingCn(dn);
            rows.Add(new Comms10kMember
            {
                Email = detail?.Mail ?? "",
                SamAccountName = detail?.SamAccountName ?? "",
                DisplayName = string.IsNullOrWhiteSpace(detail?.DisplayName) ? fallback : detail!.DisplayName!
            });
        }

        return rows;
    }

    /// <summary>
    /// The CN component of a DN, unescaped enough to read. Used only as a display fallback.
    /// </summary>
    /// <remarks>
    /// A DN escapes a literal comma inside a CN as <c>\,</c>, so splitting on a bare comma would
    /// cut a name like <c>CN=Smith\, John,OU=...</c> in half. The scan walks characters and
    /// honours the backslash instead.
    /// </remarks>
    public static string LeadingCn(string? distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
            return "";

        var dn = distinguishedName;
        var start = dn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? 3 : 0;

        var sb = new StringBuilder();
        for (var i = start; i < dn.Length; i++)
        {
            if (dn[i] == '\\' && i + 1 < dn.Length)
            {
                sb.Append(dn[i + 1]);
                i++;
                continue;
            }
            if (dn[i] == ',')
                break;

            sb.Append(dn[i]);
        }

        var cn = sb.ToString().Trim();
        return cn.Length > 0 ? cn : distinguishedName;
    }

    /// <summary>
    /// The RID of a group SID, for the <c>(primaryGroupID=...)</c> union.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>GroupManagementService.RidFromSid</c>, which exists for the identical defect
    /// (lst-2): the linked <c>member</c> attribute does NOT carry members whose membership comes
    /// from their PRIMARY group, which <c>Get-ADGroupMember</c> included. Swapping one for the
    /// other without this union silently shortens the list.
    /// </remarks>
    public static string? RidFromSid(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid) || !sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            return null;

        var i = sid.LastIndexOf('-');
        if (i < 0 || i == sid.Length - 1)
            return null;

        var tail = sid[(i + 1)..];
        return tail.All(char.IsAsciiDigit) ? tail : null;
    }
}
