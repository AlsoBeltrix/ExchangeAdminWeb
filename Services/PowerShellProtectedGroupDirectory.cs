using System.Collections.ObjectModel;
using System.Management.Automation;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Performs the protected-group membership reads against live Active Directory, in the runspace
/// and under the credential the protected-principal check already holds.
/// </summary>
/// <remarks>
/// Nothing here decides anything. Every judgement - what an empty answer means, which domain a
/// group is asked in, when the check is incomplete - lives in
/// <see cref="ProtectedGroupMembership.Evaluate"/>, which is what makes it testable. This class
/// only routes each read to the server it is told and reports what came back.
///
/// Every read uses <c>-ErrorAction Stop</c> and a cmdlet that wrote to its error stream is
/// treated as a failed read: rows from a run that also reported an error prove nothing about how
/// many objects exist, and the evaluator reads "no rows" as evidence.
/// </remarks>
internal sealed class PowerShellProtectedGroupDirectory : IProtectedGroupDirectory
{
    private static readonly string[] GroupProperties = ["objectSid", "GroupScope"];

    private readonly PowerShell _ps;
    private readonly PSCredential _credential;
    private string? _globalCatalog;

    internal PowerShellProtectedGroupDirectory(PowerShell ps, PSCredential credential)
    {
        _ps = ps;
        _credential = credential;
    }

    public DirectoryTarget ReadTarget(string targetDn, string server)
    {
        // Get-ADObject, not Get-ADUser: the target may be a group, and Get-ADUser answers a group
        // with zero rows and no error (docs/GroupMemberNesting-Plan.md S1).
        var rows = Run("Get-ADObject", new Dictionary<string, object>
        {
            ["LDAPFilter"] = ProtectedGroupMembership.ExactDnFilter(targetDn),
            ["Server"] = server,
            ["Properties"] = new[] { "objectSid", "objectClass", "primaryGroupID" },
        });

        if (rows.Count != 1)
            throw new InvalidOperationException(
                $"Reading the target in its own domain returned {rows.Count} objects; expected exactly one.");

        var row = rows[0];
        return new DirectoryTarget(
            Sid: Text(row, "objectSid"),
            ObjectClass: Text(row, "ObjectClass"),
            PrimaryGroupId: AsInt(row.Properties["primaryGroupID"]?.Value));
    }

    public string ResolveDomainServer(string domain)
    {
        // The NetBIOS-to-DNS mapping lives in the forest's CN=Partitions crossRefs - the same
        // read SectionAccessGroupDirectory.ResolveDomainServer makes - rather than being guessed
        // from the name, which is wrong whenever the two differ.
        var rootDse = Run("Get-ADRootDSE", new Dictionary<string, object>());
        var configNc = rootDse.Count == 1 ? Text(rootDse[0], "configurationNamingContext") : null;
        if (string.IsNullOrWhiteSpace(configNc))
            throw new InvalidOperationException("Could not read the forest's configuration naming context.");

        var partitions = Run("Get-ADObject", new Dictionary<string, object>
        {
            ["SearchBase"] = $"CN=Partitions,{configNc}",
            ["LDAPFilter"] = ProtectedGroupMembership.DomainCrossRefFilter(domain),
            ["Properties"] = new[] { "nETBIOSName", "dnsRoot" },
        });

        var problem = ExchangeAdminWeb.Authorization.SectionAccessDirectoryReading.PartitionMatchProblem(domain, partitions.Count);
        if (problem is not null)
            throw new InvalidOperationException(problem);

        return ExchangeAdminWeb.Authorization.SectionAccessDirectoryReading.UnwrapDnsRoot(partitions[0].Properties["dnsRoot"]?.Value)
               ?? throw new InvalidOperationException($"Domain '{domain}' has no usable dnsRoot.");
    }

    public IReadOnlyList<DirectoryGroup> FindGroups(GroupLookupKey key, string value, string? server)
    {
        // A null server is the whole forest. The global catalog holds every group's
        // sAMAccountName, name, objectSid and scope.
        var target = server ?? GlobalCatalog();

        return ToGroups(Run("Get-ADGroup", new Dictionary<string, object>
        {
            ["LDAPFilter"] = ProtectedGroupMembership.GroupLookupFilter(key, value),
            ["Server"] = target,
            ["Properties"] = GroupProperties,
        }));
    }

    public IReadOnlyList<DirectoryGroup> GroupsContainingAny(IReadOnlyList<string> memberDns, string server)
    {
        var found = new Dictionary<string, DirectoryGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in ProtectedGroupMembership.Batches(memberDns))
        {
            foreach (var group in ToGroups(Run("Get-ADGroup", new Dictionary<string, object>
            {
                ["LDAPFilter"] = ProtectedGroupMembership.ContainsAnyFilter(batch),
                ["Server"] = server,
                ["Properties"] = GroupProperties,
            })))
            {
                found[group.Sid] = group;
            }
        }
        return found.Values.ToList();
    }

    public bool DomainLocalGroupContainsAny(string groupDn, IReadOnlyList<string> memberDns, string server)
    {
        foreach (var batch in ProtectedGroupMembership.Batches(memberDns))
        {
            var rows = Run("Get-ADGroup", new Dictionary<string, object>
            {
                ["LDAPFilter"] = ProtectedGroupMembership.GroupContainsAnyFilter(groupDn, batch),
                ["Server"] = server,
            });
            if (rows.Count > 0)
                return true;
        }
        return false;
    }

    public IReadOnlyList<DirectoryGroup> UniversalGroupsContainingAny(IReadOnlyList<string> memberDns)
    {
        // The global catalog holds every universal group's member links, for every domain of the
        // forest - the one place that is true whichever DC answers for the target (aud-a3c).
        var catalog = GlobalCatalog();
        var found = new Dictionary<string, DirectoryGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in ProtectedGroupMembership.Batches(memberDns))
        {
            foreach (var group in ToGroups(Run("Get-ADGroup", new Dictionary<string, object>
            {
                ["LDAPFilter"] = ProtectedGroupMembership.UniversalContainsAnyFilter(batch),
                ["Server"] = catalog,
                ["Properties"] = GroupProperties,
            })))
            {
                found[group.Sid] = group;
            }
        }
        return found.Values.ToList();
    }

    public IReadOnlyList<string> FindObjectDnsBySid(string sid)
    {
        var dns = new List<string>();
        foreach (var row in Run("Get-ADObject", new Dictionary<string, object>
        {
            ["LDAPFilter"] = ProtectedGroupMembership.GroupLookupFilter(GroupLookupKey.Sid, sid),
            ["Server"] = GlobalCatalog(),
        }))
        {
            // A row without a DN is a failed read, never a missing object.
            dns.Add((row is null ? null : Text(row, "DistinguishedName"))
                    ?? throw new InvalidOperationException("The directory returned an object without a readable DN."));
        }
        return dns;
    }

    /// <summary>
    /// The forest's global catalog, <c>host:3268</c>, discovered from the host's own forest.
    /// </summary>
    /// <remarks>
    /// AddScript, not AddCommand plus a property read: reading <c>GlobalCatalogs</c> out of the
    /// returned object yields an empty value in these runspaces - measured, and recorded in
    /// <c>ADDirectorySearchService.ResolveGlobalCatalog</c>, whose idiom this copies. Never
    /// returns a bare port: Get-ADGroup accepts <c>:3268</c> and quietly serves the local domain.
    /// </remarks>
    private string GlobalCatalog()
    {
        if (_globalCatalog is not null)
            return _globalCatalog;

        _ps.Commands.Clear();
        _ps.Streams.Error.Clear();
        try
        {
            _ps.AddScript("param($cred) (Get-ADForest -Credential $cred -ErrorAction Stop).GlobalCatalogs | Select-Object -First 1")
               .AddArgument(_credential);
            var host = _ps.Invoke().FirstOrDefault(o => o is not null)?.ToString();
            if (_ps.HadErrors || string.IsNullOrWhiteSpace(host))
                throw new InvalidOperationException("The forest reported no global catalog.");

            _globalCatalog = $"{host.Trim()}:3268";
            return _globalCatalog;
        }
        finally
        {
            _ps.Commands.Clear();
            _ps.Streams.Error.Clear();
        }
    }

    private Collection<PSObject> Run(string command, Dictionary<string, object> parameters)
    {
        _ps.Commands.Clear();
        _ps.Streams.Error.Clear();
        try
        {
            var cmd = _ps.AddCommand(command);
            foreach (var (name, value) in parameters)
                cmd.AddParameter(name, value);
            cmd.AddParameter("Credential", _credential)
               .AddParameter("ErrorAction", "Stop");

            var rows = _ps.Invoke();
            if (_ps.HadErrors)
            {
                var error = _ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? $"{command} reported an error";
                throw new InvalidOperationException(error);
            }
            return rows;
        }
        finally
        {
            _ps.Commands.Clear();
            _ps.Streams.Error.Clear();
        }
    }

    private static List<DirectoryGroup> ToGroups(Collection<PSObject> rows)
    {
        var groups = new List<DirectoryGroup>(rows.Count);
        foreach (var row in rows)
        {
            // A row the evaluator cannot identify or classify is a failed read, never a skipped
            // one: dropping it would turn "one match" into "none" or "several" into "one".
            if (row is null)
                throw new InvalidOperationException("The directory returned an empty group row.");

            var dn = Text(row, "DistinguishedName");
            var sid = Text(row, "objectSid");
            var scope = ProtectedGroupMembership.ParseScope(Text(row, "GroupScope"));
            if (string.IsNullOrWhiteSpace(dn) || string.IsNullOrWhiteSpace(sid) || scope is null)
                throw new InvalidOperationException("The directory returned a group without a readable DN, SID or scope.");

            groups.Add(new DirectoryGroup(dn, sid, scope.Value));
        }
        return groups;
    }

    private static string? Text(PSObject row, string property)
    {
        var text = row.Properties[property]?.Value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static int? AsInt(object? value) => value switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        string s when int.TryParse(s, out var parsed) => parsed,
        _ => null,
    };
}
