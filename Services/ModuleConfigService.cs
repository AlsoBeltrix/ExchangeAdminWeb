using System.Text.Encodings.Web;
using System.Text.Json;
using ExchangeAdminWeb.Modules;

namespace ExchangeAdminWeb.Services;

public class ModuleConfigService
{
    private readonly string _configDir;
    private readonly string _legacyFilePath;
    private readonly ModuleCatalog _catalog;
    private readonly ILogger<ModuleConfigService> _logger;
    private readonly object _writeLock = new();
    private bool _legacyMigrated;

    public ModuleConfigService(ModuleCatalog catalog, IWebHostEnvironment env, ILogger<ModuleConfigService> logger)
    {
        _catalog = catalog;
        _logger = logger;
        _configDir = Path.Combine(env.ContentRootPath, "config");
        _legacyFilePath = Path.Combine(_configDir, "module-config.json");
        Directory.CreateDirectory(_configDir);
    }

    public event Action<string>? ConfigSaved;

    public bool HasConfigFile => HasModuleConfigFile(null);
    public bool IsCorrupt => IsModuleCorrupt(null);

    public bool HasModuleConfigFile(string? moduleId)
    {
        if (moduleId != null)
            return File.Exists(GetModuleConfigPath(moduleId));

        foreach (var m in _catalog.GetAll())
        {
            if (File.Exists(GetModuleConfigPath(m.Id)))
                return true;
        }
        return File.Exists(_legacyFilePath);
    }

    public bool IsModuleCorrupt(string? moduleId)
    {
        if (moduleId != null)
        {
            var path = GetModuleConfigPath(moduleId);
            if (!File.Exists(path)) return false;
            try
            {
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                return false;
            }
            catch { return true; }
        }

        foreach (var m in _catalog.GetAll())
        {
            if (IsModuleCorrupt(m.Id)) return true;
        }
        return false;
    }

    public string? GetValue(string moduleId, string key)
    {
        EnsureLegacyMigrated();
        var config = ReadModuleConfig(moduleId);
        return config.TryGetValue(key, out var value) ? value : null;
    }

    public Dictionary<string, string> GetModuleConfig(string moduleId)
    {
        EnsureLegacyMigrated();
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
            var path = GetModuleConfigPath(moduleId);
            var tempPath = Path.Combine(_configDir, $"module-config-{moduleId}.{Guid.NewGuid():N}.tmp");

            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var json = JsonSerializer.Serialize(values, options);
                File.WriteAllText(tempPath, json);

                if (File.Exists(path))
                    File.Replace(tempPath, path, null);
                else
                    File.Move(tempPath, path);

                _logger.LogInformation("Module config for {Module} saved to {Path}", moduleId, Path.GetFileName(path));
            }
            finally
            {
                if (File.Exists(tempPath))
                    try { File.Delete(tempPath); } catch { }
            }
        }

        ConfigSaved?.Invoke(moduleId);
    }

    private string GetModuleConfigPath(string moduleId) =>
        Path.Combine(_configDir, $"module-config-{moduleId}.json");

    private Dictionary<string, string> ReadModuleConfig(string moduleId)
    {
        var path = GetModuleConfigPath(moduleId);
        if (!File.Exists(path))
            return new(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Module config file corrupt for {Module}: {Path}", moduleId, Path.GetFileName(path));
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void EnsureLegacyMigrated()
    {
        if (_legacyMigrated || !File.Exists(_legacyFilePath))
        {
            _legacyMigrated = true;
            return;
        }

        lock (_writeLock)
        {
            if (_legacyMigrated) return;

            try
            {
                var json = File.ReadAllText(_legacyFilePath);
                var legacy = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);
                if (legacy == null)
                {
                    _legacyMigrated = true;
                    return;
                }

                foreach (var (moduleId, values) in legacy)
                {
                    var perModulePath = GetModuleConfigPath(moduleId);
                    if (File.Exists(perModulePath))
                        continue;

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    File.WriteAllText(perModulePath, JsonSerializer.Serialize(values, options));
                    _logger.LogInformation("Migrated config for {Module} from legacy module-config.json to {File}", moduleId, Path.GetFileName(perModulePath));
                }

                var backupPath = Path.Combine(_configDir, $"module-config.pre-migration.{DateTime.UtcNow:yyyyMMddHHmmss}.json");
                File.Move(_legacyFilePath, backupPath);
                _logger.LogInformation("Legacy module-config.json backed up to {Backup}", Path.GetFileName(backupPath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to migrate legacy module-config.json — per-module files will be created on next save");
            }

            _legacyMigrated = true;
        }
    }
}
