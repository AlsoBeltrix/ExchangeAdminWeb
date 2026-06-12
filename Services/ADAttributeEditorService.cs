using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Services;

public sealed record EditableAttribute(
    string Name,
    string Label,
    string Type,
    string[]? Choices,
    bool Required,
    bool AllowClear,
    int? MaxLength,
    string? Pattern,
    int Level = 1);

public sealed record AttributeLookupResult(
    bool Success,
    string? Error,
    ResolvedDirectoryPrincipal? Principal,
    string? SourceOfAuthority,
    bool IsReadOnly,
    string? ReadOnlyReason,
    Dictionary<string, string?>? CurrentValues);

public sealed record AttributeChange(string Name, string Label, string? OldValue, string? NewValue);

public sealed record AttributeSavePreview(
    bool Success,
    string? Error,
    List<AttributeChange>? Changes);

public sealed record AttributeLegendEntry(string Description, string? Note = null, string? Source = null);

public sealed record AttributeSaveResult(
    bool Success,
    string? Error,
    List<AttributeChange>? AppliedChanges);

public class ADAttributeEditorService
{
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly ProtectedPrincipalService _protectedPrincipalService;
    private readonly OperationTraceService _operationTrace;
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly ModuleConfigService _moduleConfig;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ADAttributeEditorService> _logger;
    private readonly object _allowlistLock = new();

    private List<EditableAttribute>? _cachedAllowlist;
    private DateTime _allowlistLoadedAt = DateTime.MinValue;
    private Dictionary<string, Dictionary<string, AttributeLegendEntry>>? _cachedLegend;
    private DateTime _legendLoadedAt = DateTime.MinValue;
    private static readonly TimeSpan AllowlistCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim _adThrottle = new(2, 2);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> HardDenylistExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "userAccountControl", "pwdLastSet", "unicodePwd", "userPassword",
        "lockoutTime", "accountExpires", "memberOf", "primaryGroupID",
        "adminCount", "badPwdCount", "objectSid", "objectGUID",
        "distinguishedName", "servicePrincipalName", "altSecurityIdentities",
        "nTSecurityDescriptor"
    };

    private static readonly string[] HardDenylistPrefixes = ["lastLogon", "msDS-"];

    public ADAttributeEditorService(
        ModuleCredentialService moduleCredentials,
        ProtectedPrincipalService protectedPrincipalService,
        OperationTraceService operationTrace,
        AuditService audit,
        EmailService email,
        ModuleConfigService moduleConfig,
        IWebHostEnvironment env,
        ILogger<ADAttributeEditorService> logger)
    {
        _moduleCredentials = moduleCredentials;
        _protectedPrincipalService = protectedPrincipalService;
        _operationTrace = operationTrace;
        _audit = audit;
        _email = email;
        _moduleConfig = moduleConfig;
        _env = env;
        _logger = logger;
    }

    public void InvalidateAllowlistCache()
    {
        lock (_allowlistLock)
        {
            _cachedAllowlist = null;
            _allowlistLoadedAt = DateTime.MinValue;
        }
    }

    public List<EditableAttribute>? GetAllowlist()
    {
        lock (_allowlistLock)
        {
            if (_cachedAllowlist != null && DateTime.UtcNow - _allowlistLoadedAt < AllowlistCacheTtl)
                return _cachedAllowlist;
        }

        var configPath = Path.Combine(_env.ContentRootPath, "config", "ad-editable-attributes.json");
        if (!File.Exists(configPath))
        {
            lock (_allowlistLock)
            {
                _cachedAllowlist = [];
                _allowlistLoadedAt = DateTime.UtcNow;
            }
            return [];
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var wrapper = JsonSerializer.Deserialize<AttributeAllowlistFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (wrapper?.Attributes == null)
            {
                _logger.LogError("ad-editable-attributes.json exists but Attributes section is missing — failing closed");
                return null;
            }

            var validated = new List<EditableAttribute>();
            foreach (var attr in wrapper.Attributes)
            {
                if (IsDenylisted(attr.Name))
                {
                    _logger.LogWarning("Attribute {Name} is on the hard denylist and was removed from the allowlist", attr.Name);
                    continue;
                }

                if (attr.Required && attr.AllowClear)
                {
                    _logger.LogError("Contradictory config for attribute {Name}: Required=true with AllowClear=true — failing closed", attr.Name);
                    return null;
                }

                if (attr.Type == "Choice" && (attr.Choices == null || attr.Choices.Length == 0))
                {
                    _logger.LogError("Attribute {Name} is type Choice but has no Choices defined — failing closed", attr.Name);
                    return null;
                }

                validated.Add(new EditableAttribute(
                    attr.Name, attr.Label, attr.Type,
                    attr.Choices, attr.Required, attr.AllowClear,
                    attr.MaxLength, attr.Pattern, attr.Level > 0 ? attr.Level : 1));
            }

            lock (_allowlistLock)
            {
                _cachedAllowlist = validated;
                _allowlistLoadedAt = DateTime.UtcNow;
            }
            return validated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse ad-editable-attributes.json — failing closed");
            return null;
        }
    }

    public List<EditableAttribute>? GetAllowlistForLevel(int maxLevel)
    {
        var all = GetAllowlist();
        return all?.Where(a => a.Level <= maxLevel).ToList();
    }

    public void SaveAllowlist(List<EditableAttribute> attributes)
    {
        var configDir = Path.Combine(_env.ContentRootPath, "config");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "ad-editable-attributes.json");

        var wrapper = new AttributeAllowlistFile
        {
            Attributes = attributes.Select(a => new AttributeAllowlistEntry
            {
                Name = a.Name,
                Label = a.Label,
                Type = a.Type,
                Choices = a.Choices,
                Required = a.Required,
                AllowClear = a.AllowClear,
                MaxLength = a.MaxLength,
                Pattern = a.Pattern,
                Level = a.Level
            }).ToArray()
        };

        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(wrapper, options);

        var tempPath = Path.Combine(configDir, $"ad-editable-attributes.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(configPath))
                File.Replace(tempPath, configPath, null);
            else
                File.Move(tempPath, configPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { }
        }

        InvalidateAllowlistCache();
    }

    public Dictionary<string, Dictionary<string, AttributeLegendEntry>> GetLegend()
    {
        lock (_allowlistLock)
        {
            if (_cachedLegend != null && DateTime.UtcNow - _legendLoadedAt < AllowlistCacheTtl)
                return _cachedLegend;
        }

        var configPath = Path.Combine(_env.ContentRootPath, "config", "ad-editable-attributes-legend.json");
        if (!File.Exists(configPath))
        {
            lock (_allowlistLock)
            {
                _cachedLegend = new();
                _legendLoadedAt = DateTime.UtcNow;
            }
            return new();
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, AttributeLegendEntry>>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new();

            lock (_allowlistLock)
            {
                _cachedLegend = parsed;
                _legendLoadedAt = DateTime.UtcNow;
            }
            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load ad-editable-attributes-legend.json — choices will display without descriptions");
            lock (_allowlistLock)
            {
                _cachedLegend = new();
                _legendLoadedAt = DateTime.UtcNow;
            }
            return new();
        }
    }

    public string[] GetEffectiveChoices(EditableAttribute attr)
    {
        var legend = GetLegend();
        if (legend.TryGetValue(attr.Name, out var attrLegend) && attrLegend.Count > 0)
        {
            var legendKeys = attrLegend.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (attr.Choices != null)
            {
                foreach (var c in attr.Choices)
                    legendKeys.Add(c);
            }

            return legendKeys
                .Select(k => (key: k, numVal: long.TryParse(k, out var n) ? n : long.MaxValue))
                .OrderBy(x => x.numVal < 0 ? 1 : 0)
                .ThenBy(x => Math.Abs(x.numVal))
                .Select(x => x.key)
                .ToArray();
        }

        return attr.Choices ?? [];
    }

    public string GetChoiceDisplayText(string attributeName, string value)
    {
        var legend = GetLegend();
        if (legend.TryGetValue(attributeName, out var attrLegend) &&
            attrLegend.TryGetValue(value, out var entry))
        {
            return $"{value} — {entry.Description}";
        }
        return value;
    }

    public async Task<AttributeLookupResult> LookupAsync(string identity)
    {
        var creds = await _moduleCredentials.GetCredentialsAsync("ADAttributeEditor", "attribute lookup");
        if (creds == null)
            return new(false, "AD credentials unavailable. Configure the module's Delinea Secret ID.", null, null, false, null, null);

        var allowlist = GetAllowlist();
        if (allowlist == null)
            return new(false, "Attribute allowlist configuration is corrupt. Contact your administrator.", null, null, false, null, null);

        if (allowlist.Count == 0)
            return new(false, "No editable attributes configured. An administrator must configure the attribute allowlist.", null, null, false, null, null);

        if (!await _adThrottle.WaitAsync(TimeSpan.FromMinutes(2)))
            return new(false, "AD service is busy. Please try again.", null, null, false, null, null);

        try
        {
            return await Task.Run(() => PerformLookup(identity, creds.Value, allowlist));
        }
        finally
        {
            _adThrottle.Release();
        }
    }

    public async Task<AttributeSavePreview> PreviewAsync(ResolvedDirectoryPrincipal target, Dictionary<string, string?> proposedValues)
    {
        var allowlist = GetAllowlist();
        if (allowlist == null)
            return new(false, "Attribute allowlist configuration is corrupt.", null);

        var validationError = ValidateProposedValues(proposedValues, allowlist);
        if (validationError != null)
            return new(false, validationError, null);

        var creds = await _moduleCredentials.GetCredentialsAsync("ADAttributeEditor", "attribute preview");
        if (creds == null)
            return new(false, "AD credentials unavailable.", null);

        if (!await _adThrottle.WaitAsync(TimeSpan.FromMinutes(2)))
            return new(false, "AD service is busy.", null);

        try
        {
            return await Task.Run(() => ComputePreview(target, proposedValues, creds.Value, allowlist));
        }
        finally
        {
            _adThrottle.Release();
        }
    }

    public async Task<AttributeSaveResult> SaveAsync(
        ResolvedDirectoryPrincipal target,
        Dictionary<string, string?> proposedValues,
        string performedBy,
        string ip,
        string ticket,
        int? maxLevel = null)
    {
        var allowlist = GetAllowlist();
        if (allowlist == null)
            return new(false, "Attribute allowlist configuration is corrupt.", null);

        // If a maxLevel is provided, filter the allowlist to enforce server-side level restrictions
        if (maxLevel.HasValue)
            allowlist = allowlist.Where(a => a.Level <= maxLevel.Value).ToList();

        var validationError = ValidateProposedValues(proposedValues, allowlist);
        if (validationError != null)
            return new(false, validationError, null);

        foreach (var key in proposedValues.Keys)
        {
            if (IsDenylisted(key))
                return new(false, $"Attribute '{key}' is on the hard denylist and cannot be modified.", null);
        }

        var protectionResult = await _protectedPrincipalService.CheckAsync(target);
        if (protectionResult.CheckFailed)
            return new(false, protectionResult.Reason, null);
        if (protectionResult.IsProtected)
            return new(false, "Target is a protected principal and cannot be modified.", null);

        var creds = await _moduleCredentials.GetCredentialsAsync("ADAttributeEditor", "attribute save");
        if (creds == null)
            return new(false, "AD credentials unavailable.", null);

        if (!await _adThrottle.WaitAsync(TimeSpan.FromMinutes(2)))
            return new(false, "AD service is busy.", null);

        try
        {
            return await Task.Run(() => PerformSave(target, proposedValues, creds.Value, allowlist, performedBy, ip, ticket));
        }
        finally
        {
            _adThrottle.Release();
        }
    }

    internal static bool IsDenylisted(string attributeName)
    {
        if (HardDenylistExact.Contains(attributeName))
            return true;

        foreach (var prefix in HardDenylistPrefixes)
        {
            if (attributeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private AttributeLookupResult PerformLookup(string identity, (string username, string password, string domain) creds, List<EditableAttribute> allowlist)
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
        var searchBases = GetConfiguredSearchBases();

        var escaped = ProtectedPrincipalService.EscapeLdapFilter(identity);
        var filter = $"(|(userPrincipalName={escaped})(mail={escaped})(sAMAccountName={escaped})(employeeID={escaped}))";
        var properties = GetPropertiesToLoad(allowlist);

        var allUsers = new List<PSObject>();

        if (searchBases.Length == 0)
        {
            ps.AddCommand("Get-ADUser")
              .AddParameter("LDAPFilter", filter)
              .AddParameter("Properties", properties)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            allUsers.AddRange(ps.Invoke());
            ps.Commands.Clear();
        }
        else
        {
            foreach (var sb in searchBases)
            {
                ps.AddCommand("Get-ADUser")
                  .AddParameter("LDAPFilter", filter)
                  .AddParameter("SearchBase", sb)
                  .AddParameter("Properties", properties)
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "SilentlyContinue");
                try
                {
                    allUsers.AddRange(ps.Invoke());
                }
                catch { }
                ps.Streams.Error.Clear();
                ps.Commands.Clear();
            }
        }

        if (ps.HadErrors)
        {
            var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "AD query failed.";
            ps.Streams.Error.Clear();
            return new(false, errMsg, null, null, false, null, null);
        }

        var users = allUsers
            .GroupBy(u => u.Properties["ObjectGUID"]?.Value?.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (users.Count == 0)
            return new(false, $"User '{identity}' not found.", null, null, false, null, null);

        if (users.Count > 1)
            return new(false, $"Ambiguous: '{identity}' matches {users.Count} AD users. Use a more specific identifier.", null, null, false, null, null);

        var adUser = users[0];
        var dn = adUser.Properties["DistinguishedName"]?.Value?.ToString();

        if (!IsWithinSearchBases(dn, searchBases))
        {
            return new(false, "User is outside the configured search base boundaries.", null, null, true, "Outside allowed OU scope.", null);
        }

        var principal = new ResolvedDirectoryPrincipal(
            Source: "OnPremAD",
            DisplayName: adUser.Properties["DisplayName"]?.Value?.ToString() ?? adUser.Properties["Name"]?.Value?.ToString() ?? identity,
            UserPrincipalName: adUser.Properties["UserPrincipalName"]?.Value?.ToString() ?? "",
            SamAccountName: adUser.Properties["SamAccountName"]?.Value?.ToString(),
            PrimarySmtpAddress: adUser.Properties["mail"]?.Value?.ToString(),
            DistinguishedName: dn,
            ObjectGuid: adUser.Properties["ObjectGUID"]?.Value?.ToString(),
            EntraObjectId: null);

        var currentValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var attr in allowlist)
        {
            var val = adUser.Properties[attr.Name]?.Value;
            currentValues[attr.Name] = val?.ToString();
        }

        return new(true, null, principal, "OnPremAD", false, null, currentValues);
    }

    private AttributeSavePreview ComputePreview(
        ResolvedDirectoryPrincipal target,
        Dictionary<string, string?> proposedValues,
        (string username, string password, string domain) creds,
        List<EditableAttribute> allowlist)
    {
        var currentValues = ReReadCurrentValues(target, creds, allowlist);
        if (currentValues == null)
            return new(false, "Failed to re-read target object for preview.", null);

        var changes = ComputeChanges(proposedValues, currentValues, allowlist);
        return new(true, null, changes);
    }

    private AttributeSaveResult PerformSave(
        ResolvedDirectoryPrincipal target,
        Dictionary<string, string?> proposedValues,
        (string username, string password, string domain) creds,
        List<EditableAttribute> allowlist,
        string performedBy,
        string ip,
        string ticket)
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

        ps.AddCommand("Get-ADUser")
          .AddParameter("Identity", target.DistinguishedName)
          .AddParameter("Properties", GetPropertiesToLoad(allowlist))
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var reReadResult = ps.Invoke();
        ps.Commands.Clear();

        if (reReadResult.Count == 0)
        {
            const string notFoundMsg = "Target object no longer exists or could not be re-read.";
            LogAudit(target, [], performedBy, ip, ticket, false, notFoundMsg);
            return new(false, notFoundMsg, null);
        }

        var reReadUser = reReadResult[0];
        var reReadGuid = reReadUser.Properties["ObjectGUID"]?.Value?.ToString();
        if (!string.IsNullOrEmpty(target.ObjectGuid) && !string.Equals(reReadGuid, target.ObjectGuid, StringComparison.OrdinalIgnoreCase))
        {
            const string mismatchMsg = "Bound-object mismatch: the resolved object no longer matches the lookup snapshot.";
            LogAudit(target, [], performedBy, ip, ticket, false, mismatchMsg);
            return new(false, mismatchMsg, null);
        }

        var reReadDn = reReadUser.Properties["DistinguishedName"]?.Value?.ToString();
        if (!IsWithinSearchBases(reReadDn, GetConfiguredSearchBases()))
        {
            const string boundaryMsg = "Target is outside the configured search base boundaries. The account may have been moved since lookup.";
            LogAudit(target, [], performedBy, ip, ticket, false, boundaryMsg);
            return new(false, boundaryMsg, null);
        }

        var currentValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var attr in allowlist)
            currentValues[attr.Name] = reReadUser.Properties[attr.Name]?.Value?.ToString();

        var changes = ComputeChanges(proposedValues, currentValues, allowlist);
        if (changes.Count == 0)
            return new(true, "No changes to apply.", changes);

        var replace = new Dictionary<string, object?>();
        var clear = new List<string>();

        foreach (var change in changes)
        {
            if (string.IsNullOrEmpty(change.NewValue))
                clear.Add(change.Name);
            else
                replace[change.Name] = change.NewValue;
        }

        // Single Set-ADUser call with both -Replace and -Clear to avoid partial-write risk
        ps.AddCommand("Set-ADUser")
          .AddParameter("Identity", target.DistinguishedName)
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        if (replace.Count > 0) ps.AddParameter("Replace", replace);
        if (clear.Count > 0) ps.AddParameter("Clear", clear.ToArray());

        // -ErrorAction Stop makes real Set-ADUser failures (access denied,
        // constraint violation, unreachable DC) THROW from Invoke() - they never
        // reach the HadErrors branch below. Catch, audit, and return a failed
        // result; a failed mutation with no audit record violates the
        // Constitution's audit invariant.
        try
        {
            ps.Invoke();
        }
        catch (Exception ex)
        {
            var thrownMsg = ex.InnerException?.Message ?? ex.Message;
            LogAudit(target, changes, performedBy, ip, ticket, false, thrownMsg);
            return new(false, thrownMsg, null);
        }
        ps.Commands.Clear();

        if (ps.HadErrors)
        {
            var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Set-ADUser failed.";
            LogAudit(target, changes, performedBy, ip, ticket, false, errMsg);
            return new(false, errMsg, null);
        }

        LogAudit(target, changes, performedBy, ip, ticket, true, null);

        return new(true, null, changes);
    }

    private Dictionary<string, string?>? ReReadCurrentValues(
        ResolvedDirectoryPrincipal target,
        (string username, string password, string domain) creds,
        List<EditableAttribute> allowlist)
    {
        try
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

            ps.AddCommand("Get-ADUser")
              .AddParameter("Identity", target.DistinguishedName)
              .AddParameter("Properties", GetPropertiesToLoad(allowlist))
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var result = ps.Invoke();
            ps.Commands.Clear();

            if (result.Count == 0) return null;

            var reReadDn = result[0].Properties["DistinguishedName"]?.Value?.ToString();
            if (!IsWithinSearchBases(reReadDn, GetConfiguredSearchBases()))
                return null;

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in allowlist)
                values[attr.Name] = result[0].Properties[attr.Name]?.Value?.ToString();
            return values;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to re-read current values for {Target}", target.UserPrincipalName);
            return null;
        }
    }

    private static List<AttributeChange> ComputeChanges(
        Dictionary<string, string?> proposed,
        Dictionary<string, string?> current,
        List<EditableAttribute> allowlist)
    {
        var changes = new List<AttributeChange>();
        foreach (var kv in proposed)
        {
            var attr = allowlist.FirstOrDefault(a => string.Equals(a.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
            if (attr == null) continue;

            current.TryGetValue(kv.Key, out var oldValue);
            var newValue = string.IsNullOrEmpty(kv.Value) ? null : kv.Value;
            var oldNorm = string.IsNullOrEmpty(oldValue) ? null : oldValue;

            if (!string.Equals(oldNorm, newValue, StringComparison.Ordinal))
                changes.Add(new AttributeChange(attr.Name, attr.Label, oldNorm, newValue));
        }
        return changes;
    }

    private string? ValidateProposedValues(Dictionary<string, string?> proposedValues, List<EditableAttribute> allowlist)
    {
        foreach (var kv in proposedValues)
        {
            if (IsDenylisted(kv.Key))
                return $"Attribute '{kv.Key}' is on the hard denylist and cannot be modified.";

            var attr = allowlist.FirstOrDefault(a => string.Equals(a.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
            if (attr == null)
                return $"Attribute '{kv.Key}' is not in the allowlist.";

            var value = kv.Value;

            if (string.IsNullOrEmpty(value))
            {
                if (attr.Required)
                    return $"Attribute '{attr.Label}' is required and cannot be blank.";
                if (!attr.AllowClear)
                    return $"Attribute '{attr.Label}' cannot be cleared.";
                continue;
            }

            if (attr.MaxLength.HasValue && value.Length > attr.MaxLength.Value)
                return $"Attribute '{attr.Label}' exceeds maximum length of {attr.MaxLength.Value}.";

            if (!string.IsNullOrEmpty(attr.Pattern) && !Regex.IsMatch(value, attr.Pattern, RegexOptions.None, RegexTimeout))
                return $"Attribute '{attr.Label}' does not match the required pattern.";

            if (attr.Type == "Choice")
            {
                var effective = GetEffectiveChoices(attr);
                if (effective.Length > 0 && !effective.Contains(value, StringComparer.OrdinalIgnoreCase))
                    return $"Attribute '{attr.Label}' must be one of the configured values.";
            }
        }

        return null;
    }

    private void LogAudit(ResolvedDirectoryPrincipal target, List<AttributeChange> changes, string performedBy, string ip, string ticket, bool success, string? errorDetail)
    {
        var changedAttrs = changes.Select(c => c.Name).ToArray();
        var details = new Dictionary<string, object?>
        {
            ["target"] = target.UserPrincipalName,
            ["changedAttributes"] = changedAttrs,
            ["changes"] = changes.Select(c => new { c.Name, c.OldValue, c.NewValue }).ToArray()
        };

        _operationTrace.Step("AttributeWriteCompleted", success ? "Success" : "Failed", details: details);

        _audit.LogADAttributeEdit(performedBy, ip, target.UserPrincipalName, changes, success, ticket, errorDetail);
    }

    private string[] GetConfiguredSearchBases()
    {
        var config = _moduleConfig.GetValue("ADAttributeEditor", "DefaultSearchBase");
        if (string.IsNullOrWhiteSpace(config))
            return [];
        return config.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsWithinSearchBases(string? dn, string[] searchBases)
    {
        if (searchBases.Length == 0 || string.IsNullOrEmpty(dn))
            return true;
        return searchBases.Any(sb =>
            dn.EndsWith("," + sb, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dn, sb, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] GetPropertiesToLoad(List<EditableAttribute> allowlist)
    {
        var props = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DisplayName", "UserPrincipalName", "SamAccountName",
            "mail", "DistinguishedName", "ObjectGUID", "Name"
        };
        foreach (var attr in allowlist)
            props.Add(attr.Name);
        return props.ToArray();
    }

    private static PSCredential CreateCredential(string username, string password, string domain)
    {
        var fullUsername = username.Contains('\\') || username.Contains('@')
            ? username : $"{domain}\\{username}";
        var securePassword = new System.Security.SecureString();
        foreach (var c in password) securePassword.AppendChar(c);
        return new PSCredential(fullUsername, securePassword);
    }

    private sealed class AttributeAllowlistFile
    {
        public AttributeAllowlistEntry[]? Attributes { get; set; }
    }

    private sealed class AttributeAllowlistEntry
    {
        public string Name { get; set; } = "";
        public string Label { get; set; } = "";
        public string Type { get; set; } = "String";
        public string[]? Choices { get; set; }
        public bool Required { get; set; }
        public bool AllowClear { get; set; }
        public int? MaxLength { get; set; }
        public string? Pattern { get; set; }
        public int Level { get; set; } = 1;
    }
}
