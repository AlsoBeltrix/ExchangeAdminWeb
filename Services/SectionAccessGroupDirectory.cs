using ExchangeAdminWeb.Authorization;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Resolves section-access group names to SIDs against on-prem Active Directory, for the one-time
/// SID migration.
/// </summary>
/// <remarks>
/// A separate service from <see cref="ADDirectorySearchService"/> rather than another method on it,
/// because the two differ in the property that matters most here: that service is fail-soft
/// everywhere by design (autocomplete must not throw at a user), and this one must throw, since a
/// migration that reads an outage as "no such group" deletes live access grants. Bolting a
/// throwing method onto a fail-soft service invites a later refactor to "make it consistent".
///
/// The directory calls go through <see cref="ISectionAccessDirectoryCommands"/> rather than a
/// runspace this class owns. That seam is what makes the orchestration here reachable from a test:
/// which errors are fatal, which absences are answers, and what a partial result means are
/// authorization decisions, and they previously could not be exercised without a domain-joined host
/// with RSAT. <see cref="SectionAccessDirectoryReading"/> covers the pure value-shaping decisions
/// beside them; production wiring is <see cref="PowerShellDirectoryCommands"/>, which owns the
/// runspace and its lifetime.
/// </remarks>
public sealed class SectionAccessGroupDirectory : ISectionAccessGroupDirectory
{
    private readonly Func<ISectionAccessDirectoryCommands> _commandsFactory;
    private readonly ILogger<SectionAccessGroupDirectory> _logger;

    public SectionAccessGroupDirectory(ILogger<SectionAccessGroupDirectory> logger)
        : this(() => new PowerShellDirectoryCommands(), logger)
    {
    }

    // A factory rather than an injected instance: each lookup gets a fresh directory session and
    // disposes it, which is what the runspace-per-call lifetime was before the seam existed. An
    // injected singleton would quietly turn a startup-time migration into shared mutable state.
    internal SectionAccessGroupDirectory(
        Func<ISectionAccessDirectoryCommands> commandsFactory,
        ILogger<SectionAccessGroupDirectory> logger)
    {
        _commandsFactory = commandsFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<DirectoryGroupMatch> FindGroupsByName(string name, string? netBiosDomain)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DirectoryUnavailableException("A blank group name reached the directory lookup.");

        try
        {
            using var commands = _commandsFactory();
            var server = netBiosDomain is null ? null : ResolveDomainServer(commands, netBiosDomain);
            return QueryGroups(commands, name, server, netBiosDomain);
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
    private static string ResolveDomainServer(ISectionAccessDirectoryCommands commands, string netBiosDomain)
    {
        var rootDseResult = commands.Invoke("Get-ADRootDSE", new Dictionary<string, object?>
        {
            ["ErrorAction"] = "Stop"
        });

        var rootDse = rootDseResult.Rows.FirstOrDefault(o => o is not null);

        var configNc = rootDse?.Text("configurationNamingContext");
        if (string.IsNullOrWhiteSpace(configNc))
            throw new DirectoryUnavailableException("Could not read the directory's configuration naming context.");

        var escaped = ProtectedPrincipalService.EscapeLdapFilter(netBiosDomain);

        var partitions = commands.Invoke("Get-ADObject", new Dictionary<string, object?>
        {
            ["SearchBase"] = $"CN=Partitions,{configNc}",
            ["LDAPFilter"] = $"(&(objectClass=crossRef)(netBIOSName={escaped}))",
            ["Properties"] = new[] { "netBIOSName", "dnsRoot" },
            ["ErrorAction"] = "Stop"
        });

        if (partitions.Error is not null)
            throw new DirectoryUnavailableException($"Looking up domain '{netBiosDomain}' failed: {partitions.Error}");

        var matches = partitions.Rows.Where(o => o is not null).ToList();

        // Exactly one partition must match; SectionAccessDirectoryReading owns the distinction
        // between "no such domain" and "ambiguous", which point an administrator at different
        // places.
        var partitionProblem = SectionAccessDirectoryReading.PartitionMatchProblem(netBiosDomain, matches.Count);
        if (partitionProblem is not null)
            throw new DirectoryUnavailableException(partitionProblem);

        // dnsRoot is multi-valued in the schema; the first entry is the domain's DNS name. The
        // unwrapping lives in SectionAccessDirectoryReading.
        var dnsRoot = SectionAccessDirectoryReading.UnwrapDnsRoot(matches[0]!.Value("dnsRoot"));

        if (dnsRoot is null)
            throw new DirectoryUnavailableException($"Domain '{netBiosDomain}' has no usable dnsRoot.");

        return dnsRoot;
    }

    private List<DirectoryGroupMatch> QueryGroups(
        ISectionAccessDirectoryCommands commands, string name, string? server, string? netBiosDomain)
    {
        // -LDAPFilter, never -Filter: -Filter expands '$' as a PowerShell variable, and this store
        // holds a group whose name begins with one ($KOO300-S3AMUVVBVMI1).
        var parameters = new Dictionary<string, object?>
        {
            ["LDAPFilter"] = SectionAccessGroupIdentity.BuildGroupLookupFilter(name),
            ["Properties"] = new[] { "objectSid", "DisplayName", "Name", "SamAccountName" },
            ["ErrorAction"] = "Stop"
        };

        if (server is not null)
            parameters["Server"] = server;

        var result = commands.Invoke("Get-ADGroup", parameters);

        // The cmdlet complained, so this run proved nothing about how many groups exist. Reporting
        // the count anyway would let a partial failure read as NotFound or as a resolved single.
        if (result.Error is not null)
            throw new DirectoryUnavailableException($"Group lookup for '{name}' failed: {result.Error}");

        var matches = new List<DirectoryGroupMatch>();
        foreach (var obj in result.Rows)
        {
            // Null-element guard: a pipeline can yield a null row (docs/MessageTraceNullRow-Plan.md).
            if (obj is null)
                continue;

            // A group with no readable SID cannot become an authorization subject, and dropping it
            // silently would understate the match count - see SectionAccessDirectoryReading for
            // why that is worse than failing.
            var sid = obj.Text("objectSid");
            var sidProblem = SectionAccessDirectoryReading.GroupSidProblem(sid, name);
            if (sidProblem is not null)
                throw new DirectoryUnavailableException(sidProblem);

            // Name precedence lives in SectionAccessDirectoryReading, which documents why
            // sAMAccountName leads and DisplayName is a last resort.
            //
            // The domain comes from translating the SID, not from netBiosDomain: a bare stored
            // name carries no domain, and that is precisely the row whose display most needs one.
            // Falls back to the queried domain, then to the bare name.
            var bare = SectionAccessDirectoryReading.ChooseBareName(
                obj.Text("SamAccountName"),
                obj.Text("Name"),
                obj.Text("DisplayName"),
                name);

            matches.Add(new DirectoryGroupMatch(
                sid!,
                SectionAccessGroupIdentity.QualifiedDisplayName(
                    ResolveNetBiosDomain(commands, sid!) ?? netBiosDomain, bare)));
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
    private string? ResolveNetBiosDomain(ISectionAccessDirectoryCommands commands, string sid)
    {
        try
        {
            // Only the translation needs the directory; the split is in
            // SectionAccessDirectoryReading, which documents why index 0 is not a match.
            var domain = SectionAccessDirectoryReading.NetBiosFromNTAccount(commands.TranslateSidToNTAccount(sid));

            // Logged, not silent: the translator returning nothing is the ordinary fail-soft path
            // (unreachable domain, deleted principal), and an operator seeing a bare name where a
            // qualified one was expected needs somewhere to look.
            if (domain is null)
                _logger.LogDebug("Could not resolve a NetBIOS domain for a group SID; showing the bare name");

            return domain;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve a NetBIOS domain for a group SID; showing the bare name");
            return null;
        }
    }
}
