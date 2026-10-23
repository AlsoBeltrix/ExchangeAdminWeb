using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.ExceptionServices;
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
/// <para><see cref="Failure"/>: the delegate THREW after its tracker had already classified the
/// session (psl-4: e.g. a precheck record on the error stream, then an unrelated throw). The
/// outcome carries that classification and the original exception, so the pool discards and
/// takes the same eligible retry as above, and rethrows that same exception when it does not
/// retry. Only <see cref="ConnectionFault"/> sets it, and always with ConnectionFailure.</para>
/// Any other thrown exception is classified by the orchestrator from its own message.
/// </summary>
internal readonly record struct PooledOutcome<T>(T Result, bool ConnectionFailure, bool RetriablePrecheck = false)
{
    public ExceptionDispatchInfo? Failure { get; private init; }

    public static PooledOutcome<T> ConnectionFault(ExceptionDispatchInfo failure, bool retriablePrecheck) =>
        new(default!, ConnectionFailure: true, RetriablePrecheck: retriablePrecheck) { Failure = failure };
}

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

/// <summary>
/// The Exchange Online module config could not be read (aud-a28x). A read that failed is not config that is absent, so
/// appsettings is not read in its place and nothing is connected; the message names the rows and their fix.
/// A borrow throws it too when the module's enablement cannot be read (aud-a44), naming that store and its fix.
/// </summary>
public sealed class ExoConfigUnreadableException(string message) : InvalidOperationException(message)
{
}

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

    // Audit finding aud-a6c. Dispose used to drain only what was idle at that instant and leave
    // the pool open behind it: a runspace borrowed (or still connecting) at shutdown came back
    // afterwards, passed the generation test and was pooled into a pool whose cleanup timer had
    // stopped - never reaped, its session never closed. So the pool now has one stop transition
    // and one way in. _poolLock guards _stopped and every ADD to _available (TryPool); takes stay
    // lock-free, so borrowing is unchanged.
    private readonly object _poolLock = new();
    private bool _stopped;

    // Every Disconnect-ExchangeOnline that threw or wrote an error, for the life of the pool. A
    // failed disconnect used to be swallowed, so it looked identical to one that worked.
    private long _failedDisconnects;

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

    /// <summary>
    /// psl-9. Test seam: the session the connect step opens - the default runspace with the Bypass policy, not yet opened. A
    /// test hands back one of stand-in cmdlets, so the step itself runs with no module and no tenant (the <c>connect</c> seam
    /// replaces the whole step).
    /// </summary>
    internal Func<Runspace> ConnectRunspace { get; set; } = DefaultConnectRunspace;

    /// <summary>psl-9. Test seam: the thumbprint of the certificate the connect step signs in with, for its subject.</summary>
    internal Func<string, string> CertificateThumbprint { get; set; } = subject => FindCertificate(subject).Thumbprint;

    private static Runspace DefaultConnectRunspace()
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        return RunspaceFactory.CreateRunspace(iss);
    }

    /// <summary>Test seam: the current pool generation (incremented by every drain).</summary>
    internal long ConfigGeneration => Interlocked.Read(ref _configGeneration);

    /// <summary>Test seam: how many idle runspaces the pool holds right now.</summary>
    internal int AvailableCount => _available.Count;

    /// <summary>How many disconnects have failed over this pool's lifetime (aud-a6c).</summary>
    internal long FailedDisconnects => Interlocked.Read(ref _failedDisconnects);

    /// <summary>
    /// Whether Exchange Online is enabled and has an AppId and an Organization, for a caller that can show no more than
    /// that: false as well when the enablement or the rows cannot be read, and never a throw (aud-a28x). A caller that can
    /// say which asks <see cref="TryGetIsConfigured"/>.
    /// </summary>
    public bool IsConfigured => TryGetIsConfigured(out var configured) && configured;

    /// <summary>
    /// <see cref="IsConfigured"/> for a caller that must not take a store it cannot read for an Exchange Online that is
    /// not configured (aud-a28t): false when the module's enablement or its module config cannot be read, and then
    /// <paramref name="configured"/> means nothing; otherwise IsConfigured's rule, from one read of each.
    /// </summary>
    /// <remarks>
    /// IsConfigured reads both fail-open - the enablement as disabled, each value as absent and so from appsettings - and
    /// its false let the permission validator keep a protected group as a bare name, its members unprotected, while the
    /// resolver's own read of the same config answered. The appsettings fallback here is for a key the module config does
    /// not hold, never for a read that failed, and both now read through TryReadExoConfig: IsConfigured is this, read as a
    /// bool (aud-a28x).
    /// </remarks>
    public bool TryGetIsConfigured(out bool configured)
    {
        configured = false;
        if (!_enablement.TryGetModuleEnabled(ConfigModuleKey, out var enabled))
            return false;
        if (!enabled)
            return true;
        if (!TryReadExoConfig(out var exo))
            return false;

        configured = !string.IsNullOrWhiteSpace(exo.AppId) && !string.IsNullOrWhiteSpace(exo.Organization);
        return true;
    }

    /// <summary>
    /// The connection settings from one read of the ExchangeOnline rows, or false when the rows cannot be read (aud-a28x).
    /// Each key goes through <see cref="ExoConfigFallback.Resolve"/>, the one owner of the fallback rule (aud-a26s): a row as
    /// stored, a key with no row from appsettings. A failed read never reaches it - the Constitution allows the fallback
    /// only when the module config is absent, and rows that cannot be read are not absent.
    /// </summary>
    private bool TryReadExoConfig(out ExoConnectionConfig config)
    {
        if (!_moduleConfig.TryGetModuleConfig(ConfigModuleKey, out var values))
        {
            config = default;
            return false;
        }

        config = new ExoConnectionConfig(
            ExoConfigFallback.Resolve(_config, ConfigAppIdKey, values.GetValueOrDefault(ConfigAppIdKey)).Value,
            ExoConfigFallback.Resolve(_config, ConfigOrganizationKey, values.GetValueOrDefault(ConfigOrganizationKey)).Value,
            ExoConfigFallback.Resolve(_config, ConfigCertSubjectKey, values.GetValueOrDefault(ConfigCertSubjectKey)).Value);
        return true;
    }

    // The rows and their fix, for the refusal and its log line: a service's text, with no page to reload (aud-a26t).
    private static string UnreadableRows => SettingsStoreText.ServiceModuleConfig(ConfigModuleKey);

    // The enablement store and its fix, for the refusal and its log line, in a service's words (aud-a44, aud-a26t): the
    // legacy file when its import failed, else the table with the service's remedy - the store text's own service form
    // (aud-a50), where the page text with its reload clause replaced used to stand.
    private string UnreadableEnablement => SettingsStoreText.ServiceEnablement(_enablement.LegacyImportFailed);

    // aud-a28x. Three fail-open value reads used to be followed by a presence read and a probe, so a value read that failed
    // while those answered was taken for absent and given the appsettings value: a borrow could connect under another
    // tenant, app id or certificate than the module config's. One read now; a read that fails throws, before a borrow
    // asks for a slot and before a drain retires anything.
    private ExoConnectionConfig GetExoConfig()
    {
        if (TryReadExoConfig(out var config))
            return config;

        _logger.LogError("Exchange Online settings could not be read, and appsettings is not read in their place. {Store}", UnreadableRows);
        throw new ExoConfigUnreadableException("Exchange Online settings could not be read: " + UnreadableRows);
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
        // aud-a6c: a stopped pool opens no new sessions. Checked again once a slot is held
        // (aud-a6e), and before and after the connect (aud-a6f), because the wait for a slot and
        // the connect can each outlast the stop. A borrow that completes as the pool stops comes
        // back through Return, which refuses a stopped pool and destroys the runspace. That
        // refusal, not these checks, is what keeps a connection returned after the stop out of
        // the pool.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped), this);

        // aud-a44. The enablement read that tells a store it cannot read from a module that is off (aud-a28t). IsModuleEnabled
        // takes the first for every module disabled: closed, but the refusal sent an administrator to Admin Settings to
        // enable a module that was enabled. Named in the aud-a28x shape, before the rows are read and a slot is asked for.
        if (!_enablement.TryGetModuleEnabled(ConfigModuleKey, out var enabled))
        {
            _logger.LogError("The Exchange Online enablement could not be read, so no connection is opened. {Store}", UnreadableEnablement);
            throw new ExoConfigUnreadableException("The Exchange Online enablement could not be read: " + UnreadableEnablement);
        }

        if (!enabled)
            throw new InvalidOperationException("Exchange Online module is not enabled. Enable it in Admin Settings and configure the connection on the Exchange Online config page.");

        // Read fresh on every borrow (ModuleConfigService has no cache; this is the shared
        // database). The same values decide the not-configured refusal below, the stale-pool
        // check inside the slot, and what a new runspace connects under. A read that fails throws
        // here, before a slot is asked for, and nothing is connected under appsettings (aud-a28x).
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
            // aud-a6e: a borrow that passed the entry check while every slot was held used to
            // resume after Dispose and connect a fresh session from a stopped pool. So the stop is
            // checked again here, before anything is taken from the pool or connected; the catch
            // below gives the slot back.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped), this);

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

                // aud-a6d: no token here. This runspace has already left the pool, so destroying it
                // is cleanup this borrow owes, not work it may abandon - and Task.Run handed a token
                // that is already cancelled never runs its delegate, which stranded the connection.
                // Cancellation still applies: the connect below honours it and throws.
                await Task.Run(() => DestroyRunspace(stale));
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
            //
            // aud-a6f: entering the connect is part of the stop protocol. The stop is checked
            // again here because the destroy above can take seconds, and Dispose may have run
            // since the post-slot check: a stopped pool opens no new session.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped), this);
            var connected = await Task.Run(() => _connect(current, currentGen, ct), ct);

            // aud-a6f: and again once the connect returns, which itself takes seconds. A session
            // that finished opening after the stop is disconnected here instead of being handed to
            // a caller that may never return it; the throw goes through the catch below, which
            // gives the slot back. No token on the destroy - it is cleanup this borrow owes (aud-a6d).
            if (Volatile.Read(ref _stopped))
            {
                await Task.Run(() => DestroyRunspace(connected));
                _operationTrace.Step("ExoConnectionDiscardedPoolStopped", backend: "ExchangeOnline");
                throw new ObjectDisposedException(GetType().FullName);
            }

            return connected;
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

        // aud-a6c: a runspace borrowed before Dispose and returned after it passes the generation
        // test above, so the generation cannot be what refuses it - the stop flag is.
        if (TryPool(runspace))
        {
            _operationTrace.Step("ExoConnectionReturned", backend: "ExchangeOnline");
        }
        else
        {
            DestroyRunspace(runspace);
            _operationTrace.Step("ExoConnectionDiscardedPoolStopped", backend: "ExchangeOnline");
        }
        _slots.Release();
    }

    /// <summary>
    /// The only way into <c>_available</c>. Refuses once the pool has stopped, and the caller
    /// must then destroy what it offered: nothing will ever take it out of a stopped pool.
    /// </summary>
    /// <remarks>
    /// Audit finding aud-a6c. Why this closes the window rather than narrowing it: the add and the
    /// stop are serialised on <c>_poolLock</c>, and <see cref="Dispose"/> drains only AFTER its
    /// stop. So every add that succeeded happened before the drain began and the drain sees it;
    /// every offer made after the stop is refused here and destroyed by whoever made it.
    /// </remarks>
    private bool TryPool(PooledRunspace runspace)
    {
        lock (_poolLock)
        {
            if (_stopped)
                return false;
            _available.Add(runspace);
            return true;
        }
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
                // the captured failure was the narrow pre-check signature. A pooled query that
                // threw after its tracker classified the session arrives here too (psl-4); when
                // no retry is taken its original exception is rethrown, not a default result.
                discard(pooled);
                if (canRetry && outcome.RetriablePrecheck) continue;
                outcome.Failure?.Throw();
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
        // before the lock so the store is not touched while holding it. A store that cannot be read
        // throws here, before anything is retired, and the Exchange Online Config page reports the
        // drain as failed; the next borrow that reads the rows retires whatever was connected under
        // other values (aud-a28x, scdi-1).
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
        var (drained, failed) = DestroyAllPooled();

        if (drained > 0)
            _logger.LogInformation("EXO pool: drained {Count} pooled connection(s) after config change; {Failed} disconnect(s) failed", drained, failed);

        _operationTrace.Step("ExoPoolDrained", backend: "ExchangeOnline",
            details: new Dictionary<string, object?> { ["connectionsDestroyed"] = drained, ["disconnectsFailed"] = failed });
    }

    /// <summary>
    /// Takes every idle runspace out of the pool and destroys each one. <see cref="DestroyRunspace"/>
    /// never throws, so one failed disconnect cannot stop the rest; the failures are counted.
    /// </summary>
    private (int destroyed, int failed) DestroyAllPooled()
    {
        var destroyed = 0;
        var failed = 0;
        while (_available.TryTake(out var item))
        {
            destroyed++;
            if (!DestroyRunspace(item))
                failed++;
        }
        return (destroyed, failed);
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

        var runspace = ConnectRunspace();
        runspace.Open();
        var ps = PowerShell.Create();
        ps.Runspace = runspace;

        try
        {
            _operationTrace.Step("ExoConnectionCreating", backend: "ExchangeOnline", command: "Connect-ExchangeOnline", details: new Dictionary<string, object?> { ["organization"] = organization });

            // psl-9. The ActiveDirectoryModuleImport.Import shape: under Stop the cmdlet's own failure throws and leaves no
            // record, and the finally clears what a record written past the preference would leave - ReadConnectionIds, next,
            // takes any record in the stream for its own failure, and an unreadable snapshot leaves the new connection's id
            // unknown.
            ps.AddCommand("Import-Module")
              .AddParameter("Name", "ExchangeOnlineManagement")
              .AddParameter("ErrorAction", "Stop");
            try
            {
                ps.Invoke();
            }
            finally
            {
                ps.Commands.Clear();
                ps.Streams.Error.Clear();
            }

            var thumbprint = CertificateThumbprint(certSubject);

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
                  .AddParameter("CertificateThumbprint", thumbprint)
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

    /// <summary>
    /// Disconnects and disposes one runspace. Never throws. Returns false when a disconnect was
    /// attempted and failed; the failure is recorded here (aud-a6c).
    /// </summary>
    private bool DestroyRunspace(PooledRunspace pooled)
    {
        Exception? failure = null;
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
                // aud-a6c. Cleared FIRST: Discard reaches here straight from a failed operation,
                // and an error that borrower's own cmdlet left behind must not be read as the
                // disconnect's.
                pooled.PowerShell.Commands.Clear();
                pooled.PowerShell.Streams.ClearStreams();
                pooled.PowerShell.AddCommand("Disconnect-ExchangeOnline")
                      .AddParameter("ConnectionId", pooled.ConnectionId)
                      .AddParameter("Confirm", false);
                pooled.PowerShell.Invoke();

                // A disconnect can fail without throwing - a non-terminating error record - and
                // that used to be ignored along with the throw. Both are a session that may still
                // be open.
                if (pooled.PowerShell.Streams.Error.Count > 0)
                {
                    var error = pooled.PowerShell.Streams.Error[0];
                    failure = error.Exception ?? new InvalidOperationException(error.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            // Contained, not rethrown: every caller loops over several connections, and one
            // failed disconnect must not leave the rest of a drain or a shutdown undone.
            failure = ex;
        }
        finally
        {
            try { pooled.PowerShell.Dispose(); } catch { }
            try { pooled.Runspace.Dispose(); } catch { }
        }

        if (failure is null)
            return true;

        RecordFailedDisconnect(pooled.ConnectionId, failure);
        return false;
    }

    /// <summary>
    /// aud-a6c: a failed disconnect is counted, logged with the id it could not close and traced.
    /// It is not retried in place - the runspace is already gone - and this is the only record that
    /// the session may still be open, because the id was the only handle on it.
    /// </summary>
    private void RecordFailedDisconnect(string? connectionId, Exception failure)
    {
        var total = Interlocked.Increment(ref _failedDisconnects);
        _logger.LogWarning(failure,
            "EXO pool: Disconnect-ExchangeOnline failed for ConnectionId {ConnectionId}; that session may still be open and nothing will retry it ({FailedDisconnects} failed disconnect(s) so far)",
            connectionId, total);
        _operationTrace.Step("ExoConnectionDisconnectFailed", "Failed", backend: "ExchangeOnline", command: "Disconnect-ExchangeOnline",
            details: new Dictionary<string, object?> { ["connectionId"] = connectionId, ["failedDisconnects"] = total },
            exception: failure);
    }

    /// <summary>The cleanup timer's idle sweep. Internal as a test seam.</summary>
    /// <remarks>
    /// Audit finding aud-a6c. The sweep takes everything out and puts the keepers back, so a
    /// <see cref="Dispose"/> that lands in between drains an empty pool - and the sweep used to put
    /// its keepers back afterwards, into the stopped pool, where nothing would ever reap them. The
    /// put-back now goes through <see cref="TryPool"/>, and a refused keeper is destroyed with the
    /// idle ones: the sweep owns whatever it took.
    ///
    /// Keepers go back BEFORE anything is destroyed, so they are out of the pool for a moment
    /// rather than for a disconnect round trip each. The retiring count is logged before the
    /// put-back; the teardown tests (ExoConnectionPoolTeardownTests.cs) pause on that call to run
    /// Dispose inside the window.
    /// </remarks>
    internal void CleanupIdle(object? state)
    {
        var snapshot = new List<PooledRunspace>();
        while (_available.TryTake(out var item))
            snapshot.Add(item);

        var now = DateTime.UtcNow;
        var retiring = new List<PooledRunspace>();
        var keeping = new List<PooledRunspace>();
        foreach (var item in snapshot)
            (now - item.LastUsed > _idleTimeout ? retiring : keeping).Add(item);

        if (retiring.Count > 0)
            _logger.LogInformation("EXO pool: idle sweep retiring {Count} of {Total} pooled connection(s)", retiring.Count, snapshot.Count);

        foreach (var item in keeping)
        {
            if (!TryPool(item))
                retiring.Add(item);
        }

        var failed = 0;
        foreach (var item in retiring)
        {
            if (!DestroyRunspace(item))
                failed++;
        }

        if (retiring.Count > 0)
            _logger.LogInformation("EXO pool: idle sweep disposed {Count} connection(s); {Failed} disconnect(s) failed", retiring.Count, failed);
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

    /// <remarks>
    /// Audit finding aud-a6c. The stop is recorded under <c>_poolLock</c> BEFORE the drain, which
    /// is what lets <see cref="TryPool"/> promise that nothing is added after the drain has looked.
    /// Anything borrowed or held by a sweep in progress comes back through <see cref="TryPool"/>,
    /// is refused, and is destroyed by whoever was holding it. A borrow still waiting for a slot
    /// is refused when it gets one, and gives the slot back (aud-a6e); one still connecting
    /// disconnects what it connected instead of handing it out (aud-a6f).
    ///
    /// The finding suggested retiring the generation here instead. That cannot hold by itself: a
    /// borrow that read the generation AFTER such a retire would stamp its connection with the new
    /// value and pass Return's generation test straight into the stopped pool. The flag is the
    /// mechanism; a generation bump beside it would only look like one.
    /// </remarks>
    public void Dispose()
    {
        lock (_poolLock)
        {
            if (_stopped) return;
            _stopped = true;
        }

        _cleanupTimer.Dispose();

        var (destroyed, failed) = DestroyAllPooled();
        if (destroyed > 0)
            _logger.LogInformation("EXO pool: shut down; disposed {Count} pooled connection(s); {Failed} disconnect(s) failed", destroyed, failed);
    }
}
