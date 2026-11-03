using System.Text.Json;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// The directory lookups behind the identifier migration: <see cref="ProtectedPrincipalService"/> in production, a stand-in
/// in tests. Never throws for a lookup that could not run; such a row comes back
/// <see cref="ProtectedPrincipalLeftReason.DirectoryUnavailable"/>.
/// </summary>
internal interface IProtectedPrincipalLegacyResolver
{
    Task<IReadOnlyList<ProtectedPrincipalRowResolution>> ResolveLegacyRowsAsync(
        IReadOnlyList<ProtectedPrincipalLegacyRow> rows, CancellationToken cancellationToken);
}

/// <summary>How an identifier pass ended. Every value leaves the app running and every unconverted row enforced as before.</summary>
public enum ProtectedPrincipalMigrationStatus
{
    /// <summary>No legacy row lacked its identifier row, or another writer converted every one first. Nothing to do.</summary>
    AlreadyMigrated,

    /// <summary>Every row the pass read was converted.</summary>
    Migrated,

    /// <summary>
    /// The directory answered, and some rows did not resolve to exactly one object: they are left, enforced by name as
    /// before, and listed with their reasons. Every row that did resolve was converted.
    /// </summary>
    RowsLeft,

    /// <summary>No lookup could run (no credential, the directory down). Nothing was written; the next pass retries.</summary>
    DirectoryUnavailable,

    /// <summary>The store could not be read or written. Nothing was changed.</summary>
    StoreUnavailable,

    /// <summary>
    /// Another writer - normally an administrator saving through the other instance - removed or changed rows after this
    /// pass read them. Those were left exactly as that change left them; every row still as read was converted (aud-a33b).
    /// </summary>
    SkippedChangedRows,

    /// <summary>A pass was already running in this instance; this one did nothing.</summary>
    AlreadyRunning,

    /// <summary>The administrator cancelled before anything was written. Nothing was changed.</summary>
    Cancelled,
}

/// <summary>A legacy row the last pass left, with why. Display-only; it decides nothing.</summary>
public sealed record ProtectedPrincipalFlaggedRow(string Kind, string Value, ProtectedPrincipalLeftReason Reason, string Detail);

/// <summary>What one pass did.</summary>
public sealed record ProtectedPrincipalMigrationResult(
    ProtectedPrincipalMigrationStatus Status,
    IReadOnlyList<ProtectedPrincipalIdConversion> Converted,
    IReadOnlyList<ProtectedPrincipalFlaggedRow> Left,
    IReadOnlyList<ProtectedPrincipalIdConversion> Skipped)
{
    internal static ProtectedPrincipalMigrationResult Of(ProtectedPrincipalMigrationStatus status) => new(status, [], [], []);

    /// <summary>The sentence the admin page shows for this pass.</summary>
    public string Summary => Status switch
    {
        ProtectedPrincipalMigrationStatus.AlreadyMigrated =>
            "Nothing to resolve: every protected user, group and OU is already stored by its directory identifier.",
        ProtectedPrincipalMigrationStatus.AlreadyRunning =>
            "A resolve pass is already running in this instance. Wait for it to finish, then reload the page.",
        ProtectedPrincipalMigrationStatus.Cancelled =>
            "Resolve cancelled. Nothing was changed.",
        ProtectedPrincipalMigrationStatus.StoreUnavailable =>
            "The protection rules could not be read or written, so nothing was changed.",
        _ => $"Converted {Converted.Count}; left {Left.Count} enforced by name as before"
             + (Left.Count == 0 ? "" : " - " + string.Join("; ", Left.Select(l => $"{l.Value}: {l.Detail}")))
             + (Skipped.Count == 0 ? "." : $"; {Skipped.Count} changed elsewhere while the pass ran and were left as that change left them."),
    };
}

/// <summary>The last pass's flagged rows, as the admin page reads them.</summary>
public sealed record ProtectedPrincipalMigrationFlags(DateTimeOffset? RanAt, IReadOnlyList<ProtectedPrincipalFlaggedRow> Rows)
{
    public static ProtectedPrincipalMigrationFlags None { get; } = new(null, []);

    /// <summary>The flag the last pass recorded for this row, or null.</summary>
    public ProtectedPrincipalFlaggedRow? For(string kind, string value) => Rows.FirstOrDefault(r =>
        string.Equals(r.Kind, kind, StringComparison.Ordinal)
        && string.Equals(r.Value.Trim(), (value ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Converts legacy protected users, groups and OUs - stored by name - to identifier rows (objectSid, objectGUID), at startup
/// and on the admin page's <b>Resolve now</b> (docs/ProtectedPrincipalIdentifiers-Plan.md B.3, B.4).
/// </summary>
/// <remarks>
/// <para><b>Never blocks boot.</b> At startup the pass runs on the thread pool (<see cref="StartAtStartup"/>): it depends on
/// the secret store and Active Directory, which the rest of startup does not, and an unconverted row is enforced exactly as
/// before (decision D1), so a pass that is slow or cannot run changes nothing. It is a one-shot call per start, not a
/// timer.</para>
/// <para><b>Never rewrites a legacy row and never half-writes.</b> A conversion is a NEW identifier row beside its legacy
/// row, which an older build sharing the database keeps enforcing (B.1). All of a pass's rows are written in one
/// transaction, re-checked under the write lock: a row removed or changed after the pass read it is left as that writer
/// left it (aud-a33b, the <see cref="SectionAccessSidMigration"/> rule). Unlike that migration, one row that does not
/// resolve does not hold back the rest: each row is its own decision and an unconverted row loses nothing.</para>
/// <para><b>Idempotent.</b> A converted row has its identifier row and is not read again, so a second pass is a no-op; an
/// older build's whole-store save, which deletes every identifier row, leaves the legacy rows unpaired and the next pass
/// re-creates them.</para>
/// </remarks>
public sealed class ProtectedPrincipalIdentifierMigration
{
    /// <summary>The audit actor of the startup pass (B.4 step 5).</summary>
    public const string SystemActor = "system:protected-principal-migration";

    /// <summary>The <c>app_setting</c> key the flagged rows are kept under for the page (B.4): display-only.</summary>
    public const string FlagsSettingKey = "protected_principal_identifier_flags";

    internal const string AuditAction = "ProtectedPrincipalIdentifierMigration";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ProtectedPrincipalRepository _repository;
    private readonly IProtectedPrincipalLegacyResolver _resolver;
    private readonly AppSettingRepository? _settings;
    private readonly Action<string, string, ProtectedPrincipalMigrationResult>? _audit;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _running = new(1, 1);

    public ProtectedPrincipalIdentifierMigration(
        ProtectedPrincipalRepository repository,
        ProtectedPrincipalService service,
        AppSettingRepository settings,
        AuditService audit,
        ILogger<ProtectedPrincipalIdentifierMigration> logger)
        : this(repository, service, settings, (actor, ip, result) => WriteAudit(audit, actor, ip, result), logger)
    {
    }

    internal ProtectedPrincipalIdentifierMigration(
        ProtectedPrincipalRepository repository,
        IProtectedPrincipalLegacyResolver resolver,
        AppSettingRepository? settings,
        Action<string, string, ProtectedPrincipalMigrationResult>? audit,
        ILogger logger)
    {
        _repository = repository;
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
            _logger.LogInformation("Protected-principal identifier pass at startup: {Status}", result.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The protected-principal identifier pass at startup failed; every entry is enforced as before");
        }
    });

    /// <summary>
    /// Runs one pass. Does not throw: every outcome is a status, and every unconverted row stays enforced as before.
    /// </summary>
    /// <param name="actor">The audit actor: <see cref="SystemActor"/> at startup, the administrator for Resolve now.</param>
    /// <param name="ipAddress">The administrator's address, or empty at startup.</param>
    /// <param name="cancellationToken">The administrator's activity token; honoured up to the write, never during it.</param>
    public async Task<ProtectedPrincipalMigrationResult> RunAsync(string actor, string ipAddress, CancellationToken cancellationToken)
    {
        if (!await _running.WaitAsync(0, CancellationToken.None))
            return ProtectedPrincipalMigrationResult.Of(ProtectedPrincipalMigrationStatus.AlreadyRunning);

        try
        {
            return await RunCoreAsync(actor, ipAddress, cancellationToken);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<ProtectedPrincipalMigrationResult> RunCoreAsync(string actor, string ipAddress, CancellationToken cancellationToken)
    {
        var snapshot = _repository.ReadSnapshot();
        if (!snapshot.Readable)
        {
            _logger.LogError("Could not read the protection rules for the identifier pass; nothing was changed");
            return ProtectedPrincipalMigrationResult.Of(ProtectedPrincipalMigrationStatus.StoreUnavailable);
        }

        var rows = UnmigratedRows(snapshot.Data);
        if (rows.Count == 0)
        {
            // Clear a stale list - every row it named is now converted or gone - without writing on every start.
            if (ReadFlags().Rows.Count > 0)
                SaveFlags([]);
            var nothing = ProtectedPrincipalMigrationResult.Of(ProtectedPrincipalMigrationStatus.AlreadyMigrated);
            if (!string.Equals(actor, SystemActor, StringComparison.Ordinal))
                Audit(actor, ipAddress, nothing);
            return nothing;
        }

        IReadOnlyList<ProtectedPrincipalRowResolution> resolved;
        try
        {
            resolved = await _resolver.ResolveLegacyRowsAsync(rows, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ProtectedPrincipalMigrationResult.Of(ProtectedPrincipalMigrationStatus.Cancelled);
        }
        catch (Exception ex)
        {
            // The resolver does not throw for a lookup; this is a defect or an unexpected failure. Nothing is written.
            _logger.LogError(ex, "The protected-principal identifier pass could not resolve; nothing was changed");
            resolved = rows.Select(r => ProtectedPrincipalRowResolution.Left(
                r, ProtectedPrincipalLeftReason.DirectoryUnavailable, $"directory unavailable: {ex.Message}")).ToList();
        }

        // The last point the administrator's cancel is honoured: the write below is one transaction and is not interrupted.
        if (cancellationToken.IsCancellationRequested)
            return ProtectedPrincipalMigrationResult.Of(ProtectedPrincipalMigrationStatus.Cancelled);

        var conversions = resolved
            .Where(r => r.Identifier is not null)
            .Select(r => new ProtectedPrincipalIdConversion(r.Row.Kind, r.Row.Value, r.IdKind, r.Identifier!))
            .ToList();
        var left = resolved
            .Where(r => r.Identifier is null)
            .Select(r => new ProtectedPrincipalFlaggedRow(r.Row.Kind, r.Row.Value, r.Reason!.Value, r.Detail ?? ""))
            .ToList();

        ProtectedPrincipalIdConversionOutcome outcome;
        try
        {
            outcome = _repository.AddIdentifierRows(conversions);
        }
        catch (Exception ex)
        {
            // One transaction: a throw rolled it back whole. Every row is still enforced as before.
            _logger.LogError(ex, "The protected-principal identifier pass could not be written; no row was changed");
            return ProtectedPrincipalMigrationResult.Of(ProtectedPrincipalMigrationStatus.StoreUnavailable);
        }

        var status =
            outcome.Changed.Count > 0 ? ProtectedPrincipalMigrationStatus.SkippedChangedRows
            : left.Count > 0 && outcome.Written.Count == 0 && left.All(l => l.Reason == ProtectedPrincipalLeftReason.DirectoryUnavailable)
                ? ProtectedPrincipalMigrationStatus.DirectoryUnavailable
            : left.Count > 0 ? ProtectedPrincipalMigrationStatus.RowsLeft
            : outcome.Written.Count > 0 ? ProtectedPrincipalMigrationStatus.Migrated
            : ProtectedPrincipalMigrationStatus.AlreadyMigrated;

        var result = new ProtectedPrincipalMigrationResult(status, outcome.Written, left, outcome.Changed);
        SaveFlags(left);
        Log(result);
        Audit(actor, ipAddress, result);
        return result;
    }

    /// <summary>
    /// The legacy user, group and OU rows no identifier row pairs with - the rows still enforced by name (plan B.2). Values
    /// of the kinds' companions are excluded by the same rule the protection check uses.
    /// </summary>
    internal static IReadOnlyList<ProtectedPrincipalLegacyRow> UnmigratedRows(ProtectedPrincipalData data)
    {
        IEnumerable<ProtectedPrincipalLegacyRow> Of(string kind, string[] legacy, string[] ids) =>
            ProtectedPrincipalIdEntry.Unpaired(legacy.Where(v => !string.IsNullOrWhiteSpace(v)), ids)
                .Select(v => new ProtectedPrincipalLegacyRow(kind, v));

        return Of(ProtectedPrincipalRepository.KindUser, data.Users, data.UserIds)
            .Concat(Of(ProtectedPrincipalRepository.KindGroup, data.Groups, data.GroupIds))
            .Concat(Of(ProtectedPrincipalRepository.KindOu, data.OrganizationalUnits, data.OuIds))
            .ToList();
    }

    /// <summary>The flagged rows the last pass recorded; <see cref="ProtectedPrincipalMigrationFlags.None"/> when none or unreadable.</summary>
    public ProtectedPrincipalMigrationFlags ReadFlags()
    {
        try
        {
            var raw = _settings?.Get(FlagsSettingKey);
            if (string.IsNullOrWhiteSpace(raw))
                return ProtectedPrincipalMigrationFlags.None;
            var stored = JsonSerializer.Deserialize<StoredFlags>(raw, Json);
            return stored is null
                ? ProtectedPrincipalMigrationFlags.None
                : new ProtectedPrincipalMigrationFlags(stored.RanAt, stored.Rows ?? []);
        }
        catch (Exception ex)
        {
            // Display-only: an unreadable list shows each unconverted row without a reason, never hides the row.
            _logger.LogWarning(ex, "The protected-principal identifier pass's flagged list could not be read");
            return ProtectedPrincipalMigrationFlags.None;
        }
    }

    private void SaveFlags(IReadOnlyList<ProtectedPrincipalFlaggedRow> left)
    {
        if (_settings is null)
            return;
        try
        {
            _settings.Set(FlagsSettingKey, JsonSerializer.Serialize(new StoredFlags(DateTimeOffset.UtcNow, [.. left]), Json));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The protected-principal identifier pass's flagged list could not be saved; the rules are unaffected");
        }
    }

    private sealed record StoredFlags(DateTimeOffset? RanAt, List<ProtectedPrincipalFlaggedRow>? Rows);

    private void Log(ProtectedPrincipalMigrationResult result)
    {
        foreach (var c in result.Converted)
            _logger.LogInformation("Protected-principal identifier pass: {Kind} '{Value}' -> {IdKind} {Identifier}",
                c.LegacyKind, c.LegacyValue, c.IdKind, c.Identifier);
        foreach (var l in result.Left)
            _logger.LogWarning("Protected-principal identifier pass: {Kind} '{Value}' left, enforced by name as before: {Detail}",
                l.Kind, l.Value, l.Detail);
        foreach (var s in result.Skipped)
            _logger.LogWarning("Protected-principal identifier pass: {Kind} '{Value}' changed after the pass read it and was left as that change left it",
                s.LegacyKind, s.LegacyValue);
    }

    private void Audit(string actor, string ipAddress, ProtectedPrincipalMigrationResult result)
    {
        try
        {
            _audit?.Invoke(actor, ipAddress, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit failed for the protected-principal identifier pass ({Status})", result.Status);
        }
    }

    /// <summary>One audit event per pass (B.4 step 5): each conversion and each flagged row with its reason.</summary>
    internal static Dictionary<string, object?> AuditDetail(ProtectedPrincipalMigrationResult result) => new()
    {
        ["status"] = result.Status.ToString(),
        ["converted"] = result.Converted.Select(c => $"{c.LegacyKind}: {c.LegacyValue} -> {c.Identifier}").ToArray(),
        ["left"] = result.Left.Select(l => $"{l.Kind}: {l.Value} ({l.Detail})").ToArray(),
        ["changedElsewhere"] = result.Skipped.Select(s => $"{s.LegacyKind}: {s.LegacyValue}").ToArray(),
    };

    private static void WriteAudit(AuditService audit, string actor, string ipAddress, ProtectedPrincipalMigrationResult result)
    {
        var success = result.Status is not (ProtectedPrincipalMigrationStatus.StoreUnavailable or ProtectedPrincipalMigrationStatus.DirectoryUnavailable);
        audit.LogModuleAction(actor, ipAddress, AuditAction, "AdminSettings", "Protected Principals", success,
            errorDetail: success ? null : result.Summary, extra: AuditDetail(result));
    }
}
