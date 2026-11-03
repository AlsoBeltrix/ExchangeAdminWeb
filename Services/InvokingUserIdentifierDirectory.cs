using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// The directory reads of <c>db convert-identifiers</c> (docs/ProtectedPrincipalIdentifiers-Plan.md K.3 point 1), made as the
/// user who runs the command - an administrator in an elevated shell on the deploy host - in a runspace of its own with the
/// ActiveDirectory module imported.
/// </summary>
/// <remarks>
/// <para>No credential, no Delinea fetch, no <see cref="DirectoryReadOperation"/>: the <c>db</c> mode starts no host and reads
/// no appsettings.json (<see cref="Storage.ConfigDbCli"/>), so the only identity it has is the invoking user's, the way section
/// access's reads run as the app pool's. Deliberately not <see cref="PowerShellProtectedGroupDirectory"/>, every form of which
/// carries the protected-principal directory-read credential (aud-a60): that class serves the protection checks, this one
/// only the one-time conversion.</para>
/// <para>Environment neutrality (repo-guidance Invariant 7): the forest is the host's own - <c>Get-ADForest</c> and
/// <c>Get-ADRootDSE</c> with no identity or server - and its global catalog and NetBIOS-to-DNS mapping are read from it at
/// run time. Nothing is named or defaulted.</para>
/// <para>Every read uses <c>-ErrorAction Stop</c>, and a cmdlet that wrote to its error stream is a failed read: the resolver
/// reads "no rows" as "not found", so rows from a run that also reported an error prove nothing.</para>
/// </remarks>
internal sealed class InvokingUserIdentifierDirectory : IProtectedPrincipalResolveDirectory, IDisposable
{
    private static readonly string[] GroupProperties = ["objectSid", "GroupScope", "GroupCategory"];

    private readonly Runspace _runspace;
    private readonly PowerShell _ps;
    private string? _globalCatalog;

    /// <param name="createRunspace">Production: <see cref="DirectorySessionPool.DefaultRunspace"/>; tests: a stand-in forest.</param>
    internal InvokingUserIdentifierDirectory(Func<Runspace> createRunspace)
    {
        _runspace = createRunspace();
        try
        {
            _runspace.Open();
            _ps = PowerShell.Create();
            _ps.Runspace = _runspace;
            ActiveDirectoryModuleImport.Import(_ps);
        }
        catch
        {
            _ps?.Dispose();
            _runspace.Dispose();
            throw;
        }
    }

    /// <summary>The verb's directory: the default session, the module imported, as the invoking user.</summary>
    internal static InvokingUserIdentifierDirectory Open() => new(DirectorySessionPool.DefaultRunspace);

    public string ResolveDomainServer(string domain)
    {
        // The forest's CN=Partitions crossRefs, as the protection check's directory reads them - never guessed from the name.
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
        var groups = new List<DirectoryGroup>();
        foreach (var row in Run("Get-ADGroup", new Dictionary<string, object>
        {
            ["LDAPFilter"] = ProtectedGroupMembership.GroupLookupFilter(key, value),
            ["Server"] = server ?? GlobalCatalog(),
            ["Properties"] = GroupProperties,
        }))
        {
            // A row that cannot be identified or classified is a failed read, never a skipped one: dropping it would turn
            // "two" into "one".
            if (row is null)
                throw new InvalidOperationException("The directory returned an empty group row.");
            var dn = Text(row, "DistinguishedName");
            var sid = Text(row, "objectSid");
            var scope = ProtectedGroupMembership.ParseScope(Text(row, "GroupScope"));
            if (string.IsNullOrWhiteSpace(dn) || string.IsNullOrWhiteSpace(sid) || scope is null)
                throw new InvalidOperationException("The directory returned a group without a readable DN, SID or scope.");
            groups.Add(new DirectoryGroup(dn, sid, scope.Value, ProtectedGroupMembership.IsSecurityCategory(Text(row, "GroupCategory"))));
        }
        return groups;
    }

    public IReadOnlyList<DirectoryUserObject> FindUsers(string ldapFilter, string? server)
    {
        // An equality search on an indexed key - UPN, mail, sAMAccountName or DN - built by ProtectedPrincipalIdentifierResolver;
        // a null server is the forest, through its global catalog.
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

    public IReadOnlyList<DirectoryObjectGuid> ReadObjectGuids(IReadOnlyList<string> dns, string server)
    {
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
                var dn = (row is null ? null : Text(row, "DistinguishedName"))
                         ?? throw new InvalidOperationException("The directory returned an object without a readable DN.");
                found.Add(new DirectoryObjectGuid(dn, Text(row!, "ObjectGUID")));
            }
        }
        return found;
    }

    /// <summary>
    /// The host's forest's global catalog, <c>host:3268</c>. AddScript, not AddCommand plus a property read: reading
    /// <c>GlobalCatalogs</c> off the returned object yields an empty value in these runspaces (measured, recorded in
    /// <c>ADDirectorySearchService.ResolveGlobalCatalog</c>). Never a bare port, which Get-ADGroup accepts and serves from the
    /// local domain.
    /// </summary>
    private string GlobalCatalog()
    {
        if (_globalCatalog is not null)
            return _globalCatalog;

        _ps.Commands.Clear();
        _ps.Streams.Error.Clear();
        try
        {
            _ps.AddScript("(Get-ADForest -ErrorAction Stop).GlobalCatalogs | Select-Object -First 1");
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
            cmd.AddParameter("ErrorAction", "Stop");

            var rows = _ps.Invoke();
            if (_ps.HadErrors)
                throw new InvalidOperationException(_ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? $"{command} reported an error");
            return rows;
        }
        finally
        {
            _ps.Commands.Clear();
            _ps.Streams.Error.Clear();
        }
    }

    private static string? Text(PSObject row, string property)
    {
        var text = row.Properties[property]?.Value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public void Dispose()
    {
        _ps.Dispose();
        _runspace.Dispose();
    }
}
