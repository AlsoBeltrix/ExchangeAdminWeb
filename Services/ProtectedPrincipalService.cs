using System.Collections.Immutable;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services.Storage;

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

    /// <summary>
    /// Protected write TARGETS (docs/ProtectedGroupWriteTarget-Plan.md T0): groups protected AS
    /// OBJECTS BEING WRITTEN INTO. A separate rule set from the four principal lists - consulted
    /// only by <see cref="ProtectedPrincipalService.CheckWriteTarget"/>, never by
    /// <see cref="ProtectedPrincipalService.CheckAsync"/>. Entry format: "objectGUID|DN".
    /// </summary>
    public string[] GroupTargets { get; set; } = [];
}

/// <summary>
/// One parsed Protected Targets entry. Stored as <c>objectGUID|DN</c>: the GUID is the immutable
/// identity (a rename or move cannot silently un-protect the group), the DN doubles as the
/// display label and as a fallback matcher. Pure so the matching rule is unit-testable.
/// </summary>
public sealed record ProtectedGroupTargetEntry(string? ObjectGuid, string? DistinguishedName)
{
    /// <summary>Safe separator: a GUID can never contain it, so the first one is unambiguous.</summary>
    public const char Separator = '|';

    public static string Format(string objectGuid, string distinguishedName)
        => $"{objectGuid}{Separator}{distinguishedName}";

    /// <summary>
    /// Parses a stored value. Without a separator, the value is matched as whichever identifier
    /// it parses to (GUID or DN), so a hand-repaired or partial row still protects rather than
    /// silently matching nothing.
    /// </summary>
    public static ProtectedGroupTargetEntry Parse(string? stored)
    {
        var v = (stored ?? string.Empty).Trim();
        if (v.Length == 0)
            return new(null, null);

        var sep = v.IndexOf(Separator);
        if (sep < 0)
            return Guid.TryParse(v, out _) ? new(v, null) : new(null, v);

        var guid = v[..sep].Trim();
        var dn = v[(sep + 1)..].Trim();
        return new(guid.Length == 0 ? null : guid, dn.Length == 0 ? null : dn);
    }

    /// <summary>Display label: the DN where present, else the GUID.</summary>
    public string Label => DistinguishedName ?? ObjectGuid ?? string.Empty;

    /// <summary>GUID first (immutable), DN as fallback. An empty identifier never matches.</summary>
    public bool Matches(ResolvedDirectoryPrincipal target)
    {
        if (!string.IsNullOrEmpty(ObjectGuid) && !string.IsNullOrEmpty(target.ObjectGuid) &&
            string.Equals(ObjectGuid, target.ObjectGuid, StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrEmpty(DistinguishedName) && !string.IsNullOrEmpty(target.DistinguishedName) &&
               string.Equals(DistinguishedName, target.DistinguishedName, StringComparison.OrdinalIgnoreCase);
    }
}

public class ProtectedPrincipalService
{
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ModuleConfigService _moduleConfig;
    private readonly ProtectedPrincipalRepository _repository;
    private readonly DelineaService _delineaService;
    private readonly ILogger<ProtectedPrincipalService> _logger;

    // This service is a singleton; IIdentityResolver is scoped (Program.cs:167, :173), so the
    // Exchange fallback has to open its own scope per lookup. Optional because nine test files
    // construct this service directly; a null factory makes the fallback fail closed
    // (Unavailable), never NotFound. See ResolveWithExchangeFallbackAsync.
    private readonly IServiceScopeFactory? _scopeFactory;

    private readonly object _cacheLock = new();

    private ProtectedPrincipalConfig? _cachedConfig;
    private DateTime _configLoadedAt = DateTime.MinValue;
    private bool _configCorrupt;

    // Set when a legacy protected-principals.json exists but is unparseable / lacks the
    // ProtectedPrincipals node, and the DB store is not yet configured. Like the section-access
    // store, a corrupt protection list must keep the store fail-closed during the upgrade window
    // rather than silently un-protect principals. The corrupt file stays on disk, so this
    // re-trips every startup until repaired/removed.
    private readonly bool _legacyFileCorrupt;

    private static readonly TimeSpan ConfigCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CredentialFailureTtl = TimeSpan.FromSeconds(60);

    private DateTime _lastCredentialFailure = DateTime.MinValue;
    private static readonly SemaphoreSlim _adThrottle = new(2, 2);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public ProtectedPrincipalService(
        IWebHostEnvironment env,
        IConfiguration config,
        ModuleConfigService moduleConfig,
        ProtectedPrincipalRepository repository,
        DelineaService delineaService,
        ILogger<ProtectedPrincipalService> logger,
        IServiceScopeFactory? scopeFactory = null)
    {
        _env = env;
        _config = config;
        _moduleConfig = moduleConfig;
        _repository = repository;
        _delineaService = delineaService;
        _logger = logger;
        _scopeFactory = scopeFactory;

        var legacyPath = Path.Combine(env.ContentRootPath, "config", "protected-principals.json");
        _legacyFileCorrupt = ImportLegacyIfPresent(legacyPath);
    }

    public const string DirectoryReadSecretConfigKey = "DirectoryReadSecretId";
    public const string ProtectedPrincipalsModuleKey = "ProtectedPrincipals";

    // True if a protected-principals config has been saved (presence marker). An unparseable
    // legacy file also counts as "has config" so PermissionValidator routes through the
    // (fail-closed) load path rather than skipping protection entirely. A DB-integrity failure
    // ALSO counts as "has config" (true) so the caller routes into LoadEffectiveConfig, which
    // returns the controlled fail-closed error - never let this probe throw through to a 500.
    // virtual for the same reason as CheckAsync below: PermissionValidator branches on this, and
    // its deny paths are unreachable in a test without a seam here. No behavior change.
    public virtual bool HasCentralConfig
    {
        get
        {
            if (_legacyFileCorrupt)
                return true;
            // TryRead never throws; if the store is unreadable it returns false with
            // configured=false, but we must still route to the fail-closed load path, so treat
            // an unreadable store as "has config".
            if (!_repository.TryRead(out _, out var configured))
                return true;
            return configured;
        }
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedConfig = null;
            _configLoadedAt = DateTime.MinValue;
        }
    }

    // virtual: a test seam so the bulk job processor's protected-principal gate can be exercised
    // without a live AD/Delinea backend (no behavior change). Mirrors the EmailService seam pattern.
    public virtual async Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
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

    /// <summary>
    /// May this GROUP be WRITTEN TO (members added or removed)? A separate question from
    /// CheckAsync's "is this principal protected" (docs/ProtectedGroupWriteTarget-Plan.md T0/T1).
    /// </summary>
    /// <remarks>
    /// Rule set: Users (direct identity), SamAccountNamePatterns, OrganizationalUnits, and the
    /// Protected Targets list. The <c>Groups</c> rule is deliberately EXCLUDED: that list means
    /// "everyone inside this group is protected", and evaluating it here would make every listed
    /// group unmanageable the moment the build deploys - the plan's AC8 anti-lockout criterion
    /// and T0's no-reinterpretation rule (plan Revision 2026-08-28). The legacy MailboxPermissions
    /// ExcludedUsers list is not evaluated either: it is a module-scoped user list, not one of
    /// the four rule kinds.
    ///
    /// Synchronous and directory-free by construction: every rule evaluated here keys on the
    /// resolved snapshot the caller supplies (pgwt-2: a FULL snapshot, never a bare DN), so an
    /// unavailable directory cannot turn this check into a silent allow. Fails closed on a
    /// config load error (AC5). virtual as a test seam, like CheckAsync.
    /// </remarks>
    public virtual ProtectedPrincipalResult CheckWriteTarget(ResolvedDirectoryPrincipal target)
    {
        var (cfg, _, loadError) = LoadEffectiveConfig();

        if (loadError != null)
            return ProtectedPrincipalResult.Failed(loadError);

        var matchedRules = new List<string>();

        if (cfg != null)
        {
            CheckDirectUserMatches(cfg, target, matchedRules);
            CheckPatternMatches(cfg, target, matchedRules);
            CheckOuMatches(cfg, target, matchedRules);
            CheckGroupTargetMatches(cfg, target, matchedRules);
        }

        if (matchedRules.Count > 0)
            return ProtectedPrincipalResult.Protected(
                "Target group is protected against membership changes.",
                matchedRules.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

        return ProtectedPrincipalResult.NotProtected();
    }

    private static void CheckGroupTargetMatches(ProtectedPrincipalConfig cfg, ResolvedDirectoryPrincipal target, List<string> matchedRules)
    {
        foreach (var stored in cfg.GroupTargets)
        {
            var entry = ProtectedGroupTargetEntry.Parse(stored);
            if (entry.Matches(target))
                matchedRules.Add($"Target:{entry.Label}");
        }
    }

    // virtual: same test seam as HasCentralConfig / CheckAsync. PermissionValidator's fail-closed
    // deny on a config load error cannot be reached in a test otherwise. No behavior change.
    public virtual (ProtectedPrincipalConfig? config, string[] legacyExclusions, string? error) LoadEffectiveConfig()
    {
        // The legacy ExcludedUsers protection list lives in the MailboxPermissions
        // module config. If that file exists but is corrupt, silently reading it as
        // empty would un-protect those principals - fail closed instead, the same
        // way PermissionValidator blocks on the same corruption.
        if (_moduleConfig.IsModuleCorrupt("MailboxPermissions"))
            return (null, [], "MailboxPermissions module configuration is corrupt - protected-principal exclusions unavailable. Contact your administrator.");

        // FAIL-CLOSED: an unparseable legacy file still on disk keeps the store corrupt during
        // the upgrade window rather than silently un-protecting principals.
        if (_legacyFileCorrupt)
            return (null, [], "Protected-principals configuration is corrupt. Contact your administrator.");

        lock (_cacheLock)
        {
            if (_cachedConfig != null && DateTime.UtcNow - _configLoadedAt < ConfigCacheTtl && !_configCorrupt)
                return (_cachedConfig, GetLegacyExclusions(), null);
        }

        // Read the four lists + configured flag in one guarded operation. A read failure (DB
        // integrity / partial schema damage) fails closed, never silently empty.
        if (!_repository.TryRead(out var data, out var configured))
        {
            _logger.LogError("Protected-principals store unreadable - failing closed");
            lock (_cacheLock) { _configCorrupt = true; }
            return (null, [], "Protected-principals configuration is corrupt. Contact your administrator.");
        }

        ProtectedPrincipalConfig? config = configured
            ? new ProtectedPrincipalConfig
            {
                Users = data.Users,
                Groups = data.Groups,
                OrganizationalUnits = data.OrganizationalUnits,
                SamAccountNamePatterns = data.SamAccountNamePatterns,
                GroupTargets = data.GroupTargets,
            }
            : null;

        lock (_cacheLock)
        {
            _cachedConfig = config;
            _configLoadedAt = DateTime.UtcNow;
            _configCorrupt = false;
        }

        return (config, GetLegacyExclusions(), null);
    }

    public enum ResolutionStatus { Resolved, NotFound, Ambiguous, Unavailable }

    /// <summary>
    /// Resolves an identity to a full ResolvedDirectoryPrincipal with explicit status.
    /// NotFound = AD lookup succeeded but found no match (safe for cloud-only fallback).
    /// Unavailable = resolver could not run (credential missing, throttle timeout, error).
    /// </summary>
    // virtual: test seam (see CheckAsync). Lets the processor tests force Resolved/NotFound/
    // Unavailable/Ambiguous outcomes without a live directory.
    public virtual async Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithStatusAsync(string identity)
    {
        if (DateTime.UtcNow - _lastCredentialFailure < CredentialFailureTtl)
        {
            _logger.LogDebug("Skipping principal resolution - credential recently failed");
            return (null, ResolutionStatus.Unavailable);
        }

        var secretId = GetDirectoryReadSecretId();
        if (secretId == null)
        {
            _logger.LogDebug("Cannot resolve principal - directory-read credential not configured");
            return (null, ResolutionStatus.Unavailable);
        }

        var creds = await _delineaService.GetCredentialsBySecretIdAsync(secretId.Value);
        if (creds == null)
        {
            _lastCredentialFailure = DateTime.UtcNow;
            _logger.LogWarning("Failed to retrieve directory-read credential for principal resolution");
            return (null, ResolutionStatus.Unavailable);
        }

        try
        {
            if (!await _adThrottle.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                _logger.LogWarning("AD throttle timeout during principal resolution for {Identity}", identity);
                return (null, ResolutionStatus.Unavailable);
            }

            try
            {
                var result = await Task.Run(() => ResolveViaActiveDirectory(identity, creds.Value.username, creds.Value.password, creds.Value.domain));
                return result != null ? (result, ResolutionStatus.Resolved) : (null, ResolutionStatus.NotFound);
            }
            finally
            {
                _adThrottle.Release();
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Ambiguous", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "Ambiguous resolution for {Identity} - blocking", identity);
            return (null, ResolutionStatus.Ambiguous);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve directory principal for {Identity}", identity);
            return (null, ResolutionStatus.Unavailable);
        }
    }

    /// <summary>
    /// Legacy wrapper - returns null for both NotFound and Unavailable.
    /// Prefer ResolveWithStatusAsync for new code.
    /// </summary>
    public async Task<ResolvedDirectoryPrincipal?> ResolveDirectoryPrincipalAsync(string identity)
    {
        var (principal, _) = await ResolveWithStatusAsync(identity);
        return principal;
    }

    /// <summary>
    /// Resolves an identity through Active Directory, falling back to Exchange Online when AD
    /// answers "no such object". This is the entry point protection gates should use.
    /// </summary>
    /// <remarks>
    /// AD alone cannot see three legitimate kinds of target: a mailbox addressed by a secondary
    /// SMTP alias, a mail-enabled group, and a cloud-only recipient. The first is a protection
    /// bypass, not just a usability gap - protected rows are stored as primary addresses, so an
    /// alias-addressed protected principal does not match. Exchange normalizes any alias to the
    /// canonical primary address, so re-resolving through that address is what closes it.
    ///
    /// Fail-closed rules, in order of importance:
    /// - Only <c>NotFound</c> falls through to Exchange. Resolved / Ambiguous / Unavailable are
    ///   returned exactly as AD produced them, so no path that denies today starts allowing.
    /// - An Exchange lookup that could not run returns <c>Unavailable</c>, never <c>NotFound</c>.
    ///   An unreachable directory is not evidence of absence.
    /// See docs/ProtectedPrincipalResolution-Plan.md.
    /// </remarks>
    // virtual: same test seam as ResolveWithStatusAsync - gate tests substitute outcomes without
    // a live directory.
    public virtual async Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity)
    {
        var (principal, status) = await ResolveWithStatusAsync(identity);
        if (status != ResolutionStatus.NotFound)
            return (principal, status);

        if (_scopeFactory == null)
        {
            _logger.LogWarning(
                "Cannot attempt Exchange fallback for {Identity} - no service scope factory. Failing closed.",
                identity);
            return (null, ResolutionStatus.Unavailable);
        }

        ResolvedRecipient? recipient;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var resolver = scope.ServiceProvider.GetService<IIdentityResolver>();
            if (resolver == null)
            {
                _logger.LogWarning(
                    "Cannot attempt Exchange fallback for {Identity} - no IIdentityResolver registered. Failing closed.",
                    identity);
                return (null, ResolutionStatus.Unavailable);
            }

            recipient = await resolver.ResolveRecipientAsync(identity);
        }
        catch (Exception ex)
        {
            // The lookup did not run. Reporting NotFound here would let an EXO outage present as
            // an affirmative absence, and callers that allow on NotFound would un-protect the
            // principal. Deny instead.
            _logger.LogWarning(ex, "Exchange fallback lookup failed for {Identity} - failing closed", identity);
            return (null, ResolutionStatus.Unavailable);
        }

        if (recipient == null)
        {
            // Both directories answered and neither has this recipient. Now the absence is
            // affirmative, which is what NotFound is allowed to mean.
            _logger.LogInformation(
                "{Identity} not found in Active Directory or Exchange Online", identity);
            return (null, ResolutionStatus.NotFound);
        }

        // Branch on whether the recipient has an on-premises object, NOT on whether the typed
        // address happened to differ from the canonical one. Those are independent: a cloud-only
        // mailbox can have secondary aliases too. Testing address equality first sent that
        // intersection down the alias path, where the AD re-query necessarily returned NotFound
        // (a cloud-only object is in AD under no address), which the gates that allow on NotFound
        // then read as "not protected" - reinstating the alias bypass for exactly the principals
        // this method exists to protect. Review finding ppv-1.
        if (recipient.ExistsOnPrem)
        {
            if (!string.Equals(recipient.PrimarySmtpAddress, identity, StringComparison.OrdinalIgnoreCase))
            {
                // The alias case. Re-resolve against the canonical address so the principal
                // carries a DN and the group / OU / pattern rules apply normally.
                _logger.LogInformation(
                    "Exchange resolved {Identity} to canonical address {Canonical} - re-resolving in AD",
                    identity, recipient.PrimarySmtpAddress);
                return await ResolveWithStatusAsync(recipient.PrimarySmtpAddress);
            }

            // Exchange says this recipient IS on-prem-backed, yet AD just missed it under the very
            // same address. The two directories disagree, so nothing here is established: treat it
            // as an unavailable lookup rather than inventing a cloud-only principal that would
            // skip the group and OU rules this synced object is subject to.
            _logger.LogWarning(
                "Exchange reports {Identity} as directory-synced but Active Directory did not "
                + "resolve it - directories disagree, failing closed",
                identity);
            return (null, ResolutionStatus.Unavailable);
        }

        // Cloud-only: no on-premises object, under this or any address. Group and OU rules are
        // evaluated from an on-prem DN, which this principal does not have and cannot have - a
        // cloud-only object cannot be a member of an on-prem group. Those rules are therefore not
        // "skipped", they are inapplicable. The user rows still apply and are what must protect
        // such a principal; the SamAccountName patterns cannot match either, for the same reason.
        // See docs/ProjectConstitution.md (Protected Principals) and
        // docs/ProtectedPrincipalResolution-Plan.md D4.
        //
        // Built from the CANONICAL address, never the typed one, so an alias-addressed cloud-only
        // principal is matched against the protected user rows by its real identity.
        //
        // CheckOuMatches and CheckTransitiveGroupMembership both degrade to "no match" on a null
        // DN without raising a failure, so this is logged rather than left silent: an operator
        // reading the audit trail has to be able to see which rules could not be evaluated.
        _logger.LogInformation(
            "Exchange resolved {Identity} as a cloud-only recipient (canonical {Canonical}) with no "
            + "on-premises object - group, OU and SamAccountName-pattern rules were NOT evaluated; "
            + "only protected user rows apply",
            identity, recipient.PrimarySmtpAddress);

        var cloudOnly = new ResolvedDirectoryPrincipal(
            Source: "ProtectedPrincipalService-EXO",
            DisplayName: recipient.PrimarySmtpAddress,
            UserPrincipalName: recipient.PrimarySmtpAddress,
            SamAccountName: null,
            PrimarySmtpAddress: recipient.PrimarySmtpAddress,
            DistinguishedName: null,
            ObjectGuid: null,
            EntraObjectId: recipient.ExternalDirectoryObjectId);

        return (cloudOnly, ResolutionStatus.Resolved);
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

        if (users.Count == 0)
            return null;

        if (users.Count > 1)
        {
            _logger.LogWarning("Ambiguous identity resolution for '{Identity}': matched {Count} AD users - failing closed", identity, users.Count);
            throw new InvalidOperationException($"Ambiguous: '{identity}' matches {users.Count} AD users.");
        }

        var adUser = users[0];

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
        _repository.Save(new ProtectedPrincipalData(
            config.Users ?? [],
            config.Groups ?? [],
            config.OrganizationalUnits ?? [],
            config.SamAccountNamePatterns ?? [],
            config.GroupTargets ?? []));

        InvalidateCache();
        _logger.LogInformation("Protected-principals config saved and cache invalidated");
    }

    // One-time import of the legacy protected-principals.json into protected_principal, then
    // archive the file (SqliteConfigStore-Plan Section 4). Only fills if not yet configured (DB wins).
    // Returns true if the legacy file exists but is unparseable / missing the ProtectedPrincipals
    // node: it is left in place (not archived) AND the store stays fail-closed until repaired.
    private bool ImportLegacyIfPresent(string legacyPath)
    {
        try
        {
            if (!File.Exists(legacyPath))
                return false;

            ProtectedPrincipalConfig? parsed;
            try
            {
                var wrapper = JsonSerializer.Deserialize<ProtectedPrincipalsFileWrapper>(
                    File.ReadAllText(legacyPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                parsed = wrapper?.ProtectedPrincipals;
                if (parsed == null)
                {
                    _logger.LogError("Legacy protected-principals.json exists but ProtectedPrincipals node is missing - failing closed until repaired/removed");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Legacy protected-principals.json is unparseable - failing closed until repaired/removed");
                return true;
            }

            try
            {
                _repository.ImportIfMissing(new ProtectedPrincipalData(
                    parsed.Users ?? [],
                    parsed.Groups ?? [],
                    parsed.OrganizationalUnits ?? [],
                    parsed.SamAccountNamePatterns ?? [],
                    parsed.GroupTargets ?? []));
            }
            catch (Exception ex)
            {
                // The file parsed fine but could not be committed to the DB (e.g. SQLite busy).
                // Do NOT archive and do NOT fall through to an unconfigured DB - that would
                // silently drop the protection rules. Fail closed; the file stays on disk so the
                // next startup retries the import.
                _logger.LogError(ex, "Failed to import legacy protected-principals.json into the store - failing closed until import succeeds");
                return true;
            }

            LegacyConfigImport.ArchiveFile(legacyPath, _logger);
            return false;
        }
        catch (Exception ex)
        {
            // Reached only if reading the file itself failed (not a parse error - those return
            // true above). A valid file we could not even read must also fail closed.
            _logger.LogError(ex, "Failed to process legacy protected-principals.json - failing closed");
            return true;
        }
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

    // Legacy exclusions come only from the MailboxPermissions/ExcludedUsers module
    // config. The appsettings Security:ExcludedUsers fallback was retired 2026-07-28:
    // it was invisible to the Protected Principals admin UI, so principals could be
    // blocked with no visible cause. Protection now lives in the DB store (UI-managed)
    // and this module-config value only.
    private string[] GetLegacyExclusions()
    {
        var excluded = _moduleConfig.GetValue("MailboxPermissions", "ExcludedUsers");
        if (!string.IsNullOrEmpty(excluded))
            return excluded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return [];
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
            _logger.LogError("Directory-read credential is not configured but protected groups are defined - configure it on the Admin Settings page");
            return (matches, true, "Protected-principal directory-read credential is not configured. Configure it on the Admin Settings page.");
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
                    _logger.LogWarning("Group expansion had errors and no matches found - failing closed for {Target}", target.UserPrincipalName);
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
            // Get-ADObject, not Get-ADUser: the target may be a GROUP (nesting plan S1), and
            // Get-ADUser answers a group with zero rows and no error - a silent allow.
            ps.AddCommand("Get-ADObject")
              .AddParameter("LDAPFilter", $"(sAMAccountName={escaped})")
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var objects = ps.Invoke();
            ps.Commands.Clear();

            // The class-agnostic lookup widens what a sAMAccountName can match, so anything
            // but exactly one result fails the check closed rather than resolving to an
            // arbitrary object (docs/GroupMemberNesting-Plan.md S1).
            bool fallbackFailed;
            (targetDn, fallbackFailed) = SelectFallbackDn(
                objects.Select(o => o?.Properties["DistinguishedName"]?.Value?.ToString()).ToList());
            if (fallbackFailed)
            {
                _logger.LogWarning(
                    "sAMAccountName {Sam} resolved to {Count} directory objects during the protected-group check - failing closed",
                    target.SamAccountName, objects.Count);
                return (matches, true);
            }
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

                // A protected group must match AS ITSELF, not only through its members:
                // in-chain ancestry never includes self, so without this a listed group is
                // freely nestable anywhere (owner 2026-08-11; nesting plan S1).
                if (IsProtectedGroupItself(targetDn, groupDn))
                {
                    matches.Add($"Group:{protectedGroup}");
                    continue;
                }

                var targetFilter = EscapeLdapFilter(targetDn);
                var groupFilter = EscapeLdapFilter(groupDn);
                // Get-ADObject, not Get-ADUser: the in-chain filter already evaluates nested
                // membership for any object class; only the cmdlet in front of it was wrong.
                ps.AddCommand("Get-ADObject")
                  .AddParameter("LDAPFilter", $"(&(distinguishedName={targetFilter})(memberOf:1.2.840.113556.1.4.1941:={groupFilter}))")
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                var inChainResult = ps.Invoke();
                ps.Commands.Clear();

                if (ps.HadErrors)
                {
                    expansionHadErrors = true;
                    ps.Streams.Error.Clear();
                    continue;
                }

                if (inChainResult.Count > 0)
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

    /// <summary>
    /// The exactly-one rule for the class-agnostic sAMAccountName fallback in
    /// CheckTransitiveGroupMembership: zero matches, multiple matches, or a match without a
    /// readable DN all fail closed instead of resolving to an arbitrary object
    /// (docs/GroupMemberNesting-Plan.md S1).
    /// </summary>
    internal static (string? TargetDn, bool Failed) SelectFallbackDn(IReadOnlyList<string?> candidateDns)
    {
        if (candidateDns.Count != 1 || string.IsNullOrWhiteSpace(candidateDns[0]))
            return (null, true);
        return (candidateDns[0], false);
    }

    /// <summary>
    /// The self-match half of the Groups rule: the target IS the protected group, compared on
    /// resolved DNs. In-chain ancestry never includes self, so without this a protected group
    /// is protected as a container of members but not as an object being moved.
    /// </summary>
    internal static bool IsProtectedGroupItself(string? targetDn, string groupDn)
        => !string.IsNullOrWhiteSpace(targetDn) &&
           string.Equals(targetDn, groupDn, StringComparison.OrdinalIgnoreCase);

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

        // No comma found - entire remaining string is the CN value
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
