using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Authorization;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Resolves section-access group names to SIDs against on-prem Active Directory, for the one-time
/// SID migration. Runs under the app pool's ambient identity - a read-only group lookup does not
/// need the protected-principal directory-read secret (.agents/decisions.md 2026-07-31).
/// </summary>
/// <remarks>
/// A separate service from <see cref="ADDirectorySearchService"/> rather than another method on it,
/// because the two differ in the property that matters most here: that service is fail-soft
/// everywhere by design (autocomplete must not throw at a user), and this one must throw, since a
/// migration that reads an outage as "no such group" deletes live access grants. Bolting a
/// throwing method onto a fail-soft service invites a later refactor to "make it consistent".
///
/// Its own runspace, for the same reason: sharing the autocomplete service's would put a
/// startup-time migration behind a 30-second lock held by interactive keystrokes.
/// </remarks>
public sealed class SectionAccessGroupDirectory : ISectionAccessGroupDirectory
{
    private readonly ILogger<SectionAccessGroupDirectory> _logger;

    public SectionAccessGroupDirectory(ILogger<SectionAccessGroupDirectory> logger) => _logger = logger;

    /// <inheritdoc />
    public IReadOnlyList<DirectoryGroupMatch> FindGroupsByName(string name, string? netBiosDomain)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DirectoryUnavailableException("A blank group name reached the directory lookup.");

        try
        {
            using var runspace = CreateRunspace();
            var server = netBiosDomain is null ? null : ResolveDomainServer(runspace, netBiosDomain);
            return QueryGroups(runspace, name, server, netBiosDomain);
        }
        catch (DirectoryUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never degraded into "no such group": the caller must be able to leave the store
            // untouched and retry on the next start.
            throw new DirectoryUnavailableException(
                $"Active Directory lookup failed for group '{name}': {ex.Message}", ex);
        }
    }

    private static Runspace CreateRunspace()
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        iss.ImportPSModule("ActiveDirectory");

        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        return runspace;
    }

    /// <summary>
    /// Maps a NetBIOS domain name to the DNS root a directory query can be pointed at.
    /// </summary>
    /// <remarks>
    /// Read from the forest's <c>CN=Partitions</c> crossRef objects, which is where the mapping
    /// actually lives, rather than guessed from the name. Guessing fails on exactly the case that
    /// matters here: this deployment's <c>ANALOG</c> is <c>ad.analog.com</c>, not
    /// <c>analog.com</c>.
    ///
    /// This step is why a domain-qualified value cannot simply be stripped to its bare name.
    /// Verified against live AD 2026-08-03: <c>Enterprise Admins</c> queried without a server
    /// returns zero matches, because it lives in <c>winroot.analog.com</c> - so dropping the
    /// domain half would turn a real cross-domain grant into an unresolvable row.
    /// </remarks>
    private string ResolveDomainServer(Runspace runspace, string netBiosDomain)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddCommand("Get-ADRootDSE").AddParameter("ErrorAction", "Stop");
        var rootDse = ps.Invoke().FirstOrDefault(o => o is not null);
        ps.Commands.Clear();

        var configNc = rootDse?.Properties["configurationNamingContext"]?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(configNc))
            throw new DirectoryUnavailableException("Could not read the directory's configuration naming context.");

        var escaped = ProtectedPrincipalService.EscapeLdapFilter(netBiosDomain);

        ps.AddCommand("Get-ADObject")
          .AddParameter("SearchBase", $"CN=Partitions,{configNc}")
          .AddParameter("LDAPFilter", $"(&(objectClass=crossRef)(netBIOSName={escaped}))")
          .AddParameter("Properties", new[] { "netBIOSName", "dnsRoot" })
          .AddParameter("ErrorAction", "Stop");

        var matches = ps.Invoke().Where(o => o is not null).ToList();
        var errors = DrainErrors(ps);
        ps.Commands.Clear();

        if (errors is not null)
            throw new DirectoryUnavailableException($"Looking up domain '{netBiosDomain}' failed: {errors}");

        if (matches.Count != 1)
        {
            throw new DirectoryUnavailableException(
                $"NetBIOS domain '{netBiosDomain}' matched {matches.Count} forest partitions; expected exactly one.");
        }

        // dnsRoot is multi-valued in the schema; the first entry is the domain's DNS name.
        var dnsRoot = matches[0].Properties["dnsRoot"]?.Value switch
        {
            string s => s,
            System.Collections.IEnumerable e => e.Cast<object?>().FirstOrDefault()?.ToString(),
            var v => v?.ToString()
        };

        if (string.IsNullOrWhiteSpace(dnsRoot))
            throw new DirectoryUnavailableException($"Domain '{netBiosDomain}' has no usable dnsRoot.");

        return dnsRoot;
    }

    private List<DirectoryGroupMatch> QueryGroups(Runspace runspace, string name, string? server, string? netBiosDomain)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        // -LDAPFilter, never -Filter: -Filter expands '$' as a PowerShell variable, and this store
        // holds a group whose name begins with one ($KOO300-S3AMUVVBVMI1).
        ps.AddCommand("Get-ADGroup")
          .AddParameter("LDAPFilter", SectionAccessGroupIdentity.BuildGroupLookupFilter(name))
          .AddParameter("Properties", new[] { "objectSid", "DisplayName", "Name", "SamAccountName" })
          .AddParameter("ErrorAction", "Stop");

        if (server is not null)
            ps.AddParameter("Server", server);

        var found = ps.Invoke();
        var errors = DrainErrors(ps);
        ps.Commands.Clear();

        // The cmdlet complained, so this run proved nothing about how many groups exist. Reporting
        // the count anyway would let a partial failure read as NotFound or as a resolved single.
        if (errors is not null)
            throw new DirectoryUnavailableException($"Group lookup for '{name}' failed: {errors}");

        var matches = new List<DirectoryGroupMatch>();
        foreach (var obj in found)
        {
            // Null-element guard: a pipeline can yield a null row (docs/MessageTraceNullRow-Plan.md).
            if (obj is null)
                continue;

            var sid = obj.Properties["objectSid"]?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(sid))
            {
                // A group with no readable SID cannot become an authorization subject, and
                // dropping it silently would understate the match count - turning a genuine
                // ambiguity into a confident single answer.
                throw new DirectoryUnavailableException(
                    $"A group matching '{name}' returned no readable objectSid.");
            }

            var bare = obj.Properties["SamAccountName"]?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(bare))
                bare = obj.Properties["Name"]?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(bare))
                bare = obj.Properties["DisplayName"]?.Value?.ToString();

            // sAMAccountName first, unlike elsewhere: it is the half of DOMAIN\Name that Windows
            // itself uses, so the rendered value matches what an admin sees in AD tooling.
            // DisplayName is a last resort - it is not unique and need not match the logon name.
            //
            // The domain comes from translating the SID, not from netBiosDomain: a bare stored
            // name carries no domain, and that is precisely the row whose display most needs one.
            // Falls back to the queried domain, then to the bare name.
            matches.Add(new DirectoryGroupMatch(
                sid,
                SectionAccessGroupIdentity.QualifiedDisplayName(
                    ResolveNetBiosDomain(sid) ?? netBiosDomain, bare ?? name)));
        }

        _logger.LogDebug("Section-access group lookup for {Name} on {Server} returned {Count} match(es)",
            name, server ?? "the local domain", matches.Count);

        return matches;
    }

    /// <summary>
    /// The NetBIOS domain owning a SID, or null when it cannot be determined.
    /// </summary>
    /// <remarks>
    /// Uses the Windows SID translator, which returns <c>DOMAIN\Name</c> and is authoritative
    /// about which domain a SID belongs to - the question a directory query cannot answer for a
    /// bare stored name, since it does not know where the match came from.
    ///
    /// Fail-soft on purpose: this decorates a DISPLAY string. A translation failure (unreachable
    /// domain, deleted principal) must leave the operator with a bare name, never fail a lookup
    /// whose real product is the SID.
    /// </remarks>
    private string? ResolveNetBiosDomain(string sid)
    {
        try
        {
            var account = new System.Security.Principal.SecurityIdentifier(sid)
                .Translate(typeof(System.Security.Principal.NTAccount))
                .Value;

            var slash = account.IndexOf('\\');
            return slash > 0 ? account[..slash] : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve a NetBIOS domain for a group SID; showing the bare name");
            return null;
        }
    }

    private static string? DrainErrors(PowerShell ps)
    {
        if (!ps.HadErrors)
            return null;

        var message = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "the directory reported an error";
        ps.Streams.Error.Clear();
        return message;
    }
}
