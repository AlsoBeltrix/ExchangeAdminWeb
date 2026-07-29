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
    /// <param name="objectKind">"User", "Group", or "Any".</param>
    /// <param name="maxResults">Maximum results to return (default 25).</param>
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
