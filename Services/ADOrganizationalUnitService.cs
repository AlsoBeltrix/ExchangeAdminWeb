using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Enumerates Active Directory Organizational Units using the app pool's ambient identity.
/// No Delinea credential is needed - standard domain membership is sufficient for
/// <c>Get-ADOrganizationalUnit -Filter *</c>.
/// </summary>
public sealed class ADOrganizationalUnitService
{
    private readonly ILogger<ADOrganizationalUnitService> _logger;
    private readonly object _cacheLock = new();
    private List<OUEntry>? _cached;
    private DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True when the last enumeration succeeded. When false the Browse OUs button
    /// should be hidden so operators can still type DNs manually.
    /// </summary>
    public bool IsAvailable { get; private set; }

    public ADOrganizationalUnitService(ILogger<ADOrganizationalUnitService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns all OUs sorted by DN depth then name, or an empty list when AD
    /// enumeration is not possible (RSAT missing, insufficient permissions, etc.).
    /// </summary>
    public List<OUEntry> GetOrganizationalUnits()
    {
        lock (_cacheLock)
        {
            if (_cached != null && DateTime.UtcNow - _cachedAt < CacheTtl)
                return _cached;
        }

        try
        {
            var entries = EnumerateOUs();

            lock (_cacheLock)
            {
                _cached = entries;
                _cachedAt = DateTime.UtcNow;
            }

            IsAvailable = true;
            return entries;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to enumerate AD Organizational Units. The OU browser will be unavailable. " +
                "Ensure RSAT Active Directory tools are installed and the app pool identity has read access.");

            lock (_cacheLock)
            {
                _cached = [];
                _cachedAt = DateTime.UtcNow;
            }

            IsAvailable = false;
            return [];
        }
    }

    /// <summary>
    /// Invalidates the cached OU list so the next call re-reads from AD.
    /// </summary>
    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cached = null;
            _cachedAt = DateTime.MinValue;
        }
    }

    private List<OUEntry> EnumerateOUs()
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        using var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        // Import the ActiveDirectory module
        ps.AddCommand("Import-Module")
          .AddParameter("Name", "ActiveDirectory")
          .AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ps.Commands.Clear();

        // Enumerate all OUs - runs under the app pool identity (no credential parameter)
        ps.AddCommand("Get-ADOrganizationalUnit")
          .AddParameter("Filter", "*")
          .AddParameter("Properties", new[] { "Name", "DistinguishedName" })
          .AddParameter("ErrorAction", "Stop");

        var results = ps.Invoke();
        ps.Commands.Clear();

        if (ps.HadErrors)
        {
            var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message
                         ?? "Get-ADOrganizationalUnit failed.";
            throw new InvalidOperationException(errMsg);
        }

        var entries = new List<OUEntry>(results.Count);

        foreach (var obj in results)
        {
            var name = obj.Properties["Name"]?.Value?.ToString();
            var dn = obj.Properties["DistinguishedName"]?.Value?.ToString();

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dn))
                continue;

            var depth = dn.Count(c => c == ',');
            entries.Add(new OUEntry(name, dn, depth));
        }

        // Sort by depth (shallowest first = tree root), then alphabetically by name
        entries.Sort((a, b) =>
        {
            var depthCmp = a.Depth.CompareTo(b.Depth);
            return depthCmp != 0
                ? depthCmp
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        _logger.LogInformation("Enumerated {Count} organizational units from Active Directory", entries.Count);
        return entries;
    }
}

/// <summary>
/// Represents an Active Directory Organizational Unit for the OU browser/picker.
/// </summary>
/// <param name="Name">The OU's display name.</param>
/// <param name="DistinguishedName">The full LDAP distinguished name.</param>
/// <param name="Depth">Number of commas in the DN, used for tree indentation.</param>
public sealed record OUEntry(string Name, string DistinguishedName, int Depth);
