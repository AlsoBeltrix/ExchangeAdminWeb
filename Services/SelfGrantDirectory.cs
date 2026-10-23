using System.Collections;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Principal;

namespace ExchangeAdminWeb.Services;

/// <summary>One directory object as the self-grant check reads it (aud-a43).</summary>
internal sealed record SelfGrantDirectoryObject(
    string DistinguishedName,
    Guid ObjectGuid,
    string ObjectClass,
    string? UserPrincipalName,
    string? PrimarySmtpAddress,
    IReadOnlyList<string> SmtpAddresses,
    string? Sid,
    int? PrimaryGroupId)
{
    internal bool IsGroup => string.Equals(ObjectClass, "group", StringComparison.OrdinalIgnoreCase);

    internal bool CarriesAddress(string address) =>
        SmtpAddresses.Any(a => string.Equals(a, address.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The Active Directory reads of one self-grant check (aud-a43): the signed-in operator, the objects a grantee can name,
/// and whether a group holds the operator.
/// </summary>
internal sealed class SelfGrantDirectory : IDisposable
{
    private static readonly string[] Properties =
        ["objectClass", "userPrincipalName", "mail", "proxyAddresses", "objectSid", "primaryGroupID"];

    private readonly Runspace _runspace;
    private readonly PowerShell _ps;
    private readonly string _globalCatalog;

    private SelfGrantDirectory(Runspace runspace, PowerShell ps, string globalCatalog)
    {
        _runspace = runspace;
        _ps = ps;
        _globalCatalog = globalCatalog;
    }

    internal static Runspace DefaultRunspace()
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        return RunspaceFactory.CreateRunspace(iss);
    }

    internal static SelfGrantDirectory Open(Func<Runspace> createRunspace)
    {
        var runspace = createRunspace();
        PowerShell? ps = null;
        try
        {
            runspace.Open();
            ps = PowerShell.Create();
            ps.Runspace = runspace;
            ActiveDirectoryModuleImport.Import(ps);
            return new SelfGrantDirectory(runspace, ps, GlobalCatalog(ps));
        }
        catch
        {
            ps?.Dispose();
            runspace.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _ps.Dispose();
        _runspace.Dispose();
    }

    internal IReadOnlyList<SelfGrantDirectoryObject> FindBySid(string sid) =>
        Search(ProtectedGroupMembership.GroupLookupFilter(GroupLookupKey.Sid, sid), _globalCatalog);

    internal IReadOnlyList<SelfGrantDirectoryObject> FindByUserPrincipalName(string userPrincipalName) =>
        Search($"(userPrincipalName={Escape(userPrincipalName.Trim())})", _globalCatalog);

    internal IReadOnlyList<SelfGrantDirectoryObject> FindNamedBy(string identity, string? canonicalAddress) =>
        Search(GranteeFilter(identity, canonicalAddress), _globalCatalog);

    internal bool HasDirectMember(SelfGrantDirectoryObject group, SelfGrantDirectoryObject member)
    {
        var server = GroupManagementService.ServerFromDn(group.DistinguishedName)
            ?? throw new InvalidOperationException($"the group '{group.DistinguishedName}' names no domain");

        var read = Search(ProtectedGroupMembership.ExactDnFilter(group.DistinguishedName), server);
        if (read.Count != 1)
            throw new InvalidOperationException(
                $"the group's own domain returned {read.Count} objects for '{group.DistinguishedName}'; exactly one was expected");

        return Search(DirectMemberFilter(group.DistinguishedName, member.DistinguishedName), server).Count > 0;
    }

    internal static string GranteeFilter(string identity, string? canonicalAddress)
    {
        var value = identity?.Trim();
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException("a blank grantee cannot be looked up");

        var escaped = Escape(value);
        var clauses = new List<string>
        {
            $"(userPrincipalName={escaped})",
            $"(mail={escaped})",
            $"(proxyAddresses=smtp:{escaped})",
            $"(mailNickname={escaped})",
            $"(sAMAccountName={escaped})",
            $"(name={escaped})",
        };

        var slash = value.IndexOf('\\');
        if (slash > 0 && slash < value.Length - 1 && !value.AsSpan(0, slash).Contains('='))
            clauses.Add($"(sAMAccountName={Escape(value[(slash + 1)..].Trim())})");

        if (value.Contains('='))
            clauses.Add($"(distinguishedName={escaped})");

        var address = canonicalAddress?.Trim();
        if (!string.IsNullOrEmpty(address) && !string.Equals(address, value, StringComparison.OrdinalIgnoreCase))
        {
            var escapedAddress = Escape(address);
            clauses.Add($"(mail={escapedAddress})");
            clauses.Add($"(proxyAddresses=smtp:{escapedAddress})");
        }

        return "(|" + string.Concat(clauses) + ")";
    }

    internal static string DirectMemberFilter(string groupDn, string memberDn) =>
        $"(&{ProtectedGroupMembership.ExactDnFilter(groupDn)}(member={Escape(memberDn)}))";

    private static string Escape(string value) => ProtectedPrincipalService.EscapeLdapFilter(value);

    private static string GlobalCatalog(PowerShell ps)
    {
        ps.Commands.Clear();
        ps.Streams.Error.Clear();
        try
        {
            ps.AddScript("(Get-ADForest -ErrorAction Stop).GlobalCatalogs | Select-Object -First 1");
            var host = ps.Invoke().FirstOrDefault(o => o is not null)?.ToString();
            if (ps.HadErrors || string.IsNullOrWhiteSpace(host))
                throw new InvalidOperationException("the forest reported no global catalog");

            return $"{host.Trim()}:3268";
        }
        finally
        {
            ps.Commands.Clear();
            ps.Streams.Error.Clear();
        }
    }

    private List<SelfGrantDirectoryObject> Search(string filter, string server)
    {
        _ps.Commands.Clear();
        _ps.Streams.Error.Clear();
        try
        {
            _ps.AddCommand("Get-ADObject")
               .AddParameter("LDAPFilter", filter)
               .AddParameter("Server", server)
               .AddParameter("Properties", Properties)
               .AddParameter("ErrorAction", "Stop");

            Collection<PSObject> rows = _ps.Invoke();
            if (_ps.HadErrors)
                throw new InvalidOperationException(
                    _ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Get-ADObject reported an error");

            return rows.Select(ToObject).ToList();
        }
        finally
        {
            _ps.Commands.Clear();
            _ps.Streams.Error.Clear();
        }
    }

    internal static SelfGrantDirectoryObject ToObject(PSObject? row)
    {
        if (row is null)
            throw new InvalidOperationException("the directory returned an empty row");

        var dn = Text(row.Properties["DistinguishedName"]?.Value);
        var guid = AsGuid(row.Properties["ObjectGUID"]?.Value);
        var objectClass = ObjectClassOf(row.Properties["ObjectClass"]?.Value);
        if (dn is null || guid is null || objectClass is null)
            throw new InvalidOperationException("the directory returned an object without a readable DN, objectGUID or class");

        var proxies = Strings(row.Properties["proxyAddresses"]?.Value);
        var addresses = proxies
            .Where(p => p.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[5..].Trim())
            .Where(p => p.Length > 0)
            .ToList();
        var primary = proxies
            .Where(p => p.StartsWith("SMTP:", StringComparison.Ordinal))
            .Select(p => p[5..].Trim())
            .FirstOrDefault(p => p.Length > 0);
        var mail = Text(row.Properties["mail"]?.Value);
        if (mail is not null && !addresses.Contains(mail, StringComparer.OrdinalIgnoreCase))
            addresses.Add(mail);

        return new SelfGrantDirectoryObject(
            dn, guid.Value, objectClass,
            Text(row.Properties["userPrincipalName"]?.Value),
            primary ?? mail,
            addresses,
            AsSid(row.Properties["objectSid"]?.Value),
            AsInt(row.Properties["primaryGroupID"]?.Value));
    }

    private static object? Unwrap(object? value) => value is PSObject wrapped ? wrapped.BaseObject : value;

    private static string? Text(object? value)
    {
        var text = Unwrap(value)?.ToString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static List<string> Strings(object? value)
    {
        value = Unwrap(value);
        var items = value switch
        {
            null => [],
            string single => [single],
            IEnumerable many => many.Cast<object?>().Select(Text).Where(t => t is not null).Select(t => t!).ToList(),
            _ => new List<string?> { Text(value) }.Where(t => t is not null).Select(t => t!).ToList(),
        };
        return items;
    }

    private static string? ObjectClassOf(object? value)
    {
        value = Unwrap(value);
        return value is string or null ? Text(value) : Strings(value).LastOrDefault();
    }

    private static Guid? AsGuid(object? value) => Unwrap(value) switch
    {
        Guid g when g != Guid.Empty => g,
        string s when Guid.TryParse(s, out var parsed) && parsed != Guid.Empty => parsed,
        byte[] { Length: 16 } bytes => new Guid(bytes),
        _ => null,
    };

    private static string? AsSid(object? value)
    {
        try
        {
            return Unwrap(value) switch
            {
                SecurityIdentifier sid => sid.Value,
                byte[] binary => new SecurityIdentifier(binary, 0).Value,
                string text when SelfServiceGroups.SelfServiceGroupService.IsSecurityIdentifier(text.Trim()) => text.Trim(),
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static int? AsInt(object? value) => Unwrap(value) switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        string s when int.TryParse(s, out var parsed) => parsed,
        _ => null,
    };
}
