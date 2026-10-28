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

    /// <summary>
    /// A change watcher over the module-config store, for a caching reader (PermissionValidator)
    /// that derives its cache from module config and must notice a write by the other instance
    /// on the shared database (docs/SharedConfigDb-Plan.md AC4).
    /// </summary>
    public ConfigChangeWatcher CreateChangeWatcher(ILogger? logger = null) => _repository.CreateChangeWatcher(logger);

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

    /// <summary>
    /// A module's config, and whether it could be read (aud-a26k): false when the store is unreadable, in
    /// which case <paramref name="config"/> is empty and means nothing.
    /// </summary>
    public bool TryGetModuleConfig(string moduleId, out Dictionary<string, string> config) =>
        _repository.TryReadModule(moduleId, out config);

    public bool IsModuleConfigured(string moduleId)
    {
        var module = _catalog.GetById(moduleId);
        if (module == null || module.ConfigFields.Count == 0) return true;

        var config = GetModuleConfig(moduleId);
        return module.ConfigFields
            .Where(f => f.Required)
            .All(f => config.TryGetValue(f.Key, out var val) && !string.IsNullOrWhiteSpace(val));
    }

    /// <summary>
    /// Replaces a module's whole config with <paramref name="values"/>. A test fixture: it compares nothing, so
    /// the app writes through <see cref="UpdateModuleValues"/> and <see cref="SaveChangedValues"/> (aud-a26k).
    /// </summary>
    /// <remarks>
    /// Internal rather than removed because the suite seeds module config through it. A production caller would
    /// be the whole-config overwrite aud-a28n took out of the Module Configuration page; WholeStoreWriterTripwireTests
    /// refuses one.
    /// </remarks>
    internal void SaveModuleConfig(string moduleId, Dictionary<string, string> values)
    {
        lock (_writeLock)
        {
            _repository.SaveModule(moduleId, values);
            ReportSafely(logger => logger.LogInformation("Module config for {Module} saved", moduleId));
        }

        ConfigSaved?.Invoke(moduleId);
    }

    /// <summary>
    /// Writes only the named keys of a module's config, leaving its other keys as they are, and
    /// returns the module's values as read under the write lock and as the write left them
    /// (aud-a28j). Throws when the write fails, like <see cref="SaveModuleConfig"/>.
    /// </summary>
    /// <remarks>
    /// The directory-read secret and the Exchange Online connection used to read the module's whole
    /// config, set their keys and replace the lot in a later transaction: a key the other instance changed
    /// in between was overwritten, and a read that failed - the read is fail-open - wiped the module's other
    /// keys. Upserting only the named keys inside one write leaves the rest alone, and the values read
    /// under the lock are the honest before-state for the save's record (aud-a28k).
    /// </remarks>
    public ModuleConfigUpdate UpdateModuleValues(string moduleId, IReadOnlyDictionary<string, string> values)
    {
        ModuleConfigUpdate update;
        lock (_writeLock)
        {
            update = _repository.UpdateModule(moduleId, values);
            ReportSafely(logger => logger.LogInformation("Module config for {Module} updated ({Count} key(s))", moduleId, values.Count));
        }

        ConfigSaved?.Invoke(moduleId);
        return update;
    }

    /// <summary>
    /// The Module Configuration page's save (aud-a28n): writes only the keys in <paramref name="wanted"/>,
    /// each decided inside one write against the module's row as it is under the lock and the value the
    /// page loaded (<paramref name="loaded"/>; a key absent from either reads as empty). Throws when the
    /// write fails.
    /// </summary>
    /// <remarks>
    /// The page used to replace the module's whole config with the values it loaded plus the admin's edits,
    /// so a key changed elsewhere since it loaded was written back - including keys the page does not show,
    /// such as Mailbox Permissions' ExcludedUsers protection list. Now a key the admin did not change is never
    /// written. A changed key is written only while its row still holds what the page loaded; one that already
    /// holds the wanted value was changed the same way elsewhere and is reported as already applied, not
    /// refused (the aud-a26k rule); anything else refuses the whole save, naming the key - nothing is merged
    /// or guessed. The decision is made inside the write because the other instance shares the database and
    /// could land between a read and a later write.
    /// </remarks>
    /// <param name="cancellationToken">kd-58 B2. The Module Configuration page's token for this save, cancelled only when the route change stops waiting for it: handed to the store's write, so a cancel before the commit leaves nothing written. None, the default, saves as before.</param>
    public ModuleConfigSaveOutcome SaveChangedValues(
        string moduleId, IReadOnlyDictionary<string, string> loaded, IReadOnlyDictionary<string, string> wanted,
        CancellationToken cancellationToken = default) =>
        DecideChangedValues(moduleId, loaded, wanted, writeIfClear: true, cancellationToken);

    /// <summary>
    /// The compare <see cref="SaveChangedValues"/> makes, under the same lock and by the same decision, writing nothing
    /// (aud-a28r): the transaction is rolled back, change token included. <see cref="ModuleConfigSaveStatus.Saved"/> here
    /// means the compare passed - a save would write the keys.
    /// </summary>
    /// <remarks>
    /// For a save that writes two stores in turn. No repository can take part in another's write - each opens its own
    /// transaction - so the second store's compare runs before the first store is written, and a conflict there refuses
    /// the whole save instead of leaving its first half written. It is the very decision the later write makes, run under
    /// the same lock, so what it passes is what that write would do; the write still decides again under its own lock.
    /// </remarks>
    public ModuleConfigSaveOutcome CompareChangedValues(
        string moduleId, IReadOnlyDictionary<string, string> loaded, IReadOnlyDictionary<string, string> wanted) =>
        DecideChangedValues(moduleId, loaded, wanted, writeIfClear: false, CancellationToken.None);

    // One decision for the save and its compare (aud-a28r), so the two cannot drift apart.
    private ModuleConfigSaveOutcome DecideChangedValues(
        string moduleId, IReadOnlyDictionary<string, string> loaded, IReadOnlyDictionary<string, string> wanted, bool writeIfClear,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(wanted);

        var none = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
            return new ModuleConfigSaveOutcome(ModuleConfigSaveStatus.NothingToSave, [], [], none, none);

        var status = ModuleConfigSaveStatus.Saved;
        var conflicting = new List<string>();
        var alreadyApplied = new List<string>();
        ModuleConfigConditionalUpdate update;
        lock (_writeLock)
        {
            update = _repository.UpdateModuleIf(moduleId, current =>
            {
                if (current is null)
                {
                    status = ModuleConfigSaveStatus.StoreCorrupt;
                    return null;
                }

                var write = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in wanted)
                {
                    var now = current.GetValueOrDefault(key) ?? "";
                    if (string.Equals(now, loaded.GetValueOrDefault(key) ?? "", StringComparison.Ordinal))
                        write[key] = value ?? "";
                    else if (string.Equals(now, value ?? "", StringComparison.Ordinal))
                        alreadyApplied.Add(key);
                    else
                        conflicting.Add(key);
                }

                if (conflicting.Count > 0)
                {
                    status = ModuleConfigSaveStatus.ConcurrentChange;
                    return null;
                }

                if (write.Count == 0)
                {
                    status = ModuleConfigSaveStatus.NothingToSave;
                    return null;
                }

                // A compare that passed stores nothing either: null is UpdateModuleIf's rollback.
                return writeIfClear ? write : null;
            }, cancellationToken);

            if (update.Written)
                ReportSafely(logger => logger.LogInformation("Module config for {Module} updated ({Count} key(s))", moduleId, wanted.Count - alreadyApplied.Count));
        }

        if (update.Written)
            ConfigSaved?.Invoke(moduleId);

        var before = update.Before ?? none;
        return new ModuleConfigSaveOutcome(status, conflicting, alreadyApplied, before, update.After ?? before);
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

            // A module that declares BOTH keys owns both: the old key is a LIVE field there,
            // not pre-rename residue, and remapping it destroys a credential reference the
            // module still reads. "Declares the new key" was treated as proof the old one was
            // stale; it is not, because a module can be an on-prem module and a Graph module at
            // once. The rule is written against the field list rather than a module name so a
            // future dual-key module is covered without an edit here.
            if (module.ConfigFields.Any(f => string.Equals(f.Key, oldKey, StringComparison.OrdinalIgnoreCase)))
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
                // aud-a64. Nor may its report: this runs once from Program.cs outside any try, so a provider that threw here
                // stopped the app starting.
                ReportSafely(logger => logger.LogError(ex, "Graph secret key remap failed for {Module}", module.Id));
            }
        }

        if (changed.Count > 0)
            ReportSafely(logger => logger.LogInformation("Migrated stranded Graph secret key for {Count} module(s): {Modules}", changed.Count, string.Join(", ", changed)));

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
            // aud-a57. Reporting the failed read must not fail the read: a provider that threw here took its exception
            // into every caller of GetValue and GetModuleConfig instead of the empty config (aud-a28u's guard).
            try
            {
                _logger.LogError(ex, "Failed to read module config for {Module}", moduleId);
            }
            catch (Exception)
            {
                // Nowhere left to say so.
            }

            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    // aud-a63. A committed write is reported through here (the aud-a28z shape): a provider that threw - inside the lock,
    // before ConfigSaved - made a saved change look failed to its caller, and this instance's ConfigSaved subscribers never
    // heard of it. The write stands whatever the report does. aud-a64: the start-up remap's and the legacy import's
    // reports too.
    private void ReportSafely(Action<ILogger> report)
    {
        try
        {
            report(_logger);
        }
        catch (Exception)
        {
            // Nowhere left to say so.
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
                    // aud-a64. Every report here goes through the guard: the constructor runs this, and a provider that threw
                    // reached the outer catch, threw again there, and the singleton could not be built.
                    ReportSafely(logger => logger.LogWarning(ex, "Skipping unparseable legacy module config {File}", Path.GetFileName(file)));
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
                    ReportSafely(logger => logger.LogWarning(ex, "Skipping unparseable legacy module-config.json"));
                }
            }
        }
        catch (Exception ex)
        {
            ReportSafely(logger => logger.LogWarning(ex, "Failed to import legacy module config"));
        }
    }
}

/// <summary>How <see cref="ModuleConfigService.SaveChangedValues"/> ended.</summary>
public enum ModuleConfigSaveStatus
{
    /// <summary>The keys still as the page loaded them were written.</summary>
    Saved,

    /// <summary>Nothing written: no key changed, or every changed key already held its wanted value.</summary>
    NothingToSave,

    /// <summary>Nothing written: the module's values could not be read under the lock.</summary>
    StoreCorrupt,

    /// <summary>Nothing written: a changed key no longer holds what the page loaded, nor what it wants.</summary>
    ConcurrentChange,
}

/// <summary>
/// The outcome of a module-config save: the changed keys that had changed since the page loaded them
/// (on <see cref="ModuleConfigSaveStatus.ConcurrentChange"/>), the changed keys that already held their
/// wanted value and were not written, and the module's values as read under the write lock and as left.
/// </summary>
public sealed record ModuleConfigSaveOutcome(
    ModuleConfigSaveStatus Status,
    IReadOnlyList<string> ConflictingKeys,
    IReadOnlyList<string> AlreadyApplied,
    IReadOnlyDictionary<string, string> Before,
    IReadOnlyDictionary<string, string> After);
