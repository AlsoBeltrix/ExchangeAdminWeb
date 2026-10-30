using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// A forest for <see cref="IProtectedGroupDirectory"/> in which each domain's DC holds its own
/// groups' member links and nothing else - no global catalog replicas - and the global catalog
/// holds every universal group's links in every domain and no other group's. That is the
/// strictest case, and the one where "the target's domain returned no rows" is most often wrong.
/// Shared by the protected-group evaluator's tests (aud-a3b, aud-a3c, kd-88) and the self-service
/// resource-domain Deny tests (aud-a4b finding 7).
/// </summary>
/// <remarks>
/// kd-88. An account's <c>tokenGroups</c> is computed as a domain controller of its own domain computes it: every SECURITY
/// group whose links that DC holds (its own domain's groups) or the global catalog it consults holds (universal groups),
/// followed from the account and its primary group until nothing more is reached - so another domain's domain-local groups
/// and every distribution group are absent, as they are from a real token. <c>memberOf</c> is answered from the same holdings:
/// a domain's DC names the groups of its own domain that link an object directly, the global catalog the universal ones.
/// </remarks>
internal sealed class FakeForest : IProtectedGroupDirectory
{
    private sealed record Entry(
        string Dn, string? Sid, string ObjectClass, int? PrimaryGroupRid, DirectoryGroupScope? Scope, string? Sam, bool Security);

    private readonly Dictionary<string, Entry> _objects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _members = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _domains = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Calls { get; } = new();
    public string? FailOn { get; set; }

    /// <summary>kd-88. Token groups the directory withholds from the reader: the read answers, without them.</summary>
    public bool WithholdTokenGroups { get; set; }

    /// <summary>kd-88. Objects a memberOf read leaves out of its answer, as a read that did not see them would.</summary>
    public HashSet<string> Unreturned { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeForest Domain(string netBios, string dns)
    {
        _domains[netBios] = dns;
        _domains[dns] = dns;
        return this;
    }

    public void Account(string dn, string sid, int? primaryGroupRid)
        => _objects[dn] = new Entry(dn, sid, "user", primaryGroupRid, null, null, Security: false);

    /// <summary>kd-88. An object with no SID - a contact - that groups can still hold.</summary>
    public void Contact(string dn)
        => _objects[dn] = new Entry(dn, null, "contact", null, null, null, Security: false);

    public void Group(string dn, string sid, DirectoryGroupScope scope, string sam, params string[] members)
    {
        _objects[dn] = new Entry(dn, sid, "group", null, scope, sam, Security: true);
        _members[dn] = members.ToList();
    }

    /// <summary>kd-88. A distribution group: its links are links, but no token carries it.</summary>
    public void DistributionGroup(string dn, string sid, DirectoryGroupScope scope, string sam, params string[] members)
    {
        _objects[dn] = new Entry(dn, sid, "group", null, scope, sam, Security: false);
        _members[dn] = members.ToList();
    }

    public void AddMember(string groupDn, string memberDn) => _members[groupDn].Add(memberDn);

    private static string DomainOf(string dn) => GroupManagementService.ServerFromDn(dn)!;

    private static bool SameDomain(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void Record(string call, string method)
    {
        Calls.Add(call);
        if (FailOn == method)
            throw new InvalidOperationException($"{method} failed (scripted).");
    }

    private IEnumerable<Entry> GroupsIn(string? server) => _objects.Values.Where(o =>
        o.Scope is not null && (server is null || SameDomain(DomainOf(o.Dn), server)));

    private static DirectoryGroup AsGroup(Entry e) => new(e.Dn, e.Sid!, e.Scope!.Value, e.Security);

    private static string CommonName(string dn) => ProtectedPrincipalService.ExtractCnFromDn(dn) ?? dn;

    public DirectoryTarget ReadTarget(string targetDn, string server)
    {
        Record($"ReadTarget {server}", nameof(ReadTarget));
        // A DC answers only for its own domain's objects.
        if (!_objects.TryGetValue(targetDn, out var o) || !SameDomain(DomainOf(o.Dn), server))
            throw new InvalidOperationException("No such object in that domain.");
        return new DirectoryTarget(o.Sid, o.ObjectClass, o.PrimaryGroupRid);
    }

    public IReadOnlyList<string> ReadTokenGroups(string accountDn, string accountSid, string server)
    {
        Record($"ReadTokenGroups {server}", nameof(ReadTokenGroups));
        // A base read at the account's DN, bound by its SID, on a DC of its own domain.
        if (!_objects.TryGetValue(accountDn, out var account) || !SameDomain(DomainOf(account.Dn), server)
            || !string.Equals(account.Sid, accountSid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("No such account at that DN in that domain.");
        if (WithholdTokenGroups)
            return [];

        // The DC's expansion: security groups only, through the links it holds (its own domain's groups) and the ones its
        // global catalog holds (universal groups), from the account and its primary group.
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { account.Dn };
        var token = new List<string>();
        var primary = account.PrimaryGroupRid is int rid
            ? ProtectedGroupMembership.PrimaryGroupSid(account.Sid!, rid)
            : null;
        var primaryGroup = primary is null
            ? null
            : _objects.Values.FirstOrDefault(o => o.Scope is not null && o.Security
                && string.Equals(o.Sid, primary, StringComparison.OrdinalIgnoreCase) && SameDomain(DomainOf(o.Dn), server));
        if (primaryGroup is not null)
        {
            reached.Add(primaryGroup.Dn);
            token.Add(primaryGroup.Sid!);
        }

        bool grew;
        do
        {
            grew = false;
            foreach (var g in _objects.Values.Where(o => o.Scope is not null && o.Security && !reached.Contains(o.Dn)))
            {
                var held = SameDomain(DomainOf(g.Dn), server) || g.Scope == DirectoryGroupScope.Universal;
                if (held && _members[g.Dn].Any(reached.Contains))
                {
                    reached.Add(g.Dn);
                    token.Add(g.Sid!);
                    grew = true;
                }
            }
        } while (grew);

        return token;
    }

    public IReadOnlyList<DirectoryMemberOf> ReadMemberOf(IReadOnlyList<string> dns, string? server)
    {
        Record($"ReadMemberOf {server ?? "forest"}", nameof(ReadMemberOf));
        var rows = new List<DirectoryMemberOf>();
        foreach (var dn in dns)
        {
            // A DC holds only its own domain's objects; the global catalog holds every object.
            if (!_objects.TryGetValue(dn, out var o) || (server is not null && !SameDomain(DomainOf(o.Dn), server))
                || Unreturned.Contains(dn))
                continue;

            var parents = _objects.Values
                .Where(g => g.Scope is not null && _members[g.Dn].Contains(o.Dn, StringComparer.OrdinalIgnoreCase))
                .Where(g => server is null ? g.Scope == DirectoryGroupScope.Universal : SameDomain(DomainOf(g.Dn), server))
                .Select(g => g.Dn)
                .ToList();
            rows.Add(new DirectoryMemberOf(o.Dn, o.Sid, parents));
        }
        return rows;
    }

    public string ResolveDomainServer(string domain)
    {
        Record($"ResolveDomainServer {domain}", nameof(ResolveDomainServer));
        return _domains.TryGetValue(domain, out var dns)
            ? dns
            : throw new InvalidOperationException($"No domain '{domain}' in this forest.");
    }

    public IReadOnlyList<DirectoryGroup> FindGroups(GroupLookupKey key, string value, string? server)
    {
        Record($"FindGroups {key} {value} {server ?? "forest"}", nameof(FindGroups));
        return GroupsIn(server).Where(g => key switch
        {
            GroupLookupKey.DistinguishedName => string.Equals(g.Dn, value, StringComparison.OrdinalIgnoreCase),
            GroupLookupKey.Sid => string.Equals(g.Sid, value, StringComparison.OrdinalIgnoreCase),
            GroupLookupKey.SamAccountName => string.Equals(g.Sam, value, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(CommonName(g.Dn), value, StringComparison.OrdinalIgnoreCase),
        }).Select(AsGroup).ToList();
    }

    public bool DomainLocalGroupContainsAny(string groupDn, IReadOnlyList<string> memberDns, string server)
    {
        Record($"DomainLocalGroupContainsAny {server}", nameof(DomainLocalGroupContainsAny));
        if (!SameDomain(DomainOf(groupDn), server))
            return false;   // that DC holds none of this group's links

        var wanted = new HashSet<string>(memberDns, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groupDn };
        var queue = new Queue<string>([groupDn]);
        while (queue.Count > 0)
        {
            foreach (var m in _members[queue.Dequeue()])
            {
                if (wanted.Contains(m))
                    return true;
                // Only links this DC holds can be followed: groups of its own domain.
                if (_members.ContainsKey(m) && SameDomain(DomainOf(m), server) && visited.Add(m))
                    queue.Enqueue(m);
            }
        }
        return false;
    }

    public IReadOnlyList<string> FindObjectDnsBySid(string sid)
    {
        Record($"FindObjectDnsBySid {sid}", nameof(FindObjectDnsBySid));
        return _objects.Values
            .Where(o => string.Equals(o.Sid, sid, StringComparison.OrdinalIgnoreCase))
            .Select(o => o.Dn)
            .ToList();
    }
}
