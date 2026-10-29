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

// ReadOnlyAttributes (aud-a26u): each allowlisted attribute the editor will not write for this object - it holds several
// values, or a binary value - by name, with the reason the page shows in place of its input. The save refuses such an
// attribute as well; this is what lets the page say so before anyone types. Null from every lookup that failed.
public sealed record AttributeLookupResult(
    bool Success,
    string? Error,
    ResolvedDirectoryPrincipal? Principal,
    string? SourceOfAuthority,
    bool IsReadOnly,
    string? ReadOnlyReason,
    Dictionary<string, string?>? CurrentValues,
    IReadOnlyDictionary<string, string>? ReadOnlyAttributes = null);

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
/// <param name="Version">After a save that went through, the version the allowlist now has (aud-a28i).</param>
public sealed record AllowlistSaveResult(bool Success, string? Error, bool StoreCorrupt = false, string? Version = null);

/// <summary>
/// The allowlist as the Module Configuration page loads it for editing (aud-a28i).
/// </summary>
/// <param name="Attributes">The validated list; null when the store is corrupt.</param>
/// <param name="Version">What a save from this load is checked against: the stored rows' own content.</param>
public sealed record AllowlistForEditing(List<EditableAttribute>? Attributes, string Version);

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

    // Set when a legacy ad-editable-attributes.json exists, the DB allowlist is not yet configured,
    // and the import refused the file - unparseable, no Attributes section, a row that fails
    // validation, a pattern that does not compile (aud-a26e), or a store that would not take it. The
    // allowlist's fail-closed signal is NULL (distinct from the valid empty []), so during the
    // upgrade window a refused file must make the load return null until it is repaired/removed -
    // never silently degrade to an empty allowlist. The file stays on disk, so this re-trips on every
    // construction.
    private readonly bool _legacyAllowlistCorrupt;

    // Why the import refused the file, in words an administrator can act on (aud-a26f). The log was
    // the only place that named the attribute; the Module Configuration page now shows this.
    private readonly string? _legacyImportRefusal;

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

        _legacyImportRefusal = ImportLegacyIfPresent();
        _legacyAllowlistCorrupt = _legacyImportRefusal is not null;
    }

    /// <summary>
    /// Why the legacy <c>config/ad-editable-attributes.json</c> was not imported, when that is why the
    /// allowlist reads as corrupt (aud-a26f); null otherwise.
    /// </summary>
    public string? LegacyAllowlistImportRefusal => _legacyImportRefusal;

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
    /// True when the allowlist in the config database (<c>editable_attribute</c>) cannot be read or
    /// fails validation, or when the legacy import refused its file (aud-a26f). Reads the store
    /// fresh on every call - it does NOT consult the 30-second cache - because GetAllowlist() can
    /// return a stale-but-valid cached list for up to the TTL after the store becomes corrupt.
    /// Mirrors SectionAccessService.IsFragmentCorrupt (incident 2026-06-12 corrupt-store class). The
    /// allowlist save no longer calls this: it decides inside its own write (aud-a28i).
    /// </summary>
    public bool IsAllowlistCorrupt() => LoadAllowlistFromDisk() == null;

    /// <summary>
    /// Reads and validates the allowlist from the store with no caching. Returns an empty list
    /// when nothing is configured (a valid "nothing allowlisted" state) and null when the store
    /// is unreadable or its rows fail validation (the fail-closed/corrupt state).
    /// </summary>
    private List<EditableAttribute>? LoadAllowlistFromDisk()
    {
        // Fail closed (null) if a corrupt legacy file is still on disk during the upgrade window.
        if (_legacyAllowlistCorrupt)
            return null;

        return Evaluate(_repository.ReadAllowlistSnapshot());
    }

    /// <summary>
    /// The allowlist a read of the store describes: empty when nothing is configured, null when the
    /// store is unreadable or its rows fail to project or validate - the fail-closed signal.
    /// </summary>
    /// <remarks>
    /// One judgement for every reader - the cached load, the editing load and the save, which calls it
    /// on the rows its own write read (aud-a28i) - so the save can never call corrupt a store the page
    /// was shown as valid, or the reverse.
    /// </remarks>
    private List<EditableAttribute>? Evaluate(AllowlistSnapshot snapshot)
    {
        if (_legacyAllowlistCorrupt)
            return null;

        if (!snapshot.Readable)
        {
            _logger.LogError("ad-editable-attributes store unreadable - failing closed");
            return null;
        }

        if (!snapshot.Configured)
            return []; // nothing configured - valid empty allowlist

        // Projecting rows can throw if a stored choices_json is malformed (operator/promote edit
        // or partial corruption). That is a corrupt-store condition -> return null (the documented
        // fail-closed signal), never let it throw through callers as a 500.
        List<AttributeAllowlistEntry> entries;
        try
        {
            entries = snapshot.Rows.Select(ToAllowlistEntry).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ad-editable-attributes store has a malformed row (choices_json) - failing closed");
            return null;
        }

        return ValidateRows(entries);
    }

    /// <summary>
    /// The allowlist as the Module Configuration page edits it, read fresh, with the version a save
    /// from that page is checked against (aud-a28i).
    /// </summary>
    /// <remarks>
    /// The list and the version come from the same single read. Two reads could straddle another
    /// admin's save, and the page would show the old rows with the new version - which is exactly
    /// the save the version exists to refuse. Not the cache, for the same reason.
    /// </remarks>
    public AllowlistForEditing LoadAllowlistForEditing()
    {
        var snapshot = _repository.ReadAllowlistSnapshot();
        return new(Evaluate(snapshot), AllowlistVersion(snapshot));
    }

    /// <summary>The allowlist's own content as a version: every stored row in name order, and the presence marker.</summary>
    /// <remarks>
    /// Not the store's change token. The token moves on every config write by either instance on the
    /// shared database - a section-access save would make every open allowlist page refuse - and it
    /// misses a writer that bypasses the store (aud-a33b's reasons). The rows themselves are exact: a
    /// save is refused only when the allowlist itself changed. The marker is in it so that an empty
    /// allowlist someone configured is not the same version as one never configured.
    /// </remarks>
    internal static string AllowlistVersion(AllowlistSnapshot snapshot)
    {
        if (!snapshot.Readable)
            return "unreadable";

        var canonical = JsonSerializer.Serialize(new
        {
            snapshot.Configured,
            Rows = snapshot.Rows
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Name, StringComparer.Ordinal)
                .ToList(),
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    // Validation (denylist removal, contradictory flags, empty Choice list) - unchanged from the
    // file world; returns null to fail closed, matching LoadAllowlistFromDisk's contract.
    private List<EditableAttribute>? ValidateRows(IEnumerable<AttributeAllowlistEntry> entries) =>
        ValidateRows(entries, out _);

    private List<EditableAttribute>? ValidateRows(IEnumerable<AttributeAllowlistEntry> entries, out string? problem)
    {
        problem = null;
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
                problem = $"Attribute '{attr.Label}' ({attr.Name}) is both Required and AllowClear.";
                return null;
            }

            if (attr.Type == "Choice" && (attr.Choices == null || attr.Choices.Length == 0))
            {
                _logger.LogError("Attribute {Name} is type Choice but has no Choices defined - failing closed", attr.Name);
                problem = $"Attribute '{attr.Label}' ({attr.Name}) is type Choice but has no choices.";
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
    /// <param name="loadedVersion">The version the page's load returned (<see cref="LoadAllowlistForEditing"/>).</param>
    /// <param name="cancellationToken">kd-58 B3. The Module Configuration page's token for this save, cancelled only when the route change stops waiting for it: handed to the store's write, so a cancel before the commit leaves nothing written and is this method's PageMoved refusal. None, the default, saves as before.</param>
    /// <remarks>
    /// The save used to run in the page. Its refusals and its catch wrote no record, and its one
    /// record - on success - passed an empty list as the previous allowlist, so every save read as
    /// adding every attribute and removing none. It lives here so every one of those paths can be
    /// driven by a test; the page keeps only the re-check, which needs the principal and the policies.
    ///
    /// Every decision is made inside one write (aud-a28i). It used to read the store, decide, and
    /// replace the table in a later transaction, so a save landing in between - another admin's, or
    /// the other instance's on the shared database - was overwritten, and the success record's
    /// before-state was the stale read. Now the rows are read under the write lock the replace holds,
    /// and a page whose load is older than those rows is refused rather than allowed to replace them:
    /// the admin reloads and re-applies, and nothing is merged or guessed. The lock alone would still
    /// let a stale page overwrite; a compare made outside the write would leave the window open.
    ///
    /// It does not throw. Every outcome is recorded here and returned, which is what lets the page's
    /// catch write the one record for an attempt that failed before reaching this method without
    /// risking a second. The records are written after the transaction, which a refusal rolls back.
    ///
    /// It is the service's only allowlist writer (aud-a28l). A public SaveAllowlist stood beside it with
    /// no caller left, writing with neither the version check nor a record, so any later caller would
    /// have gone round both; it was removed rather than kept as a backstop. AllowlistConcurrentSaveTests
    /// holds the line, here and at the repository.
    /// </remarks>
    public AllowlistSaveResult SaveAllowlistAndAudit(
        List<EditableAttribute> attributes, string loadedVersion, string performedBy, string ip,
        CancellationToken cancellationToken = default)
    {
        AllowlistRefusal? refusal = null;
        List<AttributeRow> written = [];
        List<AllowlistFieldChange> changed = [];
        AllowlistReplaceOutcome outcome;
        try
        {
            outcome = _repository.ReplaceAllowlist(stored =>
            {
                refusal = Refuse(stored, attributes, loadedVersion);
                if (refusal is not null)
                    return null;

                // Everything the success record says is worked out before the write, so a throw here is
                // a failure with nothing written, never a completed write reported as failed.
                written = RowsAsWritten(attributes);
                changed = DescribeChangedRows(stored.Rows.ToList(), written);
                return attributes.Select(ToAllowlistRow).ToList();
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // kd-58 B3 (docs/CancellationDepth-Plan.md 4.6). The Module Configuration page gave up on this save - its route
            // change stopped waiting and cancelled the page's token - and the store rolled the write back whole before its
            // commit (B1): nothing was written. The refusal the page records for the same move before the write (aud-a66), in
            // its words; recorded here, as every outcome of this save is (aud-a28l), so the page writes nothing for it.
            LogAllowlistSaveRefused(performedBy, ip, "PageMoved", SettingsSaveAudit.PageMovedRefusal);
            return new(false, SettingsSaveAudit.PageMovedRefusal);
        }
        catch (Exception ex)
        {
            // aud-a67 (the aud-a40 class, inside this service). A store another writer holds past the wait is a refusal,
            // not a failure: nothing was written - every decision above is made inside the one write a throw rolls back -
            // so it is recorded with refusedAt StoreBusy and mutationAttempted false, and the admin is told the store was
            // busy and to try again, where they used to be shown "Failed to save: SQLite Error 5: 'database is locked'."
            if (ConfigStoreBusy.IsBusy(ex))
            {
                var busy = SettingsSaveAudit.StoreBusyMessage();
                _logger.LogWarning(ex, "Allowlist save not made: the settings store was busy");
                LogAllowlistSaveRefused(performedBy, ip, SettingsSaveAudit.StoreBusy, busy);
                return new(false, busy);
            }

            // No refusedAt and no mutationAttempted: the write may have been tried, and a failure
            // record must not claim otherwise.
            var failed = $"Failed to save: {ex.Message}";
            _logger.LogError(ex, "Allowlist save failed");
            LogAllowlistSaveFailed(performedBy, ip, failed);
            return new(false, failed);
        }

        if (refusal is not null)
        {
            LogAllowlistSaveRefused(performedBy, ip, refusal.Step, refusal.Reason);
            return new(false, refusal.Reason, refusal.StoreCorrupt);
        }

        InvalidateAllowlistCache();

        // Outside the try that covers the write, and in its own: the allowlist has been saved, and an
        // audit failure must not make a completed save look failed (Constitution, Auditing). Written
        // even when nothing differs, so every attempt leaves a record.
        try
        {
            _audit.LogSettingsChange(
                performedBy,
                ip,
                AllowlistAuditSection,
                outcome.Before.Rows.Select(r => r.Name).ToArray(),
                written.Select(r => r.Name).ToArray(),
                changed.Count > 0 ? new Dictionary<string, object?> { ["changed"] = changed } : null);
        }
        catch (Exception auditEx)
        {
            _logger.LogError(auditEx, "Audit failed for allowlist save");
        }

        return new(true, null, Version: AllowlistVersion(outcome.After!));
    }

    private sealed record AllowlistRefusal(string Step, string Reason, bool StoreCorrupt = false);

    internal const string ConcurrentChangeRefusal =
        "The attribute allowlist was changed after this page loaded it, so nothing was saved. Reload the page and re-apply your changes.";

    /// <summary>
    /// Why a save of <paramref name="attributes"/> over <paramref name="stored"/> must not be written,
    /// or null when it may be. Called inside the save's write, so the store it judges is the one the
    /// write replaces.
    /// </summary>
    /// <remarks>
    /// The store comes first: whether a save can be made at all - it is not corrupt, and it is still
    /// what the page loaded - and then what this admin's rows hold. A corrupt store is reported even
    /// when the rows are also wrong, because that refusal is the one that tells the page to show its
    /// corrupt banner and stop offering a save; and a row problem found on a stale page would only be
    /// found again after the reload the conflict asks for.
    /// </remarks>
    private AllowlistRefusal? Refuse(AllowlistSnapshot stored, List<EditableAttribute> attributes, string loadedVersion)
    {
        // The store's own judgement on the rows read under the lock: the authoritative gate. Saving
        // over a corrupt store would discard whatever it holds, so it fails closed.
        if (Evaluate(stored) is null)
            return new("AllowlistConfig", DescribeCorruptStore(), StoreCorrupt: true);

        if (!string.Equals(loadedVersion, AllowlistVersion(stored), StringComparison.Ordinal))
            return new("ConcurrentChange", ConcurrentChangeRefusal);

        if (DescribeBlankRows(attributes) is { } blank)
            return new("BlankRow", blank);

        if (DescribeRepeatedNames(attributes) is { } repeated)
            return new("DuplicateName", repeated);

        var denied = attributes.Where(a => IsDenylisted(a.Name)).ToList();
        if (denied.Count > 0)
            return new("Denylist", $"Cannot save: {string.Join(", ", denied.Select(d => d.Name))} are on the hard denylist.");

        // Checked here, before the write, so an uncompilable pattern is recorded as what it is - a
        // refusal, nothing tried - rather than as a failed write.
        if (attributes.Select(DescribeUncompilablePattern).FirstOrDefault(p => p is not null) is { } uncompilable)
            return new("PatternCheck", uncompilable);

        return null;
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

    /// <summary>The corrupt-store refusal, naming the store that holds the problem and its fix (aud-a26f).</summary>
    /// <remarks>
    /// It used to send every administrator to <c>config/ad-editable-attributes.json</c>, which nothing
    /// has read since the legacy import: an admin following it edited a file to no effect. The file is
    /// named only where it is the thing to fix - the import refused it - and then with the import's
    /// reason. Otherwise the problem is in the database, and the page's table is disabled in this
    /// state, so the fix named is the documented database repair, with both pools stopped because
    /// they share the file.
    /// </remarks>
    private string DescribeCorruptStore() =>
        _legacyImportRefusal is { } import
            ? "Allowlist store is corrupt; refusing to save over it. The legacy file config/ad-editable-attributes.json "
              + $"was not imported: {import} The allowlist fails closed until the file imports: correct it, then reload this page."
            : "Allowlist store is corrupt; refusing to save over it. The editable_attribute table in the config database "
              + "cannot be read or fails validation: an administrator must correct it as docs/ConfigDbOperations.md "
              + "describes, with both application pools stopped, then reload this page.";

    /// <summary>
    /// Every row without an LDAP name or a label, by its position in the table (aud-a26f); null when
    /// there is none.
    /// </summary>
    /// <remarks>
    /// The page used to filter such a row out before the save, so it vanished from the allowlist
    /// without a word - an attribute the admin had half entered, or one whose label was cleared. It is
    /// refused instead, and the admin fills it in or removes it: nothing is dropped, and nothing is
    /// guessed for them. The position is the table's, counted from 1, because a blank row has nothing
    /// else to be known by.
    /// </remarks>
    private static string? DescribeBlankRows(List<EditableAttribute> attributes)
    {
        var blank = new List<string>();
        for (var i = 0; i < attributes.Count; i++)
        {
            var noName = string.IsNullOrWhiteSpace(attributes[i].Name);
            var noLabel = string.IsNullOrWhiteSpace(attributes[i].Label);
            if (noName && noLabel)
                blank.Add($"row {i + 1} has no LDAP name or label");
            else if (noName)
                blank.Add($"row {i + 1} ('{attributes[i].Label}') has no LDAP name");
            else if (noLabel)
                blank.Add($"row {i + 1} ({attributes[i].Name}) has no label");
        }

        return blank.Count == 0
            ? null
            : $"The allowlist was not saved: {string.Join("; ", blank)}. Every row needs an LDAP name and a label: fill in or remove each row named.";
    }

    /// <summary>
    /// Every LDAP name on more than one row, compared as the store compares them, ignoring case
    /// (aud-a26f); null when there is none.
    /// </summary>
    /// <remarks>
    /// The store keeps one row per name (its key is NOCASE), so the save used to merge the second row
    /// into the first - its values winning - without saying so. Which of two rows the admin meant is
    /// not the save's to decide; it refuses and names both.
    /// </remarks>
    private static string? DescribeRepeatedNames(List<EditableAttribute> attributes)
    {
        var repeated = attributes
            .Select((attribute, index) => (Name: attribute.Name.Trim(), Row: index + 1))
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"the LDAP name '{group.First().Name}' is on rows {string.Join(", ", group.Select(entry => entry.Row))}")
            .ToList();

        return repeated.Count == 0
            ? null
            : $"The allowlist was not saved: {string.Join("; ", repeated)}. An attribute can be listed once: remove or correct each repeated row.";
    }

    /// <summary>
    /// The rows a save of <paramref name="attributes"/> leaves in the store, so the record describes
    /// the store rather than the request. The repository's insert skips a blank name and upserts a
    /// repeated one (names compare case-insensitively): the first spelling stays, the later row's
    /// values win. The allowlist save refuses both before it gets here (aud-a26f); this stays exact
    /// for what the repository does.
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
    /// The wording of the allowlist save's PatternCheck refusal. It names the attribute and says an
    /// administrator must correct the pattern: the page shows the pattern as typed and never guesses
    /// at or repairs one (aud-a26e), so the fix is the admin's to make.
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
    // Returns why the allowlist file was refused, or null: each refusal says what an administrator
    // must correct, because the page that shows it has nothing else to go on (aud-a26f).
    private string? ImportLegacyIfPresent()
    {
        var configDir = Path.Combine(_env.ContentRootPath, "config");
        string? allowlistRefusal = null;

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
                    allowlistRefusal = $"It could not be parsed: {ex.Message}";
                }

                if (parseOk)
                {
                    if (wrapper?.Attributes == null)
                    {
                        _logger.LogError("Legacy ad-editable-attributes.json missing Attributes section - left in place, failing closed until repaired");
                        allowlistRefusal = "It has no Attributes section.";
                    }
                    else if (ValidateRows(wrapper.Attributes, out var invalid) == null)
                    {
                        // Parseable but fails validation (denylist/contradictory/empty-Choice) - the
                        // file world returned null here too. Leave the file, fail closed.
                        _logger.LogError("Legacy ad-editable-attributes.json fails validation - left in place, failing closed until repaired");
                        allowlistRefusal = invalid ?? "It fails validation.";
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
                        // keeps, because the import stores them all. The import runs as the service
                        // is constructed, with no page to tell, and repeats on each construction until
                        // the file is fixed, so the reason is both logged and returned: the Module
                        // Configuration page shows it in its corrupt banner (aud-a26f).
                        _logger.LogError(
                            "Legacy ad-editable-attributes.json was not imported: {Problem} It is left in place, and the allowlist fails closed until an administrator corrects the pattern.",
                            uncompilable);
                        allowlistRefusal = uncompilable;
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
                            allowlistRefusal = $"It could not be written to the config database: {ex.Message}";
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // A valid file we could not even read must also fail closed (parse errors are handled
            // above and already set allowlistRefusal).
            _logger.LogError(ex, "Failed to process legacy ad-editable-attributes.json - failing closed");
            allowlistRefusal = $"It could not be read: {ex.Message}";
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

        return allowlistRefusal;
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
    /// The runspace the lookup, save and preview paths open (PerformLookup, PerformSave, and since aud-a26o
    /// ReReadCurrentValues).
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
    /// This module's AD credential, for the lookup, save and (since aud-a26o) preview paths.
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

        // aud-a26o: through the credential seam the lookup and the save use. Fetched from the module's credential
        // service directly, the preview was out of every test's reach, its re-read untested.
        var creds = await GetCredentialsAsync("attribute preview");
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
        {
            // aud-a28g. The editor page used to refuse an attribute above the operator's level itself,
            // before calling here, with no record. It is refused here, recorded and announced like any
            // other result, in the page's words: the attribute IS allowlisted, just not at this level.
            // A name the allowlist does not hold at all is not a level question, and stays the
            // Validation refusal below ("not in the allowlist").
            var reachable = allowlist.Where(a => a.Level <= maxLevel.Value).ToList();
            var aboveLevel = proposedValues.Keys
                .Where(name => !reachable.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                               && allowlist.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (aboveLevel.Count > 0)
            {
                var levelDenied = $"Access denied: you do not have permission to edit {string.Join(", ", aboveLevel)}.";
                LogRefusal(target, performedBy, ip, ticket, "LevelCheck", levelDenied);
                return new(false, levelDenied, null);
            }

            allowlist = reachable;
        }

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

        ActiveDirectoryModuleImport.Import(ps);

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

            // psl-7: a record here is this read's own. The import clears the stream behind it, so a record it left
            // past -ErrorAction Stop can no longer fail the lookup in the read's name, as it did.
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

        // aud-a26r. Each value is read as the save, the preview and the read-back read it (ReadDirectoryValue). By ToString
        // a one-element collection was its type's name, in the form and in both undo conflict checks, which compare the
        // lookup's reading with the element the save's record holds - so an edit of such an attribute could never be
        // undone. Several values read as their join, a binary value as "binary (N bytes)".
        // aud-a26u. And each attribute that is not one text value is named read-only, with the reason, by the same
        // reading the save's refusal uses (NotSingleText): the page offered "a; b" and "binary (16 bytes)" as editable
        // text, and the operator learned only at save time that the editor writes neither.
        var currentValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var readOnlyAttributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attr in allowlist)
        {
            currentValues[attr.Name] = ReadDirectoryValue(adUser, attr.Name)?.ToString();
            if (NotSingleText(adUser, attr.Name) is { } notText)
                readOnlyAttributes[attr.Name] = $"This attribute {notText.Holds}; {RuleFor(notText)}";
        }

        return new(true, null, principal, "OnPremAD", false, null, currentValues, readOnlyAttributes);
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
        var (currentValues, refusal) = ReReadCurrentValues(target, proposedValues, creds, allowlist);
        if (refusal != null)
            return new(false, refusal, null);
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
        // aud-a26l (folded in). One server for the whole chain - the re-read before the write, the write and the
        // read-back - so the read-back is answered where the write went. Unrouted, the module's default could send
        // each call to a different domain controller of the domain, and one the write had not reached yet reported
        // a landed save as unconfirmed, with no Undo and a failure notice. The server is the object's own domain,
        // from the DN the re-read asks for - the object it returns is the one written and read back (aud-a2p/a2q's
        // routing). A DN naming no domain leaves all three unrouted, as every call was, and says so.
        // *Assumption, not probed (DC affinity)*: three calls in one session sent to one domain name are answered
        // by one domain controller; where they are not, a landed write reads back unconfirmed - loud, never a false
        // success.
        var server = GroupManagementService.ServerFromDn(target.DistinguishedName);
        if (server is null)
        {
            _logger.LogWarning(
                "AD attribute save for {Target}: its distinguished name names no domain, so the re-read, the write and the read-back go to the default domain controller",
                target.UserPrincipalName);
        }

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

            ActiveDirectoryModuleImport.Import(ps);

            credential = CreateCredential(creds.username, creds.password, creds.domain);

            ps.AddCommand("Get-ADUser")
              .AddParameter("Identity", target.DistinguishedName)
              .AddParameter("Properties", GetPropertiesToLoad(allowlist))
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            if (server is not null)
                ps.AddParameter("Server", server);
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

        // aud-a26o. Each attribute is read as the read-back reads it (ReadDirectoryValue): a one-element collection is its
        // element. Read by ToString a collection was its type name, so its value could never equal the one proposed,
        // and the record's old value was that name. An attribute the save would write that holds several values is
        // refused before the write: written as one, the others would be lost, and v1 edits single-valued attributes
        // only. Refused here rather than by the allowlist's save, which cannot tell a multi-valued attribute without
        // reading the schema - and one holding a single value is safely written as one. Since aud-a26r a binary value is
        // refused here too, as binary: the editor writes text attributes only.
        var notSingleText = NotSingleTextRefusal(reReadUser, proposedValues, allowlist);
        if (notSingleText != null)
        {
            LogAudit(target, [], performedBy, ip, ticket, false, notSingleText);
            return new(false, notSingleText, null);
        }

        var changes = ComputeChanges(proposedValues, CurrentValues(reReadUser, allowlist), allowlist);
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
        if (server is not null) ps.AddParameter("Server", server);
        if (replace.Count > 0) ps.AddParameter("Replace", replace);
        if (clear.Count > 0) ps.AddParameter("Clear", clear.ToArray());

        // -ErrorAction Stop makes real Set-ADUser failures (access denied,
        // constraint violation, unreachable DC) THROW from Invoke(). Catch, audit,
        // and return a failed result; a failed mutation with no audit record
        // violates the Constitution's audit invariant.
        //
        // aud-a26g: the throw is the write's whole verdict, with no HadErrors check after it. An error
        // a command writes under -ErrorAction Stop is thrown and leaves no record in the stream, so such
        // a check could never see the write's own error - only a record something else left, an earlier
        // command's or one written past the preference, and then a write the directory had taken was
        // reported and recorded as failed, with nothing to undo. So -ErrorAction Stop is load-bearing
        // here: a non-terminating refusal is caught only because it is thrown.
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
        finally
        {
            // The read-back below runs on this pipeline (aud-a26j): the write must not stay queued in
            // front of it, where it would run again, nor leave a record the read's error check would
            // take for its own - HadErrors stays true while one remains (psl-2). Without the clear, an
            // error written past -ErrorAction Stop turns an applied write into an unconfirmed one.
            ps.Commands.Clear();
            ps.Streams.Error.Clear();
        }

        // aud-a26j. Done is the attributes reading back as written, not the write raising no error: a
        // directory that takes a modify and applies nothing, or stores another value, raises none (Known
        // Failure Class 2; aud-a2p, the Conference Rooms twin). "As written" is in the attribute's own
        // syntax (aud-a26l): a DN in the directory's case and spacing, or a Boolean read back as True, is
        // the value written. A save not confirmed is recorded failed with no old/new pair - the event log
        // offers no Undo of a change its record cannot vouch for - and a confirmed one is recorded with the
        // values read: what the directory holds, not what was proposed.
        var (confirmError, asRead) = ConfirmWrite(ps, target, reReadGuid, changes, allowlist, server, credential);
        if (confirmError != null)
        {
            // aud-a28o. What it tried is recorded as attempted_<name>, old and proposed, not as old_/new_ pairs:
            // a pair is what the event log offers to undo, and this change is not known to have been made. Without
            // the values a reviewer could see neither what the editor tried to set nor what the attribute held
            // before the write.
            LogAudit(target, [], performedBy, ip, ticket, false, confirmError, attempted: changes);
            return new(false, confirmError, null);
        }

        LogAudit(target, asRead, performedBy, ip, ticket, true, null, servicedNote);

        return new(true, null, asRead);
    }

    // aud-a26j. The read-back and its verdict: the failure message, or the changes as read. A throw is
    // the read's error. Both failures name the object and what was sent, because the record they become
    // has no old/new pair to say it - and, since aud-a26l, the server asked. A value read that is not the
    // one written may be one the domain controller that answered has not been sent yet, which the
    // not-confirmed message says, so an operator saving again knows a second try can read differently.
    private (string? Error, List<AttributeChange> AsRead) ConfirmWrite(
        PowerShell ps,
        ResolvedDirectoryPrincipal target,
        string? objectGuid,
        List<AttributeChange> written,
        List<EditableAttribute> allowlist,
        string? server,
        PSCredential credential)
    {
        var asked = server ?? "the default domain controller";
        string? readError;
        PSObject? read = null;
        try
        {
            (readError, read) = ReadBackWrittenAttributes(ps, objectGuid, written, server, credential);
        }
        catch (Exception ex)
        {
            readError = ex.InnerException?.Message ?? ex.Message;
        }

        if (readError != null || read is null)
        {
            var failed = readError ?? "nothing was read";
            _logger.LogWarning("Set-ADUser on {Target} could not be confirmed by reading it back by objectGUID {ObjectGuid} from {Server}: {Error}", target.UserPrincipalName, objectGuid, asked, failed);
            return ($"Set-ADUser on {target.UserPrincipalName} was sent but could not be confirmed: reading {string.Join(", ", written.Select(c => c.Name))} back by objectGUID {objectGuid ?? "(none)"} from {asked} failed: {failed}", written);
        }

        // Each row's Type, by name, the first row for a name as everywhere else the allowlist is searched.
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in allowlist)
            types.TryAdd(attribute.Name, attribute.Type);

        var (unconfirmed, asRead) = UnconfirmedWrites(written, read, types);
        if (unconfirmed.Count > 0)
        {
            var detail = string.Join("; ", unconfirmed);
            var answered = server is null ? "the default domain controller" : $"the domain controller that answered for {server}";
            _logger.LogWarning("Set-ADUser on {Target} was not confirmed by reading it back by objectGUID {ObjectGuid} from {Server}: {Detail}", target.UserPrincipalName, objectGuid, asked, detail);
            return ($"Set-ADUser on {target.UserPrincipalName} was not confirmed: read back by objectGUID {objectGuid} from {asked}, {detail}. "
                + $"The value may not have replicated yet to {answered}.", written);
        }

        return (null, asRead);
    }

    // aud-a26j. The written attributes, read back by the objectGUID the re-read returned for the DN the
    // write named, so the read sees that object and no other - and, since aud-a26l, sent to the server the
    // write was sent to, so it asks where the write went (PerformSave's DC-affinity assumption); with no
    // server, unrouted as the write was. In the shape of aud-a2p's read: an error record fails it - after a
    // read, whatever left one, the answer is not known good - and exactly one object, the one written, must
    // answer.
    private static (string? Error, PSObject? Read) ReadBackWrittenAttributes(
        PowerShell ps, string? objectGuid, List<AttributeChange> written, string? server, PSCredential credential)
    {
        if (string.IsNullOrEmpty(objectGuid))
            return ("the object read before the write carried no objectGUID", null);

        ps.AddCommand("Get-ADUser")
          .AddParameter("Identity", objectGuid)
          .AddParameter("Properties", written.Select(c => c.Name).ToArray())
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        if (server is not null)
            ps.AddParameter("Server", server);
        var found = ps.Invoke();
        if (ps.HadErrors)
            return (ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? $"Get-ADUser reported an error reading {objectGuid}.", null);
        ps.Commands.Clear();

        var answered = found.Count == 1 ? found[0]?.Properties["ObjectGUID"]?.Value?.ToString() : null;
        if (answered is null || !string.Equals(answered, objectGuid, StringComparison.OrdinalIgnoreCase))
            return ($"{found.Count} object(s) answered, ObjectGUID {answered ?? "none"}", null);

        return (null, found[0]);
    }

    // aud-a26j, aud-a26l. What the write sent that the object read back does not hold, one description each,
    // and the changes as read. Each value sent is judged against the value read in the attribute's own syntax
    // (SyntaxOf): the directory hands a value back in its own form - a DN in its canonical case and spacing, a
    // Boolean as True - and the ordinal comparison this replaced failed every such save although the write had
    // landed, with no Undo and a failure notice. Never looser than the syntax: a difference of content still
    // fails. An empty value and an absent attribute are the same, the directory keeping no empty value, so a
    // clear is confirmed by an attribute that is gone. Whatever the verdict the change takes the value read -
    // what the directory holds - so the undo's conflict check, which compares the lookup's reading with it,
    // meets the same form. The allowlist arrives as each row's Type, not as the rows:
    // AllowlistConcurrentSaveTests takes a non-private method handed rows for a writer, and this reads one
    // field. Pure, for unit testing.
    internal static (List<string> Unconfirmed, List<AttributeChange> AsRead) UnconfirmedWrites(
        IReadOnlyList<AttributeChange> written, PSObject read, IReadOnlyDictionary<string, string> allowlistTypes)
    {
        var unconfirmed = new List<string>();
        var asRead = new List<AttributeChange>();
        foreach (var change in written)
        {
            var value = ReadDirectoryValue(read, change.Name);
            var expected = EmptyAsAbsent(change.NewValue);
            var actual = EmptyAsAbsent(value?.ToString());
            allowlistTypes.TryGetValue(change.Name, out var type);
            if (!ReadsAsWritten(SyntaxOf(type, change.Name, value), expected, actual, value))
            {
                unconfirmed.Add($"{change.Name} ({change.Label}) {(actual is null ? "is not set" : $"is '{actual}'")}, "
                    + $"not {(expected is null ? "cleared" : $"'{expected}'")} as written");
            }

            asRead.Add(change with { NewValue = actual });
        }

        return (unconfirmed, asRead);
    }

    /// <summary>The syntax a value read back is judged in (aud-a26l); see <see cref="SyntaxOf"/>.</summary>
    internal enum ReadBackSyntax
    {
        Ordinal,
        PlainString,
        DistinguishedName,
        Boolean,
        Integer,
    }

    // aud-a26l. The base-schema user attributes of DN syntax an allowlist can hold. A DN is read back as a
    // string, so only the name can say one is. *Assumption, not probed*: they are DN syntax in every forest's
    // schema, being base schema. A DN-syntax attribute outside the list compares as a plain string: a loud
    // false failure on a difference of case, never a false success.
    private static readonly HashSet<string> DnValuedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "manager", "assistant", "secretary", "seeAlso",
    };

    // aud-a26l. The syntax, first match wins. The allowlist's Type is the editor's input kind - String or
    // Choice, all the configuration page offers - so it never names the directory's syntax; the value read and
    // the attribute's name do:
    // - a System.Boolean read is a Boolean. *Assumption, not probed*: the module returns a Boolean-syntax
    //   attribute as System.Boolean, as the editor already shows one (its ToString, True/False);
    // - an integer read is an Integer. *Assumption, not probed*: the Integer and Large Integer syntaxes come
    //   back as Int32 and Int64;
    // - a DN-valued attribute by name is a distinguished name;
    // - a String or Choice row is a plain string;
    // - any other Type, a row the configuration page cannot write, is compared ordinal and untrimmed.
    // A string is never parsed because it looks like a Boolean or a number: "007" in a string attribute is not
    // 7, and "True" in one is not true.
    internal static ReadBackSyntax SyntaxOf(string? type, string name, object? read)
    {
        if (read is bool)
            return ReadBackSyntax.Boolean;
        if (IntegerOf(read) is not null)
            return ReadBackSyntax.Integer;
        if (DnValuedAttributes.Contains(name))
            return ReadBackSyntax.DistinguishedName;
        if (type is "String" or "Choice")
            return ReadBackSyntax.PlainString;
        return ReadBackSyntax.Ordinal;
    }

    // aud-a26l. Whether the value read is the value written, in that syntax. Both empty is a match and one empty
    // is not. A Boolean or an integer is parsed on the side written (invariant) and compared with the typed value
    // read. A plain string is compared ordinal after trimming leading and trailing whitespace on both sides and
    // nothing else - case and inner spacing still count. *Assumption, not probed*: the directory may store a
    // string without its leading and trailing whitespace.
    internal static bool ReadsAsWritten(ReadBackSyntax syntax, string? expected, string? actual, object? read)
    {
        if (expected is null || actual is null)
            return expected is null && actual is null;

        return syntax switch
        {
            ReadBackSyntax.Boolean => bool.TryParse(expected, out var flag) && read is bool held && flag == held,
            ReadBackSyntax.Integer => long.TryParse(expected, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
                && IntegerOf(read) == number,
            ReadBackSyntax.DistinguishedName => SameDistinguishedName(expected, actual),
            ReadBackSyntax.PlainString => string.Equals(expected.Trim(), actual.Trim(), StringComparison.Ordinal),
            _ => string.Equals(expected, actual, StringComparison.Ordinal),
        };
    }

    // aud-a26l. Two DNs are the same when their RDNs are, in number and in order: split at unescaped commas
    // (ServerFromDn's split, so an escaped comma stays inside its CN), each RDN trimmed and trimmed around its
    // first '=', compared OrdinalIgnoreCase - the repo's DN comparison (GroupManagementService.IsSelfNest,
    // aud-a2i's membership read). No prefix and no "contains": a DN naming a parent or a child of the object is
    // another DN. Escapes are compared as written (a hex escape is not equated with its character) and spaces
    // around a multi-valued RDN's '+' still count; either difference fails loud.
    internal static bool SameDistinguishedName(string written, string read)
    {
        var left = RelativeNames(written);
        var right = RelativeNames(read);
        return left.Length == right.Length
            && left.Zip(right).All(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] RelativeNames(string dn) =>
        Regex.Split(dn, @"(?<=(?<!\\)(?:\\\\)*),")
            .Select(rdn =>
            {
                var equals = rdn.IndexOf('=');
                return equals < 0 ? rdn.Trim() : $"{rdn[..equals].Trim()}={rdn[(equals + 1)..].Trim()}";
            })
            .ToArray();

    // The integral types a value can be read back as, widened to long; null for anything else, a string included.
    private static long? IntegerOf(object? value) => value switch
    {
        sbyte v => v,
        byte v => v,
        short v => v,
        ushort v => v,
        int v => v,
        uint v => v,
        long v => v,
        ulong v when v <= long.MaxValue => (long)v,
        _ => null,
    };

    private static string? EmptyAsAbsent(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // A single-valued attribute as read, null when absent: aud-a2p's, a sibling because Conference Rooms'
    // is private - since aud-a26l the value itself rather than its text, so its type can say its syntax.
    // *Assumption, not probed*: the module answers one with its value, as this service's own reads take it
    // (Properties[..].Value). A collection is taken as its one element, so a collection-shaped answer is not
    // told as a difference; several values, joined, are not the one written. Since aud-a26o the re-reads before
    // a save and for a preview read through it too, so a value is compared before the write as after it; since
    // aud-a26r the lookup does, and a binary value is named "binary (N bytes)" rather than returned - nothing compares
    // one, the refusal stopping a save or preview of a binary attribute first, and its ToString is its type's name.
    private static object? ReadDirectoryValue(PSObject read, string name)
    {
        var items = DirectoryValues(read, name);
        return items.Count switch
        {
            0 => null,
            1 => Named(items[0]),
            _ => string.Join("; ", items.Select(v => Named(v)?.ToString())),
        };
    }

    // A binary value as the editor shows and refuses it (aud-a26r): by its length, never its bytes nor its type's name.
    private static object? Named(object? value) => value is byte[] bytes ? Binary(bytes) : value;

    private static string Binary(byte[] bytes) => $"binary ({bytes.Length} bytes)";

    // An attribute's values as read (aud-a26o): none, one, or a collection's elements, a string being one value and,
    // since aud-a26r, a byte array too - it was its bytes, each counted as a value. ReadDirectoryValue takes one value
    // from them; NotSingleTextRefusal counts them.
    private static List<object?> DirectoryValues(PSObject read, string name)
    {
        var value = Unwrapped(read.Properties[name]?.Value);
        if (value is null)
            return [];
        if (value is string or byte[])
            return [value];
        if (value is System.Collections.IEnumerable values)
            return values.Cast<object?>().Select(Unwrapped).ToList();

        return [value];
    }

    // The object's values as read (aud-a26o), by attribute: what ComputeChanges compares, each in its own syntax.
    private static Dictionary<string, object?> CurrentValues(PSObject read, List<EditableAttribute> allowlist)
    {
        var current = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var attr in allowlist)
            current[attr.Name] = ReadDirectoryValue(read, attr.Name);
        return current;
    }

    // aud-a26o, aud-a26r. The refusal of a save or preview that would write an attribute that is not one text value,
    // naming each under its own rule - several values (the editor writes single-valued attributes only) or a binary
    // value (it writes text attributes only) - or null. Only the attributes proposed count: one the save does not write
    // may hold what it holds. *Assumption, not probed*: the module answers an attribute holding several values with a
    // collection, one holding a single value with that value or a one-element collection, and a binary attribute with a
    // byte array. A byte array was a collection to DirectoryValues, so a binary attribute was refused as holding one
    // value per byte: safe, but the count named values it does not have. Since aud-a26u several binary values are binary
    // too - they were refused under the single-valued rule - and the lookup marks the page by the same reading.
    private static string? NotSingleTextRefusal(PSObject read, Dictionary<string, string?> proposedValues, List<EditableAttribute> allowlist)
    {
        var several = new List<string>();
        var binary = new List<string>();
        foreach (var name in proposedValues.Keys)
        {
            var attr = allowlist.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (attr == null)
                continue;

            if (NotSingleText(read, attr.Name) is not { } notText)
                continue;

            (notText.Binary ? binary : several).Add($"'{attr.Label}' ({attr.Name}) {notText.Holds}");
        }

        var refusals = new List<string>();
        if (several.Count > 0)
            refusals.Add($"{string.Join("; ", several)}; {SingleValuedRule}");
        if (binary.Count > 0)
            refusals.Add($"{string.Join("; ", binary)}; {TextOnlyRule}");

        return refusals.Count == 0 ? null : $"{string.Join(" ", refusals)} Nothing was written.";
    }

    // The two rules a refusal and a read-only mark close with (aud-a26u, the words unchanged since aud-a26r).
    private const string SingleValuedRule = "the attribute editor writes single-valued attributes only.";

    private const string TextOnlyRule = "the attribute editor writes text attributes only.";

    // Why an attribute is not one text value: binary (the text-only rule) or several values (the single-valued rule),
    // and what it holds, in the words the refusal and the read-only mark both use.
    private readonly record struct NotText(bool Binary, string Holds);

    // aud-a26u. One reading for the save's and the preview's refusal and the lookup's read-only mark. Binary is checked
    // first, and several values that are all byte arrays are binary: counted as several before their elements were
    // looked at, they were refused under the single-valued rule, while the editor writes binary values in no number.
    private static NotText? NotSingleText(PSObject read, string name)
    {
        var values = DirectoryValues(read, name);
        if (values is [byte[] bytes])
            return new NotText(true, $"is {Binary(bytes)} in the directory");
        if (values.Count > 1 && values.All(value => value is byte[]))
            return new NotText(true, $"holds {values.Count} binary values in the directory");
        if (values.Count > 1)
            return new NotText(false, $"holds {values.Count} values in the directory");

        return null;
    }

    private static string RuleFor(NotText notText) => notText.Binary ? TextOnlyRule : SingleValuedRule;

    private static object? Unwrapped(object? value) => value is PSObject wrapped ? wrapped.BaseObject : value;

    // aud-a26o. The preview's re-read asks what the save's asks, where the save asks it: through the session seam
    // (CreateDirectoryRunspace), so it can be tested, and with the save's -Server - the object's own domain, from its
    // DN (aud-a26l) - so another domain controller than the save's cannot answer it. It opened its own session and
    // sent no -Server. A DN naming no domain goes unrouted, as the save's chain does, and says so. The values come back
    // as read, or the save's refusal of an attribute that is not one text value.
    private (Dictionary<string, object?>? Current, string? Refusal) ReReadCurrentValues(
        ResolvedDirectoryPrincipal target,
        Dictionary<string, string?> proposedValues,
        (string username, string password, string domain) creds,
        List<EditableAttribute> allowlist)
    {
        var server = GroupManagementService.ServerFromDn(target.DistinguishedName);
        if (server is null)
        {
            _logger.LogWarning(
                "AD attribute preview for {Target}: its distinguished name names no domain, so the re-read goes to the default domain controller",
                target.UserPrincipalName);
        }

        try
        {
            using var runspace = CreateDirectoryRunspace();
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            ActiveDirectoryModuleImport.Import(ps);

            var credential = CreateCredential(creds.username, creds.password, creds.domain);

            ps.AddCommand("Get-ADUser")
              .AddParameter("Identity", target.DistinguishedName)
              .AddParameter("Properties", GetPropertiesToLoad(allowlist))
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            if (server is not null)
                ps.AddParameter("Server", server);
            var result = ps.Invoke();
            ps.Commands.Clear();

            if (result.Count == 0) return (null, null);

            var reReadDn = result[0].Properties["DistinguishedName"]?.Value?.ToString();
            if (!IsWithinSearchBases(reReadDn, GetConfiguredSearchBases()))
                return (null, null);

            var refusal = NotSingleTextRefusal(result[0], proposedValues, allowlist);
            return refusal is null ? (CurrentValues(result[0], allowlist), null) : (null, refusal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to re-read current values for {Target}", target.UserPrincipalName);
            return (null, null);
        }
    }

    // aud-a26o. A proposed value is a change only when the read-back would not confirm it as written: compared in the
    // attribute's own syntax (aud-a26l's SyntaxOf and ReadsAsWritten), on the value as read. Compared ordinal, an edit
    // that differed from the directory only in form - "TRUE" against a Boolean True, a DN in another case and
    // spacing, a string with spaces round it - was written, and recorded as a change whose old and new values read
    // the same. Private: the aud-a28l tripwire takes any non-private method handed allowlist rows for a writer.
    private static List<AttributeChange> ComputeChanges(
        Dictionary<string, string?> proposed,
        IReadOnlyDictionary<string, object?> current,
        List<EditableAttribute> allowlist)
    {
        var changes = new List<AttributeChange>();
        foreach (var kv in proposed)
        {
            var attr = allowlist.FirstOrDefault(a => string.Equals(a.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
            if (attr == null) continue;

            current.TryGetValue(kv.Key, out var read);
            var newValue = EmptyAsAbsent(kv.Value);
            var oldValue = EmptyAsAbsent(read?.ToString());

            if (!ReadsAsWritten(SyntaxOf(attr.Type, attr.Name, read), newValue, oldValue, read))
                changes.Add(new AttributeChange(attr.Name, attr.Label, oldValue, newValue));
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
    /// Audit finding A28. Every refusal in <c>SaveAsync</c> - corrupt allowlist, an attribute above
    /// the caller's level (aud-a28g), failed validation, a denylisted attribute, a failed or blocking
    /// protected-principal check, missing credentials, a busy throttle - returned before
    /// <see cref="LogAudit"/> and so left nothing in the audit log. The operation trace saw them; the trace is diagnostic,
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
        string reason) =>
        WriteRefusal(target.UserPrincipalName, performedBy, ip, ticket, step, reason);

    private void WriteRefusal(string target, string performedBy, string ip, string ticket, string step, string reason)
    {
        _audit.LogModuleAction(
            performedBy,
            ip,
            "ADAttributeEdit_Refused",
            "ADAttributeEditor",
            target,
            false,
            ticket,
            reason,
            new Dictionary<string, object?> { ["refusedAt"] = step, ["mutationAttempted"] = false });
    }

    /// <summary>
    /// Records a save the editor page refused before it called <see cref="SaveAsync"/> (aud-a28g).
    /// </summary>
    /// <remarks>
    /// The record <see cref="LogRefusal"/> writes, so a page refusal and a service refusal read the same
    /// in the Admin Event Log. Public because the page writes it: its authorization re-check and its
    /// no-looked-up-user check stay on the page, which holds the principal and the panel, and SaveAsync
    /// - which records everything it returns - is never reached. A null target is recorded as
    /// <c>(none)</c>. Never throws: an audit sink that fails is logged, and must not turn a refusal into
    /// an exception the page's catch would then record a second time.
    /// </remarks>
    public void LogSaveRefused(
        ResolvedDirectoryPrincipal? target, string performedBy, string ip, string ticket, string refusedAt, string reason)
    {
        try
        {
            WriteRefusal(target?.UserPrincipalName ?? "(none)", performedBy, ip, ticket, refusedAt, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit failed for an attribute save the editor page refused");
        }
    }

    /// <summary>
    /// Records a save that ended in an exception <see cref="SaveAsync"/> did not return (aud-a28g).
    /// </summary>
    /// <remarks>
    /// The failed <c>ADAttributeEditor_Update</c> the aud-a28c catch writes, with no changes and the
    /// target's GUID when known, but not its write-completed trace step: the page cannot know how far
    /// the save got. For the same reason it carries neither refusal field - the write may have been
    /// reached - so it can never be read as "nothing was tried". Never throws, like
    /// <see cref="LogSaveRefused"/>.
    /// </remarks>
    public void LogSaveFailed(ResolvedDirectoryPrincipal target, string performedBy, string ip, string ticket, string reason)
    {
        try
        {
            var extra = string.IsNullOrWhiteSpace(target.ObjectGuid)
                ? null
                : new Dictionary<string, object?> { ["targetObjectGuid"] = target.ObjectGuid };
            _audit.LogADAttributeEdit(performedBy, ip, target.UserPrincipalName, [], false, ticket, reason, extra);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit failed for an attribute save that threw");
        }
    }

    private void LogAudit(ResolvedDirectoryPrincipal target, List<AttributeChange> changes, string performedBy, string ip, string ticket, bool success, string? errorDetail, string? servicedNote = null, List<AttributeChange>? attempted = null)
    {
        var changedAttrs = changes.Select(c => c.Name).ToArray();
        var details = new Dictionary<string, object?>
        {
            ["target"] = target.UserPrincipalName,
            ["changedAttributes"] = changedAttrs,
            ["changes"] = changes.Select(c => new { c.Name, c.OldValue, c.NewValue }).ToArray()
        };
        // aud-a28o. An unconfirmed save hands its changes here as attempted, beside an empty list of changes
        // made: listed on its trace step, and recorded below as attempted_<name>.
        if (attempted is not null)
            details["attemptedChanges"] = attempted.Select(c => new { c.Name, c.OldValue, ProposedValue = c.NewValue }).ToArray();

        _operationTrace.Step("AttributeWriteCompleted", success ? "Success" : "Failed", details: details);

        // Audit finding A9. The UPN is a label and can be reassigned to a different person;
        // the GUID cannot. Undo resolves this record back to an object, so without the GUID
        // there is nothing for it to prove it reached the same one. Recorded here so the undo
        // path has an immutable id to check - and so that a record WITHOUT one is detectable
        // as older rather than indistinguishable from a verified match.
        var extra = ProtectedPrincipalServicing.Extra(servicedNote) ?? new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(target.ObjectGuid))
            extra["targetObjectGuid"] = target.ObjectGuid;
        // A nested object rather than two fields: the JSONL writer drops a null field at the top level and keeps
        // one inside an object, so a clear's proposed value reads as null instead of vanishing.
        foreach (var change in attempted ?? [])
            extra[$"attempted_{change.Name}"] = new Dictionary<string, object?> { ["old"] = change.OldValue, ["proposed"] = change.NewValue };

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
