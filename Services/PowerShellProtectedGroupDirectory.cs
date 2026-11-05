using System.Collections.ObjectModel;
using System.Management.Automation;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Performs the protected-group membership reads against live Active Directory, in the runspace
/// and under the credential its caller holds: the protected-principal check's, and since aud-a60
/// the self-grant check's, which is the same directory-read credential.
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
///
/// kd-88. Every read is anchored - a base read at a DN, an equality search on an indexed key, or the in-chain question put
/// to one named group - and none searches a domain or the forest for whatever contains the target. The two that did, every
/// group whose member chain reaches a DN across the target's domain and every universal group at the global catalog, are
/// gone: the first ran into the domain controller's two-minute timeout on the dev forest.
/// </remarks>
internal sealed class PowerShellProtectedGroupDirectory : IProtectedGroupDirectory, IProtectedOuDirectory
{
    private static readonly string[] GroupProperties = ["objectSid", "GroupScope", "GroupCategory"];

    private readonly PowerShell _ps;
    private readonly PSCredential _credential;
    private string? _globalCatalog;

    internal PowerShellProtectedGroupDirectory(PowerShell ps, PSCredential credential, string? globalCatalog = null)
    {
        _ps = ps;
        _credential = credential;
        _globalCatalog = string.IsNullOrWhiteSpace(globalCatalog) ? null : globalCatalog;
    }

    /// <summary>The global catalog this directory reads through, host:3268, once discovered or handed in; null before.</summary>
    internal string? KnownGlobalCatalog => _globalCatalog;

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

    public IReadOnlyList<string> ReadTokenGroups(string accountDn, string accountSid, string server)
    {
        // kd-88, in kd-87's shape. tokenGroups is constructed only in a base-scope search, so the scope is stated in the
        // command - at the account's own DN, on a domain controller of its own domain - and the filter binds the read to the
        // account by its SID: an object at that DN that is not this account matches nothing and fails below.
        var rows = Run("Get-ADObject", new Dictionary<string, object>
        {
            ["SearchBase"] = accountDn,
            ["SearchScope"] = "Base",
            ["LDAPFilter"] = ProtectedGroupMembership.GroupLookupFilter(GroupLookupKey.Sid, accountSid),
            ["Server"] = server,
            ["Properties"] = new[] { "tokenGroups" },
        });

        if (rows.Count != 1 || rows[0] is null)
            throw new InvalidOperationException(
                $"Reading the account's token groups in its own domain returned {rows.Count} objects; expected exactly one.");

        // Whatever shape the cmdlet produces - SecurityIdentifier, binary, text - read as the access re-check reads it. An
        // empty answer is returned as one; the evaluator refuses a token without the account's primary group.
        return OperatorMembershipDirectory.ParseSids(rows[0].Properties["tokenGroups"]?.Value).ToList();
    }

    public IReadOnlyList<DirectoryMemberOf> ReadMemberOf(IReadOnlyList<string> dns, string? server)
    {
        // kd-88. The named objects' memberOf, by DN - never a search for whatever contains them. A null server is the forest's
        // global catalog, which holds every universal group's links.
        var target = server ?? GlobalCatalog();
        var found = new List<DirectoryMemberOf>();
        foreach (var batch in ProtectedGroupMembership.Batches(dns))
        {
            foreach (var row in Run("Get-ADObject", new Dictionary<string, object>
            {
                ["LDAPFilter"] = ProtectedGroupMembership.AnyDnFilter(batch),
                ["Server"] = target,
                ["Properties"] = new[] { "objectSid", "memberOf" },
            }))
            {
                // A row without a DN cannot say whose links it carries: a failed read, never a skipped one.
                var dn = (row is null ? null : Text(row, "DistinguishedName"))
                         ?? throw new InvalidOperationException("The directory returned an object without a readable DN.");
                found.Add(new DirectoryMemberOf(dn, Text(row!, "objectSid"), DnValues(row!.Properties["memberOf"]?.Value)));
            }
        }
        return found;
    }

    public IReadOnlyList<DirectoryObjectGuid> ReadObjectGuids(IReadOnlyList<string> dns, string server)
    {
        // The named containers by DN, in the domain that holds them - never a search for whatever is beneath an OU.
        var found = new List<DirectoryObjectGuid>();
        foreach (var batch in ProtectedGroupMembership.Batches(dns))
        {
            foreach (var row in Run("Get-ADObject", new Dictionary<string, object>
            {
                ["LDAPFilter"] = ProtectedGroupMembership.AnyDnFilter(batch),
                ["Server"] = server,
                ["Properties"] = new[] { "ObjectGUID" },
            }))
            {
                // A row without a DN is a failed read, never a missing object.
                var dn = (row is null ? null : Text(row, "DistinguishedName"))
                         ?? throw new InvalidOperationException("The directory returned an object without a readable DN.");
                found.Add(new DirectoryObjectGuid(dn, Text(row!, "ObjectGUID")));
            }
        }
        return found;
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
    /// docs/ProtectedPrincipalIdentifiers-Plan.md C.2 step 4. Every object the global catalog holds under objectSid
    /// <paramref name="sid"/>: one indexed equality read, the properties the binding needs - all in the partial attribute set.
    /// Not on <see cref="IProtectedGroupDirectory"/>: the protected-principal service's binding is its only caller.
    /// </summary>
    internal IReadOnlyList<DirectorySidObject> FindObjectsBySidAtCatalog(string sid)
    {
        var found = new List<DirectorySidObject>();
        foreach (var row in Run("Get-ADObject", new Dictionary<string, object>
        {
            ["LDAPFilter"] = ADDirectorySearchService.BuildSidFilter(sid),
            ["Server"] = GlobalCatalog(),
            ["Properties"] = new[] { "objectSid", "objectGUID", "objectClass", "sAMAccountName", "userPrincipalName", "mail", "displayName" },
        }))
        {
            // A row that cannot be read is a failed read, never a missing object: dropping it would turn "two" into "one".
            if (row is null)
                throw new InvalidOperationException("The directory returned an empty row for a read by SID.");
            found.Add(new DirectorySidObject(
                DistinguishedName: Text(row, "DistinguishedName"),
                ObjectGuid: Text(row, "ObjectGUID"),
                ObjectSid: Text(row, "objectSid"),
                ObjectClass: Text(row, "ObjectClass"),
                SamAccountName: Text(row, "SamAccountName"),
                UserPrincipalName: Text(row, "userPrincipalName"),
                Mail: Text(row, "mail"),
                DisplayName: Text(row, "displayName")));
        }
        return found;
    }

    /// <summary>
    /// docs/DirectoryObjectVerify-Plan.md B.1. The objects <paramref name="ldapFilter"/> matches - an equality filter on objectSid,
    /// objectGUID or distinguishedName terms - on <paramref name="server"/>, or the forest's global catalog when null, with the
    /// attributes Verify compares. A row without its DN is a failed read, never a missing object.
    /// </summary>
    internal IReadOnlyList<VerifyDirectoryObject> FindVerifyObjects(string ldapFilter, string? server)
    {
        var found = new List<VerifyDirectoryObject>();
        foreach (var row in Run("Get-ADObject", new Dictionary<string, object>
        {
            ["LDAPFilter"] = ldapFilter,
            ["Server"] = server ?? GlobalCatalog(),
            ["Properties"] = new[] { "objectSid", "objectGUID", "objectClass", "userPrincipalName", "mail", "distinguishedName", "sAMAccountName" },
        }))
        {
            var dn = (row is null ? null : Text(row, "DistinguishedName"))
                     ?? throw new InvalidOperationException("The directory returned an object without a readable DN.");
            found.Add(new VerifyDirectoryObject(
                DistinguishedName: dn,
                ObjectSid: Text(row!, "objectSid"),
                ObjectGuid: Text(row!, "ObjectGUID"),
                ObjectClass: Text(row!, "ObjectClass"),
                UserPrincipalName: Text(row!, "userPrincipalName"),
                Mail: Text(row!, "mail"),
                SamAccountName: Text(row!, "sAMAccountName")));
        }
        return found;
    }

    /// <summary>
    /// docs/DirectoryObjectVerify-Plan.md B.1, B.2, A6 (slice V4). Whether an organizational unit is at <paramref name="dn"/>:
    /// <c>Get-ADOrganizationalUnit -Identity</c>, the Emergency Disable module's own precondition, read on
    /// <paramref name="server"/> (a domain controller of the DN's own domain) with the directory-read credential. True when it
    /// returns the OU; false when the AD module answers that there is no such object (<see cref="IsConfirmedAbsence"/>) - its
    /// answer, too, for a container at that DN that is not an OU, which the module refuses as it refuses a missing one. Any
    /// other failure, and an answer that is not exactly one object, throws: unreadable, never missing.
    /// </summary>
    internal bool OrganizationalUnitExists(string dn, string server)
    {
        Collection<PSObject> rows;
        try
        {
            rows = Run("Get-ADOrganizationalUnit", new Dictionary<string, object>
            {
                ["Identity"] = dn,
                ["Server"] = server,
            });
        }
        catch (Exception ex) when (IsConfirmedAbsence(ex, dn))
        {
            return false;
        }

        return rows.Count == 1 && rows[0] is not null
            ? true
            : throw new InvalidOperationException($"Get-ADOrganizationalUnit returned {rows.Count} objects for '{dn}'; expected one.");
    }

    /// <summary>
    /// docs/DirectoryObjectVerify-Plan.md B.1 (R4). Whether a failed read by identity was the AD module saying there is no such
    /// object - <c>ADIdentityNotFoundException</c>, under the <c>ObjectNotFound</c> category - and nothing else: the shape the
    /// Migration module reads for its own Get-ADGroup lookup (<see cref="ExchangeServiceBase.IsNotFound"/>). Never a missing
    /// cmdlet, never a refusal or a timeout.
    /// </summary>
    internal static bool IsConfirmedAbsence(Exception ex, string identity) => ExchangeServiceBase.IsNotFound(ex, identity);

    /// <summary>
    /// docs/DirectoryObjectVerify-Plan.md B.2, A5 (slice V4). The forest's domains, asked as the Migration module asks
    /// (<see cref="MigrationService.ForestDomains"/>): in this session, under its own identity, as that module's check runs.
    /// </summary>
    internal IReadOnlyList<string> ForestDomainNames()
    {
        _ps.Commands.Clear();
        _ps.Streams.Error.Clear();
        return MigrationService.ForestDomains(_ps);
    }

    /// <summary>
    /// docs/DirectoryObjectVerify-Plan.md B.2, A5 (slice V4). One excluded-group value through the Migration module's own
    /// per-domain lookup (<see cref="MigrationService.TryResolveExcludedGroup"/>), in this session, under its own identity - the
    /// lookup and the identity that module's check uses in the same warm sessions, so Verify answers what the module would.
    /// </summary>
    internal MigrationService.ExcludedGroupResolution ResolveExcludedGroup(string configured, IReadOnlyList<string> domains)
    {
        _ps.Commands.Clear();
        _ps.Streams.Error.Clear();
        return MigrationService.TryResolveExcludedGroup(_ps, configured, domains);
    }

    /// <summary>
    /// docs/DirectoryObjectVerify-Plan.md B.1 (F6). The DNS name of the domain of this forest that <paramref name="dnsDomain"/>
    /// names, from the forest's CN=Partitions crossRefs - the check <see cref="ADDirectorySearchService"/> makes for an OU's
    /// domain - or null when the forest has no such domain. Throws when the read fails or names more than one.
    /// </summary>
    internal string? ForestDomainServer(string dnsDomain)
    {
        var rootDse = Run("Get-ADRootDSE", new Dictionary<string, object>());
        var configNc = rootDse.Count == 1 ? Text(rootDse[0], "configurationNamingContext") : null;
        if (string.IsNullOrWhiteSpace(configNc))
            throw new InvalidOperationException("Could not read the forest's configuration naming context.");

        var partitions = Run("Get-ADObject", new Dictionary<string, object>
        {
            ["SearchBase"] = $"CN=Partitions,{configNc}",
            ["LDAPFilter"] = ProtectedGroupMembership.DomainCrossRefFilter(dnsDomain),
            ["Properties"] = new[] { "nETBIOSName", "dnsRoot" },
        });

        return ADDirectorySearchService.ForestDomainFromCrossRefs(
            dnsDomain, partitions.Where(p => p is not null).Select(p => p.Properties["dnsRoot"]?.Value).ToList());
    }

    /// <summary>
    /// The user-class objects <paramref name="ldapFilter"/> matches on <paramref name="server"/> (a domain controller of that
    /// domain, or - null - the global catalog): the resolver's user lookup for Verify's not-converted rows, read with the
    /// directory-read credential in the press's one session.
    /// </summary>
    internal IReadOnlyList<DirectoryUserObject> FindUsers(string ldapFilter, string? server)
    {
        var users = new List<DirectoryUserObject>();
        foreach (var row in Run("Get-ADObject", new Dictionary<string, object>
        {
            ["LDAPFilter"] = ldapFilter,
            ["Server"] = server ?? GlobalCatalog(),
            ["Properties"] = new[] { "objectSid" },
        }))
        {
            var dn = (row is null ? null : Text(row, "DistinguishedName"))
                     ?? throw new InvalidOperationException("The directory returned an object without a readable DN.");
            users.Add(new DirectoryUserObject(dn, Text(row!, "objectSid")));
        }
        return users;
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
            DirectoryReadOperation.CountRead(); // kd-88: the timing line's count
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

            DirectoryReadOperation.CountRead(); // kd-88: the timing line's count
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

            // kd-88. A category that cannot be read is not "Security": the evaluator then reads the group's membership links
            // rather than trusting a token to carry it.
            groups.Add(new DirectoryGroup(dn, sid, scope.Value, ProtectedGroupMembership.IsSecurityCategory(Text(row, "GroupCategory"))));
        }
        return groups;
    }

    /// <summary>kd-88. A multi-valued DN attribute (memberOf) as its values; none when the attribute is absent.</summary>
    private static List<string> DnValues(object? value)
    {
        var values = new List<string>();
        switch (value)
        {
            case null:
                break;
            case PSObject wrapped:
                return DnValues(wrapped.BaseObject);
            case string single:
                if (!string.IsNullOrWhiteSpace(single))
                    values.Add(single.Trim());
                break;
            case System.Collections.IEnumerable many:
                foreach (var item in many)
                {
                    var text = (item is PSObject wrapped ? wrapped.BaseObject : item)?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        values.Add(text.Trim());
                }
                break;
            default:
                var other = value.ToString();
                if (!string.IsNullOrWhiteSpace(other))
                    values.Add(other.Trim());
                break;
        }
        return values;
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
