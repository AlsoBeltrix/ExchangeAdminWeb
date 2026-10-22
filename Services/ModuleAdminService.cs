using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

public class ModuleAdminService
{
    private readonly ModuleAdminRepository _repository;
    private readonly ILogger<ModuleAdminService> _logger;
    private readonly object _writeLock = new();

    public ModuleAdminService(IWebHostEnvironment env, ModuleAdminRepository repository, ILogger<ModuleAdminService> logger)
    {
        _logger = logger;
        _repository = repository;

        var legacyPath = Path.Combine(env.ContentRootPath, "config", "module-admins.json");
        ImportLegacyIfPresent(legacyPath);
    }

    /// <summary>
    /// True when <paramref name="user"/> holds a group SID granted module-admin rights for
    /// <paramref name="moduleId"/>. Fail closed: no grants, no usable grants, or no group claims
    /// all deny.
    /// </summary>
    /// <remarks>
    /// <b>This store is SIDs, and the comparison is EXACT.</b> It previously stripped the domain
    /// (<c>group.Split('\\')[1]</c>) and accepted a bare-name match through
    /// <c>WindowsPrincipal.IsInRole</c>, which made a grant for one domain's <c>Ops</c>
    /// satisfiable by a member of a different domain's <c>Ops</c> - in the field that decides who
    /// can reach a module's configuration and the AD attribute allowlist. Audit finding A1,
    /// 2026-10-16; the same defect <c>section_access</c> fixed in
    /// <c>docs/SectionAccessSidStorage-Plan.md</c>, which this store was simply missed by.
    ///
    /// <see cref="GroupMembershipChecker.UsableSidsOnly"/> is not optional and not belt-and-braces.
    /// Review finding <c>sid-1</c> measured it: exact comparison ALONE does not make an unmigrated
    /// row fail closed, because <c>IsInRole</c> resolves names as well as SIDs and a role claim can
    /// carry a name too. Filtering to SIDs is what makes a legacy name-valued grant inert rather
    /// than loosely matched.
    ///
    /// <b>A legacy name-valued grant therefore stops authorizing the moment this ships</b> - that
    /// is the owner's ruling of 2026-10-19 (refuse rather than silently honour), and it is why the
    /// list of non-resolving grants goes to the owner before deployment. Do NOT re-add a name
    /// fallback here to soften it; that reopens the hole this closes.
    ///
    /// Contrast <c>Security:AdminGroups</c> in appsettings, which must NOT be filtered this way:
    /// no migration converts it, it ships as <c>DOMAIN\Name</c>, and filtering it locks every
    /// admin out of the admin page (review finding <c>sidf-1</c>). Different store, different rule.
    /// </remarks>
    public bool IsModuleAdmin(string moduleId, ClaimsPrincipal user)
    {
        var usableGrants = GroupMembershipChecker.UsableSidsOnly(GetModuleAdmins(moduleId));
        if (usableGrants.Count == 0) return false;

        return GroupMembershipChecker.IsMemberOfAny(
            GroupMembershipChecker.ExtractGroupClaims(user), usableGrants);
    }

    /// <summary>
    /// Stored module-admin grants that are not usable group SIDs, by module. Empty once the store
    /// is migrated.
    /// </summary>
    /// <remarks>
    /// These grants authorize NOTHING - see <see cref="IsModuleAdmin"/>. This exists so that fact
    /// is reportable before anyone is surprised by it, rather than discovered when an admin cannot
    /// open their own module.
    /// </remarks>
    public IReadOnlyDictionary<string, string[]> GetUnusableGrants()
        => TryGetUnusableGrants(out var unusable)
            ? unusable
            : throw new InvalidOperationException(
                "Module-admin grants could not be read, so the unusable-grant inventory is unknown. "
                + "Do not treat this as an empty result.");

    /// <summary>
    /// Stored module-admin grants that are not usable group SIDs, by module.
    /// Returns false when the store could not be read - in which case
    /// <paramref name="unusableGrants"/> is empty and means NOTHING.
    /// </summary>
    /// <remarks>
    /// The out-parameter shape is the finding. This inventory is the deployment check for
    /// aud-a1, and a swallowed read error previously made it report zero unusable grants -
    /// the same answer as a fully migrated store. A report that cannot fail visibly is not a
    /// check. <see cref="GetUnusableGrants"/> throws rather than returning the ambiguous value.
    /// </remarks>
    public bool TryGetUnusableGrants(out IReadOnlyDictionary<string, string[]> unusableGrants)
    {
        var unusable = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        unusableGrants = unusable;

        if (!TryReadConfig(out var config))
            return false;

        foreach (var (moduleId, groups) in config)
        {
            var bad = groups
                .Where(g => !SectionAccessGroupIdentity.IsUsableGroupSid(g))
                .ToArray();

            if (bad.Length > 0)
                unusable[moduleId] = bad;
        }

        return true;
    }

    public bool IsModuleAdminForAny(ClaimsPrincipal user)
    {
        var allAdmins = ReadConfig();
        return allAdmins.Any(kvp => kvp.Value.Length > 0 && IsModuleAdmin(kvp.Key, user));
    }

    public string[] GetModuleAdmins(string moduleId)
    {
        var config = ReadConfig();
        return config.TryGetValue(moduleId, out var groups) ? groups : Array.Empty<string>();
    }

    /// <summary>
    /// Every module's stored grants, by module. Returns false when the store could not be read - in
    /// which case <paramref name="grants"/> is empty and means nothing.
    /// </summary>
    /// <remarks>
    /// For circuit revalidation (aud-a29d), the same distinction <see cref="TryGetUnusableGrants"/>
    /// draws: an unreadable store must not look like a store that grants nothing.
    /// </remarks>
    public bool TryGetAllGrants(out IReadOnlyDictionary<string, string[]> grants)
    {
        var readable = TryReadConfig(out var config);
        grants = config;
        return readable;
    }

    internal void SetModuleAdmins(string moduleId, string[] groups)
    {
        lock (_writeLock)
        {
            _repository.SetForModule(moduleId, groups);
            _logger.LogInformation("Module admins for {Module} saved", moduleId);
        }
    }

    /// <summary>
    /// The Module Configuration page's save (aud-a28n): adds and removes only the groups named, each decided
    /// against the module's rows under the write lock; groups not named are not touched. Throws when the
    /// write fails.
    /// </summary>
    public ModuleAdminChangeOutcome ApplyAdminChanges(string moduleId, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove)
    {
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);

        lock (_writeLock)
        {
            var outcome = _repository.ApplyChanges(moduleId, add, remove);
            if (outcome.Written)
                _logger.LogInformation("Module admins for {Module} changed ({Before} -> {After} group(s))", moduleId, outcome.Before.Length, outcome.After.Length);
            return outcome;
        }
    }

    /// <summary>
    /// Every module's grants, or an empty map when the store cannot be read.
    /// </summary>
    /// <remarks>
    /// The old comment here called this "fail-open", which misdescribed it: an empty map means
    /// no grants, and no grants DENIES. For authorization that is the right direction and it is
    /// why <see cref="IsModuleAdmin"/> keeps using this.
    ///
    /// It is the wrong direction for anything that REPORTS, and that is what
    /// <see cref="TryReadConfig"/> exists for - an unreadable store and a clean store are the
    /// same value here, and a reporting caller must be able to tell them apart.
    /// </remarks>
    private Dictionary<string, string[]> ReadConfig()
        => TryReadConfig(out var config) ? config : new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads every module's grants, reporting whether the read actually succeeded.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="ReadConfig"/> on a review finding against aud-a1: the
    /// deployment check that lists unusable grants was built on a swallowed read error, so an
    /// unreadable SQLite file produced "zero unusable grants" - indistinguishable from a clean
    /// inventory, in the one report whose entire job is to say what will stop authorizing.
    /// </remarks>
    private bool TryReadConfig(out Dictionary<string, string[]> config)
    {
        try
        {
            config = _repository.GetAll();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read module admins from config store");
            config = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            return false;
        }
    }

    // One-time import of the legacy module-admins.json into the module_admins table, then
    // archive the file (SqliteConfigStore-Plan Section 4). Only fills modules absent from the DB.
    private void ImportLegacyIfPresent(string legacyPath)
    {
        try
        {
            if (!File.Exists(legacyPath))
                return;

            var json = File.ReadAllText(legacyPath);
            var legacy = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
            if (legacy is { Count: > 0 })
                _repository.ImportIfMissing(legacy);

            LegacyConfigImport.ArchiveFile(legacyPath, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import legacy module-admins.json");
        }
    }
}
