using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// On-prem AD self-service group service (plan docs/SelfServiceGroupManagement-Plan.md, on-prem only
/// scope). Task 1: the ownership reverse-lookup - "the groups I own". The signed-in Windows principal
/// is resolved ONCE to an immutable directory object (by SID) and its DN is used to query, per-user,
/// the groups where it is the <c>managedBy</c> owner or a listed <c>msExchCoManagedByLink</c>
/// co-manager. This is a bounded per-user server-side query, never a tenant scan.
///
/// Credential isolation (Spec): reads use THIS module's own credential
/// (<c>ModuleCredentialService.GetCredentialsAsync("SelfServiceGroups", ...)</c>), not another
/// module's. Injection safety (codex F11): the caller identity is a bound -Identity parameter (never
/// interpolated), and the ownership filter is built by <see cref="AdOwnershipFilter"/> with RFC 4515
/// escaping and passed as a bound -LDAPFilter value.
///
/// Ownership alone is NOT authorization: every returned group has <see cref="ManageableGroup.CanManageMembers"/>
/// = false here; the fail-closed eligibility rule (task 2) is what flips it, and every write re-checks
/// (task 5). The live AD query is manual-validation-on-dev (no dev tenant); the pure filter core is
/// unit-tested (AdOwnershipFilterTests).
/// </summary>
public class SelfServiceGroupService
{
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly ILogger<SelfServiceGroupService> _logger;
    private static readonly SemaphoreSlim _adThrottle = new(2, 2);

    public SelfServiceGroupService(
        ModuleCredentialService moduleCredentials,
        ILogger<SelfServiceGroupService> logger)
    {
        _moduleCredentials = moduleCredentials;
        _logger = logger;
    }

    /// <summary>
    /// Returns the on-prem AD groups the given caller owns (managedBy or msExchCoManagedByLink). The
    /// caller is identified by their immutable Windows SID - the self-service owner is ALWAYS the
    /// authenticated principal (AC6); no submitted group/owner id can widen this. Throws on a hard AD
    /// failure so the page surfaces a clear error rather than an empty list (AC8, never "no groups
    /// found" on failure).
    /// </summary>
    /// <param name="callerSid">The authenticated Windows principal's SID (e.g. "S-1-5-21-..."). MUST be
    /// a SID string taken from the authenticated principal at a trusted boundary. It is validated as a
    /// SID here so an alternate identity form (DN, GUID, sAMAccountName) - which Get-ADUser -Identity
    /// would otherwise happily accept and resolve to a DIFFERENT principal - is rejected. This is what
    /// keeps the self-service owner always the authenticated caller (AC6), not any submitted id.</param>
    public async Task<IReadOnlyList<ManageableGroup>> GetOwnedGroupsAsync(string callerSid)
    {
        if (string.IsNullOrWhiteSpace(callerSid))
            throw new ArgumentException("Caller SID is required.", nameof(callerSid));
        if (!IsSecurityIdentifier(callerSid))
            throw new ArgumentException(
                "Caller identity must be a Windows SID from the authenticated principal, not an alternate identity form.",
                nameof(callerSid));

        var creds = await _moduleCredentials.GetCredentialsAsync("SelfServiceGroups", "on-prem AD ownership reverse-lookup");
        if (creds is null)
            throw new InvalidOperationException("AD credentials unavailable. Check the DelineaSecretId configuration for SelfServiceGroups.");

        return await ThrottledAdAsync(async () => await Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            var credential = CreateCredential(creds.Value.username, creds.Value.password, creds.Value.domain);

            // Resolve the caller ONCE to their DN via the immutable SID (bound -Identity, no
            // interpolation). This resolved DN is the sole ownership key used below (codex F11).
            ps.AddCommand("Get-ADUser")
              .AddParameter("Identity", callerSid)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var callerResults = ps.Invoke();
            ps.Commands.Clear();

            var callerDn = callerResults.FirstOrDefault()?.Properties["DistinguishedName"]?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(callerDn))
                throw new InvalidOperationException("Could not resolve the signed-in user in Active Directory.");

            var filter = AdOwnershipFilter.BuildOwnedGroupsFilter(callerDn);
            ps.AddCommand("Get-ADGroup")
              .AddParameter("LDAPFilter", filter)
              .AddParameter("Properties", new[] { "Description", "managedBy", "msExchCoManagedByLink", "GroupCategory", "GroupScope" })
              .AddParameter("Credential", credential)
              .AddParameter("ResultSetSize", 500)
              .AddParameter("ErrorAction", "Stop");
            var groups = ps.Invoke();
            ps.Commands.Clear();

            // Cache owner-DN -> display-name across groups so shared owners resolve once.
            var ownerDisplayCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<ManageableGroup>();

            foreach (var group in groups)
            {
                var category = group.Properties["GroupCategory"]?.Value?.ToString() ?? "";
                var scope = group.Properties["GroupScope"]?.Value?.ToString() ?? "";
                var groupType = category == "Security" ? $"Security ({scope})" : $"Distribution ({scope})";

                var ownerDns = CollectOwnerDns(group);
                var otherOwners = new List<string>();
                foreach (var ownerDn in ownerDns
                             .Where(dn => !string.Equals(dn, callerDn, StringComparison.OrdinalIgnoreCase))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    otherOwners.Add(ResolveOwnerDisplay(ps, credential, ownerDn, ownerDisplayCache));
                }

                results.Add(new ManageableGroup
                {
                    ObjectGuid = group.Properties["ObjectGUID"]?.Value?.ToString() ?? "",
                    DistinguishedName = group.Properties["DistinguishedName"]?.Value?.ToString() ?? "",
                    Name = group.Properties["Name"]?.Value?.ToString() ?? "",
                    SamAccountName = group.Properties["SamAccountName"]?.Value?.ToString() ?? "",
                    Description = group.Properties["Description"]?.Value?.ToString(),
                    GroupType = groupType,
                    OtherOwners = otherOwners,
                    // Ownership is not authorization: the eligibility rule (task 2) flips this.
                    CanManageMembers = false,
                });
            }

            return (IReadOnlyList<ManageableGroup>)results;
        }));
    }

    /// <summary>
    /// Gathers the owner DNs from a group's managedBy (single) and msExchCoManagedByLink (multi-valued)
    /// attributes. Both are DN-valued directory links.
    /// </summary>
    private static List<string> CollectOwnerDns(PSObject group)
    {
        var dns = new List<string>();

        var managedBy = group.Properties["managedBy"]?.Value?.ToString();
        if (!string.IsNullOrWhiteSpace(managedBy))
            dns.Add(managedBy);

        var coManaged = group.Properties["msExchCoManagedByLink"]?.Value;
        if (coManaged is string single)
        {
            if (!string.IsNullOrWhiteSpace(single))
                dns.Add(single);
        }
        else if (coManaged is System.Collections.IEnumerable many)
        {
            foreach (var o in many)
            {
                var s = o?.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    dns.Add(s);
            }
        }

        return dns;
    }

    /// <summary>
    /// Resolves an owner DN to a display name (falling back to Name, then the raw DN), caching the
    /// result. A failed lookup for one owner never fails the whole load - the DN is shown instead.
    /// </summary>
    private static string ResolveOwnerDisplay(
        PowerShell ps, PSCredential credential, string ownerDn, Dictionary<string, string> cache)
    {
        if (cache.TryGetValue(ownerDn, out var cached))
            return cached;

        ps.AddCommand("Get-ADObject")
          .AddParameter("Identity", ownerDn)
          .AddParameter("Properties", new[] { "displayName" })
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "SilentlyContinue");
        var resolved = ps.Invoke();
        ps.Commands.Clear();

        var first = resolved.FirstOrDefault();
        var display = first?.Properties["displayName"]?.Value?.ToString()
                      ?? first?.Properties["Name"]?.Value?.ToString()
                      ?? ownerDn;

        cache[ownerDn] = display;
        return display;
    }

    /// <summary>
    /// True only when the value is a syntactically valid Windows SID (e.g. "S-1-5-21-...-1105").
    /// Uses the framework SID parser so no other -Identity form (DN, GUID, sAMAccountName) passes.
    /// Pure and static so it is unit-testable without AD.
    /// </summary>
    internal static bool IsSecurityIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            _ = new System.Security.Principal.SecurityIdentifier(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<T> ThrottledAdAsync<T>(Func<Task<T>> operation)
    {
        if (!await _adThrottle.WaitAsync(TimeSpan.FromMinutes(2)))
            throw new InvalidOperationException("Self-service group service is busy. Please try again shortly.");
        try { return await operation(); }
        finally { _adThrottle.Release(); }
    }

    private static PSCredential CreateCredential(string username, string password, string domain)
    {
        var fullUsername = username.Contains('\\') || username.Contains('@')
            ? username : $"{domain}\\{username}";
        var securePassword = new System.Security.SecureString();
        foreach (var c in password) securePassword.AppendChar(c);
        return new PSCredential(fullUsername, securePassword);
    }
}
