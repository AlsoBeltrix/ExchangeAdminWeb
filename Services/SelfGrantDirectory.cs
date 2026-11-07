using System.Collections;
using System.Collections.ObjectModel;
using System.Management.Automation;
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
/// One session per check, warm since kd-88: lent from the directory sessions the protection checks share
/// (<see cref="DirectorySessionPool"/>) - a runspace with the ActiveDirectory module already imported
/// (<see cref="ActiveDirectoryModuleImport"/>) - where every check built a runspace and imported the module before its first
/// read. A read that fails in it throws it away, so a session a failure may have broken is not lent again. The
/// host forest's global catalog is discovered from <c>Get-ADForest</c> at runtime - the
/// <c>PowerShellProtectedGroupDirectory.GlobalCatalog</c> idiom. Nothing is named, defaulted or configured (Architectural
/// Invariant 7). The lookup of the operator by SID runs under the app pool's identity, as the other read-only lookups of the
/// signed-in operator do (<c>OperatorEmailResolver</c>, <c>OperatorMembershipDirectory</c>); a missing or doubled operator
/// refuses. By a UPN claim alone it runs under the directory-read credential (aud-a70 (7)): there none found is an answer. The
/// lookup of the objects a grantee names runs under the protected-principal directory-read credential (aud-a69,
/// <see cref="FindNamedBy"/>): its empty answer is what lets a grantee through, so it may not rest on what the app pool can see.
///
/// Every read is <c>Get-ADObject</c> under <c>-ErrorAction Stop</c>, and a cmdlet that wrote to its error stream is a failed
/// read; so is a row without a DN, an objectGUID or a class. The validator reads "no rows" as an answer, so nothing here
/// may report one for a read that did not complete.
///
/// aud-a52. Whether a group holds the operator is the protected-group evaluator's question, read in this session. aud-a60:
/// under the protected-principal directory-read credential (<see cref="GroupsUnder"/>), not the session's identity - an
/// identity that finds a group but cannot see some of its member links gets no rows, not an error.
/// </remarks>
internal sealed class SelfGrantDirectory : IDisposable
{
    private static readonly string[] Properties =
        ["objectClass", "userPrincipalName", "mail", "proxyAddresses", "objectSid", "primaryGroupID"];

    private readonly DirectorySessionPool.DirectorySession _session;
    private readonly PowerShell _ps;
    private readonly string _globalCatalog;

    private SelfGrantDirectory(DirectorySessionPool.DirectorySession session, string globalCatalog)
    {
        _session = session;
        _ps = session.PowerShell;
        _globalCatalog = globalCatalog;
    }

    /// <summary>
    /// The reads <see cref="ProtectedGroupMembership.Evaluate"/> needs, over this session's runspace and under
    /// <paramref name="credential"/> - the protected-principal directory-read credential (aud-a60), never this session's
    /// own identity: a membership link that identity cannot see reads as no row, and "no row" is the evaluator's evidence.
    /// </summary>
    internal IProtectedGroupDirectory GroupsUnder(PSCredential credential) =>
        new PowerShellProtectedGroupDirectory(_ps, credential ?? throw new ArgumentNullException(nameof(credential)));

    /// <summary>How long a check waits for a warm session before it refuses as busy.</summary>
    internal static readonly TimeSpan SessionWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Opens one check's session: a warm one lent from <paramref name="sessions"/> (kd-88), and the forest's catalog. Throws
    /// when no session can be had - none free in time, or a new one whose runspace or module cannot be had - or the catalog
    /// cannot; nothing is left lent then, and a session the catalog's discovery failed in is thrown away.
    /// </summary>
    internal static SelfGrantDirectory Open(DirectorySessionPool sessions)
    {
        var session = sessions.Lease(SessionWait);
        try
        {
            return new SelfGrantDirectory(session, GlobalCatalog(session.PowerShell));
        }
        catch
        {
            session.Fail();
            session.Dispose();
            throw;
        }
    }

    /// <summary>Gives the session back - warm, unless a read failed in it.</summary>
    public void Dispose() => _session.Dispose();

    /// <summary>A read in this session could not be completed: the session is thrown away, not lent again (kd-88).</summary>
    internal void Fail() => _session.Fail();

    /// <summary>Every object in the forest whose objectSid is <paramref name="sid"/>, under the session's identity.</summary>
    internal IReadOnlyList<SelfGrantDirectoryObject> FindBySid(string sid) =>
        Search(ProtectedGroupMembership.GroupLookupFilter(GroupLookupKey.Sid, sid), _globalCatalog, credential: null);

    /// <summary>
    /// Every object in the forest whose userPrincipalName is <paramref name="userPrincipalName"/>, read under
    /// <paramref name="credential"/> - the protected-principal directory-read credential (aud-a70 (7)): an account the
    /// session's identity cannot see reads as none, and none is "no on-premises account", which no group is asked about.
    /// </summary>
    internal IReadOnlyList<SelfGrantDirectoryObject> FindByUserPrincipalName(string userPrincipalName, PSCredential credential) =>
        Search($"(userPrincipalName={Escape(userPrincipalName.Trim())})", _globalCatalog,
            credential ?? throw new ArgumentNullException(nameof(credential)));

    /// <summary>
    /// Every object in the forest a grantee identity can name (<see cref="GranteeFilter"/>), read under
    /// <paramref name="credential"/> - the protected-principal directory-read credential (aud-a69): an object the session's
    /// identity cannot see reads as no row, and no row lets a grantee through. aud-a70 (6): the only way a grantee is looked
    /// up - the session-identity form for a grantee Exchange names is gone, and without the credential the grant is refused.
    /// </summary>
    internal IReadOnlyList<SelfGrantDirectoryObject> FindNamedBy(string identity, string? canonicalAddress, PSCredential credential) =>
        Search(GranteeFilter(identity, canonicalAddress), _globalCatalog,
            credential ?? throw new ArgumentNullException(nameof(credential)));

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
            DirectoryReadOperation.CountRead(); // kd-88: the timing line's count
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

    // A null credential is the session's own identity; any other is passed as -Credential (aud-a69). Every caller searches the
    // forest at its global catalog, so the base is stated empty (rev-a3, rev-b1, rev-c1): with none the AD cmdlets start at the
    // catalog's own domain partition, and a grantee in another tree would read as no row - which lets the grant through.
    private List<SelfGrantDirectoryObject> Search(string filter, string server, PSCredential? credential)
    {
        _ps.Commands.Clear();
        _ps.Streams.Error.Clear();
        try
        {
            var command = _ps.AddCommand("Get-ADObject")
               .AddParameter("LDAPFilter", filter)
               .AddParameter("Server", server)
               .AddParameter("SearchBase", string.Empty)
               .AddParameter("Properties", Properties);
            if (credential is not null)
                command.AddParameter("Credential", credential);
            command.AddParameter("ErrorAction", "Stop");

            DirectoryReadOperation.CountRead(); // kd-88: the timing line's count
            Collection<PSObject> rows = _ps.Invoke();
            if (_ps.HadErrors)
                throw new InvalidOperationException(
                    _ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Get-ADObject reported an error");

            return rows.Select(ToObject).ToList();
        }
        catch (Exception ex)
        {
            DirectoryCredentialRejection.Observe(ex); // rev-a2: a rotated secret, for the check to fetch afresh
            // kd-88: a read that failed throws the warm session away rather than lending it again.
            _session.Fail();
            throw;
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
    // kd-92: Group Management's manager notice reads the class by this rule too.
    internal static string? ObjectClassOf(object? value)
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
