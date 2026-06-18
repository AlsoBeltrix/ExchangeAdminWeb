using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

public class ModuleEnablementService
{
    private readonly ModuleEnablementRepository _repository;
    private readonly ModuleCatalog _catalog;
    private readonly ModuleConfigService _moduleConfig;
    private readonly IConfiguration _config;
    private readonly ILogger<ModuleEnablementService> _logger;
    private readonly object _writeLock = new();
    private bool _startupCheckDone;

    public ModuleEnablementService(
        ModuleCatalog catalog,
        IWebHostEnvironment env,
        ModuleConfigService moduleConfig,
        ModuleEnablementRepository repository,
        IConfiguration config,
        ILogger<ModuleEnablementService> logger)
    {
        _catalog = catalog;
        _moduleConfig = moduleConfig;
        _repository = repository;
        _config = config;
        _logger = logger;

        var legacyPath = Path.Combine(env.ContentRootPath, "config", "modules-enabled.json");
        ImportLegacyIfPresent(legacyPath);
    }

    /// <summary>
    /// Returns effective enabled state: checks parent cascade via DependsOn.
    /// If the parent module is not effectively enabled, the child is effectively disabled.
    /// </summary>
    public bool IsModuleEnabled(string moduleId)
    {
        var module = _catalog.GetById(moduleId);
        if (module == null) return false;
        if (module.IsSystemModule) return true;

        // Check parent cascade
        if (module.DependsOn != null && !IsModuleEnabled(module.DependsOn))
            return false;

        return IsModuleRawEnabled(moduleId);
    }

    /// <summary>
    /// Returns the raw toggle state without checking parent dependencies.
    /// Used by the Admin Settings UI to display and save toggle states independently.
    /// </summary>
    public bool IsModuleRawEnabled(string moduleId)
    {
        WarnIfExchangeOnlineUnset();

        var module = _catalog.GetById(moduleId);
        if (module == null) return false;
        if (module.IsSystemModule) return true;

        var state = ReadState();
        if (state.TryGetValue(moduleId, out var enabled))
            return enabled;

        return module.EnabledByDefault;
    }

    public Dictionary<string, bool> GetAllEnablement()
    {
        WarnIfExchangeOnlineUnset();

        var state = ReadState();
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in _catalog.GetAll())
        {
            if (module.IsSystemModule)
            {
                result[module.Id] = true;
                continue;
            }

            result[module.Id] = state.TryGetValue(module.Id, out var enabled)
                ? enabled
                : module.EnabledByDefault;
        }

        return result;
    }

    /// <summary>
    /// True when modules-enabled.json exists but cannot be parsed. Admin pages use this
    /// to show an explicit error and refuse to save instead of rendering the all-disabled
    /// fallback as blank editable state (blank-render-save trap, incident 2026-06-12).
    /// </summary>
    public bool IsStoreCorrupt()
    {
        // Post-SQLite, "corrupt" means the store cannot be read at all (a DB-integrity
        // failure), not an unparseable file. A healthy-but-empty store is NOT corrupt.
        return !_repository.TryGetAll(out _);
    }

    public void SaveEnablement(Dictionary<string, bool> enablement)
    {
        lock (_writeLock)
        {
            var toSave = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var module in _catalog.GetAll().Where(m => !m.IsSystemModule))
            {
                if (enablement.TryGetValue(module.Id, out var enabled))
                    toSave[module.Id] = enabled;
                else
                    toSave[module.Id] = module.EnabledByDefault;
            }

            _repository.SaveAll(toSave);
            _logger.LogInformation("Module enablement saved");
        }
    }

    // Read-only startup check. Enablement state is written ONLY by SaveEnablement from
    // Admin Settings — never at startup (owner direction, incident 2026-06-12).
    private void WarnIfExchangeOnlineUnset()
    {
        if (_startupCheckDone) return;
        _startupCheckDone = true;

        try
        {
            var state = ReadState();
            if (state.ContainsKey("ExchangeOnline"))
                return;

            var hasExoConfig = !string.IsNullOrWhiteSpace(_moduleConfig.GetValue("ExchangeOnline", "AppId"))
                            || !string.IsNullOrWhiteSpace(_config["ExchangeOnline:AppId"]);

            if (hasExoConfig)
                _logger.LogWarning("ExchangeOnline has EXO config but no enablement entry; it stays disabled (and dependent modules with it) until an admin saves enablement from Admin Settings");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skipping ExchangeOnline enablement check");
        }
    }

    private Dictionary<string, bool> ReadState()
    {
        // FAIL-CLOSED: if the store cannot be read, return an explicit all-disabled map for
        // every non-system module (NOT an empty map — empty would let modules fall back to
        // EnabledByDefault). This preserves the file version's corrupt-store behavior.
        if (_repository.TryGetAll(out var state))
            return state;

        _logger.LogError("Module enablement store unreadable - all modules disabled until fixed");
        var disabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in _catalog.GetAll().Where(m => !m.IsSystemModule))
            disabled[module.Id] = false;
        return disabled;
    }

    // One-time import of legacy modules-enabled.json into module_enablement, then archive the
    // file (SqliteConfigStore-Plan §4). Only fills if the table is empty (DB wins). An
    // unparseable legacy file is left in place (not archived) so a corrupt file is not silently
    // discarded.
    private void ImportLegacyIfPresent(string legacyPath)
    {
        try
        {
            if (!File.Exists(legacyPath))
                return;

            Dictionary<string, bool>? legacy;
            try
            {
                legacy = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(legacyPath));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Legacy modules-enabled.json is unparseable; leaving it in place, not importing");
                return;
            }

            if (legacy is { Count: > 0 })
                _repository.ImportIfMissing(legacy);

            LegacyConfigImport.ArchiveFile(legacyPath, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import legacy modules-enabled.json");
        }
    }
}
