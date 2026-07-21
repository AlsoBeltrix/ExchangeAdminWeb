using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

public class ModuleConfigService
{
    private readonly ModuleConfigRepository _repository;
    private readonly ModuleCatalog _catalog;
    private readonly ILogger<ModuleConfigService> _logger;
    private readonly object _writeLock = new();

    public ModuleConfigService(ModuleCatalog catalog, IWebHostEnvironment env, ModuleConfigRepository repository, ILogger<ModuleConfigService> logger)
    {
        _catalog = catalog;
        _logger = logger;
        _repository = repository;

        var configDir = Path.Combine(env.ContentRootPath, "config");
        ImportLegacyConfig(configDir);
    }

    public event Action<string>? ConfigSaved;

    public bool HasConfigFile => HasModuleConfigFile(null);
    public bool IsCorrupt => IsModuleCorrupt(null);

    public bool HasModuleConfigFile(string? moduleId)
    {
        // Name kept for API compatibility; "config file" now means "has config rows in the DB".
        if (moduleId != null)
            return _repository.HasModule(moduleId);

        return _repository.HasAny();
    }

    public bool IsModuleCorrupt(string? moduleId)
    {
        // File-parse corruption is gone with per-row storage; "corrupt" now means the store
        // cannot be read at all (DB-integrity failure). Preserved as a guard so the admin-page
        // refuse-to-save behavior keeps working.
        if (moduleId != null)
            return !_repository.TryReadModule(moduleId, out _);

        foreach (var m in _catalog.GetAll())
        {
            if (IsModuleCorrupt(m.Id)) return true;
        }
        return false;
    }

    public string? GetValue(string moduleId, string key)
    {
        var config = ReadModuleConfig(moduleId);
        return config.TryGetValue(key, out var value) ? value : null;
    }

    public Dictionary<string, string> GetModuleConfig(string moduleId)
    {
        return ReadModuleConfig(moduleId);
    }

    public bool IsModuleConfigured(string moduleId)
    {
        var module = _catalog.GetById(moduleId);
        if (module == null || module.ConfigFields.Count == 0) return true;

        var config = GetModuleConfig(moduleId);
        return module.ConfigFields
            .Where(f => f.Required)
            .All(f => config.TryGetValue(f.Key, out var val) && !string.IsNullOrWhiteSpace(val));
    }

    public void SaveModuleConfig(string moduleId, Dictionary<string, string> values)
    {
        lock (_writeLock)
        {
            _repository.SaveModule(moduleId, values);
            _logger.LogInformation("Module config for {Module} saved", moduleId);
        }

        ConfigSaved?.Invoke(moduleId);
    }

    /// <summary>
    /// One-time remap of the renamed Graph credential key: for every catalog module that declares
    /// a <c>GraphDelineaSecretId</c> ConfigField, move a value stranded under the old
    /// <c>DelineaSecretId</c> key to the new key (and drop the stale row). Catalog-driven so it is
    /// scoped to Graph modules only - on-prem modules that legitimately use <c>DelineaSecretId</c>
    /// as their current key are never touched. Idempotent and non-destructive (never overwrites an
    /// existing new-key value); a module with nothing to remap performs no write. Returns the IDs
    /// actually changed. See docs/GraphSecretKeyMigration-Plan.md.
    /// </summary>
    public IReadOnlyList<string> MigrateGraphSecretKeys()
    {
        const string oldKey = "DelineaSecretId";
        const string newKey = "GraphDelineaSecretId";

        var changed = new List<string>();
        foreach (var module in _catalog.GetAll())
        {
            if (!module.ConfigFields.Any(f => string.Equals(f.Key, newKey, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                if (_repository.RemapKey(module.Id, oldKey, newKey))
                    changed.Add(module.Id);
            }
            catch (Exception ex)
            {
                // Best-effort repair; a write failure (locked store, etc.) must not abort startup.
                // The service-side read still works on whichever key holds the value until then.
                _logger.LogError(ex, "Graph secret key remap failed for {Module}", module.Id);
            }
        }

        if (changed.Count > 0)
            _logger.LogInformation("Migrated stranded Graph secret key for {Count} module(s): {Modules}", changed.Count, string.Join(", ", changed));

        return changed;
    }

    private Dictionary<string, string> ReadModuleConfig(string moduleId)
    {
        // Fail-open: return an empty config on a read error, matching the file version.
        try
        {
            return _repository.GetModule(moduleId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read module config for {Module}", moduleId);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    // One-time import of legacy config into module_config, then archive the files
    // (SqliteConfigStore-Plan Section 4). Folds in BOTH legacy shapes: the per-module
    // module-config-{Id}.json files AND the older single module-config.json (module -> values).
    // Only modules absent from the DB are imported, so existing DB state always wins.
    private void ImportLegacyConfig(string configDir)
    {
        try
        {
            if (!Directory.Exists(configDir))
                return;

            // Per-module files: module-config-{ModuleId}.json
            foreach (var file in Directory.GetFiles(configDir, "module-config-*.json"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var moduleId = fileName["module-config-".Length..];
                if (string.IsNullOrWhiteSpace(moduleId))
                    continue;

                try
                {
                    var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                    // Import even an empty {} file: in the file world its mere existence counted
                    // as "configured" and suppressed the appsettings fallback, so it must mark
                    // presence here too.
                    if (values != null)
                        _repository.ImportModuleIfMissing(moduleId, values);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping unparseable legacy module config {File}", Path.GetFileName(file));
                    continue; // leave an unparseable file in place rather than archive it
                }

                LegacyConfigImport.ArchiveFile(file, _logger);
            }

            // Older single-file shape: module-config.json (module -> key -> value)
            var legacyPath = Path.Combine(configDir, "module-config.json");
            if (File.Exists(legacyPath))
            {
                try
                {
                    var legacy = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(legacyPath));
                    if (legacy is { Count: > 0 })
                    {
                        foreach (var (moduleId, values) in legacy)
                            _repository.ImportModuleIfMissing(moduleId, values);
                    }
                    LegacyConfigImport.ArchiveFile(legacyPath, _logger);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping unparseable legacy module-config.json");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import legacy module config");
        }
    }
}
