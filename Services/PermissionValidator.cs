using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Management.Automation;

namespace ExchangeAdminWeb.Services;

public class PermissionValidator
{
    private ImmutableHashSet<string> _excludedUsers = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
    private ImmutableDictionary<string, string> _excludedObjectIds = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<PermissionValidator> _logger;
    private readonly IConfiguration _config;
    private readonly ModuleConfigService _moduleConfig;
    private readonly ExoConnectionPool _exoPool;
    private readonly ProtectedPrincipalService _protectedPrincipalService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
    private bool _initialized = false;
    private bool _initFailed = false;
    private DateTime _lastRefresh = DateTime.MinValue;

    public PermissionValidator(IConfiguration config, ModuleConfigService moduleConfig, ExoConnectionPool exoPool, ProtectedPrincipalService protectedPrincipalService, ILogger<PermissionValidator> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _config = config;
        _moduleConfig = moduleConfig;
        _exoPool = exoPool;
        _protectedPrincipalService = protectedPrincipalService;
        _scopeFactory = scopeFactory;

        moduleConfig.ConfigSaved += moduleId =>
        {
            if (moduleId == "MailboxPermissions")
            {
                InvalidateCache();
                _protectedPrincipalService.InvalidateCache();
            }
        };
    }

    private string[] GetConfiguredExclusions()
    {
        var excluded = _moduleConfig.GetValue("MailboxPermissions", "ExcludedUsers");
        if (!string.IsNullOrEmpty(excluded))
        {
            return excluded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var legacy = _config.GetSection("Security:ExcludedUsers").Get<string[]>();
        return legacy ?? Array.Empty<string>();
    }

    private bool GetPreventSelfGrant()
    {
        var val = _moduleConfig.GetValue("MailboxPermissions", "PreventSelfGrant");
        if (!string.IsNullOrEmpty(val) && bool.TryParse(val, out var result))
            return result;

        return bool.Parse(_config["Security:PreventSelfGrant"] ?? "true");
    }

    public void InvalidateCache()
    {
        _lastRefresh = DateTime.MinValue;
    }

    public async Task<bool> IsUserExcludedAsync(string userIdentity)
    {
        await EnsureInitializedAsync();

        if (_excludedUsers.Contains(userIdentity))
            return true;

        if (_excludedUsers.Any(excluded => IdentitiesMatch(excluded, userIdentity)))
            return true;

        if (_excludedObjectIds.Count > 0)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var resolver = scope.ServiceProvider.GetService<IIdentityResolver>();
                if (resolver != null)
                {
                    var targetId = await resolver.ResolveToObjectIdAsync(userIdentity);
                    if (targetId != null && _excludedObjectIds.Values
                            .Any(id => string.Equals(id, targetId, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Identity resolution failed for exclusion check on {Identity}, falling back to string matching", userIdentity);
            }
        }

        return false;
    }

    public async Task<string?> ValidateTargetMailboxAsync(string targetMailbox)
    {
        if (_protectedPrincipalService.HasCentralConfig)
        {
            var (cfg, _, loadError) = _protectedPrincipalService.LoadEffectiveConfig();

            if (loadError != null)
            {
                _logger.LogWarning("Blocking operation on {Target} — protected-principal config load failed: {Reason}", targetMailbox, loadError);
                return $"Access denied: {loadError}";
            }

            bool requiresFullResolution = cfg != null &&
                (cfg.Groups.Length > 0 || cfg.OrganizationalUnits.Length > 0 || cfg.SamAccountNamePatterns.Length > 0
                 || cfg.Users.Any(u => !u.Contains('@') && !u.Contains('\\')));

            ResolvedDirectoryPrincipal principal;

            if (requiresFullResolution)
            {
                var resolved = await _protectedPrincipalService.ResolveDirectoryPrincipalAsync(targetMailbox);
                if (resolved == null)
                {
                    _logger.LogWarning(
                        "Blocking operation on {Target} — cannot resolve full identity but Group/OU/Pattern rules are configured",
                        targetMailbox);
                    return "Access denied: Protected-principal identity resolution is unavailable. Contact your administrator.";
                }
                principal = resolved;
            }
            else
            {
                principal = new ResolvedDirectoryPrincipal(
                    Source: "PermissionValidator",
                    DisplayName: targetMailbox,
                    UserPrincipalName: targetMailbox,
                    SamAccountName: null,
                    PrimarySmtpAddress: targetMailbox.Contains('@') ? targetMailbox : null,
                    DistinguishedName: null,
                    ObjectGuid: null,
                    EntraObjectId: null);
            }

            var result = await _protectedPrincipalService.CheckAsync(principal);
            if (result.CheckFailed)
            {
                _logger.LogWarning("Blocking operation on {Target} — protected-principal check failed: {Reason}", targetMailbox, result.Reason);
                return $"Access denied: {result.Reason}";
            }
            if (result.IsProtected)
            {
                _logger.LogWarning("Attempted operation on protected principal: {Target} (rules: {Rules})", targetMailbox, string.Join(", ", result.MatchedRules));
                return $"Access denied: {targetMailbox} is protected and cannot be modified through this interface.";
            }
        }

        await EnsureInitializedAsync();

        if (_initFailed)
        {
            _logger.LogWarning("Blocking operation on {Target} — protected-user list failed to load", targetMailbox);
            return "Access denied: Protected-user list is unavailable. Contact your administrator.";
        }

        if (await IsUserExcludedAsync(targetMailbox))
        {
            _logger.LogWarning("Attempted operation on excluded user: {Target}", targetMailbox);
            return $"Access denied: {targetMailbox} is protected and cannot be modified through this interface.";
        }

        return null;
    }

    public string? ValidateSelfGrant(string currentUser, string affectedUser)
    {
        if (!GetPreventSelfGrant())
            return null;

        if (IdentitiesMatch(currentUser, affectedUser))
        {
            _logger.LogWarning("User {User} attempted to grant permissions to themselves ({Affected})", currentUser, affectedUser);
            return "Access denied: You cannot grant permissions to yourself.";
        }

        return null;
    }

    public async Task<string?> ValidateSelfGrantAsync(string currentUser, string affectedUser)
    {
        if (!GetPreventSelfGrant())
            return null;

        if (IdentitiesMatch(currentUser, affectedUser))
        {
            _logger.LogWarning("User {User} attempted to grant permissions to themselves ({Affected})", currentUser, affectedUser);
            return "Access denied: You cannot grant permissions to yourself.";
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var resolver = scope.ServiceProvider.GetService<IIdentityResolver>();
            if (resolver != null)
            {
                var currentId = await resolver.ResolveToObjectIdAsync(currentUser);
                var affectedId = await resolver.ResolveToObjectIdAsync(affectedUser);

                if (currentId != null && affectedId != null &&
                    string.Equals(currentId, affectedId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("User {User} attempted to grant permissions to themselves ({Affected}) — resolved via ObjectId", currentUser, affectedUser);
                    return "Access denied: You cannot grant permissions to yourself.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Identity resolution failed for self-grant check, falling back to string matching");
        }

        return null;
    }

    public static bool IdentitiesMatch(string identity1, string identity2)
    {
        var names1 = GetNormalizedNames(identity1);
        var names2 = GetNormalizedNames(identity2);
        return names1.Overlaps(names2);
    }

    private static HashSet<string> GetNormalizedNames(string identity)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var username = ExtractUsername(identity);
        names.Add(username);
        names.Add(username.Replace(".", ""));
        if (identity.Contains('@'))
            names.Add(identity.Trim().ToLowerInvariant());
        return names;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized && DateTime.UtcNow - _lastRefresh < CacheLifetime) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized && DateTime.UtcNow - _lastRefresh < CacheLifetime) return;

            if (_moduleConfig.HasConfigFile && _moduleConfig.IsCorrupt)
            {
                _excludedUsers = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
                _excludedObjectIds = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
                _initFailed = true;
                _initialized = true;
                _lastRefresh = DateTime.UtcNow;
                _logger.LogError("Module config file is corrupt — blocking all protected-target operations until file is fixed");
                return;
            }

            var configuredExclusions = GetConfiguredExclusions();
            _logger.LogInformation("Initializing permission validator with {Count} configured exclusions", configuredExclusions.Length);

            var newExcluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in configuredExclusions)
            {
                var trimmed = entry.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                newExcluded.Add(trimmed);

                var members = await TryExpandGroupAsync(trimmed);
                foreach (var member in members)
                {
                    newExcluded.Add(member);
                    _logger.LogDebug("Excluded (from group {Group}): {Member}", trimmed, member);
                }
            }

            var newObjectIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var resolver = scope.ServiceProvider.GetService<IIdentityResolver>();
                if (resolver != null)
                {
                    foreach (var user in newExcluded)
                    {
                        var objectId = await resolver.ResolveToObjectIdAsync(user);
                        if (objectId != null)
                            newObjectIds[user] = objectId;
                    }
                    _logger.LogInformation("Resolved {Count}/{Total} excluded identities to ObjectIds",
                        newObjectIds.Count, newExcluded.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve some excluded-user ObjectIds — string matching will be used as fallback");
            }

            _excludedUsers = newExcluded.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            _excludedObjectIds = newObjectIds.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
            _initFailed = false;
            _initialized = true;
            _lastRefresh = DateTime.UtcNow;
            _logger.LogInformation("Permission validator initialized with {Total} total excluded identities", _excludedUsers.Count);
        }
        catch (Exception ex)
        {
            _excludedUsers = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
            _excludedObjectIds = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
            _initFailed = true;
            _initialized = true;
            _lastRefresh = DateTime.UtcNow;
            _logger.LogError(ex, "Failed to initialize permission validator — all operations on protected targets will be blocked until app pool recycle.");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<List<string>> TryExpandGroupAsync(string identity)
    {
        var members = new List<string>();

        if (!_exoPool.IsConfigured)
        {
            _logger.LogWarning("Exchange Online not configured, cannot expand groups");
            return members;
        }

        var pooled = await _exoPool.BorrowAsync();
        try
        {
            var ps = pooled.PowerShell;

            ps.AddCommand("Get-Recipient")
              .AddParameter("Identity", identity)
              .AddParameter("ErrorAction", "Stop");

            Collection<PSObject> recipients;
            try
            {
                recipients = ps.Invoke();
                ps.Commands.Clear();
            }
            catch (Exception ex) when (ex.Message.Contains("couldn't be found"))
            {
                ps.Commands.Clear();
                _logger.LogInformation("Excluded entry '{Identity}' not found in EXO — kept as literal match", identity);
                _exoPool.Return(pooled);
                return members;
            }

            if (recipients.Count == 0)
            {
                _exoPool.Return(pooled);
                return members;
            }

            var recipient = recipients[0];
            var recipientType = recipient.Properties["RecipientTypeDetails"]?.Value?.ToString();

            if (recipientType?.Contains("Group", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogInformation("Expanding group: {Group} (type: {Type})", identity, recipientType);

                ps.AddCommand("Get-DistributionGroupMember")
                  .AddParameter("Identity", identity)
                  .AddParameter("ResultSize", "Unlimited")
                  .AddParameter("ErrorAction", "Stop");

                var groupMembers = ps.Invoke();
                ps.Commands.Clear();

                foreach (var member in groupMembers)
                {
                    var email = member.Properties["PrimarySmtpAddress"]?.Value?.ToString();
                    var upn = member.Properties["UserPrincipalName"]?.Value?.ToString();
                    var sam = member.Properties["SamAccountName"]?.Value?.ToString();

                    if (!string.IsNullOrWhiteSpace(email)) members.Add(email);
                    if (!string.IsNullOrWhiteSpace(upn) && upn != email) members.Add(upn);
                    if (!string.IsNullOrWhiteSpace(sam)) members.Add(sam);
                }

                _logger.LogInformation("Expanded group {Group} to {Count} members", identity, members.Count);
            }

            _exoPool.Return(pooled);
        }
        catch (Exception ex)
        {
            _exoPool.Discard(pooled);
            _logger.LogWarning(ex, "Failed to expand group '{Identity}' via EXO pool", identity);
            throw;
        }

        return members;
    }

    private static string ExtractUsername(string identity)
    {
        if (identity.Contains('\\'))
            return identity.Split('\\')[1];
        if (identity.Contains('@'))
            return identity.Split('@')[0];
        return identity;
    }
}
