using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExchangeAdminWeb.Modules;

namespace ExchangeAdminWeb.Services;

public class ModuleConfigService
{
    private readonly string _configFilePath;
    private readonly ModuleCatalog _catalog;
    private readonly ILogger<ModuleConfigService> _logger;
    private readonly object _writeLock = new();

    public ModuleConfigService(ModuleCatalog catalog, IWebHostEnvironment env, ILogger<ModuleConfigService> logger)
    {
        _catalog = catalog;
        _logger = logger;
        var configDir = Path.Combine(env.ContentRootPath, "config");
        _configFilePath = Path.Combine(configDir, "module-config.json");
        Directory.CreateDirectory(configDir);
    }

    public string? GetValue(string moduleId, string key)
    {
        var config = ReadConfig();
        if (config.TryGetValue(moduleId, out var moduleConfig))
        {
            if (moduleConfig.TryGetValue(key, out var value))
                return value;
        }
        return null;
    }

    public Dictionary<string, string> GetModuleConfig(string moduleId)
    {
        var config = ReadConfig();
        return config.TryGetValue(moduleId, out var moduleConfig)
            ? new Dictionary<string, string>(moduleConfig, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            var config = ReadConfig();
            config[moduleId] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

            var configDir = Path.GetDirectoryName(_configFilePath)!;
            var tempPath = Path.Combine(configDir, $"module-config.{Guid.NewGuid():N}.tmp");

            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var json = JsonSerializer.Serialize(config, options);
                File.WriteAllText(tempPath, json);

                if (File.Exists(_configFilePath))
                    File.Replace(tempPath, _configFilePath, null);
                else
                    File.Move(tempPath, _configFilePath);

                _logger.LogInformation("Module config for {Module} saved", moduleId);
            }
            finally
            {
                if (File.Exists(tempPath))
                    try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private Dictionary<string, Dictionary<string, string>> ReadConfig()
    {
        if (!File.Exists(_configFilePath))
            return new(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(_configFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)
                ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read module config file");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
