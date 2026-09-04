using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Records anonymous usage events (docs/UsageTelemetry-Plan.md, AC3/AC10). Registered as a
/// singleton beside <see cref="AuditService"/>.
/// </summary>
/// <remarks>
/// Two properties define this class and every change to it must preserve them:
///
/// 1. It is ANONYMOUS. Nothing it writes identifies a person: no actor, no IP, no target, no
///    ticket, and the detail column is only ever an enum-like short string the app itself
///    chose (a route, a theme id, an audit action name), never operator input. The audit log
///    stays the record of who did what.
/// 2. It CANNOT break the caller. Every public Record method returns void, queues the write
///    through <see cref="Enqueue"/>, and never throws - the kill-switch read, the module
///    lookup and the insert are all inside a try/catch. This is the rule the Constitution
///    states for audit and notification failures, one layer down: a telemetry fault must not
///    change, delay or mask the operation that triggered it.
/// </remarks>
public class UsageTelemetryService
{
    /// <summary>How long rows are kept. A code constant, not a config key (owner ruling).</summary>
    public const int RetentionDays = 90;

    internal const string ConfigModuleId = "AdminEventLog";
    internal const string ConfigKey = "UsageTelemetryEnabled";

    /// <summary>
    /// Audit categories that predate the catalog ids and do not match one. The Event Log
    /// category dropdown enumerates the same strings. A category in neither this table nor the
    /// catalog maps to no module and is reported by its raw category name in the Usage view.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyCategoryToModule = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MailboxPermission"] = "MailboxPermissions",
        ["CalendarPermission"] = "CalendarPermissions",
        ["MigrationCheck"] = "Migration",
        ["MigrationBatch"] = "Migration",
        ["MigrationAction"] = "Migration",
    };

    private readonly UsageEventRepository _repository;
    private readonly ModuleConfigService _moduleConfig;
    private readonly ModuleCatalog _catalog;
    private readonly ILogger<UsageTelemetryService> _logger;

    private int _switchReadFailureLogged;

    public UsageTelemetryService(
        UsageEventRepository repository,
        ModuleConfigService moduleConfig,
        ModuleCatalog catalog,
        ILogger<UsageTelemetryService> logger)
    {
        _repository = repository;
        _moduleConfig = moduleConfig;
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>
    /// The kill switch: the UsageTelemetryEnabled Boolean field on the Event Log module,
    /// defaulting to on when it was never configured. An unreadable config reads as OFF and is
    /// logged once - a switch whose state cannot be established must not be assumed permissive.
    /// The Home page disclosure is driven by this same read, so the notice cannot claim
    /// collection that is not happening.
    /// </summary>
    public virtual bool Enabled()
    {
        try
        {
            if (_moduleConfig.IsModuleCorrupt(ConfigModuleId))
                return LogSwitchUnreadableOnce(null);

            var configured = _moduleConfig.GetValue(ConfigModuleId, ConfigKey);

            // Absent or blank is not a mistype: the field was never configured, and the
            // documented default is on.
            if (string.IsNullOrWhiteSpace(configured))
                return true;

            // An unparseable value follows the PreventSelfGrant convention and means the
            // default, because this guards a behavior preference rather than a control an
            // operator relies on for safety.
            return !bool.TryParse(configured, out var enabled) || enabled;
        }
        catch (Exception ex)
        {
            return LogSwitchUnreadableOnce(ex);
        }
    }

    /// <summary>Records that a visit began. Detail is null; the row exists to count visits.</summary>
    public void RecordSessionStart(string session) =>
        Record(session, kind: "session", module: null, category: null, detail: null, success: null);

    /// <summary>Records a page open. The module is null for home and access-denied.</summary>
    public void RecordOpen(string session, string? module, string? route) =>
        Record(session, kind: "open", module: module, category: null, detail: route, success: null);

    /// <summary>Records the theme a visit is on. The theme id is a catalog id, never free text.</summary>
    public void RecordTheme(string session, string themeId) =>
        Record(session, kind: "theme", module: null, category: null, detail: themeId, success: null);

    /// <summary>
    /// Records one audited action, called from the single common audit write so every audit
    /// method counts (review finding ute-2). The session comes from the ambient
    /// <see cref="UsageSession.Current"/> and is null when the action did not come from a
    /// browser circuit (a background job, startup) - the only nullable-session case. The raw
    /// category is stored alongside the mapped module so an action belonging to no catalog
    /// module can still be reported by name.
    /// </summary>
    public void RecordAction(string category, string action, bool success) =>
        Record(
            UsageSession.Current.Value,
            kind: "action",
            module: ModuleOf(category, _catalog),
            category: string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
            detail: string.IsNullOrWhiteSpace(action) ? null : action.Trim(),
            success: success);

    /// <summary>
    /// Maps an audit category to a catalog module id: the id verbatim when one matches, else
    /// the legacy table, else null. Pure and static so it can be tested without a service.
    /// </summary>
    internal static string? ModuleOf(string category, ModuleCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(category))
            return null;

        var trimmed = category.Trim();
        var module = catalog.GetById(trimmed);
        if (module != null)
            return module.Id;

        return LegacyCategoryToModule.GetValueOrDefault(trimmed);
    }

    /// <summary>
    /// The single write seam. Runs the insert off the caller's path and swallows everything: no
    /// telemetry failure may surface to, delay, or fail the operation that triggered it.
    /// Virtual so tests can capture events synchronously instead of racing a background task.
    /// </summary>
    internal virtual void Enqueue(UsageEvent e)
    {
        _ = Task.Run(() =>
        {
            try
            {
                _repository.Insert(e);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record a usage event of kind {Kind}; telemetry only", e.Kind);
            }
        });
    }

    /// <summary>
    /// The one path every Record method takes. The kill-switch read is inside the try because
    /// it touches the config store: a store fault here must be as harmless as an insert fault.
    /// </summary>
    private void Record(string? session, string kind, string? module, string? category, string? detail, bool? success)
    {
        try
        {
            if (!Enabled())
                return;

            Enqueue(new UsageEvent(DateTime.UtcNow, session, kind, module, category, detail, success));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to queue a usage event of kind {Kind}; telemetry only", kind);
        }
    }

    private bool LogSwitchUnreadableOnce(Exception? ex)
    {
        if (Interlocked.Exchange(ref _switchReadFailureLogged, 1) == 0)
        {
            _logger.LogWarning(
                ex,
                "Could not read the {Key} switch on {Module}; usage telemetry is treated as disabled",
                ConfigKey,
                ConfigModuleId);
        }
        return false;
    }
}
