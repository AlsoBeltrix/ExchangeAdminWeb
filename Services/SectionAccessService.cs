using System.Text.Json;
using System.Text.Json.Nodes;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

public class SectionAccessService
{
    private readonly IConfiguration _config;
    private readonly ILogger<SectionAccessService> _logger;
    private readonly Modules.ModuleCatalog _catalog;
    private readonly SectionAccessRepository _repository;
    private readonly string[] _allowedGroups;
    private readonly string[] _adminGroups;
    private readonly object _writeLock = new();

    // Set when a legacy sectionaccess.json exists but is unparseable / lacks the
    // Security:SectionAccess node, and the DB store is not yet configured. Like B.4, an
    // unparseable authorization fragment must keep the store fail-closed during the upgrade
    // window rather than fall through to the appsettings/AllowedGroups fallback. The corrupt
    // file stays on disk, so this re-trips every startup until repaired/removed.
    private readonly bool _legacyFileCorrupt;

    public SectionAccessService(IConfiguration config, ILogger<SectionAccessService> logger, IWebHostEnvironment env, Modules.ModuleCatalog catalog, SectionAccessRepository repository)
    {
        _config = config;
        _logger = logger;
        _catalog = catalog;
        _repository = repository;

        _allowedGroups = config.GetSection("Security:AllowedGroups").Get<string[]>() ?? Array.Empty<string>();
        _adminGroups = config.GetSection("Security:AdminGroups").Get<string[]>() ?? Array.Empty<string>();
        _failClosedSections = BuildFailClosedSet();

        var legacyPath = Path.Combine(env.ContentRootPath, "config", "sectionaccess.json");
        _legacyFileCorrupt = ImportLegacyIfPresent(legacyPath);
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
        if (_repository.IsConfigured())
            return true;

        var legacySection = _config.GetSection("Security:SectionAccess");
        return legacySection.Exists() && legacySection.GetChildren().Any();
    }

    /// <summary>
    /// True when the section-access store cannot be read (DB-integrity failure) OR an
    /// unparseable legacy sectionaccess.json is still present. Runtime reads fail closed in that
    /// state; admin pages use this to show an explicit error and refuse to save instead of
    /// rendering blank group lists that would wipe access on save (incident 2026-06-12).
    /// </summary>
    public bool IsFragmentCorrupt()
    {
        if (_legacyFileCorrupt)
            return true;
        // Corrupt if either the data OR the presence marker cannot be read (partial schema
        // damage) — same guarded read the runtime path uses.
        return !_repository.TryRead(out _, out _);
    }

    private enum SectionAccessSource { None, Fragment, AppSettings }

    private (Dictionary<string, string[]> data, SectionAccessSource source) ReadSectionAccess()
    {
        // FAIL-CLOSED: an unparseable legacy fragment still on disk keeps the store corrupt
        // (empty Fragment source — everything denied) during the upgrade window, rather than
        // falling through to the appsettings/AllowedGroups fallback.
        if (_legacyFileCorrupt)
            return (new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase), SectionAccessSource.Fragment);

        // The DB store is the "fragment" source. It is read fresh each call (the change-token
        // model makes out-of-band writes visible). Read data AND the configured flag in a single
        // guarded operation so a partial/damaged schema (e.g. a missing marker table) fails
        // closed as an empty Fragment rather than throwing through the authorization path.
        if (!_repository.TryRead(out var data, out var configured))
        {
            _logger.LogError("Section access store unreadable — failing closed");
            return (new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase), SectionAccessSource.Fragment);
        }

        if (configured)
            return (data, SectionAccessSource.Fragment);

        // Not configured in the DB: fall back to the legacy appsettings Security:SectionAccess.
        var legacySection = _config.GetSection("Security:SectionAccess");
        if (legacySection.Exists() && legacySection.GetChildren().Any())
        {
            var legacy = legacySection.Get<Dictionary<string, string[]>>();
            if (legacy != null && legacy.Count > 0)
                return (legacy, SectionAccessSource.AppSettings);
        }

        return (new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase), SectionAccessSource.None);
    }

    public void SaveSectionAccess(Dictionary<string, string[]> sectionAccess)
    {
        lock (_writeLock)
        {
            _repository.SaveAll(sectionAccess);
            _logger.LogInformation("SectionAccess config saved");
        }
    }

    // One-time import of the legacy sectionaccess.json into section_access, then archive the
    // file (SqliteConfigStore-Plan §4). Only fills if not yet configured (DB wins). Returns true
    // if the legacy file exists but is unparseable / missing the Security:SectionAccess node: it
    // is left in place (not archived) AND the store stays fail-closed until repaired/removed.
    private bool ImportLegacyIfPresent(string legacyPath)
    {
        try
        {
            if (!File.Exists(legacyPath))
                return false;

            Dictionary<string, string[]> parsed;
            try
            {
                var doc = JsonNode.Parse(File.ReadAllText(legacyPath));
                var sectionAccess = doc?["Security"]?["SectionAccess"];
                if (sectionAccess == null)
                {
                    _logger.LogError("Legacy sectionaccess.json exists but Security:SectionAccess is missing — failing closed until repaired/removed");
                    return true;
                }

                var dict = sectionAccess.Deserialize<Dictionary<string, string[]?>>() ?? new();
                parsed = dict.ToDictionary(k => k.Key, k => k.Value ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Legacy sectionaccess.json is unparseable — failing closed until repaired/removed");
                return true;
            }

            try
            {
                _repository.ImportIfMissing(parsed);
            }
            catch (Exception ex)
            {
                // The file parsed fine but could not be committed to the DB (e.g. SQLite busy).
                // Do NOT archive and do NOT fall through to an unconfigured store — that would
                // silently drop the section-access rules and fall back to the permissive
                // appsettings/_allowedGroups path. Fail closed; the file stays on disk so the
                // next startup retries the import.
                _logger.LogError(ex, "Failed to import legacy sectionaccess.json into the store — failing closed until import succeeds");
                return true;
            }

            LegacyConfigImport.ArchiveFile(legacyPath, _logger);
            return false;
        }
        catch (Exception ex)
        {
            // Reached only if reading the file itself failed (not a parse error — those return
            // true above). A valid file we could not even read must also fail closed.
            _logger.LogError(ex, "Failed to process legacy sectionaccess.json — failing closed");
            return true;
        }
    }
}
