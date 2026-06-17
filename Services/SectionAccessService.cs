using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExchangeAdminWeb.Services;

public class SectionAccessService
{
    private readonly IConfiguration _config;
    private readonly ILogger<SectionAccessService> _logger;
    private readonly Modules.ModuleCatalog _catalog;
    private readonly string _configDir;
    private readonly string _configFilePath;
    private readonly string[] _allowedGroups;
    private readonly string[] _adminGroups;
    private readonly object _writeLock = new();
    private CachedAccess? _cache;

    private sealed record CachedAccess(Dictionary<string, string[]> Data, SectionAccessSource Source);

    public SectionAccessService(IConfiguration config, ILogger<SectionAccessService> logger, IWebHostEnvironment env, Modules.ModuleCatalog catalog)
    {
        _config = config;
        _logger = logger;
        _catalog = catalog;
        _configDir = Path.Combine(env.ContentRootPath, "config");
        _configFilePath = Path.Combine(_configDir, "sectionaccess.json");

        _allowedGroups = config.GetSection("Security:AllowedGroups").Get<string[]>() ?? Array.Empty<string>();
        _adminGroups = config.GetSection("Security:AdminGroups").Get<string[]>() ?? Array.Empty<string>();
        _failClosedSections = BuildFailClosedSet();

        Directory.CreateDirectory(_configDir);
    }

    public string[] GetAllowedGroups() => _allowedGroups;

    public string[] GetAdminGroups() => _adminGroups;

    private readonly HashSet<string> _failClosedSections;

    private HashSet<string> BuildFailClosedSet()
    {
        // Do NOT swallow exceptions here. This set is the list of sections that must
        // deny access when section-access config is absent (GetGroupsForSection). An
        // empty/partial set would silently downgrade those sections to the
        // AllowedGroups fallback — fail-OPEN — which is the opposite of the intent.
        // Let any failure propagate and abort startup rather than serve a permissive
        // catalog. The catalog is the injected DI singleton, not a fresh instance.
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in _catalog.GetAll())
        {
            if (module.MainPermission.FailClosed)
                set.Add(module.MainPermission.PolicyAlias);
            foreach (var gp in module.GranularPermissions.Where(p => p.FailClosed))
                set.Add(gp.PolicyAlias);
        }
        return set;
    }

    public string[] GetGroupsForSection(string section)
    {
        var (data, source) = ReadSectionAccess();
        if (source == SectionAccessSource.None)
            return _failClosedSections.Contains(section) ? Array.Empty<string>() : _allowedGroups;

        return data.TryGetValue(section, out var groups) ? groups : Array.Empty<string>();
    }

    public Dictionary<string, string[]> GetSectionAccess()
    {
        var (data, _) = ReadSectionAccess();
        return data;
    }

    public bool IsSectionAccessConfigured()
    {
        if (File.Exists(_configFilePath))
            return true;

        var legacySection = _config.GetSection("Security:SectionAccess");
        return legacySection.Exists() && legacySection.GetChildren().Any();
    }

    /// <summary>
    /// True when config/sectionaccess.json exists but cannot be parsed or lacks the
    /// Security:SectionAccess node. Runtime reads fail closed in that state; admin pages
    /// use this to show an explicit error and refuse to save instead of rendering blank
    /// group lists that would wipe the fragment on save (incident 2026-06-12).
    /// </summary>
    public bool IsFragmentCorrupt()
    {
        if (!File.Exists(_configFilePath)) return false;
        try
        {
            var doc = JsonNode.Parse(File.ReadAllText(_configFilePath));
            return doc?["Security"]?["SectionAccess"] == null;
        }
        catch
        {
            return true;
        }
    }

    private enum SectionAccessSource { None, Fragment, AppSettings }

    private (Dictionary<string, string[]> data, SectionAccessSource source) ReadSectionAccess()
    {
        var cached = _cache;
        if (cached != null)
            return (cached.Data, cached.Source);

        var result = ReadSectionAccessFromDisk();
        _cache = new CachedAccess(result.data, result.source);
        return result;
    }

    private (Dictionary<string, string[]> data, SectionAccessSource source) ReadSectionAccessFromDisk()
    {
        if (File.Exists(_configFilePath))
        {
            try
            {
                var json = File.ReadAllText(_configFilePath);
                var doc = JsonNode.Parse(json);
                var sectionAccess = doc?["Security"]?["SectionAccess"];
                if (sectionAccess == null)
                {
                    _logger.LogError("Fragment file exists but Security:SectionAccess is missing — failing closed");
                    return (new Dictionary<string, string[]>(), SectionAccessSource.Fragment);
                }

                var dict = sectionAccess.Deserialize<Dictionary<string, string[]?>>() ?? new();
                var normalized = dict.ToDictionary(k => k.Key, k => k.Value ?? Array.Empty<string>());
                return (normalized, SectionAccessSource.Fragment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read section access fragment at {Path} — failing closed", _configFilePath);
                return (new Dictionary<string, string[]>(), SectionAccessSource.Fragment);
            }
        }

        var legacySection = _config.GetSection("Security:SectionAccess");
        if (legacySection.Exists() && legacySection.GetChildren().Any())
        {
            var legacy = legacySection.Get<Dictionary<string, string[]>>();
            if (legacy != null && legacy.Count > 0)
                return (legacy, SectionAccessSource.AppSettings);
        }

        return (new Dictionary<string, string[]>(), SectionAccessSource.None);
    }

    public void SaveSectionAccess(Dictionary<string, string[]> sectionAccess)
    {
        lock (_writeLock)
        {
            var tempPath = Path.Combine(_configDir, $"sectionaccess.{Guid.NewGuid():N}.tmp");
            var backupPath = Path.Combine(_configDir, "sectionaccess.bak");

            try
            {
                var doc = new JsonObject
                {
                    ["Security"] = new JsonObject
                    {
                        ["SectionAccess"] = JsonSerializer.SerializeToNode(sectionAccess)
                    }
                };

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                File.WriteAllText(tempPath, doc.ToJsonString(options), System.Text.Encoding.UTF8);

                var validation = JsonNode.Parse(File.ReadAllText(tempPath));
                if (validation?["Security"]?["SectionAccess"] == null)
                    throw new InvalidOperationException("Generated config failed validation");

                if (File.Exists(_configFilePath))
                    File.Replace(tempPath, _configFilePath, backupPath);
                else
                    File.Move(tempPath, _configFilePath);

                _cache = null;
                _logger.LogInformation("SectionAccess config saved to {Path}", _configFilePath);
            }
            finally
            {
                if (File.Exists(tempPath))
                    try { File.Delete(tempPath); } catch { }
            }
        }
    }
}
