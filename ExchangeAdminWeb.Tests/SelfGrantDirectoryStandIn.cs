using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a43. A stand-in Active Directory forest for the self-grant check's directory reads
/// (<c>Services/SelfGrantDirectory.cs</c>): objects, their member links and two domains, behind a runspace whose
/// <c>Import-Module</c>, <c>Get-ADForest</c>, <c>Get-ADObject</c> and <c>Get-ADGroup</c> are advanced functions. No
/// directory is reached.
/// </summary>
/// <remarks>
/// Each stub records the step it was asked to run in <see cref="Calls"/> ("Get-ADObject &lt;server&gt; &lt;filter&gt;",
/// "Get-ADGroup &lt;server&gt; &lt;filter&gt;"), then fails as <see cref="Failures"/> says - its message written as the
/// cmdlet's error, so the caller's <c>-ErrorAction Stop</c> throws it - or answers. A search evaluates the LDAP filter the
/// code sends over the objects its <c>-Server</c> reaches: the catalog endpoint reaches every domain, a domain's DNS name
/// that domain, anything else nothing ("Unable to contact the server"); <c>Get-ADGroup</c> answers with groups only, as
/// the real cmdlet does. As on a real global catalog, the catalog holds no member link of a group that is not universal,
/// so a membership asked of the catalog rather than the group's own domain reads as no member. aud-a52: the in-chain rule
/// (<c>member:1.2.840.113556.1.4.1941:=</c>) follows only the links the server asked holds - its own domain's groups, or
/// at the catalog the universal ones - and the bit-and rule reads <c>groupType</c>; every domain holds its Domain Users
/// group, the primary group of its accounts. A filter naming an attribute the stand-in does not hold, or a construct it
/// does not read, is an error, so no test can pass through a filter it did not evaluate. aud-a60: the search stubs and
/// <c>Get-ADForest</c> take <c>-Credential</c>, and who each call ran as is kept beside it (<see cref="CallsAs"/>: the
/// credential's account, or null for the session's own identity). A group can hide its member links from a read made with
/// no credential (<see cref="Entry.MembersHiddenFromSession"/>): such a read sees no row for them, not an error - as a
/// directory answers an identity that may not read a link.
/// </remarks>
internal sealed class SelfGrantDirectoryStandIn
{
    public const string GlobalCatalogHost = "gc01.contoso.test";
    public const string GlobalCatalogEndpoint = GlobalCatalogHost + ":3268";
    public const string DomainDn = "DC=contoso,DC=test";
    public const string OtherDomainDn = "DC=fabrikam,DC=test";
    public const string DomainSid = "S-1-5-21-1111111111-2222222222-3333333333";
    public const string OtherDomainSid = "S-1-5-21-4444444444-5555555555-6666666666";
    public const int DomainUsersRid = 513;
    public const string UnreachableServer = "Unable to contact the server. This may be because this server does not exist, it is currently down, or it does not have the Active Directory Web Services running.";

    private const string InChainRule = "1.2.840.113556.1.4.1941";
    private const string BitAndRule = "1.2.840.113556.1.4.803";

    // The member chain as a pseudo-attribute: what the in-chain rule compares against.
    private const string MemberChain = "member;chain";

    private readonly List<Entry> _objects = [];
    private int _nextRid = 2000;

    public SelfGrantDirectoryStandIn()
    {
        DomainUsers = AddGroup("Domain Users", rid: DomainUsersRid);
        OtherDomainUsers = AddGroup("Domain Users", domainDn: OtherDomainDn, domainSid: OtherDomainSid, rid: DomainUsersRid);
    }

    /// <summary>The primary group of the accounts of <see cref="DomainDn"/>.</summary>
    public Entry DomainUsers { get; }

    /// <summary>The primary group of the accounts of <see cref="OtherDomainDn"/>.</summary>
    public Entry OtherDomainUsers { get; }

    /// <summary>Every step a stub was asked to run, in order.</summary>
    public IList Calls { get; } = ArrayList.Synchronized(new ArrayList());

    /// <summary>aud-a60. Who each of <see cref="Calls"/> ran as, in the same order: the credential's account, or "" for none.</summary>
    public IList Identities { get; } = ArrayList.Synchronized(new ArrayList());

    /// <summary>aud-a60. Every step with who it ran as: the credential's account, or null for the session's own identity.</summary>
    public IReadOnlyList<(string Call, string? As)> CallsAs =>
        Calls.Cast<string>().Zip(Identities.Cast<string>(), (call, who) => (call, who.Length == 0 ? null : who)).ToList();

    /// <summary>aud-a60. Every membership read (a search on <c>member</c>, by either cmdlet) with who it ran as.</summary>
    public IReadOnlyList<(string Call, string? As)> MembershipReads =>
        CallsAs.Where(read => read.Call.StartsWith("Get-AD", StringComparison.Ordinal)
            && read.Call.Contains("(member", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// How a step fails instead of answering: "Import-Module", "Get-ADForest", "Get-ADObject" or "Get-ADGroup" (every
    /// search by that cmdlet), "&lt;cmdlet&gt; &lt;server&gt;" (every search it sends there), "&lt;cmdlet&gt; member" (a
    /// membership read: a filter on <c>member</c>), or "&lt;cmdlet&gt; &lt;fragment&gt;" for a search whose filter contains
    /// the fragment.
    /// </summary>
    public Hashtable Failures { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>The hosts Get-ADForest reports as the forest's global catalogs.</summary>
    public string[] GlobalCatalogs { get; set; } = [GlobalCatalogHost];

    /// <summary>Opening the runspace throws this, when set.</summary>
    public string? RunspaceFailure { get; set; }

    /// <summary>Every search a stub was asked to run, either cmdlet.</summary>
    public IReadOnlyList<string> Searches =>
        Calls.Cast<string>().Where(call => call.StartsWith("Get-ADObject ", StringComparison.Ordinal)
            || call.StartsWith("Get-ADGroup ", StringComparison.Ordinal)).ToList();

    public sealed class Entry
    {
        public required string Dn { get; init; }
        public required string Class { get; init; }
        public Guid Guid { get; init; } = Guid.NewGuid();
        public string? Upn { get; init; }
        public string? Mail { get; init; }
        public string? Sam { get; init; }
        public string? Nickname { get; init; }
        public string? Sid { get; init; }
        public int? PrimaryGroupId { get; init; }
        public List<string> Proxies { get; init; } = [];
        public List<string> Members { get; init; } = [];

        /// <summary>A group's scope as Get-ADGroup reports it: Global, DomainLocal or Universal.</summary>
        public string Scope { get; init; } = "Global";

        /// <summary>A universal group: the catalog holds its member links as well as its own domain does.</summary>
        public bool Universal => string.Equals(Scope, "Universal", StringComparison.Ordinal);

        /// <summary>Held by the catalog and not by its own domain - a domain that cannot answer for it.</summary>
        public bool CatalogOnly { get; init; }

        /// <summary>
        /// aud-a60. Member links a read made with no credential cannot see: the group is found, its links read as none.
        /// </summary>
        public bool MembersHiddenFromSession { get; init; }

        public string Name => Dn[3..Dn.IndexOf(',')];

        /// <summary>A security group's groupType: the scope's flag and the security flag.</summary>
        public int? GroupType => !string.Equals(Class, "group", StringComparison.OrdinalIgnoreCase)
            ? null
            : unchecked((int)0x80000000) | (Scope switch
            {
                "DomainLocal" => 0x4,
                "Universal" => 0x8,
                _ => 0x2,
            });
    }

    public static string DomainName(string domainDn) =>
        string.Join('.', domainDn.Split(',').Select(part => part.Trim()[3..]));

    /// <summary>A user account: a SID in <paramref name="domainSid"/>, Domain Users as its primary group unless told otherwise.</summary>
    public Entry AddUser(string sam, string? upn = null, string? mail = null, string domainDn = DomainDn,
        string domainSid = DomainSid, int primaryGroupRid = DomainUsersRid, string? nickname = null, params string[] proxies)
    {
        var entry = new Entry
        {
            Dn = $"CN={sam},OU=Staff,{domainDn}",
            Class = "user",
            Upn = upn,
            Mail = mail,
            Sam = sam,
            Nickname = nickname,
            Sid = $"{domainSid}-{Interlocked.Increment(ref _nextRid)}",
            PrimaryGroupId = primaryGroupRid,
            Proxies = [.. proxies],
        };
        _objects.Add(entry);
        return entry;
    }

    /// <summary>
    /// A security group of <paramref name="domainDn"/> holding <paramref name="members"/> - accounts or groups - as direct
    /// members: global unless <paramref name="universal"/> or <paramref name="domainLocal"/>.
    /// </summary>
    public Entry AddGroup(string name, string? mail = null, string domainDn = DomainDn, string domainSid = DomainSid,
        int? rid = null, bool universal = false, bool catalogOnly = false, bool domainLocal = false,
        bool membersHiddenFromSession = false, params Entry[] members)
    {
        var entry = new Entry
        {
            Dn = $"CN={name},OU=Groups,{domainDn}",
            Class = "group",
            Mail = mail,
            Sam = name,
            Sid = $"{domainSid}-{rid ?? Interlocked.Increment(ref _nextRid)}",
            Proxies = mail is null ? [] : [$"SMTP:{mail}"],
            Members = [.. members.Select(member => member.Dn)],
            Scope = universal ? "Universal" : domainLocal ? "DomainLocal" : "Global",
            CatalogOnly = catalogOnly,
            MembersHiddenFromSession = membersHiddenFromSession,
        };
        _objects.Add(entry);
        return entry;
    }

    /// <summary>Any other directory object, as given.</summary>
    public Entry Add(Entry entry)
    {
        _objects.Add(entry);
        return entry;
    }

    /// <summary>
    /// A signed-in principal as the Negotiate scheme gives one: the logon name and the account SID; a UPN claim only when
    /// given. Not signed in when <paramref name="authenticated"/> is false.
    /// </summary>
    public static ClaimsPrincipal Principal(string? logonName, string? sid = null, string? upn = null, bool authenticated = true)
    {
        var claims = new List<Claim>();
        if (logonName is not null) claims.Add(new Claim(ClaimTypes.Name, logonName));
        if (sid is not null) claims.Add(new Claim(ClaimTypes.PrimarySid, sid));
        if (upn is not null) claims.Add(new Claim(ClaimTypes.Upn, upn));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "Negotiate" : null));
    }

    // aud-a60: every stub records who it ran as beside the call - the credential's account, or "" for none - and a search
    // tells the forest whether it carried a credential.
    private static readonly (string Name, string Body)[] Stubs =
    [
        ("Import-Module", """
            [CmdletBinding()] param([string] $Name)
            [void]$SelfGrantCalls.Add("Import-Module $Name")
            [void]$SelfGrantIdentities.Add('')
            $problem = $SelfGrantProblem.Invoke('Import-Module', '', '')
            if ($problem) { Write-Error $problem }
            """),
        ("Get-ADForest", """
            [CmdletBinding()] param([System.Management.Automation.PSCredential] $Credential)
            [void]$SelfGrantCalls.Add('Get-ADForest')
            [void]$SelfGrantIdentities.Add($(if ($Credential) { $Credential.UserName } else { '' }))
            $problem = $SelfGrantProblem.Invoke('Get-ADForest', '', '')
            if ($problem) { Write-Error $problem; return }
            [pscustomobject]@{ GlobalCatalogs = [string[]]$SelfGrantCatalogs.Invoke() }
            """),
        ("Get-ADObject", """
            [CmdletBinding()] param([string] $LDAPFilter, [string] $Server, [string[]] $Properties, [System.Management.Automation.PSCredential] $Credential)
            [void]$SelfGrantCalls.Add("Get-ADObject $Server $LDAPFilter")
            [void]$SelfGrantIdentities.Add($(if ($Credential) { $Credential.UserName } else { '' }))
            $problem = $SelfGrantProblem.Invoke('Get-ADObject', $Server, $LDAPFilter)
            if ($problem) { Write-Error $problem; return }
            foreach ($row in $SelfGrantSearch.Invoke($LDAPFilter, $Server, $false, [bool]$Credential)) { $row }
            """),
        ("Get-ADGroup", """
            [CmdletBinding()] param([string] $LDAPFilter, [string] $Server, [string[]] $Properties, [System.Management.Automation.PSCredential] $Credential)
            [void]$SelfGrantCalls.Add("Get-ADGroup $Server $LDAPFilter")
            [void]$SelfGrantIdentities.Add($(if ($Credential) { $Credential.UserName } else { '' }))
            $problem = $SelfGrantProblem.Invoke('Get-ADGroup', $Server, $LDAPFilter)
            if ($problem) { Write-Error $problem; return }
            foreach ($row in $SelfGrantSearch.Invoke($LDAPFilter, $Server, $true, [bool]$Credential)) { $row }
            """),
    ];

    /// <summary>A filter or a search the stand-in could not evaluate: a test expecting an answer fails on it.</summary>
    public IList Errors { get; } = ArrayList.Synchronized(new ArrayList());

    /// <summary>What the self-grant check's runspace seam returns: the stubs over this forest, not yet opened.</summary>
    public Runspace Open()
    {
        if (RunspaceFailure is { } failure)
            throw new InvalidOperationException(failure);

        var state = InitialSessionState.CreateDefault2();
        state.Variables.Add(new SessionStateVariableEntry("SelfGrantCalls", Calls, null));
        state.Variables.Add(new SessionStateVariableEntry("SelfGrantIdentities", Identities, null));
        state.Variables.Add(new SessionStateVariableEntry("SelfGrantProblem", (Func<string, string, string, string?>)Problem, null));
        state.Variables.Add(new SessionStateVariableEntry("SelfGrantCatalogs", (Func<string[]>)(() => GlobalCatalogs), null));
        state.Variables.Add(new SessionStateVariableEntry("SelfGrantSearch", (Func<string, string, bool, bool, object[]>)Search, null));
        foreach (var (name, body) in Stubs)
            state.Commands.Add(new SessionStateFunctionEntry(name, body));
        return RunspaceFactory.CreateRunspace(state);
    }

    private string? Problem(string command, string server, string filter)
    {
        if (Failures[command] is string all)
            return all;
        if (command is not ("Get-ADObject" or "Get-ADGroup"))
            return null;
        if (Failures[$"{command} {server}"] is string here)
            return here;
        if (filter.Contains("(member", StringComparison.OrdinalIgnoreCase) && Failures[$"{command} member"] is string member)
            return member;
        foreach (var key in Failures.Keys.Cast<string>())
        {
            if (key.StartsWith(command + " (", StringComparison.Ordinal) && filter.Contains(key[(command.Length + 1)..], StringComparison.OrdinalIgnoreCase))
                return (string)Failures[key]!;
        }
        return Reach(server) is null ? UnreachableServer : null;
    }

    /// <summary>"forest" for the catalog endpoint, a domain's DN for its DNS name, null for anything else.</summary>
    private string? Reach(string server)
    {
        if (GlobalCatalogs.Any(host => string.Equals(server, $"{host}:3268", StringComparison.OrdinalIgnoreCase)))
            return "forest";
        foreach (var domain in new[] { DomainDn, OtherDomainDn })
        {
            if (string.Equals(server, DomainName(domain), StringComparison.OrdinalIgnoreCase))
                return domain;
        }
        return null;
    }

    private object[] Search(string filter, string server, bool groupsOnly, bool credentialed)
    {
        try
        {
            var reach = Reach(server) ?? throw new InvalidOperationException(UnreachableServer);
            var match = LdapFilter.Parse(filter);
            return _objects
                .Where(entry => !groupsOnly || string.Equals(entry.Class, "group", StringComparison.OrdinalIgnoreCase))
                .Where(entry => Holds(reach, entry))
                .Where(entry => match(attribute => Values(entry, attribute, reach, credentialed)))
                .Select(Row)
                .Cast<object>()
                .ToArray();
        }
        catch (Exception ex)
        {
            Errors.Add($"{server} {filter}: {ex.Message}");
            throw;
        }
    }

    // The objects a server holds: every object at the catalog; a domain's own, unless only the catalog holds it.
    private static bool Holds(string reach, Entry entry) =>
        reach == "forest" || (!entry.CatalogOnly && entry.Dn.EndsWith("," + reach, StringComparison.OrdinalIgnoreCase));

    // Whose member links a server holds: at the catalog a universal group's only; in a domain its own groups'.
    private static bool LinksHeld(string reach, Entry group) => reach == "forest" ? group.Universal : Holds(reach, group);

    // aud-a60. Whose member links this read can see: a link held, and not hidden from a read made with no credential.
    private static bool LinksSeen(string reach, Entry group, bool credentialed) =>
        LinksHeld(reach, group) && (credentialed || !group.MembersHiddenFromSession);

    private IEnumerable<string> Values(Entry entry, string attribute, string reach, bool credentialed) => attribute.ToLowerInvariant() switch
    {
        "objectsid" => One(entry.Sid),
        "userprincipalname" => One(entry.Upn),
        "mail" => One(entry.Mail),
        "proxyaddresses" => entry.Proxies,
        "mailnickname" => One(entry.Nickname),
        "samaccountname" => One(entry.Sam),
        "name" or "cn" => One(entry.Name),
        "distinguishedname" => One(entry.Dn),
        "objectclass" => One(entry.Class),
        "member" => LinksSeen(reach, entry, credentialed) ? entry.Members : [],
        MemberChain => LinksSeen(reach, entry, credentialed) ? Chain(entry, reach, credentialed) : [],
        "grouptype" => entry.GroupType is int type ? [type.ToString(CultureInfo.InvariantCulture)] : [],
        _ => throw new InvalidOperationException($"stand-in: the filter names '{attribute}', which it does not hold"),
    };

    // Every DN reached from the group through member links the server holds and this read can see.
    private List<string> Chain(Entry group, string reach, bool credentialed)
    {
        var reached = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Dn };
        var queue = new Queue<Entry>([group]);
        while (queue.Count > 0)
        {
            foreach (var dn in queue.Dequeue().Members)
            {
                reached.Add(dn);
                var nested = _objects.FirstOrDefault(candidate =>
                    string.Equals(candidate.Class, "group", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.Dn, dn, StringComparison.OrdinalIgnoreCase));
                if (nested is not null && seen.Add(dn) && LinksSeen(reach, nested, credentialed))
                    queue.Enqueue(nested);
            }
        }
        return reached;
    }

    private static IEnumerable<string> One(string? value) => value is null ? [] : [value];

    private static PSObject Row(Entry entry)
    {
        var row = new PSObject();
        row.Properties.Add(new PSNoteProperty("DistinguishedName", entry.Dn));
        row.Properties.Add(new PSNoteProperty("ObjectGUID", entry.Guid));
        row.Properties.Add(new PSNoteProperty("ObjectClass", entry.Class));
        row.Properties.Add(new PSNoteProperty("Name", entry.Name));
        row.Properties.Add(new PSNoteProperty("userPrincipalName", entry.Upn));
        row.Properties.Add(new PSNoteProperty("mail", entry.Mail));
        row.Properties.Add(new PSNoteProperty("proxyAddresses", entry.Proxies.ToArray()));
        row.Properties.Add(new PSNoteProperty("objectSid", entry.Sid is null ? null : new SecurityIdentifier(entry.Sid)));
        row.Properties.Add(new PSNoteProperty("primaryGroupID", entry.PrimaryGroupId));
        if (entry.GroupType is not null)
            row.Properties.Add(new PSNoteProperty("GroupScope", entry.Scope));
        return row;
    }

    /// <summary>
    /// The LDAP filters the self-grant directory sends: and, or, not, attribute equality with RFC 4515 escapes, and two
    /// extensible matches - the in-chain rule on <c>member</c> and the bit-and rule on <c>groupType</c>.
    /// </summary>
    private static class LdapFilter
    {
        public static Func<Func<string, IEnumerable<string>>, bool> Parse(string filter)
        {
            var at = 0;
            var parsed = Filter(filter, ref at);
            if (at != filter.Length)
                throw new InvalidOperationException($"stand-in: cannot read the filter '{filter}'");
            return parsed;
        }

        private static Func<Func<string, IEnumerable<string>>, bool> Filter(string s, ref int at)
        {
            Expect(s, ref at, '(');
            Func<Func<string, IEnumerable<string>>, bool> result;
            switch (s[at])
            {
                case '&':
                    at++;
                    var all = List(s, ref at);
                    result = values => all.All(f => f(values));
                    break;
                case '|':
                    at++;
                    var any = List(s, ref at);
                    result = values => any.Any(f => f(values));
                    break;
                case '!':
                    at++;
                    var not = Filter(s, ref at);
                    result = values => !not(values);
                    break;
                default:
                    result = Item(s, ref at);
                    break;
            }
            Expect(s, ref at, ')');
            return result;
        }

        private static List<Func<Func<string, IEnumerable<string>>, bool>> List(string s, ref int at)
        {
            var filters = new List<Func<Func<string, IEnumerable<string>>, bool>>();
            while (at < s.Length && s[at] == '(')
                filters.Add(Filter(s, ref at));
            if (filters.Count == 0)
                throw new InvalidOperationException($"stand-in: an empty set in '{s}'");
            return filters;
        }

        private static Func<Func<string, IEnumerable<string>>, bool> Item(string s, ref int at)
        {
            var equals = s.IndexOf('=', at);
            if (equals < 0)
                throw new InvalidOperationException($"stand-in: cannot read the filter '{s}'");
            var attribute = s[at..equals];
            var bitAnd = false;
            if (string.Equals(attribute, $"member:{InChainRule}:", StringComparison.OrdinalIgnoreCase))
            {
                attribute = MemberChain;
            }
            else if (string.Equals(attribute, $"groupType:{BitAndRule}:", StringComparison.OrdinalIgnoreCase))
            {
                attribute = "groupType";
                bitAnd = true;
            }
            else if (attribute.Contains(':') || attribute.EndsWith('~') || attribute.EndsWith('>') || attribute.EndsWith('<'))
            {
                throw new InvalidOperationException($"stand-in: does not evaluate '{attribute}=' in '{s}'");
            }
            at = equals + 1;

            var value = new StringBuilder();
            while (at < s.Length && s[at] != ')')
            {
                if (s[at] == '*')
                    throw new InvalidOperationException($"stand-in: does not evaluate a wildcard in '{s}'");
                if (s[at] == '\\')
                {
                    value.Append((char)Convert.ToInt32(s.Substring(at + 1, 2), 16));
                    at += 3;
                }
                else
                {
                    value.Append(s[at++]);
                }
            }

            var wanted = value.ToString();
            if (bitAnd)
            {
                var flags = int.Parse(wanted, CultureInfo.InvariantCulture);
                return values => values(attribute).Any(v => (int.Parse(v, CultureInfo.InvariantCulture) & flags) == flags);
            }

            return values => values(attribute).Any(v => string.Equals(v, wanted, StringComparison.OrdinalIgnoreCase));
        }

        private static void Expect(string s, ref int at, char c)
        {
            if (at >= s.Length || s[at] != c)
                throw new InvalidOperationException($"stand-in: expected '{c}' at {at} in '{s}'");
            at++;
        }
    }
}
