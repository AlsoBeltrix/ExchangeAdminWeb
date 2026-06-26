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

    // Set when a legacy modules-enabled.json exists but cannot be parsed. We must NOT silently
    // fall through to an empty (healthy) DB and let modules default to EnabledByDefault — that
    // would downgrade the file world's fail-closed corrupt behavior during the upgrade window.
    // The corrupt file stays on disk, so this re-trips on every startup until it is repaired or
    // removed (an admin save is also blocked while it is set; see IsStoreCorrupt).
    private readonly bool _legacyFileCorrupt;

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
        _legacyFileCorrupt = ImportLegacyIfPresent(legacyPath);
    }

    /// <summary>
    /// Startup self-registration (SqliteConfigStore-Plan §3d): non-destructively seed a
    /// <see cref="module_enablement"/> row for every catalog module that has none yet, at the
    /// descriptor's <c>EnabledByDefault</c>. Existing rows are never modified, so this does NOT
    /// reintroduce the banned destructive startup write (the 2026-06-12 incident). System
    /// modules are always-on and not stored, so they are skipped. No-op (and intentionally so)
    /// when the store is corrupt — we must not seed over an unreadable/legacy-corrupt store.
    /// Returns the IDs newly seeded.
    /// </summary>
    public IReadOnlyList<string> SeedMissingModules()
    {
        if (_legacyFileCorrupt)
        {
            _logger.LogWarning("Skipping module enablement seeding — legacy store is corrupt (failing closed)");
            return Array.Empty<string>();
        }

        // If the store can't even be read, don't try to write to it.
        if (!_repository.TryGetAll(out _))
        {
            _logger.LogWarning("Skipping module enablement seeding — store is unreadable");
            return Array.Empty<string>();
        }

        var defaults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in _catalog.GetAll().Where(m => !m.IsSystemModule))
            defaults[module.Id] = module.EnabledByDefault;

        try
        {
            var seeded = _repository.SeedMissing(defaults);
            if (seeded.Count > 0)
                _logger.LogInformation("Seeded {Count} missing module enablement rows: {Modules}", seeded.Count, string.Join(", ", seeded));
            return seeded;
        }
        catch (Exception ex)
        {
            // Seeding is a non-essential convenience. A write failure here (read-only ACL,
            // exclusive/WAL lock, etc.) must NOT abort app startup — log and move on; the
            // fail-closed read paths handle whatever state the store is actually in.
            _logger.LogError(ex, "Module enablement seeding failed — continuing startup without seeding");
            return Array.Empty<string>();
        }
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
        // Corrupt if either the DB cannot be read (DB-integrity failure) OR an unparseable
        // legacy modules-enabled.json is still present (upgrade window — must stay fail-closed
        // until it is repaired/removed rather than fall through to an empty healthy DB).
        return _legacyFileCorrupt || !_repository.TryGetAll(out _);
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
        // FAIL-CLOSED: if the store cannot be read, OR an unparseable legacy file is still
        // present, return an explicit all-disabled map for every non-system module (NOT an empty
        // map — empty would let modules fall back to EnabledByDefault). This preserves the file
        // version's corrupt-store behavior, including during the upgrade window.
        if (!_legacyFileCorrupt && _repository.TryGetAll(out var state))
            return state;

        _logger.LogError("Module enablement store unreadable or legacy file corrupt - all modules disabled until fixed");
        var disabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in _catalog.GetAll().Where(m => !m.IsSystemModule))
            disabled[module.Id] = false;
        return disabled;
    }

    // One-time import of legacy modules-enabled.json into module_enablement, then archive the
    // file (SqliteConfigStore-Plan §4). Only fills if the table is empty (DB wins). Returns true
    // if the legacy file exists but is unparseable: it is left in place (not archived) AND the
    // service treats the store as corrupt (fail closed) until it is repaired/removed, so the
    // upgrade window does not silently downgrade to EnabledByDefault.
    private bool ImportLegacyIfPresent(string legacyPath)
    {
        try
        {
            if (!File.Exists(legacyPath))
                return false;

            Dictionary<string, bool>? legacy;
            try
            {
                legacy = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(legacyPath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Legacy modules-enabled.json is unparseable; failing closed (all modules disabled) until it is repaired or removed");
                return true;
            }

            if (legacy is { Count: > 0 })
            {
                try
                {
                    _repository.ImportIfMissing(legacy);
                }
                catch (Exception ex)
                {
                    // The file parsed fine but could not be committed to the DB (e.g. SQLite busy).
                    // Do NOT archive and do NOT fall through to a readable-but-empty store — that
                    // would let modules silently default to EnabledByDefault. Fail closed (all
                    // modules disabled); the file stays on disk so the next startup retries.
                    _logger.LogError(ex, "Failed to import legacy modules-enabled.json into the store — failing closed (all modules disabled) until import succeeds");
                    return true;
                }
            }

            LegacyConfigImport.ArchiveFile(legacyPath, _logger);
            return false;
        }
        catch (Exception ex)
        {
            // Reached only if reading the file itself failed (not a parse error — those return
            // true above). A valid file we could not even read must also fail closed.
            _logger.LogError(ex, "Failed to process legacy modules-enabled.json — failing closed (all modules disabled)");
            return true;
        }
    }
}
