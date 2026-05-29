using System.Collections.Immutable;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Services;

public sealed record ResolvedDirectoryPrincipal(
    string Source,
    string DisplayName,
    string UserPrincipalName,
    string? SamAccountName,
    string? PrimarySmtpAddress,
    string? DistinguishedName,
    string? ObjectGuid,
    string? EntraObjectId);

public sealed record ProtectedPrincipalResult(
    bool IsProtected,
    bool CheckFailed,
    string Reason,
    string[] MatchedRules)
{
    public static ProtectedPrincipalResult NotProtected() => new(false, false, "", []);
    public static ProtectedPrincipalResult Protected(string reason, params string[] rules) => new(true, false, reason, rules);
    public static ProtectedPrincipalResult Failed(string reason) => new(false, true, reason, []);
}

public sealed class ProtectedPrincipalConfig
{
    public string[] Users { get; set; } = [];
    public string[] Groups { get; set; } = [];
    public string[] OrganizationalUnits { get; set; } = [];
    public string[] SamAccountNamePatterns { get; set; } = [];
}

public class ProtectedPrincipalService
{
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ModuleConfigService _moduleConfig;
    private readonly DelineaService _delineaService;
    private readonly ILogger<ProtectedPrincipalService> _logger;
    private readonly object _cacheLock = new();

    private ProtectedPrincipalConfig? _cachedConfig;
    private DateTime _configLoadedAt = DateTime.MinValue;
    private bool _configCorrupt;
    private string? _configFilePath;

    private static readonly TimeSpan ConfigCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CredentialFailureTtl = TimeSpan.FromSeconds(60);

    private DateTime _lastCredentialFailure = DateTime.MinValue;
    private static readonly SemaphoreSlim _adThrottle = new(2, 2);

    public ProtectedPrincipalService(
        IWebHostEnvironment env,
        IConfiguration config,
        ModuleConfigService moduleConfig,
        DelineaService delineaService,
        ILogger<ProtectedPrincipalService> logger)
    {
        _env = env;
        _config = config;
        _moduleConfig = moduleConfig;
        _delineaService = delineaService;
        _logger = logger;
        _configFilePath = Path.Combine(env.ContentRootPath, "config", "protected-principals.json");
    }

    public bool HasCentralConfig => File.Exists(_configFilePath);

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedConfig = null;
            _configLoadedAt = DateTime.MinValue;
        }
    }

    public async Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
    {
        var (cfg, legacyExclusions, loadError) = LoadEffectiveConfig();

        if (loadError != null)
            return ProtectedPrincipalResult.Failed(loadError);

        var matchedRules = new List<string>();

        if (cfg != null)
        {
            CheckDirectUserMatches(cfg, target, matchedRules);
            CheckPatternMatches(cfg, target, matchedRules);
            CheckOuMatches(cfg, target, matchedRules);

            if (cfg.Groups.Length > 0)
            {
                var groupResult = await CheckGroupMembershipAsync(cfg, target);
                if (groupResult.checkFailed)
                    return ProtectedPrincipalResult.Failed(groupResult.failReason ?? "Group membership check failed.");
                matchedRules.AddRange(groupResult.matches);
            }
        }

        if (legacyExclusions.Length > 0)
            CheckLegacyExclusions(legacyExclusions, target, matchedRules);

        if (matchedRules.Count > 0)
            return ProtectedPrincipalResult.Protected(
                "Target is a protected principal.",
                matchedRules.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

        return ProtectedPrincipalResult.NotProtected();
    }

    public (ProtectedPrincipalConfig? config, string[] legacyExclusions, string? error) LoadEffectiveConfig()
    {
        lock (_cacheLock)
        {
            if (_cachedConfig != null && DateTime.UtcNow - _configLoadedAt < ConfigCacheTtl && !_configCorrupt)
                return (_cachedConfig, GetLegacyExclusions(), null);
        }

        ProtectedPrincipalConfig? config = null;
        var configPath = _configFilePath!;

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var wrapper = JsonSerializer.Deserialize<ProtectedPrincipalsFileWrapper>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                config = wrapper?.ProtectedPrincipals;

                if (config == null)
                {
                    _logger.LogError("Protected-principals config exists but ProtectedPrincipals section is missing");
                    lock (_cacheLock) { _configCorrupt = true; }
                    return (null, [], "Protected-principals configuration is corrupt. Contact your administrator.");
                }

                lock (_cacheLock)
                {
                    _cachedConfig = config;
                    _configLoadedAt = DateTime.UtcNow;
                    _configCorrupt = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse protected-principals.json — failing closed");
                lock (_cacheLock) { _configCorrupt = true; }
                return (null, [], "Protected-principals configuration is corrupt. Contact your administrator.");
            }
        }
        else
        {
            lock (_cacheLock)
            {
                _cachedConfig = null;
                _configLoadedAt = DateTime.UtcNow;
                _configCorrupt = false;
            }
        }

        return (config, GetLegacyExclusions(), null);
    }

    public void SaveConfig(ProtectedPrincipalConfig config)
    {
        var configDir = Path.GetDirectoryName(_configFilePath!)!;
        Directory.CreateDirectory(configDir);

        var wrapper = new ProtectedPrincipalsFileWrapper { ProtectedPrincipals = config };
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(wrapper, options);

        var tempPath = Path.Combine(configDir, $"protected-principals.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(_configFilePath))
                File.Replace(tempPath, _configFilePath!, null);
            else
                File.Move(tempPath, _configFilePath!);
        }
        finally
        {
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { }
        }

        InvalidateCache();
        _logger.LogInformation("Protected-principals config saved and cache invalidated");
    }

    private string[] GetLegacyExclusions()
    {
        var excluded = _moduleConfig.GetValue("MailboxPermissions", "ExcludedUsers");
        if (!string.IsNullOrEmpty(excluded))
            return excluded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var legacy = _config.GetSection("Security:ExcludedUsers").Get<string[]>();
        return legacy ?? [];
    }

    private static void CheckDirectUserMatches(ProtectedPrincipalConfig cfg, ResolvedDirectoryPrincipal target, List<string> matchedRules)
    {
        foreach (var protectedUser in cfg.Users)
        {
            if (MatchesIdentity(protectedUser, target))
                matchedRules.Add($"User:{protectedUser}");
        }
    }

    private static bool MatchesIdentity(string protectedValue, ResolvedDirectoryPrincipal target)
    {
        var candidates = new[]
        {
            target.UserPrincipalName,
            target.PrimarySmtpAddress,
            target.SamAccountName,
            target.DistinguishedName,
            target.ObjectGuid,
            target.EntraObjectId
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate) &&
                string.Equals(protectedValue, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (!string.IsNullOrEmpty(target.SamAccountName) && protectedValue.Contains('\\'))
        {
            var parts = protectedValue.Split('\\', 2);
            if (parts.Length == 2 && string.Equals(parts[1], target.SamAccountName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void CheckPatternMatches(ProtectedPrincipalConfig cfg, ResolvedDirectoryPrincipal target, List<string> matchedRules)
    {
        if (string.IsNullOrEmpty(target.SamAccountName))
            return;

        foreach (var pattern in cfg.SamAccountNamePatterns)
        {
            if (MatchesWildcardPattern(pattern, target.SamAccountName))
                matchedRules.Add($"Pattern:{pattern}");
        }
    }

    internal static bool MatchesWildcardPattern(string pattern, string value)
    {
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regexPattern, RegexOptions.IgnoreCase);
    }

    private static void CheckOuMatches(ProtectedPrincipalConfig cfg, ResolvedDirectoryPrincipal target, List<string> matchedRules)
    {
        if (string.IsNullOrEmpty(target.DistinguishedName))
            return;

        foreach (var ou in cfg.OrganizationalUnits)
        {
            if (target.DistinguishedName.EndsWith("," + ou, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target.DistinguishedName, ou, StringComparison.OrdinalIgnoreCase))
                matchedRules.Add($"OU:{ou}");
        }
    }

    private async Task<(List<string> matches, bool checkFailed, string? failReason)> CheckGroupMembershipAsync(
        ProtectedPrincipalConfig cfg, ResolvedDirectoryPrincipal target)
    {
        var matches = new List<string>();

        if (DateTime.UtcNow - _lastCredentialFailure < CredentialFailureTtl)
            return (matches, true, "Directory-read credential recently failed. Retry shortly or check configuration.");

        var secretIdStr = _config["Security:ProtectedPrincipalDirectoryReadSecretId"];
        if (!int.TryParse(secretIdStr, out var secretId) || secretId <= 0)
        {
            _logger.LogError("Security:ProtectedPrincipalDirectoryReadSecretId is not configured but protected groups are defined");
            return (matches, true, "Protected-principal directory-read credential is not configured. Contact your administrator.");
        }

        var creds = await _delineaService.GetCredentialsBySecretIdAsync(secretId);
        if (creds == null)
        {
            _lastCredentialFailure = DateTime.UtcNow;
            _logger.LogError("Failed to retrieve directory-read credential for protected-principal group check");
            return (matches, true, "Protected-principal directory-read credential is unavailable. Contact your administrator.");
        }

        try
        {
            if (!await _adThrottle.WaitAsync(TimeSpan.FromMinutes(2)))
                return (matches, true, "AD service is busy. Please try again.");

            try
            {
                var groupMatches = await Task.Run(() =>
                    CheckTransitiveGroupMembership(cfg.Groups, target, creds.Value.username, creds.Value.password, creds.Value.domain));
                matches.AddRange(groupMatches);
            }
            finally
            {
                _adThrottle.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Group membership check failed for {Target}", target.UserPrincipalName);
            return (matches, true, "Group membership check failed. Contact your administrator.");
        }

        return (matches, false, null);
    }

    private List<string> CheckTransitiveGroupMembership(
        string[] protectedGroups, ResolvedDirectoryPrincipal target,
        string username, string password, string domain)
    {
        var matches = new List<string>();

        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        using var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ps.Commands.Clear();

        var credential = CreateCredential(username, password, domain);

        string? targetDn = target.DistinguishedName;
        if (string.IsNullOrEmpty(targetDn) && !string.IsNullOrEmpty(target.SamAccountName))
        {
            var escaped = EscapeLdapFilter(target.SamAccountName);
            ps.AddCommand("Get-ADUser")
              .AddParameter("LDAPFilter", $"(sAMAccountName={escaped})")
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var users = ps.Invoke();
            ps.Commands.Clear();
            targetDn = users.FirstOrDefault()?.Properties["DistinguishedName"]?.Value?.ToString();
        }

        if (string.IsNullOrEmpty(targetDn))
            return matches;

        ps.AddCommand("Get-ADUser")
          .AddParameter("Identity", targetDn)
          .AddParameter("Properties", new[] { "memberOf" })
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var adUser = ps.Invoke();
        ps.Commands.Clear();

        var memberOfRaw = adUser.FirstOrDefault()?.Properties["memberOf"]?.Value;
        var allGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (memberOfRaw is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                if (item != null) allGroups.Add(item.ToString()!);
        }
        else if (memberOfRaw is string singleGroup)
        {
            allGroups.Add(singleGroup);
        }

        var expandedGroups = new HashSet<string>(allGroups, StringComparer.OrdinalIgnoreCase);
        var toExpand = new Queue<string>(allGroups);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (toExpand.Count > 0)
        {
            var groupDn = toExpand.Dequeue();
            if (!visited.Add(groupDn)) continue;

            try
            {
                ps.AddCommand("Get-ADGroup")
                  .AddParameter("Identity", groupDn)
                  .AddParameter("Properties", new[] { "memberOf" })
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "SilentlyContinue");
                var groupResult = ps.Invoke();
                ps.Commands.Clear();

                var parentGroups = groupResult.FirstOrDefault()?.Properties["memberOf"]?.Value;
                if (parentGroups is System.Collections.IEnumerable parentEnum)
                {
                    foreach (var parent in parentEnum)
                    {
                        if (parent != null)
                        {
                            var parentDn = parent.ToString()!;
                            if (expandedGroups.Add(parentDn))
                                toExpand.Enqueue(parentDn);
                        }
                    }
                }
                else if (parentGroups is string singleParent && expandedGroups.Add(singleParent))
                {
                    toExpand.Enqueue(singleParent);
                }
            }
            catch
            {
                ps.Commands.Clear();
            }
        }

        foreach (var protectedGroup in protectedGroups)
        {
            if (expandedGroups.Any(g => g.Contains(protectedGroup, StringComparison.OrdinalIgnoreCase)))
                matches.Add($"Group:{protectedGroup}");
        }

        return matches;
    }

    private static void CheckLegacyExclusions(string[] exclusions, ResolvedDirectoryPrincipal target, List<string> matchedRules)
    {
        foreach (var excluded in exclusions)
        {
            if (MatchesIdentity(excluded, target))
                matchedRules.Add($"LegacyExclusion:{excluded}");
        }
    }

    internal static string EscapeLdapFilter(string input)
    {
        var sb = new System.Text.StringBuilder(input.Length + 10);
        foreach (var c in input)
        {
            switch (c)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*': sb.Append("\\2a"); break;
                case '(': sb.Append("\\28"); break;
                case ')': sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static PSCredential CreateCredential(string username, string password, string domain)
    {
        var fullUsername = username.Contains('\\') || username.Contains('@')
            ? username : $"{domain}\\{username}";
        var securePassword = new System.Security.SecureString();
        foreach (var c in password) securePassword.AppendChar(c);
        return new PSCredential(fullUsername, securePassword);
    }

    private sealed class ProtectedPrincipalsFileWrapper
    {
        public ProtectedPrincipalConfig? ProtectedPrincipals { get; set; }
    }
}
