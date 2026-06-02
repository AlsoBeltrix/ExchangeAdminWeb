using System.Text.Json;
using System.Text.Json.Nodes;
using ExchangeAdminWeb.Modules;

namespace ExchangeAdminWeb.Services;

public class ModuleEnablementService
{
    private readonly string _configFilePath;
    private readonly ModuleCatalog _catalog;
    private readonly ModuleConfigService _moduleConfig;
    private readonly IConfiguration _config;
    private readonly ILogger<ModuleEnablementService> _logger;
    private readonly object _writeLock = new();
    private bool _migrationChecked;

    public ModuleEnablementService(
        ModuleCatalog catalog,
        IWebHostEnvironment env,
        ModuleConfigService moduleConfig,
        IConfiguration config,
        ILogger<ModuleEnablementService> logger)
    {
        _catalog = catalog;
        _moduleConfig = moduleConfig;
        _config = config;
        _logger = logger;
        var configDir = Path.Combine(env.ContentRootPath, "config");
        _configFilePath = Path.Combine(configDir, "modules-enabled.json");
        Directory.CreateDirectory(configDir);
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
        RunUpgradeMigration();

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

            var configDir = Path.GetDirectoryName(_configFilePath)!;
            var tempPath = Path.Combine(configDir, $"modules-enabled.{Guid.NewGuid():N}.tmp");

            try
            {
                var json = JsonSerializer.Serialize(toSave, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tempPath, json);

                var validation = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(tempPath));
                if (validation == null)
                    throw new InvalidOperationException("Generated enablement file failed validation");

                if (File.Exists(_configFilePath))
                    File.Replace(tempPath, _configFilePath, null);
                else
                    File.Move(tempPath, _configFilePath);

                _logger.LogInformation("Module enablement saved to {Path}", _configFilePath);
            }
            finally
            {
                if (File.Exists(tempPath))
                    try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private void RunUpgradeMigration()
    {
        if (_migrationChecked) return;
        _migrationChecked = true;

        if (!File.Exists(_configFilePath))
            return;

        try
        {
            var json = File.ReadAllText(_configFilePath);
            var state = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
            if (state == null || state.ContainsKey("ExchangeOnline"))
                return;

            var hasExoConfig = !string.IsNullOrWhiteSpace(_moduleConfig.GetValue("ExchangeOnline", "AppId"))
                            || !string.IsNullOrWhiteSpace(_config["ExchangeOnline:AppId"]);

            lock (_writeLock)
            {
                var reRead = File.ReadAllText(_configFilePath);
                var reState = JsonSerializer.Deserialize<Dictionary<string, bool>>(reRead);
                if (reState == null || reState.ContainsKey("ExchangeOnline")) return;

                reState["ExchangeOnline"] = hasExoConfig;
                var updated = JsonSerializer.Serialize(reState, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configFilePath, updated);
            }

            if (hasExoConfig)
                _logger.LogInformation("Auto-enabled ExchangeOnline: existing EXO config detected");
            else
                _logger.LogInformation("ExchangeOnline disabled: no EXO config found (fresh install)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skipping ExchangeOnline upgrade migration - enablement file may be corrupt");
        }
    }

    private Dictionary<string, bool> ReadState()
    {
        if (!File.Exists(_configFilePath))
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(_configFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(json)
                ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Module enablement file corrupt - all modules disabled until fixed");
            var disabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var module in _catalog.GetAll().Where(m => !m.IsSystemModule))
                disabled[module.Id] = false;
            return disabled;
        }
    }
}
