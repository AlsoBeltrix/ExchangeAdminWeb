using System.Collections;
using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using Page = ExchangeAdminWeb.Components.Pages.MessageTrace;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-58, slice A10 of docs/CancellationDepth-Plan.md: Message Trace's reads take the operator's cancel. The cloud
/// search's windows and the cloud detail read end at their wait for a pool connection (the read helper, E3); the on-premises
/// search and detail end at the wait for one of the server's two slots, as the session's runspace opens, at the connect and
/// before each read (E7). Each read's catch lets the cancel through, so the search leaves as the operator's cancel and the
/// detail says the cancel stopped it - which the page files Cancelled in kd-57's words (kd-59's filing). Before, kd-59 and
/// kd-73 gave the token the credential fetches and the gaps between windows only: a read waiting its turn ran when its turn
/// came.
/// </summary>
/// <remarks>
/// The real service over <see cref="ExchangeWriteHarness"/>'s configuration and trace: Exchange Online in a pool of the test's
/// own whose stand-in Get-MessageTraceV2 and Get-MessageTraceDetailV2 answer nothing and log each call; on-premises Exchange in
/// the service's seams - its connect defines a stand-in session (an Invoke-Command run locally, Get-TransportService and
/// Get-MessageTrackingLog logging each call) and records the token it was handed, its runspace counted as it is made, its
/// credential answered at once. The throttle is the base's own, process-wide: this class shares the collection of the others
/// that hold its two slots. The page's RunTrace and ToggleDetail run by reflection. Every wait bounded. No Exchange, no vault.
/// </remarks>
[Collection(OnPremThrottleCollection.Name)]
public sealed class MessageTraceReadCancellationTests : IDisposable
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int PoolConnections = 5;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>"At once": far inside the two-minute waits for a pool connection and for the on-premises server.</summary>
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(10);

    private readonly ExchangeWriteHarness _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>A search of the last five days: one window of the cloud read.</summary>
    private static DateTime Start => DateTime.Today.AddDays(-5);

    private static DateTime End => DateTime.Today;

    private static MessageTraceResult CloudMessage() => new()
    {
        Received = DateTime.UtcNow,
        SenderAddress = "sender@contoso.test",
        RecipientAddress = "rcpt@contoso.test",
        Subject = "Quarterly figures",
        Status = "Delivered",
        MessageId = "<m1@contoso.test>",
        MessageTraceId = "trace-1",
        Backend = "ExchangeOnline",
    };

    private static MessageTraceResult OnPremMessage() => new()
    {
        Received = DateTime.UtcNow,
        SenderAddress = "sender@contoso.test",
        RecipientAddress = "rcpt@contoso.test",
        Subject = "Quarterly figures",
        Status = "Delivered",
        MessageId = "<m1@contoso.test>",
        Backend = "OnPrem",
    };

    // ---- Exchange Online ----------------------------------------------------------------------------------------------------

    // Every connection the pool admits held elsewhere, the search's window waits for one, and the operator cancels: the search
    // ends at once, every connection still held, nothing asked of Exchange Online - the operator's cancel, not a backend that
    // failed. Before, the window waited until a connection freed and ran. A search of one window, and one of several.
    [Theory]
    [InlineData(5)]
    [InlineData(30)]
    public async Task ASearchCancelledWhileItsWindowWaitsForAPoolConnection_EndsAtOnce_NothingAsked(int days)
    {
        using var pool = Pool();
        var service = Service(pool, onPrem: false);
        using var cancel = new CancellationTokenSource();
        var held = await HoldEveryPoolConnection(pool);
        var search = Task.FromResult<MessageTraceResponse>(null!);
        Exception? outcome;
        try
        {
            search = service.GetMessageTraceAsync("sender@contoso.test", null, End.AddDays(-days), End, null, null, cancel.Token);
            await WaitUntil(() => BorrowsWaitingForAPoolConnection() == 1, "the window waits for a pool connection");
            await cancel.CancelAsync();

            outcome = await Record.ExceptionAsync(() => search.WaitAsync(Promptly, TestContext.Current.CancellationToken)); // every connection still held
        }
        finally
        {
            ReturnAll(pool, held);
            await Record.ExceptionAsync(() => search.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        Assert.IsAssignableFrom<OperationCanceledException>(outcome);
        Assert.Empty(_h.CallsTo("Get-MessageTraceV2"));
    }

    // The same for the cloud detail read: cancelled while it waits for a connection, it ends at once and says the cancel stopped
    // it - what the page files Cancelled. Before, it waited, ran, and said nothing of the cancel.
    [Fact]
    public async Task ADetailCancelledWhileItWaitsForAPoolConnection_EndsAtOnce_SayingTheCancelStoppedIt()
    {
        using var pool = Pool();
        var service = Service(pool, onPrem: false);
        using var cancel = new CancellationTokenSource();
        var held = await HoldEveryPoolConnection(pool);
        var read = Task.FromResult<MessageTraceDetail>(null!);
        MessageTraceDetail? detail = null;
        try
        {
            read = service.GetMessageDetailAsync(CloudMessage(), cancel.Token);
            await WaitUntil(() => BorrowsWaitingForAPoolConnection() == 1, "the detail read waits for a pool connection");
            await cancel.CancelAsync();

            detail = await read.WaitAsync(Promptly, TestContext.Current.CancellationToken); // every connection still held
        }
        finally
        {
            ReturnAll(pool, held);
            await Record.ExceptionAsync(() => read.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        Assert.True(detail.Cancelled);
        Assert.Empty(_h.CallsTo("Get-MessageTraceDetailV2"));
    }

    // The page: the search's activity cancelled while its window waits for a connection is filed Cancelled at once, in kd-57's
    // words.
    [Fact]
    public async Task ACancelOfTheSearch_WhileItsWindowWaitsForAPoolConnection_IsFiledCancelled_AtOnce()
    {
        using var pool = Pool();
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var page = PageOver(Service(pool, onPrem: false), progress);
        var held = await HoldEveryPoolConnection(pool);
        Task? run = null;
        try
        {
            run = RunTrace(page);
            await WaitUntil(() => BorrowsWaitingForAPoolConnection() == 1, "the window waits for a pool connection");
            await cancel.CancelAsync();

            await run.WaitAsync(Promptly, TestContext.Current.CancellationToken); // every connection still held
        }
        finally
        {
            ReturnAll(pool, held);
            if (run is not null)
                await Record.ExceptionAsync(() => run.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        var shown = Assert.IsType<MessageTraceResponse>(Get(page, "response"));
        Assert.Equal("Cancelled by the operator", shown.Error);
        Assert.Equal((false, "Cancelled by the operator"), Assert.Single(progress.Handles).Outcome);
        var row = Assert.Single(AuditRows(), r => r.GetProperty("action").GetString() == "MessageTrace");
        Assert.Equal("Cancelled", row.GetProperty("result").GetString());
        Assert.Empty(_h.CallsTo("Get-MessageTraceV2"));
    }

    // And the detail a row opens: cancelled while its read waits for a connection, filed Cancelled at once.
    [Fact]
    public async Task ACancelOfTheDetail_WhileItsReadWaitsForAPoolConnection_IsFiledCancelled_AtOnce()
    {
        using var pool = Pool();
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var page = PageOver(Service(pool, onPrem: false), progress);
        var held = await HoldEveryPoolConnection(pool);
        Task? run = null;
        try
        {
            run = ToggleDetail(page, CloudMessage());
            await WaitUntil(() => BorrowsWaitingForAPoolConnection() == 1, "the detail read waits for a pool connection");
            await cancel.CancelAsync();

            await run.WaitAsync(Promptly, TestContext.Current.CancellationToken); // every connection still held
        }
        finally
        {
            ReturnAll(pool, held);
            if (run is not null)
                await Record.ExceptionAsync(() => run.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        var shown = Assert.IsType<MessageTraceDetail>(Get(page, "detail"));
        Assert.Equal("Cancelled by the operator", shown.Error);
        var row = Assert.Single(AuditRows(), r => r.GetProperty("action").GetString() == "MessageTrace_Detail");
        Assert.Equal("Cancelled", row.GetProperty("result").GetString());
        Assert.Empty(_h.CallsTo("Get-MessageTraceDetailV2"));
    }

    // ---- on-premises Exchange -------------------------------------------------------------------------------------------------

    public static TheoryData<string> OnPremReads => ["the search", "the detail"];

    // Both of the on-premises server's slots held, a read cancelled while it waits for one ends at once - no runspace made, no
    // session opened - as the operator's cancel. Before, it waited until a slot freed, up to two minutes, and ran.
    [Theory]
    [MemberData(nameof(OnPremReads))]
    public async Task AnOnPremReadCancelledWhileItWaitsForTheServer_EndsAtOnce_OpeningNothing(string read)
    {
        var service = Service(Pool(), onPrem: true);
        using var cancel = new CancellationTokenSource();
        await HoldBothOnPremSlots();
        var reading = Task.CompletedTask;
        try
        {
            service.CredentialsFetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reading = Run(service, read, cancel.Token);
            await service.CredentialsFetched.Task.WaitAsync(Bound, TestContext.Current.CancellationToken); // next: the server's queue
            await cancel.CancelAsync();

            await AssertEndsAsTheCancel(reading, read, Promptly); // both slots still held
        }
        finally
        {
            ReleaseBothOnPremSlots();
            await Record.ExceptionAsync(() => reading.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, service.RunspacesMade);
    }

    // Cancelled as the read's runspace is made: the look once it has opened ends the read before the connect.
    [Theory]
    [MemberData(nameof(OnPremReads))]
    public async Task AnOnPremReadCancelledAsItsRunspaceOpens_NeverConnects(string read)
    {
        var service = Service(Pool(), onPrem: true);
        using var cancel = new CancellationTokenSource();
        service.AsTheRunspaceIsMade = cancel.Cancel;

        await AssertEndsAsTheCancel(Run(service, read, cancel.Token), read, Bound);

        Assert.Equal(1, service.RunspacesMade);
        Assert.Equal(0, service.Connects);
    }

    // Each read hands its token to ConnectOnPrem, which looks at it before each attempt and waits on it between them (A3).
    [Theory]
    [MemberData(nameof(OnPremReads))]
    public async Task EveryOnPremReadHandsItsTokenToTheConnect(string read)
    {
        var service = Service(Pool(), onPrem: true);
        using var cancel = new CancellationTokenSource();

        await Run(service, read, cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(cancel.Token, Assert.Single(service.ConnectTokens));
    }

    // Cancelled as the session opens - the connect has run to its end - the read sends nothing, ends as the cancel and not as a
    // read that failed, and its session is still removed.
    [Theory]
    [MemberData(nameof(OnPremReads))]
    public async Task AnOnPremReadCancelledAsItsSessionOpens_SendsNothing_AndRemovesTheSession(string read)
    {
        var service = Service(Pool(), onPrem: true);
        using var cancel = new CancellationTokenSource();
        service.AsTheSessionOpens = cancel.Cancel;

        await AssertEndsAsTheCancel(Run(service, read, cancel.Token), read, Bound);

        Assert.Empty(_h.CallsTo("Get-TransportService"));
        Assert.Empty(_h.CallsTo("Get-MessageTrackingLog"));
        Assert.Single(service.SessionsRemoved);
    }

    // The search's reads: cancelled as its server list is read - that read runs to its end - it sends no tracking query. The look
    // before each query sits outside the query's own catch, which would have made a cancel one more failed server.
    [Fact]
    public async Task AnOnPremSearchCancelledAsItsServerListIsRead_SendsNoTrackingQuery()
    {
        var service = Service(Pool(), onPrem: true);
        using var cancel = new CancellationTokenSource();
        service.AsAnOnPremCallIsMade = call =>
        {
            if (Command(call) == "Get-TransportService")
                cancel.Cancel();
        };

        await AssertEndsAsTheCancel(Run(service, "the search", cancel.Token), "the search", Bound);

        Assert.Single(_h.CallsTo("Get-TransportService"));
        Assert.Empty(_h.CallsTo("Get-MessageTrackingLog"));
    }

    // The page: the search's activity cancelled while its on-premises half waits for the server - both slots held - is filed
    // Cancelled at once, in kd-57's words.
    [Fact]
    public async Task ACancelOfTheSearch_WhileItsOnPremHalfWaitsForTheServer_IsFiledCancelled_AtOnce()
    {
        var service = Service(Pool(), onPrem: true);
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var page = PageOver(service, progress);
        await HoldBothOnPremSlots();
        Task? run = null;
        try
        {
            service.CredentialsFetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            run = RunTrace(page);
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

        var shown = Assert.IsType<MessageTraceResponse>(Get(page, "response"));
        Assert.Equal("Cancelled by the operator", shown.Error);
        var row = Assert.Single(AuditRows(), r => r.GetProperty("action").GetString() == "MessageTrace");
        Assert.Equal("Cancelled", row.GetProperty("result").GetString());
        Assert.Equal(0, service.RunspacesMade);
    }

    // ---- the service --------------------------------------------------------------------------------------------------------

    private TraceReads Service(ExoConnectionPool pool, bool onPrem) => new(_h, pool, onPrem ? WithAnOnPremServer() : _h.Config);

    private IConfiguration WithAnOnPremServer() => new ConfigurationBuilder()
        .AddConfiguration(_h.Config)
        .AddInMemoryCollection(new Dictionary<string, string?> { ["OnPremExchange:ServerUri"] = "https://exchange.contoso.test/PowerShell/" })
        .Build();

    private static Task Run(TraceReads service, string read, CancellationToken cancellationToken) => read == "the search"
        ? service.GetMessageTraceAsync("sender@contoso.test", null, Start, End, null, null, cancellationToken)
        : service.GetMessageDetailAsync(OnPremMessage(), cancellationToken);

    // The search leaves as the operator's cancel; the detail, which never throws, says the cancel stopped it.
    private static async Task AssertEndsAsTheCancel(Task reading, string read, TimeSpan within)
    {
        if (read == "the search")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(within, TestContext.Current.CancellationToken));
            return;
        }
        var detail = await ((Task<MessageTraceDetail>)reading).WaitAsync(within, TestContext.Current.CancellationToken);
        Assert.True(detail.Cancelled, detail.Error);
    }

    /// <summary>
    /// The real Message Trace service over <paramref name="pool"/>, its on-premises seams watched: the runspace a read opens
    /// (counted, <see cref="AsTheRunspaceIsMade"/> run as it is made), the connect (the token it was handed recorded, a stand-in
    /// session defined - every call through it shown to <see cref="AsAnOnPremCallIsMade"/>, its removal recorded - then
    /// <see cref="AsTheSessionOpens"/> run), and the credential (answered at once, <see cref="CredentialsFetched"/> signalled).
    /// </summary>
    private sealed class TraceReads(ExchangeWriteHarness h, ExoConnectionPool pool, IConfiguration config)
        : MessageTraceService(pool, null!, NullLogger<MessageTraceService>.Instance, config, null!, h.Trace)
    {
        private int _runspacesMade;
        private int _connects;

        public static SemaphoreSlim Throttle => _onPremThrottle;

        public ConcurrentQueue<CancellationToken> ConnectTokens { get; } = new();

        public ArrayList SessionsRemoved { get; } = ArrayList.Synchronized(new ArrayList());

        public Action? AsTheRunspaceIsMade { get; set; }

        public Action? AsTheSessionOpens { get; set; }

        public Action<Hashtable>? AsAnOnPremCallIsMade { get; set; }

        public TaskCompletionSource? CredentialsFetched { get; set; }

        public int RunspacesMade => Volatile.Read(ref _runspacesMade);

        public int Connects => Volatile.Read(ref _connects);

        internal override Runspace CreateOnPremRunspace()
        {
            var runspace = base.CreateOnPremRunspace();
            Interlocked.Increment(ref _runspacesMade);
            AsTheRunspaceIsMade?.Invoke();
            return runspace;
        }

        protected override void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connects);
            ConnectTokens.Enqueue(cancellationToken);
            var proxy = ps.Runspace.SessionStateProxy;
            proxy.SetVariable("Calls", new ObservedCalls(h.Calls, call => AsAnOnPremCallIsMade?.Invoke(call)));
            proxy.SetVariable("SessionsRemoved", SessionsRemoved);
            using (var define = PowerShell.Create())
            {
                define.Runspace = ps.Runspace;
                define.AddScript(OnPremStandIns).Invoke();
                Assert.False(define.HadErrors, "The on-premises stand-ins could not be defined.");
            }
            proxy.SetVariable("onpremSession", "stand-in-session");
            AsTheSessionOpens?.Invoke();
        }

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken)
        {
            CredentialsFetched?.TrySetResult();
            return Task.FromResult<(string username, string password, string domain)?>(("svc-trace", "not-a-secret", "CONTOSO"));
        }
    }

    // The remote session, run locally: Invoke-Command runs the script it is sent; the two on-premises reads log each call and
    // answer nothing.
    private const string OnPremStandIns = """
        function global:Invoke-Command { [CmdletBinding()] param($Session, [scriptblock] $ScriptBlock, [object[]] $ArgumentList) & $ScriptBlock @ArgumentList }
        function global:Remove-PSSession { param($Session) [void]$global:SessionsRemoved.Add("$Session") }
        function global:Get-TransportService { [CmdletBinding()] param() [void]$global:Calls.Add(@{ '__Command' = 'Get-TransportService' }) }
        function global:Get-MessageTrackingLog {
            [CmdletBinding()] param($Start, $End, $ResultSize, $Server, $Sender, $Recipients, $MessageSubject, $MessageId)
            [void]$global:Calls.Add(@{ '__Command' = 'Get-MessageTrackingLog' })
        }
        """;

    // Exchange Online: the two cloud reads log each call and answer nothing.
    private const string CloudStandIns = """
        function global:Get-MessageTraceV2 {
            [CmdletBinding()] param($StartDate, $EndDate, $ResultSize, $SenderAddress, $RecipientAddress, $MessageId, $Subject, $SubjectFilterType)
            [void]$global:Calls.Add(@{ '__Command' = 'Get-MessageTraceV2' })
        }
        function global:Get-MessageTraceDetailV2 {
            [CmdletBinding()] param($MessageTraceId, $RecipientAddress)
            [void]$global:Calls.Add(@{ '__Command' = 'Get-MessageTraceDetailV2' })
        }
        function global:Disconnect-ExchangeOnline { param($ConnectionId, $Confirm) }
        """;

    /// <summary>A pool of the test's own: each connection a runspace of the cloud stand-ins, logging to the harness's calls.</summary>
    private ExoConnectionPool Pool() =>
        new(_h.Config, _h.ModuleConfig, _h.Enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace,
            (_, generation, _) =>
            {
                var runspace = RunspaceFactory.CreateRunspace();
                runspace.Open();
                runspace.SessionStateProxy.SetVariable("Calls", _h.Calls);
                using (var define = PowerShell.Create())
                {
                    define.Runspace = runspace;
                    define.AddScript(CloudStandIns).Invoke();
                }
                var ps = PowerShell.Create();
                ps.Runspace = runspace;
                return new PooledRunspace(runspace, ps, generation, "stand-in");
            });

    /// <summary>The harness's call list: every call added there as before, then shown to <paramref name="observe"/>.</summary>
    private sealed class ObservedCalls(IList calls, Action<Hashtable> observe) : ArrayList
    {
        public override int Add(object? value)
        {
            var index = calls.Add(value);
            observe((Hashtable)value!);
            return index;
        }
    }

    private static string Command(Hashtable call) => (string)call["__Command"]!;

    // ---- the waits ----------------------------------------------------------------------------------------------------------

    private static async Task<List<PooledRunspace>> HoldEveryPoolConnection(ExoConnectionPool pool)
    {
        var held = new List<PooledRunspace>();
        for (var i = 0; i < PoolConnections; i++)
            held.Add(await pool.BorrowAsync(TestContext.Current.CancellationToken));
        return held;
    }

    private static void ReturnAll(ExoConnectionPool pool, List<PooledRunspace> held)
    {
        foreach (var runspace in held)
            pool.Return(runspace);
        held.Clear();
    }

    private static async Task HoldBothOnPremSlots()
    {
        Assert.True(await TraceReads.Throttle.WaitAsync(Bound, TestContext.Current.CancellationToken), "The first on-premises slot never came free.");
        if (!await TraceReads.Throttle.WaitAsync(Bound, TestContext.Current.CancellationToken))
        {
            TraceReads.Throttle.Release();
            Assert.Fail("The second on-premises slot never came free.");
        }
    }

    private static void ReleaseBothOnPremSlots() => TraceReads.Throttle.Release(2);

    /// <summary>
    /// How many borrows are waiting for a free connection, from the pool's own trace: a slot asked for that was neither
    /// granted, refused nor cancelled. A line still being written is skipped and read whole the next time.
    /// </summary>
    private int BorrowsWaitingForAPoolConnection()
    {
        var (asked, granted, ended) = (0, 0, 0);
        foreach (var path in _h.Jsonl.GetTraceLogPaths(DateTime.Now).Where(File.Exists))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                string? stage, result;
                try
                {
                    using var step = JsonDocument.Parse(line);
                    stage = step.RootElement.TryGetProperty("stage", out var s) ? s.GetString() : null;
                    result = step.RootElement.TryGetProperty("result", out var r) ? r.GetString() : null;
                }
                catch (JsonException)
                {
                    continue;
                }

                if (stage == "ExoPoolSlotRequested" && result is "Failed" or "Cancelled")
                    ended++;
                else if (stage == "ExoPoolSlotRequested")
                    asked++;
                else if (stage == "ExoPoolSlotAcquired")
                    granted++;
            }
        }
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

    // ---- the page -----------------------------------------------------------------------------------------------------------

    private Page PageOver(MessageTraceService service, RecordingProgress progress)
    {
        var page = new Page();
        Set(page, "MsgTrace", service);
        Set(page, "Audit", _h.Audit);
        Set(page, "Progress", progress);
        Set(page, "currentUser", @"CONTOSO\operator");
        Set(page, "clientIpAddress", "10.0.0.5");
        Set(page, "ticketNumber", "INC0001");
        Set(page, "sender", "sender@contoso.test");
        Set(page, "startDate", Start);
        return page;
    }

    private static Task RunTrace(Page page) => (Task)typeof(Page).GetMethod("RunTrace", Members)!.Invoke(page, null)!;

    private static Task ToggleDetail(Page page, MessageTraceResult message) =>
        (Task)typeof(Page).GetMethod("ToggleDetail", Members)!.Invoke(page, [0, message])!;

    private List<JsonElement> AuditRows() => _h.AuditRows();

    private static void Set(object page, string name, object value)
    {
        var property = typeof(Page).GetProperty(name, Members);
        if (property is not null)
            property.SetValue(page, value);
        else
            typeof(Page).GetField(name, Members)!.SetValue(page, value);
    }

    private static object? Get(object page, string name) => typeof(Page).GetField(name, Members)!.GetValue(page);
}
