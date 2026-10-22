using System.Collections.Immutable;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

/// <param name="ExchangeGuid">
/// The mailbox GUID Exchange will write, when one was established; null otherwise. Set by
/// <see cref="ProtectedPrincipalService.ResolveExchangeTargetAsync"/> only: for a synced mailbox,
/// the GUID its Active Directory object was shown to hold in <c>msExchMailboxGuid</c> (aud-a2c);
/// for a cloud-only recipient, Exchange's own. It is not compared against protection rows - it
/// identifies the mailbox, so that a write can be bound to the mailbox that was checked rather
/// than to the string the operator typed (aud-a2d).
/// </param>
public sealed record ResolvedDirectoryPrincipal(
    string Source,
    string DisplayName,
    string UserPrincipalName,
    string? SamAccountName,
    string? PrimarySmtpAddress,
    string? DistinguishedName,
    string? ObjectGuid,
    string? EntraObjectId,
    Guid? ExchangeGuid = null);

/// <summary>
/// One Active Directory object returned by the mailbox GUID lookup
/// (<see cref="ProtectedPrincipalService.FindMailboxGuidHoldersAsync"/>), with the
/// <c>msExchMailboxGuid</c> AD returned for it - null when absent or unreadable.
/// </summary>
public sealed record MailboxGuidHolder(string? ObjectGuid, string? DistinguishedName, Guid? MailboxGuid);

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

/// <summary>How <see cref="ProtectedPrincipalService.ReplaceConfig"/> ended.</summary>
public enum ProtectedPrincipalSaveStatus
{
    /// <summary>The rules were replaced.</summary>
    Saved,

    /// <summary>Nothing written: the legacy file was not imported, or the rules could not be read under the lock.</summary>
    StoreCorrupt,

    /// <summary>Nothing written: the save held no rules, the store held some, and the page had loaded none.</summary>
    EmptyOverNonEmpty,

    /// <summary>Nothing written: the rules changed after the page loaded them, to something other than this save.</summary>
    ConcurrentChange,

    /// <summary>Nothing written: the rules changed after the page loaded them, to exactly the rules this save holds.</summary>
    AlreadyApplied,
}

/// <summary>
/// The outcome of a protected-principals replace: the rules found under the write lock (null when they
/// could not be read), the rules it left (after a replace, or as they stand when they already held this
/// save), and - on <see cref="ProtectedPrincipalSaveStatus.Saved"/> and
/// <see cref="ProtectedPrincipalSaveStatus.AlreadyApplied"/> - the version of the rules as they stand.
/// </summary>
public sealed record ProtectedPrincipalSaveOutcome(
    ProtectedPrincipalSaveStatus Status,
    ProtectedPrincipalData? Before,
    ProtectedPrincipalData? After,
    string? Version = null);

/// <summary>
/// The protection rules as the admin page edits them, and the version of the one read they came from
/// (aud-a28n).
/// </summary>
public sealed record ProtectedPrincipalRulesForEditing(ProtectedPrincipalConfig Config, string Version);

/// <summary>
/// The directory-read secret as the admin page shows it (aud-a26n): the value stored in the database, empty when none
/// is, and the appsettings fallback the lookups use while that value gives them no Secret ID (null otherwise).
/// </summary>
public sealed record DirectoryReadSecretSetting(string Stored, int? FallbackInEffect);

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

    // Cross-process freshness on the shared config database (docs/SharedConfigDb-Plan.md AC4):
    // the token recorded as of the cached load; a cache hit first asks the watcher whether the
    // store moved past it. The TTL below still bounds staleness when the token cannot be read.
    private readonly ConfigChangeWatcher _changeWatcher;
    private long _loadedToken = ConfigChangeWatcher.Unknown;

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
        _changeWatcher = repository.CreateChangeWatcher(logger);

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

    /// <summary>True when a legacy protected-principals.json was not imported and keeps the store fail-closed (aud-a26h).</summary>
    public bool LegacyImportFailed => _legacyFileCorrupt;

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedConfig = null;
            _configLoadedAt = DateTime.MinValue;
        }
    }

    /// <summary>Test seam: the watcher this service consults on every cache hit.</summary>
    internal ConfigChangeWatcher ChangeWatcher => _changeWatcher;

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
        //
        // aud-a28p. One read is both the corruption probe and the list. A probe followed by a second,
        // fail-open read of the list let a failure between the two read as "no exclusions", and every
        // principal on the list was unprotected for that check. Read ahead of the rules' cache, which
        // does not hold the list.
        if (!TryGetLegacyExclusions(out var legacyExclusions))
            return (null, [], "MailboxPermissions module configuration is corrupt - protected-principal exclusions unavailable. Contact your administrator.");

        // FAIL-CLOSED: an unparseable legacy file still on disk keeps the store corrupt during
        // the upgrade window rather than silently un-protecting principals.
        if (_legacyFileCorrupt)
            return (null, [], "Protected-principals configuration is corrupt. Contact your administrator.");

        lock (_cacheLock)
        {
            // A write by the other instance on the shared database invalidates the cache before
            // its TTL does; the watcher reads the token at most once per 2 s.
            if (_cachedConfig != null && !_configCorrupt
                && !_changeWatcher.HasChangedSince(_loadedToken)
                && DateTime.UtcNow - _configLoadedAt < ConfigCacheTtl)
                return (_cachedConfig, legacyExclusions, null);
        }

        // Token BEFORE the load (scd-2): a write racing the read moves the stored token past
        // this value and is caught on the next check.
        var token = _changeWatcher.CurrentToken();

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
            _loadedToken = token;
        }

        return (config, legacyExclusions, null);
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
    /// answers "no such object". This is the entry point protection gates should use for a target
    /// the write binds by DN or UPN; a gate whose write hands the operator's string to EXCHANGE
    /// uses <see cref="ResolveExchangeTargetAsync"/> instead (aud-a2b).
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

        var (answered, recipient) = await AskExchangeAsync(identity);
        if (!answered)
            return (null, ResolutionStatus.Unavailable);

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
                var (canonical, canonicalStatus) = await ResolveWithStatusAsync(recipient.PrimarySmtpAddress);

                // aud-a2b. Exchange says this recipient is synced and names its canonical
                // address, and AD holds no object under it: the directories disagree, exactly as
                // in the same-address case below. Passing AD's NotFound through was not only the
                // wrong message - the gates that ALLOW on NotFound let the operation through.
                if (canonicalStatus == ResolutionStatus.NotFound)
                {
                    _logger.LogWarning(
                        "Exchange reports {Identity} as directory-synced with canonical address {Canonical}, "
                        + "but Active Directory holds no object under it - directories disagree, failing closed",
                        identity, recipient.PrimarySmtpAddress);
                    return (null, ResolutionStatus.Unavailable);
                }

                return (canonical, canonicalStatus);
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
        return (CloudOnlyPrincipal(identity, recipient), ResolutionStatus.Resolved);
    }

    /// <summary>
    /// Resolves the target of an operation EXCHANGE will perform with the operator's string, the
    /// way Exchange will resolve it, and refuses when Active Directory reads the same string as a
    /// different object. aud-a2b hole 2.
    /// </summary>
    /// <remarks>
    /// <see cref="ResolveWithExchangeFallbackAsync"/> asks AD first and stops at a Resolved answer.
    /// A string can be the sAMAccountName of an unprotected AD account and the Exchange alias of a
    /// protected mailbox: AD answered with the account, the check passed, and the write - which
    /// hands the same string to Exchange - landed on the mailbox. Nothing established that the
    /// object checked and the object written were the same object.
    ///
    /// Here Exchange is asked first, because it performs the write, and AD's reading of the
    /// string must agree with it:
    /// - synced recipient: the principal is the AD object behind its canonical address, and if AD
    ///   reads the typed string as a DIFFERENT object (by objectGUID) the string names two
    ///   objects - <c>Ambiguous</c>. No AD object under the canonical address is the directories
    ///   disagreeing - <c>Unavailable</c>. That object must then prove it owns the mailbox: it has
    ///   to be the only AD object whose <c>msExchMailboxGuid</c> is the recipient's ExchangeGuid
    ///   (aud-a2c, <see cref="SelectMailboxBacking"/>). An address alone is not proof - it can sit
    ///   in <c>mail</c> or <c>userPrincipalName</c> on an account that has no mailbox at all.
    /// - cloud-only recipient: an AD object for the same string is a second object - <c>Ambiguous</c>.
    /// - no recipient: an Exchange write cannot land anywhere, so AD's answer is the only object the
    ///   string names; neither directory knowing it is <c>NotFound</c>.
    /// - Exchange not asked, AD ambiguous or unavailable: refused, as everywhere else.
    /// </remarks>
    // virtual: the same test seam as ResolveWithExchangeFallbackAsync.
    public virtual async Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveExchangeTargetAsync(string identity)
    {
        var (answered, recipient) = await AskExchangeAsync(identity);
        if (!answered)
            return (null, ResolutionStatus.Unavailable);

        var (adPrincipal, adStatus) = await ResolveWithStatusAsync(identity);
        if (adStatus is ResolutionStatus.Ambiguous or ResolutionStatus.Unavailable)
            return (null, adStatus);

        if (recipient == null)
        {
            return adStatus == ResolutionStatus.Resolved && adPrincipal is not null
                ? (adPrincipal, ResolutionStatus.Resolved)
                : (null, ResolutionStatus.NotFound);
        }

        if (!recipient.ExistsOnPrem)
        {
            if (adStatus == ResolutionStatus.Resolved)
            {
                _logger.LogWarning(
                    "{Identity} is a cloud-only recipient in Exchange but an on-premises object in Active Directory "
                    + "- the string names two objects, failing closed",
                    identity);
                return (null, ResolutionStatus.Ambiguous);
            }

            return (CloudOnlyPrincipal(identity, recipient), ResolutionStatus.Resolved);
        }

        var (canonical, canonicalStatus) =
            string.Equals(recipient.PrimarySmtpAddress, identity, StringComparison.OrdinalIgnoreCase)
                ? (adPrincipal, adStatus)
                : await ResolveWithStatusAsync(recipient.PrimarySmtpAddress);

        if (canonicalStatus == ResolutionStatus.NotFound)
        {
            _logger.LogWarning(
                "Exchange reports {Identity} as directory-synced with canonical address {Canonical}, but Active "
                + "Directory holds no object under it - directories disagree, failing closed",
                identity, recipient.PrimarySmtpAddress);
            return (null, ResolutionStatus.Unavailable);
        }

        if (canonicalStatus != ResolutionStatus.Resolved || canonical is null)
            return (null, canonicalStatus == ResolutionStatus.Resolved ? ResolutionStatus.Unavailable : canonicalStatus);

        if (adStatus == ResolutionStatus.Resolved && (adPrincipal is null || !IsSameDirectoryObject(adPrincipal, canonical)))
        {
            _logger.LogWarning(
                "{Identity} resolves to one object in Active Directory and Exchange will write another "
                + "(canonical {Canonical}) - failing closed",
                identity, recipient.PrimarySmtpAddress);
            return (null, ResolutionStatus.Ambiguous);
        }

        // aud-a2c. Everything above compares AD with AD: its reading of the typed string against
        // its reading of the canonical address. Neither reading is tied to the mailbox Exchange
        // resolved, and the address lookup matches mail and userPrincipalName, which a non-recipient
        // account can carry - an admin's second account pointed at the owner's address, or any
        // account in the searched domain when the mailbox's own object lives in another. Checked
        // here, that account stood in for the mailbox and Exchange wrote the mailbox.
        return await ProveMailboxBackingAsync(identity, recipient, canonical);
    }

    /// <summary>
    /// Accepts <paramref name="canonical"/> - AD's object under the recipient's canonical address -
    /// only when Active Directory shows it owns the mailbox Exchange resolved, by the mailbox GUID
    /// both directories carry (aud-a2c). Every way of not knowing refuses; nothing here can turn an
    /// earlier refusal into an answer, because it runs only on a principal a2b already accepted.
    /// </summary>
    private async Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ProveMailboxBackingAsync(
        string identity, ResolvedRecipient recipient, ResolvedDirectoryPrincipal canonical)
    {
        if (recipient.ExchangeGuid is not Guid exchangeGuid)
        {
            _logger.LogWarning(
                "Exchange reports no mailbox GUID for {Identity} (canonical {Canonical}) - the Active Directory object "
                + "under that address cannot be shown to own the mailbox, failing closed",
                identity, recipient.PrimarySmtpAddress);
            return (null, ResolutionStatus.Unavailable);
        }

        var (answered, holders) = await FindMailboxGuidHoldersAsync(exchangeGuid);
        if (!answered)
            return (null, ResolutionStatus.Unavailable);

        var (proven, status, refusal) = SelectMailboxBacking(exchangeGuid, canonical, holders);
        if (status != ResolutionStatus.Resolved)
        {
            _logger.LogWarning(
                "{Identity} (canonical {Canonical}, mailbox GUID {MailboxGuid}): {Refusal} - failing closed",
                identity, recipient.PrimarySmtpAddress, exchangeGuid, refusal);
        }

        return (proven, status);
    }

    /// <summary>
    /// The aud-a2c decision, pure so every outcome is testable without a directory.
    /// <paramref name="canonical"/> is accepted only when it is the one and only object in
    /// <paramref name="found"/> that holds <paramref name="exchangeGuid"/>, compared by objectGUID.
    /// </summary>
    /// <remarks>
    /// - An object returned WITHOUT the GUID is not a holder, whatever the lookup that produced it:
    ///   the proof is the GUID, never the fact that a query returned the object.
    /// - No holder is <c>Unavailable</c>: nothing in AD shows which object owns the mailbox (a remote
    ///   mailbox whose ExchangeGuid was never stamped on-premises, or a mailbox object outside the
    ///   searched domain). It is never read as "not protected".
    /// - Two holders is <c>Ambiguous</c>, even when one of them is the canonical object: a copied
    ///   GUID is how a forged stand-in would look, and the real owner still holding it is how it is
    ///   seen.
    /// - One holder that is not the canonical object is <c>Ambiguous</c>: the address names one
    ///   object and the mailbox is another. This is the case the finding describes.
    /// - The comparison needs objectGUID on both sides - an identifier no attribute edit can give
    ///   another object. Without it there is nothing to compare, so <c>Unavailable</c>.
    /// </remarks>
    internal static (ResolvedDirectoryPrincipal? principal, ResolutionStatus status, string? refusal) SelectMailboxBacking(
        Guid exchangeGuid, ResolvedDirectoryPrincipal canonical, IReadOnlyList<MailboxGuidHolder> found)
    {
        var holders = found.Where(h => h.MailboxGuid == exchangeGuid).ToList();

        if (holders.Count == 0)
        {
            return (null, ResolutionStatus.Unavailable,
                "no Active Directory object holds this mailbox GUID in msExchMailboxGuid (a remote mailbox whose "
                + "ExchangeGuid was never stamped on-premises, or an object outside the searched domain)");
        }

        if (holders.Count > 1)
            return (null, ResolutionStatus.Ambiguous, $"{holders.Count} Active Directory objects hold this mailbox GUID");

        var holder = holders[0];
        if (string.IsNullOrWhiteSpace(holder.ObjectGuid) || string.IsNullOrWhiteSpace(canonical.ObjectGuid))
        {
            return (null, ResolutionStatus.Unavailable,
                "the mailbox GUID holder or the object under the canonical address has no objectGUID to compare");
        }

        if (!string.Equals(holder.ObjectGuid, canonical.ObjectGuid, StringComparison.OrdinalIgnoreCase))
        {
            return (null, ResolutionStatus.Ambiguous,
                $"the object under the canonical address ({canonical.DistinguishedName}) is not the object that "
                + $"holds this mailbox GUID ({holder.DistinguishedName})");
        }

        return (canonical with { ExchangeGuid = exchangeGuid }, ResolutionStatus.Resolved, null);
    }

    /// <summary>
    /// Every Active Directory object whose <c>msExchMailboxGuid</c> is <paramref name="mailboxGuid"/>.
    /// <c>answered</c> is false when the lookup could not run - credential, throttle, any error,
    /// including a schema with no Exchange attributes - and the list is then meaningless.
    /// </summary>
    /// <remarks>
    /// <c>Get-ADObject</c>, not <c>Get-ADUser</c>: a second holder of any object class has to be
    /// seen for <see cref="SelectMailboxBacking"/> to call it ambiguous. Same credential, throttle and
    /// fail-closed handling as <see cref="ResolveWithStatusAsync"/>, and the same scope: the default
    /// domain, so a holder elsewhere in the forest is not seen (the miss refuses; it cannot admit).
    /// </remarks>
    // virtual: test seam, the same shape as ResolveWithStatusAsync.
    public virtual async Task<(bool answered, IReadOnlyList<MailboxGuidHolder> holders)> FindMailboxGuidHoldersAsync(Guid mailboxGuid)
    {
        if (DateTime.UtcNow - _lastCredentialFailure < CredentialFailureTtl)
        {
            _logger.LogDebug("Skipping the mailbox GUID lookup - credential recently failed");
            return (false, []);
        }

        var secretId = GetDirectoryReadSecretId();
        if (secretId == null)
        {
            _logger.LogDebug("Cannot look up a mailbox GUID - directory-read credential not configured");
            return (false, []);
        }

        var creds = await _delineaService.GetCredentialsBySecretIdAsync(secretId.Value);
        if (creds == null)
        {
            _lastCredentialFailure = DateTime.UtcNow;
            _logger.LogWarning("Failed to retrieve directory-read credential for the mailbox GUID lookup");
            return (false, []);
        }

        try
        {
            if (!await _adThrottle.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                _logger.LogWarning("AD throttle timeout during the mailbox GUID lookup for {MailboxGuid}", mailboxGuid);
                return (false, []);
            }

            try
            {
                var holders = await Task.Run(() => FindMailboxGuidHoldersViaActiveDirectory(
                    mailboxGuid, creds.Value.username, creds.Value.password, creds.Value.domain));
                return (true, holders);
            }
            finally
            {
                _adThrottle.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mailbox GUID lookup failed for {MailboxGuid}", mailboxGuid);
            return (false, []);
        }
    }

    private static IReadOnlyList<MailboxGuidHolder> FindMailboxGuidHoldersViaActiveDirectory(
        Guid mailboxGuid, string username, string password, string domain)
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
        ps.Streams.Error.Clear();

        ps.AddCommand("Get-ADObject")
          .AddParameter("LDAPFilter", BuildMailboxGuidFilter(mailboxGuid))
          .AddParameter("Properties", new[] { "DistinguishedName", "ObjectGUID", "msExchMailboxGuid" })
          .AddParameter("Credential", CreateCredential(username, password, domain))
          .AddParameter("ErrorAction", "Stop");
        var objects = ps.Invoke();
        ps.Commands.Clear();

        // Completeness matters here, not just success: a search that dropped a second holder would
        // read as a unique one. Any error recorded alongside the results fails the lookup.
        if (ps.Streams.Error.Count > 0)
            throw new InvalidOperationException($"The mailbox GUID lookup reported {ps.Streams.Error.Count} error(s).");

        return objects
            .Where(o => o != null)
            .Select(o => new MailboxGuidHolder(
                ObjectGuid: o.Properties["ObjectGUID"]?.Value?.ToString(),
                DistinguishedName: o.Properties["DistinguishedName"]?.Value?.ToString(),
                MailboxGuid: ParseMailboxGuid(o.Properties["msExchMailboxGuid"]?.Value)))
            .ToList();
    }

    /// <summary>
    /// Reads <c>msExchMailboxGuid</c> as AD returns it - a 16-byte octet string in the Windows GUID
    /// layout (Data1, Data2 and Data3 little-endian), which is the layout <see cref="Guid(byte[])"/>
    /// reads. Wrapped, single-element and already-parsed forms are accepted; anything else, and all
    /// zeros, is null - which <see cref="SelectMailboxBacking"/> treats as "not a holder".
    /// </summary>
    internal static Guid? ParseMailboxGuid(object? value)
    {
        switch (value)
        {
            case PSObject wrapped:
                return ParseMailboxGuid(wrapped.BaseObject);
            case byte[] bytes:
                return bytes.Length == 16 ? NonEmpty(new Guid(bytes)) : null;
            case Guid g:
                return NonEmpty(g);
            case string s:
                return Guid.TryParse(s, out var parsed) ? NonEmpty(parsed) : null;
            case System.Collections.IList { Count: 1 } single:
                return ParseMailboxGuid(single[0]);
            default:
                return null;
        }

        static Guid? NonEmpty(Guid g) => g == Guid.Empty ? null : g;
    }

    /// <summary>
    /// The LDAP equality filter for a binary attribute: every byte escaped as <c>\hh</c>, in the
    /// order AD stores them (<see cref="Guid.ToByteArray"/>). Matching the GUID's text form would
    /// match nothing - which would refuse every synced mailbox rather than admit one, but is still
    /// wrong.
    /// </summary>
    internal static string BuildMailboxGuidFilter(Guid mailboxGuid)
    {
        var sb = new System.Text.StringBuilder("(msExchMailboxGuid=", 19 + 48 + 1);
        foreach (var b in mailboxGuid.ToByteArray())
            sb.Append('\\').Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.Append(')').ToString();
    }

    /// <summary>
    /// The same directory object: objectGUID when both carry one, otherwise the DN. Two
    /// principals that carry neither cannot be shown to be one object, so they are not.
    /// </summary>
    internal static bool IsSameDirectoryObject(ResolvedDirectoryPrincipal a, ResolvedDirectoryPrincipal b)
    {
        if (!string.IsNullOrWhiteSpace(a.ObjectGuid) && !string.IsNullOrWhiteSpace(b.ObjectGuid))
            return string.Equals(a.ObjectGuid, b.ObjectGuid, StringComparison.OrdinalIgnoreCase);

        return !string.IsNullOrWhiteSpace(a.DistinguishedName)
               && string.Equals(a.DistinguishedName, b.DistinguishedName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asks Exchange which recipient an identity is. <c>answered</c> is false when the question
    /// could not be put - no scope, no resolver, or a lookup that threw - and the recipient is
    /// then meaningless; a null recipient with <c>answered</c> true is Exchange's affirmative
    /// "no such recipient".
    /// </summary>
    private async Task<(bool answered, ResolvedRecipient? recipient)> AskExchangeAsync(string identity)
    {
        if (_scopeFactory == null)
        {
            _logger.LogWarning(
                "Cannot ask Exchange about {Identity} - no service scope factory. Failing closed.",
                identity);
            return (false, null);
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var resolver = scope.ServiceProvider.GetService<IIdentityResolver>();
            if (resolver == null)
            {
                _logger.LogWarning(
                    "Cannot ask Exchange about {Identity} - no IIdentityResolver registered. Failing closed.",
                    identity);
                return (false, null);
            }

            return (true, await resolver.ResolveRecipientAsync(identity));
        }
        catch (Exception ex)
        {
            // The lookup did not run. Reporting NotFound here would let an EXO outage present as
            // an affirmative absence, and callers that allow on NotFound would un-protect the
            // principal. Deny instead.
            _logger.LogWarning(ex, "Exchange lookup failed for {Identity} - failing closed", identity);
            return (false, null);
        }
    }

    /// <summary>
    /// A principal for a recipient with no on-premises object, built from its CANONICAL address
    /// so an alias-addressed cloud-only principal is matched by its real identity.
    /// </summary>
    private ResolvedDirectoryPrincipal CloudOnlyPrincipal(string identity, ResolvedRecipient recipient)
    {
        // CheckOuMatches and CheckTransitiveGroupMembership both degrade to "no match" on a null
        // DN without raising a failure, so this is logged rather than left silent: an operator
        // reading the audit trail has to be able to see which rules could not be evaluated.
        _logger.LogInformation(
            "Exchange resolved {Identity} as a cloud-only recipient (canonical {Canonical}) with no "
            + "on-premises object - group, OU and SamAccountName-pattern rules were NOT evaluated; "
            + "only protected user rows apply",
            identity, recipient.PrimarySmtpAddress);

        return new ResolvedDirectoryPrincipal(
            Source: "ProtectedPrincipalService-EXO",
            DisplayName: recipient.PrimarySmtpAddress,
            UserPrincipalName: recipient.PrimarySmtpAddress,
            SamAccountName: null,
            PrimarySmtpAddress: recipient.PrimarySmtpAddress,
            DistinguishedName: null,
            ObjectGuid: null,
            EntraObjectId: recipient.ExternalDirectoryObjectId,
            // Exchange's own mailbox GUID: there is no AD object to agree with, so this is simply the
            // identity of the mailbox Exchange will write, carried for aud-a2d.
            ExchangeGuid: recipient.ExchangeGuid);
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

    /// <summary>
    /// Replaces every protection rule with <paramref name="config"/>. A test fixture: it compares nothing and
    /// records nothing, so the app saves through <see cref="ReplaceConfig"/> (aud-a26k).
    /// </summary>
    /// <remarks>
    /// Internal rather than removed because the suite seeds the store through it. A production caller would be
    /// the snapshot overwrite aud-a28j and aud-a28n took out of the admin page; WholeStoreWriterTripwireTests
    /// refuses one.
    /// </remarks>
    internal void SaveConfig(ProtectedPrincipalConfig config)
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

    /// <summary>
    /// The admin page's save (aud-a28j): replaces the rules with <paramref name="config"/>, deciding
    /// inside the write from the rules as they are under its lock, and returns what it found and left.
    /// </summary>
    /// <param name="pageLoadedRules">
    /// Whether the page saving this loaded at least one rule. A save holding no rules over a store
    /// that holds some is refused unless it did.
    /// </param>
    /// <param name="loadedVersion">
    /// The <see cref="RulesVersion"/> of the rules the page loaded, from <see cref="LoadRulesForEditing"/>, or
    /// the one its last save returned.
    /// </param>
    /// <remarks>
    /// Four refusals, each writing nothing. A legacy file the import refused still holds rules the
    /// database has never seen: writing now would mark the store configured, and the import would never
    /// run again. Rows that cannot be read under the lock cannot be replaced knowingly. A save holding
    /// no rules, over a store that holds some, from a page that loaded none, is the aud-a28j wipe itself -
    /// the page's lists are empty because it could not read the rules, or because the other instance added
    /// them since, and either way the admin never saw what the save would delete. A page that loaded rules
    /// and had the admin remove them one by one is that admin's intent, and saves.
    ///
    /// And rules that changed after the page loaded them (aud-a28n): the save replaces every rule with the
    /// page's lists, so it would delete a rule another admin or the other instance added since, or restore
    /// one they removed, without anyone having seen it. The compare is made here, inside the write, because
    /// a compare made before it can be overtaken by the other instance before the replace lands; the page's
    /// version, not a read just before the save, because the stale view is the page's. A store that already
    /// holds exactly the rules this save writes is no conflict - the change was made the same way elsewhere -
    /// so it is reported as already applied and nothing is written (the aud-a26k rule). Nothing is merged:
    /// any other difference is refused, and the admin reloads and re-applies.
    /// </remarks>
    public ProtectedPrincipalSaveOutcome ReplaceConfig(ProtectedPrincipalConfig config, bool pageLoadedRules, string loadedVersion)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (_legacyFileCorrupt)
            return new ProtectedPrincipalSaveOutcome(ProtectedPrincipalSaveStatus.StoreCorrupt, null, null);

        var data = new ProtectedPrincipalData(
            config.Users ?? [],
            config.Groups ?? [],
            config.OrganizationalUnits ?? [],
            config.SamAccountNamePatterns ?? [],
            config.GroupTargets ?? []);
        var refusal = ProtectedPrincipalSaveStatus.Saved;

        var outcome = _repository.Replace(current =>
        {
            if (!current.Readable)
            {
                refusal = ProtectedPrincipalSaveStatus.StoreCorrupt;
                return null;
            }

            if (data.RuleCount == 0 && current.Data.RuleCount > 0 && !pageLoadedRules)
            {
                refusal = ProtectedPrincipalSaveStatus.EmptyOverNonEmpty;
                return null;
            }

            if (!string.Equals(RulesVersion(current), loadedVersion, StringComparison.Ordinal))
            {
                refusal = current.Configured && SameRules(current.Data, data)
                    ? ProtectedPrincipalSaveStatus.AlreadyApplied
                    : ProtectedPrincipalSaveStatus.ConcurrentChange;
                return null;
            }

            return data;
        });

        if (!outcome.Replaced)
        {
            var status = refusal == ProtectedPrincipalSaveStatus.Saved ? ProtectedPrincipalSaveStatus.StoreCorrupt : refusal;
            var before = outcome.Before.Readable ? outcome.Before.Data : null;
            return status == ProtectedPrincipalSaveStatus.AlreadyApplied
                ? new ProtectedPrincipalSaveOutcome(status, before, before, RulesVersion(outcome.Before))
                : new ProtectedPrincipalSaveOutcome(status, before, null);
        }

        InvalidateCache();
        _logger.LogInformation("Protected-principals config replaced and cache invalidated");
        return new ProtectedPrincipalSaveOutcome(
            ProtectedPrincipalSaveStatus.Saved,
            outcome.Before.Data,
            outcome.After is { Readable: true } after ? after.Data : data,
            outcome.After is { Readable: true } left ? RulesVersion(left) : null);
    }

    /// <summary>
    /// The rules as the admin page shows them for editing, and the version of the same read; null when the
    /// legacy file was not imported or the rows cannot be read (aud-a28n).
    /// </summary>
    /// <remarks>
    /// One fresh read, not <see cref="LoadEffectiveConfig"/>'s cache: the version is the save's proof that the
    /// rules are still what the admin was shown, so the lists shown and the version must come from the same
    /// read. A version taken from a newer read than the lists would let the save write the older lists over a
    /// change the page never displayed. An unconfigured store shows no rules, as the gate reads it.
    /// </remarks>
    public ProtectedPrincipalRulesForEditing? LoadRulesForEditing()
    {
        if (_legacyFileCorrupt)
            return null;

        var snapshot = _repository.ReadSnapshot();
        if (!snapshot.Readable)
            return null;

        var rules = snapshot.Configured ? snapshot.Data : ProtectedPrincipalData.Empty;
        return new ProtectedPrincipalRulesForEditing(
            new ProtectedPrincipalConfig
            {
                Users = rules.Users,
                Groups = rules.Groups,
                OrganizationalUnits = rules.OrganizationalUnits,
                SamAccountNamePatterns = rules.SamAccountNamePatterns,
                GroupTargets = rules.GroupTargets,
            },
            RulesVersion(snapshot));
    }

    /// <summary>The rules' own content as a version: each kind's stored rows in order, and the presence marker.</summary>
    /// <remarks>
    /// Not the store's change token, for aud-a28i's reasons: it moves on every config write by either instance on
    /// the shared database, so any settings save elsewhere would refuse every open page, and it misses a writer
    /// that bypasses the store. The rows are exact. The marker is in it so that rules someone configured empty are
    /// not the same version as rules never configured.
    /// </remarks>
    internal static string RulesVersion(ProtectedPrincipalSnapshot snapshot)
    {
        if (!snapshot.Readable)
            return "unreadable";

        var canonical = JsonSerializer.Serialize(new
        {
            snapshot.Configured,
            Users = Sorted(snapshot.Data.Users),
            Groups = Sorted(snapshot.Data.Groups),
            OrganizationalUnits = Sorted(snapshot.Data.OrganizationalUnits),
            SamAccountNamePatterns = Sorted(snapshot.Data.SamAccountNamePatterns),
            GroupTargets = Sorted(snapshot.Data.GroupTargets),
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    private static string[] Sorted(string[] values) =>
        values.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ThenBy(v => v, StringComparer.Ordinal).ToArray();

    // Whether the stored rules are, kind by kind, the rules a save would write - compared as the table
    // stores them: blanks dropped, and case ignored, as its NOCASE key does.
    private static bool SameRules(ProtectedPrincipalData stored, ProtectedPrincipalData wanted) =>
        SameSet(stored.Users, wanted.Users)
        && SameSet(stored.Groups, wanted.Groups)
        && SameSet(stored.OrganizationalUnits, wanted.OrganizationalUnits)
        && SameSet(stored.SamAccountNamePatterns, wanted.SamAccountNamePatterns)
        && SameSet(stored.GroupTargets, wanted.GroupTargets);

    private static bool SameSet(string[] a, string[] b) =>
        new HashSet<string>(a.Where(v => !string.IsNullOrWhiteSpace(v)), StringComparer.OrdinalIgnoreCase)
            .SetEquals(b.Where(v => !string.IsNullOrWhiteSpace(v)));

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

    public int? GetDirectoryReadSecretId() =>
        PositiveId(_moduleConfig.GetValue(ProtectedPrincipalsModuleKey, DirectoryReadSecretConfigKey))
        ?? PositiveId(_config["Security:ProtectedPrincipalDirectoryReadSecretId"]);

    /// <summary>
    /// The directory-read secret as the admin page shows it: false when the ProtectedPrincipals module config cannot
    /// be read (aud-a26k), so the page can say so rather than show the id as unset; otherwise the value the database
    /// holds and, while that value gives the lookups no Secret ID, the fallback they use instead (aud-a26n).
    /// </summary>
    /// <remarks>
    /// The lookups keep <see cref="GetDirectoryReadSecretId"/>'s fail-open read, because an unreadable id there
    /// means no credential and every lookup fails closed. On the admin page the same read made an unreadable
    /// store look unset - blank, or the appsettings fallback - and a page that then saved a changed id wrote over
    /// a value it had never shown.
    /// </remarks>
    public bool TryGetDirectoryReadSecret(out DirectoryReadSecretSetting setting)
    {
        if (!_moduleConfig.TryGetModuleConfig(ProtectedPrincipalsModuleKey, out var values))
        {
            setting = new DirectoryReadSecretSetting("", null);
            return false;
        }

        setting = DescribeDirectoryReadSecret(values.GetValueOrDefault(DirectoryReadSecretConfigKey));
        return true;
    }

    /// <summary>
    /// The setting for a value stored in the database: the value, and the appsettings fallback while it gives the
    /// lookups no Secret ID - the rule <see cref="GetDirectoryReadSecretId"/> applies.
    /// </summary>
    public DirectoryReadSecretSetting DescribeDirectoryReadSecret(string? stored) =>
        new(stored ?? "", PositiveId(stored) is null ? PositiveId(_config["Security:ProtectedPrincipalDirectoryReadSecretId"]) : null);

    public static bool IsSecretId(string? value) => PositiveId(value) is not null;

    private static int? PositiveId(string? value) =>
        int.TryParse(value, out var id) && id > 0 ? id : null;

    /// <summary>
    /// Stores the directory-read secret id, comparing nothing, and returns the ProtectedPrincipals module values as
    /// they were under the write lock and as the write left them. A test fixture: the admin page writes through the
    /// two-argument form (aud-a26n).
    /// </summary>
    internal ModuleConfigUpdate SaveDirectoryReadSecretId(string value) =>
        _moduleConfig.UpdateModuleValues(
            ProtectedPrincipalsModuleKey,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [DirectoryReadSecretConfigKey] = value });

    /// <summary>
    /// The admin page's write of the directory-read secret id (aud-a26n): written only while the stored value is
    /// still <paramref name="loaded"/>, decided under the write lock; a stored value already
    /// <paramref name="wanted"/> is reported as already applied and not written; any other refuses, nothing written.
    /// Throws when the write fails.
    /// </summary>
    public ModuleConfigSaveOutcome SaveDirectoryReadSecretId(string loaded, string wanted) =>
        _moduleConfig.SaveChangedValues(
            ProtectedPrincipalsModuleKey,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [DirectoryReadSecretConfigKey] = loaded },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [DirectoryReadSecretConfigKey] = wanted });

    public ModuleConfigSaveOutcome CompareDirectoryReadSecretId(string loaded, string wanted) =>
        _moduleConfig.CompareChangedValues(
            ProtectedPrincipalsModuleKey,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [DirectoryReadSecretConfigKey] = loaded },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [DirectoryReadSecretConfigKey] = wanted });

    // Legacy exclusions come only from the MailboxPermissions/ExcludedUsers module
    // config. The appsettings Security:ExcludedUsers fallback was retired 2026-07-28:
    // it was invisible to the Protected Principals admin UI, so principals could be
    // blocked with no visible cause. Protection now lives in the DB store (UI-managed)
    // and this module-config value only. False when the rows cannot be read: never an
    // empty list standing in for one that was not read (aud-a28p).
    private bool TryGetLegacyExclusions(out string[] exclusions)
    {
        if (!_moduleConfig.TryGetModuleConfig("MailboxPermissions", out var values))
        {
            exclusions = [];
            return false;
        }

        var excluded = values.GetValueOrDefault("ExcludedUsers");
        exclusions = string.IsNullOrEmpty(excluded)
            ? []
            : excluded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return true;
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

        // Audit findings A3 and aud-a3b. What used to follow was one in-chain query per
        // protected group, sent to the target's domain (A3 added the routing), reading "no rows"
        // as "not a member". aud-a3b found three ways to get no rows while the answer is yes - a
        // domain-local group in another domain, the primary group, and a resolver that dropped
        // the domain half of DOMAIN\Name - so the decision now lives in a pure evaluator that
        // asks each question of the domain holding the answer, and the runspace only performs
        // the reads it is told to. The owning domain is still derived from the target's DN and
        // an undeterminable one still fails closed; that rule moved into the evaluator with it.
        var directory = new PowerShellProtectedGroupDirectory(ps, credential);
        var outcome = ProtectedGroupMembership.Evaluate(protectedGroups, targetDn, directory, _logger);
        matches.AddRange(outcome.Matches);
        return (matches, outcome.Incomplete);
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
