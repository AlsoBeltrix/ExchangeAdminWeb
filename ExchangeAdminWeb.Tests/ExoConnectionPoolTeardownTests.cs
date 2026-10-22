using System.Collections;
using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a6c: the pool's teardown must own the connections it removes. A connection
/// taken out of the pool is disconnected, a failed disconnect is logged and counted, and a stopped
/// pool never takes a live connection back.
/// </summary>
/// <remarks>
/// Every runspace here is real and OPEN and carries a stub <c>Disconnect-ExchangeOnline</c> that
/// records the id it was asked to close - the A6B harness, extended so one connection's disconnect
/// can throw or write an error. PowerShell resolves the function ahead of the cmdlet, so the pool's
/// real call runs unmodified.
/// </remarks>
public partial class ExoConnectionPoolSharedConfigTests
{
    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    /// <summary>Keeps every log entry; optionally hands each message to a callback first.</summary>
    private sealed class RecordingLogger(Action<string>? onMessage = null) : ILogger<ExoConnectionPool>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Entries.Enqueue(new LogEntry(logLevel, message, exception));
            onMessage?.Invoke(message);
        }
    }

    /// <param name="behaviour">"ok", "throw" (a terminating error) or "error" (an error record).</param>
    private static PooledRunspace DisconnectStubRunspace(long generation, string connectionId, string behaviour, IList log)
    {
        var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        rs.SessionStateProxy.SetVariable("disconnectLog", log);
        rs.SessionStateProxy.SetVariable("disconnectBehaviour", behaviour);

        using (var init = PowerShell.Create())
        {
            init.Runspace = rs;
            init.AddScript("""
                function Disconnect-ExchangeOnline {
                    param($ConnectionId, $Confirm)
                    [void]$global:disconnectLog.Add($ConnectionId)
                    if ($global:disconnectBehaviour -eq 'throw') { throw "tenant blip closing $ConnectionId" }
                    if ($global:disconnectBehaviour -eq 'error') { Write-Error "disconnect refused for $ConnectionId" }
                }
                """);
            init.Invoke();
        }

        var ps = PowerShell.Create();
        ps.Runspace = rs;
        return new PooledRunspace(rs, ps, generation, connectionId);
    }

    /// <summary>A pool whose connects hand out the planned connections, in order.</summary>
    private ExoConnectionPool TeardownPool(IList log, ILogger<ExoConnectionPool>? logger, params (string Id, string Behaviour)[] planned)
    {
        var queue = new ConcurrentQueue<(string Id, string Behaviour)>(planned);
        return NewPool(logger: logger, connect: (_, generation, _) =>
        {
            Assert.True(queue.TryDequeue(out var next), "The pool connected more often than this test planned.");
            return DisconnectStubRunspace(generation, next.Id, next.Behaviour, log);
        });
    }

    private static async Task<List<PooledRunspace>> BorrowMany(ExoConnectionPool pool, int count)
    {
        var borrowed = new List<PooledRunspace>();
        for (var i = 0; i < count; i++)
            borrowed.Add(await pool.BorrowAsync(TestContext.Current.CancellationToken));
        return borrowed;
    }

    private static IList NewDisconnectLog() => ArrayList.Synchronized(new ArrayList());

    private static string[] Disconnected(IList log) => log.Cast<string>().Order(StringComparer.Ordinal).ToArray();

    private List<JsonElement> TraceSteps()
    {
        var steps = new List<JsonElement>();
        foreach (var file in Directory.GetFiles(Path.Combine(_dir, "ExchangeAdminWeb"), "*_trace*.jsonl"))
        {
            foreach (var line in File.ReadAllLines(file).Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                using var document = JsonDocument.Parse(line);
                steps.Add(document.RootElement.Clone());
            }
        }
        return steps;
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("error")]
    public async Task DisposeDisconnectsEveryPooledConnection_EvenWhenOneDisconnectFails(string failure)
    {
        var log = NewDisconnectLog();
        var pool = TeardownPool(log, null, ("conn-1", "ok"), ("conn-2", failure), ("conn-3", "ok"));
        foreach (var runspace in await BorrowMany(pool, 3))
            pool.Return(runspace);
        Assert.Equal(3, pool.AvailableCount);

        pool.Dispose();

        Assert.Equal(["conn-1", "conn-2", "conn-3"], Disconnected(log));
        Assert.Equal(0, pool.AvailableCount);
        Assert.Equal(1, pool.FailedDisconnects);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("error")]
    public async Task AFailedDisconnectIsLoggedWithTheConnectionItCouldNotClose(string failure)
    {
        var log = NewDisconnectLog();
        var logger = new RecordingLogger();
        using var pool = TeardownPool(log, logger, ("conn-bad", failure));
        var borrowed = await pool.BorrowAsync(TestContext.Current.CancellationToken);

        pool.Discard(borrowed);

        Assert.Equal(["conn-bad"], Disconnected(log));
        var warning = Assert.Single(logger.Entries,
            entry => entry.Message.Contains("Disconnect-ExchangeOnline failed", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("conn-bad", warning.Message, StringComparison.Ordinal);
        Assert.NotNull(warning.Exception);
        Assert.Equal(1, pool.FailedDisconnects);

        Assert.Contains(TraceSteps(), step =>
            step.TryGetProperty("stage", out var stage) && stage.GetString() == "ExoConnectionDisconnectFailed"
            && step.TryGetProperty("result", out var result) && result.GetString() == "Failed"
            && step.TryGetProperty("details", out var details)
            && details.TryGetProperty("connectionId", out var id) && id.GetString() == "conn-bad");
    }

    // Discard reaches the disconnect straight from a failed operation, with that operation's error
    // still in the stream. Counting it as a failed disconnect would make the new signal noise.
    [Fact]
    public async Task AnErrorLeftByTheBorrowersOwnCmdlet_IsNotMistakenForAFailedDisconnect()
    {
        var log = NewDisconnectLog();
        var logger = new RecordingLogger();
        using var pool = TeardownPool(log, logger, ("conn-1", "ok"));
        var borrowed = await pool.BorrowAsync(TestContext.Current.CancellationToken);
        borrowed.PowerShell.AddScript("Write-Error 'the borrower''s own cmdlet failed'");
        borrowed.PowerShell.Invoke();
        Assert.NotEmpty(borrowed.PowerShell.Streams.Error);

        pool.Discard(borrowed);

        Assert.Equal(["conn-1"], Disconnected(log));
        Assert.Equal(0, pool.FailedDisconnects);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task TheIdleSweepDisconnectsWhatItRetires_AndKeepsTheRest()
    {
        var log = NewDisconnectLog();
        using var pool = TeardownPool(log, null, ("conn-idle", "ok"), ("conn-busy", "ok"));
        var borrowed = await BorrowMany(pool, 2);
        foreach (var runspace in borrowed)
            pool.Return(runspace);
        borrowed[0].LastUsed = DateTime.UtcNow - TimeSpan.FromMinutes(21);

        pool.CleanupIdle(null);

        Assert.Equal(["conn-idle"], Disconnected(log));
        Assert.Equal(1, pool.AvailableCount);
        var next = await pool.BorrowAsync(TestContext.Current.CancellationToken);
        Assert.Same(borrowed[1], next);
        pool.Return(next);
    }

    // The finding's second gap, driven as the interleaving itself. The sweep takes everything out
    // and logs its retiring count BEFORE putting the keepers back, so holding it on that log call
    // runs Dispose while the sweep holds the keeper - Dispose drains an empty pool. The defect was
    // the put-back that followed: the keeper landed in the stopped pool, never reaped.
    [Fact]
    public async Task ASweepHoldingAConnectionWhenThePoolStops_DisconnectsItInsteadOfPuttingItBack()
    {
        using var sweepHolding = new SemaphoreSlim(0, 1);
        using var poolStopped = new SemaphoreSlim(0, 1);
        var heldTheWindow = false;
        var logger = new RecordingLogger(message =>
        {
            if (!message.Contains("idle sweep retiring", StringComparison.Ordinal)) return;
            sweepHolding.Release();
            heldTheWindow = poolStopped.Wait(TimeSpan.FromSeconds(30));
        });

        var log = NewDisconnectLog();
        using var pool = TeardownPool(log, logger, ("conn-idle", "ok"), ("conn-keep", "ok"));
        var borrowed = await BorrowMany(pool, 2);
        foreach (var runspace in borrowed)
            pool.Return(runspace);
        borrowed[0].LastUsed = DateTime.UtcNow - TimeSpan.FromMinutes(21);

        var ct = TestContext.Current.CancellationToken;
        var sweep = Task.Run(() => pool.CleanupIdle(null), ct);
        Assert.True(await sweepHolding.WaitAsync(TimeSpan.FromSeconds(30), ct),
            "The sweep never reached its log call - this test is stale.");

        Assert.True(pool.AvailableCount == 0,
            "The sweep must be holding both connections at its log call, or this test no longer runs inside the window.");
        try
        {
            pool.Dispose();
        }
        finally
        {
            poolStopped.Release();
        }
        await sweep;

        Assert.True(heldTheWindow,
            "The sweep's wait timed out, so Dispose did not run inside the window - this result proves nothing.");
        Assert.Equal(0, pool.AvailableCount);
        Assert.Equal(["conn-idle", "conn-keep"], Disconnected(log));
    }

    // The finding's first gap and its predicted failure: an app-pool recycle with an operation in
    // flight. The borrowed runspace is invisible to Dispose, still carries the current generation,
    // and used to be pooled on return into a pool whose cleanup had stopped.
    [Fact]
    public async Task AConnectionReturnedAfterThePoolStops_IsDisconnectedNotPooled()
    {
        var log = NewDisconnectLog();
        using var pool = TeardownPool(log, null, ("conn-late", "ok"));
        var borrowed = await pool.BorrowAsync(TestContext.Current.CancellationToken);

        pool.Dispose();
        Assert.Empty(log);

        pool.Return(borrowed);

        Assert.Equal(0, pool.AvailableCount);
        Assert.Equal(["conn-late"], Disconnected(log));
    }

    /// <summary>
    /// Holds every continuation posted to it until the test pumps, so a borrow can be stopped
    /// between its slot wait completing and the next line of <c>BorrowAsync</c> running.
    /// </summary>
    private sealed class HeldContinuations : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly SemaphoreSlim _posted = new(0);

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Enqueue((d, state));
            _posted.Release();
        }

        public Task<T> Start<T>(Func<Task<T>> start)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return start(); }
            finally { SetSynchronizationContext(previous); }
        }

        public async Task PumpUntil(Task task, CancellationToken ct)
        {
            while (!task.IsCompleted)
            {
                if (!await _posted.WaitAsync(TimeSpan.FromSeconds(10), ct))
                    throw new TimeoutException("The held borrow neither completed nor posted a continuation.");
                if (!_queue.TryDequeue(out var item))
                    throw new InvalidOperationException("Continuation signal without a queued callback.");
                var previous = Current;
                SetSynchronizationContext(this);
                try { item.Callback(item.State); }
                finally { SetSynchronizationContext(previous); }
            }
        }
    }

    // aud-a6d. A pooled runspace that is stale or idle-expired is taken out of the pool and then
    // destroyed through Task.Run - which, handed a token that is already cancelled, never runs the
    // destroy. The window is between the slot wait completing and that line, and this holds the
    // borrow's continuation exactly there while the runspace is aged and the token cancelled.
    [Fact]
    public async Task ABorrowCancelledAfterItsSlotWait_StillDisconnectsTheExpiredConnectionItTook()
    {
        var log = NewDisconnectLog();
        using var pool = TeardownPool(log, null,
            ("conn-1", "ok"), ("conn-2", "ok"), ("conn-3", "ok"), ("conn-4", "ok"), ("conn-5", "ok"));
        var held = await BorrowMany(pool, 5);

        using var cancellation = new CancellationTokenSource();
        var context = new HeldContinuations();
        var sixth = context.Start(() => pool.BorrowAsync(TimeSpan.FromSeconds(30), cancellation.Token));
        Assert.False(sixth.IsCompleted, "With every slot held, the sixth borrow must be waiting for one.");

        pool.Return(held[0]);
        held[0].LastUsed = DateTime.UtcNow - TimeSpan.FromMinutes(21);
        cancellation.Cancel();

        await context.PumpUntil(sixth, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sixth);
        Assert.Equal(["conn-1"], Disconnected(log));
        Assert.Equal(0, pool.AvailableCount);

        foreach (var runspace in held.Skip(1))
            pool.Return(runspace);
    }

    [Fact]
    public async Task ABorrowAfterThePoolStops_IsRefusedWithoutConnecting()
    {
        var connects = 0;
        var pool = NewPool(connect: (_, generation, _) =>
        {
            Interlocked.Increment(ref connects);
            var rs = RunspaceFactory.CreateRunspace();
            var ps = PowerShell.Create();
            ps.Runspace = rs;
            return new PooledRunspace(rs, ps, generation);
        });

        pool.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => pool.BorrowAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, connects);
    }

    // aud-a6e. A borrow that passed the entry check and then queued for a slot used to resume after
    // Dispose with nothing checking the stop again, and connect a fresh session from a stopped pool.
    // Two orderings: the pool stops while the borrow still waits, or after its slot was granted but
    // before it resumed (the continuation is held between the wait and its next line). Nothing frees
    // a slot after the first Return, so the seventh borrow can only finish if the refused sixth gave
    // its slot back.
    [Theory]
    [InlineData("stopped while queued")]
    [InlineData("stopped after the slot was granted")]
    public async Task ABorrowQueuedWhenThePoolStops_IsRefusedWithoutConnecting(string order)
    {
        var log = NewDisconnectLog();
        var connects = 0;
        using var pool = NewPool(connect: (_, generation, _) =>
            DisconnectStubRunspace(generation, $"conn-{Interlocked.Increment(ref connects)}", "ok", log));
        var held = await BorrowMany(pool, 5);

        var context = new HeldContinuations();
        var sixth = context.Start(() => pool.BorrowAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var seventh = context.Start(() => pool.BorrowAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.False(sixth.IsCompleted || seventh.IsCompleted, "With every slot held, both borrows must be waiting for one.");

        if (order == "stopped while queued")
        {
            pool.Dispose();
            pool.Return(held[0]);
        }
        else
        {
            pool.Return(held[0]);
            pool.Dispose();
        }

        await context.PumpUntil(sixth, TestContext.Current.CancellationToken);
        var sixthFailure = await Record.ExceptionAsync(() => sixth);
        Assert.Equal(5, Volatile.Read(ref connects));
        Assert.IsType<ObjectDisposedException>(sixthFailure);

        await context.PumpUntil(seventh, TestContext.Current.CancellationToken);
        Assert.IsType<ObjectDisposedException>(await Record.ExceptionAsync(() => seventh));
        Assert.Equal(5, Volatile.Read(ref connects));

        foreach (var runspace in held.Skip(1))
            pool.Return(runspace);
        Assert.Equal(0, pool.AvailableCount);
        Assert.Equal(["conn-1", "conn-2", "conn-3", "conn-4", "conn-5"], Disconnected(log));
    }
}
