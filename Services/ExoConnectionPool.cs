using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography.X509Certificates;

namespace ExchangeAdminWeb.Services;

public sealed class PooledRunspace
{
    public PowerShell PowerShell { get; }
    public Runspace Runspace { get; }
    public DateTime LastUsed { get; set; }
    public long ConfigGeneration { get; }

    /// <summary>
    /// The ExchangeOnlineManagement ConnectionId this runspace's session was given, or null when
    /// it could not be established.
    /// </summary>
    /// <remarks>
    /// Audit finding A6B. `Disconnect-ExchangeOnline` with no selector calls
    /// `RemoveAllConnectionContexts()` against a STATIC map, so it tears down every connection in
    /// the process - every other borrower's included, mid-operation. Verified against the
    /// installed 3.9.2 module. The id is what lets this runspace's session be closed on its own.
    ///
    /// Null means "do not disconnect": leaking one server-side session until it times out is
    /// recoverable, and breaking every other operator is not.
    /// </remarks>
    public string? ConnectionId { get; }

    public PooledRunspace(Runspace runspace, PowerShell ps, long configGeneration = 0, string? connectionId = null)
    {
        Runspace = runspace;
        PowerShell = ps;
        LastUsed = DateTime.UtcNow;
        ConfigGeneration = configGeneration;
        ConnectionId = connectionId;
    }
}

/// <summary>
/// Result of running a delegate on a borrowed connection.
/// <para><see cref="ConnectionFailure"/>: the delegate returned normally (RunAsync swallows
/// the throw) but a dead/suspect session was detected - gates DISCARD.</para>
/// <para><see cref="RetriablePrecheck"/>: the captured failure was the narrow
/// "must call Connect-ExchangeOnline" pre-cmdlet signature, proving the cmdlet never ran -
/// gates RETRY. Strictly implies <see cref="ConnectionFailure"/>. A mid-flight drop sets
/// ConnectionFailure (discard) but NOT RetriablePrecheck (no replay).</para>
/// Connection failures that surface as a thrown exception are classified by the orchestrator
/// from the exception directly.
/// </summary>
internal readonly record struct PooledOutcome<T>(T Result, bool ConnectionFailure, bool RetriablePrecheck = false);

/// <summary>
/// What to do with the borrowed connection when the delegate throws a NON-connection
/// error. Base helpers <see cref="ExchangeServiceBase.Invoke(PowerShell, ExchangeServiceBase.ConnectionErrorTracker)"/>
/// clear the pipeline before throwing, so the connection is clean and can be returned.
/// PermissionValidator uses raw <c>ps.Invoke()</c> and cannot guarantee a clean pipeline,
/// so it discards (preserving its prior behavior; avoids poisoning the next borrow).
/// Connection errors ALWAYS discard regardless of this policy - the session is dead.
/// </summary>
internal enum PoolFailurePolicy { Return, Discard }

/// <summary>
/// The three values that define an Exchange Online connection - exactly what
/// <c>Components/Pages/ExchangeOnlineConfig.razor</c> saves and <c>Connect-ExchangeOnline</c>
/// is given. Two runspaces connected under equal values are interchangeable; a change to any
/// one of them retires every runspace connected under the old values.
/// </summary>
internal readonly record struct ExoConnectionConfig(string AppId, string Organization, string CertificateSubject);

public sealed class ExoConnectionPool : IDisposable
{
    public const string ConfigModuleKey = "ExchangeOnline";
    public const string ConfigAppIdKey = "AppId";
    public const string ConfigOrganizationKey = "Organization";
    public const string ConfigCertSubjectKey = "CertificateSubject";

    private readonly ConcurrentBag<PooledRunspace> _available = new();
    private readonly SemaphoreSlim _slots;
    private readonly ILogger<ExoConnectionPool> _logger;
    private readonly OperationTraceService _operationTrace;
    private readonly ModuleEnablementService _enablement;
    private readonly ModuleConfigService _moduleConfig;
    private readonly IConfiguration _config;
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(20);
    private readonly Timer _cleanupTimer;
    private readonly Func<ExoConnectionConfig, long, CancellationToken, PooledRunspace> _connect;
    private readonly object _connectedLock = new();
    private long _configGeneration;
    private bool _disposed;

    // The connection config every runspace in the pool (and every one still borrowed) was
    // connected under; null until the first borrow. Review finding scdi-1: with the shared
    // config database an ExchangeOnline save on the OTHER instance is live here at once for
    // reads, but only the saving process's page calls DrainPool - so each borrow compares the
    // config it just read against this and drains itself when the connection fields moved.
    private ExoConnectionConfig? _connectedUnder;

    public ExoConnectionPool(IConfiguration config, ModuleConfigService moduleConfig, ModuleEnablementService enablement, ILogger<ExoConnectionPool> logger, OperationTraceService operationTrace)
        : this(config, moduleConfig, enablement, logger, operationTrace, connect: null)
    {
    }

    /// <summary>
    /// Test seam: <paramref name="connect"/> replaces the live <c>Connect-ExchangeOnline</c> step.
    /// It receives the config to connect under and the pool generation the runspace must carry.
    /// </summary>
    internal ExoConnectionPool(IConfiguration config, ModuleConfigService moduleConfig, ModuleEnablementService enablement, ILogger<ExoConnectionPool> logger, OperationTraceService operationTrace,
        Func<ExoConnectionConfig, long, CancellationToken, PooledRunspace>? connect)
    {
        _logger = logger;
        _operationTrace = operationTrace;
        _enablement = enablement;
        _moduleConfig = moduleConfig;
        _config = config;
        _connect = connect ?? CreateConnected;
        _slots = new SemaphoreSlim(5, 5);
        _cleanupTimer = new Timer(CleanupIdle, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>Test seam: the current pool generation (incremented by every drain).</summary>
    internal long ConfigGeneration => Interlocked.Read(ref _configGeneration);

    /// <summary>Test seam: how many idle runspaces the pool holds right now.</summary>
    internal int AvailableCount => _available.Count;

    public bool IsConfigured
    {
        get
        {
            if (!_enablement.IsModuleEnabled("ExchangeOnline"))
                return false;
            var (appId, org, _) = GetExoConfig();
            return !string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(org);
        }
    }

    private ExoConnectionConfig GetExoConfig()
    {
        var appId = _moduleConfig.GetValue(ConfigModuleKey, ConfigAppIdKey);
        var organization = _moduleConfig.GetValue(ConfigModuleKey, ConfigOrganizationKey);
        var certSubject = _moduleConfig.GetValue(ConfigModuleKey, ConfigCertSubjectKey);

        if (_moduleConfig.HasModuleConfigFile(ConfigModuleKey) && _moduleConfig.IsModuleCorrupt(ConfigModuleKey))
        {
            _logger.LogError("ExchangeOnline module config is corrupt - refusing to fall back to appsettings");
            return new ExoConnectionConfig("", "", "");
        }

        appId ??= _config["ExchangeOnline:AppId"] ?? "";
        organization ??= _config["ExchangeOnline:Organization"] ?? "";
        certSubject ??= _config["ExchangeOnline:CertificateSubject"]
            ?? "CN=EXO-Automation";
        return new ExoConnectionConfig(appId, organization, certSubject);
    }

    public Task<PooledRunspace> BorrowAsync(CancellationToken ct = default)
        => BorrowAsync(TimeSpan.FromMinutes(2), ct);

    /// <summary>
    /// Borrow with a caller-chosen wait: interactive read paths (autocomplete)
    /// should give up quickly instead of queueing keystrokes for minutes behind
    /// mutation operations.
    /// </summary>
    public async Task<PooledRunspace> BorrowAsync(TimeSpan waitTimeout, CancellationToken ct = default)
    {
        if (!_enablement.IsModuleEnabled("ExchangeOnline"))
            throw new InvalidOperationException("Exchange Online module is not enabled. Enable it in Admin Settings and configure the connection on the Exchange Online config page.");

        // Read fresh on every borrow (ModuleConfigService has no cache; this is the shared
        // database). The same values decide the not-configured refusal below, the stale-pool
        // check inside the slot, and what a new runspace connects under.
        var current = GetExoConfig();
        var org = current.Organization;
        if (string.IsNullOrWhiteSpace(current.AppId) || string.IsNullOrWhiteSpace(org))
            throw new InvalidOperationException("Exchange Online is not configured. Set AppId and Organization on the Exchange Online config page.");

        _operationTrace.Step("ExoPoolSlotRequested", backend: "ExchangeOnline", details: new Dictionary<string, object?> { ["organization"] = org });
        if (!await _slots.WaitAsync(waitTimeout, ct))
        {
            _operationTrace.Step("ExoPoolSlotRequested", "Failed", backend: "ExchangeOnline", details: new Dictionary<string, object?> { ["reason"] = "Pool busy" });
            throw new InvalidOperationException("Exchange service is busy. Please try again shortly.");
        }

        _operationTrace.Step("ExoPoolSlotAcquired", backend: "ExchangeOnline");

        try
        {
            // scdi-1: a save on the other instance never reaches this process's DrainPool, so
            // the pool retires its own runspaces when the connection config it just read
            // differs from the one they connected under.
            //
            // The generation comes back from that same call, read under the same lock, and is
            // never re-read here. Review of aud-a6 found that reading it separately let the two
            // drift apart: this borrow could compare configs, pause, watch another borrow retire
            // the generation for a DIFFERENT organization, and then stamp its own connection
            // with that newer generation. The connection would be for the organization THIS
            // borrow read, but it would pass the staleness test of the newer one, be returned to
            // the pool and handed to a borrower of the other organization. That is persistent
            // contamination, not one operation finishing late.
            var (_, currentGen) = DrainIfConnectionConfigChanged(current);
            if (_available.TryTake(out var pooled))
            {
                if (pooled.ConfigGeneration == currentGen && DateTime.UtcNow - pooled.LastUsed < _idleTimeout)
                {
                    pooled.LastUsed = DateTime.UtcNow;
                    pooled.PowerShell.Commands.Clear();
                    pooled.PowerShell.Streams.ClearStreams();
                    _operationTrace.Step("ExoConnectionBorrowed", backend: "ExchangeOnline", details: new Dictionary<string, object?> { ["source"] = "Pool" });
                    return pooled;
                }

                _operationTrace.Step(pooled.ConfigGeneration != currentGen ? "ExoConnectionStaleConfig" : "ExoConnectionExpired", backend: "ExchangeOnline");
                var stale = pooled;
                await Task.Run(() => DestroyRunspace(stale), ct);
            }

            // Connect-ExchangeOnline takes multiple seconds and runs synchronously
            // inside the runspace. When BorrowAsync is awaited from a Blazor
            // handler, running it inline blocks the circuit dispatcher and freezes
            // every UI event for that user until the connect finishes - run it on
            // the thread pool instead.
            //
            // The runspace carries the generation read BEFORE the connect (currentGen), under
            // the config read before it: a drain that lands while Connect-ExchangeOnline runs
            // retired exactly that config, so the runspace must come back stale and be
            // discarded on Return instead of being stamped with the post-drain generation.
            return await Task.Run(() => _connect(current, currentGen, ct), ct);
        }
        catch (Exception ex)
        {
            _operationTrace.Step("ExoConnectionBorrowed", "Failed", backend: "ExchangeOnline", exception: ex);
            _slots.Release();
            throw;
        }
    }

    public void Return(PooledRunspace runspace)
    {
        if (runspace.ConfigGeneration != Interlocked.Read(ref _configGeneration))
        {
            DestroyRunspace(runspace);
            _operationTrace.Step("ExoConnectionDiscardedStaleConfig", backend: "ExchangeOnline");
            _slots.Release();
            return;
        }

        runspace.LastUsed = DateTime.UtcNow;
        runspace.PowerShell.Commands.Clear();
        runspace.PowerShell.Streams.ClearStreams();
        _available.Add(runspace);
        _operationTrace.Step("ExoConnectionReturned", backend: "ExchangeOnline");
        _slots.Release();
    }

    public void Discard(PooledRunspace runspace)
    {
        DestroyRunspace(runspace);
        _operationTrace.Step("ExoConnectionDiscarded", backend: "ExchangeOnline");
        _slots.Release();
    }

    /// <summary>
    /// Borrow a connection, run <paramref name="run"/> on it, and return/discard it. If the
    /// failure is a dead/stale session (a thrown connection error, or a returned outcome with
    /// <see cref="PooledOutcome{T}.ConnectionFailure"/> set) AND <paramref name="allowRetry"/>
    /// is true, the dead connection is discarded and the delegate runs <b>once</b> more on a
    /// fresh borrow. This heals the "you must call Connect-ExchangeOnline" failure the pool
    /// produces when EXO tears a session down inside the idle window.
    ///
    /// <para><paramref name="allowRetry"/> MUST be true only for read-only or single-write
    /// delegates. Re-running a delegate that already committed an earlier write would repeat
    /// it (double-grant, duplicate batch, etc.), so multi-write delegates pass false. The
    /// default at every layer is false (no retry) - the safe default.</para>
    /// </summary>
    internal Task<T> RunWithRetryAsync<T>(
        Func<PooledRunspace, Task<PooledOutcome<T>>> run,
        bool allowRetry,
        PoolFailurePolicy nonConnectionFailurePolicy)
        => RunWithRetryCoreAsync(() => BorrowAsync(), Return, Discard, run,
            IsConnectionError, IsRetriablePrecheckError, allowRetry, nonConnectionFailurePolicy);

    /// <summary>
    /// Broad classifier for "the pooled EXO session is dead/suspect" - gates DISCARD. Any of
    /// these means the connection must not return to the pool. <see cref="ExchangeServiceBase.IsConnectionError"/>
    /// delegates here so the rule lives in one place.
    /// </summary>
    public static bool IsConnectionError(Exception? ex) =>
        ex != null && (
            ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("runspace", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Connect-ExchangeOnline", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Narrow classifier for "the borrowed session was already dead BEFORE the first cmdlet ran"
    /// - gates RETRY (review #2, 2026-06-26). The EXO module's pre-flight context check throws
    /// "You must call Connect-ExchangeOnline before calling any other cmdlet" /
    /// "GetCurrentConnectionContext" only before a cmdlet executes, so the operation provably did
    /// nothing and replay is safe. This is STRICTLY NARROWER than <see cref="IsConnectionError"/>:
    /// a session that drops during/after a cmdlet yields a different transport error that
    /// discards but must NOT retry (it could re-submit an already-accepted single write, e.g.
    /// Start-HistoricalSearch). Every retriable error is also a connection error, so discard
    /// always fires alongside.
    /// </summary>
    public static bool IsRetriablePrecheckError(Exception? ex) =>
        ex != null && (
            ex.Message.Contains("Connect-ExchangeOnline", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("GetCurrentConnectionContext", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Pure orchestration core for <see cref="RunWithRetryAsync{T}"/>, expressed over injected
    /// borrow/return/discard primitives so it is unit-testable without a live EXO connection
    /// (pool-backed services cannot be unit-hosted). Invariant: every borrow is paired with
    /// exactly one return-or-discard, so the pool's slot semaphore is conserved across retries.
    /// </summary>
    internal static async Task<T> RunWithRetryCoreAsync<T>(
        Func<Task<PooledRunspace>> borrow,
        Action<PooledRunspace> ret,
        Action<PooledRunspace> discard,
        Func<PooledRunspace, Task<PooledOutcome<T>>> run,
        Func<Exception, bool> isConnectionError,
        Func<Exception, bool> isRetriablePrecheckError,
        bool allowRetry,
        PoolFailurePolicy nonConnectionFailurePolicy)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            var canRetry = allowRetry && attempt < 2;
            var pooled = await borrow();

            PooledOutcome<T> outcome;
            try
            {
                outcome = await run(pooled);
            }
            catch (Exception ex) when (isConnectionError(ex))
            {
                // Dead/suspect session surfaced as a throw. Always discard. Retry ONLY if the
                // error proves the cmdlet never ran (narrow pre-check signature) - a mid-flight
                // drop discards but must not replay a possibly-committed write.
                discard(pooled);
                if (canRetry && isRetriablePrecheckError(ex)) continue;
                throw;
            }
            catch
            {
                // Non-connection throw: the session is fine. Return or discard per the
                // caller's pipeline-cleanliness policy; never retried.
                if (nonConnectionFailurePolicy == PoolFailurePolicy.Discard) discard(pooled);
                else ret(pooled);
                throw;
            }

            if (outcome.ConnectionFailure)
            {
                // Dead/suspect session surfaced as a flagged-but-returned outcome (RunAsync
                // swallows the throw and returns a Fail result). Always discard. Retry ONLY if
                // the captured failure was the narrow pre-check signature.
                discard(pooled);
                if (canRetry && outcome.RetriablePrecheck) continue;
                return outcome.Result;
            }

            ret(pooled);
            return outcome.Result;
        }
    }

    /// <summary>
    /// Destroys all pooled connections so that subsequent borrows create new
    /// connections using the current config. Call after EXO config changes made IN THIS
    /// PROCESS (the config page). A change made by the other instance on the shared database
    /// is caught by the borrow path itself (<see cref="DrainIfConnectionConfigChanged"/>).
    /// </summary>
    public void DrainPool()
    {
        // The caller saved first, so what the store holds now is what the next runspace will
        // connect under; recording it keeps the next borrow from draining a second time. Read
        // before the lock so the store is not touched while holding it.
        var now = GetExoConfig();

        // Retiring and publishing are one transition here too. Review of aud-a6: this path used
        // to retire first and publish afterwards, which left its own window - a borrow holding
        // an older config snapshot could pick up the already-incremented generation and stamp a
        // connection to the previous organization with it.
        lock (_connectedLock)
        {
            RetireGeneration();
            _connectedUnder = now;
        }

        DestroyPooled();
    }

    /// <summary>
    /// Review finding scdi-1. Compares the connection config this borrow just read against the
    /// one the pooled runspaces connected under and, when any of the three fields differs,
    /// drains (increments the generation exactly as <see cref="DrainPool"/> does) so no runspace
    /// connected under the previous AppId / Organization / certificate is handed out or
    /// returned to the pool. A write to any OTHER module's config leaves these values equal and
    /// drains nothing. Returns true when it drained.
    /// </summary>
    internal (bool drained, long generation) DrainIfConnectionConfigChanged(ExoConnectionConfig current)
    {
        ExoConnectionConfig previous;
        long generation;
        lock (_connectedLock)
        {
            if (_connectedUnder is null || _connectedUnder.Value == current)
            {
                _connectedUnder = current;
                return (false, Interlocked.Read(ref _configGeneration));
            }

            previous = _connectedUnder.Value;
            _connectedUnder = current;

            // Audit finding A6. The generation used to be incremented by DrainPoolCore AFTER
            // this lock was released, which left a window: a second borrower arriving in it
            // read the NEW marker, so this method returned false for them, and then read the
            // OLD generation - so a pooled runspace still connected to the PREVIOUS
            // organization passed the staleness test and was handed out. Commands then ran
            // against the wrong tenant.
            //
            // Publishing the marker and retiring the generation must be one transition, so the
            // increment happens here, under the same lock. Destroying the runspaces does not -
            // that is slow and does not need to be atomic, because the generation bump has
            // already made every one of them fail the borrow test.
            generation = RetireGeneration();
        }

        _logger.LogInformation(
            "EXO pool: connection config changed outside this process (Org {OldOrg} -> {NewOrg}); discarding runspaces connected under the previous config",
            previous.Organization, current.Organization);
        _operationTrace.Step("ExoPoolSharedConfigChanged", backend: "ExchangeOnline",
            details: new Dictionary<string, object?> { ["previousOrganization"] = previous.Organization, ["organization"] = current.Organization });
        DestroyPooled();
        return (true, generation);
    }

    /// <summary>
    /// Retires every runspace created so far by moving the generation on. Nothing is destroyed
    /// here; the bump alone is what makes a pooled runspace fail the borrow test at
    /// <see cref="ConfigGeneration"/>, so this is the half that has to be atomic with
    /// publishing a new connection identity.
    /// </summary>
    private long RetireGeneration() => Interlocked.Increment(ref _configGeneration);

    private void DestroyPooled()
    {
        var drained = 0;
        while (_available.TryTake(out var item))
        {
            DestroyRunspace(item);
            drained++;
        }

        if (drained > 0)
            _logger.LogInformation("EXO pool: drained {Count} pooled connection(s) after config change", drained);

        _operationTrace.Step("ExoPoolDrained", backend: "ExchangeOnline",
            details: new Dictionary<string, object?> { ["connectionsDestroyed"] = drained });
    }

    /// <summary>
    /// Audit finding A6B. Connection establishment is serialised, and it has to be: the only way
    /// to learn the ConnectionId the module generated is to compare the process-wide connection
    /// list before and after, and two concurrent connects make that comparison ambiguous.
    /// </summary>
    /// <remarks>
    /// Held across the whole Connect-ExchangeOnline call, which takes seconds. That is the cost
    /// of the fix and it is deliberate: borrowing stays concurrent, only ESTABLISHING a new
    /// connection queues, and the pool reuses connections so this is not the common path.
    /// </remarks>
    /// <remarks>
    /// A semaphore rather than `lock`, because review of the first A6B fix showed `lock` was an
    /// availability bug. A borrower has already taken one of five slots before it gets here, so
    /// one stalled establishment could pin every slot with no timeout and no cancellation, and
    /// `Task.Run(..., ct)` does not cancel a delegate already blocked inside a monitor.
    ///
    /// The section is also dearer than it looks: `Get-ConnectionInformation` pings each existing
    /// connection over HTTP synchronously, so it scales with the number of live connections, not
    /// with one. Serial establishment is the accepted cost of attributing the id; an unbounded
    /// queue behind it is not.
    /// </remarks>
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    /// <summary>
    /// How long a borrower waits for its turn to establish a connection before giving up. Long
    /// enough for a connect plus its snapshots ahead of it, short enough that a wedged
    /// establishment cannot hold the pool shut.
    /// </summary>
    private static readonly TimeSpan ConnectGateTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The one id present after a connect that was not present before it. Null when that is not
    /// exactly one id, which is the only honest answer - and callers must treat null as "never
    /// disconnect this", because the alternative selector disconnects everybody.
    /// </summary>
    internal static string? NewConnectionId(IReadOnlyCollection<string>? before, IReadOnlyCollection<string>? after)
    {
        // Either snapshot being unreadable means there is no difference to take. Treating a
        // failed read as "nothing was there" would invent an attribution out of a blind spot.
        if (before is null || after is null)
            return null;

        var added = after.Where(id => !before.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        return added.Count == 1 ? added[0] : null;
    }

    /// <summary>
    /// The ConnectionIds the module currently holds - process-wide, not per-runspace - or
    /// <c>null</c> when the list could not be read at all.
    /// </summary>
    /// <remarks>
    /// Null and empty are different answers and review of the first A6B fix was right to insist
    /// on it: an earlier version returned an empty list on failure, so "I could not look" was
    /// indistinguishable from "there is nothing there". That silently turned a readable
    /// connection into an unattributable one.
    ///
    /// This also pings every existing connection over HTTP, synchronously, inside the module.
    /// It is not a cheap map read, which is why the caller holds a bounded gate around it.
    /// </remarks>
    private static List<string>? ReadConnectionIds(PowerShell ps)
    {
        try
        {
            ps.Commands.Clear();
            ps.AddCommand("Get-ConnectionInformation").AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();

            if (ps.Streams.Error.Count > 0)
                return null;

            return results
                .Select(item => item?.Properties["ConnectionId"]?.Value?.ToString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToList();
        }
        catch
        {
            return null;
        }
        finally
        {
            ps.Commands.Clear();
            ps.Streams.ClearStreams();
        }
    }

    private PooledRunspace CreateConnected(ExoConnectionConfig config, long generation, CancellationToken cancellationToken)
    {
        var (appId, organization, certSubject) = config;

        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        var ps = PowerShell.Create();
        ps.Runspace = runspace;

        try
        {
            _operationTrace.Step("ExoConnectionCreating", backend: "ExchangeOnline", command: "Connect-ExchangeOnline", details: new Dictionary<string, object?> { ["organization"] = organization });

            ps.AddCommand("Import-Module")
              .AddParameter("Name", "ExchangeOnlineManagement")
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            var cert = FindCertificate(certSubject);

            // A6B: the connect and the two reads around it are one critical section. The id is
            // only identifiable as "the one that appeared", so nothing else may connect in
            // between.
            string? connectionId;
            if (!_connectGate.Wait(ConnectGateTimeout, cancellationToken))
                throw new InvalidOperationException(
                    "Exchange service is busy establishing connections. Please try again shortly.");
            try
            {
                var before = ReadConnectionIds(ps);

                ps.AddCommand("Connect-ExchangeOnline")
                  .AddParameter("AppId", appId)
                  .AddParameter("CertificateThumbprint", cert.Thumbprint)
                  .AddParameter("Organization", organization)
                  .AddParameter("ShowBanner", false)
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();

                if (ps.Streams.Error.Count > 0)
                {
                    var msg = ps.Streams.Error.First().Exception?.Message ?? ps.Streams.Error.First().ToString();
                    throw new InvalidOperationException($"EXO Connect failed: {msg}");
                }

                ps.Commands.Clear();
                ps.Streams.ClearStreams();

                connectionId = NewConnectionId(before, ReadConnectionIds(ps));
            }
            finally
            {
                _connectGate.Release();
            }

            if (connectionId is null)
            {
                // Not fatal - the connection works. It just cannot be closed on its own later,
                // so Return and the drain will dispose it without disconnecting rather than
                // disconnect everybody. Worth a warning because it means a session will linger.
                _logger.LogWarning(
                    "EXO pool: could not identify the ConnectionId for a new connection (Org={Org}); its server session will be left to time out rather than risk disconnecting other operators",
                    organization);
            }

            _logger.LogInformation("EXO pool: created new connection (Org={Org})", organization);
            _operationTrace.Step("ExoConnectionCreated", backend: "ExchangeOnline", command: "Connect-ExchangeOnline", details: new Dictionary<string, object?> { ["organization"] = organization, ["connectionIdKnown"] = connectionId is not null });
            return new PooledRunspace(runspace, ps, generation, connectionId);
        }
        catch (Exception ex)
        {
            _operationTrace.Step("ExoConnectionCreated", "Failed", backend: "ExchangeOnline", command: "Connect-ExchangeOnline", exception: ex);
            ps.Dispose();
            runspace.Dispose();
            throw;
        }
    }

    private void DestroyRunspace(PooledRunspace pooled)
    {
        try
        {
            // Audit finding A6B. Disconnect-ExchangeOnline with no selector routes to
            // ConnectionContextFactory.RemoveAllConnectionContexts(), and that map is a STATIC
            // field - verified against the installed 3.9.2 module. So the unselected form used
            // to tear down every other borrower's live connection, mid-operation, every time
            // this pool retired one runspace.
            //
            // Without an id there is nothing safe to send: the only other form is the one that
            // hits everybody. Disposing without disconnecting leaks one server-side session
            // until it times out, which is recoverable; breaking every operator in the process
            // is not.
            if (string.IsNullOrWhiteSpace(pooled.ConnectionId))
            {
                _logger.LogWarning("EXO pool: disposing a connection with no known ConnectionId; its server session is left to time out");
            }
            else
            {
                pooled.PowerShell.Commands.Clear();
                pooled.PowerShell.AddCommand("Disconnect-ExchangeOnline")
                      .AddParameter("ConnectionId", pooled.ConnectionId)
                      .AddParameter("Confirm", false);
                pooled.PowerShell.Invoke();
            }
        }
        catch { }
        finally
        {
            try { pooled.PowerShell.Dispose(); } catch { }
            try { pooled.Runspace.Dispose(); } catch { }
        }
    }

    private void CleanupIdle(object? state)
    {
        var snapshot = new List<PooledRunspace>();
        while (_available.TryTake(out var item))
            snapshot.Add(item);

        foreach (var item in snapshot)
        {
            if (DateTime.UtcNow - item.LastUsed > _idleTimeout)
            {
                DestroyRunspace(item);
                _logger.LogInformation("EXO pool: disposed idle connection");
            }
            else
            {
                _available.Add(item);
            }
        }
    }

    private static X509Certificate2 FindCertificate(string certSubject)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var cert = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, certSubject, false)
            .OfType<X509Certificate2>()
            .Where(c => c.HasPrivateKey)
            .OrderByDescending(c => c.NotBefore)
            .FirstOrDefault();

        if (cert != null) return cert;

        using var userStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        userStore.Open(OpenFlags.ReadOnly);
        cert = userStore.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, certSubject, false)
            .OfType<X509Certificate2>()
            .Where(c => c.HasPrivateKey)
            .OrderByDescending(c => c.NotBefore)
            .FirstOrDefault();

        return cert ?? throw new InvalidOperationException($"Certificate '{certSubject}' not found in LocalMachine or CurrentUser certificate stores.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer.Dispose();

        while (_available.TryTake(out var item))
            DestroyRunspace(item);
    }
}
