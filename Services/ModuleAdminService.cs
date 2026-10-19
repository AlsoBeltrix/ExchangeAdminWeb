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
    {
        var unusable = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var (moduleId, groups) in ReadConfig())
        {
            var bad = groups
                .Where(g => !SectionAccessGroupIdentity.IsUsableGroupSid(g))
                .ToArray();

            if (bad.Length > 0)
                unusable[moduleId] = bad;
        }

        return unusable;
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

    public void SetModuleAdmins(string moduleId, string[] groups)
    {
        lock (_writeLock)
        {
            _repository.SetForModule(moduleId, groups);
            _logger.LogInformation("Module admins for {Module} saved", moduleId);
        }
    }

    private Dictionary<string, string[]> ReadConfig()
    {
        // Fail-open (silent): on any read error return an empty map, preserving the file
        // version's behavior. With a DB this is effectively never hit, but the catch keeps the
        // contract.
        try
        {
            return _repository.GetAll();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read module admins from config store");
            return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
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
