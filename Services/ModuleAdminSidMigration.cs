using System.Text.Json;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// The directory lookups behind the module-admin SID pass: <see cref="ProtectedPrincipalService"/> in production - the
/// identifier pass's one operation, one credential fetch and one warm session - a stand-in in tests. Never throws for a
/// lookup that could not run; such a grant comes back <see cref="ProtectedPrincipalLeftReason.DirectoryUnavailable"/>.
/// </summary>
internal interface IModuleAdminGrantResolver
{
    /// <summary>One resolution per grant, each of kind <c>group</c> with the grant as stored for its value.</summary>
    Task<IReadOnlyList<ProtectedPrincipalRowResolution>> ResolveModuleAdminGrantsAsync(
        IReadOnlyList<string> grants, CancellationToken cancellationToken);
}

/// <summary>A module-admin grant the last pass left stored by name, with why. Display-only; it decides nothing.</summary>
public sealed record ModuleAdminFlaggedGrant(string ModuleId, string Value, ProtectedPrincipalLeftReason Reason, string Detail);

/// <summary>What one module-admin SID pass did.</summary>
public sealed record ModuleAdminMigrationResult(
    ProtectedPrincipalMigrationStatus Status,
    IReadOnlyList<ModuleAdminSidConversion> Converted,
    IReadOnlyList<ModuleAdminFlaggedGrant> Left,
    IReadOnlyList<ModuleAdminSidConversion> Skipped)
{
    internal static ModuleAdminMigrationResult Of(ProtectedPrincipalMigrationStatus status) => new(status, [], [], []);

    /// <summary>The sentence the Module Configuration page shows for this pass.</summary>
    public string Summary => Status switch
    {
        ProtectedPrincipalMigrationStatus.AlreadyMigrated =>
            "Nothing to resolve: every module-admin grant is already stored as a group SID.",
        ProtectedPrincipalMigrationStatus.AlreadyRunning =>
            "A resolve pass is already running in this instance. Wait for it to finish, then reload the page.",
        ProtectedPrincipalMigrationStatus.Cancelled =>
            "Resolve cancelled. Nothing was changed.",
        ProtectedPrincipalMigrationStatus.StoreUnavailable =>
            "The module admin groups could not be read or written, so nothing was changed.",
        _ => $"Converted {Converted.Count}; left {Left.Count} stored by name, granting nothing"
             + (Left.Count == 0 ? "" : " - " + string.Join("; ", Left.Select(l => $"{l.ModuleId}: {l.Value}: {l.Detail}")))
             + (Skipped.Count == 0 ? "." : $"; {Skipped.Count} changed elsewhere while the pass ran and were left as that change left them."),
    };
}

/// <summary>The last pass's flagged grants, as the Module Configuration page reads them.</summary>
public sealed record ModuleAdminMigrationFlags(DateTimeOffset? RanAt, IReadOnlyList<ModuleAdminFlaggedGrant> Rows)
{
    public static ModuleAdminMigrationFlags None { get; } = new(null, []);

    /// <summary>The flag the last pass recorded for this module's grant, or null.</summary>
    public ModuleAdminFlaggedGrant? For(string moduleId, string value) => Rows.FirstOrDefault(r =>
        string.Equals(r.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(r.Value.Trim(), (value ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Converts module-admin grants stored by name to the SID of the one group each names, at startup and on the Module
/// Configuration page's <b>Resolve now</b> (docs/ProtectedPrincipalIdentifiers-Plan.md B.6; owner ruling 2026-11-03: "stored
/// names are migrated to SIDs by directory lookup; a name that does not resolve to exactly one group is listed on the Module
/// Configuration page for re-picking").
/// </summary>
/// <remarks>
/// <para><b>What a name grant does until then: nothing.</b> <see cref="ModuleAdminService.IsModuleAdmin"/> reads usable SIDs
/// only (aud-a1). A converted grant therefore <b>starts authorising at once</b> - decision D3, settled: convert every name that
/// resolves to exactly one group, effective immediately, and the audit event lists each.</para>
/// <para><b>Never blocks boot.</b> At startup the pass runs on the thread pool (<see cref="StartAtStartup"/>), as the
/// identifier pass does: it depends on the secret store and Active Directory, and a pass that is slow or cannot run changes
/// nothing - the grants it would convert are inert already.</para>
/// <para><b>Resolve once, write once.</b> The inventory is <see cref="ModuleAdminService.TryGetUnusableGrants"/>; each
/// distinct value is looked up once, however many modules hold it, in one directory operation (one credential fetch). The
/// conversions are written in one transaction, each re-checked under the write lock: a grant removed or replaced after the
/// pass read it is left as that change left it (aud-a33b). The row is replaced in place - safe for every build sharing the
/// database (<see cref="ModuleAdminRepository.ReplaceNamesWithSids"/>).</para>
/// <para><b>Idempotent.</b> A converted grant is a usable SID and is not read again; a second pass is a no-op.</para>
/// </remarks>
public sealed class ModuleAdminSidMigration
{
    /// <summary>The audit actor of the startup pass.</summary>
    public const string SystemActor = "system:module-admin-sid-migration";

    /// <summary>The <c>app_setting</c> key the flagged grants are kept under for the page: display-only, no DDL.</summary>
    public const string FlagsSettingKey = "module_admin_sid_flags";

    internal const string AuditAction = "ModuleAdminSidMigration";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ModuleAdminService _admins;
    private readonly IModuleAdminGrantResolver _resolver;
    private readonly AppSettingRepository? _settings;
    private readonly Action<string, string, ModuleAdminMigrationResult>? _audit;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _running = new(1, 1);

    public ModuleAdminSidMigration(
        ModuleAdminService admins,
        ProtectedPrincipalService resolver,
        AppSettingRepository settings,
        AuditService audit,
        ILogger<ModuleAdminSidMigration> logger)
        : this(admins, resolver, settings, (actor, ip, result) => WriteAudit(audit, actor, ip, result), logger)
    {
    }

    internal ModuleAdminSidMigration(
        ModuleAdminService admins,
        IModuleAdminGrantResolver resolver,
        AppSettingRepository? settings,
        Action<string, string, ModuleAdminMigrationResult>? audit,
        ILogger logger)
    {
        _admins = admins;
        _resolver = resolver;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// Starts the startup pass on the thread pool and returns at once - startup never waits on it. The task is returned for
    /// tests; it never faults.
    /// </summary>
    public Task StartAtStartup() => Task.Run(async () =>
    {
        try
        {
            var result = await RunAsync(SystemActor, string.Empty, CancellationToken.None);
            _logger.LogInformation("Module-admin SID pass at startup: {Status}", result.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The module-admin SID pass at startup failed; no grant was changed");
        }
    });

    /// <summary>Runs one pass. Does not throw: every outcome is a status.</summary>
    /// <param name="actor">The audit actor: <see cref="SystemActor"/> at startup, the administrator for Resolve now.</param>
    /// <param name="ipAddress">The administrator's address, or empty at startup.</param>
    /// <param name="cancellationToken">The administrator's activity token; honoured up to the write, never during it.</param>
    public async Task<ModuleAdminMigrationResult> RunAsync(string actor, string ipAddress, CancellationToken cancellationToken)
    {
        if (!await _running.WaitAsync(0, CancellationToken.None))
            return ModuleAdminMigrationResult.Of(ProtectedPrincipalMigrationStatus.AlreadyRunning);

        try
        {
            return await RunCoreAsync(actor, ipAddress, cancellationToken);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<ModuleAdminMigrationResult> RunCoreAsync(string actor, string ipAddress, CancellationToken cancellationToken)
    {
        if (!_admins.TryGetUnusableGrants(out var unusable))
        {
            _logger.LogError("Could not read the module admin groups for the SID pass; nothing was changed");
            return ModuleAdminMigrationResult.Of(ProtectedPrincipalMigrationStatus.StoreUnavailable);
        }

        if (unusable.Count == 0)
        {
            // Clear a stale list - every grant it named is now converted or gone - without writing on every start.
            if (ReadFlags().Rows.Count > 0)
                SaveFlags([]);
            var nothing = ModuleAdminMigrationResult.Of(ProtectedPrincipalMigrationStatus.AlreadyMigrated);
            if (!string.Equals(actor, SystemActor, StringComparison.Ordinal))
                Audit(actor, ipAddress, nothing);
            return nothing;
        }

        // Resolve once: a value two modules hold is one lookup.
        var values = unusable.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IReadOnlyList<ProtectedPrincipalRowResolution> resolved;
        try
        {
            resolved = await _resolver.ResolveModuleAdminGrantsAsync(values, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ModuleAdminMigrationResult.Of(ProtectedPrincipalMigrationStatus.Cancelled);
        }
        catch (Exception ex)
        {
            // The resolver does not throw for a lookup; this is a defect or an unexpected failure. Nothing is written.
            _logger.LogError(ex, "The module-admin SID pass could not resolve; nothing was changed");
            resolved = values.Select(v => ProtectedPrincipalRowResolution.Left(
                new ProtectedPrincipalLegacyRow(ProtectedPrincipalRepository.KindGroup, v),
                ProtectedPrincipalLeftReason.DirectoryUnavailable, $"directory unavailable: {ex.Message}")).ToList();
        }

        // The last point the administrator's cancel is honoured: the write below is one transaction and is not interrupted.
        if (cancellationToken.IsCancellationRequested)
            return ModuleAdminMigrationResult.Of(ProtectedPrincipalMigrationStatus.Cancelled);

        var byValue = new Dictionary<string, ProtectedPrincipalRowResolution>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in resolved)
            byValue.TryAdd(r.Row.Value, r);

        var conversions = new List<ModuleAdminSidConversion>();
        var left = new List<ModuleAdminFlaggedGrant>();
        foreach (var (moduleId, grants) in unusable.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var grant in grants)
            {
                if (!byValue.TryGetValue(grant, out var r))
                    left.Add(new ModuleAdminFlaggedGrant(moduleId, grant, ProtectedPrincipalLeftReason.DirectoryUnavailable, "directory unavailable: not looked up"));
                else if (r.Identifier is not null)
                    conversions.Add(new ModuleAdminSidConversion(moduleId, grant, r.Identifier));
                else
                    left.Add(new ModuleAdminFlaggedGrant(moduleId, grant, r.Reason!.Value, r.Detail ?? ""));
            }
        }

        ModuleAdminSidConversionOutcome outcome;
        try
        {
            outcome = _admins.ReplaceNameGrantsWithSids(conversions);
        }
        catch (Exception ex)
        {
            // One transaction: a throw rolled it back whole.
            _logger.LogError(ex, "The module-admin SID pass could not be written; no grant was changed");
            return ModuleAdminMigrationResult.Of(ProtectedPrincipalMigrationStatus.StoreUnavailable);
        }

        var status =
            outcome.Changed.Count > 0 ? ProtectedPrincipalMigrationStatus.SkippedChangedRows
            : left.Count > 0 && outcome.Written.Count == 0 && left.All(l => l.Reason == ProtectedPrincipalLeftReason.DirectoryUnavailable)
                ? ProtectedPrincipalMigrationStatus.DirectoryUnavailable
            : left.Count > 0 ? ProtectedPrincipalMigrationStatus.RowsLeft
            : outcome.Written.Count > 0 ? ProtectedPrincipalMigrationStatus.Migrated
            : ProtectedPrincipalMigrationStatus.AlreadyMigrated;

        var result = new ModuleAdminMigrationResult(status, outcome.Written, left, outcome.Changed);
        SaveFlags(left);
        Log(result);
        Audit(actor, ipAddress, result);
        return result;
    }

    /// <summary>The flagged grants the last pass recorded; <see cref="ModuleAdminMigrationFlags.None"/> when none or unreadable.</summary>
    public ModuleAdminMigrationFlags ReadFlags()
    {
        try
        {
            var raw = _settings?.Get(FlagsSettingKey);
            if (string.IsNullOrWhiteSpace(raw))
                return ModuleAdminMigrationFlags.None;
            var stored = JsonSerializer.Deserialize<StoredFlags>(raw, Json);
            return stored is null
                ? ModuleAdminMigrationFlags.None
                : new ModuleAdminMigrationFlags(stored.RanAt, stored.Rows ?? []);
        }
        catch (Exception ex)
        {
            // Display-only: an unreadable list shows each name grant without a reason, never hides the grant.
            _logger.LogWarning(ex, "The module-admin SID pass's flagged list could not be read");
            return ModuleAdminMigrationFlags.None;
        }
    }

    private void SaveFlags(IReadOnlyList<ModuleAdminFlaggedGrant> left)
    {
        if (_settings is null)
            return;
        try
        {
            _settings.Set(FlagsSettingKey, JsonSerializer.Serialize(new StoredFlags(DateTimeOffset.UtcNow, [.. left]), Json));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The module-admin SID pass's flagged list could not be saved; the grants are unaffected");
        }
    }

    private sealed record StoredFlags(DateTimeOffset? RanAt, List<ModuleAdminFlaggedGrant>? Rows);

    private void Log(ModuleAdminMigrationResult result)
    {
        foreach (var c in result.Converted)
            _logger.LogInformation("Module-admin SID pass: {Module} grant '{Name}' -> {Sid}; it grants module admin from now on",
                c.ModuleId, c.Name, c.Sid);
        foreach (var l in result.Left)
            _logger.LogWarning("Module-admin SID pass: {Module} grant '{Name}' left stored by name, granting nothing: {Detail}",
                l.ModuleId, l.Value, l.Detail);
        foreach (var s in result.Skipped)
            _logger.LogWarning("Module-admin SID pass: {Module} grant '{Name}' changed after the pass read it and was left as that change left it",
                s.ModuleId, s.Name);
    }

    private void Audit(string actor, string ipAddress, ModuleAdminMigrationResult result)
    {
        try
        {
            _audit?.Invoke(actor, ipAddress, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit failed for the module-admin SID pass ({Status})", result.Status);
        }
    }

    /// <summary>One audit event per pass: each conversion by module, and each grant left with its reason.</summary>
    internal static Dictionary<string, object?> AuditDetail(ModuleAdminMigrationResult result) => new()
    {
        ["status"] = result.Status.ToString(),
        ["converted"] = result.Converted.Select(c => $"{c.ModuleId}: {c.Name} -> {c.Sid}").ToArray(),
        ["left"] = result.Left.Select(l => $"{l.ModuleId}: {l.Value} ({l.Detail})").ToArray(),
        ["changedElsewhere"] = result.Skipped.Select(s => $"{s.ModuleId}: {s.Name}").ToArray(),
    };

    private static void WriteAudit(AuditService audit, string actor, string ipAddress, ModuleAdminMigrationResult result)
    {
        var success = result.Status is not (ProtectedPrincipalMigrationStatus.StoreUnavailable or ProtectedPrincipalMigrationStatus.DirectoryUnavailable);
        audit.LogModuleAction(actor, ipAddress, AuditAction, "AdminSettings", "Module Admins", success,
            errorDetail: success ? null : result.Summary, extra: AuditDetail(result));
    }
}
