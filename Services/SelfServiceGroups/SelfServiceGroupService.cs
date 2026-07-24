using System.Collections.ObjectModel;
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
/// Ownership alone is NOT authorization (task 1). Task 2 enforces eligibility AT LIST TIME: the
/// managedBy/msExchCoManagedByLink filter is necessary but not sufficient (a group can name the caller
/// as manager with "Manager can update membership" UNCHECKED), so for each candidate group this reads
/// the group's DACL through a credentialed AD drive and includes it only when the caller's own SID holds
/// an Allow member-write ACE (the WriteProperty-on-<c>member</c> ACE that checkbox grants, or
/// GenericWrite/GenericAll) that no Deny revokes - classified by the pure, unit-tested
/// <see cref="GroupMembershipAce"/>. A candidate that fails is EXCLUDED (fail-closed, Known Failure
/// Class #3), never shown-then-refused. Every write still re-checks (task 5). The live AD query and ACL
/// read are manual-validation-on-dev (no dev tenant); the pure cores are unit-tested
/// (AdOwnershipFilterTests, GroupMembershipAceTests).
/// </summary>
public class SelfServiceGroupService
{
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly ILogger<SelfServiceGroupService> _logger;
    private static readonly SemaphoreSlim _adThrottle = new(2, 2);

    // Name of the credentialed AD: provider drive mounted per runspace. The DEFAULT AD: drive binds
    // to the PROCESS identity, which would break credential isolation (Spec); every ACL read in this
    // service goes through this drive bound to THIS module's credential instead.
    private const string AdDriveName = "SsgAd";

    // The group properties both the ownership reverse-lookup and the single-group search load.
    private static readonly string[] GroupProperties =
        ["Description", "managedBy", "msExchCoManagedByLink", "GroupCategory", "GroupScope"];

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

            var credential = CreateCredential(creds.Value.username, creds.Value.password, creds.Value.domain);
            PrepareAdRunspace(ps, credential);

            var callerDn = ResolveCallerDn(ps, credential, callerSid);

            var filter = AdOwnershipFilter.BuildOwnedGroupsFilter(callerDn);
            // No ResultSetSize cap: this is already bounded to the groups ONE user owns, and a silent
            // truncation would read as a complete list (Known Failure Class #2). Get-ADGroup pages
            // internally (ResultPageSize) and returns all matches.
            ps.AddCommand("Get-ADGroup")
              .AddParameter("LDAPFilter", filter)
              .AddParameter("Properties", GroupProperties)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var groups = ps.Invoke();
            ps.Commands.Clear();

            // Cache owner-DN -> display-name across groups so shared owners resolve once.
            var ownerDisplayCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<ManageableGroup>();

            foreach (var group in groups)
            {
                // List-time eligibility (task 2, plan §6.3): being the managedBy manager is necessary
                // but NOT sufficient - "Manager can update membership" may be unchecked. Include the
                // group ONLY when the caller holds member-write on it; fail-closed exclusion otherwise.
                var groupDn = group.Properties["DistinguishedName"]?.Value?.ToString() ?? "";
                if (!CallerCanManageMembers(ps, AdDriveName, groupDn, callerSid))
                    continue;

                results.Add(ProjectGroup(ps, credential, group, callerDn, ownerDisplayCache, canManageMembers: true));
            }

            return (IReadOnlyList<ManageableGroup>)results;
        }));
    }

    /// <summary>
    /// On-demand single-group search (plan §6.3): resolves ONE user-typed group name and returns it
    /// ONLY if the signed-in caller can manage its membership. This exists because there is no
    /// domain-wide scan - a user who knows they can manage a group (e.g. via a direct per-group ACE)
    /// types its name. The name is resolved injection-safely (RFC 4515-escaped, no PowerShell
    /// interpolation, exact match, no wildcards - codex F11) and the SAME DACL eligibility check as the
    /// list path decides manageability, so this can never surface a group the caller cannot edit.
    /// </summary>
    /// <param name="callerSid">The authenticated Windows principal's SID (validated as a SID, per
    /// <see cref="GetOwnedGroupsAsync"/>).</param>
    /// <param name="groupName">The user-typed group name (matched exactly against name / sAMAccountName).</param>
    /// <returns>A <see cref="GroupSearchResult"/>: the group when manageable; otherwise a
    /// not-found-or-not-manageable outcome whose message tells the user to contact the IT Support Desk.
    /// The two are deliberately indistinguishable to the user so the search cannot be used to probe
    /// which groups exist.</returns>
    public async Task<GroupSearchResult> SearchManageableGroupAsync(string callerSid, string groupName)
    {
        if (string.IsNullOrWhiteSpace(callerSid))
            throw new ArgumentException("Caller SID is required.", nameof(callerSid));
        if (!IsSecurityIdentifier(callerSid))
            throw new ArgumentException(
                "Caller identity must be a Windows SID from the authenticated principal, not an alternate identity form.",
                nameof(callerSid));
        if (string.IsNullOrWhiteSpace(groupName))
            throw new ArgumentException("Group name is required.", nameof(groupName));

        var creds = await _moduleCredentials.GetCredentialsAsync("SelfServiceGroups", "on-prem AD single-group search");
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

            var credential = CreateCredential(creds.Value.username, creds.Value.password, creds.Value.domain);
            PrepareAdRunspace(ps, credential);

            var callerDn = ResolveCallerDn(ps, credential, callerSid);

            var filter = AdOwnershipFilter.BuildGroupByNameFilter(groupName.Trim());
            ps.AddCommand("Get-ADGroup")
              .AddParameter("LDAPFilter", filter)
              .AddParameter("Properties", GroupProperties)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var found = ps.Invoke();
            ps.Commands.Clear();

            // Not-found and not-manageable return the SAME outcome (contact IT Support Desk): the
            // search must not double as a directory-enumeration oracle. An ambiguous match (>1 group
            // with the typed name) is treated the same way - the user cannot disambiguate here.
            const string contactSupport =
                "That group was not found, or you do not have permission to manage its membership. " +
                "If you believe you should be able to manage it, contact the IT Support Desk.";

            if (found.Count != 1)
                return new GroupSearchResult(null, contactSupport);

            var group = found[0];
            var groupDn = group.Properties["DistinguishedName"]?.Value?.ToString() ?? "";
            if (!CallerCanManageMembers(ps, AdDriveName, groupDn, callerSid))
                return new GroupSearchResult(null, contactSupport);

            var ownerDisplayCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var manageable = ProjectGroup(ps, credential, group, callerDn, ownerDisplayCache, canManageMembers: true);
            return new GroupSearchResult(manageable, null);
        }));
    }

    /// <summary>
    /// Imports the ActiveDirectory module and mounts the credentialed <see cref="AdDriveName"/> AD
    /// drive so subsequent ACL reads use THIS module's credential, not the process identity (Spec
    /// credential isolation). The drive is runspace-scoped and disposed with the runspace.
    /// </summary>
    private static void PrepareAdRunspace(PowerShell ps, PSCredential credential)
    {
        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("New-PSDrive")
          .AddParameter("Name", AdDriveName)
          .AddParameter("PSProvider", "ActiveDirectory")
          .AddParameter("Root", "//RootDSE/")
          .AddParameter("Credential", credential)
          .AddParameter("Scope", "Global")
          .AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ps.Commands.Clear();
    }

    /// <summary>
    /// Resolves the caller ONCE to their DN via the immutable SID (bound -Identity, no interpolation).
    /// This resolved DN is the sole ownership key used downstream (codex F11). Throws when the caller
    /// cannot be resolved, so the surface shows a clear error rather than an empty result.
    /// </summary>
    private static string ResolveCallerDn(PowerShell ps, PSCredential credential, string callerSid)
    {
        ps.AddCommand("Get-ADUser")
          .AddParameter("Identity", callerSid)
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var callerResults = ps.Invoke();
        ps.Commands.Clear();

        var callerDn = callerResults.FirstOrDefault()?.Properties["DistinguishedName"]?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(callerDn))
            throw new InvalidOperationException("Could not resolve the signed-in user in Active Directory.");
        return callerDn;
    }

    /// <summary>
    /// Projects a Get-ADGroup result into a normalized <see cref="ManageableGroup"/>, resolving the
    /// other owners' display names (caller excluded). <paramref name="canManageMembers"/> is set by
    /// the caller only after the DACL eligibility check has passed.
    /// </summary>
    private static ManageableGroup ProjectGroup(
        PowerShell ps,
        PSCredential credential,
        PSObject group,
        string callerDn,
        Dictionary<string, string> ownerDisplayCache,
        bool canManageMembers)
    {
        var category = group.Properties["GroupCategory"]?.Value?.ToString() ?? "";
        var scope = group.Properties["GroupScope"]?.Value?.ToString() ?? "";
        var groupType = category == "Security" ? $"Security ({scope})" : $"Distribution ({scope})";

        var otherOwners = new List<string>();
        foreach (var ownerDn in CollectOwnerDns(group)
                     .Where(dn => !string.Equals(dn, callerDn, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            otherOwners.Add(ResolveOwnerDisplay(ps, credential, ownerDn, ownerDisplayCache));
        }

        return new ManageableGroup
        {
            ObjectGuid = group.Properties["ObjectGUID"]?.Value?.ToString() ?? "",
            DistinguishedName = group.Properties["DistinguishedName"]?.Value?.ToString() ?? "",
            Name = group.Properties["Name"]?.Value?.ToString() ?? "",
            SamAccountName = group.Properties["SamAccountName"]?.Value?.ToString() ?? "",
            Description = group.Properties["Description"]?.Value?.ToString(),
            GroupType = groupType,
            OtherOwners = otherOwners,
            CanManageMembers = canManageMembers,
        };
    }

    /// <summary>
    /// List-time eligibility check (task 2, plan §6.3): reads the group's DACL through the credentialed
    /// AD drive and returns true ONLY when the caller's own SID holds an Allow member-write ACE
    /// (WriteProperty-on-<c>member</c>, GenericWrite, or GenericAll) that no Deny member-write ACE for
    /// the same SID revokes. Classification is delegated to the pure, unit-tested
    /// <see cref="GroupMembershipAce"/>, keyed on rights BITS (never the ObjectType name) so a
    /// Self-Membership ACE - which shares the <c>member</c> schema GUID - never counts.
    ///
    /// Fail-closed (Known Failure Class #3): an unreadable DACL, or any error, returns false so the
    /// group is EXCLUDED rather than shown as manageable. The per-ACE projection runs in PowerShell so
    /// this type takes no dependency on System.DirectoryServices ACL types; C# sees only primitives.
    /// </summary>
    private static bool CallerCanManageMembers(PowerShell ps, string adDrive, string groupDn, string callerSid)
    {
        if (string.IsNullOrWhiteSpace(groupDn))
            return false;

        Collection<PSObject> aces;
        try
        {
            // Read the DACL via the AD drive (the discovery script confirmed Get-Acl AD:\<DN> is more
            // reliable than Get-ADGroup -Properties nTSecurityDescriptor, which can return an empty
            // .Access). Project each ACE to primitives (Allow/Deny, rights int, ObjectType GUID, trustee
            // SID) so no ACL type crosses back into C#. -Path is built from a bound variable, never
            // interpolated into a script expression.
            ps.AddScript(
                "param($drivePath) " +
                "$acl = Get-Acl -Path $drivePath -ErrorAction Stop; " +
                "foreach ($ace in $acl.Access) { " +
                "  $sid = $null; " +
                "  try { $sid = $ace.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value } catch { $sid = $null }; " +
                "  [pscustomobject]@{ " +
                "    Type = $ace.AccessControlType.ToString(); " +
                "    Rights = [int]$ace.ActiveDirectoryRights; " +
                "    ObjectType = $ace.ObjectType.ToString(); " +
                "    Sid = $sid " +
                "  } " +
                "}")
              .AddArgument($"{adDrive}:\\{groupDn}");
            aces = ps.Invoke();
        }
        catch
        {
            ps.Commands.Clear();
            return false;
        }
        finally
        {
            ps.Commands.Clear();
        }

        if (ps.HadErrors)
        {
            ps.Streams.Error.Clear();
            return false;
        }

        var allow = false;
        foreach (var ace in aces)
        {
            var sid = ace.Properties["Sid"]?.Value?.ToString();
            if (!string.Equals(sid, callerSid, StringComparison.OrdinalIgnoreCase))
                continue;

            var rights = ace.Properties["Rights"]?.Value is int r ? r : 0;
            var objectTypeRaw = ace.Properties["ObjectType"]?.Value?.ToString();
            var objectType = Guid.TryParse(objectTypeRaw, out var g) ? g : Guid.Empty;

            if (!GroupMembershipAce.ConveysMemberWrite(rights, objectType))
                continue;

            var type = ace.Properties["Type"]?.Value?.ToString();
            if (string.Equals(type, "Deny", StringComparison.OrdinalIgnoreCase))
                return false; // an explicit Deny of member-write for the caller wins (fail-closed).
            if (string.Equals(type, "Allow", StringComparison.OrdinalIgnoreCase))
                allow = true;
        }

        return allow;
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
    /// True only when the value is a Windows SID in STRING form (e.g. "S-1-5-21-...-1105"). Uses the
    /// framework SID parser so no other -Identity form (DN, GUID, sAMAccountName) passes, AND rejects
    /// SDDL 2-letter aliases: <c>new SecurityIdentifier("BA")</c> succeeds and resolves to
    /// BUILTIN\Administrators, so parse-success alone is NOT sufficient - the alias would then reach
    /// <c>Get-ADUser -Identity</c> as a DIFFERENT principal than the authenticated caller (codex
    /// slice-2 finding). The value must round-trip to the canonical SID string it parsed to; an alias
    /// ("BA" -> "S-1-5-32-544") does not, a genuine SID string does. Pure and static so it is
    /// unit-testable without AD.
    /// </summary>
    internal static bool IsSecurityIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            var sid = new System.Security.Principal.SecurityIdentifier(value);
            // Reject SDDL aliases (BA, DA, SY, WD, ...) and any padded form: only the exact canonical
            // SID string is accepted, since that same string is what reaches Get-ADUser -Identity.
            return string.Equals(sid.Value, value, StringComparison.OrdinalIgnoreCase);
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
