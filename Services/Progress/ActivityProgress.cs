namespace ExchangeAdminWeb.Services.Progress;

/// <summary>
/// How much an activity knows about its own size. This is the whole of the honesty rule from
/// docs/GlobalProgressSystem-Plan.md section 4: a module reports what it actually knows, and
/// there is deliberately no fourth option and no way to supply a percentage directly.
/// </summary>
public enum ActivityShape
{
    /// <summary>The module cannot know how much work there is. Renders as a moving bar, no number.</summary>
    Indeterminate,

    /// <summary>The module knows its stages. Renders as "step n of m".</summary>
    Steps,

    /// <summary>The module is iterating a known collection. Renders as "x of y".</summary>
    Items,
}

/// <summary>
/// The size an activity declares at <see cref="IActivityProgress.Begin"/>. A struct-like value
/// with private construction so the only way to get one is through the three factories: a module
/// cannot invent a shape, and cannot declare Steps or Items without supplying a real total.
/// </summary>
public sealed record ActivitySize
{
    private ActivitySize(ActivityShape shape, int total)
    {
        Shape = shape;
        Total = total;
    }

    public ActivityShape Shape { get; }

    /// <summary>Zero for <see cref="ActivityShape.Indeterminate"/>; otherwise at least one.</summary>
    public int Total { get; }

    /// <summary>For work whose size cannot be known in advance. The honest default.</summary>
    public static ActivitySize Unknown { get; } = new(ActivityShape.Indeterminate, 0);

    /// <summary>
    /// For work with a known number of stages. A total below one is not a step-shaped activity at
    /// all, so it degrades to <see cref="Unknown"/> rather than rendering "step 1 of 0".
    /// </summary>
    public static ActivitySize Steps(int total) =>
        total > 0 ? new ActivitySize(ActivityShape.Steps, total) : Unknown;

    /// <summary>
    /// For work iterating a collection whose size is known. As with <see cref="Steps"/>, a
    /// non-positive total degrades to <see cref="Unknown"/>.
    /// </summary>
    public static ActivitySize Items(int total) =>
        total > 0 ? new ActivitySize(ActivityShape.Items, total) : Unknown;
}

/// <summary>One progress report from a module, for the <see cref="IProgress{T}"/> bridge.</summary>
/// <param name="Done">Units completed. Ignored by an indeterminate activity.</param>
/// <param name="Detail">Optional short line under the label. Not a log; keep it operator-facing.</param>
public readonly record struct ActivityUpdate(int Done, string? Detail = null);

/// <summary>
/// A live activity, as the display reads it. Immutable snapshot: the service hands out copies so
/// a render cannot observe a half-applied update from another thread.
/// </summary>
public sealed record Activity
{
    public required string Id { get; init; }
    public required string ModuleId { get; init; }
    public required string Label { get; init; }
    public required ActivityShape Shape { get; init; }
    public required int Total { get; init; }
    public required int Done { get; init; }
    public string? Detail { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required bool CancellationRequested { get; init; }

    /// <summary>True only when a real total is known, so a determinate bar is honest.</summary>
    public bool IsDeterminate => Shape != ActivityShape.Indeterminate && Total > 0;

    /// <summary>
    /// Null for an indeterminate activity, and that is the point: there is no API on this type
    /// that yields a number for work whose size is unknown, so a caller cannot render a fake bar
    /// without writing the lie itself. Guarded by ActivityProgressTests.
    /// </summary>
    public int? PercentComplete =>
        IsDeterminate ? (int)Math.Clamp(Done * 100L / Total, 0L, 100L) : null;
}

/// <summary>How an activity ended, held briefly so the operator sees the result anywhere in the app.</summary>
public sealed record ActivityOutcome
{
    public required string Id { get; init; }
    public required string ModuleId { get; init; }
    public required string Label { get; init; }
    public required bool Success { get; init; }
    public string? Message { get; init; }
    public required DateTime EndedUtc { get; init; }
}

/// <summary>
/// A module's handle on one activity. Disposing it always ends the activity, so a `using` keeps
/// the display honest on every exception path.
/// </summary>
public interface IActivityHandle : IDisposable
{
    string Id { get; }

    /// <summary>
    /// The token the module must honour. Cancelling from the navigation guard sets this; work
    /// that ignores it leaves the operator told it stopped when it did not.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Sets units completed. A no-op on an indeterminate activity.</summary>
    void Report(int done, string? detail = null);

    /// <summary>Advances a step-shaped activity by one and relabels the current stage.</summary>
    void Step(string detail);

    /// <summary>
    /// Ends the activity. Safe to call more than once; the first call wins, so a `using` around an
    /// explicit Complete does not overwrite a real outcome with the dispose fallback.
    /// </summary>
    void Complete(bool success, string? message = null);

    /// <summary>
    /// An <see cref="IProgress{T}"/> view, for passing into a service method. Services must take
    /// progress as an argument rather than injecting <see cref="IActivityProgress"/>, because most
    /// module services are registered as singletons and would capture one circuit's sink for the
    /// life of the process. See docs/GlobalProgressSystem-Plan.md section 5.1.
    /// </summary>
    IProgress<ActivityUpdate> AsProgress();
}

/// <summary>
/// The app's single progress channel. Modules report into it; modules render nothing themselves.
/// </summary>
/// <remarks>
/// PER-CIRCUIT (scoped). It must never be injected into a singleton - see the lifetime rule in
/// docs/GlobalProgressSystem-Plan.md section 5.1 and the tripwire in
/// ExchangeAdminWeb.Tests/ActivityProgressLifetimeTests.cs. Pages own the handle and pass
/// <see cref="IActivityHandle.AsProgress"/> and the cancellation token down into services.
///
/// A service rather than page state for the reason recorded on <see cref="AdminPageDirtyState"/>:
/// this repo has no bUnit harness, so anything left in markup cannot be tested at all.
/// </remarks>
public interface IActivityProgress
{
    /// <summary>Starts an activity and returns its handle. Never returns null.</summary>
    IActivityHandle Begin(string moduleId, string label, ActivitySize size);

    /// <summary>Everything running right now, oldest first.</summary>
    IReadOnlyList<Activity> Active { get; }

    /// <summary>Recently ended activities, newest first, bounded. Cleared as they age out.</summary>
    IReadOnlyList<ActivityOutcome> RecentOutcomes { get; }

    /// <summary>True when anything is in flight. What the navigation guard asks.</summary>
    bool HasActive { get; }

    /// <summary>Requests cancellation of one activity.</summary>
    void Cancel(string id);

    /// <summary>Requests cancellation of everything in flight. The navigation guard's OK path.</summary>
    void CancelAll();

    /// <summary>Drops one recorded outcome once the operator has seen it.</summary>
    void DismissOutcome(string id);

    /// <summary>
    /// Raised on every change. Subscribers on a Blazor circuit MUST marshal through
    /// InvokeAsync: a module reporting from a worker thread raises this off the renderer.
    /// </summary>
    event Action? Changed;
}

/// <inheritdoc cref="IActivityProgress"/>
public sealed class ActivityProgressService : IActivityProgress, IDisposable
{
    // How many ended activities are kept for the result line. Small on purpose: this is a
    // transient operator notice, not a history. The durable record is the audit log.
    private const int MaxRecentOutcomes = 5;

    private readonly object _gate = new();
    private readonly List<ActivityState> _active = [];
    private readonly List<ActivityOutcome> _recent = [];
    private bool _disposed;

    public event Action? Changed;

    public IReadOnlyList<Activity> Active
    {
        get
        {
            lock (_gate)
            {
                return _active.Select(a => a.Snapshot()).ToList();
            }
        }
    }

    public IReadOnlyList<ActivityOutcome> RecentOutcomes
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToList();
            }
        }
    }

    public bool HasActive
    {
        get
        {
            lock (_gate)
            {
                return _active.Count > 0;
            }
        }
    }

    public IActivityHandle Begin(string moduleId, string label, ActivitySize size)
    {
        ArgumentNullException.ThrowIfNull(size);

        var state = new ActivityState(
            this,
            string.IsNullOrWhiteSpace(moduleId) ? "Unknown" : moduleId.Trim(),
            string.IsNullOrWhiteSpace(label) ? "Working" : label.Trim(),
            size);

        lock (_gate)
        {
            _active.Add(state);
        }

        Raise();
        return state;
    }

    public void Cancel(string id)
    {
        ActivityState? target;
        lock (_gate)
        {
            target = _active.FirstOrDefault(a => a.Id == id);
        }

        target?.RequestCancel();
        Raise();
    }

    public void CancelAll()
    {
        List<ActivityState> targets;
        lock (_gate)
        {
            targets = _active.ToList();
        }

        foreach (var target in targets)
            target.RequestCancel();

        if (targets.Count > 0)
            Raise();
    }

    public void DismissOutcome(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _recent.RemoveAll(o => o.Id == id) > 0;
        }

        if (removed)
            Raise();
    }

    /// <summary>
    /// Ends the circuit's activities. Anything still running is recorded as a failure rather than
    /// vanishing: an activity that was in flight when the circuit went away did NOT succeed, and
    /// silently dropping it is the exact defect this system exists to remove.
    /// </summary>
    public void Dispose()
    {
        List<ActivityState> stragglers;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            stragglers = _active.ToList();
        }

        foreach (var straggler in stragglers)
            straggler.Complete(false, "The page was closed before this finished.");

        lock (_gate)
        {
            _active.Clear();
            _recent.Clear();
        }
    }

    private void Finish(ActivityState state, bool success, string? message)
    {
        lock (_gate)
        {
            if (!_active.Remove(state))
                return;

            _recent.Insert(0, new ActivityOutcome
            {
                Id = state.Id,
                ModuleId = state.ModuleId,
                Label = state.Label,
                Success = success,
                Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
                EndedUtc = DateTime.UtcNow,
            });

            if (_recent.Count > MaxRecentOutcomes)
                _recent.RemoveRange(MaxRecentOutcomes, _recent.Count - MaxRecentOutcomes);
        }

        Raise();
    }

    // A subscriber that throws must not take down the module that was only reporting progress.
    // Mirrors BulkJobService's handling of JobChanged subscribers.
    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // Intentionally swallowed: a broken display is not a reason to fail an operation.
        }
    }

    private sealed class ActivityState : IActivityHandle
    {
        private readonly ActivityProgressService _owner;
        private readonly ActivitySize _size;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _stateGate = new();
        private int _done;
        private string? _detail;
        private bool _completed;

        internal ActivityState(ActivityProgressService owner, string moduleId, string label, ActivitySize size)
        {
            _owner = owner;
            _size = size;
            ModuleId = moduleId;
            Label = label;
            StartedUtc = DateTime.UtcNow;
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public string ModuleId { get; }

        public string Label { get; }

        public DateTime StartedUtc { get; }

        public CancellationToken CancellationToken => _cts.Token;

        public void Report(int done, string? detail = null)
        {
            if (_size.Shape == ActivityShape.Indeterminate)
            {
                // No count to show, but a detail line is still useful and still honest.
                if (detail is null)
                    return;

                lock (_stateGate)
                {
                    if (_completed)
                        return;
                    _detail = detail;
                }

                _owner.Raise();
                return;
            }

            lock (_stateGate)
            {
                if (_completed)
                    return;

                // Never report past the declared total: "10,400 of 10,000" reads as a bug and
                // destroys trust in every other number the system shows.
                _done = Math.Clamp(done, 0, _size.Total);
                if (detail is not null)
                    _detail = detail;
            }

            _owner.Raise();
        }

        public void Step(string detail)
        {
            int next;
            lock (_stateGate)
            {
                if (_completed)
                    return;
                next = _done + 1;
            }

            Report(next, detail);
        }

        public void Complete(bool success, string? message = null)
        {
            lock (_stateGate)
            {
                if (_completed)
                    return;
                _completed = true;
            }

            _owner.Finish(this, success, message);
        }

        public IProgress<ActivityUpdate> AsProgress() =>
            new Progress<ActivityUpdate>(u => Report(u.Done, u.Detail));

        /// <summary>
        /// Dispose without an explicit Complete records a failure, mirroring
        /// <c>OperationTraceService.OperationScope.Dispose</c>. An operation that fell out of its
        /// scope without saying it succeeded did not succeed.
        /// </summary>
        public void Dispose()
        {
            Complete(false, "This did not finish.");
            _cts.Dispose();
        }

        internal void RequestCancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished and disposed; nothing to cancel.
            }
        }

        internal Activity Snapshot()
        {
            lock (_stateGate)
            {
                return new Activity
                {
                    Id = Id,
                    ModuleId = ModuleId,
                    Label = Label,
                    Shape = _size.Shape,
                    Total = _size.Total,
                    Done = _done,
                    Detail = _detail,
                    StartedUtc = StartedUtc,
                    CancellationRequested = _cts.IsCancellationRequested,
                };
            }
        }
    }
}
