using System.Collections;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Principal;

namespace ExchangeAdminWeb.Services;

/// <summary>One directory object as the self-grant check reads it (aud-a43).</summary>
/// <param name="PrimarySmtpAddress">The address on the uppercase <c>SMTP:</c> proxy, else <c>mail</c>; null when neither.</param>
/// <param name="SmtpAddresses">Every <c>smtp:</c> proxy address and <c>mail</c>: what the object can be addressed as.</param>
/// <param name="Sid">The canonical objectSid, or null when the object has none (a contact).</param>
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
/// <remarks>
/// One session per check: a fresh runspace, the ActiveDirectory module (<see cref="ActiveDirectoryModuleImport"/>) and the
/// host forest's global catalog, discovered from <c>Get-ADForest</c> at runtime - the
/// <c>PowerShellProtectedGroupDirectory.GlobalCatalog</c> idiom. Nothing is named, defaulted or configured (Architectural
/// Invariant 7). The reads run under the app pool's identity, as the other read-only lookups of the signed-in operator do
/// (<c>OperatorEmailResolver</c>, <c>OperatorMembershipDirectory</c>); the protected-principal directory-read secret stays
/// with the protected-principal check.
///
/// Every read is <c>Get-ADObject</c> under <c>-ErrorAction Stop</c>, and a cmdlet that wrote to its error stream is a failed
/// read; so is a row without a DN, an objectGUID or a class. The validator reads "no rows" as an answer, so nothing here
/// may report one for a read that did not complete.
/// </remarks>
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
        Groups = PowerShellProtectedGroupDirectory.UnderSessionIdentity(ps, globalCatalog);
    }

    internal IProtectedGroupDirectory Groups { get; }

    /// <summary>The production session, built as every directory path here builds it; not yet opened.</summary>
    internal static Runspace DefaultRunspace()
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        return RunspaceFactory.CreateRunspace(iss);
    }

    /// <summary>
    /// Opens one check's session. Throws when the runspace, the module or the forest's catalog cannot be had; nothing is
    /// left open then.
    /// </summary>
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

    /// <summary>Every object in the forest whose objectSid is <paramref name="sid"/>.</summary>
    internal IReadOnlyList<SelfGrantDirectoryObject> FindBySid(string sid) =>
        Search(ProtectedGroupMembership.GroupLookupFilter(GroupLookupKey.Sid, sid), _globalCatalog);

    /// <summary>Every object in the forest whose userPrincipalName is <paramref name="userPrincipalName"/>.</summary>
    internal IReadOnlyList<SelfGrantDirectoryObject> FindByUserPrincipalName(string userPrincipalName) =>
        Search($"(userPrincipalName={Escape(userPrincipalName.Trim())})", _globalCatalog);

    /// <summary>Every object in the forest a grantee identity can name (<see cref="GranteeFilter"/>).</summary>
    internal IReadOnlyList<SelfGrantDirectoryObject> FindNamedBy(string identity, string? canonicalAddress) =>
        Search(GranteeFilter(identity, canonicalAddress), _globalCatalog);

    /// <summary>
    /// Every object a grantee identity can name: the identities Exchange accepts for a grantee that the directory holds -
    /// userPrincipalName, mail, any SMTP proxy address, the alias (mailNickname), sAMAccountName (also the name half of
    /// <c>DOMAIN\name</c>), the name, and a DN - and Exchange's canonical address for it when it named one.
    /// </summary>
    /// <remarks>
    /// A backslash before any '=' is a <c>DOMAIN\</c> prefix; a DN carries '=' before any backslash (ProtectedGroupEntry.Parse's
    /// discriminator). Every value is escaped (RFC 4515), so a typed '*' cannot turn an equality into a wildcard. A blank
    /// identity throws: there is nothing to search for, and the check refuses.
    /// </remarks>
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

    private static string Escape(string value) => ProtectedPrincipalService.EscapeLdapFilter(value);

    // The forest's global catalog, "host:3268", from the host's own forest. AddScript, not AddCommand and a property read:
    // GlobalCatalogs read off the returned object is empty in these runspaces (ADDirectorySearchService.ResolveGlobalCatalog).
    // Never a bare port - the AD cmdlets accept ":3268" and quietly serve the local domain.
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

    /// <summary>
    /// One row, strictly: a DN, an objectGUID and a class, or the read failed - a row dropped would turn "one match" into
    /// "none". The primary address is the uppercase <c>SMTP:</c> proxy, else <c>mail</c>; every <c>smtp:</c> proxy and
    /// <c>mail</c> are kept.
    /// </summary>
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

    // Get-ADObject reports the most specific class; read as the multi-valued attribute, the last value is that class.
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
