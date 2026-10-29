using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using Page = ExchangeAdminWeb.Components.Pages.RecipientLookup;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-58, slice A3 of docs/CancellationDepth-Plan.md: the Exchange base's on-premises steps take the caller's cancel
/// (E7). The wait for one of the on-premises server's two slots (<c>ThrottledAsync</c>, up to two minutes) ends at once; the
/// token is looked at before the session's runspace opens and once it has (<c>OpenOnPremRunspace</c>); <c>ConnectOnPrem</c>
/// looks at it before each attempt and waits on it, not <c>Thread.Sleep</c>, between attempts; and each read is preceded by a
/// look. A command already sent - an attempt's New-PSSession, a read - runs to its end, and the session is still removed. The
/// base's two on-premises reads take it, and so does Recipient Lookup's own, built from the same surface.
/// </summary>
/// <remarks>
/// The real Recipient Lookup service over <see cref="MigrationStubExchange"/>: Exchange Online in its pool, on-premises
/// Exchange in the service's seams - its ConnectOnPrem defines the on-premises stand-ins in the service's own runspace, and
/// its runspace seam lets a test watch that runspace being made. The real ConnectOnPrem is driven over a local runspace of
/// stand-in New-PSSessionOption and New-PSSession. The throttle is the base's own, process-wide, so a test that holds its two
/// slots holds them only until the read it waits on has ended. Every wait bounded. No Exchange, no vault, no directory.
/// </remarks>
[Collection(OnPremThrottleCollection.Name)] // kd-58 A6: it holds the throttle's two slots, as PermissionOnPremChangeCancellationTests does
public sealed class ExchangeServiceBaseOnPremCancellationTests : IDisposable
{
    private const string Address = "alice@contoso.test";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>"At once": far inside the throttle's two-minute wait.</summary>
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(10);

    private readonly MigrationStubExchange _exchange = new() { OnPremConfigured = true };

    public ExchangeServiceBaseOnPremCancellationTests()
    {
        // Exchange Online holds no mailbox at the address and reports it as a mail user synced from on-premises, so the
        // shared location read and Recipient Lookup both ask on-premises Exchange, which holds the mailbox.
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "MailUser"), ("PrimarySmtpAddress", Address), ("IsDirSynced", true));
        _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));
    }

    public void Dispose() => _exchange.Dispose();

    /// <summary>
    /// The three on-premises reads: the base's size read, the base's location read (through the shared location read, as the
    /// permission pages call it) and Recipient Lookup's own (through the lookup).
    /// </summary>
    public static TheoryData<string> Reads => ["the base's size read", "the base's location read", "Recipient Lookup's own read"];

    // ---- the throttle --------------------------------------------------------------------------------------------------

    // Both of the on-premises server's slots held, a read cancelled while it waits for one ends at once - its runspace never
    // made, no session opened. Before, the throttle took no token and the read waited up to two minutes, then ran.
    [Theory]
    [MemberData(nameof(Reads))]
    public async Task AReadCancelledWhileItWaitsForTheOnPremServer_EndsAtOnce_OpeningNothing(string read)
    {
        var service = Service();
        using var cancel = new CancellationTokenSource();
        await HoldBothOnPremSlots();
        var reading = Task.CompletedTask;
        try
        {
            service.CredentialsFetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reading = Run(service, read, cancel.Token);
            await service.CredentialsFetched.Task.WaitAsync(Bound, TestContext.Current.CancellationToken); // next: the throttle
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(Promptly, TestContext.Current.CancellationToken));
        }
        finally
        {
            ReleaseBothOnPremSlots();
            await Record.ExceptionAsync(() => reading.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, service.RunspacesMade);
        Assert.Equal(0, _exchange.OnPremSessions);
    }

    // ---- the runspace --------------------------------------------------------------------------------------------------

    // The read is admitted by the throttle, and the cancel lands before its body runs - its continuation held by the test's
    // context, as on a busy circuit: the look before the runspace opens ends it with no runspace made.
    [Fact]
    public async Task ASizeReadCancelledAsTheThrottleAdmitsIt_MakesNoRunspace()
    {
        var service = Service();
        using var cancel = new CancellationTokenSource();
        await HoldBothOnPremSlots();
        var released = 0;
        try
        {
            var context = new HeldContinuations();
            var reading = context.Start(() => service.SizeAsync(Address, cancel.Token));
            Assert.False(reading.IsCompleted, "With both slots held, the read must be waiting for one.");

            OnPremReads.Throttle.Release();
            released++;
            await cancel.CancelAsync();
            await context.PumpUntil(reading, TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        }
        finally
        {
            OnPremReads.Throttle.Release(2 - released);
        }

        Assert.Equal(0, service.RunspacesMade);
        Assert.Equal(0, _exchange.OnPremSessions);
    }

    // The cancel lands as the runspace opens - made by the seam, then opened: the look once it has opened ends the read before
    // the connect. Every read.
    [Theory]
    [MemberData(nameof(Reads))]
    public async Task AReadCancelledAsItsRunspaceOpens_NeverConnects(string read)
    {
        var service = Service();
        using var cancel = new CancellationTokenSource();
        service.AsTheRunspaceIsMade = cancel.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(service, read, cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken));

        Assert.Equal(1, service.RunspacesMade);
        Assert.Equal(0, _exchange.OnPremSessions);
        Assert.Equal(0, service.Connects);
    }

    // ---- the connect ---------------------------------------------------------------------------------------------------

    // Each read hands its token to ConnectOnPrem, which looks at it before each attempt and between attempts.
    [Theory]
    [MemberData(nameof(Reads))]
    public async Task EveryReadHandsItsTokenToTheConnect(string read)
    {
        var service = Service();
        using var cancel = new CancellationTokenSource();

        await Run(service, read, cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Recipient Lookup connects twice - its own read, then the base's size read for the on-premises mailbox it found.
        Assert.NotEmpty(service.ConnectTokens);
        Assert.All(service.ConnectTokens, token => Assert.Equal(cancel.Token, token));
    }

    // The real ConnectOnPrem, cancelled before an attempt: nothing is sent.
    [Fact]
    public void AConnectCancelledBeforeAnAttempt_SendsNothing()
    {
        var service = Service();
        using var session = new ConnectSession(failures: 0);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => service.RealConnect(session.PowerShell, cancel.Token));

        Assert.Empty(session.Calls);
    }

    // The real ConnectOnPrem's first attempt fails, and the cancel lands during the pause before the second: it ends there,
    // well before the pause would have (two seconds after the first attempt), and the second attempt is never sent. Before,
    // the pause was a Thread.Sleep the token could not reach.
    [Fact]
    public void AConnectCancelledDuringThePauseBetweenAttempts_EndsBeforeTheNext()
    {
        var service = Service();
        using var session = new ConnectSession(failures: 1);
        using var cancel = new CancellationTokenSource();
        session.CancelAfterTheFirstFailure = cancel;
        var clock = Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() => service.RealConnect(session.PowerShell, cancel.Token));

        Assert.Equal(["New-PSSessionOption", "New-PSSession"], session.Calls.Cast<string>().ToArray()); // one attempt, never a second
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(1500), $"The cancel took {clock.Elapsed} to end the pause.");
    }

    // The control: with no cancel, the pause is waited out and the second attempt connects, as before.
    [Fact]
    public void AConnectWithNoCancel_RetriesAfterThePause_AsBefore()
    {
        var service = Service();
        using var session = new ConnectSession(failures: 1);

        service.RealConnect(session.PowerShell, CancellationToken.None);

        Assert.Equal(["New-PSSessionOption", "New-PSSession", "New-PSSessionOption", "New-PSSession"], session.Calls.Cast<string>().ToArray());
        Assert.Equal("stand-in-remote-session", session.PowerShell.Runspace.SessionStateProxy.GetVariable("onpremSession")?.ToString());
    }

    // ---- between the reads ---------------------------------------------------------------------------------------------

    // Cancelled as the session opens - the connect has run to its end - each read ends before its first command is sent.
    [Theory]
    [MemberData(nameof(Reads))]
    public async Task AReadCancelledAsItsSessionOpens_SendsNoRead(string read)
    {
        var service = Service();
        using var cancel = new CancellationTokenSource();
        _exchange.Behaviour["Cancel OnPrem Connect"] = cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(service, read, cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken));

        Assert.Equal(1, _exchange.OnPremSessions);
        Assert.DoesNotContain(_exchange.CallList(), call => call.StartsWith("OnPrem Get-", StringComparison.Ordinal));
    }

    // Cancelled while a read is in flight, each read with a second read ends before the second - the first ran to its end.
    [Theory]
    [InlineData("the base's size read", "OnPrem Get-MailboxStatistics", "OnPrem Get-MailboxStatistics -Archive")]
    [InlineData("the base's location read", "OnPrem Get-Mailbox", "OnPrem Get-RemoteMailbox")]
    public async Task AReadCancelledBetweenItsReads_SendsNoMore(string read, string first, string second)
    {
        _exchange.OnPremMailboxes.Remove(Address); // the location read's first read finds nothing, so it would ask the second
        var service = Service();
        using var cancel = new CancellationTokenSource();
        _exchange.Behaviour[$"Cancel {first}"] = cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(service, read, cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken));

        Assert.Contains(_exchange.CallList(), call => call.StartsWith(first + " ", StringComparison.Ordinal));
        Assert.DoesNotContain(_exchange.CallList(), call => call.StartsWith(second + " ", StringComparison.Ordinal));
    }

    // ---- the page ------------------------------------------------------------------------------------------------------

    // Recipient Lookup's page: a lookup cancelled while its on-premises read waits for the server ends at once, filed
    // Cancelled in kd-57's words.
    [Fact]
    public async Task ALookupCancelledWhileItsOnPremReadWaitsForTheServer_IsFiledCancelled_AtOnce()
    {
        var service = Service();
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var page = new Page();
        Inject(page, "LookupService", service);
        Inject(page, "Audit", _exchange.Audit);
        Inject(page, "Logger", NullLogger<Page>.Instance);
        Inject(page, "Progress", progress);
        PageField("emailAddress").SetValue(page, Address);
        PageField("currentUser").SetValue(page, "CONTOSO\\operator");
        await HoldBothOnPremSlots();
        Task? run = null;
        try
        {
            service.CredentialsFetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            run = (Task)typeof(Page).GetMethod("RunLookup", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [])!;
            await service.CredentialsFetched.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            await cancel.CancelAsync();

            await run.WaitAsync(Promptly, TestContext.Current.CancellationToken); // both slots still held
        }
        finally
        {
            ReleaseBothOnPremSlots();
            if (run is not null)
                await Record.ExceptionAsync(() => run.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        var shown = Assert.IsType<RecipientInfoResult>(PageField("result").GetValue(page));
        Assert.Equal("Cancelled by the operator", shown.Error);
        Assert.Equal((false, shown.Error), Assert.Single(progress.Handles).Outcome);
        var row = Assert.Single(_exchange.AuditEvents());
        Assert.Equal("Cancelled", row.GetProperty("result").GetString());
        Assert.Equal(0, _exchange.OnPremSessions);
    }

    // ---- the service ---------------------------------------------------------------------------------------------------

    private OnPremReads Service() => new(_exchange);

    private static Task Run(OnPremReads service, string read, CancellationToken cancellationToken) => read switch
    {
        "the base's size read" => service.SizeAsync(Address, cancellationToken),
        "the base's location read" => service.GetMailboxLocationAsync(Address, cancellationToken),
        _ => service.GetRecipientInfoAsync(Address, cancellationToken),
    };

    private static async Task HoldBothOnPremSlots()
    {
        Assert.True(await OnPremReads.Throttle.WaitAsync(Bound, TestContext.Current.CancellationToken), "The first on-premises slot never came free.");
        if (!await OnPremReads.Throttle.WaitAsync(Bound, TestContext.Current.CancellationToken))
        {
            OnPremReads.Throttle.Release();
            Assert.Fail("The second on-premises slot never came free.");
        }
    }

    private static void ReleaseBothOnPremSlots() => OnPremReads.Throttle.Release(2);

    /// <summary>The sizes the base's size read returns, as this test reads them.</summary>
    internal sealed record OnPremSizes(double MailboxGB, string? MailboxUnreadable);

    /// <summary>
    /// The real Recipient Lookup service over the stand-ins, its seams watched: the runspace an on-premises read opens (made,
    /// counted, and <see cref="AsTheRunspaceIsMade"/> run as it is made), the connect (the token it was given, then the
    /// stand-in session), and the credential fetch (answered at once, <see cref="CredentialsFetched"/> signalled).
    /// </summary>
    internal sealed class OnPremReads(MigrationStubExchange exchange)
        : RecipientLookupService(exchange.Pool(), null!, NullLogger<RecipientLookupService>.Instance, OnPremServer(), null!, exchange.Trace)
    {
        private int _runspacesMade;
        private int _connects;

        public static SemaphoreSlim Throttle => _onPremThrottle;

        public ConcurrentQueue<CancellationToken> ConnectTokens { get; } = new();

        public Action? AsTheRunspaceIsMade { get; set; }

        public TaskCompletionSource? CredentialsFetched { get; set; }

        public int RunspacesMade => Volatile.Read(ref _runspacesMade);

        public int Connects => Volatile.Read(ref _connects);

        public async Task<OnPremSizes> SizeAsync(string address, CancellationToken cancellationToken)
        {
            var sizes = await GetOnPremMailboxSizeAsync(address, cancellationToken);
            return new OnPremSizes(sizes.MailboxGB, sizes.MailboxUnreadable);
        }

        public void RealConnect(PowerShell ps, CancellationToken cancellationToken) =>
            base.ConnectOnPrem(ps, "svc-lookup", "not-a-secret", "CONTOSO", cancellationToken: cancellationToken);

        internal override Runspace CreateOnPremRunspace()
        {
            Interlocked.Increment(ref _runspacesMade);
            var runspace = base.CreateOnPremRunspace();
            AsTheRunspaceIsMade?.Invoke();
            return runspace;
        }

        protected override void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connects);
            ConnectTokens.Enqueue(cancellationToken);
            exchange.DefineOnPremSession(ps);
        }

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken)
        {
            CredentialsFetched?.TrySetResult();
            return Task.FromResult<(string username, string password, string domain)?>(("svc-lookup", "not-a-secret", "CONTOSO"));
        }

        private static IConfiguration OnPremServer() => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OnPremExchange:ServerUri"] = "https://exchange.contoso.test/PowerShell/" })
            .Build();
    }

    /// <summary>
    /// A local runspace whose New-PSSessionOption and New-PSSession stand in for remoting: each logs its call; New-PSSession
    /// fails the first <c>failures</c> times - cancelling <see cref="CancelAfterTheFirstFailure"/> a moment later, from a timer,
    /// so the cancel lands during the pause - and then hands back a stand-in session.
    /// </summary>
    private sealed class ConnectSession : IDisposable
    {
        private readonly Runspace _runspace;

        public ConnectSession(int failures)
        {
            _runspace = RunspaceFactory.CreateRunspace();
            _runspace.Open();
            _runspace.SessionStateProxy.SetVariable("connectCalls", Calls);
            _runspace.SessionStateProxy.SetVariable("failuresLeft", new int[] { failures });
            _runspace.SessionStateProxy.SetVariable("connect", this);
            using (var init = PowerShell.Create())
            {
                init.Runspace = _runspace;
                init.AddScript("""
                    function global:New-PSSessionOption { param($OperationTimeout, $OpenTimeout) [void]$global:connectCalls.Add('New-PSSessionOption'); 'stand-in-options' }
                    function global:New-PSSession {
                        [CmdletBinding()] param($ConfigurationName, $ConnectionUri, $Authentication, $Credential, $SessionOption)
                        [void]$global:connectCalls.Add('New-PSSession')
                        if ($global:failuresLeft[0] -gt 0) {
                            $global:failuresLeft[0]--
                            $global:connect.FirstFailure()
                            throw 'the on-premises server did not answer'
                        }
                        'stand-in-remote-session'
                    }
                    """).Invoke();
                Assert.False(init.HadErrors, "The stand-in remoting cmdlets could not be defined.");
            }
            PowerShell = PowerShell.Create();
            PowerShell.Runspace = _runspace;
        }

        public ArrayList Calls { get; } = ArrayList.Synchronized(new ArrayList());

        public PowerShell PowerShell { get; }

        public CancellationTokenSource? CancelAfterTheFirstFailure { get; set; }

        public void FirstFailure() => CancelAfterTheFirstFailure?.CancelAfter(TimeSpan.FromMilliseconds(200));

        public void Dispose()
        {
            PowerShell.Dispose();
            _runspace.Dispose();
        }
    }

    private static void Inject(Page page, string property, object value) =>
        typeof(Page).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(page, value);

    private static FieldInfo PageField(string name) => typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Holds every continuation posted to it until the test pumps (the aud-a6d technique).</summary>
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
                    throw new TimeoutException("The held read neither completed nor posted a continuation.");
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
