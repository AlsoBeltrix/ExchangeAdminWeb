using System.Collections.Immutable;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

/// <param name="ExchangeGuid">
/// The mailbox GUID Exchange will write, when one was established; null otherwise. Set by
/// <see cref="ProtectedPrincipalService.ResolveExchangeTargetAsync"/> only: Exchange's own, for the
/// recipient Exchange resolved - a synced mailbox's carried beside the account found by its
/// on-premises SID, a cloud-only recipient's alone. It is not compared against protection rows - it
/// identifies the mailbox, so that a write can be bound to the mailbox that was checked rather
/// than to the string the operator typed (aud-a2d).
/// </param>
/// <param name="ObjectSid">
/// The Active Directory object's objectSid, canonical, when the resolution read it; null otherwise. Filled by
/// <see cref="ProtectedPrincipalService"/>'s own AD resolution and by its binding of a synced Exchange recipient by SID.
/// A <c>user_sid</c> protection row compares against it (docs/ProtectedPrincipalIdentifiers-Plan.md C.2); a target that
/// does not carry one is compared by the row's label, as before the identifier rows existed, so a resolution that did not
/// read it cannot drop a protection.
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
    Guid? ExchangeGuid = null,
    string? ObjectSid = null);

/// <summary>
/// One object the global catalog holds under an objectSid (<see cref="ProtectedPrincipalService.FindObjectsBySidAsync"/>),
/// as it was read. docs/ProtectedPrincipalIdentifiers-Plan.md C.2 step 4.
/// </summary>
public sealed record DirectorySidObject(
    string? DistinguishedName,
    string? ObjectGuid,
    string? ObjectSid,
    string? ObjectClass,
    string? SamAccountName,
    string? UserPrincipalName,
    string? Mail,
    string? DisplayName);

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

    /// <summary>
    /// Identifier rows (docs/ProtectedPrincipalIdentifiers-Plan.md A.2), each "identifier|label"
    /// (<see cref="ProtectedPrincipalIdEntry"/>): protected users by objectSid. A row of <see cref="Users"/> whose value is
    /// one of these rows' label is its companion - written for an older build, not evaluated by this one.
    /// </summary>
    public string[] UserIds { get; set; } = [];

    /// <summary>Protected groups by objectSid, "objectSid|DN"; their companions are in <see cref="Groups"/>.</summary>
    public string[] GroupIds { get; set; } = [];

    /// <summary>Protected OUs by objectGUID, "objectGUID|DN"; their companions are in <see cref="OrganizationalUnits"/>.</summary>
    public string[] OuIds { get; set; } = [];

    /// <summary>These lists, repository-shaped: all eight kinds, a null list as an empty one.</summary>
    public ProtectedPrincipalData ToData() =>
        new(Users ?? [], Groups ?? [], OrganizationalUnits ?? [], SamAccountNamePatterns ?? [], GroupTargets ?? [])
        {
            UserIds = UserIds ?? [],
            GroupIds = GroupIds ?? [],
            OuIds = OuIds ?? [],
        };

    /// <summary>The lists <paramref name="data"/> holds, all eight kinds.</summary>
    public static ProtectedPrincipalConfig FromData(ProtectedPrincipalData data) => new()
    {
        Users = data.Users,
        Groups = data.Groups,
        OrganizationalUnits = data.OrganizationalUnits,
        SamAccountNamePatterns = data.SamAccountNamePatterns,
        GroupTargets = data.GroupTargets,
        UserIds = data.UserIds,
        GroupIds = data.GroupIds,
        OuIds = data.OuIds,
    };
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

public class ProtectedPrincipalService : IProtectedPrincipalLegacyResolver
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

    /// <summary>
    /// Test seam (kd-88): fetches the directory-read secret for its Secret ID. Null in production, which asks the secret store
    /// (<see cref="DelineaService.GetCredentialsBySecretIdAsync"/>) as before.
    /// </summary>
    internal Func<int, Task<(string username, string password, string domain)?>>? DirectoryReadSecretFetch { get; set; }

    /// <summary>
    /// Test seam (kd-88): the directory session the resolution, the mailbox proof and the group check read in, not yet open.
    /// Production builds the session they always built; the ActiveDirectory module is imported into it before the first read.
    /// </summary>
    internal Func<Runspace> DirectoryRunspace { get; set; } = DefaultDirectoryRunspace;

    /// <summary>
    /// docs/ProtectedPrincipalIdentifiers-Plan.md C.2 step 3: where a synced Exchange recipient's on-premises SID is read -
    /// Microsoft Graph in production (<see cref="GraphOnPremisesSidSource"/>). Null for a service built without one, which
    /// cannot ask, so a synced Exchange target is refused as Unavailable. Settable as a test seam.
    /// </summary>
    internal IOnPremisesSidSource? OnPremisesSidSource { get; set; }

    private static Runspace DefaultDirectoryRunspace() => DirectorySessionPool.DefaultRunspace();

    // kd-88. The directory search service, whose warm sessions for checks this service's reads borrow; null for a service
    // built without one (the suite), which keeps warm sessions of its own over DirectoryRunspace.
    private readonly ADDirectorySearchService? _directorySearch;
    private DirectorySessionPool? _ownSessions;
    private Func<Runspace>? _ownSessionsFactory;

    /// <summary>How long a check waits for a warm session before it refuses as busy.</summary>
    internal static readonly TimeSpan SessionWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// kd-88. The warm directory sessions this service's reads are lent: the directory search service's sessions for checks
    /// in production, else sessions of its own over <see cref="DirectoryRunspace"/> - rebuilt if that seam is replaced.
    /// </summary>
    internal DirectorySessionPool DirectorySessions
    {
        get
        {
            if (_directorySearch is not null)
                return _directorySearch.CheckSessions;

            lock (_cacheLock)
            {
                if (_ownSessions is null || _ownSessionsFactory != DirectoryRunspace)
                {
                    _ownSessions?.Dispose();
                    _ownSessions = new DirectorySessionPool(DirectoryRunspace);
                    _ownSessionsFactory = DirectoryRunspace;
                }
                return _ownSessions;
            }
        }
    }

    /// <summary>
    /// kd-88. The directory-read secret, fetched once per operation: inside a <see cref="DirectoryReadOperation"/> - a single
    /// change, a bulk file - the operation's one answer, its secret or its failure; outside one, a fetch of its own. A fetch
    /// that throws is a secret not retrieved, as the store's own failures already are.
    /// </summary>
    private async Task<(string username, string password, string domain)?> DirectoryReadSecretAsync(int secretId)
    {
        try
        {
            return await DirectoryReadOperation.DirectoryReadSecretAsync(secretId,
                DirectoryReadSecretFetch ?? (id => _delineaService.GetCredentialsBySecretIdAsync(id)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The protected-principal directory-read credential could not be retrieved");
            return null;
        }
    }
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public ProtectedPrincipalService(
        IWebHostEnvironment env,
        IConfiguration config,
        ModuleConfigService moduleConfig,
        ProtectedPrincipalRepository repository,
        DelineaService delineaService,
        ILogger<ProtectedPrincipalService> logger,
        IServiceScopeFactory? scopeFactory = null,
        ADDirectorySearchService? directorySearch = null,
        IOnPremisesSidSource? onPremisesSidSource = null)
    {
        OnPremisesSidSource = onPremisesSidSource;
        _env = env;
        _config = config;
        _moduleConfig = moduleConfig;
        _repository = repository;
        _delineaService = delineaService;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _directorySearch = directorySearch;
        _changeWatcher = repository.CreateChangeWatcher(logger);

        var legacyPath = Path.Combine(env.ContentRootPath, "config", "protected-principals.json");
        _legacyFileCorrupt = ImportLegacyIfPresent(legacyPath);
    }

    public const string DirectoryReadSecretConfigKey = "DirectoryReadSecretId";

    /// <summary>
    /// The Admin Settings field "Graph Directory Read Credential (Delinea Secret ID)": the secret of the app registration
    /// (<c>User.Read.All</c>, application) through which <see cref="GraphOnPremisesSidSource"/> reads a synced recipient's
    /// on-premises SID. Its own field, empty until an admin fills it, with no appsettings fallback (owner ruling 2026-11-03).
    /// </summary>
    public const string GraphDirectoryReadSecretConfigKey = "GraphDirectoryReadSecretId";
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
        // kd-88: timed and logged, as part of the caller's check when it began one.
        using var check = BeginCheck(target.UserPrincipalName is { Length: > 0 } upn ? upn : target.DistinguishedName ?? target.DisplayName);

        var (cfg, legacyExclusions, loadError) = LoadEffectiveConfig();

        if (loadError != null)
            return ProtectedPrincipalResult.Failed(loadError);

        var matchedRules = new List<string>();

        if (cfg != null)
        {
            CheckDirectUserMatches(cfg, target, matchedRules);
            CheckPatternMatches(cfg, target, matchedRules);
            CheckOuMatches(cfg, target, matchedRules, ouIdsByLabel: false);

            // docs/ProtectedPrincipalIdentifiers-Plan.md C.2: group_sid rows join the group check as SIDs, and ou_guid rows
            // are compared by the objectGUIDs of the target's containers - both read in the one directory session the group
            // check already takes. With no identifier rows this is the group check exactly as it was.
            var groupEntries = GroupEntries(cfg, out var groupLabels);
            var ouIds = RowsToEvaluate(cfg.OrganizationalUnits, cfg.OuIds, ProtectedPrincipalIdEntry.CanonicalGuid).Ids;
            if (groupEntries.Count > 0 || ouIds.Count > 0)
            {
                var groupResult = await CheckGroupMembershipAsync(groupEntries, groupLabels, ouIds, target);
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
    /// kd-88. One directory read in a lent session, counted for the timing line: a read that throws fails the session, so a
    /// session it may have broken is not lent again.
    /// </summary>
    private static System.Collections.ObjectModel.Collection<PSObject> InvokeIn(DirectorySessionPool.DirectorySession session)
    {
        DirectoryReadOperation.CountRead();
        try
        {
            return session.PowerShell.Invoke();
        }
        catch
        {
            session.Fail();
            throw;
        }
    }

    /// <summary>The check's name in the timing line (kd-88).</summary>
    internal const string ProtectionCheckName = "Protected-principal check";

    /// <summary>
    /// kd-88. Begins one protected-principal check of <paramref name="target"/> - its resolution and its rules - as one
    /// operation: the directory-read credential fetched at most once for it, and when it ends one Information line with the
    /// time it took, the directory reads it made and the credential fetches it started ("Protected-principal check for
    /// {Target} took {ElapsedMs} ms ({Reads} directory reads, {Fetches} credential fetches)"). Dispose it where the check
    /// ends. Begun inside a check already in progress, it is part of that one.
    /// </summary>
    public DirectoryReadOperation BeginCheck(string? target) =>
        DirectoryReadOperation.BeginCheck(_logger, ProtectionCheckName, target);

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
            // Directory-free, so an ou_guid row is compared here by its label - the DN suffix its companion carries - as
            // this check compared that OU before the identifier rows existed: no protection it gave is lost.
            CheckOuMatches(cfg, target, matchedRules, ouIdsByLabel: true);
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

        ProtectedPrincipalConfig? config = configured ? ProtectedPrincipalConfig.FromData(data) : null;

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

        var creds = await DirectoryReadSecretAsync(secretId.Value); // kd-88: the operation's one fetch
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
        // kd-88: its two directory lookups share the operation's one credential fetch.
        using var operation = DirectoryReadOperation.Begin();

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
                // docs/ProtectedPrincipalIdentifiers-Plan.md C.2: when the SID source reports the recipient's on-premises
                // SID, the principal is the one account holding it, read at the global catalog (steps 3-4), not whatever
                // AD holds under the address. A source that reports none, or cannot be asked, leaves the address
                // re-resolve below exactly as it was: this entry point is not re-ordered, and its 13 callers keep working
                // while the Graph credential is unset.
                if (OnPremisesSidSource is not null)
                {
                    var sid = await ReadOnPremisesSidAsync(identity, recipient);
                    if (sid.Outcome == OnPremisesSidOutcome.Reported)
                    {
                        var (bySid, bySidStatus) = await BindBySidAsync(identity, recipient with { OnPremisesSid = sid.Sid }, sid.Sid!);
                        // ExchangeGuid stays ResolveExchangeTargetAsync's alone (aud-a2d, ValidatedMailbox.From).
                        return bySidStatus == ResolutionStatus.Resolved && bySid is not null
                            ? (bySid with { ExchangeGuid = null }, ResolutionStatus.Resolved)
                            : (null, bySidStatus == ResolutionStatus.Resolved ? ResolutionStatus.Unavailable : bySidStatus);
                    }
                }

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
    /// different object. aud-a2b hole 2; bound by SID since docs/ProtectedPrincipalIdentifiers-Plan.md C.2.
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
    /// - synced recipient: the principal is the one account in the forest whose objectSid is the
    ///   on-premises SID Microsoft Graph reports for the recipient (<see cref="IOnPremisesSidSource"/>),
    ///   read at a global catalog - so a mailbox in another domain resolves, and an account that
    ///   merely carries the mailbox's address in <c>mail</c> or <c>userPrincipalName</c> is never it
    ///   (aud-a2c, closed by construction: an address is not an identifier, a SID is). No such account
    ///   is <c>Unavailable</c>; two, or one that is not a user object, <c>Ambiguous</c>. If AD reads the
    ///   typed string as a DIFFERENT object (by objectGUID) the string names two objects -
    ///   <c>Ambiguous</c>.
    /// - synced recipient whose SID source answered with no SID: the canonical address, with its
    ///   one-match rule, logged ("bound by address") so its frequency is visible. A source that could
    ///   not be asked - no setting, a failed or throttled read - is <c>Unavailable</c>, never "no SID".
    /// - cloud-only recipient: an AD object for the same string is a second object - <c>Ambiguous</c>.
    /// - no recipient: an Exchange write cannot land anywhere, so AD's answer is the only object the
    ///   string names; neither directory knowing it is <c>NotFound</c>.
    /// - Exchange not asked, AD ambiguous or unavailable: refused, as everywhere else.
    /// </remarks>
    // virtual: the same test seam as ResolveWithExchangeFallbackAsync.
    public virtual async Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveExchangeTargetAsync(string identity)
    {
        // kd-88: its directory lookups - the typed string, the account by SID or by address - share the operation's one
        // credential fetch, where each fetched its own (4.1 s and 2.7 s on the dev log).
        using var operation = DirectoryReadOperation.Begin();

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

        // C.2 step 3: the on-premises SID of the account the recipient is synced from. Step 6: a source that could not be
        // asked refuses; only an answer with no SID falls to the address (step 5).
        var sid = await ReadOnPremisesSidAsync(identity, recipient);
        if (sid.Outcome == OnPremisesSidOutcome.NotAnswered)
        {
            _logger.LogWarning(
                "{Identity} is a directory-synced recipient whose on-premises SID could not be read - failing closed", identity);
            return (null, ResolutionStatus.Unavailable);
        }

        ResolvedDirectoryPrincipal? bound;
        ResolutionStatus boundStatus;
        if (sid.Outcome == OnPremisesSidOutcome.NoneReported)
        {
            _logger.LogWarning("no on-premises SID reported for {Identity}; bound by address", identity);
            (bound, boundStatus) = await BindByAddressAsync(identity, recipient, adPrincipal, adStatus);
        }
        else
        {
            (bound, boundStatus) = await BindBySidAsync(identity, recipient with { OnPremisesSid = sid.Sid }, sid.Sid!);
        }

        if (boundStatus != ResolutionStatus.Resolved || bound is null)
            return (null, boundStatus == ResolutionStatus.Resolved ? ResolutionStatus.Unavailable : boundStatus);

        // aud-a2b, unchanged: AD's reading of the typed string must be the object Exchange will write.
        if (adStatus == ResolutionStatus.Resolved && (adPrincipal is null || !IsSameDirectoryObject(adPrincipal, bound)))
        {
            _logger.LogWarning(
                "{Identity} resolves to one object in Active Directory and Exchange will write another "
                + "(canonical {Canonical}) - failing closed",
                identity, recipient.PrimarySmtpAddress);
            return (null, ResolutionStatus.Ambiguous);
        }

        return (bound, ResolutionStatus.Resolved);
    }

    /// <summary>
    /// C.2 step 3. Asks the SID source for the on-premises SID of the account <paramref name="recipient"/> is synced from,
    /// counted and timed for the check's timing line. No source - a service built without one - could not be asked.
    /// </summary>
    private async Task<OnPremisesSidAnswer> ReadOnPremisesSidAsync(string identity, ResolvedRecipient recipient)
    {
        var source = OnPremisesSidSource;
        if (source is null)
        {
            _logger.LogWarning(
                "No on-premises SID source is available to read the SID of {Identity} - failing closed", identity);
            return OnPremisesSidAnswer.NotAnswered("no on-premises SID source");
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        OnPremisesSidAnswer answer;
        try
        {
            answer = await source.ReadAsync(recipient.ExternalDirectoryObjectId ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The on-premises SID read for {Identity} failed", identity);
            answer = OnPremisesSidAnswer.NotAnswered("the read failed");
        }
        finally
        {
            DirectoryReadOperation.CountSidSourceRead(source.Name,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        if (answer.Outcome == OnPremisesSidOutcome.NotAnswered)
        {
            _logger.LogWarning(
                "The on-premises SID of {Identity} could not be read from {Source} ({Reason})",
                identity, source.Name, answer.Reason ?? "no reason given");
        }
        return answer;
    }

    /// <summary>
    /// C.2 step 4. The one user object in the forest whose objectSid is <paramref name="sid"/>, read at a global catalog,
    /// as the principal: its objectSid, objectGUID and DN, with the recipient's canonical address and mailbox GUID.
    /// </summary>
    private async Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> BindBySidAsync(
        string identity, ResolvedRecipient recipient, string sid)
    {
        var (answered, found) = await FindObjectsBySidAsync(sid);
        if (!answered)
            return (null, ResolutionStatus.Unavailable);

        var (principal, status, refusal) = SelectSidObject(sid, recipient, found);
        if (status != ResolutionStatus.Resolved)
        {
            _logger.LogWarning("{Identity} (canonical {Canonical}): {Refusal} - failing closed",
                identity, recipient.PrimarySmtpAddress, refusal);
        }
        return (principal, status);
    }

    /// <summary>
    /// C.2 step 5, for a synced recipient whose SID source answered with no SID: AD's one object under the canonical
    /// address, as the resolution read it before the SID binding. Its one-match rule is the resolution's own.
    /// </summary>
    private async Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> BindByAddressAsync(
        string identity, ResolvedRecipient recipient, ResolvedDirectoryPrincipal? adPrincipal, ResolutionStatus adStatus)
    {
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

        // aud-a2d: the mailbox Exchange will write, carried for the write to be bound to.
        return (canonical with { ExchangeGuid = recipient.ExchangeGuid }, ResolutionStatus.Resolved);
    }

    /// <summary>
    /// The C.2 step 4 decision, pure so every outcome is testable without a directory: <paramref name="found"/> is what
    /// the global catalog holds under <paramref name="sid"/>.
    /// </summary>
    /// <remarks>
    /// - None is <c>Unavailable</c>: Exchange names an on-premises account the forest does not hold. Never "not protected".
    /// - Two or more is <c>Ambiguous</c>: a SID names one object, so the forest is not answering as one.
    /// - One that is not a user object (<c>user</c> or <c>inetOrgPerson</c>; rooms and equipment are user objects) is
    ///   <c>Ambiguous</c>: a mailbox is synced from a user object, so the SID does not name the mailbox's account.
    /// - One read without its DN or objectGUID, or answering under another SID, is <c>Unavailable</c>: there is nothing to
    ///   bind a write to, or the read is not the one asked.
    /// </remarks>
    internal static (ResolvedDirectoryPrincipal? principal, ResolutionStatus status, string? refusal) SelectSidObject(
        string sid, ResolvedRecipient recipient, IReadOnlyList<DirectorySidObject> found)
    {
        if (found.Count == 0)
        {
            return (null, ResolutionStatus.Unavailable,
                "Exchange names an on-premises account the forest does not hold (no object carries the SID Graph reports)");
        }

        if (found.Count > 1)
            return (null, ResolutionStatus.Ambiguous, $"{found.Count} objects in the forest carry the SID Graph reports");

        var only = found[0];
        if (!IsUserObject(only.ObjectClass))
        {
            return (null, ResolutionStatus.Ambiguous,
                $"the object carrying the SID Graph reports is not a user object ({only.ObjectClass ?? "class not read"})");
        }

        if (string.IsNullOrWhiteSpace(only.DistinguishedName) || string.IsNullOrWhiteSpace(only.ObjectGuid))
            return (null, ResolutionStatus.Unavailable, "the object carrying the SID Graph reports was read without its DN or objectGUID");

        var wanted = ProtectedPrincipalIdEntry.CanonicalSid(sid);
        if (wanted is null || !string.Equals(ProtectedPrincipalIdEntry.CanonicalSid(only.ObjectSid), wanted, StringComparison.Ordinal))
            return (null, ResolutionStatus.Unavailable, "the global catalog answered with an object under another SID");

        return (new ResolvedDirectoryPrincipal(
            Source: "ProtectedPrincipalService-AD",
            DisplayName: only.DisplayName ?? recipient.PrimarySmtpAddress,
            UserPrincipalName: only.UserPrincipalName ?? recipient.PrimarySmtpAddress,
            SamAccountName: only.SamAccountName,
            // The mailbox's canonical address, which Exchange reported for it: a protected user row stored as that address
            // protects this mailbox, whatever the account's mail attribute holds.
            PrimarySmtpAddress: recipient.PrimarySmtpAddress,
            DistinguishedName: only.DistinguishedName,
            ObjectGuid: only.ObjectGuid,
            EntraObjectId: recipient.ExternalDirectoryObjectId,
            ExchangeGuid: recipient.ExchangeGuid,
            ObjectSid: wanted), ResolutionStatus.Resolved, null);
    }

    private static bool IsUserObject(string? objectClass) =>
        string.Equals(objectClass, "user", StringComparison.OrdinalIgnoreCase)
        || string.Equals(objectClass, "inetOrgPerson", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// C.2 step 4. Every object in the forest whose objectSid is <paramref name="sid"/>: one indexed equality read at a
    /// global catalog (<see cref="ADDirectorySearchService.BuildSidFilter"/>). <c>answered</c> is false when the read could
    /// not run - credential, throttle, no catalog, any error - and the list is then meaningless.
    /// </summary>
    /// <remarks>
    /// The catalog is discovered from the host's own forest (Invariant 7) and kept for later checks; a read that fails
    /// forgets it, so the next check discovers again. Same credential, throttle and fail-closed handling as
    /// <see cref="ResolveWithStatusAsync"/>, and inside an operation the same one credential fetch (kd-88).
    /// </remarks>
    // virtual: test seam, the same shape as ResolveWithStatusAsync.
    public virtual async Task<(bool answered, IReadOnlyList<DirectorySidObject> found)> FindObjectsBySidAsync(string sid)
    {
        if (DateTime.UtcNow - _lastCredentialFailure < CredentialFailureTtl)
        {
            _logger.LogDebug("Skipping the read by SID - credential recently failed");
            return (false, []);
        }

        var secretId = GetDirectoryReadSecretId();
        if (secretId == null)
        {
            _logger.LogDebug("Cannot read an object by SID - directory-read credential not configured");
            return (false, []);
        }

        var creds = await DirectoryReadSecretAsync(secretId.Value); // kd-88: the operation's one fetch
        if (creds == null)
        {
            _lastCredentialFailure = DateTime.UtcNow;
            _logger.LogWarning("Failed to retrieve directory-read credential for the read by SID");
            return (false, []);
        }

        try
        {
            if (!await _adThrottle.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                _logger.LogWarning("AD throttle timeout during the read by SID");
                return (false, []);
            }

            try
            {
                var found = await Task.Run(() => FindObjectsBySidAtCatalog(
                    sid, creds.Value.username, creds.Value.password, creds.Value.domain));
                return (true, found);
            }
            finally
            {
                _adThrottle.Release();
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _globalCatalog, null);
            _logger.LogWarning(ex, "The read by SID at the global catalog failed");
            return (false, []);
        }
    }

    // The global catalog the read by SID last reached, host:3268; null until discovered, and after a read that failed.
    private string? _globalCatalog;

    private IReadOnlyList<DirectorySidObject> FindObjectsBySidAtCatalog(string sid, string username, string password, string domain)
    {
        // kd-88: a warm session, lent for this read and given back.
        using var session = DirectorySessions.Lease(SessionWait);
        try
        {
            var directory = new PowerShellProtectedGroupDirectory(
                session.PowerShell, CreateCredential(username, password, domain), Volatile.Read(ref _globalCatalog));
            var found = directory.FindObjectsBySidAtCatalog(sid);
            Volatile.Write(ref _globalCatalog, directory.KnownGlobalCatalog);
            return found;
        }
        catch
        {
            session.Fail();
            throw;
        }
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
        // kd-88: a warm session, lent for this lookup and given back - no runspace or module import of its own.
        using var session = DirectorySessions.Lease(SessionWait);
        var ps = session.PowerShell;

        var credential = CreateCredential(username, password, domain);
        var escaped = EscapeLdapFilter(identity);
        var filter = $"(|(userPrincipalName={escaped})(mail={escaped})(sAMAccountName={escaped}))";

        ps.AddCommand("Get-ADUser")
          .AddParameter("LDAPFilter", filter)
          .AddParameter("Properties", new[] { "DisplayName", "UserPrincipalName", "SamAccountName", "mail", "DistinguishedName", "ObjectGUID", "objectSid" })
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var users = InvokeIn(session);
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
            EntraObjectId: null,
            // docs/ProtectedPrincipalIdentifiers-Plan.md C.2: what a user_sid row compares against.
            ObjectSid: ProtectedPrincipalIdEntry.CanonicalSid(
                AccountLockoutRemediationService.SidText(adUser.Properties["objectSid"]?.Value)));
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
        _repository.Save(config.ToData());

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

        var data = config.ToData();
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
        return new ProtectedPrincipalRulesForEditing(ProtectedPrincipalConfig.FromData(rules), RulesVersion(snapshot));
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
            UserIds = Sorted(snapshot.Data.UserIds),
            GroupIds = Sorted(snapshot.Data.GroupIds),
            OuIds = Sorted(snapshot.Data.OuIds),
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
        && SameSet(stored.GroupTargets, wanted.GroupTargets)
        && SameSet(stored.UserIds, wanted.UserIds)
        && SameSet(stored.GroupIds, wanted.GroupIds)
        && SameSet(stored.OuIds, wanted.OuIds);

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
                _repository.ImportIfMissing(parsed.ToData());
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

    /// <summary>
    /// Whether a value is a Secret ID as the admin page takes one: digits only, the first not zero, a number above zero that
    /// an <c>int</c> holds - no sign, no leading zero, no white space (aud-a26s). The admin page refuses a changed value this
    /// rejects. Whatever it accepts, the lookups read as the same ID (<see cref="GetDirectoryReadSecretId"/>), so nothing is
    /// stored that they would pass over for the fallback (aud-a26p); their read also takes "+42" and "0042", which this
    /// does not, so what is stored is what was meant.
    /// </summary>
    public static bool IsSecretId(string? value) =>
        !string.IsNullOrEmpty(value) && value[0] != '0' && value.All(char.IsAsciiDigit) && PositiveId(value) is not null;

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

    /// <summary>
    /// The decision <see cref="SaveDirectoryReadSecretId(string, string)"/> makes, under the same lock, writing nothing
    /// (aud-a28r). The admin page runs it before it writes the protection rules: the rules and the secret are two stores,
    /// so a secret changed elsewhere since the page loaded is caught before the rules land, refusing the whole save,
    /// rather than refused after them with the rules already saved.
    /// </summary>
    public ModuleConfigSaveOutcome CompareDirectoryReadSecretId(string loaded, string wanted) =>
        _moduleConfig.CompareChangedValues(
            ProtectedPrincipalsModuleKey,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [DirectoryReadSecretConfigKey] = loaded },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [DirectoryReadSecretConfigKey] = wanted });

    /// <summary>
    /// The Graph Directory Read Credential's Secret ID as the database holds it (<see cref="GraphDirectoryReadSecretConfigKey"/>),
    /// for the admin page: false when the ProtectedPrincipals module config cannot be read, so the page can say so rather
    /// than show the id as unset (the aud-a26k rule the directory-read secret follows). No fallback: the field is its own.
    /// </summary>
    public bool TryGetGraphDirectoryReadSecret(out string stored)
    {
        if (!_moduleConfig.TryGetModuleConfig(ProtectedPrincipalsModuleKey, out var values))
        {
            stored = "";
            return false;
        }

        stored = values.GetValueOrDefault(GraphDirectoryReadSecretConfigKey) ?? "";
        return true;
    }

    /// <summary>
    /// The admin page's write of the Graph Directory Read Credential's Secret ID: the rule
    /// <see cref="SaveDirectoryReadSecretId(string, string)"/> applies - written only while the stored value is still
    /// <paramref name="loaded"/>, decided under the write lock. Throws when the write fails.
    /// </summary>
    public ModuleConfigSaveOutcome SaveGraphDirectoryReadSecretId(string loaded, string wanted) =>
        _moduleConfig.SaveChangedValues(
            ProtectedPrincipalsModuleKey,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [GraphDirectoryReadSecretConfigKey] = loaded },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [GraphDirectoryReadSecretConfigKey] = wanted });

    /// <summary>
    /// The decision <see cref="SaveGraphDirectoryReadSecretId"/> makes, writing nothing - run before the protection rules
    /// are written, as <see cref="CompareDirectoryReadSecretId"/> is (aud-a28r).
    /// </summary>
    public ModuleConfigSaveOutcome CompareGraphDirectoryReadSecretId(string loaded, string wanted) =>
        _moduleConfig.CompareChangedValues(
            ProtectedPrincipalsModuleKey,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [GraphDirectoryReadSecretConfigKey] = loaded },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [GraphDirectoryReadSecretConfigKey] = wanted });

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

    /// <summary>
    /// The rows one base kind evaluates (docs/ProtectedPrincipalIdentifiers-Plan.md B.2): the legacy rows no identifier
    /// row pairs with - unmigrated, and enforced exactly as before - and the identifier rows, their identifiers canonical.
    /// </summary>
    /// <remarks>
    /// An identifier row whose identifier does not parse cannot be compared by it, and its companion is not evaluated; it
    /// is evaluated by its label instead, joining the legacy rows, as its companion would have been. A row that cannot
    /// answer the question it was written for falls back to the answer it gave before, never to none.
    /// </remarks>
    internal static (string[] Legacy, IReadOnlyList<ProtectedPrincipalIdEntry> Ids) RowsToEvaluate(
        string[]? legacyRows, string[]? idRows, Func<string?, string?> canonical)
    {
        legacyRows ??= [];
        idRows ??= [];
        var ids = new List<ProtectedPrincipalIdEntry>();
        var byLabel = new List<string>();
        foreach (var row in idRows)
        {
            if (ProtectedPrincipalIdEntry.Parse(row) is not { } entry)
                continue;

            if (canonical(entry.Identifier) is { } id)
                ids.Add(entry with { Identifier = id });
            else if (entry.Label.Length > 0)
                byLabel.Add(entry.Label);
        }

        return (ProtectedPrincipalIdEntry.Unpaired(legacyRows, idRows).Concat(byLabel).ToArray(), ids);
    }

    /// <summary>
    /// The entries the group check evaluates: unmigrated legacy Group rows as stored, and each group_sid row as its SID,
    /// which <see cref="ProtectedGroupEntry.Parse"/> reads as a SID entry. <paramref name="labels"/> maps each SID back to
    /// its row's label, so a matched rule names the group as the page does.
    /// </summary>
    private static List<string> GroupEntries(ProtectedPrincipalConfig cfg, out Dictionary<string, string> labels)
    {
        var (legacy, ids) = RowsToEvaluate(cfg.Groups, cfg.GroupIds, ProtectedPrincipalIdEntry.CanonicalSid);
        labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = legacy.ToList();
        foreach (var id in ids)
        {
            entries.Add(id.Identifier);
            labels.TryAdd(id.Identifier, id.Display);
        }
        return entries;
    }

    private static void CheckDirectUserMatches(ProtectedPrincipalConfig cfg, ResolvedDirectoryPrincipal target, List<string> matchedRules)
    {
        var (legacy, ids) = RowsToEvaluate(cfg.Users, cfg.UserIds, ProtectedPrincipalIdEntry.CanonicalSid);
        foreach (var protectedUser in legacy)
        {
            if (MatchesIdentity(protectedUser, target))
                matchedRules.Add($"User:{protectedUser}");
        }

        // A user_sid row is this object, compared by SID: another account carrying its address in mail or UPN is not it
        // (plan C.2). A target whose resolution did not read a SID is compared by the row's label, as its companion was -
        // the absence of an identifier on the TARGET is not evidence it is someone else.
        var targetSid = ProtectedPrincipalIdEntry.CanonicalSid(target.ObjectSid);
        foreach (var id in ids)
        {
            var matches = targetSid is not null
                ? string.Equals(id.Identifier, targetSid, StringComparison.OrdinalIgnoreCase)
                : id.Label.Length > 0 && MatchesIdentity(id.Label, target);
            if (matches)
                matchedRules.Add($"User:{id.Display}");
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

    /// <summary>
    /// Unmigrated OU rows by DN suffix, as always. ou_guid rows are compared by objectGUID in the directory phase of
    /// <see cref="CheckAsync"/>; with <paramref name="ouIdsByLabel"/> - the directory-free write-target check - by their
    /// label's DN suffix instead, the comparison their companion row gets from an older build.
    /// </summary>
    private static void CheckOuMatches(
        ProtectedPrincipalConfig cfg, ResolvedDirectoryPrincipal target, List<string> matchedRules, bool ouIdsByLabel)
    {
        if (string.IsNullOrEmpty(target.DistinguishedName))
            return;

        var (legacy, ids) = RowsToEvaluate(cfg.OrganizationalUnits, cfg.OuIds, ProtectedPrincipalIdEntry.CanonicalGuid);
        foreach (var ou in legacy)
        {
            if (IsAtOrBeneath(target.DistinguishedName, ou))
                matchedRules.Add($"OU:{ou}");
        }

        if (!ouIdsByLabel)
            return;

        foreach (var id in ids)
        {
            if (id.Label.Length > 0 && IsAtOrBeneath(target.DistinguishedName, id.Label))
                matchedRules.Add($"OU:{id.Display}");
        }
    }

    private static bool IsAtOrBeneath(string targetDn, string ou) =>
        targetDn.EndsWith("," + ou, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(targetDn, ou, StringComparison.OrdinalIgnoreCase);

    private async Task<(List<string> matches, bool checkFailed, string? failReason)> CheckGroupMembershipAsync(
        IReadOnlyList<string> groupEntries, IReadOnlyDictionary<string, string> groupLabels,
        IReadOnlyList<ProtectedPrincipalIdEntry> ouIds, ResolvedDirectoryPrincipal target)
    {
        var matches = new List<string>();

        if (DateTime.UtcNow - _lastCredentialFailure < CredentialFailureTtl)
            return (matches, true, "Directory-read credential recently failed. Retry shortly or check configuration.");

        var secretId = GetDirectoryReadSecretId();
        if (secretId == null)
        {
            _logger.LogError("Directory-read credential is not configured but protected groups or OUs are defined - configure it on the Admin Settings page");
            return (matches, true, "Protected-principal directory-read credential is not configured. Configure it on the Admin Settings page.");
        }

        var creds = await DirectoryReadSecretAsync(secretId.Value); // kd-88: the operation's one fetch
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
                    CheckGroupsInWarmSession(groupEntries, groupLabels, ouIds, target, creds.Value.username, creds.Value.password, creds.Value.domain));
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

    /// <summary>
    /// kd-88. The group check in a warm session - the ActiveDirectory module already imported - lent for this check and given
    /// back, where every check built a runspace and imported the module before its first read. A session a read failed in,
    /// or whose answer was incomplete, is thrown away rather than lent again.
    /// </summary>
    private (List<string> matches, bool expansionHadErrors) CheckGroupsInWarmSession(
        IReadOnlyList<string> protectedGroups, IReadOnlyDictionary<string, string> groupLabels,
        IReadOnlyList<ProtectedPrincipalIdEntry> ouIds, ResolvedDirectoryPrincipal target,
        string username, string password, string domain)
    {
        using var session = DirectorySessions.Lease(SessionWait);
        try
        {
            var outcome = CheckTransitiveGroupMembership(
                session.PowerShell, protectedGroups, groupLabels, ouIds, target, username, password, domain);
            if (outcome.expansionHadErrors)
                session.Fail();
            return outcome;
        }
        catch
        {
            session.Fail();
            throw;
        }
    }

    private (List<string> matches, bool expansionHadErrors) CheckTransitiveGroupMembership(
        PowerShell ps, IReadOnlyList<string> protectedGroups, IReadOnlyDictionary<string, string> groupLabels,
        IReadOnlyList<ProtectedPrincipalIdEntry> ouIds, ResolvedDirectoryPrincipal target,
        string username, string password, string domain)
    {
        var matches = new List<string>();

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
            DirectoryReadOperation.CountRead(); // kd-88: the timing line's count
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
        var incomplete = false;
        if (protectedGroups.Count > 0)
        {
            var outcome = ProtectedGroupMembership.Evaluate(protectedGroups, targetDn, directory, _logger);
            matches.AddRange(LabelGroupMatches(outcome, groupLabels));
            incomplete |= outcome.Incomplete;
        }

        // docs/ProtectedPrincipalIdentifiers-Plan.md C.2: one read of the target's containers, by DN, in its own domain.
        if (ouIds.Count > 0)
        {
            var (ouMatches, ouIncomplete) = ProtectedOuMembership.Evaluate(ouIds, targetDn, directory, _logger);
            matches.AddRange(ouMatches);
            incomplete |= ouIncomplete;
        }

        return (matches, incomplete);
    }

    /// <summary>
    /// docs/ProtectedPrincipalIdentifiers-Plan.md B.4. Looks each legacy row up once, inside ONE
    /// <see cref="DirectoryReadOperation"/>: one directory-read credential fetch and one warm session for the whole pass,
    /// however many rows it reads (kd-88). Never throws for a lookup that could not run: every row is then returned as
    /// <see cref="ProtectedPrincipalLeftReason.DirectoryUnavailable"/> with the cause, and nothing is written.
    /// </summary>
    async Task<IReadOnlyList<ProtectedPrincipalRowResolution>> IProtectedPrincipalLegacyResolver.ResolveLegacyRowsAsync(
        IReadOnlyList<ProtectedPrincipalLegacyRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
            return [];

        IReadOnlyList<ProtectedPrincipalRowResolution> AllLeft(string cause) => rows
            .Select(r => ProtectedPrincipalRowResolution.Left(r, ProtectedPrincipalLeftReason.DirectoryUnavailable, $"directory unavailable: {cause}"))
            .ToList();

        using var operation = DirectoryReadOperation.Begin();
        var secretId = GetDirectoryReadSecretId();
        if (secretId is null)
            return AllLeft("the directory-read credential is not configured (Admin Settings, Protected principals)");

        var creds = await DirectoryReadSecretAsync(secretId.Value); // kd-88: the pass's one fetch
        if (creds is null)
            return AllLeft("the directory-read credential could not be retrieved");

        if (!await _adThrottle.WaitAsync(SessionWait, cancellationToken))
            return AllLeft("the directory service is busy");

        try
        {
            return await Task.Run(() =>
            {
                using var session = DirectorySessions.Lease(SessionWait);
                try
                {
                    var directory = new PowerShellProtectedGroupDirectory(
                        session.PowerShell, CreateCredential(creds.Value.username, creds.Value.password, creds.Value.domain));
                    var resolved = ProtectedPrincipalIdentifierResolver.Resolve(rows, directory, cancellationToken);

                    // A session a read failed in is thrown away rather than lent again, as the group check's is.
                    if (resolved.Any(r => r.Reason == ProtectedPrincipalLeftReason.DirectoryUnavailable))
                        session.Fail();
                    return resolved;
                }
                catch
                {
                    session.Fail();
                    throw;
                }
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The protected-principal identifier pass could not read the directory");
            return AllLeft(ex.Message);
        }
        finally
        {
            _adThrottle.Release();
        }
    }

    /// <summary>A matched group_sid entry named by its row's label, as the page names it; every other match as it was.</summary>
    internal static IEnumerable<string> LabelGroupMatches(
        ProtectedGroupMembership.Result outcome, IReadOnlyDictionary<string, string> groupLabels) =>
        outcome.MatchedEntries.Select(entry =>
            "Group:" + (groupLabels.TryGetValue(entry, out var label) ? label : entry));

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
