using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Claims;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class GroupManagementService
{
    private readonly ModuleConfigService _moduleConfig;
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly ProtectedPrincipalService _protectedPrincipals;
    private readonly ProtectedPrincipalServicerService _servicers;

    /// <summary>Module id for the servicer grant. Must match the catalog descriptor.</summary>
    private const string ServicerModuleId = "GroupManagement";
    private readonly ILogger<GroupManagementService> _logger;
    private static readonly SemaphoreSlim _adThrottle = new(2, 2);

    public GroupManagementService(
        ModuleConfigService moduleConfig,
        ModuleCredentialService moduleCredentials,
        ProtectedPrincipalService protectedPrincipals,
        ProtectedPrincipalServicerService servicers,
        ILogger<GroupManagementService> logger)
    {
        _moduleConfig = moduleConfig;
        _moduleCredentials = moduleCredentials;
        _protectedPrincipals = protectedPrincipals;
        _servicers = servicers;
        _logger = logger;
    }

    /// <summary>
    /// In-service protected-principal gate, enforced immediately before the AD write
    /// regardless of caller or identity format. The Blazor page also checks, but
    /// "UI hiding is not security" (Constitution): a protected member supplied by
    /// sAMAccountName or DOMAIN\user (no '@') bypassed the page's '@'-gated check, and
    /// any non-page caller bypassed protection entirely. Fails closed when resolution
    /// is Unavailable or Ambiguous, mirroring ADAttributeEditorService.SaveAsync.
    /// Returns null when the member is clear to mutate, or a Fail result to abort.
    ///
    /// Resolution falls back to Exchange, which closes a real bypass here: protected rows are
    /// stored as primary SMTP addresses, so a protected member supplied by a secondary alias
    /// resolved NotFound and was allowed straight through. Exchange returns the canonical address,
    /// which then resolves in AD and matches. A cloud-only member cannot reach an on-prem group in
    /// any case - AddMemberAsync's own Get-ADUser lookup rejects it before the write.
    /// </summary>
    /// <summary>
    /// Outcome of the gate: a <paramref name="Denial"/> to return to the caller, or null to
    /// proceed. <paramref name="ServicedNote"/> is set only when the member was protected and
    /// an authorised servicer overrode the refusal; it must reach the page's audit call.
    /// </summary>
    private readonly record struct ProtectionGate(PermissionResult? Denial, string? ServicedNote);

    private async Task<ProtectionGate> CheckProtectedAsync(string member, ClaimsPrincipal? actingUser)
    {
        if (string.IsNullOrWhiteSpace(member))
            return new(null, null);

        try
        {
            var (resolved, status) = await _protectedPrincipals.ResolveWithExchangeFallbackAsync(member);
            if (status is ProtectedPrincipalService.ResolutionStatus.Unavailable
                       or ProtectedPrincipalService.ResolutionStatus.Ambiguous)
            {
                return new(PermissionResult.Fail(status == ProtectedPrincipalService.ResolutionStatus.Ambiguous
                    ? "Identity is ambiguous - matches multiple AD users."
                    : "Protection check unavailable. Cannot verify if this member is protected."), null);
            }

            if (resolved != null)
            {
                var check = await _protectedPrincipals.CheckAsync(resolved);
                if (check.CheckFailed)
                    return new(PermissionResult.Fail($"Protection check failed: {check.Reason}"), null);

                // An authorised servicer may proceed. Protection is evaluated first and never
                // weakened - this only decides whether THIS operator may act on a member already
                // known to be protected. A null actingUser refuses, so any caller that does not
                // supply one is safe.
                if (check.IsProtected)
                {
                    var servicedNote = ProtectedPrincipalServicing.NoteFor(
                        _servicers, actingUser, ServicerModuleId, check.MatchedRules);

                    if (servicedNote is null)
                        return new(PermissionResult.Fail("This member is a protected principal. Operation not permitted."), null);

                    return new(null, servicedNote);
                }
            }

            return new(null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Protected principal check failed for member {Member} - blocking as precaution", member);
            return new(PermissionResult.Fail($"Protection check error: {ex.Message}"), null);
        }
    }

    public async Task<List<GroupInfo>> SearchGroupsAsync(string searchTerm)
    {
        var creds = await GetCredentialsAsync("on-prem AD group search");
        if (creds is null)
            throw new InvalidOperationException("AD credentials unavailable. Check the DelineaSecretId configuration for GroupManagement.");

        return await ThrottledAdAsync(async () => await Task.Run(() =>
        {
            var results = new List<GroupInfo>();
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
            var escaped = searchTerm.Replace("'", "''");

            ps.AddCommand("Get-ADGroup")
              .AddParameter("Filter", $"Name -like '*{escaped}*' -or SamAccountName -like '*{escaped}*' -or Mail -like '*{escaped}*'")
              .AddParameter("Properties", new[] { "Mail", "GroupCategory", "GroupScope", "SamAccountName", "Description" })
              .AddParameter("Credential", credential)
              // Fetch wider than we display so ranking sees more than the shown set -
              // guarantees an exact match is fetched even when many groups share the
              // substring. RankGroups then promotes it to the top; the page shows 100.
              .AddParameter("ResultSetSize", 200)
              .AddParameter("ErrorAction", "Stop");
            var groups = ps.Invoke();
            ps.Commands.Clear();

            foreach (var group in groups)
            {
                var category = group.Properties["GroupCategory"]?.Value?.ToString() ?? "";
                var scope = group.Properties["GroupScope"]?.Value?.ToString() ?? "";
                var groupType = category == "Security" ? $"Security ({scope})" : $"Distribution ({scope})";

                results.Add(new GroupInfo
                {
                    Name = group.Properties["Name"]?.Value?.ToString() ?? "",
                    Email = group.Properties["Mail"]?.Value?.ToString() ?? "",
                    Identity = group.Properties["DistinguishedName"]?.Value?.ToString() ?? "",
                    SamAccountName = group.Properties["SamAccountName"]?.Value?.ToString() ?? "",
                    GroupType = groupType,
                    Backend = "OnPremAD"
                });
            }

            return RankGroups(results, searchTerm.Trim()).Take(100).ToList();
        }));
    }

    /// <summary>
    /// Orders search results so the most relevant groups surface first, exact match always
    /// at the top. Pure and deterministic so it is unit-testable (the live AD query in
    /// SearchGroupsAsync is not). Tiers (case-insensitive), exact first:
    ///   1. exact match on Name or SamAccountName
    ///   2. Name or SamAccountName starts with the term
    ///   3. remaining (substring) matches
    /// Within a tier, alphabetical by Name (ordinal-ignore-case). A blank term returns the
    /// input ordered by Name.
    /// </summary>
    internal static List<GroupInfo> RankGroups(IEnumerable<GroupInfo> results, string term)
    {
        var list = results ?? Enumerable.Empty<GroupInfo>();

        if (string.IsNullOrWhiteSpace(term))
            return list.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

        static int Tier(GroupInfo g, string term)
        {
            bool ExactOn(string? v) => string.Equals(v, term, StringComparison.OrdinalIgnoreCase);
            bool StartsOn(string? v) => !string.IsNullOrEmpty(v) && v.StartsWith(term, StringComparison.OrdinalIgnoreCase);

            if (ExactOn(g.Name) || ExactOn(g.SamAccountName)) return 0;
            if (StartsOn(g.Name) || StartsOn(g.SamAccountName)) return 1;
            return 2;
        }

        return list
            .OrderBy(g => Tier(g, term))
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<GroupMemberList> GetMembersAsync(string groupIdentity, string? samAccountName = null)
    {
        var creds = await GetCredentialsAsync("on-prem AD group membership lookup");
        if (creds is null)
            return new GroupMemberList { GroupName = groupIdentity, Error = "AD credentials unavailable." };

        return await ThrottledAdAsync(async () => await Task.Run(() =>
        {
            var result = new GroupMemberList { GroupName = groupIdentity };
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
            var resolvedDn = ResolveAdGroupIdentity(ps, samAccountName, groupIdentity, credential);

            // The member list comes from the group's own linked attribute, NOT Get-ADGroupMember:
            // that cmdlet makes ADWS resolve every member server-side and faults the WHOLE read
            // ("An operations error occurred", GetADGroupMemberFault) when a member belongs to
            // another domain in the forest and cannot be chased under this credential - one
            // WINROOT group nested in an ANALOG group broke the entire listing (dev, 2026-08-28).
            // Comms10k already reads membership this way, for the same cmdlet's 5000-object cap.
            ps.AddCommand("Get-ADGroup")
              .AddParameter("Identity", resolvedDn)
              .AddParameter("Properties", new[] { "member" })
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var groupWithMembers = ps.Invoke().FirstOrDefault(o => o is not null);
            ps.Commands.Clear();
            if (groupWithMembers is null)
            {
                result.Error = "The group could not be read.";
                return result;
            }

            foreach (var memberDn in MemberDnsOf(groupWithMembers))
            {
                // One class-agnostic lookup per member, routed to the member's own domain
                // (gmn-8) - a foreign-domain member's partition does not exist on the local DCs.
                // Class, immutable id and details all come from this read; nesting plan S5a's
                // intent (the list reports what a member IS) is preserved with a new source.
                PSObject? detail = null;
                try
                {
                    ps.AddCommand("Get-ADObject")
                      .AddParameter("Identity", memberDn)
                      .AddParameter("Properties", new[] { "mail", "DisplayName" })
                      .AddParameter("Credential", credential)
                      .AddParameter("ErrorAction", "SilentlyContinue");
                    var server = ServerFromDn(memberDn);
                    if (server is not null)
                        ps.AddParameter("Server", server);
                    detail = ps.Invoke().FirstOrDefault(o => o is not null);
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

                // A member that cannot be resolved still appears - named from its DN, kind
                // "Other", with no immutable id so nothing can act on it. Omitting it would
                // present a partial list as complete (Known Failure Class #2).
                var objectClass = detail?.Properties["ObjectClass"]?.Value?.ToString();
                var kind = SelfServiceGroups.GroupMemberClassifier.KindOf(objectClass);

                result.Members.Add(new GroupMemberInfo
                {
                    DisplayName = detail?.Properties["DisplayName"]?.Value?.ToString()
                        ?? detail?.Properties["Name"]?.Value?.ToString()
                        ?? DisplayNameFromDn(memberDn),
                    Email = detail?.Properties["mail"]?.Value?.ToString() ?? "",
                    RecipientType = kind == "User" ? "ADUser" : kind,
                    MemberKind = kind,
                    ObjectGuid = detail?.Properties["ObjectGUID"]?.Value?.ToString() ?? "",
                    DistinguishedName = memberDn
                });
            }

            return result;
        }));
    }

    /// <param name="actingUser">
    /// REQUIRED and not defaulted: the servicer decision needs a real principal, and a default
    /// would silently make every caller that forgot it unable to service - or invite an ambient
    /// lookup, which attributes a bypass to whoever is on the thread. Null refuses.
    /// </param>
    /// <param name="memberDn">Resolved DN of a picker selection, when one was made (nesting plan
    /// gmn-3): group search is forest-wide, so two domains can hold same-named groups and only
    /// the DN distinguishes them. Typed input passes null and goes through the exact class-aware
    /// resolver with its exactly-one refusal.</param>
    public async Task<PermissionResult> AddMemberAsync(string groupIdentity, string member, ClaimsPrincipal? actingUser, string? samAccountName = null, string? memberDn = null)
    {
        // Pre-gate on the typed identity, exactly as before: for USER members this closes the
        // secondary-alias bypass via the Exchange fallback (pinned by tests). A GROUP identity
        // resolves NotFound here and passes through - the class-aware gate on the RESOLVED
        // principal below is what catches it (gmn-1).
        var gate = await CheckProtectedAsync(member, actingUser);
        if (gate.Denial is not null)
            return gate.Denial;

        var creds = await GetCredentialsAsync("on-prem AD group membership add");
        if (creds is null)
            return PermissionResult.Fail("AD credentials unavailable.");

        // Resolve the member ONCE, class-agnostically (nesting plan S5b): user OR group, by the
        // picker's DN when one is held, else by the typed identity through a bound, escaped LDAP
        // filter with an exactly-one refusal. This single resolution feeds BOTH the protection
        // gate and the write target, so the object that clears the gate is provably the object
        // written. A member that cannot be resolved is REFUSED, never dropped through (gmn-1).
        ResolvedMember resolvedMember;
        try
        {
            resolvedMember = await ThrottledAdAsync(async () => await Task.Run(
                () => ResolveMemberForWrite(creds.Value, member, memberDn, memberObjectGuid: null)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve member {Member} for group add - blocking as a precaution", member);
            return PermissionResult.Fail("The member could not be resolved right now. Please try again shortly.");
        }
        if (resolvedMember.Error is not null)
            return PermissionResult.Fail(resolvedMember.Error);

        // The AUTHORITATIVE gate runs on the RESOLVED principal, unconditionally (gmn-1,
        // gmn-6): the string pre-gate cannot see a group at all, and misses a USER whose label
        // (a display name, a stale identity) resolves NotFound while the DN/GUID resolves the
        // real - possibly protected - object. Whatever resolution produced is what gets gated,
        // and what gets written.
        var resolvedGate = await CheckResolvedMemberAsync(resolvedMember.Principal!, actingUser);
        if (resolvedGate.Denial is not null)
            return resolvedGate.Denial;
        gate = resolvedGate;

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
            var resolvedGroupDn = ResolveAdGroupIdentity(ps, samAccountName, groupIdentity, credential);
            var candidateDn = resolvedMember.Principal!.DistinguishedName!;

            // Idempotent desired-state (AC11b): an add whose member is already present succeeds
            // as a no-op - and is therefore never misreported as a cycle by the guard below.
            if (IsDirectMemberOf(ps, credential, resolvedGroupDn, candidateDn))
                return PermissionResult.Ok($"{member} is already a member of {groupIdentity} (on-premises).", gate.ServicedNote);

            if (resolvedMember.IsGroup)
            {
                // Nesting guards live HERE, immediately before the write, never in the page
                // (gmn-2; GroupManagementService.cs:36-38 records the page-only shape this
                // module already shipped and had bypassed). TARGET = the group being edited,
                // CANDIDATE = the group being added.
                if (IsSelfNest(resolvedGroupDn, candidateDn))
                    return PermissionResult.Fail("Refused: a group cannot be nested inside itself.");

                // Cycle: refuse when TARGET already sits inside CANDIDATE (directly or
                // transitively) - adding CANDIDATE under TARGET would then close a loop. The
                // subject of the query is TARGET and the group searched is CANDIDATE; the
                // mirror question ("is CANDIDATE inside TARGET?") is the benign
                // already-a-member case handled above, never a cycle (gmn-2).
                ps.AddCommand("Get-ADGroup")
                  .AddParameter("LDAPFilter", BuildCycleProbeFilter(resolvedGroupDn, candidateDn))
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                var cycle = ps.Invoke();
                ps.Commands.Clear();
                if (ps.HadErrors)
                {
                    // Fail closed: an unanswerable cycle question is not a "no" (gmn-2).
                    ps.Streams.Error.Clear();
                    return PermissionResult.Fail("The nesting check could not be completed. Please try again shortly.");
                }
                if (cycle.Count > 0)
                    return PermissionResult.Fail($"Refused: '{member}' already contains this group (directly or through nesting), so adding it would create a membership cycle.");
            }

            // The write. AD's own refusals (group scope rules across domains, etc.) surface
            // verbatim through the read-back failure below rather than being pre-empted by a
            // local rule that would drift from AD's (AC12).
            Exception? writeError = null;
            ps.AddCommand("Add-ADGroupMember")
              .AddParameter("Identity", resolvedGroupDn)
              .AddParameter("Members", candidateDn)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            try { ps.Invoke(); }
            catch (Exception ex) { writeError = ex; }
            finally { ps.Commands.Clear(); ps.Streams.Error.Clear(); }

            // Read-back reconciliation (Known Failure Class #2): success is decided ONLY by the
            // membership reflecting the change, never by the absence of an exception.
            bool presentAfter;
            try { presentAfter = IsDirectMemberOf(ps, credential, resolvedGroupDn, candidateDn); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-add membership read-back failed for {Member}", member);
                return PermissionResult.Fail("The change could not be confirmed after writing. Reload the member list to check the current membership.");
            }
            if (!presentAfter)
            {
                return writeError is not null
                    ? PermissionResult.Fail($"Add failed: {writeError.Message}")
                    : PermissionResult.Fail("The add did not take effect. Reload the member list and try again.");
            }
            return PermissionResult.Ok($"{member} added to {groupIdentity} (on-premises).", gate.ServicedNote);
        }));
    }

    /// <param name="actingUser">See <see cref="AddMemberAsync"/>: required, null refuses.</param>
    /// <param name="memberObjectGuid">Immutable objectGUID from the member list (nesting plan
    /// S5b). When present the member resolves by GUID - the value cannot drift between the list
    /// render and the write, and it is the only way a listed GROUP (whose Email is empty) can be
    /// removed. Absent, the typed-identity path applies.</param>
    /// <param name="memberDnHint">The listed member's DN, when known (gmn-8): the GUID stays
    /// the identity key, the DN only routes the lookup to the owning domain's server - a
    /// foreign-domain member's partition does not exist on the local domain's DCs.</param>
    public async Task<PermissionResult> RemoveMemberAsync(string groupIdentity, string member, ClaimsPrincipal? actingUser, string? samAccountName = null, string? memberObjectGuid = null, string? memberDnHint = null)
    {
        // lst-1: a listed row whose immutable identity could not be resolved is INERT. The page
        // sends the row's ObjectGuid verbatim, so a blank (non-null) GUID means exactly that
        // degraded row; refusing here guarantees the resolver can never fall back from the
        // absent immutable key to the mutable display name and act on a same-named local object.
        if (memberObjectGuid is not null && string.IsNullOrWhiteSpace(memberObjectGuid))
            return PermissionResult.Fail("That member could not be fully resolved from the list and cannot be removed here. Reload the member list and try again.");

        // String pre-gate as before (secondary-alias bypass for USER members; pinned by tests).
        // Vacuous when the label is empty or names no user - the resolved-principal gate below
        // covers those (gmn-1).
        var gate = await CheckProtectedAsync(member, actingUser);
        if (gate.Denial is not null)
            return gate.Denial;

        var creds = await GetCredentialsAsync("on-prem AD group membership remove");
        if (creds is null)
            return PermissionResult.Fail("AD credentials unavailable.");

        ResolvedMember resolvedMember;
        try
        {
            resolvedMember = await ThrottledAdAsync(async () => await Task.Run(
                () => ResolveMemberForWrite(creds.Value, member, memberDn: memberDnHint, memberObjectGuid)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve member {Member} for group remove - blocking as a precaution", member);
            return PermissionResult.Fail("The member could not be resolved right now. Please try again shortly.");
        }
        if (resolvedMember.Error is not null)
            return PermissionResult.Fail(resolvedMember.Error);

        // The AUTHORITATIVE gate runs on the RESOLVED principal, unconditionally (gmn-1,
        // gmn-6): a listed member's label is its DISPLAY NAME when it has no mail - non-blank,
        // yet the string pre-gate resolves it NotFound - so gating "when the label was blank"
        // skipped exactly the members the pre-gate cannot see. The GUID-resolved object is what
        // gets gated, and what gets written.
        var resolvedGate = await CheckResolvedMemberAsync(resolvedMember.Principal!, actingUser);
        if (resolvedGate.Denial is not null)
            return resolvedGate.Denial;
        gate = resolvedGate;

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
            var resolvedGroupDn = ResolveAdGroupIdentity(ps, samAccountName, groupIdentity, credential);
            var memberDnResolved = resolvedMember.Principal!.DistinguishedName!;

            // Idempotent desired-state: removing a member that is not present is a no-op.
            if (!IsDirectMemberOf(ps, credential, resolvedGroupDn, memberDnResolved))
                return PermissionResult.Ok($"{member} is not a member of {groupIdentity} (on-premises).", gate.ServicedNote);

            Exception? writeError = null;
            ps.AddCommand("Remove-ADGroupMember")
              .AddParameter("Identity", resolvedGroupDn)
              .AddParameter("Members", memberDnResolved)
              .AddParameter("Credential", credential)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            try { ps.Invoke(); }
            catch (Exception ex) { writeError = ex; }
            finally { ps.Commands.Clear(); ps.Streams.Error.Clear(); }

            // Read-back reconciliation (Known Failure Class #2): success is decided ONLY by the
            // membership reflecting the change, never by the absence of an exception.
            bool presentAfter;
            try { presentAfter = IsDirectMemberOf(ps, credential, resolvedGroupDn, memberDnResolved); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-remove membership read-back failed for {Member}", member);
                return PermissionResult.Fail("The change could not be confirmed after writing. Reload the member list to check the current membership.");
            }
            if (presentAfter)
            {
                return writeError is not null
                    ? PermissionResult.Fail($"Remove failed: {writeError.Message}")
                    : PermissionResult.Fail("The remove did not take effect. Reload the member list and try again.");
            }
            return PermissionResult.Ok($"{member} removed from {groupIdentity} (on-premises).", gate.ServicedNote);
        }));
    }

    // --- Helpers ---

    /// <summary>Outcome of the class-agnostic member resolution for the write paths (S5b).</summary>
    internal readonly record struct ResolvedMember(ResolvedDirectoryPrincipal? Principal, bool IsGroup, string? Error)
    {
        public static ResolvedMember Failed(string error) => new(null, false, error);
    }

    /// <summary>
    /// TARGET/CANDIDATE self-nest guard: refuses adding a group to itself, compared on the
    /// resolved DNs, never on typed names (gmn-2). Pure so the rule is unit-testable.
    /// </summary>
    internal static bool IsSelfNest(string targetGroupDn, string candidateDn)
        => string.Equals(targetGroupDn, candidateDn, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The cycle probe (gmn-2): asks whether TARGET (the group being edited) already sits inside
    /// CANDIDATE (the group being added), directly or transitively - if so, adding CANDIDATE
    /// under TARGET closes a loop. The SUBJECT is TARGET and the in-chain group is CANDIDATE;
    /// the INVERTED filter answers the benign already-a-member question and must never be used
    /// here. Pure so the direction is unit-testable (plan AC11b asserts both directions).
    /// </summary>
    internal static string BuildCycleProbeFilter(string targetGroupDn, string candidateDn)
    {
        var targetEsc = SelfServiceGroups.AdOwnershipFilter.EscapeLdapFilterValue(targetGroupDn);
        var candidateEsc = SelfServiceGroups.AdOwnershipFilter.EscapeLdapFilterValue(candidateDn);
        return $"(&(distinguishedName={targetEsc})(memberOf:1.2.840.113556.1.4.1941:={candidateEsc}))";
    }

    /// <summary>
    /// Derives the owning domain's DNS name from a distinguished name's DC components
    /// (gmn-8): the picker's suggestions are deliberately forest-wide, but Get-ADObject binds
    /// to the local domain by default, where a foreign object's partition does not exist -
    /// selecting a WINROOT group on an ANALOG-hosted app failed at resolution. Pure so the
    /// routing rule is unit-testable. Null when the DN carries no DC components (caller then
    /// omits -Server and keeps today's local binding). Splits on unescaped commas only, so an
    /// escaped comma inside a CN cannot smuggle a fake DC segment.
    /// </summary>
    internal static string? ServerFromDn(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
            return null;
        var labels = System.Text.RegularExpressions.Regex.Split(dn, @"(?<!\\),")
            .Select(p => p.Trim())
            .Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[3..])
            .Where(p => p.Length > 0)
            .ToArray();
        return labels.Length == 0 ? null : string.Join('.', labels);
    }

    /// <summary>
    /// The member DNs held on a group's linked <c>member</c> attribute, as surfaced through a
    /// PSObject property (a collection, a single string, or absent for an empty group). Pure so
    /// the projection is unit-testable without a directory.
    /// </summary>
    internal static List<string> MemberDnsOf(PSObject group)
    {
        var dns = new List<string>();
        var raw = group.Properties["member"]?.Value;
        if (raw is string single)
        {
            if (!string.IsNullOrWhiteSpace(single))
                dns.Add(single);
        }
        else if (raw is System.Collections.IEnumerable many)
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
    /// Display fallback for a member that could not be resolved in its own domain: the DN's CN
    /// with AD's comma escaping undone, else the DN itself. Pure for unit tests.
    /// </summary>
    internal static string DisplayNameFromDn(string dn)
        => ProtectedPrincipalService.ExtractCnFromDn(dn)?.Replace("\\,", ",") ?? dn;

    /// <summary>
    /// First non-BLANK of two identity keys (lst-1). Null-coalescing let an empty-string GUID
    /// shadow a real DN, which then fell through to typed-name resolution - the one thing a
    /// degraded row must never do. Pure for unit tests.
    /// </summary>
    internal static string? FirstNonBlank(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first
         : !string.IsNullOrWhiteSpace(second) ? second
         : null;

    /// <summary>
    /// Class-agnostic exactly-once member resolution for the write paths (S5b). Precedence:
    /// objectGUID (immutable, from the member list) over picker DN over the typed identity via a
    /// bound RFC 4515-escaped -LDAPFilter (replacing the interpolated -Filter strings this
    /// module carried). Accepts user OR group; anything else, zero, or multiple matches is an
    /// error the caller refuses on. For a GROUP the UserPrincipalName is string.Empty, never the
    /// group's name (nesting plan S1 note: MatchesIdentity skips empty candidates, while a group
    /// name in a UPN-shaped field could false-match a protected USER entry sharing the name).
    /// Internal virtual as a TEST SEAM: resolution needs a live directory, and overriding it is
    /// what lets tests prove a GROUP reaches the protection gate (gmn-1) without one.
    /// </summary>
    internal virtual ResolvedMember ResolveMemberForWrite(
        (string username, string password, string domain) creds,
        string memberIdentity, string? memberDn, string? memberObjectGuid)
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

        var credential = CreateCredential(creds.username, creds.password, creds.domain);
        var props = new[] { "DisplayName", "UserPrincipalName", "SamAccountName", "mail", "DistinguishedName", "ObjectGUID" };

        PSObject? obj;
        // lst-1: coalesce on non-BLANK, never null-only. A blank GUID beside a real DN must
        // resolve by the DN (routed to its owning domain) or fail - `??` let the empty string
        // win and dropped through to typed-name resolution of the display label.
        var immutableKey = FirstNonBlank(memberObjectGuid, memberDn);
        if (!string.IsNullOrWhiteSpace(immutableKey))
        {
            ps.AddCommand("Get-ADObject")
              .AddParameter("Identity", immutableKey)
              .AddParameter("Properties", props)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            // gmn-8: bind the lookup to the object's own domain, derived from the DN in hand
            // (the picker's selected DN, or the member list's DN hint alongside the GUID).
            var server = ServerFromDn(memberDn);
            if (server is not null)
                ps.AddParameter("Server", server);
            obj = ps.Invoke().FirstOrDefault();
            ps.Commands.Clear();
            if (obj is null)
                return ResolvedMember.Failed($"'{memberIdentity}' could not be resolved. Reload the list or select the entry again.");
        }
        else
        {
            var m = SelfServiceGroups.AdOwnershipFilter.EscapeLdapFilterValue(memberIdentity.Trim());
            var filter = "(|" +
                $"(&(objectCategory=person)(objectClass=user)(|(userPrincipalName={m})(mail={m})(sAMAccountName={m})))" +
                $"(&(objectCategory=group)(|(name={m})(sAMAccountName={m})(mail={m})))" +
                ")";
            ps.AddCommand("Get-ADObject")
              .AddParameter("LDAPFilter", filter)
              .AddParameter("Properties", props)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var objects = ps.Invoke();
            ps.Commands.Clear();
            if (objects.Count == 0)
                return ResolvedMember.Failed($"'{memberIdentity}' was not found in AD as a user or group.");
            if (objects.Count > 1)
                return ResolvedMember.Failed($"Ambiguous: '{memberIdentity}' matches {objects.Count} directory objects. Select the entry from the search suggestions.");
            obj = objects[0];
        }

        var objectClass = obj.Properties["ObjectClass"]?.Value?.ToString()?.Trim().ToLowerInvariant();
        var isUser = objectClass == "user";
        var isGroup = objectClass == "group";
        if (!isUser && !isGroup)
            return ResolvedMember.Failed($"'{memberIdentity}' is a {objectClass ?? "directory object"}, not a user or group, and cannot be managed here.");
        var dn = obj.Properties["DistinguishedName"]?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(dn))
            return ResolvedMember.Failed($"'{memberIdentity}' resolved without a readable distinguished name.");

        var principal = new ResolvedDirectoryPrincipal(
            Source: "GroupManagementService-AD",
            DisplayName: obj.Properties["DisplayName"]?.Value?.ToString()
                ?? obj.Properties["Name"]?.Value?.ToString() ?? dn,
            UserPrincipalName: isUser ? (obj.Properties["UserPrincipalName"]?.Value?.ToString() ?? string.Empty) : string.Empty,
            SamAccountName: obj.Properties["SamAccountName"]?.Value?.ToString(),
            PrimarySmtpAddress: obj.Properties["mail"]?.Value?.ToString(),
            DistinguishedName: dn,
            ObjectGuid: obj.Properties["ObjectGUID"]?.Value?.ToString(),
            EntraObjectId: null);
        return new ResolvedMember(principal, isGroup, null);
    }

    /// <summary>
    /// Protection gate on an already-RESOLVED principal (gmn-1): the class-agnostic path for
    /// members the string pre-gate cannot see - groups, and GUID-keyed members with no
    /// resolvable label. Same fail-closed and servicer semantics as CheckProtectedAsync; with S1
    /// in place, CheckAsync's Groups rule sees group targets, including the protected group
    /// ITSELF via the DN self-match. Refusals direct to the IT Support Desk (D3).
    /// </summary>
    private async Task<ProtectionGate> CheckResolvedMemberAsync(ResolvedDirectoryPrincipal principal, ClaimsPrincipal? actingUser)
    {
        try
        {
            var check = await _protectedPrincipals.CheckAsync(principal);
            if (check.CheckFailed)
                return new(PermissionResult.Fail($"Protection check failed: {check.Reason}"), null);

            if (check.IsProtected)
            {
                var servicedNote = ProtectedPrincipalServicing.NoteFor(
                    _servicers, actingUser, ServicerModuleId, check.MatchedRules);

                if (servicedNote is null)
                    return new(PermissionResult.Fail("This member is a protected principal. Operation not permitted. Contact the IT Support Desk."), null);

                return new(null, servicedNote);
            }

            return new(null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Protected principal check failed for resolved member - blocking as precaution");
            return new(PermissionResult.Fail($"Protection check error: {ex.Message}"), null);
        }
    }

    /// <summary>
    /// Direct-membership read used by the idempotency pre-checks and the post-write read-backs
    /// (S5b, mirroring the self-service module). Class-agnostic; throws on a read error so an
    /// unverifiable outcome fails closed rather than reading as success.
    /// </summary>
    private static bool IsDirectMemberOf(PowerShell ps, PSCredential credential, string groupDn, string memberDn)
    {
        var memberEsc = SelfServiceGroups.AdOwnershipFilter.EscapeLdapFilterValue(memberDn);
        var groupEsc = SelfServiceGroups.AdOwnershipFilter.EscapeLdapFilterValue(groupDn);
        ps.AddCommand("Get-ADObject")
          .AddParameter("LDAPFilter", $"(&(distinguishedName={memberEsc})(memberOf={groupEsc}))")
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var result = ps.Invoke();
        ps.Commands.Clear();
        if (ps.HadErrors)
        {
            ps.Streams.Error.Clear();
            throw new InvalidOperationException("Could not read the group's membership.");
        }
        return result.Count > 0;
    }

    // Internal virtual as a TEST SEAM (this project exposes internals to the test assembly):
    // both write paths fetch credentials before the resolution seam, so without this no test
    // can drive a member past the credential step to prove the resolved-principal gate fires.
    internal virtual async Task<(string username, string password, string domain)?> GetCredentialsAsync(string purpose)
    {
        return await _moduleCredentials.GetCredentialsAsync("GroupManagement", purpose);
    }

    private async Task<T> ThrottledAdAsync<T>(Func<Task<T>> operation)
    {
        if (!await _adThrottle.WaitAsync(TimeSpan.FromMinutes(2)))
            throw new InvalidOperationException("AD group service is busy. Please try again shortly.");
        try { return await operation(); }
        finally { _adThrottle.Release(); }
    }

    private static string ResolveAdGroupIdentity(PowerShell ps, string? alias, string email, PSCredential credential)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(alias)) candidates.Add(alias);
        if (!string.IsNullOrEmpty(email)) candidates.Add(email);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var escaped = candidate.Replace("'", "''");
            ps.AddCommand("Get-ADGroup")
              .AddParameter("Filter", $"SamAccountName -eq '{escaped}' -or Name -eq '{escaped}' -or Mail -eq '{escaped}'")
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "SilentlyContinue");
            var groups = ps.Invoke();
            ps.Commands.Clear();

            if (groups.Count == 1)
                return groups[0].Properties["DistinguishedName"]?.Value?.ToString()
                    ?? throw new InvalidOperationException($"Could not resolve DN for group '{candidate}'.");
        }

        var tried = string.Join(", ", candidates);
        throw new InvalidOperationException($"AD group not found. Tried: {tried}");
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

public class GroupInfo
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Identity { get; set; } = "";
    public string SamAccountName { get; set; } = "";
    public string GroupType { get; set; } = "";
    public string Backend { get; set; } = "OnPremAD";
}

public class GroupMemberList
{
    public string GroupName { get; set; } = "";
    public string? Error { get; set; }
    public List<GroupMemberInfo> Members { get; set; } = new();
}

public class GroupMemberInfo
{
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string RecipientType { get; set; } = "";

    /// <summary>Human-readable member kind ("User" / "Group" / "Computer" / "Other") - nesting plan S5a.</summary>
    public string MemberKind { get; set; } = "";

    /// <summary>Immutable directory id (objectGUID), the key the remove path uses (nesting plan S5b).</summary>
    public string ObjectGuid { get; set; } = "";

    /// <summary>Distinguished name - routes lookups to the member's own domain (gmn-8).</summary>
    public string DistinguishedName { get; set; } = "";
}
