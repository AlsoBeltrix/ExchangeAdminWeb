using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Read-only Active Directory search service for autocomplete purposes.
/// Runs under the app pool's ambient identity (no Delinea credential).
/// NOT used for authorization, protected-principal enforcement, or writes.
/// </summary>
public sealed class ADDirectorySearchService : IOperatorDirectory
{
    private readonly ILogger<ADDirectorySearchService> _logger;
    private readonly SemaphoreSlim _runspaceLock = new(1, 1);

    private bool? _isAvailable;
    private readonly object _availabilityLock = new();

    /// <summary>
    /// True when the ActiveDirectory module is loadable and AD is reachable.
    /// Checked lazily on first call and cached thereafter.
    /// When false, autocomplete components should render a plain text input.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            lock (_availabilityLock)
            {
                if (_isAvailable.HasValue)
                    return _isAvailable.Value;
            }

            // First access: probe availability
            ProbeAvailability();

            lock (_availabilityLock)
            {
                return _isAvailable ?? false;
            }
        }
    }

    public ADDirectorySearchService(ILogger<ADDirectorySearchService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Search Active Directory users matching the given term.
    /// </summary>
    public List<ADSearchResult> SearchUsers(string term, int maxResults = 25)
        => Search(term, "User", maxResults);

    /// <summary>
    /// Search Active Directory groups matching the given term.
    /// </summary>
    public List<ADSearchResult> SearchGroups(string term, int maxResults = 25)
        => Search(term, "Group", maxResults);

    /// <summary>
    /// Search Active Directory for users, groups, or both.
    /// </summary>
    /// <param name="term">Search term (minimum 3 characters).</param>
    /// <param name="objectKind">"User", "Group", "OU", or "Any".</param>
    /// <param name="maxResults">Maximum results to return (default 25).</param>
    /// <remarks>
    /// "OU" is exclusive: it is never included in "Any", because organizational units are only a
    /// meaningful suggestion where an OU is specifically being chosen, and mixing containers into
    /// a people-and-groups picker would be noise everywhere else.
    /// </remarks>
    public List<ADSearchResult> Search(string term, string objectKind, int maxResults = 25)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 3)
            return [];

        if (maxResults <= 0)
            maxResults = 25;

        if (!IsAvailable)
            return [];

        try
        {
            if (!_runspaceLock.Wait(TimeSpan.FromSeconds(30)))
            {
                _logger.LogWarning("AD search throttle timeout for term '{Term}'", term);
                return [];
            }

            try
            {
                return ExecuteSearch(term.Trim(), objectKind, maxResults);
            }
            catch
            {
                // A failed search may leave the persistent runspace in a broken
                // state; drop it so the next search builds a fresh one.
                _searchRunspace?.Dispose();
                _searchRunspace = null;
                throw;
            }
            finally
            {
                _runspaceLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AD directory search failed for term '{Term}' objectKind '{ObjectKind}'", term, objectKind);
            return [];
        }
    }

    /// <summary>
    /// Resolve one user by their Windows SID, for identity resolution rather than
    /// autocomplete. Returns null when the SID does not resolve, AD is unavailable, or the
    /// directory errors (fail-soft, matching <see cref="Search"/>).
    /// </summary>
    /// <remarks>
    /// Deliberately NOT routed through <see cref="Search"/>. That is a wildcard substring
    /// query with a length minimum and a result cap, built for autocomplete; using it as an
    /// identity oracle can return a confidently wrong user (a same-named account in another
    /// trusted domain) and would mail mail-flow data to the wrong person. This binds
    /// <c>-Identity</c> to the SID: immutable, unambiguous, domain-qualified, no post-filter.
    /// Mirrors <c>SelfServiceGroupService.ResolveCallerDn</c>. See
    /// <c>docs/OperatorEmailResolution-Plan.md</c> ("Why the SID").
    /// </remarks>
    public ADSearchResult? FindUserBySid(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
            return null;

        if (!IsAvailable)
            return null;

        try
        {
            if (!_runspaceLock.Wait(TimeSpan.FromSeconds(30)))
            {
                _logger.LogWarning("AD lookup throttle timeout resolving a principal by SID");
                return null;
            }

            try
            {
                return ExecuteFindUserBySid(sid.Trim());
            }
            catch
            {
                // A failed lookup may leave the persistent runspace in a broken
                // state; drop it so the next call builds a fresh one.
                _searchRunspace?.Dispose();
                _searchRunspace = null;
                throw;
            }
            finally
            {
                _runspaceLock.Release();
            }
        }
        catch (Exception ex)
        {
            // The SID is not logged: it identifies the operator, and this path runs on every
            // page load. The failure itself is what the operator's empty pre-fill needs explained.
            _logger.LogWarning(ex, "AD lookup by SID failed");
            return null;
        }
    }

    private ADSearchResult? ExecuteFindUserBySid(string sid)
    {
        var runspace = GetOrCreateRunspace();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        // -Identity is a bound parameter, never string interpolation: the SID reaches the
        // cmdlet as data, so no LDAP filter escaping question arises.
        ps.AddCommand("Get-ADUser")
          .AddParameter("Identity", sid)
          .AddParameter("Properties", new[] { "DisplayName", "DistinguishedName", "SamAccountName", "UserPrincipalName", "mail" })
          .AddParameter("ErrorAction", "Stop");

        var users = ps.Invoke();
        ps.Commands.Clear();

        if (ps.HadErrors)
        {
            var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Get-ADUser by SID failed";
            _logger.LogWarning("AD lookup by SID had errors: {Error}", errMsg);
            ps.Streams.Error.Clear();
        }

        // The null-element guard matches MessageTraceService's four mapping loops: a pipeline
        // can yield a null row, and dereferencing it throws an NRE the caller reads as an
        // outage (see docs/MessageTraceNullRow-Plan.md).
        var obj = users.FirstOrDefault(u => u is not null);
        if (obj is null)
            return null;

        return new ADSearchResult(
            DisplayName: obj.Properties["DisplayName"]?.Value?.ToString() ?? "",
            DistinguishedName: obj.Properties["DistinguishedName"]?.Value?.ToString() ?? "",
            SamAccountName: obj.Properties["SamAccountName"]?.Value?.ToString(),
            UserPrincipalName: obj.Properties["UserPrincipalName"]?.Value?.ToString(),
            Email: obj.Properties["mail"]?.Value?.ToString(),
            ObjectType: "User");
    }

    /// <summary>
    /// Exact-match existence check for admin input validation. Unlike <see cref="Search"/>,
    /// separates an affirmative absence from a lookup that could not run.
    /// </summary>
    /// <param name="identity">
    /// The value the operator typed. For users this may be a UPN, mail address, sAMAccountName,
    /// or <c>DOMAIN\username</c>; for groups a DN, <c>DOMAIN\GroupName</c>, or bare name; for OUs
    /// a distinguished name.
    /// </param>
    /// <param name="objectKind">"User", "Group", or "OU".</param>
    /// <remarks>
    /// Deliberately NOT routed through <see cref="Search"/>, for two independent reasons.
    ///
    /// First, <see cref="Search"/> is fail-soft: unavailable, throttle timeout, a thrown
    /// exception, and a too-short term all return an empty list. A caller cannot tell "the
    /// directory says no such object" from "the lookup never ran". Validation must, because the
    /// two demand opposite messages - the first means the operator mistyped, the second means
    /// retry later. Collapsing them tells an admin their correct entry was a typo during an
    /// outage.
    ///
    /// Second, <see cref="Search"/> is a wildcard substring query built for autocomplete, so
    /// <c>jdoe</c> also matches <c>jdoe2</c>. Confirming existence needs an exact match. Same
    /// trap <see cref="FindUserBySid"/> documents for the same reason.
    ///
    /// Ambiguity is not an error here. Resolution must fail closed on multiple matches because
    /// it has to act on one object; this only asks whether the input corresponds to something
    /// real, and several matches still answer yes.
    ///
    /// Runs under the app pool's ambient identity, never the protected-principal directory-read
    /// secret: a read-only existence check does not need that credential's permissions
    /// (.agents/decisions.md 2026-07-31).
    /// </remarks>
    public DirectoryValidationResult ValidateExists(string identity, string objectKind)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.BlankInput), null);

        // Not "no such object" - the question was never put to the directory.
        if (!IsAvailable)
            return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.DirectoryUnavailable), null);

        try
        {
            if (!_runspaceLock.Wait(TimeSpan.FromSeconds(30)))
            {
                _logger.LogWarning("AD validation throttle timeout for {ObjectKind}", objectKind);
                return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.ThrottleTimeout), null);
            }

            try
            {
                return ExecuteValidateExists(identity.Trim(), objectKind);
            }
            catch
            {
                // A failed lookup may leave the persistent runspace in a broken
                // state; drop it so the next call builds a fresh one.
                _searchRunspace?.Dispose();
                _searchRunspace = null;
                throw;
            }
            finally
            {
                _runspaceLock.Release();
            }
        }
        catch (Exception ex)
        {
            // Unavailable, never NotFound: an exception means the lookup did not complete, which
            // is not evidence the object is absent.
            _logger.LogWarning(ex, "AD validation lookup failed for {ObjectKind}", objectKind);
            return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.LookupThrew), null);
        }
    }

    /// <summary>
    /// Why a validation attempt ended where it did. Exists so <see cref="ClassifyOutcome"/> can
    /// be a pure function: the absence/failure split is then testable on a machine that HAS a
    /// working Active Directory, which a test guarded on <c>IsAvailable</c> is not.
    /// </summary>
    public enum ValidationStep
    {
        /// <summary>Nothing to look up. Not an outage.</summary>
        BlankInput,

        /// <summary>The ActiveDirectory module is not loadable / AD is not reachable.</summary>
        DirectoryUnavailable,

        /// <summary>Could not acquire the shared runspace lock in time.</summary>
        ThrottleTimeout,

        /// <summary>The directory call raised.</summary>
        LookupThrew,

        /// <summary>The cmdlet wrote to its error stream, so this run proved nothing.</summary>
        CmdletReportedErrors,

        /// <summary>The call completed and returned no objects.</summary>
        CompletedWithNoResults,

        /// <summary>The call completed and returned at least one object.</summary>
        CompletedWithResults
    }

    /// <summary>
    /// Maps a terminating step to its outcome. The only step permitted to yield
    /// <see cref="DirectoryLookupOutcome.NotFound"/> from the directory is
    /// <see cref="ValidationStep.CompletedWithNoResults"/> - a query that actually ran and came
    /// back empty. Every step where the lookup did not complete yields
    /// <see cref="DirectoryLookupOutcome.Unavailable"/>, because a directory that did not answer
    /// is not evidence that an object is absent.
    /// </summary>
    public static DirectoryLookupOutcome ClassifyOutcome(ValidationStep step) => step switch
    {
        ValidationStep.CompletedWithResults => DirectoryLookupOutcome.Found,

        // Blank input never reaches the directory, and telling the operator "AD is down" when
        // they submitted an empty box would be a lie in the other direction.
        ValidationStep.BlankInput => DirectoryLookupOutcome.NotFound,
        ValidationStep.CompletedWithNoResults => DirectoryLookupOutcome.NotFound,

        _ => DirectoryLookupOutcome.Unavailable
    };

    private DirectoryValidationResult ExecuteValidateExists(string identity, string objectKind)
    {
        var runspace = GetOrCreateRunspace();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        var command = objectKind switch
        {
            "Group" => "Get-ADGroup",
            "OU" => "Get-ADOrganizationalUnit",
            _ => "Get-ADUser"
        };

        ps.AddCommand(command)
          .AddParameter("LDAPFilter", BuildExactMatchFilter(identity, objectKind))
          .AddParameter("Properties", ValidationProperties(objectKind))
          .AddParameter("ResultSetSize", 2)
          .AddParameter("ErrorAction", "Stop");

        var found = ps.Invoke();
        ps.Commands.Clear();

        if (ps.HadErrors)
        {
            // The cmdlet reported a problem, so this run proved nothing about existence.
            var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? $"{command} validation failed";
            ps.Streams.Error.Clear();
            _logger.LogWarning("AD validation had errors for {ObjectKind}: {Error}", objectKind, errMsg);
            return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.CmdletReportedErrors), null);
        }

        // Null-element guard: a pipeline can yield a null row (see docs/MessageTraceNullRow-Plan.md).
        var obj = found.FirstOrDefault(o => o is not null);
        if (obj is null)
            return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.CompletedWithNoResults), null);

        return new DirectoryValidationResult(
            ClassifyOutcome(ValidationStep.CompletedWithResults),
            new ADSearchResult(
                DisplayName: obj.Properties["DisplayName"]?.Value?.ToString()
                             ?? obj.Properties["Name"]?.Value?.ToString() ?? "",
                DistinguishedName: obj.Properties["DistinguishedName"]?.Value?.ToString() ?? "",
                SamAccountName: obj.Properties["SamAccountName"]?.Value?.ToString(),
                UserPrincipalName: obj.Properties["UserPrincipalName"]?.Value?.ToString(),
                Email: obj.Properties["mail"]?.Value?.ToString(),
                ObjectType: objectKind == "OU" ? "OU" : (objectKind == "Group" ? "Group" : "User")));
    }

    private static string[] ValidationProperties(string objectKind) => objectKind switch
    {
        "Group" => ["DisplayName", "DistinguishedName", "SamAccountName", "mail"],
        "OU" => ["Name", "DistinguishedName"],
        _ => ["DisplayName", "DistinguishedName", "SamAccountName", "UserPrincipalName", "mail"]
    };

    /// <summary>
    /// Builds the exact-match LDAP filter for an existence check. Separated from the directory
    /// call so the filter shape is testable without a live AD.
    /// </summary>
    /// <remarks>
    /// The per-kind attribute sets are not arbitrary - each mirrors what the protection engine
    /// actually matches on, so validation accepts exactly the identity forms enforcement can
    /// resolve. Users mirror <c>ProtectedPrincipalService.ResolveViaActiveDirectory</c>'s filter;
    /// groups mirror <c>ResolveProtectedGroupDn</c>'s fallback and the three formats
    /// <c>MatchesDnToProtectedGroup</c> supports; an OU is only meaningful to
    /// <c>CheckOuMatches</c> as a DN, since that is a suffix comparison.
    ///
    /// <c>proxyAddresses</c> is deliberately absent: docs/ProtectedPrincipalResolution-Plan.md
    /// rejects broadening the AD filter, because Exchange already normalizes aliases to the
    /// primary address and two mechanisms for one job is the worse design.
    /// </remarks>
    internal static string BuildExactMatchFilter(string identity, string objectKind)
    {
        var escaped = ProtectedPrincipalService.EscapeLdapFilter(NormalizeIdentity(identity));

        return objectKind switch
        {
            "Group" => $"(|(distinguishedName={escaped})(cn={escaped})(sAMAccountName={escaped})(name={escaped}))",
            "OU" => $"(distinguishedName={escaped})",
            _ => $"(|(userPrincipalName={escaped})(mail={escaped})(sAMAccountName={escaped}))"
        };
    }

    /// <summary>
    /// Strips a <c>DOMAIN\</c> prefix, which neither sAMAccountName nor any other AD attribute
    /// carries. The admin page invites this form for users and groups, and
    /// <c>ProtectedPrincipalService.ResolveProtectedGroupDn</c> already strips it the same way -
    /// without this a legitimate <c>CONTOSO\Admins</c> entry would be rejected as nonexistent.
    /// </summary>
    internal static string NormalizeIdentity(string identity)
    {
        var trimmed = identity.Trim();
        var slash = trimmed.IndexOf('\\');

        // A trailing backslash leaves nothing to search for; keep the original so the caller
        // gets a NotFound on the literal input rather than an empty filter matching everything.
        if (slash < 0 || slash == trimmed.Length - 1)
            return trimmed;

        return trimmed[(slash + 1)..];
    }

    // Reused across searches (created and accessed only while _runspaceLock is
    // held). Building a runspace and importing the ActiveDirectory module per
    // query cost seconds under the global lock, serializing every user's
    // autocomplete keystrokes behind module imports.
    private Runspace? _searchRunspace;

    private Runspace GetOrCreateRunspace()
    {
        if (_searchRunspace is { RunspaceStateInfo.State: RunspaceState.Opened })
            return _searchRunspace;

        _searchRunspace?.Dispose();

        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        iss.ImportPSModule("ActiveDirectory");

        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        _searchRunspace = runspace;
        return runspace;
    }

    private List<ADSearchResult> ExecuteSearch(string term, string objectKind, int maxResults)
    {
        var escaped = ProtectedPrincipalService.EscapeLdapFilter(term);
        var results = new List<ADSearchResult>();

        var runspace = GetOrCreateRunspace();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        if (objectKind is "User" or "Any")
        {
            var userFilter = $"(|(displayName=*{escaped}*)(sAMAccountName=*{escaped}*)(userPrincipalName=*{escaped}*)(mail=*{escaped}*))";

            ps.AddCommand("Get-ADUser")
              .AddParameter("LDAPFilter", userFilter)
              .AddParameter("Properties", new[] { "DisplayName", "DistinguishedName", "SamAccountName", "UserPrincipalName", "mail" })
              .AddParameter("ResultSetSize", maxResults)
              .AddParameter("ErrorAction", "Stop");

            var users = ps.Invoke();
            ps.Commands.Clear();

            foreach (var obj in users)
            {
                results.Add(new ADSearchResult(
                    DisplayName: obj.Properties["DisplayName"]?.Value?.ToString() ?? "",
                    DistinguishedName: obj.Properties["DistinguishedName"]?.Value?.ToString() ?? "",
                    SamAccountName: obj.Properties["SamAccountName"]?.Value?.ToString(),
                    UserPrincipalName: obj.Properties["UserPrincipalName"]?.Value?.ToString(),
                    Email: obj.Properties["mail"]?.Value?.ToString(),
                    ObjectType: "User"));
            }

            if (ps.HadErrors)
            {
                var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Get-ADUser search failed";
                _logger.LogWarning("AD user search had errors: {Error}", errMsg);
                ps.Streams.Error.Clear();
            }
        }

        if (objectKind is "Group" or "Any")
        {
            var groupFilter = $"(|(displayName=*{escaped}*)(sAMAccountName=*{escaped}*)(mail=*{escaped}*))";

            ps.AddCommand("Get-ADGroup")
              .AddParameter("LDAPFilter", groupFilter)
              .AddParameter("Properties", new[] { "DisplayName", "DistinguishedName", "SamAccountName", "mail" })
              .AddParameter("ResultSetSize", maxResults)
              .AddParameter("ErrorAction", "Stop");

            var groups = ps.Invoke();
            ps.Commands.Clear();

            foreach (var obj in groups)
            {
                results.Add(new ADSearchResult(
                    DisplayName: obj.Properties["DisplayName"]?.Value?.ToString() ?? obj.Properties["Name"]?.Value?.ToString() ?? "",
                    DistinguishedName: obj.Properties["DistinguishedName"]?.Value?.ToString() ?? "",
                    SamAccountName: obj.Properties["SamAccountName"]?.Value?.ToString(),
                    UserPrincipalName: null,
                    Email: obj.Properties["mail"]?.Value?.ToString(),
                    ObjectType: "Group"));
            }

            if (ps.HadErrors)
            {
                var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Get-ADGroup search failed";
                _logger.LogWarning("AD group search had errors: {Error}", errMsg);
                ps.Streams.Error.Clear();
            }
        }

        // Deliberately NOT part of "Any" - see the remarks on Search. An OU suggestion is only
        // wanted where an OU is being picked.
        if (objectKind is "OU")
        {
            // Matched on name and DN: an operator either recalls what the OU is called, or is
            // pasting/refining a path they already have.
            var ouFilter = $"(&(objectClass=organizationalUnit)(|(name=*{escaped}*)(distinguishedName=*{escaped}*)))";

            ps.AddCommand("Get-ADOrganizationalUnit")
              .AddParameter("LDAPFilter", ouFilter)
              .AddParameter("Properties", new[] { "Name", "DistinguishedName" })
              .AddParameter("ResultSetSize", maxResults)
              .AddParameter("ErrorAction", "Stop");

            var ous = ps.Invoke();
            ps.Commands.Clear();

            foreach (var obj in ous)
            {
                if (obj is null)
                    continue;

                results.Add(new ADSearchResult(
                    // "Name", not "DisplayName" - Get-ADOrganizationalUnit does not return the
                    // latter, and reading it yields a blank suggestion label. Guarded by
                    // ADDirectoryLiveTests.Search_Ou_MapsNameAndDistinguishedName.
                    DisplayName: obj.Properties["Name"]?.Value?.ToString() ?? "",
                    DistinguishedName: obj.Properties["DistinguishedName"]?.Value?.ToString() ?? "",
                    SamAccountName: null,
                    UserPrincipalName: null,
                    Email: null,
                    ObjectType: "OU"));
            }

            if (ps.HadErrors)
            {
                var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Get-ADOrganizationalUnit search failed";
                _logger.LogWarning("AD OU search had errors: {Error}", errMsg);
                ps.Streams.Error.Clear();
            }
        }

        // Sort by DisplayName, then cap to maxResults (relevant when objectKind is "Any"
        // and both user + group results are combined)
        results.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        if (results.Count > maxResults)
            results = results.GetRange(0, maxResults);

        return results;
    }

    private void ProbeAvailability()
    {
        try
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();

            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            ps.AddCommand("Import-Module")
              .AddParameter("Name", "ActiveDirectory")
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();

            if (ps.HadErrors)
            {
                _logger.LogWarning("ActiveDirectory module could not be loaded. AD autocomplete will be unavailable.");
                lock (_availabilityLock) { _isAvailable = false; }
                return;
            }

            lock (_availabilityLock) { _isAvailable = true; }
            _logger.LogInformation("ActiveDirectory module loaded successfully. AD autocomplete is available.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ActiveDirectory module is not available. AD autocomplete will be disabled. " +
                "Ensure RSAT Active Directory tools are installed.");
            lock (_availabilityLock) { _isAvailable = false; }
        }
    }
}

/// <summary>
/// The three answers an existence check can give. The distinction between <see cref="NotFound"/>
/// and <see cref="Unavailable"/> is the point of the type: one means the operator mistyped, the
/// other means retry later, and reporting the first when the second is true tells an admin their
/// correct entry was a typo during an outage.
/// </summary>
public enum DirectoryLookupOutcome
{
    /// <summary>The directory answered and the object exists.</summary>
    Found,

    /// <summary>The directory answered and no such object exists. An affirmative absence.</summary>
    NotFound,

    /// <summary>The lookup could not be performed. NOT evidence of absence.</summary>
    Unavailable
}

/// <summary>
/// The result of an existence check. <see cref="Match"/> is populated only when
/// <see cref="Outcome"/> is <see cref="DirectoryLookupOutcome.Found"/>.
/// </summary>
public sealed record DirectoryValidationResult(
    DirectoryLookupOutcome Outcome,
    ADSearchResult? Match);

/// <summary>
/// Represents a single Active Directory search result for autocomplete display.
/// </summary>
/// <param name="DisplayName">The object's display name.</param>
/// <param name="DistinguishedName">Full LDAP distinguished name.</param>
/// <param name="SamAccountName">Pre-Windows 2000 logon name (may be null for groups without one).</param>
/// <param name="UserPrincipalName">UPN (users only, null for groups).</param>
/// <param name="Email">Primary email address if set.</param>
/// <param name="ObjectType">"User" or "Group".</param>
public sealed record ADSearchResult(
    string DisplayName,
    string DistinguishedName,
    string? SamAccountName,
    string? UserPrincipalName,
    string? Email,
    string ObjectType);
