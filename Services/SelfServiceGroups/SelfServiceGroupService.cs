using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Claims;
using ExchangeAdminWeb.Models;

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
/// Class #3), never shown-then-refused. Every write still re-checks (task 5). The live AD query and DACL
/// read are validated against PROD AD from the dev instance (both instances run on this server against
/// the same live directory); the pure cores are unit-tested (AdOwnershipFilterTests, GroupMembershipAceTests).
/// </summary>
public class SelfServiceGroupService
{
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly ProtectedPrincipalService _protectedPrincipals;
    private readonly ProtectedPrincipalServicerService _servicers;

    /// <summary>Module id for the servicer grant. Must match the catalog descriptor.</summary>
    private const string ServicerModuleId = "SelfServiceGroups";
    private readonly ILogger<SelfServiceGroupService> _logger;
    private static readonly SemaphoreSlim _adThrottle = new(2, 2);

    // Serializes membership writes PER GROUP (plan section 6.5): two concurrent changes to the same
    // group must not interleave their read-check-write cycles. Keyed on the group's immutable objectGUID.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _groupWriteLocks = new(StringComparer.OrdinalIgnoreCase);

    // The group properties both the ownership reverse-lookup and the single-group search load.
    private static readonly string[] GroupProperties =
        ["Description", "managedBy", "msExchCoManagedByLink", "GroupCategory", "GroupScope"];

    public SelfServiceGroupService(
        ModuleCredentialService moduleCredentials,
        ProtectedPrincipalService protectedPrincipals,
        ProtectedPrincipalServicerService servicers,
        ILogger<SelfServiceGroupService> logger)
    {
        _moduleCredentials = moduleCredentials;
        _protectedPrincipals = protectedPrincipals;
        _servicers = servicers;
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
            PrepareAdRunspace(ps);

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
                // List-time eligibility (task 2, plan section 6.3): being the managedBy manager is necessary
                // but NOT sufficient - "Manager can update membership" may be unchecked. Include the
                // group ONLY when the caller holds member-write on it; fail-closed exclusion otherwise.
                var groupDn = group.Properties["DistinguishedName"]?.Value?.ToString() ?? "";
                if (!CallerCanManageMembers(ps, credential, groupDn, callerSid))
                    continue;

                results.Add(ProjectGroup(ps, credential, group, callerDn, ownerDisplayCache, canManageMembers: true));
            }

            return (IReadOnlyList<ManageableGroup>)results;
        }));
    }

    /// <summary>
    /// On-demand single-group search (plan section 6.3): resolves ONE user-typed group name and returns it
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
            PrepareAdRunspace(ps);

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
            if (!CallerCanManageMembers(ps, credential, groupDn, callerSid))
                return new GroupSearchResult(null, contactSupport);

            var ownerDisplayCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var manageable = ProjectGroup(ps, credential, group, callerDn, ownerDisplayCache, canManageMembers: true);
            return new GroupSearchResult(manageable, null);
        }));
    }

    /// <summary>
    /// Returns the direct members of a group the signed-in caller is eligible to manage (plan
    /// docs/SelfServiceGroupsMemberListingAndPicker-Plan.md, member listing). Eligibility is
    /// re-checked here (fail-closed, <see cref="CallerCanManageMembers"/>) so a caller who cannot
    /// manage the group gets NO member list - never a leaked membership. The caller is identified by
    /// their immutable Windows SID (AC6, as the other read paths enforce). Throws on a hard AD failure
    /// or an ineligible/deleted group so the page surfaces a clear error rather than an empty list
    /// presented as "no members" (Known Failure Class #2).
    ///
    /// Each member is projected to <see cref="GroupMember"/> primitives in PowerShell so no
    /// System.DirectoryServices type crosses into C#; removability is decided by the pure, unit-tested
    /// <see cref="GroupMemberClassifier"/> (USER-only, matching the first-cut write scope).
    /// </summary>
    /// <param name="callerSid">The authenticated Windows principal's SID (validated as a SID, per
    /// <see cref="GetOwnedGroupsAsync"/>).</param>
    /// <param name="groupObjectGuid">The immutable objectGUID of the target group (from a
    /// <see cref="ManageableGroup"/> the caller loaded).</param>
    public async Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(string callerSid, string groupObjectGuid)
    {
        if (string.IsNullOrWhiteSpace(callerSid))
            throw new ArgumentException("Caller SID is required.", nameof(callerSid));
        if (!IsSecurityIdentifier(callerSid))
            throw new ArgumentException(
                "Caller identity must be a Windows SID from the authenticated principal, not an alternate identity form.",
                nameof(callerSid));
        if (string.IsNullOrWhiteSpace(groupObjectGuid))
            throw new ArgumentException("Group objectGUID is required.", nameof(groupObjectGuid));

        var creds = await _moduleCredentials.GetCredentialsAsync("SelfServiceGroups", "on-prem AD group member listing");
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
            PrepareAdRunspace(ps);

            // Re-read the group by its immutable objectGUID (never a display name); null => gone.
            var group = ResolveGroupByGuid(ps, credential, groupObjectGuid);
            if (group is null)
                throw new InvalidOperationException("That group no longer exists, or could not be read.");
            var groupDn = group.Properties["DistinguishedName"]?.Value?.ToString() ?? "";

            // Fail-closed eligibility gate (AC-M4): a caller who cannot manage the group is never
            // shown its membership. Throw rather than return empty, so the page shows an error.
            if (!CallerCanManageMembers(ps, credential, groupDn, callerSid))
                throw new InvalidOperationException("You are not permitted to view or manage this group's membership.");

            // The member list comes from the group's own linked attribute, NOT Get-ADGroupMember:
            // that cmdlet makes ADWS resolve every member server-side and faults the WHOLE read
            // ("An operations error occurred", GetADGroupMemberFault) when a member belongs to
            // another domain in the forest and cannot be chased under this credential - one
            // WINROOT group nested in an ANALOG group broke the entire listing (dev, 2026-08-28).
            ps.AddCommand("Get-ADGroup")
              .AddParameter("Identity", groupObjectGuid)
              .AddParameter("Properties", new[] { "member" })
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var withMembers = ps.Invoke().FirstOrDefault(o => o is not null);
            ps.Commands.Clear();
            if (ps.HadErrors || withMembers is null)
            {
                ps.Streams.Error.Clear();
                // An errored read must not read as "no members" (Known Failure Class #2).
                throw new InvalidOperationException("The group's membership could not be read.");
            }

            var results = new List<GroupMember>();
            foreach (var memberDn in GroupManagementService.MemberDnsOf(withMembers))
            {
                // One class-agnostic lookup per member, routed to the member's own domain (a
                // foreign-domain member's partition does not exist on the local DCs, gmn-8). A
                // member that cannot be resolved still appears - named from its DN, kind
                // "Other", not removable - because omitting it would present a partial list as
                // complete (Known Failure Class #2).
                PSObject? m = null;
                try
                {
                    ps.AddCommand("Get-ADObject")
                      .AddParameter("Identity", memberDn)
                      .AddParameter("Properties", new[] { "SamAccountName", "UserPrincipalName" })
                      .AddParameter("Credential", credential)
                      .AddParameter("ErrorAction", "SilentlyContinue");
                    var server = GroupManagementService.ServerFromDn(memberDn);
                    if (server is not null)
                        ps.AddParameter("Server", server);
                    m = ps.Invoke().FirstOrDefault(o => o is not null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Member {MemberDn} could not be resolved in its own domain - listing it from the DN alone",
                        memberDn);
                }
                finally
                {
                    ps.Commands.Clear();
                    ps.Streams.Error.Clear();
                }

                var objectClass = m?.Properties["ObjectClass"]?.Value?.ToString();
                var sam = m?.Properties["SamAccountName"]?.Value?.ToString() ?? "";
                var upn = m?.Properties["UserPrincipalName"]?.Value?.ToString();
                results.Add(new GroupMember
                {
                    ObjectGuid = m?.Properties["ObjectGUID"]?.Value?.ToString() ?? "",
                    DistinguishedName = memberDn,
                    DisplayName = m?.Properties["Name"]?.Value?.ToString()
                                  ?? (sam.Length > 0 ? sam : GroupManagementService.DisplayNameFromDn(memberDn)),
                    Identity = !string.IsNullOrWhiteSpace(upn) ? upn : sam,
                    Kind = GroupMemberClassifier.KindOf(objectClass),
                    IsRemovable = GroupMemberClassifier.IsRemovable(objectClass),
                });
            }

            return (IReadOnlyList<GroupMember>)results;
        }));
    }

    /// <summary>
    /// Adds or removes a single USER member on a group the signed-in caller is eligible to manage
    /// (plan task 5, section 6.5). This is the ONLY mutation in the first cut. Every safety re-check
    /// runs immediately before the write (AC5), fail-closed on any failure:
    /// <list type="number">
    /// <item>the caller identity is a genuine Windows SID (AC6, as the read paths enforce);</item>
    /// <item>the affected member is resolved USER-ONLY to exactly one immutable id (codex F7) - a
    ///   not-found or ambiguous member is refused;</item>
    /// <item>that SAME resolved member passes the protected-principal check
    ///   (<see cref="ProtectedPrincipalService.CheckAsync"/> on the one resolution, not a second
    ///   independent lookup), fail-closed when protected or the check cannot complete (Known Failure
    ///   Class #3);</item>
    /// <item>the group still exists, and the caller still holds member-write on it RIGHT NOW - the
    ///   same DACL eligibility check the list uses (<see cref="CallerCanManageMembers"/>), re-read
    ///   here so a revoked right blocks the write even though the page still lists the group;</item>
    /// <item>the change is expressed as idempotent desired-state via
    ///   <see cref="MembershipChangeReconciler.PlanWrite"/> (add-if-absent / remove-if-present), so a
    ///   retry is a safe no-op; and</item>
    /// <item>after the write, membership is READ BACK and reconciled
    ///   (<see cref="MembershipChangeReconciler.IsDesiredStateReached"/>) so a write that did not take
    ///   effect is reported as failed, never as blind success (codex F10, Known Failure Class #2).</item>
    /// </list>
    /// Writes to the same group are serialized (<see cref="_groupWriteLocks"/>). The residual TOCTOU
    /// window between the last check and the service-account write is accepted and documented (owner
    /// decision 2026-07-27); the AD write credential's least-privilege ACL/JEA rights are the backstop.
    /// Audit + notification are the caller's (page) responsibility (plan section 6.5); this method
    /// returns a <see cref="MembershipChangeResult"/> - the user-facing <see cref="PermissionResult"/>
    /// plus the facts the caller needs for the mandatory notifications that are only known here (the
    /// affected member's resolved SMTP/display from the SINGLE resolution above, and whether the group
    /// is a security group). No second directory lookup is done for notify (codex F1 anti-pattern).
    /// </summary>
    /// <param name="callerSid">The authenticated Windows principal's SID (validated as a SID, per
    /// <see cref="GetOwnedGroupsAsync"/>). The self-service owner is ALWAYS the authenticated caller.</param>
    /// <param name="groupObjectGuid">The immutable objectGUID of the target group (from a
    /// <see cref="ManageableGroup"/> the caller loaded). The write target is keyed on this id, never a
    /// display name (codex F11).</param>
    /// <param name="memberIdentity">The user-typed identity of the USER member to add/remove
    /// (UPN / email / sAMAccountName).</param>
    /// <param name="operation">Add or Remove.</param>
    /// <param name="actingUser">
    /// The authenticated principal, for the protected-principal servicer decision only. REQUIRED and
    /// not defaulted: a default would silently make every caller that forgot it unable to service -
    /// or invite an ambient lookup, which attributes a bypass to whoever is on the thread. Null
    /// refuses. This does NOT replace <paramref name="callerSid"/>: ownership and eligibility still
    /// key on the SID, so the servicer grant cannot become a second, weaker route to those checks.
    /// </param>
    public async Task<MembershipChangeResult> ChangeMemberAsync(
        string callerSid, string groupObjectGuid, string memberIdentity, MembershipOperation operation,
        ClaimsPrincipal? actingUser)
    {
        if (string.IsNullOrWhiteSpace(callerSid))
            throw new ArgumentException("Caller SID is required.", nameof(callerSid));
        if (!IsSecurityIdentifier(callerSid))
            throw new ArgumentException(
                "Caller identity must be a Windows SID from the authenticated principal, not an alternate identity form.",
                nameof(callerSid));
        if (string.IsNullOrWhiteSpace(groupObjectGuid))
            throw new ArgumentException("Group objectGUID is required.", nameof(groupObjectGuid));
        if (string.IsNullOrWhiteSpace(memberIdentity))
            return MembershipChangeResult.From(PermissionResult.Fail("A member identity is required."));

        // Fetch THIS module's credential first: it is used both to resolve the affected member and to
        // perform the write, so the resolution the protection check runs on and the write target are the
        // same directory object seen through the same credential.
        var creds = await _moduleCredentials.GetCredentialsAsync("SelfServiceGroups", "on-prem AD self-service membership change");
        if (creds is null)
            return MembershipChangeResult.From(PermissionResult.Fail("AD credentials unavailable. Check the DelineaSecretId configuration for SelfServiceGroups."));

        // Resolve the affected member ONCE, USER-ONLY, to an immutable principal (codex F7, F11), using
        // THIS module's credential. This single resolution feeds BOTH the protected-principal gate below
        // AND the write target (member.DistinguishedName), so the principal that clears the protection
        // check is provably the one written - no second, differently-credentialed or untrimmed lookup can
        // drift the identity between check and write. A resolution error fails closed (Known Failure
        // Class #3).
        ResolvedDirectoryPrincipal? member;
        try
        {
            member = await ThrottledAdAsync(async () => await Task.Run(
                () => ResolveUserMember(creds.Value, memberIdentity)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve the affected member - blocking as a precaution");
            return MembershipChangeResult.From(PermissionResult.Fail("The member could not be resolved right now. Please try again shortly."));
        }
        if (member is null || string.IsNullOrWhiteSpace(member.DistinguishedName))
        {
            // D1 / nesting plan S3: on the ADD path only, one extra class-bounded probe
            // distinguishes "you typed a GROUP into a users-only control" from a genuine miss, so
            // the refusal states the scope rule instead of reading as a typo. The probe runs only
            // after a user-resolution miss, so the happy path costs nothing; a probe failure
            // degrades to the generic message rather than blocking the refusal.
            var identityIsGroup = false;
            if (operation == MembershipOperation.Add)
            {
                try
                {
                    identityIsGroup = await ThrottledAdAsync(async () => await Task.Run(
                        () => GroupWithIdentityExists(creds.Value, memberIdentity)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Group probe after a member-resolution miss failed; returning the generic not-found message");
                }
            }
            return MembershipChangeResult.From(PermissionResult.Fail(
                ComposeMemberNotFoundMessage(memberIdentity, operation, identityIsGroup)));
        }

        // Protected-principal check on the SAME resolved member (not a second, independent lookup).
        // Fail-closed when protected or the check cannot complete (Known Failure Class #3). Enforced in
        // the service, not just the page, so protection cannot be bypassed by a non-page caller ("UI
        // hiding is not security").
        var protection = await CheckMemberProtectedAsync(member, actingUser);
        if (protection.Denial is not null)
            return MembershipChangeResult.From(protection.Denial);

        return await ApplyMembershipChangeAsync(callerSid, groupObjectGuid, creds.Value, member, operation, protection);
    }

    /// <summary>
    /// Single executor for every membership write (nesting plan S4): serialize per group,
    /// re-check eligibility, idempotency pre-check, write, then read-back reconciliation. Both
    /// public entry points - typed ChangeMemberAsync and list-driven RemoveListedMemberAsync -
    /// feed this method, so the check-write-reconcile contract cannot diverge between them.
    /// </summary>
    private async Task<MembershipChangeResult> ApplyMembershipChangeAsync(
        string callerSid, string groupObjectGuid,
        (string username, string password, string domain) creds,
        ResolvedDirectoryPrincipal member, MembershipOperation operation, ProtectionGate protection)
    {
        var memberDn = member.DistinguishedName!;

        // Serialize per-group so two changes to the same group cannot interleave their check->write
        // cycles (plan section 6.5). Keyed on the immutable objectGUID.
        var gate = _groupWriteLocks.GetOrAdd(groupObjectGuid, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.FromMinutes(2)))
            return MembershipChangeResult.From(PermissionResult.Fail("That group is busy with another change. Please try again shortly."));

        try
        {
            return await ThrottledAdAsync(async () => await Task.Run(() =>
            {
                var iss = InitialSessionState.CreateDefault();
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
                using var runspace = RunspaceFactory.CreateRunspace(iss);
                runspace.Open();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                var credential = CreateCredential(creds.username, creds.password, creds.domain);
                PrepareAdRunspace(ps);

                var callerDn = ResolveCallerDn(ps, credential, callerSid);

                // Re-read the group by its immutable objectGUID (AC5): resolves nothing if the group was
                // deleted since load, and gives us the current DN for the ACL + membership operations.
                var group = ResolveGroupByGuid(ps, credential, groupObjectGuid);
                if (group is null)
                    return MembershipChangeResult.From(PermissionResult.Fail("That group no longer exists, or could not be read. Reload your groups and try again."));
                var groupDn = group.Properties["DistinguishedName"]?.Value?.ToString() ?? "";

                // Whether the target is a SECURITY group decides if the affected user is notified (AC10,
                // Constitution "Notifications" scopes affected-user notify to access changes; on-prem
                // security-group membership is the access-bearing case). Read from the re-read group.
                var isSecurityGroup = string.Equals(
                    group.Properties["GroupCategory"]?.Value?.ToString(), "Security", StringComparison.OrdinalIgnoreCase);

                // Re-check eligibility RIGHT NOW (AC5): the caller's member-write right may have been
                // revoked since the page listed the group. Same fail-closed DACL check the list uses.
                if (!CallerCanManageMembers(ps, credential, groupDn, callerSid))
                    return MembershipChangeResult.From(PermissionResult.Fail("You are no longer permitted to manage this group's membership."));

                // Idempotent desired-state (slice 5a): only write if the current membership requires it.
                // Uses the member DN resolved once above - no second resolution.
                var present = IsMemberOfGroup(ps, credential, groupDn, memberDn);
                var plan = MembershipChangeReconciler.PlanWrite(operation, present);
                if (plan == MembershipWriteAction.AlreadySatisfied)
                {
                    // No write happened, so no affected-user notification (MembershipChanged stays false).
                    return new MembershipChangeResult(
                        PermissionResult.Ok(operation == MembershipOperation.Add
                            ? "That user is already a member of the group."
                            : "That user is not a member of the group.",
                            protection.ServicedNote),
                        member.PrimarySmtpAddress, member.DisplayName, isSecurityGroup, MembershipChanged: false);
                }

                if (operation == MembershipOperation.Add)
                {
                    ps.AddCommand("Add-ADGroupMember")
                      .AddParameter("Identity", groupDn)
                      .AddParameter("Members", memberDn)
                      .AddParameter("Credential", credential)
                      .AddParameter("ErrorAction", "Stop");
                }
                else
                {
                    ps.AddCommand("Remove-ADGroupMember")
                      .AddParameter("Identity", groupDn)
                      .AddParameter("Members", memberDn)
                      .AddParameter("Credential", credential)
                      .AddParameter("Confirm", false)
                      .AddParameter("ErrorAction", "Stop");
                }
                // The write (ErrorAction=Stop) can throw a TERMINATING error - including a timeout that
                // fires AFTER the change already committed at the DC. Capture it rather than let it exit
                // before reconciliation: whether the write "succeeded" is decided ONLY by the read-back
                // below, never by the presence or absence of this exception (codex F10, Known Failure
                // Class #2).
                Exception? writeError = null;
                try
                {
                    ps.Invoke();
                }
                catch (Exception ex)
                {
                    writeError = ex;
                }
                finally
                {
                    ps.Commands.Clear();
                    ps.Streams.Error.Clear();
                }

                // Post-write read-back reconciliation (slice 5a / codex F10): confirm the membership
                // actually reached the requested end state. A write that silently did nothing, threw, or
                // timed out after we lost the response is reported as failure unless the read-back proves
                // the change is in place. The read-back itself throws on a read error (IsMemberOfGroup),
                // so an unverifiable outcome also fails closed rather than reading as success.
                bool presentAfter;
                try
                {
                    presentAfter = IsMemberOfGroup(ps, credential, groupDn, memberDn);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Post-write membership read-back failed - reporting the change as unconfirmed");
                    return MembershipChangeResult.From(PermissionResult.Fail("The change could not be confirmed after writing. Reload your groups to check the current membership."));
                }

                if (!MembershipChangeReconciler.IsDesiredStateReached(operation, presentAfter))
                {
                    if (writeError is not null)
                        _logger.LogWarning(writeError, "Membership write threw and the read-back shows the desired state was not reached");
                    return MembershipChangeResult.From(PermissionResult.Fail("The change could not be confirmed after writing. Reload your groups to check the current membership."));
                }

                // Write applied and confirmed by read-back: carry the notify metadata so the caller can
                // audit + notify (MembershipChanged true only here, where a real add/remove took effect).
                return new MembershipChangeResult(
                    PermissionResult.Ok(operation == MembershipOperation.Add
                        ? "The user was added to the group."
                        : "The user was removed from the group.",
                        protection.ServicedNote),
                    member.PrimarySmtpAddress, member.DisplayName, isSecurityGroup, MembershipChanged: true);
            }));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// List-driven membership REMOVAL keyed on the member's IMMUTABLE objectGUID (nesting plan S4,
    /// D2) - the only path that can remove a GROUP member. A display identity (sAMAccountName/UPN)
    /// can drift or collide between the list render and the write; the GUID cannot. The member is
    /// resolved once via Get-ADObject, accepted only as objectClass user or group, run through the
    /// SAME protection gate as the typed path (with S1 in place a protected nested group is refused
    /// with the IT Support Desk message, D3), and fed to the same single executor.
    /// Typed identities keep using <see cref="ChangeMemberAsync"/>, which stays USER-only (D1).
    /// Audit and notification remain the caller's (page) responsibility, as on the typed path.
    /// </summary>
    public async Task<MembershipChangeResult> RemoveListedMemberAsync(
        string callerSid, string groupObjectGuid, string memberObjectGuid, ClaimsPrincipal? actingUser)
    {
        if (string.IsNullOrWhiteSpace(callerSid))
            throw new ArgumentException("Caller SID is required.", nameof(callerSid));
        if (!IsSecurityIdentifier(callerSid))
            throw new ArgumentException(
                "Caller identity must be a Windows SID from the authenticated principal, not an alternate identity form.",
                nameof(callerSid));
        if (string.IsNullOrWhiteSpace(groupObjectGuid))
            throw new ArgumentException("Group objectGUID is required.", nameof(groupObjectGuid));
        if (string.IsNullOrWhiteSpace(memberObjectGuid))
            return MembershipChangeResult.From(PermissionResult.Fail("A member objectGUID is required."));

        var creds = await _moduleCredentials.GetCredentialsAsync("SelfServiceGroups", "on-prem AD self-service membership change");
        if (creds is null)
            return MembershipChangeResult.From(PermissionResult.Fail("AD credentials unavailable. Check the DelineaSecretId configuration for SelfServiceGroups."));

        // Resolve the affected member ONCE by its immutable objectGUID, using THIS module's
        // credential; the single resolution feeds both the protection gate and the write target,
        // exactly as on the typed path. A resolution error fails closed (Known Failure Class #3).
        ResolvedDirectoryPrincipal? member;
        try
        {
            member = await ThrottledAdAsync(async () => await Task.Run(
                () => ResolveListedMemberByGuid(creds.Value, memberObjectGuid)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve the listed member - blocking as a precaution");
            return MembershipChangeResult.From(PermissionResult.Fail("The member could not be resolved right now. Please try again shortly."));
        }
        if (member is null || string.IsNullOrWhiteSpace(member.DistinguishedName))
            return MembershipChangeResult.From(PermissionResult.Fail("That member could not be found, or is not a user or group. Reload the member list and try again."));

        var protection = await CheckMemberProtectedAsync(member, actingUser);
        if (protection.Denial is not null)
            return MembershipChangeResult.From(protection.Denial);

        return await ApplyMembershipChangeAsync(callerSid, groupObjectGuid, creds.Value, member, MembershipOperation.Remove, protection);
    }

    /// <summary>
    /// Resolves a listed member by objectGUID for <see cref="RemoveListedMemberAsync"/>. Accepts
    /// objectClass user OR group only (nesting plan S4); anything else resolves null and the caller
    /// refuses. For a GROUP the UserPrincipalName is string.Empty, NEVER the group's name:
    /// MatchesIdentity skips empty candidates, while a group name in a UPN-shaped field could
    /// false-match a protected USER entry sharing the name. A group's identity flows through
    /// SamAccountName, DistinguishedName and ObjectGuid; its mail attribute rides
    /// PrimarySmtpAddress so the affected-member notification runs on the same predicate as a
    /// user's (D6 - no class check added anywhere).
    /// </summary>
    private static ResolvedDirectoryPrincipal? ResolveListedMemberByGuid(
        (string username, string password, string domain) creds, string memberObjectGuid)
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        using var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        var credential = CreateCredential(creds.username, creds.password, creds.domain);
        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("Get-ADObject")
          .AddParameter("Identity", memberObjectGuid)
          .AddParameter("Properties", new[] { "DisplayName", "UserPrincipalName", "SamAccountName", "mail", "DistinguishedName", "ObjectGUID" })
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var objects = ps.Invoke();
        ps.Commands.Clear();

        if (objects.Count != 1)
            return null;
        var o = objects[0];
        var objectClass = o.Properties["ObjectClass"]?.Value?.ToString()?.Trim().ToLowerInvariant();
        var isUser = objectClass == "user";
        var isGroup = objectClass == "group";
        if (!isUser && !isGroup)
            return null;
        var dn = o.Properties["DistinguishedName"]?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(dn))
            return null;

        var displayName = o.Properties["DisplayName"]?.Value?.ToString();
        var plainName = o.Properties["Name"]?.Value?.ToString();
        return new ResolvedDirectoryPrincipal(
            Source: "SelfServiceGroupService-AD",
            DisplayName: displayName ?? plainName ?? dn,
            UserPrincipalName: isUser ? (o.Properties["UserPrincipalName"]?.Value?.ToString() ?? string.Empty) : string.Empty,
            SamAccountName: o.Properties["SamAccountName"]?.Value?.ToString(),
            PrimarySmtpAddress: o.Properties["mail"]?.Value?.ToString(),
            DistinguishedName: dn,
            ObjectGuid: o.Properties["ObjectGUID"]?.Value?.ToString(),
            EntraObjectId: null);
    }

    /// <summary>
    /// Protected-principal gate on the affected member (plan section 6.5, codex F9). Runs
    /// <see cref="ProtectedPrincipalService.CheckAsync"/> on the SAME principal already resolved for the
    /// write (not a second, independent lookup), so the account that clears this gate is provably the one
    /// written. Refuses the change when the member is protected OR when the check cannot be completed -
    /// fail-closed (Known Failure Class #3). Returns null when the member is clear to mutate, or a Fail
    /// result to abort.
    /// </summary>
    /// <summary>
    /// Outcome of the gate: a <paramref name="Denial"/> to return to the caller, or null to proceed.
    /// <paramref name="ServicedNote"/> is set only when the member was protected and an authorised
    /// servicer overrode the refusal; it must reach the page's audit call.
    /// </summary>
    internal readonly record struct ProtectionGate(PermissionResult? Denial, string? ServicedNote);

    /// <remarks>
    /// Internal rather than private as a TEST SEAM (the project already exposes internals to
    /// ExchangeAdminWeb.Tests). ChangeMemberAsync fetches this module's AD credential before it
    /// reaches this gate and returns early when none is configured, so no test can drive the gate
    /// through the public method without a live directory. Exposing the decision itself is what
    /// makes the servicer path testable at all; it stays a decision with no side effects.
    /// </remarks>
    internal async Task<ProtectionGate> CheckMemberProtectedAsync(
        ResolvedDirectoryPrincipal member, ClaimsPrincipal? actingUser)
    {
        try
        {
            var check = await _protectedPrincipals.CheckAsync(member);
            if (check.CheckFailed)
                return new(PermissionResult.Fail("The protection check could not be completed. Please try again shortly."), null);

            // An authorised servicer may proceed. Protection is evaluated first and never weakened -
            // this only decides whether THIS operator may act on a member already known to be
            // protected. A null actingUser refuses, so any caller that does not supply one is safe.
            //
            // Note this module's ordinary user is a group owner, not an administrator, so the grant
            // will almost never match here; that is correct rather than pointless. An IT operator who
            // holds the grant and is also a group owner gets the same override they have elsewhere,
            // and every other self-service user keeps hitting the refusal.
            if (check.IsProtected)
            {
                var servicedNote = ProtectedPrincipalServicing.NoteFor(
                    _servicers, actingUser, ServicerModuleId, check.MatchedRules);

                if (servicedNote is null)
                    return new(PermissionResult.Fail("That user is a protected account and cannot be changed here. Contact the IT Support Desk."), null);

                return new(null, servicedNote);
            }

            return new(null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Protected-principal check failed for member - blocking as a precaution");
            return new(PermissionResult.Fail("The protection check could not be completed. Please try again shortly."), null);
        }
    }

    /// <summary>
    /// Re-reads a group by its immutable objectGUID (bound -Identity, no interpolation). Returns null
    /// when the group cannot be resolved (e.g. deleted since the caller loaded it) so the caller fails
    /// closed rather than writing to a stale target.
    /// </summary>
    private static PSObject? ResolveGroupByGuid(PowerShell ps, PSCredential credential, string groupObjectGuid)
    {
        try
        {
            ps.AddCommand("Get-ADGroup")
              .AddParameter("Identity", groupObjectGuid)
              .AddParameter("Properties", GroupProperties)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var found = ps.Invoke();
            ps.Commands.Clear();
            if (ps.HadErrors)
            {
                ps.Streams.Error.Clear();
                return null;
            }
            return found.FirstOrDefault();
        }
        catch
        {
            ps.Commands.Clear();
            ps.Streams.Error.Clear();
            return null;
        }
    }

    /// <summary>
    /// Resolves the affected member USER-ONLY (codex F7) to exactly one immutable principal via an
    /// injection-safe, RFC 4515-escaped -LDAPFilter (codex F11,
    /// <see cref="AdOwnershipFilter.BuildUserByIdentityFilter"/>) using THIS module's credential. The
    /// SINGLE resolution both the protected-principal gate and the write consume: returning the whole
    /// <see cref="ResolvedDirectoryPrincipal"/> (identifiers + DN) means the account checked for
    /// protection is provably the account written. Returns null unless EXACTLY one user matches - a
    /// not-found or ambiguous identity is refused so the write never targets the wrong or an unintended
    /// principal. Runs in its own runspace so it can execute before the write-phase runspace is built.
    /// </summary>
    private static ResolvedDirectoryPrincipal? ResolveUserMember(
        (string username, string password, string domain) creds, string memberIdentity)
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        using var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        var credential = CreateCredential(creds.username, creds.password, creds.domain);
        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ps.Commands.Clear();

        var filter = AdOwnershipFilter.BuildUserByIdentityFilter(memberIdentity.Trim());
        ps.AddCommand("Get-ADUser")
          .AddParameter("LDAPFilter", filter)
          .AddParameter("Properties", new[] { "DisplayName", "UserPrincipalName", "SamAccountName", "mail", "DistinguishedName", "ObjectGUID" })
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var users = ps.Invoke();
        ps.Commands.Clear();

        if (users.Count != 1)
            return null;
        var u = users[0];
        var dn = u.Properties["DistinguishedName"]?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(dn))
            return null;

        return new ResolvedDirectoryPrincipal(
            Source: "SelfServiceGroupService-AD",
            DisplayName: u.Properties["DisplayName"]?.Value?.ToString() ?? memberIdentity,
            UserPrincipalName: u.Properties["UserPrincipalName"]?.Value?.ToString() ?? memberIdentity,
            SamAccountName: u.Properties["SamAccountName"]?.Value?.ToString(),
            PrimarySmtpAddress: u.Properties["mail"]?.Value?.ToString(),
            DistinguishedName: dn,
            ObjectGuid: u.Properties["ObjectGUID"]?.Value?.ToString(),
            EntraObjectId: null);
    }

    /// <summary>
    /// True when the given member DN is currently a member of the group. Uses a bound -LDAPFilter with
    /// the member DN LDAP-escaped (codex F11); checks direct membership on the group's <c>member</c>
    /// attribute. Used both for the idempotency pre-check and the post-write read-back (slice 5a).
    /// </summary>
    private static bool IsMemberOfGroup(PowerShell ps, PSCredential credential, string groupDn, string memberDn)
    {
        var memberEsc = AdOwnershipFilter.EscapeLdapFilterValue(memberDn);
        var groupEsc = AdOwnershipFilter.EscapeLdapFilterValue(groupDn);
        // Ask AD directly whether this member is in this group. distinguishedName is bound to the
        // resolved member DN and memberOf to the resolved group DN; both are escaped so neither can alter
        // the filter. This reflects the write immediately (unlike a cached member list).
        var filter = $"(&(distinguishedName={memberEsc})(memberOf={groupEsc}))";
        // Get-ADObject, not Get-ADUser (nesting plan S2): the filter is already class-agnostic,
        // and a GROUP member must be visible to both the idempotency pre-check and the
        // post-write read-back, or group operations misreport their end state.
        ps.AddCommand("Get-ADObject")
          .AddParameter("LDAPFilter", filter)
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var result = ps.Invoke();
        ps.Commands.Clear();
        if (ps.HadErrors)
        {
            ps.Streams.Error.Clear();
            // An errored membership read must not be treated as a definitive answer. Throw so the
            // read-back reconciliation reports the change as unconfirmed rather than silently wrong.
            throw new InvalidOperationException("Could not read the group's membership.");
        }
        return result.Count > 0;
    }

    /// <summary>
    /// Message selection for a member-resolution miss (nesting plan S3). D1 (owner, 2026-08-11):
    /// self-service NEVER adds a group - a typed group gets a refusal that names the scope rule
    /// and directs to the IT Support Desk; everything else keeps the existing not-found message.
    /// The group probe result is only consulted on the ADD path; removal keeps the generic miss.
    /// Extracted static so the selection is unit-testable without a directory.
    /// </summary>
    internal static string ComposeMemberNotFoundMessage(
        string memberIdentity, MembershipOperation operation, bool identityIsGroup)
    {
        if (operation == MembershipOperation.Add && identityIsGroup)
            return $"'{memberIdentity}' is a group. Only users can be added here - to nest a group " +
                   "inside this group, open an IT Support Desk ticket.";
        return $"'{memberIdentity}' did not match exactly one user. Check the identity and try again.";
    }

    /// <summary>
    /// Class-bounded existence probe behind the S3 refusal message: does the typed identity name an
    /// AD GROUP? Read-only, runs in its own runspace under this module's credential, and is only
    /// reached after a user resolution returned no match. Errors return false - the probe shapes
    /// the refusal's wording, never the authorization outcome.
    /// </summary>
    private static bool GroupWithIdentityExists(
        (string username, string password, string domain) creds, string memberIdentity)
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        using var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        var credential = CreateCredential(creds.username, creds.password, creds.domain);
        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("Get-ADObject")
          .AddParameter("LDAPFilter", AdOwnershipFilter.BuildGroupProbeFilter(memberIdentity.Trim()))
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var result = ps.Invoke();
        ps.Commands.Clear();
        if (ps.HadErrors)
        {
            ps.Streams.Error.Clear();
            return false;
        }
        return result.Count > 0;
    }

    /// <summary>
    /// Imports the ActiveDirectory module into the runspace. Every AD read/write in this service binds
    /// THIS module's credential explicitly via -Credential (Spec credential isolation), so no
    /// credentialed provider drive is needed. (An earlier version mounted a credentialed "SsgAd" AD
    /// drive for Get-Acl reads; that read returned an empty DACL here and was replaced by
    /// Get-ADGroup -Properties nTSecurityDescriptor - see <see cref="CallerCanManageMembers"/>.)
    /// </summary>
    private static void PrepareAdRunspace(PowerShell ps)
    {
        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
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
    /// List-time eligibility check (task 2, plan section 6.3): reads the group's DACL and returns true
    /// ONLY when the caller's own SID holds an Allow member-write ACE (WriteProperty-on-<c>member</c>,
    /// GenericWrite, or GenericAll) that no Deny member-write ACE for the same SID revokes. Classification
    /// is delegated to the pure, unit-tested <see cref="GroupMembershipAce"/>, keyed on rights BITS (never
    /// the ObjectType name) so a Self-Membership ACE - which shares the <c>member</c> schema GUID - never
    /// counts.
    ///
    /// The DACL is read via <c>Get-ADGroup -Properties nTSecurityDescriptor</c> and its
    /// <c>GetAccessRules</c>. An earlier version read <c>Get-Acl AD:\&lt;DN&gt;</c> through a credentialed
    /// provider drive; live validation on 2026-07-28 showed that path returns an EMPTY <c>.Access</c>
    /// collection in this runspace (0 ACEs, blank Owner) while the same group's DACL enumerates 298 rules
    /// via nTSecurityDescriptor - so every group was fail-closed excluded and the page always showed "no
    /// groups". The nTSecurityDescriptor read returns the real DACL under the same module credential.
    ///
    /// Fail-closed (Known Failure Class #3): an unreadable DACL, or any error, returns false so the
    /// group is EXCLUDED rather than shown as manageable. The per-ACE projection runs in PowerShell so
    /// this type takes no dependency on System.DirectoryServices ACL types; C# sees only primitives.
    /// </summary>
    private static bool CallerCanManageMembers(PowerShell ps, PSCredential credential, string groupDn, string callerSid)
    {
        if (string.IsNullOrWhiteSpace(groupDn))
            return false;

        Collection<PSObject> aces;
        try
        {
            // Read the DACL via Get-ADGroup -Properties nTSecurityDescriptor (bound -Identity, no
            // interpolation) and project each access rule to primitives (Allow/Deny, rights int,
            // ObjectType GUID, trustee SID) so no ACL type crosses back into C#. GetAccessRules with
            // targetType=SecurityIdentifier yields the trustee SID directly, so no per-ACE Translate()
            // round-trip is needed. This replaces the Get-Acl AD:\ drive read that returned an empty
            // .Access here (see summary).
            ps.AddScript(
                "param($groupDn, $cred) " +
                "$g = Get-ADGroup -Identity $groupDn -Properties nTSecurityDescriptor -Credential $cred -ErrorAction Stop; " +
                "$rules = $g.nTSecurityDescriptor.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]); " +
                "foreach ($ace in $rules) { " +
                "  [pscustomobject]@{ " +
                "    Type = $ace.AccessControlType.ToString(); " +
                "    Rights = [int]$ace.ActiveDirectoryRights; " +
                "    ObjectType = $ace.ObjectType.ToString(); " +
                "    Sid = $ace.IdentityReference.Value " +
                "  } " +
                "}")
              .AddArgument(groupDn)
              .AddArgument(credential);
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
