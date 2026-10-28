using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-58, slice A1 of docs/CancellationDepth-Plan.md: the Exchange Online pool takes the caller's cancel. The token
/// reaches the borrow - the wait for a slot and the wait at the connect gate end at once (E1) - and the retry core looks at it
/// before every attempt's borrow and as the borrow returns, giving an unused connection back (E2). A cancellation of the
/// caller's token is not a failure: never read as a dead session, never retried (3.5). No pipeline is ever stopped (E5), and
/// a pipeline found stopped anyway is destroyed by Return, never lent again (E6).
/// </summary>
/// <remarks>
/// The real pool over <see cref="ExchangeWriteHarness"/>'s settings and trace. Its connections are either stand-in runspaces
/// handed over by the connect seam - opened, with stand-in cmdlets that log each call by connection - or, where the connect
/// itself is under test, the pool's own connect step over a session of stand-in Import-Module, Get-ConnectionInformation,
/// Connect-ExchangeOnline and Disconnect-ExchangeOnline (the psl-9 seams). Slot accounting is checked after every case: all
/// five slots free, and five borrows granted at once. Every wait is bounded. No Exchange, no tenant.
/// </remarks>
public sealed class ExoConnectionPoolCancellationTests : IDisposable
{
    /// <summary>The connections <see cref="ExoConnectionPool"/> admits at once: its slots.</summary>
    private const int PoolConnections = 5;

    /// <summary>Far above any scheduling stall, and short enough that a test which will not finish says so.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>"At once": far inside the pool's two-minute slot wait and ninety-second gate wait.</summary>
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(10);

    private readonly ExchangeWriteHarness _h = new();
    private readonly List<ExoConnectionPool> _pools = [];

    // The stand-in connections' calls ("<connection>|<command>") and the ids Disconnect-ExchangeOnline was asked to close.
    private readonly ArrayList _calls = ArrayList.Synchronized(new ArrayList());
    private readonly ArrayList _disconnects = ArrayList.Synchronized(new ArrayList());
    private readonly ManualResetEventSlim _readEntered = new(false);
    private readonly ManualResetEventSlim _readGate = new(false);
    private int _connects;

    // The pool's own connect step: its stand-in session's calls, and the ids Get-ConnectionInformation reports.
    private readonly ArrayList _connectCalls = ArrayList.Synchronized(new ArrayList());
    private readonly ArrayList _connectIds = ArrayList.Synchronized(new ArrayList());
    private readonly ManualResetEventSlim _connectEntered = new(false);
    private readonly ManualResetEventSlim _connectGate = new(true);
    private int _thumbprints;

    public void Dispose()
    {
        _readGate.Set();
        _connectGate.Set();
        foreach (var pool in _pools)
            pool.Dispose();
        _h.Dispose();
        _readEntered.Dispose();
        _readGate.Dispose();
        _connectEntered.Dispose();
        _connectGate.Dispose();
    }

    private int Connects => Volatile.Read(ref _connects);

    private string[] Calls() => _calls.Cast<string>().ToArray();

    private string[] Disconnects() => _disconnects.Cast<string>().ToArray();

    // ---- E1: the borrow takes the token ------------------------------------------------------------------------------

    // (1) Every slot held, a sixth call with a token, cancelled while its borrow waits for a slot. Before kd-58 the borrow took
    // no token: it waited up to two minutes and then ran the call.
    [Fact]
    public async Task ACallCancelledWhileItsBorrowWaitsForASlot_EndsAtOnce_AsksForNoConnect_AndEverySlotComesBack()
    {
        var pool = StandInPool();
        var held = await HoldEveryConnection(pool);
        using var cancel = new CancellationTokenSource();
        var ran = false;
        var call = pool.RunWithRetryAsync(_ => { ran = true; return Task.FromResult(new PooledOutcome<string>("ran", false)); },
            allowRetry: true, PoolFailurePolicy.Return, cancel.Token);
        try
        {
            await WaitUntil(() => BorrowsWaitingForASlot() == 1, "the call's borrow waits for a slot");
            var clock = Stopwatch.StartNew();
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Promptly, TestContext.Current.CancellationToken));
            Assert.True(clock.Elapsed < Promptly, $"The cancel took {clock.Elapsed} to end the call.");
        }
        finally
        {
            ReturnAll(pool, held);
            await Record.ExceptionAsync(() => call.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        Assert.False(ran);
        Assert.Equal(PoolConnections, Connects); // the five held: the cancelled call asked for none
        Assert.Contains(PoolTrace(), step => step == ("ExoPoolSlotRequested", "Cancelled"));
        Assert.DoesNotContain(PoolTrace(), step => step.Result == "Failed");
        await AssertEverySlotIsFree(pool);
    }

    // (2) A connect in progress holds the connect gate - its Connect-ExchangeOnline waits on the test - and a second call,
    // cancelled while its borrow waits at the gate, ends at once, its slot given back.
    [Fact]
    public async Task ACallCancelledAtTheConnectGate_EndsAtOnce_AndItsSlotComesBack()
    {
        var pool = ConnectStepPool();
        var testToken = TestContext.Current.CancellationToken;
        _connectGate.Reset();
        var first = pool.BorrowAsync(testToken);
        Assert.True(_connectEntered.Wait(Bound, testToken), "The first connect never reached Connect-ExchangeOnline.");

        using var cancel = new CancellationTokenSource();
        var ran = false;
        var second = pool.RunWithRetryAsync(_ => { ran = true; return Task.FromResult(new PooledOutcome<string>("ran", false)); },
            allowRetry: false, PoolFailurePolicy.Return, cancel.Token);
        try
        {
            // The certificate is read right before the gate: once the second connect has read it, it waits there.
            await WaitUntil(() => Volatile.Read(ref _thumbprints) == 2, "the second connect reaches the gate");
            var clock = Stopwatch.StartNew();
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(Promptly, testToken));
            Assert.True(clock.Elapsed < Promptly, $"The cancel took {clock.Elapsed} to end the call.");
            Assert.Equal(PoolConnections - 1, pool.FreeSlots); // the cancelled borrow's slot is back; the first still holds one
        }
        finally
        {
            _connectGate.Set();
            pool.Return(await first.WaitAsync(Bound, testToken));
            await Record.ExceptionAsync(() => second.WaitAsync(Bound, testToken));
        }

        Assert.False(ran);
        Assert.Single(_connectCalls.Cast<string>(), call => call == "Connect-ExchangeOnline"); // the second never connected
        var trace = PoolTrace();
        Assert.Contains(("ExoConnectionCreated", "Cancelled"), trace);
        Assert.Contains(("ExoConnectionBorrowed", "Cancelled"), trace);
        Assert.DoesNotContain(trace, step => step.Result == "Failed");
        await AssertEverySlotIsFree(pool);
    }

    // (3) Cancelled during the connect proper, past the gate: the connect cannot be interrupted and runs to its end, and the
    // connection it made is healthy - so it is pooled for the next borrower, and the caller gets the cancellation, never the
    // connection, and nothing runs on it.
    [Fact]
    public async Task ACallCancelledDuringTheConnectItself_PoolsTheNewConnection_AndGetsTheCancellation()
    {
        var pool = ConnectStepPool();
        var testToken = TestContext.Current.CancellationToken;
        _connectGate.Reset();
        using var cancel = new CancellationTokenSource();
        var ran = false;
        var call = pool.RunWithRetryAsync(_ => { ran = true; return Task.FromResult(new PooledOutcome<string>("ran", false)); },
            allowRetry: true, PoolFailurePolicy.Return, cancel.Token);
        Assert.True(_connectEntered.Wait(Bound, testToken), "The connect never reached Connect-ExchangeOnline.");
        Assert.Equal(0, pool.AvailableCount);

        await cancel.CancelAsync();
        _connectGate.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Bound, testToken));
        Assert.False(ran);
        Assert.Equal(1, pool.AvailableCount);
        Assert.Contains(("ExoConnectionCreated", "Success"), PoolTrace());
        Assert.Contains(("ExoConnectionReturned", "Success"), PoolTrace());
        Assert.DoesNotContain(_connectCalls.Cast<string>(), c => c.StartsWith("Disconnect-ExchangeOnline", StringComparison.Ordinal));
        await AssertEverySlotIsFree(pool);
    }

    // (4) Cancelled as the borrow returns: the slot is granted to the waiting borrow - its next line held by the test's
    // context - and the cancel lands before it goes on. It comes back with a pooled connection; the call runs nothing on it
    // and gives it straight back.
    [Fact]
    public async Task ACallCancelledAsItsBorrowReturns_RunsNothing_AndGivesTheConnectionBack()
    {
        var pool = StandInPool();
        var held = await HoldEveryConnection(pool);
        using var cancel = new CancellationTokenSource();
        var ran = false;
        var context = new HeldContinuations();
        var call = context.Start(() => pool.RunWithRetryAsync(_ => { ran = true; return Task.FromResult(new PooledOutcome<string>("ran", false)); },
            allowRetry: true, PoolFailurePolicy.Return, cancel.Token));
        Assert.False(call.IsCompleted, "With every slot held, the call's borrow must be waiting for one.");

        ReturnOne(pool, held);
        await cancel.CancelAsync();
        await context.PumpUntil(call, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.False(ran);
        Assert.Equal(1, pool.AvailableCount);
        Assert.Equal(PoolConnections, Connects);
        ReturnAll(pool, held);
        await AssertEverySlotIsFree(pool);
    }

    // (5) A first attempt meets the pre-flight error that makes a retry eligible, and the caller cancels before the second:
    // no second borrow - the dead connection discarded once, the cancellation thrown. Thrown and returned, as a read and
    // RunAsync each report it.
    [Theory]
    [InlineData("thrown")]
    [InlineData("returned")]
    public async Task ACancelBetweenAttempts_StopsTheRetry_BeforeItsBorrow(string how)
    {
        using var cancel = new CancellationTokenSource();
        var core = new CountingPrimitives();

        var failure = await Record.ExceptionAsync(() => core.Run(_ =>
        {
            cancel.Cancel();
            return how == "thrown"
                ? throw DeadSession()
                : Task.FromResult(new PooledOutcome<string>("failed", ConnectionFailure: true, RetriablePrecheck: true));
        }, allowRetry: true, PoolFailurePolicy.Return, cancel.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(1, core.Borrows);
        Assert.Equal(1, core.Discards);
        Assert.Equal(0, core.Returns);
    }

    // (6) A borrow cancelled while the pool stops: whichever is seen first, the slot comes back exactly once and nothing is
    // pooled into the stopped pool.
    [Theory]
    [InlineData("cancelled while queued, then stopped")]
    [InlineData("granted, then stopped, then cancelled")]
    [InlineData("granted, then cancelled, then stopped")]
    public async Task ABorrowCancelledAsThePoolStops_GivesItsSlotBackOnce_AndPoolsNothing(string order)
    {
        var pool = StandInPool();
        var held = await HoldEveryConnection(pool);
        using var cancel = new CancellationTokenSource();
        var context = new HeldContinuations();
        var call = context.Start(() => pool.RunWithRetryAsync(_ => Task.FromResult(new PooledOutcome<string>("ran", false)),
            allowRetry: true, PoolFailurePolicy.Return, cancel.Token));
        Assert.False(call.IsCompleted, "With every slot held, the call's borrow must be waiting for one.");

        switch (order)
        {
            case "cancelled while queued, then stopped":
                await cancel.CancelAsync();
                pool.Dispose();
                break;
            case "granted, then stopped, then cancelled":
                ReturnOne(pool, held);
                pool.Dispose();
                await cancel.CancelAsync();
                break;
            default:
                ReturnOne(pool, held);
                await cancel.CancelAsync();
                pool.Dispose();
                break;
        }
        await context.PumpUntil(call, TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => call);
        Assert.True(failure is OperationCanceledException or ObjectDisposedException, $"Unexpected: {failure}");
        ReturnAll(pool, held);
        Assert.Equal(PoolConnections, pool.FreeSlots);
        Assert.Equal(0, pool.AvailableCount);
    }

    // ---- 3.5: the caller's cancel is not a failure, and nothing else is the caller's cancel --------------------------

    // The caller's cancel thrown by the delegate - between a read's commands, or by its own look before a first write - in
    // words that name a session: still the caller's cancel, so the connection goes back, never discarded as dead, never retried.
    [Fact]
    public async Task TheCallersCancelThrownByTheDelegate_InWordsThatNameASession_GivesTheConnectionBack()
    {
        using var cancel = new CancellationTokenSource();
        var core = new CountingPrimitives();
        var cancellation = new OperationCanceledException("the session read was cancelled", cancel.Token);

        var thrown = await Record.ExceptionAsync(() => core.Run(_ =>
        {
            cancel.Cancel();
            throw cancellation;
        }, allowRetry: true, PoolFailurePolicy.Return, cancel.Token));

        Assert.Same(cancellation, thrown);
        Assert.True(ExoConnectionPool.IsConnectionError(cancellation)); // the premise: by its words alone it would be discarded
        Assert.Equal(1, core.Borrows);
        Assert.Equal(1, core.Returns);
        Assert.Equal(0, core.Discards);
    }

    // A caller that cannot vouch for its pipeline keeps its policy for its own cancel too.
    [Fact]
    public async Task TheCallersCancel_UnderTheDiscardPolicy_Discards()
    {
        using var cancel = new CancellationTokenSource();
        var core = new CountingPrimitives();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => core.Run(_ =>
        {
            cancel.Cancel();
            cancel.Token.ThrowIfCancellationRequested();
            return Task.FromResult(new PooledOutcome<string>("unreached", false));
        }, allowRetry: true, PoolFailurePolicy.Discard, cancel.Token));

        Assert.Equal(1, core.Discards);
        Assert.Equal(0, core.Returns);
    }

    // E8, the control: a cancellation that is not of the caller's token - a cmdlet's own timeout - is sorted by its words as
    // before kd-58, whether or not the caller has cancelled: given back, or discarded when its words name a session.
    [Theory]
    [InlineData("a cmdlet's own timeout", false, false)]
    [InlineData("the session timed out", false, true)]
    [InlineData("the session timed out", true, true)]
    [InlineData("a task", true, false)]
    public async Task ACancellationThatIsNotTheCallers_IsSortedByItsWords_AsBefore(string words, bool callerCancelled, bool discarded)
    {
        using var cancel = new CancellationTokenSource();
        using var other = new CancellationTokenSource();
        var core = new CountingPrimitives();
        Exception foreign = words == "a task" ? new TaskCanceledException("a task was canceled") : new OperationCanceledException(words, other.Token);

        var thrown = await Record.ExceptionAsync(() => core.Run(_ =>
        {
            if (callerCancelled)
                cancel.Cancel();
            throw foreign;
        }, allowRetry: true, PoolFailurePolicy.Return, cancel.Token));

        Assert.Same(foreign, thrown);
        Assert.Equal(1, core.Borrows);
        Assert.Equal(discarded ? 1 : 0, core.Discards);
        Assert.Equal(discarded ? 0 : 1, core.Returns);
    }

    // ---- E6: a pipeline stopped mid-command is discarded, never lent again ---------------------------------------------

    /// <summary>
    /// What a delegate can make of a stop, and the direct borrow. Nothing in the app stops a pipeline (E5), so the test makes
    /// one: the stand-in read blocks inside a .NET call, the test asks for a stop from outside (BeginStop) and lets the call
    /// return, and the read stops at the next statement the stop reaches. On this host's PowerShell 7.6 SDK a stopped
    /// synchronous Invoke throws nothing: it returns no rows, HadErrors set and no error record written, and the pipeline's
    /// state reads Stopped (its reason a PipelineStoppedException). So a delegate meets it as the base's helpers present it -
    /// a throw from Invoke, an error flagged and none surfaced; no rows from ReadUnlessNotFound - or as no rows at all.
    /// </summary>
    public static TheoryData<string> Stops =>
    [
        "the base's Invoke throws it out of RunPooledQueryAsync",
        "a catch-all turns it into a result, as Recipient Lookup's read did",
        "RunAsync makes it a failed result",
        "ReadUnlessNotFound reads it as no such object",
        "it runs another command after the stop",
        "it reports a dead session the pool would retry",
        "it throws the pre-flight error after the stop",
        "it was borrowed directly, as the recipient autocomplete borrows",
    ];

    private static string StopOutcome(string shape) => shape switch
    {
        "the base's Invoke throws it out of RunPooledQueryAsync" => "InvalidOperationException: " + ExchangeServiceBase.ErrorNotSurfaced,
        "a catch-all turns it into a result, as Recipient Lookup's read did" => "error: " + ExchangeServiceBase.ErrorNotSurfaced,
        "RunAsync makes it a failed result" => "failed: " + ExchangeServiceBase.ErrorNotSurfaced,
        "it runs another command after the stop" => "rows: 0, then answer-conn-1",
        "it reports a dead session the pool would retry" => "a dead session reported",
        "it throws the pre-flight error after the stop" => "InvalidOperationException: " + DeadSession().Message,
        _ => "rows: 0",
    };

    // The proof that a stopped pipeline cannot poison the pool: in every shape, the connection is destroyed rather than pooled
    // - its disconnect seen once, ExoConnectionDiscardedStopped traced - the call is never retried, every slot comes back, and
    // the next call connects a fresh runspace and gets its own results only. Before kd-58, Return pooled any connection of the
    // current generation without looking at its pipeline, and the next call was handed the stopped one.
    [Theory]
    [MemberData(nameof(Stops))]
    public async Task AStoppedPipeline_IsDestroyedNotPooled_AndTheNextCallConnectsAFreshOne(string shape)
    {
        var pool = StandInPool();
        var testToken = TestContext.Current.CancellationToken;
        var service = new StoppedReadService(pool);
        var call = Task.Run(() => service.Read(shape, testToken), testToken);

        var ps = await service.Started.Task.WaitAsync(Bound, testToken);
        Assert.True(_readEntered.Wait(Bound, testToken), "The stand-in read never started.");
        _ = ps.BeginStop(null, null); // the test's stop, from outside: the only one there is
        _readGate.Set();
        var thrown = await Record.ExceptionAsync(() => call.WaitAsync(Bound, testToken));
        var outcome = thrown is null ? await call : $"{thrown.GetType().Name}: {thrown.Message}";

        // The premise: the read started and was stopped before its last statement, its pipeline reading Stopped after it.
        Assert.Contains("conn-1|Get-StandInRead", Calls());
        Assert.DoesNotContain("conn-1|Get-StandInRead finished", Calls());
        Assert.Equal(PSInvocationState.Stopped, service.StateAfterTheRead);
        Assert.Equal(StopOutcome(shape), outcome); // what the delegate made of it goes out as it is
        Assert.Equal(1, service.Attempts); // never retried

        Assert.Equal(0, pool.AvailableCount);
        Assert.Equal(["conn-1"], Disconnects());
        Assert.Single(PoolTrace(), step => step.Stage == "ExoConnectionDiscardedStopped");
        Assert.Equal(1, Connects);

        var next = await pool.RunWithRetryAsync(pooled => Task.Run(() =>
        {
            Assert.Empty(pooled.PowerShell.Commands.Commands);
            Assert.Empty(pooled.PowerShell.Streams.Error);
            pooled.PowerShell.AddCommand("Get-StandInAnswer").AddParameter("Name", "next");
            return new PooledOutcome<string[]>([.. pooled.PowerShell.Invoke().Select(row => row.ToString())], false);
        }), allowRetry: false, PoolFailurePolicy.Return, testToken);

        Assert.Equal(2, Connects); // a fresh runspace: the stopped one was not lent again
        Assert.Equal(["answer-conn-2"], next);
        await AssertEverySlotIsFree(pool);
    }

    // ---- E5: no pipeline is ever stopped -------------------------------------------------------------------------------

    /// <summary>
    /// The one file whose code stops a PowerShell pipeline, and why it is not an Exchange one: the section-access and
    /// operator-membership directory reads (PowerShellDirectoryCommands) run Active Directory cmdlets under the app's own
    /// identity, in a runspace of their own per call that is disposed after it - never a pooled Exchange Online connection,
    /// never an on-premises Exchange session - and stop their own read when their caller gives up. Outside kd-58.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> StopsOutsideExchange = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [Path.Combine("Services", "PowerShellDirectoryCommands.cs")] = "Active Directory reads in a runspace of their own",
    };

    // kd-58 E5 (kd-6's rule, widened): no command already sent is ever stopped - pooled or on-premises, read or write. A
    // stop could leave a write's outcome unknown and a shared connection in a state nobody can see. So no code under Services
    // or Components calls Stop, BeginStop, StopAsync or EndStop, outside the one directory runner named above.
    [Fact]
    public void NoExchangePipelineIsEverStopped()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        foreach (var folder in new[] { "Services", "Components" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)))
            {
                var relative = Path.GetRelativePath(root, file);
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var code = Regex.Replace(lines[i], @"//.*$", "");
                    if (StopCall.IsMatch(code) && !StopsOutsideExchange.ContainsKey(relative))
                        offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A pipeline must never be stopped (docs/CancellationDepth-Plan.md E5): a cancel lands between commands. Found: "
            + string.Join(" | ", offenders));
        // The exemption still describes a file that stops its own read, and that file holds no Exchange connection.
        var directory = File.ReadAllText(Path.Combine(root, "Services", "PowerShellDirectoryCommands.cs"));
        Assert.Matches(StopCall, directory);
        Assert.DoesNotContain("ExoConnectionPool", directory, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectOnPrem", directory, StringComparison.Ordinal);
    }

    private static readonly Regex StopCall = new(@"\.\s*(Stop|BeginStop|StopAsync|EndStop)\s*\(", RegexOptions.CultureInvariant);

    // ---- the pools -------------------------------------------------------------------------------------------------------

    /// <summary>A pool whose connect seam hands over an opened stand-in runspace per connection: conn-1, conn-2, ...</summary>
    private ExoConnectionPool StandInPool()
    {
        var pool = new ExoConnectionPool(_h.Config, _h.ModuleConfig, _h.Enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace,
            (_, generation, _) => StandIn(generation, $"conn-{Interlocked.Increment(ref _connects)}"));
        _pools.Add(pool);
        return pool;
    }

    /// <summary>
    /// A pool that runs its own connect step over a stand-in session (psl-9): Import-Module, Get-ConnectionInformation,
    /// Connect-ExchangeOnline - which waits on <see cref="_connectGate"/> once it has signalled <see cref="_connectEntered"/>
    /// - and Disconnect-ExchangeOnline. The certificate read just before the connect gate is counted.
    /// </summary>
    private ExoConnectionPool ConnectStepPool()
    {
        var pool = new ExoConnectionPool(_h.Config, _h.ModuleConfig, _h.Enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace)
        {
            ConnectRunspace = ConnectSession,
            CertificateThumbprint = _ =>
            {
                Interlocked.Increment(ref _thumbprints);
                return "0123456789ABCDEF";
            },
        };
        _pools.Add(pool);
        return pool;
    }

    private Runspace ConnectSession()
    {
        var state = InitialSessionState.CreateDefault2();
        state.Variables.Add(new SessionStateVariableEntry("ConnectCalls", _connectCalls, null));
        state.Variables.Add(new SessionStateVariableEntry("ConnectIds", _connectIds, null));
        state.Variables.Add(new SessionStateVariableEntry("ConnectEntered", _connectEntered, null));
        state.Variables.Add(new SessionStateVariableEntry("ConnectGate", _connectGate, null));
        state.Commands.Add(new SessionStateFunctionEntry("Import-Module", """
            [CmdletBinding()] param([string] $Name)
            [void]$ConnectCalls.Add("Import-Module $Name")
            """));
        state.Commands.Add(new SessionStateFunctionEntry("Get-ConnectionInformation", """
            [CmdletBinding()] param()
            foreach ($id in @($ConnectIds.ToArray())) { [pscustomobject]@{ ConnectionId = $id } }
            """));
        state.Commands.Add(new SessionStateFunctionEntry("Connect-ExchangeOnline", """
            [CmdletBinding()] param($AppId, $CertificateThumbprint, $Organization, $ShowBanner)
            [void]$ConnectCalls.Add('Connect-ExchangeOnline')
            [void]$ConnectEntered.Set()
            [void]$ConnectGate.Wait(60000)
            [void]$ConnectIds.Add('c-' + $ConnectIds.Count)
            """));
        state.Commands.Add(new SessionStateFunctionEntry("Disconnect-ExchangeOnline", """
            [CmdletBinding()] param($ConnectionId, $Confirm)
            [void]$ConnectCalls.Add("Disconnect-ExchangeOnline $ConnectionId")
            """));
        return RunspaceFactory.CreateRunspace(state);
    }

    /// <summary>
    /// An opened runspace of stand-in cmdlets for one connection. Get-StandInRead logs its call, says it has started, waits on
    /// the test's gate inside a .NET call - where no stop can reach it - and then waits, up to ten seconds, at statements a
    /// stop can land at, logging "finished" only if none did; Get-StandInAnswer answers with its connection's name;
    /// Disconnect-ExchangeOnline logs the id it was asked to close.
    /// </summary>
    private PooledRunspace StandIn(long generation, string connectionId)
    {
        var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        rs.SessionStateProxy.SetVariable("calls", _calls);
        rs.SessionStateProxy.SetVariable("disconnects", _disconnects);
        rs.SessionStateProxy.SetVariable("connectionId", connectionId);
        rs.SessionStateProxy.SetVariable("readEntered", _readEntered);
        rs.SessionStateProxy.SetVariable("readGate", _readGate);
        using (var init = PowerShell.Create())
        {
            init.Runspace = rs;
            init.AddScript("""
                function global:Get-StandInRead {
                    [CmdletBinding()] param()
                    [void]$global:calls.Add("$global:connectionId|Get-StandInRead")
                    [void]$global:readEntered.Set()
                    [void]$global:readGate.Wait(60000)
                    # A stop asked for while the gate held this read reaches the pipeline asynchronously (BeginStop queues it),
                    # so the read goes on to statements it can be stopped at and waits there - bounded - rather than race it.
                    for ($i = 0; $i -lt 500; $i++) { Start-Sleep -Milliseconds 20 }
                    "read-$global:connectionId"
                    [void]$global:calls.Add("$global:connectionId|Get-StandInRead finished")
                }
                function global:Get-StandInAnswer {
                    [CmdletBinding()] param([string] $Name)
                    [void]$global:calls.Add("$global:connectionId|Get-StandInAnswer $Name")
                    "answer-$global:connectionId"
                }
                function global:Disconnect-ExchangeOnline {
                    param($ConnectionId, $Confirm)
                    [void]$global:disconnects.Add($ConnectionId)
                }
                """);
            init.Invoke();
            Assert.False(init.HadErrors, "The stand-in cmdlets could not be defined.");
        }

        var ps = PowerShell.Create();
        ps.Runspace = rs;
        return new PooledRunspace(rs, ps, generation, connectionId);
    }

    /// <summary>
    /// The stand-in read through the base's own helpers and the pool, in each shape of <see cref="Stops"/>. Every attempt is
    /// counted and its pipeline handed to the test before it runs; the pipeline's state is noted as the read returns.
    /// </summary>
    private sealed class StoppedReadService(ExoConnectionPool pool) : ExchangeServiceBase(pool, null!, NullLogger.Instance, "")
    {
        private int _attempts;

        public TaskCompletionSource<PowerShell> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PSInvocationState? StateAfterTheRead { get; private set; }

        public int Attempts => Volatile.Read(ref _attempts);

        public async Task<string> Read(string shape, CancellationToken cancellationToken)
        {
            switch (shape)
            {
                case "the base's Invoke throws it out of RunPooledQueryAsync":
                    return await RunPooledQueryAsync((ps, tracker) => $"rows: {TheRead(ps, p => Invoke(p, tracker).Count)}", allowRetry: true);
                case "a catch-all turns it into a result, as Recipient Lookup's read did":
                    return await RunPooledQueryAsync((ps, tracker) =>
                    {
                        try
                        {
                            return $"rows: {TheRead(ps, p => Invoke(p, tracker).Count)}";
                        }
                        catch (Exception ex)
                        {
                            return "error: " + ex.Message;
                        }
                    }, allowRetry: true);
                case "RunAsync makes it a failed result":
                    var result = await RunAsync((ps, tracker) => TheRead(ps, p => Invoke(p, tracker).Count), allowRetry: true);
                    return result.Success ? "succeeded" : "failed: " + result.Message;
                case "ReadUnlessNotFound reads it as no such object":
                    return await RunPooledQueryAsync((ps, tracker) => $"rows: {TheRead(ps, p => ReadUnlessNotFound(p, tracker).Count)}", allowRetry: true);
                case "it runs another command after the stop":
                    return await RunPooledQueryAsync((ps, _) =>
                    {
                        var rows = TheRead(ps, p => p.Invoke().Count);
                        ps.Commands.Clear();
                        ps.AddCommand("Get-StandInAnswer").AddParameter("Name", "after the stop");
                        return $"rows: {rows}, then {string.Join(",", ps.Invoke())}";
                    }, allowRetry: true);
                case "it reports a dead session the pool would retry":
                    return await _exoPool.RunWithRetryAsync(pooled => Task.Run(() =>
                    {
                        TheRead(pooled.PowerShell, p => p.Invoke().Count);
                        return new PooledOutcome<string>("a dead session reported", ConnectionFailure: true, RetriablePrecheck: true);
                    }), allowRetry: true, PoolFailurePolicy.Return, cancellationToken);
                case "it throws the pre-flight error after the stop":
                    return await RunPooledQueryAsync<string>((ps, _) =>
                    {
                        TheRead(ps, p => p.Invoke().Count);
                        throw DeadSession();
                    }, allowRetry: true);
                default:
                    var pooled = await _exoPool.BorrowAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    try
                    {
                        return $"rows: {TheRead(pooled.PowerShell, p => p.Invoke().Count)}";
                    }
                    finally
                    {
                        _exoPool.Return(pooled);
                    }
            }
        }

        private int TheRead(PowerShell ps, Func<PowerShell, int> read)
        {
            Interlocked.Increment(ref _attempts);
            Started.TrySetResult(ps);
            ps.AddCommand("Get-StandInRead");
            try
            {
                return read(ps);
            }
            finally
            {
                StateAfterTheRead ??= ps.InvocationStateInfo.State;
            }
        }
    }

    private static async Task<List<PooledRunspace>> HoldEveryConnection(ExoConnectionPool pool)
    {
        var held = new List<PooledRunspace>();
        for (var i = 0; i < PoolConnections; i++)
            held.Add(await pool.BorrowAsync(Promptly, TestContext.Current.CancellationToken));
        return held;
    }

    private static void ReturnOne(ExoConnectionPool pool, List<PooledRunspace> held)
    {
        pool.Return(held[0]);
        held.RemoveAt(0);
    }

    private static void ReturnAll(ExoConnectionPool pool, List<PooledRunspace> held)
    {
        foreach (var runspace in held)
            pool.Return(runspace);
        held.Clear();
    }

    /// <summary>
    /// The slot accounting checked after every case: with nothing borrowed every slot is free, and five borrows are granted at
    /// once - a slot leaked would hold the fifth, a slot given back twice would have thrown at its release.
    /// </summary>
    private static async Task AssertEverySlotIsFree(ExoConnectionPool pool)
    {
        Assert.Equal(PoolConnections, pool.FreeSlots);
        var held = new List<PooledRunspace>();
        try
        {
            for (var i = 0; i < PoolConnections; i++)
                held.Add(await pool.BorrowAsync(Promptly, TestContext.Current.CancellationToken));
            Assert.Equal(0, pool.FreeSlots);
        }
        finally
        {
            ReturnAll(pool, held);
        }
        Assert.Equal(PoolConnections, pool.FreeSlots);
    }

    // The narrow pre-flight signature: a dead session found before any cmdlet ran, which makes a retry eligible.
    private static InvalidOperationException DeadSession() =>
        new("You must call Connect-ExchangeOnline before calling any other cmdlet.");

    /// <summary>The retry core over borrow, return and discard primitives that only count, on real (unopened) runspaces.</summary>
    private sealed class CountingPrimitives
    {
        public int Borrows;
        public int Returns;
        public int Discards;

        public Task<string> Run(Func<PooledRunspace, Task<PooledOutcome<string>>> run, bool allowRetry, PoolFailurePolicy policy,
            CancellationToken cancellationToken) =>
            ExoConnectionPool.RunWithRetryCoreAsync(
                () =>
                {
                    Borrows++;
                    return Task.FromResult(new PooledRunspace(RunspaceFactory.CreateRunspace(), PowerShell.Create()));
                },
                _ => Returns++,
                _ => Discards++,
                run, ExoConnectionPool.IsConnectionError, ExoConnectionPool.IsRetriablePrecheckError, allowRetry, policy,
                cancellationToken);
    }

    // ---- the pool's own trace ----------------------------------------------------------------------------------------

    /// <summary>
    /// The pool's trace, oldest first: each step's stage and result. Read while a borrow waits, so a line still being written
    /// can be incomplete; it is skipped and read whole the next time.
    /// </summary>
    private List<(string? Stage, string? Result)> PoolTrace()
    {
        var steps = new List<(string? Stage, string? Result)>();
        foreach (var path in _h.Jsonl.GetTraceLogPaths(DateTime.Now).Where(File.Exists))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var step = JsonDocument.Parse(line);
                    steps.Add((step.RootElement.TryGetProperty("stage", out var s) ? s.GetString() : null,
                        step.RootElement.TryGetProperty("result", out var r) ? r.GetString() : null));
                }
                catch (JsonException)
                {
                    continue;
                }
            }
        }
        return steps;
    }

    /// <summary>How many borrows wait for a slot: asked for, and neither granted nor ended - refused busy or cancelled.</summary>
    private int BorrowsWaitingForASlot()
    {
        var trace = PoolTrace();
        var asked = trace.Count(s => s.Stage == "ExoPoolSlotRequested" && s.Result == "Success");
        var ended = trace.Count(s => s.Stage == "ExoPoolSlotRequested" && s.Result is "Failed" or "Cancelled");
        var granted = trace.Count(s => s.Stage == "ExoPoolSlotAcquired");
        return asked - granted - ended;
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static string RepoRoot() =>
        Path.GetDirectoryName(AuditCategoryFilingTests.FindRepoFile("ExchangeAdminWeb.csproj"))!;

    /// <summary>
    /// Holds every continuation posted to it until the test pumps, so a borrow can be held between its slot being granted and
    /// its next line (the aud-a6d technique, ExoConnectionPoolTeardownTests).
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
                    throw new TimeoutException("The held call neither completed nor posted a continuation.");
                if (!_queue.TryDequeue(out var item))
                    throw new InvalidOperationException("Continuation signal without a queued callback.");
                var previous = Current;
                SetSynchronizationContext(this);
                try { item.Callback(item.State); }
                finally { SetSynchronizationContext(previous); }
            }
        }
    }
}
