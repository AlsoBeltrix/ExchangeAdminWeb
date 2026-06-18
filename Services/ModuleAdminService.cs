using System.Security.Claims;
using System.Text.Json;
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

    public bool IsModuleAdmin(string moduleId, ClaimsPrincipal user)
    {
        var groups = GetModuleAdmins(moduleId);
        if (groups.Length == 0) return false;

        var roleClaims = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

        foreach (var group in groups)
        {
            var normalizedGroup = group.Contains('\\')
                ? group.Split('\\')[1]
                : group;

            if (user.IsInRole(group)
                || user.IsInRole(normalizedGroup)
                || roleClaims.Any(r => r.Equals(group, StringComparison.OrdinalIgnoreCase))
                || roleClaims.Any(r => r.Equals(normalizedGroup, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
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
    // archive the file (SqliteConfigStore-Plan §4). Only fills modules absent from the DB.
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
