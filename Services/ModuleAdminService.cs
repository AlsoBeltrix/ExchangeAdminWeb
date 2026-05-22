using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using ExchangeAdminWeb.Modules;

namespace ExchangeAdminWeb.Services;

public class ModuleAdminService
{
    private readonly string _configFilePath;
    private readonly ILogger<ModuleAdminService> _logger;
    private readonly object _writeLock = new();

    public ModuleAdminService(IWebHostEnvironment env, ILogger<ModuleAdminService> logger)
    {
        _logger = logger;
        var configDir = Path.Combine(env.ContentRootPath, "config");
        _configFilePath = Path.Combine(configDir, "module-admins.json");
        Directory.CreateDirectory(configDir);
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
            var config = ReadConfig();
            config[moduleId] = groups;

            var configDir = Path.GetDirectoryName(_configFilePath)!;
            var tempPath = Path.Combine(configDir, $"module-admins.{Guid.NewGuid():N}.tmp");

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

                _logger.LogInformation("Module admins for {Module} saved", moduleId);
            }
            finally
            {
                if (File.Exists(tempPath))
                    try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private Dictionary<string, string[]> ReadConfig()
    {
        if (!File.Exists(_configFilePath))
            return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(_configFilePath);
            var result = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
            return result != null
                ? new Dictionary<string, string[]>(result, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Module admins config file corrupt at {Path}", _configFilePath);
            return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
