using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// A forest for <see cref="IProtectedGroupDirectory"/> in which each domain's DC holds its own
/// groups' member links and nothing else - no global catalog replicas - and the global catalog
/// holds every universal group's links in every domain and no other group's. That is the
/// strictest case, and the one where "the target's domain returned no rows" is most often wrong.
/// Shared by the protected-group evaluator's tests (aud-a3b, aud-a3c) and the self-service
/// resource-domain Deny tests (aud-a4b finding 7).
/// </summary>
internal sealed class FakeForest : IProtectedGroupDirectory
{
    private sealed record Entry(
        string Dn, string? Sid, string ObjectClass, int? PrimaryGroupRid, DirectoryGroupScope? Scope, string? Sam);

    private readonly Dictionary<string, Entry> _objects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _members = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _domains = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Calls { get; } = new();
    public string? FailOn { get; set; }

    public FakeForest Domain(string netBios, string dns)
    {
        _domains[netBios] = dns;
        _domains[dns] = dns;
        return this;
    }

    public void Account(string dn, string sid, int? primaryGroupRid)
        => _objects[dn] = new Entry(dn, sid, "user", primaryGroupRid, null, null);

    public void Group(string dn, string sid, DirectoryGroupScope scope, string sam, params string[] members)
    {
        _objects[dn] = new Entry(dn, sid, "group", null, scope, sam);
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

    private static DirectoryGroup AsGroup(Entry e) => new(e.Dn, e.Sid!, e.Scope!.Value);

    private static string CommonName(string dn) => ProtectedPrincipalService.ExtractCnFromDn(dn) ?? dn;

    public DirectoryTarget ReadTarget(string targetDn, string server)
    {
        Record($"ReadTarget {server}", nameof(ReadTarget));
        // A DC answers only for its own domain's objects.
        if (!_objects.TryGetValue(targetDn, out var o) || !SameDomain(DomainOf(o.Dn), server))
            throw new InvalidOperationException("No such object in that domain.");
        return new DirectoryTarget(o.Sid, o.ObjectClass, o.PrimaryGroupRid);
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

    public IReadOnlyList<DirectoryGroup> GroupsContainingAny(IReadOnlyList<string> memberDns, string server)
    {
        Record($"GroupsContainingAny {server}", nameof(GroupsContainingAny));
        return Closure(memberDns, GroupsIn(server));
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

    public IReadOnlyList<DirectoryGroup> UniversalGroupsContainingAny(IReadOnlyList<string> memberDns)
    {
        // The global catalog: every universal group's links, in every domain, and no other group's.
        Record("UniversalGroupsContainingAny forest", nameof(UniversalGroupsContainingAny));
        return Closure(memberDns, GroupsIn(null).Where(g => g.Scope == DirectoryGroupScope.Universal));
    }

    public IReadOnlyList<string> FindObjectDnsBySid(string sid)
    {
        Record($"FindObjectDnsBySid {sid}", nameof(FindObjectDnsBySid));
        return _objects.Values
            .Where(o => string.Equals(o.Sid, sid, StringComparison.OrdinalIgnoreCase))
            .Select(o => o.Dn)
            .ToList();
    }

    /// <summary>The groups among <paramref name="holders"/> whose member chain, through those same groups' links, reaches a start DN.</summary>
    private List<DirectoryGroup> Closure(IReadOnlyList<string> startDns, IEnumerable<Entry> holders)
    {
        var candidates = holders.ToList();
        var reached = new HashSet<string>(startDns, StringComparer.OrdinalIgnoreCase);
        var found = new List<Entry>();
        bool grew;
        do
        {
            grew = false;
            foreach (var g in candidates.Where(g => !reached.Contains(g.Dn)))
            {
                if (_members[g.Dn].Any(reached.Contains))
                {
                    reached.Add(g.Dn);
                    found.Add(g);
                    grew = true;
                }
            }
        } while (grew);
        return found.Select(AsGroup).ToList();
    }
}
