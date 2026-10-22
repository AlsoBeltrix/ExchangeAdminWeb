using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

public sealed record EditableAttribute(
    string Name,
    string Label,
    string Type,
    string[]? Choices,
    bool Required,
    bool AllowClear,
    int? MaxLength,
    string? Pattern,
    int Level = 1);

public sealed record AttributeLookupResult(
    bool Success,
    string? Error,
    ResolvedDirectoryPrincipal? Principal,
    string? SourceOfAuthority,
    bool IsReadOnly,
    string? ReadOnlyReason,
    Dictionary<string, string?>? CurrentValues);

public sealed record AttributeChange(string Name, string Label, string? OldValue, string? NewValue);

public sealed record AttributeSavePreview(
    bool Success,
    string? Error,
    List<AttributeChange>? Changes);

public sealed record AttributeLegendEntry(string Description, string? Note = null, string? Source = null);

public sealed record AttributeSaveResult(
    bool Success,
    string? Error,
    List<AttributeChange>? AppliedChanges);

/// <summary>
/// How one allowlist save from the Module Configuration page ended (aud-a28f). Whatever it says, the
/// attempt has already been audited.
/// </summary>
/// <param name="Success">The allowlist was written.</param>
/// <param name="Error">What to show the admin when it was not.</param>
/// <param name="StoreCorrupt">
/// The refusal was a corrupt store. The page needs to know, not just to say so: it shows the corrupt
/// banner and disables the table, because any save from it would replace what the store holds.
/// </param>
public sealed record AllowlistSaveResult(bool Success, string? Error, bool StoreCorrupt = false);

/// <summary>
/// One field of a kept allowlist row that a save changed, as the settings-change record lists it
/// (aud-a28f). Values are text - a flag as true/false, a number in invariant digits - and null for
/// none, so every field reads the same way in the Admin Event Log.
/// </summary>
internal sealed record AllowlistFieldChange(string Attribute, string Field, string? Old, string? New);

/// <summary>
/// Marks a save as the reversal of an earlier attribute edit (aud-a26, aud-a26b).
/// </summary>
/// <remarks>
/// Passed to <see cref="ADAttributeEditorService.SaveAsync"/>, it makes the save announce itself
/// as <see cref="NoticeAction"/> - naming what was reverted and the operation it reverses -
/// instead of as another <c>ADAttributeEditor_Update</c>, so a change and its retraction can be
/// paired by eye in the mailbox. <see cref="NoticeDetails"/> is the one place that notice's rows
/// are built: SaveAsync uses it for every result it returns, and the undo service for an
/// exception SaveAsync never returns.
/// </remarks>
/// <param name="Target">The target as the original audit record names it.</param>
/// <param name="RevertedAttributes">The attributes being put back.</param>
/// <param name="OriginalOperationId">The operation being reversed, when the record carries it.</param>
public sealed record AttributeEditReversal(string Target, string[] RevertedAttributes, string? OriginalOperationId)
{
    public const string NoticeAction = "ADAttributeEditor_Undo";

    public Dictionary<string, string> NoticeDetails(bool success, string? error)
    {
        var details = new Dictionary<string, string>
        {
            ["Target"] = Target,
            ["Reverted"] = string.Join(", ", RevertedAttributes),
            ["Original operation"] = OriginalOperationId ?? "(unknown)",
        };

        if (!success)
            details["Error"] = error ?? "Unknown";

        return details;
    }
}

public class ADAttributeEditorService
{
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly ProtectedPrincipalService _protectedPrincipalService;
    private readonly ProtectedPrincipalServicerService _servicers;

    /// <summary>Module id for the servicer grant. Must match the catalog descriptor.</summary>
    private const string ServicerModuleId = "ADAttributeEditor";
    private readonly OperationTraceService _operationTrace;
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly ModuleConfigService _moduleConfig;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ADAttributeEditorService> _logger;
    private readonly object _allowlistLock = new();

    private List<EditableAttribute>? _cachedAllowlist;
    private DateTime _allowlistLoadedAt = DateTime.MinValue;
    private Dictionary<string, Dictionary<string, AttributeLegendEntry>>? _cachedLegend;
    private DateTime _legendLoadedAt = DateTime.MinValue;
    private static readonly TimeSpan AllowlistCacheTtl = TimeSpan.FromSeconds(30);

    // Cross-process freshness on the shared config database (docs/SharedConfigDb-Plan.md AC4):
    // one watcher, a token per cache recorded as of that cache's load. A cache hit first asks
    // whether the store moved past it; the TTL still bounds staleness when the token is unreadable.
    private readonly ConfigChangeWatcher _changeWatcher;
    private long _allowlistLoadedToken = ConfigChangeWatcher.Unknown;
    private long _legendLoadedToken = ConfigChangeWatcher.Unknown;
    private static readonly SemaphoreSlim _adThrottle = new(2, 2);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> HardDenylistExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "userAccountControl", "pwdLastSet", "unicodePwd", "userPassword",
        "lockoutTime", "accountExpires", "memberOf", "primaryGroupID",
        "adminCount", "badPwdCount", "objectSid", "objectGUID",
        "distinguishedName", "servicePrincipalName", "altSecurityIdentities",
        "nTSecurityDescriptor"
    };

    private static readonly string[] HardDenylistPrefixes = ["lastLogon", "msDS-"];

    private readonly AttributeEditorRepository _repository;

    // Set when a legacy ad-editable-attributes.json exists but is unparseable / missing the
    // Attributes section, and the DB allowlist is not yet configured. The allowlist's fail-closed
    // signal is NULL (distinct from the valid empty []), so during the upgrade window a corrupt
    // file must make the load return null until it is repaired/removed - never silently degrade
    // to an empty allowlist. The file stays on disk, so this re-trips every startup.
    private readonly bool _legacyAllowlistCorrupt;

    public ADAttributeEditorService(
        ModuleCredentialService moduleCredentials,
        ProtectedPrincipalService protectedPrincipalService,
        ProtectedPrincipalServicerService servicers,
        OperationTraceService operationTrace,
        AuditService audit,
        EmailService email,
        ModuleConfigService moduleConfig,
        AttributeEditorRepository repository,
        IWebHostEnvironment env,
        ILogger<ADAttributeEditorService> logger)
    {
        _moduleCredentials = moduleCredentials;
        _protectedPrincipalService = protectedPrincipalService;
        _servicers = servicers;
        _operationTrace = operationTrace;
        _audit = audit;
        _email = email;
        _moduleConfig = moduleConfig;
        _repository = repository;
        _env = env;
        _logger = logger;
        _changeWatcher = repository.CreateChangeWatcher(logger);

        _legacyAllowlistCorrupt = ImportLegacyIfPresent();
    }

    public void InvalidateAllowlistCache()
    {
        lock (_allowlistLock)
        {
            _cachedAllowlist = null;
            _allowlistLoadedAt = DateTime.MinValue;
        }
    }

    /// <summary>Test seam: the watcher this service consults on every cache hit.</summary>
    internal ConfigChangeWatcher ChangeWatcher => _changeWatcher;

    public List<EditableAttribute>? GetAllowlist()
    {
        lock (_allowlistLock)
        {
            if (_cachedAllowlist != null
                && !_changeWatcher.HasChangedSince(_allowlistLoadedToken)
                && DateTime.UtcNow - _allowlistLoadedAt < AllowlistCacheTtl)
                return _cachedAllowlist;
        }

        // Token BEFORE the load (scd-2) so a racing write is caught on the next check.
        var token = _changeWatcher.CurrentToken();
        var loaded = LoadAllowlistFromDisk();

        // Only cache a successful load. A corrupt store (null) is never cached, so a
        // file that became corrupt is re-read on every call rather than masked by a
        // stale entry - but a valid load that is later followed by on-disk corruption
        // is still served from cache for the TTL, which is why the pre-save gate must
        // use IsAllowlistCorrupt (disk-fresh), not GetAllowlist (cached).
        if (loaded != null)
        {
            lock (_allowlistLock)
            {
                _cachedAllowlist = loaded;
                _allowlistLoadedAt = DateTime.UtcNow;
                _allowlistLoadedToken = token;
            }
        }
        return loaded;
    }

    /// <summary>
    /// True when config/ad-editable-attributes.json exists but cannot be parsed or fails
    /// validation. Reads disk fresh on every call - it does NOT consult the 30-second
    /// cache - so admin pages can use it as the authoritative pre-save corruption gate.
    /// GetAllowlist() can return a stale-but-valid cached list for up to the TTL after the
    /// file becomes corrupt; saving over the file in that window would discard whatever it
    /// currently holds. This gate observes the actual disk state instead. Mirrors
    /// SectionAccessService.IsFragmentCorrupt (incident 2026-06-12 corrupt-store class).
    /// </summary>
    public bool IsAllowlistCorrupt() => LoadAllowlistFromDisk() == null;

    /// <summary>
    /// Reads and validates the allowlist from the store with no caching. Returns an empty list
    /// when nothing is configured (a valid "nothing allowlisted" state) and null when the store
    /// is unreadable or its rows fail validation (the fail-closed/corrupt state).
    /// </summary>
    private List<EditableAttribute>? LoadAllowlistFromDisk() => LoadAllowlistFromDisk(out _);

    /// <param name="storedRows">
    /// The rows exactly as the store holds them - including any the returned list drops, such as a
    /// denylisted name - so a save can say what it replaced (aud-a28f). Empty when the read failed.
    /// </param>
    private List<EditableAttribute>? LoadAllowlistFromDisk(out List<AttributeRow> storedRows)
    {
        storedRows = [];

        // Fail closed (null) if a corrupt legacy file is still on disk during the upgrade window.
        if (_legacyAllowlistCorrupt)
            return null;

        if (!_repository.TryReadAllowlist(out var rows, out var configured))
        {
            _logger.LogError("ad-editable-attributes store unreadable - failing closed");
            return null;
        }

        storedRows = rows;
        if (!configured)
            return []; // nothing configured - valid empty allowlist

        // Projecting rows can throw if a stored choices_json is malformed (operator/promote edit
        // or partial corruption). That is a corrupt-store condition -> return null (the documented
        // fail-closed signal), never let it throw through callers as a 500.
        // Projecting rows can throw if a stored choices_json is malformed (operator/promote edit
        // or partial corruption). That is a corrupt-store condition -> return null (the documented
        // fail-closed signal), never let it throw through callers as a 500.
        List<AttributeAllowlistEntry> entries;
        try
        {
            entries = rows.Select(ToAllowlistEntry).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ad-editable-attributes store has a malformed row (choices_json) - failing closed");
            return null;
        }

        return ValidateRows(entries);
    }

    // Validation (denylist removal, contradictory flags, empty Choice list) - unchanged from the
    // file world; returns null to fail closed, matching LoadAllowlistFromDisk's contract.
    private List<EditableAttribute>? ValidateRows(IEnumerable<AttributeAllowlistEntry> entries)
    {
        var validated = new List<EditableAttribute>();
        foreach (var attr in entries)
        {
            if (IsDenylisted(attr.Name))
            {
                _logger.LogWarning("Attribute {Name} is on the hard denylist and was removed from the allowlist", attr.Name);
                continue;
            }

            if (attr.Required && attr.AllowClear)
            {
                _logger.LogError("Contradictory config for attribute {Name}: Required=true with AllowClear=true - failing closed", attr.Name);
                return null;
            }

            if (attr.Type == "Choice" && (attr.Choices == null || attr.Choices.Length == 0))
            {
                _logger.LogError("Attribute {Name} is type Choice but has no Choices defined - failing closed", attr.Name);
                return null;
            }

            validated.Add(new EditableAttribute(
                attr.Name, attr.Label, attr.Type,
                attr.Choices, attr.Required, attr.AllowClear,
                attr.MaxLength, attr.Pattern, attr.Level > 0 ? attr.Level : 1));
        }

        return validated;
    }

    private static AttributeAllowlistEntry ToAllowlistEntry(AttributeRow row) => new()
    {
        Name = row.Name,
        Label = row.Label,
        Type = row.Type,
        Choices = string.IsNullOrEmpty(row.ChoicesJson)
            ? null
            : JsonSerializer.Deserialize<string[]>(row.ChoicesJson),
        Required = row.Required,
        AllowClear = row.AllowClear,
        MaxLength = row.MaxLength,
        Pattern = row.Pattern,
        Level = row.Level
    };

    private static AttributeRow ToAllowlistRow(EditableAttribute a) => new(
        a.Name, a.Label, a.Type,
        a.Choices == null ? null : JsonSerializer.Serialize(a.Choices),
        a.Required, a.AllowClear, a.MaxLength, a.Pattern, a.Level > 0 ? a.Level : 1);

    public List<EditableAttribute>? GetAllowlistForLevel(int maxLevel)
    {
        var all = GetAllowlist();
        return all?.Where(a => a.Level <= maxLevel).ToList();
    }

    public void SaveAllowlist(List<EditableAttribute> attributes)
    {
        // aud-a28e. A pattern that does not compile is refused where the allowlist is saved, before
        // the store is touched, rather than found by the first operator to save that attribute. It
        // throws, so no caller can carry on as if the save happened. The Module Configuration page's
        // save refuses such a pattern itself, as an audited refusal, before it calls this
        // (SaveAllowlistAndAudit, aud-a28f); this throw is the backstop for any other caller.
        // ValidateProposedValues keeps its own catch: a pattern already stored - edited into the
        // database, or imported before the legacy import checked patterns (aud-a26e) - never passes
        // through here, and a timeout depends on the value being checked, so it cannot be judged here.
        foreach (var attribute in attributes)
        {
            var problem = DescribeUncompilablePattern(attribute);
            if (problem is not null)
                throw new ArgumentException(problem);
        }

        _repository.SaveAllowlist(attributes.Select(ToAllowlistRow).ToList());
        InvalidateAllowlistCache();
    }

    /// <summary>
    /// The section every allowlist save record is filed under, whether the save succeeded or not, so
    /// an admin looking for the allowlist's changes finds the refused and failed attempts beside them.
    /// </summary>
    internal const string AllowlistAuditSection = "ADAttributeEditor:Allowlist";

    /// <summary>
    /// The Module Configuration page's allowlist save, after the page's own authorisation re-check:
    /// re-checks the store, refuses what must not be written, writes, and leaves exactly one audit
    /// record however it ends (aud-a28f).
    /// </summary>
    /// <remarks>
    /// The save used to run in the page. Its refusals and its catch wrote no record, and its one
    /// record - on success - passed an empty list as the previous allowlist, so every save read as
    /// adding every attribute and removing none. It lives here so every one of those paths can be
    /// driven by a test; the page keeps only the re-check, which needs the principal and the policies.
    ///
    /// It does not throw. Every outcome is recorded here and returned, which is what lets the page's
    /// catch write the one record for an attempt that failed before reaching this method without
    /// risking a second.
    /// </remarks>
    public AllowlistSaveResult SaveAllowlistAndAudit(List<EditableAttribute> attributes, string performedBy, string ip)
    {
        List<AttributeRow> stored;
        List<AttributeRow> written;
        List<AllowlistFieldChange> changed;
        try
        {
            // One fresh read is both the corruption re-check and the "before" the record needs. The
            // re-check is the authoritative gate - the page's disabled buttons are UI only, and the
            // store may have become corrupt since the page loaded - and saving over a corrupt store
            // would discard whatever it holds, so it fails closed. It must be this read, not
            // GetAllowlist: the cache can serve a stale-but-valid list for up to its TTL after the
            // store became corrupt, which would let the gate pass and the save replace the store.
            // The raw rows are the "before" because they are what the write replaces, rows the load
            // drops included.
            if (LoadAllowlistFromDisk(out stored) == null)
            {
                const string corrupt = "Allowlist store is corrupt; refusing to save over it. Fix or restore config/ad-editable-attributes.json, then reload this page.";
                LogAllowlistSaveRefused(performedBy, ip, "AllowlistConfig", corrupt);
                return new(false, corrupt, StoreCorrupt: true);
            }

            var denied = attributes.Where(a => IsDenylisted(a.Name)).ToList();
            if (denied.Count > 0)
            {
                var refused = $"Cannot save: {string.Join(", ", denied.Select(d => d.Name))} are on the hard denylist.";
                LogAllowlistSaveRefused(performedBy, ip, "Denylist", refused);
                return new(false, refused);
            }

            // Checked here, before SaveAllowlist, so an uncompilable pattern is recorded as what it
            // is - a refusal, nothing tried - rather than reaching SaveAllowlist's throw and the
            // catch below, which records a failed write.
            var uncompilable = attributes.Select(DescribeUncompilablePattern).FirstOrDefault(p => p is not null);
            if (uncompilable is not null)
            {
                LogAllowlistSaveRefused(performedBy, ip, "PatternCheck", uncompilable);
                return new(false, uncompilable);
            }

            // Everything the success record says is worked out before the write, so a throw here is
            // a failure with nothing written, never a completed write reported as failed.
            written = RowsAsWritten(attributes);
            changed = DescribeChangedRows(stored, written);
            SaveAllowlist(attributes);
        }
        catch (Exception ex)
        {
            // No refusedAt and no mutationAttempted: the write may have been tried, and a failure
            // record must not claim otherwise.
            var failed = $"Failed to save: {ex.Message}";
            _logger.LogError(ex, "Allowlist save failed");
            LogAllowlistSaveFailed(performedBy, ip, failed);
            return new(false, failed);
        }

        // Outside the try that covers the write, and in its own: the allowlist has been saved, and an
        // audit failure must not make a completed save look failed (Constitution, Auditing). Written
        // even when nothing differs, so every attempt leaves a record.
        try
        {
            _audit.LogSettingsChange(
                performedBy,
                ip,
                AllowlistAuditSection,
                stored.Select(r => r.Name).ToArray(),
                written.Select(r => r.Name).ToArray(),
                changed.Count > 0 ? new Dictionary<string, object?> { ["changed"] = changed } : null);
        }
        catch (Exception auditEx)
        {
            _logger.LogError(auditEx, "Audit failed for allowlist save");
        }

        return new(true, null);
    }

    /// <summary>
    /// Records an allowlist save refused before anything was written (aud-a28f): aud-a28's
    /// <c>refusedAt</c> names the step, and <c>mutationAttempted</c> false is what tells it apart
    /// from a failed write.
    /// </summary>
    /// <remarks>
    /// Public because the Module Configuration page writes one of these itself, for its
    /// authorisation re-check. Never throws: an audit sink that fails is logged, and must not turn
    /// a refusal into an exception or put a second record behind it.
    /// </remarks>
    public void LogAllowlistSaveRefused(string performedBy, string ip, string refusedAt, string reason) =>
        WriteAllowlistSaveFailure(performedBy, ip, reason,
            new Dictionary<string, object?> { ["refusedAt"] = refusedAt, ["mutationAttempted"] = false });

    /// <summary>
    /// Records an allowlist save that failed with an exception (aud-a28f). Public and non-throwing
    /// for the same reasons as <see cref="LogAllowlistSaveRefused"/>: the page's catch writes one.
    /// </summary>
    public void LogAllowlistSaveFailed(string performedBy, string ip, string reason) =>
        WriteAllowlistSaveFailure(performedBy, ip, reason, null);

    private void WriteAllowlistSaveFailure(string performedBy, string ip, string reason, Dictionary<string, object?>? extra)
    {
        try
        {
            _audit.LogSettingsChangeFailed(performedBy, ip, AllowlistAuditSection, reason, extra);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit failed for an allowlist save that did not complete");
        }
    }

    /// <summary>
    /// The rows a save of <paramref name="attributes"/> leaves in the store, so the record describes
    /// the store rather than the request. The repository's insert skips a blank name and upserts a
    /// repeated one (names compare case-insensitively): the first spelling stays, the later row's
    /// values win.
    /// </summary>
    private static List<AttributeRow> RowsAsWritten(List<EditableAttribute> attributes) =>
        attributes
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .Select(ToAllowlistRow)
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last() with { Name = g.First().Name })
            .ToList();

    /// <summary>
    /// Every field that differs on a row present both before and after. Rows only on one side are
    /// not here: the settings-change record already names them under <c>added</c> and
    /// <c>removed</c>.
    /// </summary>
    private static List<AllowlistFieldChange> DescribeChangedRows(List<AttributeRow> before, List<AttributeRow> after)
    {
        var changed = new List<AllowlistFieldChange>();
        foreach (var now in after)
        {
            var was = before.FirstOrDefault(b => string.Equals(b.Name, now.Name, StringComparison.OrdinalIgnoreCase));
            if (was is null)
                continue;

            AddIfChanged(changed, now.Name, "Name", was.Name, now.Name);
            AddIfChanged(changed, now.Name, "Label", was.Label, now.Label);
            AddIfChanged(changed, now.Name, "Type", was.Type, now.Type);
            AddIfChanged(changed, now.Name, "Choices", was.ChoicesJson, now.ChoicesJson);
            AddIfChanged(changed, now.Name, "Required", Flag(was.Required), Flag(now.Required));
            AddIfChanged(changed, now.Name, "AllowClear", Flag(was.AllowClear), Flag(now.AllowClear));
            AddIfChanged(changed, now.Name, "MaxLength", Number(was.MaxLength), Number(now.MaxLength));
            AddIfChanged(changed, now.Name, "Pattern", was.Pattern, now.Pattern);
            AddIfChanged(changed, now.Name, "Level", Number(was.Level), Number(now.Level));
        }

        return changed;
    }

    private static void AddIfChanged(List<AllowlistFieldChange> changed, string attribute, string field, string? old, string? now)
    {
        // Empty and absent are one value, "none": the store can hold an unset value as empty text,
        // and reads some NULL columns back that way, while the page sends null - the same nothing,
        // which must not be recorded as a change.
        old = string.IsNullOrEmpty(old) ? null : old;
        now = string.IsNullOrEmpty(now) ? null : now;
        if (!string.Equals(old, now, StringComparison.Ordinal))
            changed.Add(new AllowlistFieldChange(attribute, field, old, now));
    }

    private static string Flag(bool value) => value ? "true" : "false";

    private static string? Number(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Why <paramref name="attribute"/>'s pattern does not compile, naming the attribute; null when
    /// it has no pattern or the pattern compiles.
    /// </summary>
    /// <remarks>
    /// The allowlist-save wording, shared by SaveAllowlist's throw and the page save's audited
    /// refusal so the two cannot drift. It names the attribute and says an administrator must correct
    /// the pattern: the page shows the pattern as typed and never guesses at or repairs one
    /// (aud-a26e), so the fix is the admin's to make.
    /// </remarks>
    private static string? DescribeUncompilablePattern(EditableAttribute attribute) =>
        DescribeUncompilablePattern(attribute.Name, attribute.Label, attribute.Pattern) is { } problem
            ? $"{problem} The allowlist was not saved: an administrator must correct the pattern."
            : null;

    /// <summary>
    /// The core of the check, for a row that is not yet an <see cref="EditableAttribute"/> - the
    /// legacy import's (aud-a26e) - and for the save wording above. Compiled with the options and
    /// timeout the validation-time match uses, so what passes here is what that match will use.
    /// </summary>
    private static string? DescribeUncompilablePattern(string name, string label, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return null;

        try
        {
            _ = new Regex(pattern, RegexOptions.None, RegexTimeout);
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"The pattern for attribute '{label}' ({name}) is not a valid regular expression: {ex.Message}";
        }
    }

    public Dictionary<string, Dictionary<string, AttributeLegendEntry>> GetLegend()
    {
        lock (_allowlistLock)
        {
            if (_cachedLegend != null
                && !_changeWatcher.HasChangedSince(_legendLoadedToken)
                && DateTime.UtcNow - _legendLoadedAt < AllowlistCacheTtl)
                return _cachedLegend;
        }

        // Token BEFORE the load (scd-2). Legend is fail-open: the repository returns an empty
        // map on any read failure.
        var token = _changeWatcher.CurrentToken();
        var rows = _repository.ReadLegend();
        var parsed = rows.ToDictionary(
            outer => outer.Key,
            outer => outer.Value.ToDictionary(
                inner => inner.Key,
                inner => new AttributeLegendEntry(inner.Value.Description, inner.Value.Note, inner.Value.Source),
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        lock (_allowlistLock)
        {
            _cachedLegend = parsed;
            _legendLoadedAt = DateTime.UtcNow;
            _legendLoadedToken = token;
        }
        return parsed;
    }

    // One-time import of legacy ad-editable-attributes(.legend).json into the store, then archive
    // each file (SqliteConfigStore-Plan Section 4). DB wins (only imports if not already configured).
    // Allowlist: an unparseable/invalid file is left in place and NOT marked configured, so the
    // store stays at the empty/null-on-load fail-closed state until repaired - we do not import
    // partial/garbage rows. Legend is fail-open: an unparseable legend is skipped (left in place).
    private bool ImportLegacyIfPresent()
    {
        var configDir = Path.Combine(_env.ContentRootPath, "config");
        var allowlistCorrupt = false;

        // Allowlist
        try
        {
            var allowlistPath = Path.Combine(configDir, "ad-editable-attributes.json");
            if (File.Exists(allowlistPath) && !_repository.TryReadAllowlistConfigured())
            {
                AttributeAllowlistFile? wrapper = null;
                var parseOk = true;
                try
                {
                    wrapper = JsonSerializer.Deserialize<AttributeAllowlistFile>(
                        File.ReadAllText(allowlistPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Legacy ad-editable-attributes.json is unparseable - left in place, failing closed until repaired");
                    parseOk = false;
                    allowlistCorrupt = true;
                }

                if (parseOk)
                {
                    if (wrapper?.Attributes == null)
                    {
                        _logger.LogError("Legacy ad-editable-attributes.json missing Attributes section - left in place, failing closed until repaired");
                        allowlistCorrupt = true;
                    }
                    else if (ValidateRows(wrapper.Attributes) == null)
                    {
                        // Parseable but fails validation (denylist/contradictory/empty-Choice) - the
                        // file world returned null here too. Leave the file, fail closed.
                        _logger.LogError("Legacy ad-editable-attributes.json fails validation - left in place, failing closed until repaired");
                        allowlistCorrupt = true;
                    }
                    else if (wrapper.Attributes
                        .Select(a => DescribeUncompilablePattern(a.Name, a.Label, a.Pattern))
                        .FirstOrDefault(p => p is not null) is { } uncompilable)
                    {
                        // aud-a26e. The import stores rows straight through the repository, so it
                        // used to skip the compile check the allowlist save makes (aud-a28e), and a
                        // bad pattern was found only by the first operator to save that attribute.
                        // Refused like the file's other validation failures: the whole file or none
                        // of it, the file left for an administrator to correct, the allowlist failing
                        // closed meanwhile. Every row is checked, not just the ones ValidateRows
                        // keeps, because the import stores them all. The log is the only place this
                        // can name the attribute: the import runs as the service is constructed, with
                        // no page to tell, and repeats on each construction until the file is fixed.
                        _logger.LogError(
                            "Legacy ad-editable-attributes.json was not imported: {Problem} It is left in place, and the allowlist fails closed until an administrator corrects the pattern.",
                            uncompilable);
                        allowlistCorrupt = true;
                    }
                    else
                    {
                        var rows = wrapper.Attributes
                            .Select(a => new AttributeRow(
                                a.Name, a.Label, a.Type,
                                a.Choices == null ? null : JsonSerializer.Serialize(a.Choices),
                                a.Required, a.AllowClear, a.MaxLength, a.Pattern, a.Level > 0 ? a.Level : 1))
                            .ToList();
                        try
                        {
                            _repository.ImportAllowlistIfMissing(rows);
                            LegacyConfigImport.ArchiveFile(allowlistPath, _logger);
                        }
                        catch (Exception ex)
                        {
                            // Valid file but the DB import could not be committed (e.g. SQLite
                            // busy). Do NOT archive and do NOT leave the store looking
                            // unconfigured - that would silently drop the existing allowlist
                            // rules. Fail closed; the file stays for the next startup to retry.
                            _logger.LogError(ex, "Failed to import legacy ad-editable-attributes.json into the store - failing closed until import succeeds");
                            allowlistCorrupt = true;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // A valid file we could not even read must also fail closed (parse errors are handled
            // above and already set allowlistCorrupt).
            _logger.LogError(ex, "Failed to process legacy ad-editable-attributes.json - failing closed");
            allowlistCorrupt = true;
        }

        // Legend (fail-open)
        try
        {
            var legendPath = Path.Combine(configDir, "ad-editable-attributes-legend.json");
            if (File.Exists(legendPath) && !_repository.IsLegendConfigured())
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, AttributeLegendEntry>>>(
                        File.ReadAllText(legendPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (parsed != null)
                    {
                        var rows = parsed.ToDictionary(
                            o => o.Key,
                            o => o.Value.ToDictionary(
                                i => i.Key,
                                i => new AttributeLegendRow(i.Value.Description, i.Value.Note, i.Value.Source)));
                        _repository.ImportLegendIfMissing(rows);
                    }
                    LegacyConfigImport.ArchiveFile(legendPath, _logger);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Legacy ad-editable-attributes-legend.json unparseable - left in place");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import legacy ad-editable-attributes-legend.json");
        }

        return allowlistCorrupt;
    }

    public string[] GetEffectiveChoices(EditableAttribute attr)
    {
        var legend = GetLegend();
        if (legend.TryGetValue(attr.Name, out var attrLegend) && attrLegend.Count > 0)
        {
            var legendKeys = attrLegend.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (attr.Choices != null)
            {
                foreach (var c in attr.Choices)
                    legendKeys.Add(c);
            }

            return legendKeys
                .Select(k => (key: k, numVal: long.TryParse(k, out var n) ? n : long.MaxValue))
                .OrderBy(x => x.numVal < 0 ? 1 : 0)
                .ThenBy(x => Math.Abs(x.numVal))
                .Select(x => x.key)
                .ToArray();
        }

        return attr.Choices ?? [];
    }

    public string GetChoiceDisplayText(string attributeName, string value)
    {
        var legend = GetLegend();
        if (legend.TryGetValue(attributeName, out var attrLegend) &&
            attrLegend.TryGetValue(value, out var entry))
        {
            return $"{value} - {entry.Description}";
        }
        return value;
    }

    /// <summary>
    /// The runspace the lookup and save paths open (PerformLookup, PerformSave).
    /// </summary>
    /// <remarks>
    /// Internal virtual as a TEST SEAM, the shape of <c>Comms10kService.CreateReplaceRunspace</c>:
    /// a test hands back a session whose Import-Module, Get-ADUser and Set-ADUser are local
    /// functions, so those paths run for real with no directory. Production builds exactly the
    /// session it always did.
    /// </remarks>
    internal virtual Runspace CreateDirectoryRunspace()
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        return RunspaceFactory.CreateRunspace(iss);
    }

    /// <summary>
    /// This module's AD credential, for the lookup and save paths.
    /// </summary>
    /// <remarks>
    /// Internal virtual as a TEST SEAM, like <c>GroupManagementService.GetCredentialsAsync</c>:
    /// the save path fetches credentials before it opens a session, so without this no test can
    /// drive a save past the credential step.
    /// </remarks>
    internal virtual Task<(string username, string password, string domain)?> GetCredentialsAsync(string purpose) =>
        _moduleCredentials.GetCredentialsAsync("ADAttributeEditor", purpose);

    public async Task<AttributeLookupResult> LookupAsync(string identity)
    {
        var creds = await GetCredentialsAsync("attribute lookup");
        if (creds == null)
            return new(false, "AD credentials unavailable. Configure the module's Delinea Secret ID.", null, null, false, null, null);

        var allowlist = GetAllowlist();
        if (allowlist == null)
            return new(false, "Attribute allowlist configuration is corrupt. Contact your administrator.", null, null, false, null, null);

        if (allowlist.Count == 0)
            return new(false, "No editable attributes configured. An administrator must configure the attribute allowlist.", null, null, false, null, null);

        if (!await _adThrottle.WaitAsync(TimeSpan.FromMinutes(2)))
            return new(false, "AD service is busy. Please try again.", null, null, false, null, null);

        try
        {
            return await Task.Run(() => PerformLookup(identity, creds.Value, allowlist));
        }
        finally
        {
            _adThrottle.Release();
        }
    }

    public async Task<AttributeSavePreview> PreviewAsync(ResolvedDirectoryPrincipal target, Dictionary<string, string?> proposedValues)
    {
        var allowlist = GetAllowlist();
        if (allowlist == null)
            return new(false, "Attribute allowlist configuration is corrupt.", null);

        var validationError = ValidateProposedValues(proposedValues, allowlist);
        if (validationError != null)
            return new(false, validationError, null);

        var creds = await _moduleCredentials.GetCredentialsAsync("ADAttributeEditor", "attribute preview");
        if (creds == null)
            return new(false, "AD credentials unavailable.", null);

        if (!await _adThrottle.WaitAsync(TimeSpan.FromMinutes(2)))
            return new(false, "AD service is busy.", null);

        try
        {
            return await Task.Run(() => ComputePreview(target, proposedValues, creds.Value, allowlist));
        }
        finally
        {
            _adThrottle.Release();
        }
    }

    /// <summary>
    /// Writes the proposed values, audits the outcome and tells administrators about it.
    /// </summary>
    /// <remarks>
    /// aud-a26b. Every result returned here - an applied write, a no-op, a refusal, a failed
    /// write - has been audited by <see cref="SaveAndAuditAsync"/> and is announced here, once.
    /// Dispatch used to live in the page, so each caller had to remember it and the undo path
    /// did not (aud-a26); a caller now gets the notice by calling this and cannot forget it. An
    /// exception that escapes is not announced here; both callers' catches announce it.
    /// </remarks>
    public async Task<AttributeSaveResult> SaveAsync(
        ResolvedDirectoryPrincipal target,
        Dictionary<string, string?> proposedValues,
        string performedBy,
        string ip,
        string ticket,
        // REQUIRED and not defaulted: the servicer decision needs a real principal, and a default
        // would silently make every caller that forgot it unable to service - or invite an ambient
        // lookup, which attributes a bypass to whoever is on the thread. Null refuses.
        System.Security.Claims.ClaimsPrincipal? actingUser,
        int? maxLevel = null,
        // Set by the undo path only, so the notice reads as a reversal rather than another update.
        AttributeEditReversal? reversal = null)
    {
        var result = await SaveAndAuditAsync(target, proposedValues, performedBy, ip, ticket, actingUser, maxLevel);
        await NotifyAdministratorsAsync(target, performedBy, ip, ticket, result, reversal);
        return result;
    }

    private async Task<AttributeSaveResult> SaveAndAuditAsync(
        ResolvedDirectoryPrincipal target,
        Dictionary<string, string?> proposedValues,
        string performedBy,
        string ip,
        string ticket,
        System.Security.Claims.ClaimsPrincipal? actingUser,
        int? maxLevel)
    {
        var allowlist = GetAllowlist();
        if (allowlist == null)
        {
            const string msg = "Attribute allowlist configuration is corrupt.";
            LogRefusal(target, performedBy, ip, ticket, "AllowlistConfig", msg);
            return new(false, msg, null);
        }

        // If a maxLevel is provided, filter the allowlist to enforce server-side level restrictions
        if (maxLevel.HasValue)
            allowlist = allowlist.Where(a => a.Level <= maxLevel.Value).ToList();

        var validationError = ValidateProposedValues(proposedValues, allowlist);
        if (validationError != null)
        {
            LogRefusal(target, performedBy, ip, ticket, "Validation", validationError);
            return new(false, validationError, null);
        }

        foreach (var key in proposedValues.Keys)
        {
            if (IsDenylisted(key))
            {
                var denied = $"Attribute '{key}' is on the hard denylist and cannot be modified.";
                LogRefusal(target, performedBy, ip, ticket, "Denylist", denied);
                return new(false, denied, null);
            }
        }

        var protectionResult = await _protectedPrincipalService.CheckAsync(target);
        if (protectionResult.CheckFailed)
        {
            LogRefusal(target, performedBy, ip, ticket, "ProtectedPrincipalCheck", protectionResult.Reason);
            return new(false, protectionResult.Reason, null);
        }

        // An authorised servicer may proceed. Protection is evaluated first and never weakened -
        // this only decides whether THIS operator may act on a target already known to be
        // protected. A null actingUser refuses, so any caller that does not supply one is safe.
        string? servicedNote = null;
        if (protectionResult.IsProtected)
        {
            servicedNote = ProtectedPrincipalServicing.NoteFor(
                _servicers, actingUser, ServicerModuleId, protectionResult.MatchedRules);

            if (servicedNote is null)
            {
                const string msg = "Target is a protected principal and cannot be modified.";
                LogRefusal(target, performedBy, ip, ticket, "ProtectedPrincipalCheck", msg);
                return new(false, msg, null);
            }
        }

        var creds = await GetCredentialsAsync("attribute save");
        if (creds == null)
        {
            const string msg = "AD credentials unavailable.";
            LogRefusal(target, performedBy, ip, ticket, "GetADCredentials", msg);
            return new(false, msg, null);
        }

        if (!await _adThrottle.WaitAsync(TimeSpan.FromMinutes(2)))
        {
            const string msg = "AD service is busy.";
            LogRefusal(target, performedBy, ip, ticket, "AcquireThrottle", msg);
            return new(false, msg, null);
        }

        try
        {
            return await Task.Run(() => PerformSave(target, proposedValues, creds.Value, allowlist, performedBy, ip, ticket, servicedNote));
        }
        finally
        {
            _adThrottle.Release();
        }
    }

    internal static bool IsDenylisted(string attributeName)
    {
        if (HardDenylistExact.Contains(attributeName))
            return true;

        foreach (var prefix in HardDenylistPrefixes)
        {
            if (attributeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private AttributeLookupResult PerformLookup(string identity, (string username, string password, string domain) creds, List<EditableAttribute> allowlist)
    {
        using var runspace = CreateDirectoryRunspace();
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ps.Commands.Clear();

        var credential = CreateCredential(creds.username, creds.password, creds.domain);
        var searchBases = GetConfiguredSearchBases();

        var escaped = ProtectedPrincipalService.EscapeLdapFilter(identity);
        var filter = $"(|(userPrincipalName={escaped})(mail={escaped})(sAMAccountName={escaped})(employeeID={escaped}))";
        var properties = GetPropertiesToLoad(allowlist);

        var allUsers = new List<PSObject>();
        var failedBases = new List<(string SearchBase, string Reason)>();

        if (searchBases.Length == 0)
        {
            ps.AddCommand("Get-ADUser")
              .AddParameter("LDAPFilter", filter)
              .AddParameter("Properties", properties)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            allUsers.AddRange(ps.Invoke());
            ps.Commands.Clear();

            if (ps.HadErrors)
            {
                var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "AD query failed.";
                ps.Streams.Error.Clear();
                return new(false, errMsg, null, null, false, null, null);
            }
        }
        else
        {
            // psl-6: each base is judged on its own invocation, here, before the next one runs.
            // HadErrors describes only the most recent invocation and survives
            // Streams.Error.Clear(), so the one check that used to follow this loop saw the LAST
            // base alone: a failure there failed a lookup that had already found the user, and a
            // failure on any earlier base was lost.
            foreach (var sb in searchBases)
            {
                ps.AddCommand("Get-ADUser")
                  .AddParameter("LDAPFilter", filter)
                  .AddParameter("SearchBase", sb)
                  .AddParameter("Properties", properties)
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "SilentlyContinue");
                try
                {
                    allUsers.AddRange(ps.Invoke());

                    // Under SilentlyContinue a cmdlet's error writes no record but still sets the
                    // flag, so the flag is the evidence and a record, when there is one, the detail.
                    if (ps.HadErrors)
                        failedBases.Add((sb, ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "the query reported an error without detail"));
                }
                catch (Exception ex)
                {
                    // A terminating error throws whatever the ErrorAction: an unreachable server,
                    // a search base that does not exist.
                    failedBases.Add((sb, ex.InnerException?.Message ?? ex.Message));
                }
                finally
                {
                    ps.Streams.Error.Clear();
                    ps.Commands.Clear();
                }
            }
        }

        var users = allUsers
            .GroupBy(u => u.Properties["ObjectGUID"]?.Value?.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (users.Count == 0)
        {
            // Nobody found while a base did not answer: the user may be in that base, so this is a
            // lookup that could not be completed, and it says where - not "not found".
            if (failedBases.Count > 0)
                return new(false, $"AD lookup for '{identity}' could not be completed: {DescribeFailedBases(failedBases)}.", null, null, false, null, null);

            return new(false, $"User '{identity}' not found.", null, null, false, null, null);
        }

        // Found, while another base did not answer: the lookup stands and the failure is logged
        // against its base (owner's rule for psl-6). The ambiguity check below sees only the bases
        // that answered.
        if (failedBases.Count > 0)
        {
            _logger.LogWarning(
                "AD attribute lookup for {Identity} found a match, but {Count} search base(s) could not be searched: {FailedBases}",
                identity, failedBases.Count, DescribeFailedBases(failedBases));
        }

        if (users.Count > 1)
            return new(false, $"Ambiguous: '{identity}' matches {users.Count} AD users. Use a more specific identifier.", null, null, false, null, null);

        var adUser = users[0];
        var dn = adUser.Properties["DistinguishedName"]?.Value?.ToString();

        if (!IsWithinSearchBases(dn, searchBases))
        {
            return new(false, "User is outside the configured search base boundaries.", null, null, true, "Outside allowed OU scope.", null);
        }

        var principal = new ResolvedDirectoryPrincipal(
            Source: "OnPremAD",
            DisplayName: adUser.Properties["DisplayName"]?.Value?.ToString() ?? adUser.Properties["Name"]?.Value?.ToString() ?? identity,
            UserPrincipalName: adUser.Properties["UserPrincipalName"]?.Value?.ToString() ?? "",
            SamAccountName: adUser.Properties["SamAccountName"]?.Value?.ToString(),
            PrimarySmtpAddress: adUser.Properties["mail"]?.Value?.ToString(),
            DistinguishedName: dn,
            ObjectGuid: adUser.Properties["ObjectGUID"]?.Value?.ToString(),
            EntraObjectId: null);

        var currentValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var attr in allowlist)
        {
            var val = adUser.Properties[attr.Name]?.Value;
            currentValues[attr.Name] = val?.ToString();
        }

        return new(true, null, principal, "OnPremAD", false, null, currentValues);
    }

    /// <summary>Each failed search base with its reason, for the operator's message and the log (psl-6).</summary>
    private static string DescribeFailedBases(IEnumerable<(string SearchBase, string Reason)> failedBases) =>
        string.Join("; ", failedBases.Select(f => $"search base '{f.SearchBase}' failed ({f.Reason})"));

    private AttributeSavePreview ComputePreview(
        ResolvedDirectoryPrincipal target,
        Dictionary<string, string?> proposedValues,
        (string username, string password, string domain) creds,
        List<EditableAttribute> allowlist)
    {
        var currentValues = ReReadCurrentValues(target, creds, allowlist);
        if (currentValues == null)
            return new(false, "Failed to re-read target object for preview.", null);

        var changes = ComputeChanges(proposedValues, currentValues, allowlist);
        return new(true, null, changes);
    }

    private AttributeSaveResult PerformSave(
        ResolvedDirectoryPrincipal target,
        Dictionary<string, string?> proposedValues,
        (string username, string password, string domain) creds,
        List<EditableAttribute> allowlist,
        string performedBy,
        string ip,
        string ticket,
        string? servicedNote)
    {
        using var runspace = CreateDirectoryRunspace();
        using var ps = PowerShell.Create();
        PSCredential credential;
        System.Collections.ObjectModel.Collection<PSObject> reReadResult;

        // aud-a28c: under -ErrorAction Stop everything before the write fails by THROWING - the
        // module missing, a domain controller unreachable, access denied, and an object moved or
        // deleted since the lookup, which Get-ADUser -Identity reports by throwing rather than by
        // returning nothing. Uncaught, the attempt left no audit record. Caught here, it gets the
        // failed record the checks below write, and a failed result SaveAsync announces.
        try
        {
            runspace.Open();
            ps.Runspace = runspace;

            ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            credential = CreateCredential(creds.username, creds.password, creds.domain);

            ps.AddCommand("Get-ADUser")
              .AddParameter("Identity", target.DistinguishedName)
              .AddParameter("Properties", GetPropertiesToLoad(allowlist))
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            reReadResult = ps.Invoke();
            ps.Commands.Clear();
        }
        catch (Exception ex)
        {
            var reReadMsg = $"Target object could not be re-read before the write: {ex.InnerException?.Message ?? ex.Message}";
            LogAudit(target, [], performedBy, ip, ticket, false, reReadMsg);
            return new(false, reReadMsg, null);
        }

        if (reReadResult.Count == 0)
        {
            const string notFoundMsg = "Target object no longer exists or could not be re-read.";
            LogAudit(target, [], performedBy, ip, ticket, false, notFoundMsg);
            return new(false, notFoundMsg, null);
        }

        var reReadUser = reReadResult[0];
        var reReadGuid = reReadUser.Properties["ObjectGUID"]?.Value?.ToString();
        if (!string.IsNullOrEmpty(target.ObjectGuid) && !string.Equals(reReadGuid, target.ObjectGuid, StringComparison.OrdinalIgnoreCase))
        {
            const string mismatchMsg = "Bound-object mismatch: the resolved object no longer matches the lookup snapshot.";
            LogAudit(target, [], performedBy, ip, ticket, false, mismatchMsg);
            return new(false, mismatchMsg, null);
        }

        var reReadDn = reReadUser.Properties["DistinguishedName"]?.Value?.ToString();
        if (!IsWithinSearchBases(reReadDn, GetConfiguredSearchBases()))
        {
            const string boundaryMsg = "Target is outside the configured search base boundaries. The account may have been moved since lookup.";
            LogAudit(target, [], performedBy, ip, ticket, false, boundaryMsg);
            return new(false, boundaryMsg, null);
        }

        var currentValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var attr in allowlist)
            currentValues[attr.Name] = reReadUser.Properties[attr.Name]?.Value?.ToString();

        var changes = ComputeChanges(proposedValues, currentValues, allowlist);
        if (changes.Count == 0)
        {
            // aud-a26d: SaveAsync announces this result too, so it needs a record to answer to.
            LogNoChangeAudit(target, performedBy, ip, ticket, servicedNote);
            return new(true, "No changes to apply.", changes);
        }

        var replace = new Dictionary<string, object?>();
        var clear = new List<string>();

        foreach (var change in changes)
        {
            if (string.IsNullOrEmpty(change.NewValue))
                clear.Add(change.Name);
            else
                replace[change.Name] = change.NewValue;
        }

        // Single Set-ADUser call with both -Replace and -Clear to avoid partial-write risk
        ps.AddCommand("Set-ADUser")
          .AddParameter("Identity", target.DistinguishedName)
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        if (replace.Count > 0) ps.AddParameter("Replace", replace);
        if (clear.Count > 0) ps.AddParameter("Clear", clear.ToArray());

        // -ErrorAction Stop makes real Set-ADUser failures (access denied,
        // constraint violation, unreachable DC) THROW from Invoke() - they never
        // reach the HadErrors branch below. Catch, audit, and return a failed
        // result; a failed mutation with no audit record violates the
        // Constitution's audit invariant.
        try
        {
            ps.Invoke();
        }
        catch (Exception ex)
        {
            var thrownMsg = ex.InnerException?.Message ?? ex.Message;
            LogAudit(target, changes, performedBy, ip, ticket, false, thrownMsg);
            return new(false, thrownMsg, null);
        }
        ps.Commands.Clear();

        if (ps.HadErrors)
        {
            var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Set-ADUser failed.";
            LogAudit(target, changes, performedBy, ip, ticket, false, errMsg);
            return new(false, errMsg, null);
        }

        LogAudit(target, changes, performedBy, ip, ticket, true, null, servicedNote);

        return new(true, null, changes);
    }

    private Dictionary<string, string?>? ReReadCurrentValues(
        ResolvedDirectoryPrincipal target,
        (string username, string password, string domain) creds,
        List<EditableAttribute> allowlist)
    {
        try
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            var credential = CreateCredential(creds.username, creds.password, creds.domain);

            ps.AddCommand("Get-ADUser")
              .AddParameter("Identity", target.DistinguishedName)
              .AddParameter("Properties", GetPropertiesToLoad(allowlist))
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var result = ps.Invoke();
            ps.Commands.Clear();

            if (result.Count == 0) return null;

            var reReadDn = result[0].Properties["DistinguishedName"]?.Value?.ToString();
            if (!IsWithinSearchBases(reReadDn, GetConfiguredSearchBases()))
                return null;

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in allowlist)
                values[attr.Name] = result[0].Properties[attr.Name]?.Value?.ToString();
            return values;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to re-read current values for {Target}", target.UserPrincipalName);
            return null;
        }
    }

    private static List<AttributeChange> ComputeChanges(
        Dictionary<string, string?> proposed,
        Dictionary<string, string?> current,
        List<EditableAttribute> allowlist)
    {
        var changes = new List<AttributeChange>();
        foreach (var kv in proposed)
        {
            var attr = allowlist.FirstOrDefault(a => string.Equals(a.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
            if (attr == null) continue;

            current.TryGetValue(kv.Key, out var oldValue);
            var newValue = string.IsNullOrEmpty(kv.Value) ? null : kv.Value;
            var oldNorm = string.IsNullOrEmpty(oldValue) ? null : oldValue;

            if (!string.Equals(oldNorm, newValue, StringComparison.Ordinal))
                changes.Add(new AttributeChange(attr.Name, attr.Label, oldNorm, newValue));
        }
        return changes;
    }

    private string? ValidateProposedValues(Dictionary<string, string?> proposedValues, List<EditableAttribute> allowlist)
    {
        foreach (var kv in proposedValues)
        {
            if (IsDenylisted(kv.Key))
                return $"Attribute '{kv.Key}' is on the hard denylist and cannot be modified.";

            var attr = allowlist.FirstOrDefault(a => string.Equals(a.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
            if (attr == null)
                return $"Attribute '{kv.Key}' is not in the allowlist.";

            var value = kv.Value;

            if (string.IsNullOrEmpty(value))
            {
                if (attr.Required)
                    return $"Attribute '{attr.Label}' is required and cannot be blank.";
                if (!attr.AllowClear)
                    return $"Attribute '{attr.Label}' cannot be cleared.";
                continue;
            }

            if (attr.MaxLength.HasValue && value.Length > attr.MaxLength.Value)
                return $"Attribute '{attr.Label}' exceeds maximum length of {attr.MaxLength.Value}.";

            // aud-a28e. The pattern is the allowlist's, set by an administrator, not the operator's.
            // One that does not compile (ArgumentException), or that runs past RegexTimeout on this
            // value (RegexMatchTimeoutException), used to throw out of SaveAsync ahead of LogRefusal
            // and the notice, so the attempt left no audit record. Returned as a validation error
            // HERE - the one place that knows which attribute it was - it becomes the audited
            // Validation refusal SaveAsync announces. The message blames the allowlist because the
            // operator cannot fix it and must not be left guessing at a value that was never checked.
            // It refuses: an unusable pattern is never counted as a match.
            if (!string.IsNullOrEmpty(attr.Pattern))
            {
                bool matches;
                try
                {
                    matches = Regex.IsMatch(value, attr.Pattern, RegexOptions.None, RegexTimeout);
                }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
                {
                    return $"The allowlist pattern for attribute '{attr.Label}' "
                        + (ex is RegexMatchTimeoutException ? "took too long to evaluate" : "is not a valid regular expression")
                        + ", so the value could not be checked. An administrator must correct the attribute allowlist.";
                }

                if (!matches)
                    return $"Attribute '{attr.Label}' does not match the required pattern.";
            }

            if (attr.Type == "Choice")
            {
                var effective = GetEffectiveChoices(attr);
                if (effective.Length > 0 && !effective.Contains(value, StringComparer.OrdinalIgnoreCase))
                    return $"Attribute '{attr.Label}' must be one of the configured values.";
            }
        }

        return null;
    }

    /// <summary>
    /// Records an attempt that was refused before any write was tried.
    /// </summary>
    /// <remarks>
    /// Audit finding A28. Every refusal in <c>SaveAsync</c> - corrupt allowlist, failed
    /// validation, a denylisted attribute, a failed or blocking protected-principal check,
    /// missing credentials, a busy throttle - returned before <see cref="LogAudit"/> and so
    /// left nothing in the audit log. The operation trace saw them; the trace is diagnostic,
    /// with different retention and different readers. "Who tried to edit this protected
    /// account and was stopped?" is asked of the audit log, which did not know.
    ///
    /// <c>mutationAttempted</c> is false because that is the material difference between this
    /// row and a failed write: nothing was sent to the directory.
    /// </remarks>
    private void LogRefusal(
        ResolvedDirectoryPrincipal target,
        string performedBy,
        string ip,
        string ticket,
        string step,
        string reason)
    {
        _audit.LogModuleAction(
            performedBy,
            ip,
            "ADAttributeEdit_Refused",
            "ADAttributeEditor",
            target.UserPrincipalName,
            false,
            ticket,
            reason,
            new Dictionary<string, object?> { ["refusedAt"] = step, ["mutationAttempted"] = false });
    }

    private void LogAudit(ResolvedDirectoryPrincipal target, List<AttributeChange> changes, string performedBy, string ip, string ticket, bool success, string? errorDetail, string? servicedNote = null)
    {
        var changedAttrs = changes.Select(c => c.Name).ToArray();
        var details = new Dictionary<string, object?>
        {
            ["target"] = target.UserPrincipalName,
            ["changedAttributes"] = changedAttrs,
            ["changes"] = changes.Select(c => new { c.Name, c.OldValue, c.NewValue }).ToArray()
        };

        _operationTrace.Step("AttributeWriteCompleted", success ? "Success" : "Failed", details: details);

        // Audit finding A9. The UPN is a label and can be reassigned to a different person;
        // the GUID cannot. Undo resolves this record back to an object, so without the GUID
        // there is nothing for it to prove it reached the same one. Recorded here so the undo
        // path has an immutable id to check - and so that a record WITHOUT one is detectable
        // as older rather than indistinguishable from a verified match.
        var extra = ProtectedPrincipalServicing.Extra(servicedNote) ?? new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(target.ObjectGuid))
            extra["targetObjectGuid"] = target.ObjectGuid;

        _audit.LogADAttributeEdit(performedBy, ip, target.UserPrincipalName, changes, success, ticket, errorDetail, extra: extra);
    }

    private const string NoChangeNeeded =
        "No change was needed: the directory already held the requested values, so nothing was written.";

    /// <summary>
    /// Records a save that found nothing to change (aud-a26d).
    /// </summary>
    /// <remarks>
    /// SaveAsync announces every result it returns (aud-a26b), this one included, so without this
    /// administrators were sent an <c>ADAttributeEditor_Update</c> notice that nothing in the audit
    /// log answered to. The record is that action, successful, with no changes, and
    /// <c>mutationAttempted</c> false because nothing was sent to the directory - the session only
    /// re-read the target. With no old_/new_ pair it offers no undo. Not <see cref="LogAudit"/>,
    /// whose trace step says a write completed.
    /// </remarks>
    private void LogNoChangeAudit(ResolvedDirectoryPrincipal target, string performedBy, string ip, string ticket, string? servicedNote)
    {
        var extra = ProtectedPrincipalServicing.Extra(servicedNote) ?? new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(target.ObjectGuid))
            extra["targetObjectGuid"] = target.ObjectGuid;
        extra["mutationAttempted"] = false;
        extra["detail"] = NoChangeNeeded;

        _audit.LogADAttributeEdit(performedBy, ip, target.UserPrincipalName, [], true, ticket, extra: extra);
    }

    /// <summary>The forward notice's <c>Changed</c> row when the save found nothing to change.</summary>
    internal const string NothingNeededChanging = "(none - the directory already held the requested values)";

    /// <summary>
    /// Tells administrators how one save ended (aud-a26b). SaveAsync calls this once for every
    /// result it returns.
    /// </summary>
    /// <remarks>
    /// The forward notice keeps the action and rows the page used to send. <c>Changed</c> names
    /// what was actually applied, which only this service knows; the page could only name what it
    /// asked for. A reversal is announced with the undo's content instead
    /// (<see cref="AttributeEditReversal"/>).
    ///
    /// A failed send is logged and swallowed. The directory has been written, or deliberately not,
    /// and the audit record exists either way; a mail server being down must not change the result
    /// the caller is given.
    /// </remarks>
    private async Task NotifyAdministratorsAsync(
        ResolvedDirectoryPrincipal target,
        string performedBy,
        string ip,
        string ticket,
        AttributeSaveResult result,
        AttributeEditReversal? reversal)
    {
        var error = result.Success ? null : result.Error;
        try
        {
            if (reversal is not null)
            {
                await _email.SendAdminNotificationAsync(
                    performedBy, ip, AttributeEditReversal.NoticeAction, result.Success, ticket,
                    reversal.NoticeDetails(result.Success, error), error);
                return;
            }

            var details = new Dictionary<string, string> { ["Target"] = target.UserPrincipalName };
            if (result.Success)
            {
                details["Changed"] = result.AppliedChanges is { Count: > 0 } applied
                    ? string.Join(", ", applied.Select(c => c.Name))
                    : NothingNeededChanging;
            }
            else
            {
                details["Error"] = error ?? "Unknown";
            }

            await _email.SendAdminNotificationAsync(
                performedBy, ip, "ADAttributeEditor_Update", result.Success, ticket, details, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email notification failed for AD attribute edit of {Target}", target.UserPrincipalName);
        }
    }

    private string[] GetConfiguredSearchBases()
    {
        var config = _moduleConfig.GetValue("ADAttributeEditor", "DefaultSearchBase");
        if (string.IsNullOrWhiteSpace(config))
            return [];
        return config.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsWithinSearchBases(string? dn, string[] searchBases)
    {
        if (searchBases.Length == 0 || string.IsNullOrEmpty(dn))
            return true;
        return searchBases.Any(sb =>
            dn.EndsWith("," + sb, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dn, sb, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] GetPropertiesToLoad(List<EditableAttribute> allowlist)
    {
        var props = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DisplayName", "UserPrincipalName", "SamAccountName",
            "mail", "DistinguishedName", "ObjectGUID", "Name"
        };
        foreach (var attr in allowlist)
            props.Add(attr.Name);
        return props.ToArray();
    }

    private static PSCredential CreateCredential(string username, string password, string domain)
    {
        var fullUsername = username.Contains('\\') || username.Contains('@')
            ? username : $"{domain}\\{username}";
        var securePassword = new System.Security.SecureString();
        foreach (var c in password) securePassword.AppendChar(c);
        return new PSCredential(fullUsername, securePassword);
    }

    private sealed class AttributeAllowlistFile
    {
        public AttributeAllowlistEntry[]? Attributes { get; set; }
    }

    private sealed class AttributeAllowlistEntry
    {
        public string Name { get; set; } = "";
        public string Label { get; set; } = "";
        public string Type { get; set; } = "String";
        public string[]? Choices { get; set; }
        public bool Required { get; set; }
        public bool AllowClear { get; set; }
        public int? MaxLength { get; set; }
        public string? Pattern { get; set; }
        public int Level { get; set; } = 1;
    }
}
