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
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

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

    public const string DirectoryReadSecretConfigKey = "DirectoryReadSecretId";
    public const string ProtectedPrincipalsModuleKey = "ProtectedPrincipals";

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

    /// <summary>
    /// Resolves an identity (UPN, email, sAMAccountName) to a full ResolvedDirectoryPrincipal
    /// using the configured directory-read credential. Returns null if resolution is unavailable.
    /// </summary>
    public async Task<ResolvedDirectoryPrincipal?> ResolveDirectoryPrincipalAsync(string identity)
    {
        if (DateTime.UtcNow - _lastCredentialFailure < CredentialFailureTtl)
        {
            _logger.LogDebug("Skipping principal resolution — credential recently failed");
            return null;
        }

        var secretId = GetDirectoryReadSecretId();
        if (secretId == null)
        {
            _logger.LogDebug("Cannot resolve principal — directory-read credential not configured");
            return null;
        }

        var creds = await _delineaService.GetCredentialsBySecretIdAsync(secretId.Value);
        if (creds == null)
        {
            _lastCredentialFailure = DateTime.UtcNow;
            _logger.LogWarning("Failed to retrieve directory-read credential for principal resolution");
            return null;
        }

        try
        {
            if (!await _adThrottle.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                _logger.LogWarning("AD throttle timeout during principal resolution for {Identity}", identity);
                return null;
            }

            try
            {
                return await Task.Run(() => ResolveViaActiveDirectory(identity, creds.Value.username, creds.Value.password, creds.Value.domain));
            }
            finally
            {
                _adThrottle.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve directory principal for {Identity}", identity);
            return null;
        }
    }

    private ResolvedDirectoryPrincipal? ResolveViaActiveDirectory(string identity, string username, string password, string domain)
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

        var credential = CreateCredential(username, password, domain);
        var escaped = EscapeLdapFilter(identity);
        var filter = $"(|(userPrincipalName={escaped})(mail={escaped})(sAMAccountName={escaped}))";

        ps.AddCommand("Get-ADUser")
          .AddParameter("LDAPFilter", filter)
          .AddParameter("Properties", new[] { "DisplayName", "UserPrincipalName", "SamAccountName", "mail", "DistinguishedName", "ObjectGUID" })
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var users = ps.Invoke();
        ps.Commands.Clear();

        var adUser = users.FirstOrDefault();
        if (adUser == null)
            return null;

        return new ResolvedDirectoryPrincipal(
            Source: "ProtectedPrincipalService-AD",
            DisplayName: adUser.Properties["DisplayName"]?.Value?.ToString() ?? identity,
            UserPrincipalName: adUser.Properties["UserPrincipalName"]?.Value?.ToString() ?? identity,
            SamAccountName: adUser.Properties["SamAccountName"]?.Value?.ToString(),
            PrimarySmtpAddress: adUser.Properties["mail"]?.Value?.ToString(),
            DistinguishedName: adUser.Properties["DistinguishedName"]?.Value?.ToString(),
            ObjectGuid: adUser.Properties["ObjectGUID"]?.Value?.ToString(),
            EntraObjectId: null);
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

    public int? GetDirectoryReadSecretId()
    {
        var fromModuleConfig = _moduleConfig.GetValue(ProtectedPrincipalsModuleKey, DirectoryReadSecretConfigKey);
        if (int.TryParse(fromModuleConfig, out var moduleId) && moduleId > 0)
            return moduleId;

        var fromAppSettings = _config["Security:ProtectedPrincipalDirectoryReadSecretId"];
        if (int.TryParse(fromAppSettings, out var appId) && appId > 0)
            return appId;

        return null;
    }

    public void SaveDirectoryReadSecretId(string value)
    {
        var current = _moduleConfig.GetModuleConfig(ProtectedPrincipalsModuleKey);
        current[DirectoryReadSecretConfigKey] = value;
        _moduleConfig.SaveModuleConfig(ProtectedPrincipalsModuleKey, current);
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
        return Regex.IsMatch(value, regexPattern, RegexOptions.IgnoreCase, RegexTimeout);
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

        var secretId = GetDirectoryReadSecretId();
        if (secretId == null)
        {
            _logger.LogError("Directory-read credential is not configured but protected groups are defined — configure it on the Protected Principals admin page");
            return (matches, true, "Protected-principal directory-read credential is not configured. Configure it on the Protected Principals admin page.");
        }

        var creds = await _delineaService.GetCredentialsBySecretIdAsync(secretId.Value);
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
                var (groupMatches, expansionHadErrors) = await Task.Run(() =>
                    CheckTransitiveGroupMembership(cfg.Groups, target, creds.Value.username, creds.Value.password, creds.Value.domain));
                matches.AddRange(groupMatches);

                // Fail closed: if expansion had errors and no matches were found,
                // we cannot confirm the user is NOT in a protected group
                if (expansionHadErrors && matches.Count == 0)
                {
                    _logger.LogWarning("Group expansion had errors and no matches found — failing closed for {Target}", target.UserPrincipalName);
                    return (matches, true, "Group membership check was incomplete due to expansion errors. Cannot confirm target is not protected.");
                }
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

    private (List<string> matches, bool expansionHadErrors) CheckTransitiveGroupMembership(
        string[] protectedGroups, ResolvedDirectoryPrincipal target,
        string username, string password, string domain)
    {
        var matches = new List<string>();
        bool expansionHadErrors = false;

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
            return (matches, false);

        foreach (var protectedGroup in protectedGroups.Where(g => !string.IsNullOrWhiteSpace(g)))
        {
            try
            {
                var groupDn = ResolveProtectedGroupDn(ps, credential, protectedGroup);
                if (string.IsNullOrWhiteSpace(groupDn))
                {
                    _logger.LogWarning("Protected group {ProtectedGroup} could not be resolved during membership check", protectedGroup);
                    expansionHadErrors = true;
                    continue;
                }

                var targetFilter = EscapeLdapFilter(targetDn);
                var groupFilter = EscapeLdapFilter(groupDn);
                ps.AddCommand("Get-ADUser")
                  .AddParameter("LDAPFilter", $"(&(distinguishedName={targetFilter})(memberOf:1.2.840.113556.1.4.1941:={groupFilter}))")
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                var userResult = ps.Invoke();
                ps.Commands.Clear();

                if (ps.HadErrors)
                {
                    expansionHadErrors = true;
                    ps.Streams.Error.Clear();
                    continue;
                }

                if (userResult.Count > 0)
                    matches.Add($"Group:{protectedGroup}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to evaluate protected group {ProtectedGroup} during transitive membership check", protectedGroup);
                expansionHadErrors = true;
                ps.Commands.Clear();
                ps.Streams.Error.Clear();
            }
        }

        return (matches, expansionHadErrors);
    }

    private string? ResolveProtectedGroupDn(PowerShell ps, PSCredential credential, string protectedGroup)
    {
        if (protectedGroup.Contains("DC=", StringComparison.OrdinalIgnoreCase) ||
            protectedGroup.Contains("CN=", StringComparison.OrdinalIgnoreCase))
        {
            return protectedGroup;
        }

        var groupIdentity = protectedGroup.Contains('\\')
            ? protectedGroup.Split('\\', 2)[1]
            : protectedGroup;

        try
        {
            ps.AddCommand("Get-ADGroup")
              .AddParameter("Identity", groupIdentity)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var result = ps.Invoke();
            ps.Commands.Clear();

            var dn = result.FirstOrDefault()?.Properties["DistinguishedName"]?.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(dn))
                return dn;
        }
        catch
        {
            ps.Commands.Clear();
            ps.Streams.Error.Clear();
        }

        var escaped = EscapeLdapFilter(groupIdentity);
        ps.AddCommand("Get-ADGroup")
          .AddParameter("LDAPFilter", $"(|(cn={escaped})(sAMAccountName={escaped})(name={escaped}))")
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var fallback = ps.Invoke();
        ps.Commands.Clear();

        return fallback.FirstOrDefault()?.Properties["DistinguishedName"]?.Value?.ToString();
    }

    /// <summary>
    /// Matches a Distinguished Name from expandedGroups against a protectedGroup config value.
    /// Supports three formats:
    /// - Full DN (contains "DC=" or "CN="): compare full DN case-insensitively
    /// - DOMAIN\GroupName: extract name after backslash, compare against CN extracted from DN
    /// - Simple name: extract CN from DN and compare case-insensitively
    /// </summary>
    internal static bool MatchesDnToProtectedGroup(string groupDn, string protectedGroup)
    {
        if (string.IsNullOrEmpty(groupDn) || string.IsNullOrEmpty(protectedGroup))
            return false;

        // If protectedGroup looks like a DN, compare full DN
        if (protectedGroup.Contains("DC=", StringComparison.OrdinalIgnoreCase) ||
            protectedGroup.Contains("CN=", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(groupDn, protectedGroup, StringComparison.OrdinalIgnoreCase);
        }

        // Extract the CN from the group DN for name-based comparisons
        var cn = ExtractCnFromDn(groupDn);
        if (cn == null)
            return false;

        // If protectedGroup is in DOMAIN\GroupName format, extract the name part
        if (protectedGroup.Contains('\\'))
        {
            var parts = protectedGroup.Split('\\', 2);
            if (parts.Length == 2)
                return string.Equals(cn, parts[1], StringComparison.OrdinalIgnoreCase);
        }

        // Simple name comparison against extracted CN
        return string.Equals(cn, protectedGroup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the Common Name (CN) from a Distinguished Name.
    /// For "CN=Domain Admins,CN=Users,DC=ad,DC=analog,DC=com" returns "Domain Admins".
    /// Handles escaped commas (\,) within the CN value.
    /// </summary>
    internal static string? ExtractCnFromDn(string dn)
    {
        if (string.IsNullOrEmpty(dn))
            return null;

        const string cnPrefix = "CN=";
        if (!dn.StartsWith(cnPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var valueStart = cnPrefix.Length;
        // Find the first unescaped comma
        for (int i = valueStart; i < dn.Length; i++)
        {
            if (dn[i] == ',' && (i == 0 || dn[i - 1] != '\\'))
                return dn[valueStart..i];
        }

        // No comma found — entire remaining string is the CN value
        return dn[valueStart..];
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
