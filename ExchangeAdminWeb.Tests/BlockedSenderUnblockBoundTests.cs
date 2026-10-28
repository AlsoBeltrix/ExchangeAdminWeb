using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.BlockedSenders;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-6. <see cref="BlockedSenderService.UnblockSenderAsync"/> took no cancellation token and had no
/// bound, so a stalled Exchange Online call held the Blocked Senders page's operation - its busy flag,
/// its activity - for as long as the call stalled.
/// </summary>
/// <remarks>
/// Driven through the real service on a real pool over the stub cmdlets (<see cref="MigrationStubExchange"/>):
/// Behaviour["UnblockStalls"] makes Remove-BlockedSenderAddress wait on a gate the test holds, after it has
/// logged the call - so the call is provably SENT and then stalls, which is the case that matters. The pool is
/// warmed with one ordinary unblock first, so the stalled call borrows a connected runspace at once and the
/// bound cannot run out during a connect instead. No Exchange. kd-76: a token cancelled before the call starts
/// nothing, and one cancelled before the pooled call claims the send makes that claim fail.
/// </remarks>
public sealed class BlockedSenderUnblockBoundTests : IDisposable
{
    private const string Sender = "spam@fabrikam.test";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Far longer than any healthy stub call; far shorter than the watchdog.</summary>
    private static readonly TimeSpan ShortBound = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    private readonly MigrationStubExchange _exchange = new();
    private readonly ManualResetEventSlim _stall = new();
    private readonly SignallingLogger _logger = new();

    public void Dispose()
    {
        // Opened first, so a stalled pipeline ends before its pool is disposed. The event is not disposed:
        // a pipeline thread may still be returning from its Wait, and a slim event holds no handle to free.
        _stall.Set();
        _exchange.Dispose();
    }

    private BlockedSenderService Service(ExoConnectionPool pool, TimeSpan? bound = null) =>
        new(pool, null!, _logger, _exchange.Trace) { UnblockBound = bound ?? ShortBound };

    /// <summary>A service whose pool already holds a connected runspace, then the stall switched on.</summary>
    private async Task<BlockedSenderService> WarmedServiceThatStalls(TimeSpan? bound = null)
    {
        var service = Service(_exchange.Pool(), bound);
        var warm = await service.UnblockSenderAsync("warm@fabrikam.test", "INC0000");
        Assert.True(warm.Success, warm.Message);
        _exchange.Calls.Clear();
        _exchange.Behaviour["UnblockStalls"] = _stall;
        return service;
    }

    private bool UnblockWasSent() =>
        _exchange.CallList().Any(call => call.StartsWith("Remove-BlockedSenderAddress", StringComparison.Ordinal));

    [Fact]
    public async Task AStalledUnblock_StopsWaitingAtTheBound()
    {
        var service = await WarmedServiceThatStalls();

        var call = service.UnblockSenderAsync(Sender, "INC0001", TestContext.Current.CancellationToken);
        var first = await Task.WhenAny(call, Task.Delay(Watchdog, TestContext.Current.CancellationToken));

        // Before kd-6 the caller waited exactly as long as Exchange Online did: here, until the gate opens.
        Assert.Same(call, first);

        // The warmed pool hands the call a connected runspace at once, so it is sent well inside the bound
        // and its outcome is unknown. On a host too loaded to start it inside the bound it is abandoned
        // unsent instead - then it must say nothing changed and must never send. The answer is read
        // first and then held to what the stub saw, so the check does not race the stub's own log line.
        BlockedSenderUnblockUnconfirmedException? unconfirmed = null;
        PermissionResult? result = null;
        try
        {
            result = await call;
        }
        catch (BlockedSenderUnblockUnconfirmedException ex)
        {
            unconfirmed = ex;
        }

        if (unconfirmed is not null)
        {
            Assert.Equal(Sender, unconfirmed.Address);
            Assert.Contains("could not be confirmed", unconfirmed.Message, StringComparison.Ordinal);
            Assert.IsType<TimeoutException>(unconfirmed.InnerException);
            await WaitUntil(UnblockWasSent); // claimed as sent, so the stub has it or is about to
        }
        else
        {
            Assert.False(result!.Success);
            Assert.Contains("nothing was changed", result.Message, StringComparison.Ordinal);
            Assert.False(UnblockWasSent(), "an unblock reported as never sent was sent");
            await _logger.Abandoned.Task.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
            Assert.False(UnblockWasSent(), "an unblock reported as never sent was sent afterwards");
        }
    }

    [Fact]
    public async Task ACallerWhoCancelsAfterTheUnblockWasSent_StopsWaiting_AndItsOutcomeIsUnconfirmed()
    {
        var service = await WarmedServiceThatStalls(bound: UnblockBoundLongerThanTheTest());
        using var cancel = new CancellationTokenSource();

        var call = service.UnblockSenderAsync(Sender, "INC0001", cancel.Token);
        await WaitUntil(UnblockWasSent);
        cancel.Cancel();

        Assert.Same(call, await Task.WhenAny(call, Task.Delay(Watchdog, TestContext.Current.CancellationToken)));
        var unconfirmed = await Assert.ThrowsAsync<BlockedSenderUnblockUnconfirmedException>(() => call);
        Assert.IsAssignableFrom<OperationCanceledException>(unconfirmed.InnerException);
    }

    [Fact]
    public async Task ACallAbandonedBeforeTheUnblockWasSent_SaysNothingChanged_AndNeverSendsIt()
    {
        using var connecting = new ManualResetEventSlim();
        using var connectGate = new ManualResetEventSlim();
        var service = Service(_exchange.Pool(beforeConnect: () =>
        {
            connecting.Set();
            connectGate.Wait(Watchdog);
        }), UnblockBoundLongerThanTheTest());
        using var cancel = new CancellationTokenSource();

        var call = service.UnblockSenderAsync(Sender, "INC0001", cancel.Token);
        Assert.True(connecting.Wait(Watchdog, TestContext.Current.CancellationToken), "the pool never began connecting");
        cancel.Cancel();

        // kd-57: the cancel is thrown as a cancel, in the app's phrase for one - it was a failed result, which the page filed
        // Failed. It still says nothing was changed.
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => call.WaitAsync(Watchdog, TestContext.Current.CancellationToken));
        Assert.StartsWith($"Cancelled by the operator before the unblock of {Sender} was sent", cancelled.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was changed", cancelled.Message, StringComparison.Ordinal);

        // The connect completes and the abandoned call runs - and must not send.
        connectGate.Set();
        await _logger.Abandoned.Task.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        Assert.False(UnblockWasSent(), "an unblock reported as never sent was sent afterwards");
    }

    [Fact]
    public async Task AnUnblockThatAnswersInTime_IsUnchanged()
    {
        var service = Service(_exchange.Pool());

        var result = await service.UnblockSenderAsync(Sender, "INC0001", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Unblocked sender {Sender}.", result.Message);
    }

    [Fact]
    public void TheBoundOutlastsThePoolsOwnAdmission()
    {
        // Two minutes for a free connection, then up to ninety seconds behind the connect gate: a bound
        // shorter than that would report a busy pool as a stall.
        Assert.True(BlockedSenderService.UnblockTimeout > TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(90));
    }

    // ---- kd-76: a cancel before the claim sends nothing ---------------------------------------------------------------

    [Fact]
    public async Task ACallWhoseTokenIsAlreadyCancelled_StartsNothing_AndIsACancel()
    {
        // kd-76, the finding: the page awaits its checks before the call, and a cancel made during them handed the service a
        // token already cancelled. It was seen only by the wait, after RunAsync had started; with a warm pool the call could
        // claim the send and unblock the sender first. Now nothing starts: the pool is never asked for a connection.
        var connects = 0;
        var service = Service(_exchange.Pool(beforeConnect: () => Interlocked.Increment(ref connects)), UnblockBoundLongerThanTheTest());
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.UnblockSenderAsync(Sender, "INC0001", cancel.Token).WaitAsync(Watchdog, TestContext.Current.CancellationToken));

        Assert.StartsWith($"Cancelled by the operator before the unblock of {Sender} was sent", cancelled.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was changed", cancelled.Message, StringComparison.Ordinal);
        Assert.Empty(PoolSlotRequests());
        Assert.Equal(0, Volatile.Read(ref connects));
        Assert.False(UnblockWasSent());
    }

    [Fact]
    public async Task ACancelThatReachesTheCallBeforeItClaimsTheSend_SendsNothing_AndIsACancel()
    {
        // kd-76, the guard. On a circuit the waiter resumes on the circuit's dispatcher, so the pooled call, on the thread
        // pool, can reach its guard before the waiter has seen the cancel: the guard looked only at whether the waiter had
        // given up, so the call claimed the send and unblocked the sender, and the cancel came back as an unknown outcome or
        // a success. The guard reads the token too: the claim fails and nothing is sent. The cancel lands while the pool
        // connects; the context holds the waiter's resumption, as a busy circuit would, until the call has ended.
        using var connecting = new ManualResetEventSlim();
        using var connectGate = new ManualResetEventSlim();
        var pool = _exchange.Pool(beforeConnect: () =>
        {
            connecting.Set();
            connectGate.Wait(Watchdog);
        });
        var service = Service(pool, UnblockBoundLongerThanTheTest());
        using var cancel = new CancellationTokenSource();
        var circuit = new CircuitLikeContext();

        var call = circuit.Start(() => service.UnblockSenderAsync(Sender, "INC0001", cancel.Token));
        Assert.True(connecting.Wait(Watchdog, TestContext.Current.CancellationToken), "the pool never began connecting");
        circuit.CancelHoldingWhatItResumes(cancel);
        Assert.Equal(1, circuit.Held);

        connectGate.Set();
        await WaitUntil(() => pool.AvailableCount == 1 && circuit.Idle);
        Assert.False(call.IsCompleted, "the waiter resumed before it was released");
        circuit.Release();

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => call.WaitAsync(Watchdog, TestContext.Current.CancellationToken));
        Assert.StartsWith($"Cancelled by the operator before the unblock of {Sender} was sent", cancelled.Message, StringComparison.Ordinal);
        Assert.False(UnblockWasSent(), "an unblock cancelled before the call claimed the send was sent");
    }

    [Fact]
    public async Task ACancelRacingTheClaim_EndsAsExactlyOneOfSentOrCancelled()
    {
        // kd-76. The cancel and the claim, raced: the cancel lands at a different point each time - before the claim, at it or
        // after it. Whichever wins, the outcome says what happened: cancelled, and then never sent; or sent, and then an
        // unblock that succeeded or whose outcome is unknown. Never both, and never neither.
        var pool = _exchange.Pool();
        var service = Service(pool, UnblockBoundLongerThanTheTest());
        var warm = await service.UnblockSenderAsync("warm@fabrikam.test", "INC0000", TestContext.Current.CancellationToken);
        Assert.True(warm.Success, warm.Message);
        var outcomes = new Dictionary<string, int>();

        for (var i = 0; i < 60; i++)
        {
            _exchange.Calls.Clear();
            using var cancel = new CancellationTokenSource();
            var call = service.UnblockSenderAsync(Sender, "INC0001", cancel.Token);
            Thread.SpinWait(i * 100);
            cancel.Cancel();

            string outcome;
            try
            {
                var result = await call.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
                Assert.True(result.Success, $"attempt {i}: neither sent nor cancelled - {result.Message}");
                outcome = "unblocked";
            }
            catch (BlockedSenderUnblockUnconfirmedException)
            {
                outcome = "unconfirmed";
            }
            catch (OperationCanceledException ex) when (ex.Message.StartsWith($"Cancelled by the operator before the unblock of {Sender} was sent", StringComparison.Ordinal))
            {
                outcome = "cancelled";
            }

            // The pooled call has ended once its connection is back: what it sent is then all it will send.
            await WaitUntil(() => pool.AvailableCount == 1);
            var sends = _exchange.CallList().Count(c => c.StartsWith("Remove-BlockedSenderAddress", StringComparison.Ordinal));
            Assert.True(sends == (outcome == "cancelled" ? 0 : 1), $"attempt {i}: {outcome}, and {sends} unblock(s) sent");
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
        }

        Assert.Equal(60, outcomes.Values.Sum());
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(", ", outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key} {o.Value}")));
    }

    [Fact]
    public void TheSubmissionGuard_LetsACancelBeforeTheClaimWin_AndKeepsACallThatSentAsSent()
    {
        // kd-76. The guard itself: a cancel before the claim fails the claim, and the waiter then hears nothing was sent; a
        // claim before the cancel stands, and a pool retry of that call - which runs only when the first attempt provably
        // sent nothing - may send, its outcome unknown to the waiter.
        using var before = new CancellationTokenSource();
        var cancelledFirst = new BlockedSenderService.UnblockSubmission(before.Token);
        before.Cancel();
        Assert.False(cancelledFirst.TrySend());
        Assert.True(cancelledFirst.IsAbandoned);
        Assert.True(cancelledFirst.TryAbandon());

        using var after = new CancellationTokenSource();
        var sentFirst = new BlockedSenderService.UnblockSubmission(after.Token);
        Assert.True(sentFirst.TrySend());
        after.Cancel();
        Assert.True(sentFirst.TrySend());
        Assert.False(sentFirst.IsAbandoned);
        Assert.False(sentFirst.TryAbandon());

        var abandoned = new BlockedSenderService.UnblockSubmission(CancellationToken.None);
        Assert.True(abandoned.TryAbandon());
        Assert.False(abandoned.TrySend());
    }

    // ---- the page: ConfirmUnblock files an unconfirmed unblock Undetermined, and the operator gets the page back ----

    [Fact]
    public async Task Page_AStalledUnblockTheOperatorWalksAwayFrom_EndsTheOperation_AndIsFiledUndetermined()
    {
        // The page hands the service its activity's token, so the navigation guard's OK (CancelAll) ends
        // the wait. The bound is set past the test, so it is the token - and only the token - that does.
        var service = await WarmedServiceThatStalls(bound: UnblockBoundLongerThanTheTest());
        var page = UnblockPage(service, out var progress);
        using var activities = progress;

        var confirm = (Task)typeof(Page).GetMethod("ConfirmUnblock", PrivateInstance)!.Invoke(page, null)!;
        await WaitUntil(UnblockWasSent);
        progress.CancelAll();

        Assert.Same(confirm, await Task.WhenAny(confirm, Task.Delay(Watchdog, TestContext.Current.CancellationToken)));
        await confirm;

        var row = Assert.Single(_exchange.AuditEvents(), r => r.GetProperty("action").GetString() == "UnblockSender");
        Assert.Equal("Undetermined", row.GetProperty("result").GetString());
        Assert.Equal(Sender, row.GetProperty("target").GetString());
        Assert.Contains("could not be confirmed", row.GetProperty("error").GetString(), StringComparison.Ordinal);

        Assert.False((bool)typeof(Page).GetField("isLoading", PrivateInstance)!.GetValue(page)!);
        var shown = (PermissionResult)typeof(Page).GetField("result", PrivateInstance)!.GetValue(page)!;
        Assert.False(shown.Success);
        Assert.Contains("could not be confirmed", shown.Message, StringComparison.Ordinal);
        Assert.Empty(progress.Active);
    }

    [Fact]
    public async Task Page_AnUnblockTheOperatorCancelsBeforeItWasSent_IsFiledCancelled()
    {
        // kd-57. The navigation guard's OK before the unblock was sent: nothing was changed. It came back a failed result and
        // was filed Failed; it is a cancel, filed Cancelled, in the app's phrase for one. A cancel after the send stays
        // Undetermined (the test above).
        using var connecting = new ManualResetEventSlim();
        using var connectGate = new ManualResetEventSlim();
        var service = Service(_exchange.Pool(beforeConnect: () =>
        {
            connecting.Set();
            connectGate.Wait(Watchdog);
        }), UnblockBoundLongerThanTheTest());
        var page = UnblockPage(service, out var progress);
        using var activities = progress;

        var confirm = (Task)typeof(Page).GetMethod("ConfirmUnblock", PrivateInstance)!.Invoke(page, null)!;
        Assert.True(connecting.Wait(Watchdog, TestContext.Current.CancellationToken), "the pool never began connecting");
        progress.CancelAll();

        Assert.Same(confirm, await Task.WhenAny(confirm, Task.Delay(Watchdog, TestContext.Current.CancellationToken)));
        await confirm;
        connectGate.Set();
        await _logger.Abandoned.Task.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        Assert.False(UnblockWasSent(), "an unblock filed as cancelled before it was sent was sent afterwards");

        var row = Assert.Single(_exchange.AuditEvents(), r => r.GetProperty("action").GetString() == "UnblockSender");
        Assert.Equal("Cancelled", row.GetProperty("result").GetString());
        var error = row.GetProperty("error").GetString();
        Assert.StartsWith($"Cancelled by the operator before the unblock of {Sender} was sent", error, StringComparison.Ordinal);
        Assert.Contains("nothing was changed", error, StringComparison.Ordinal);

        Assert.False((bool)typeof(Page).GetField("isLoading", PrivateInstance)!.GetValue(page)!);
        var shown = (PermissionResult)typeof(Page).GetField("result", PrivateInstance)!.GetValue(page)!;
        Assert.False(shown.Success);
        Assert.Equal(error, shown.Message);
        Assert.Empty(progress.Active);
    }

    [Fact]
    public async Task Page_ACancelDuringTheChecksBeforeTheUnblock_SendsNothing_AndIsFiledCancelled()
    {
        // kd-76 at the page, the reviewer's case: the navigation guard's OK while the page awaits its checks before the write -
        // here its permission re-check - so the token it hands the service is already cancelled. Nothing is started and
        // nothing sent; the row is filed Cancelled, in the service's sentence, as kd-57 files one.
        var service = Service(_exchange.Pool(), UnblockBoundLongerThanTheTest());
        var page = UnblockPage(service, out var progress, duringTheChecks: activities => activities.CancelAll());
        using var activities = progress;

        var confirm = (Task)typeof(Page).GetMethod("ConfirmUnblock", PrivateInstance)!.Invoke(page, null)!;
        await confirm.WaitAsync(Watchdog, TestContext.Current.CancellationToken);

        Assert.False(UnblockWasSent(), "an unblock cancelled during the checks before it was sent");
        Assert.Empty(PoolSlotRequests());
        var row = Assert.Single(_exchange.AuditEvents(), r => r.GetProperty("action").GetString() == "UnblockSender");
        Assert.Equal("Cancelled", row.GetProperty("result").GetString());
        var error = row.GetProperty("error").GetString();
        Assert.StartsWith($"Cancelled by the operator before the unblock of {Sender} was sent", error, StringComparison.Ordinal);
        Assert.Contains("nothing was changed", error, StringComparison.Ordinal);

        Assert.False((bool)typeof(Page).GetField("isLoading", PrivateInstance)!.GetValue(page)!);
        var shown = (PermissionResult)typeof(Page).GetField("result", PrivateInstance)!.GetValue(page)!;
        Assert.False(shown.Success);
        Assert.Equal(error, shown.Message);
        Assert.Empty(progress.Active);
    }

    /// <summary>The page with its confirm row open for <see cref="Sender"/>, its services set by hand.</summary>
    /// <param name="duringTheChecks">kd-76. Run inside the page's permission re-check, before the write: where a test cancels.</param>
    private Page UnblockPage(BlockedSenderService service, out ActivityProgressService progress,
        Action<ActivityProgressService>? duringTheChecks = null)
    {
        var page = new Page();
        var activities = new ActivityProgressService();
        progress = activities;
        var auth = Substitute.For<AuthenticationStateProvider>();
        auth.GetAuthenticationStateAsync().Returns(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "CONTOSO\\operator")], "test"))));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(_ =>
            {
                duringTheChecks?.Invoke(activities);
                return Task.FromResult(AuthorizationResult.Success());
            });

        Set(page, "BlockedSenderSvc", service);
        Set(page, "Progress", progress);
        Set(page, "ProtectionGate", NothingProtectedGate());
        Set(page, "Audit", _exchange.Audit);
        Set(page, "AuthStateProvider", auth);
        Set(page, "AuthorizationService", authorization);
        Set(page, "OperationTrace", _exchange.Trace);
        Set(page, "Logger", NullLogger<Page>.Instance);
        // Email stays unset: the page's admin notification is caught and logged when it cannot be sent.
        SetField(page, "currentUser", "CONTOSO\\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "confirmAddress", Sender);
        SetField(page, "ticketNumber", "INC0001");
        return page;
    }

    /// <summary>The real protection gate over empty protection stores: provably nothing to protect, so it allows.</summary>
    private BlockedSenderProtectionGate NothingProtectedGate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eaw-kd6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://secrets.invalid",
            ["Audit:LogRoot"] = directory,
        }).Build();
        var environment = _exchange.Environment;
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), environment,
            TestConfigStore.CreateModuleConfig(directory), NullLogger<ModuleConfigService>.Instance);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var delinea = new DelineaService(http, config, NullLogger<DelineaService>.Instance,
            new ExtendedLogService(config, environment, TestConfigStore.CreateAppSettings(directory), NullLogger<ExtendedLogService>.Instance),
            new OperationTraceService(config, log));
        var protectedPrincipals = new ProtectedPrincipalService(environment, config, moduleConfig,
            TestConfigStore.CreateProtectedPrincipal(directory), delinea, NullLogger<ProtectedPrincipalService>.Instance);
        var sectionAccess = new SectionAccessService(config, NullLogger<SectionAccessService>.Instance, environment,
            new ModuleCatalog(), new ExchangeAdminWeb.Services.Storage.SectionAccessRepository(TestConfigStore.Create(directory)));
        return new BlockedSenderProtectionGate(protectedPrincipals,
            new ProtectedPrincipalServicerService(sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance),
            NullLogger<BlockedSenderProtectionGate>.Instance);
    }

    private static void Set(Page page, string property, object? value) =>
        typeof(Page).GetProperty(property, PrivateInstance)!.SetValue(page, value);

    private static void SetField(Page page, string field, object? value) =>
        typeof(Page).GetField(field, PrivateInstance)!.SetValue(page, value);

    private static TimeSpan UnblockBoundLongerThanTheTest() => TimeSpan.FromMinutes(10);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Watchdog;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the condition");
            await Task.Delay(20);
        }
    }

    /// <summary>kd-76. Every slot the pool was asked for, from its own trace: none means no connection was asked for.</summary>
    private IReadOnlyList<System.Text.Json.JsonElement> PoolSlotRequests() =>
        _exchange.TraceEvents()
            .Where(e => e.TryGetProperty("stage", out var stage) && stage.GetString() == "ExoPoolSlotRequested")
            .ToList();

    /// <summary>
    /// kd-76. A synchronization context like a circuit's: what the service's awaits resume on is posted here, and runs at once
    /// on the thread pool - but for what the test's own thread posts while it cancels, which is the waiter's resumption, held
    /// until <see cref="Release"/>. So the pooled call can see the cancel first, as it can when a busy circuit's dispatcher
    /// has not yet run the waiter.
    /// </summary>
    private sealed class CircuitLikeContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _held = new();
        private int _holdingThread = -1;
        private int _queued;
        private int _running;

        public int Held => _held.Count;

        /// <summary>Nothing this context was given is waiting to run or running, the held callbacks aside.</summary>
        public bool Idle => Volatile.Read(ref _queued) == 0 && Volatile.Read(ref _running) == 0;

        public Task<T> Start<T>(Func<Task<T>> start)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return start(); }
            finally { SetSynchronizationContext(previous); }
        }

        public void CancelHoldingWhatItResumes(CancellationTokenSource cancel)
        {
            Volatile.Write(ref _holdingThread, Environment.CurrentManagedThreadId);
            try { cancel.Cancel(); }
            finally { Volatile.Write(ref _holdingThread, -1); }
        }

        public void Release()
        {
            while (_held.TryDequeue(out var item))
                Queue(item.Callback, item.State);
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (Volatile.Read(ref _holdingThread) == Environment.CurrentManagedThreadId)
                _held.Enqueue((d, state));
            else
                Queue(d, state);
        }

        private void Queue(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _queued);
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                Interlocked.Increment(ref _running);
                Interlocked.Decrement(ref _queued);
                var previous = Current;
                SetSynchronizationContext(this);
                try { d(state); }
                finally
                {
                    SetSynchronizationContext(previous);
                    Interlocked.Decrement(ref _running);
                }
            }, null);
        }
    }

    /// <summary>Signals when the service logs that a call it stopped waiting for has ended.</summary>
    private sealed class SignallingLogger : ILogger<BlockedSenderService>
    {
        public TaskCompletionSource<string> Abandoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (text.Contains("after its caller stopped waiting", StringComparison.Ordinal))
                Abandoned.TrySetResult(text);
        }
    }
}
