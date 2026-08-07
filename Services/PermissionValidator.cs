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
    private readonly ProtectedPrincipalServicerService _servicers;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
    private bool _initialized = false;
    private bool _initFailed = false;
    private DateTime _lastRefresh = DateTime.MinValue;

    public PermissionValidator(IConfiguration config, ModuleConfigService moduleConfig, ExoConnectionPool exoPool, ProtectedPrincipalService protectedPrincipalService, ProtectedPrincipalServicerService servicers, ILogger<PermissionValidator> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _config = config;
        _moduleConfig = moduleConfig;
        _exoPool = exoPool;
        _protectedPrincipalService = protectedPrincipalService;
        _servicers = servicers;
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

    // Exclusions come only from the MailboxPermissions/ExcludedUsers module config.
    // The appsettings Security:ExcludedUsers fallback was retired 2026-07-28: it was
    // invisible to the Protected Principals admin UI, so principals could be blocked
    // with no visible cause.
    private string[] GetConfiguredExclusions()
    {
        var excluded = _moduleConfig.GetValue("MailboxPermissions", "ExcludedUsers");
        if (!string.IsNullOrEmpty(excluded))
        {
            return excluded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return Array.Empty<string>();
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

    /// <summary>
    /// The protected-principal verdict for a target: an error message to refuse with, or null to
    /// proceed - plus the servicer note when an authorised operator overrode a refusal.
    /// </summary>
    /// <remarks>
    /// A record rather than a bare string so a caller cannot take the allow and drop the reason.
    /// <see cref="ServicedNote"/> is non-null only when the target WAS protected and this operator
    /// is an authorised servicer, and it must reach the audit event's <c>extra</c> - the action
    /// succeeds, so the error channel would discard it.
    /// </remarks>
    public sealed record TargetValidation(string? Error, string? ServicedNote)
    {
        public static readonly TargetValidation Ok = new(null, null);
        public static TargetValidation Deny(string error) => new(error, null);
        public static TargetValidation Serviced(string note) => new(null, note);
    }

    /// <summary>
    /// Back-compat overload for callers with no servicer concept. Never services: it passes no
    /// principal, and a null principal refuses.
    /// </summary>
    public async Task<string?> ValidateTargetMailboxAsync(string targetMailbox)
        => (await ValidateTargetMailboxAsync(targetMailbox, actingUser: null, moduleId: null)).Error;

    /// <param name="actingUser">The operator, for the servicer decision. Null refuses.</param>
    /// <param name="moduleId">
    /// The MODULE the caller is acting as. Required alongside a principal and never defaulted:
    /// this one method serves Mailbox Permissions, Calendar and Out of Office, and a shared or
    /// borrowed id would let a grant in one authorise the others - collapsing the per-module
    /// boundary the whole design rests on.
    /// </param>
    public async Task<TargetValidation> ValidateTargetMailboxAsync(
        string targetMailbox,
        System.Security.Claims.ClaimsPrincipal? actingUser,
        string? moduleId)
    {
        if (_protectedPrincipalService.HasCentralConfig)
        {
            var (cfg, _, loadError) = _protectedPrincipalService.LoadEffectiveConfig();

            if (loadError != null)
            {
                _logger.LogWarning("Blocking operation on {Target} - protected-principal config load failed: {Reason}", targetMailbox, loadError);
                return TargetValidation.Deny($"Access denied: {loadError}");
            }

            bool requiresFullResolution = cfg != null &&
                (cfg.Groups.Length > 0 || cfg.OrganizationalUnits.Length > 0 || cfg.SamAccountNamePatterns.Length > 0
                 || cfg.Users.Any(u => !u.Contains('@') && !u.Contains('\\')));

            ResolvedDirectoryPrincipal principal;

            if (requiresFullResolution)
            {
                var (resolved, status) = await _protectedPrincipalService.ResolveWithExchangeFallbackAsync(targetMailbox);

                if (status == ProtectedPrincipalService.ResolutionStatus.Ambiguous)
                {
                    _logger.LogWarning(
                        "Blocking operation on {Target} - identity is ambiguous in Active Directory",
                        targetMailbox);
                    return TargetValidation.Deny("Access denied: This identity is ambiguous - it matches multiple directory objects. Contact your administrator.");
                }

                if (status == ProtectedPrincipalService.ResolutionStatus.Unavailable)
                {
                    _logger.LogWarning(
                        "Blocking operation on {Target} - directory unreachable and Group/OU/Pattern rules are configured",
                        targetMailbox);
                    return TargetValidation.Deny("Access denied: Protected-principal identity resolution is unavailable. Contact your administrator.");
                }

                if (status == ProtectedPrincipalService.ResolutionStatus.NotFound)
                {
                    // Both directories answered and neither has this recipient. Naming that is the
                    // point of the change: the old blanket message read like an outage and sent
                    // support chasing the wrong problem when the real cause was a bad address.
                    _logger.LogWarning(
                        "Blocking operation on {Target} - not found in Active Directory or Exchange Online",
                        targetMailbox);
                    return TargetValidation.Deny($"Access denied: '{targetMailbox}' was not found in Active Directory or Exchange Online. Check the address.");
                }

                principal = resolved!;
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
                _logger.LogWarning("Blocking operation on {Target} - protected-principal check failed: {Reason}", targetMailbox, result.Reason);
                return TargetValidation.Deny($"Access denied: {result.Reason}");
            }
            if (result.IsProtected)
            {
                // An authorised servicer for THIS module may proceed. moduleId is passed by the
                // caller and never defaulted, so a Mailbox grant cannot authorise Calendar or
                // Out of Office.
                var note = ServicerNote(actingUser, moduleId, result.MatchedRules);
                if (note is not null)
                    return TargetValidation.Serviced(note);

                _logger.LogWarning("Attempted operation on protected principal: {Target} (rules: {Rules})", targetMailbox, string.Join(", ", result.MatchedRules));
                return TargetValidation.Deny($"Access denied: {targetMailbox} is protected and cannot be modified through this interface.");
            }
        }

        await EnsureInitializedAsync();

        if (_initFailed)
        {
            _logger.LogWarning("Blocking operation on {Target} - protected-user list failed to load", targetMailbox);
            return TargetValidation.Deny("Access denied: Protected-user list is unavailable. Contact your administrator.");
        }

        if (await IsUserExcludedAsync(targetMailbox))
        {
            // The legacy excluded-user list. It protects the same principals by a different
            // mechanism, so a servicer grant must apply here too - otherwise whether an operator
            // can service a VIP would depend on WHICH list happens to name them, which is
            // invisible from the UI and impossible to reason about.
            var note = ServicerNote(actingUser, moduleId, [$"excluded-user list: {targetMailbox}"]);
            if (note is not null)
                return TargetValidation.Serviced(note);

            _logger.LogWarning("Attempted operation on excluded user: {Target}", targetMailbox);
            return TargetValidation.Deny($"Access denied: {targetMailbox} is protected and cannot be modified through this interface.");
        }

        return TargetValidation.Ok;
    }

    /// <summary>
    /// The servicer note for this operator in this module, or null to refuse.
    /// </summary>
    /// <remarks>
    /// Refuses outright when <paramref name="moduleId"/> is absent. That is what makes the
    /// back-compat overload safe: it supplies neither principal nor module, so it can never
    /// service - and a future caller that forgets the module id gets the refusing behaviour rather
    /// than a borrowed grant.
    /// </remarks>
    private string? ServicerNote(
        System.Security.Claims.ClaimsPrincipal? actingUser,
        string? moduleId,
        IEnumerable<string> matchedRules)
    {
        if (actingUser is null || string.IsNullOrWhiteSpace(moduleId))
            return null;

        return ProtectedPrincipalServicing.NoteFor(_servicers, actingUser, moduleId, matchedRules);
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
                    _logger.LogWarning("User {User} attempted to grant permissions to themselves ({Affected}) - resolved via ObjectId", currentUser, affectedUser);
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

            if (_moduleConfig.HasModuleConfigFile("MailboxPermissions") && _moduleConfig.IsModuleCorrupt("MailboxPermissions"))
            {
                _excludedUsers = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
                _excludedObjectIds = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
                _initFailed = true;
                _initialized = true;
                _lastRefresh = DateTime.UtcNow;
                _logger.LogError("Module config file is corrupt - blocking all protected-target operations until file is fixed");
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
                _logger.LogWarning(ex, "Failed to resolve some excluded-user ObjectIds - string matching will be used as fallback");
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
            _logger.LogError(ex, "Failed to initialize permission validator - all operations on protected targets will be blocked until app pool recycle.");
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
            _logger.LogWarning("Exchange Online not configured - group '{Identity}' kept as literal match only; group members are not individually protected", identity);
            return members;
        }

        // Read-only (Get-Recipient + Get-DistributionGroupMember): retry-eligible. The pool helper
        // owns borrow/return/discard and the one-shot retry on a dead session; on a NON-connection
        // throw it discards (this delegate uses raw ps.Invoke() and can't guarantee a clean
        // pipeline, matching the prior manual Discard). The "couldn't be found = keep as literal
        // match" rule stays here as a normal (non-connection) success.
        return await _exoPool.RunWithRetryAsync(pooled => Task.Run(() =>
        {
            var found = new List<string>();
            var ps = pooled.PowerShell;

            ps.AddCommand("Get-Recipient")
              .AddParameter("Identity", identity)
              .AddParameter("ErrorAction", "Stop");

            Collection<PSObject> recipients;
            try
            {
                recipients = ps.Invoke();
                if (ps.HadErrors)
                {
                    var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Unknown EXO error";
                    ps.Streams.Error.Clear();
                    ps.Commands.Clear();
                    throw new InvalidOperationException($"EXO error resolving '{identity}': {errMsg}");
                }
                ps.Commands.Clear();
            }
            catch (Exception ex) when (ex.Message.Contains("couldn't be found"))
            {
                ps.Commands.Clear();
                _logger.LogInformation("Excluded entry '{Identity}' not found in EXO - kept as literal match", identity);
                return new PooledOutcome<List<string>>(found, false);
            }

            if (recipients.Count == 0)
                return new PooledOutcome<List<string>>(found, false);

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
                if (ps.HadErrors)
                {
                    var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Unknown EXO error";
                    ps.Streams.Error.Clear();
                    ps.Commands.Clear();
                    throw new InvalidOperationException($"EXO error expanding group '{identity}': {errMsg}");
                }
                ps.Commands.Clear();

                foreach (var member in groupMembers)
                {
                    var email = member.Properties["PrimarySmtpAddress"]?.Value?.ToString();
                    var upn = member.Properties["UserPrincipalName"]?.Value?.ToString();
                    var sam = member.Properties["SamAccountName"]?.Value?.ToString();

                    if (!string.IsNullOrWhiteSpace(email)) found.Add(email);
                    if (!string.IsNullOrWhiteSpace(upn) && upn != email) found.Add(upn);
                    if (!string.IsNullOrWhiteSpace(sam)) found.Add(sam);
                }

                _logger.LogInformation("Expanded group {Group} to {Count} members", identity, found.Count);
            }

            return new PooledOutcome<List<string>>(found, false);
        }), allowRetry: true, PoolFailurePolicy.Discard);
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
