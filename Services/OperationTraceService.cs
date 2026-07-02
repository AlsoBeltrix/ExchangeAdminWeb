using System.Diagnostics;
using System.Threading;

namespace ExchangeAdminWeb.Services;

public class OperationTraceService
{
    private static readonly AsyncLocal<OperationContext?> Current = new();
    private readonly JsonlLogService _log;
    private readonly bool _enabled;

    public OperationTraceService(IConfiguration config, JsonlLogService log)
    {
        _log = log;
        _enabled = config.GetValue<bool?>("OperationTrace:Enabled") ?? true;
    }

    public string? CurrentOperationId => Current.Value?.OperationId;

    public bool HasCurrentOperation => Current.Value != null;

    /// <summary>
    /// Begins a trace operation as a CLEAN ROOT, ignoring any ambient parent context. The bulk job
    /// runner needs this: AsyncLocal flows across Task.Run unless execution-context flow is
    /// suppressed, so a job pump could otherwise inherit a stale parent context from whatever ran
    /// before it and nest every row under it (codex review 2026-07-02). Each job row opens its own
    /// root here so its trace is correctly parented to nothing, not to leaked context.
    /// </summary>
    public OperationScope BeginRootOperation(
        string module,
        string action,
        string actor,
        string ipAddress,
        string? target = null,
        string? ticket = null,
        IReadOnlyDictionary<string, object?>? details = null)
        => BeginOperation(module, action, actor, ipAddress, target, ticket, details, forceRoot: true);

    public OperationScope BeginOperation(
        string module,
        string action,
        string actor,
        string ipAddress,
        string? target = null,
        string? ticket = null,
        IReadOnlyDictionary<string, object?>? details = null)
        => BeginOperation(module, action, actor, ipAddress, target, ticket, details, forceRoot: false);

    private OperationScope BeginOperation(
        string module,
        string action,
        string actor,
        string ipAddress,
        string? target,
        string? ticket,
        IReadOnlyDictionary<string, object?>? details,
        bool forceRoot)
    {
        // A root operation deliberately ignores any inherited ambient context so it parents to
        // nothing; a normal operation nests under the current context as before.
        var parent = forceRoot ? null : Current.Value;
        var context = new OperationContext(
            OperationId: Guid.NewGuid().ToString("N"),
            ParentOperationId: parent?.OperationId,
            Module: module,
            Action: action,
            Actor: SamName(actor),
            IpAddress: ipAddress,
            Target: string.IsNullOrWhiteSpace(target) ? null : target,
            Ticket: string.IsNullOrWhiteSpace(ticket) ? null : ticket,
            Started: Stopwatch.StartNew(),
            PriorContext: parent);

        Current.Value = context;
        Write("operation.start", context, stage: "Start", result: "Started", details: details);
        return new OperationScope(this, context);
    }

    public void Step(
        string stage,
        string result = "Success",
        string? backend = null,
        string? command = null,
        string? target = null,
        IReadOnlyDictionary<string, object?>? details = null,
        Exception? exception = null)
    {
        var context = Current.Value;
        if (context == null)
        {
            WriteStandaloneOperation(stage, result, backend, command, target, details, exception);
            return;
        }

        Write("operation.step", context, stage, result, backend, command, target, details, exception);
    }

    private void WriteStandaloneOperation(
        string stage,
        string result,
        string? backend,
        string? command,
        string? target,
        IReadOnlyDictionary<string, object?>? details,
        Exception? exception)
    {
        var context = new OperationContext(
            OperationId: Guid.NewGuid().ToString("N"),
            ParentOperationId: null,
            Module: string.IsNullOrWhiteSpace(backend) ? "OperationTrace" : backend,
            Action: string.IsNullOrWhiteSpace(command) ? stage : command,
            Actor: "unknown",
            IpAddress: "unknown",
            Target: string.IsNullOrWhiteSpace(target) ? null : target,
            Ticket: null,
            Started: Stopwatch.StartNew(),
            PriorContext: null);

        Write("operation.start", context, stage: "Standalone", result: "Started", details: new Dictionary<string, object?> { ["source"] = "BackendStep" });
        Write("operation.step", context, stage, result, backend, command, target, details, exception);
        Complete(context, !string.Equals(result, "Failed", StringComparison.OrdinalIgnoreCase), exception is null ? null : "Backend operation failed", exception);
    }

    private void Complete(OperationContext context, bool success, string? message = null, Exception? exception = null)
    {
        Write(
            "operation.complete",
            context,
            stage: "Complete",
            result: success ? "Success" : "Failed",
            details: string.IsNullOrWhiteSpace(message) ? null : new Dictionary<string, object?> { ["message"] = message },
            exception: exception);
    }

    private void End(OperationContext context)
    {
        if (ReferenceEquals(Current.Value, context))
            Current.Value = context.PriorContext;
    }

    private void Write(
        string eventType,
        OperationContext context,
        string stage,
        string result,
        string? backend = null,
        string? command = null,
        string? target = null,
        IReadOnlyDictionary<string, object?>? details = null,
        Exception? exception = null)
    {
        if (!_enabled)
            return;

        var evt = new Dictionary<string, object?>
        {
            ["eventType"] = eventType,
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["operationId"] = context.OperationId,
            ["parentOperationId"] = context.ParentOperationId,
            ["module"] = context.Module,
            ["action"] = context.Action,
            ["stage"] = stage,
            ["backend"] = backend,
            ["command"] = command,
            ["user"] = context.Actor,
            ["ip"] = context.IpAddress,
            ["target"] = string.IsNullOrWhiteSpace(target) ? context.Target : target,
            ["ticket"] = context.Ticket,
            ["result"] = result,
            ["durationMs"] = context.Started.ElapsedMilliseconds,
            ["details"] = SanitizeDetails(details),
            ["errorType"] = exception?.GetType().Name
        };

        _log.WriteTrace(evt);
    }

    private static Dictionary<string, object?>? SanitizeDetails(IReadOnlyDictionary<string, object?>? details)
    {
        if (details == null || details.Count == 0)
            return null;

        var sanitized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in details)
        {
            sanitized[key] = IsSensitiveKey(key) ? "***" : value;
        }

        return sanitized;
    }

    private static bool IsSensitiveKey(string key)
    {
        var lowered = key.ToLowerInvariant();
        return lowered.Contains("password")
            || lowered.Contains("secret")
            || lowered.Contains("token")
            || lowered.Contains("apikey")
            || lowered.Contains("api_key")
            || lowered.Contains("clientsecret")
            || lowered.Contains("client_secret");
    }

    private static string SamName(string identity) =>
        identity.Contains('\\') ? identity.Split('\\')[1] : identity;

    internal sealed record OperationContext(
        string OperationId,
        string? ParentOperationId,
        string Module,
        string Action,
        string Actor,
        string IpAddress,
        string? Target,
        string? Ticket,
        Stopwatch Started,
        OperationContext? PriorContext);

    public sealed class OperationScope : IDisposable
    {
        private readonly OperationTraceService _owner;
        private readonly OperationContext _context;
        private bool _completed;
        private bool _disposed;

        internal OperationScope(OperationTraceService owner, OperationContext context)
        {
            _owner = owner;
            _context = context;
        }

        public string OperationId => _context.OperationId;

        public void Complete(bool success, string? message = null, Exception? exception = null)
        {
            if (_completed)
                return;

            _owner.Complete(_context, success, message, exception);
            _completed = true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (!_completed)
                Complete(false, "Operation scope disposed before explicit completion");

            _owner.End(_context);
            _disposed = true;
        }
    }
}
