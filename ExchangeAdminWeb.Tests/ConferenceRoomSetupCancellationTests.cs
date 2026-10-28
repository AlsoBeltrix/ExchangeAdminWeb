using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-58, slice A8 of docs/CancellationDepth-Plan.md: Conference Rooms' single-room setup takes the operator's cancel
/// at every step before Set-Place. The room read and the room list's resolution end at their wait for a pool connection or
/// between their commands (E3); the room-list membership read likewise; each preflight's directory steps - the runspace, the
/// ActiveDirectory module's import, each read - are looked at before they start (E7); Set-Place's own wait for a connection
/// takes the token (E4). Every catch on the way lets the operator's cancel through: no warning for a read that "failed", no
/// refusal for a list that "could not be resolved", no error for a preflight step - the row ends as a cancellation, nothing
/// written, which the page files Cancelled (kd-57's words, kd-59's filing). Once Set-Place is sent nothing looks at it.
/// </summary>
/// <remarks>
/// The real service over <see cref="ExchangeWriteHarness"/>'s stand-ins: Exchange Online in a pool of the test's own whose
/// calls a test can watch, Active Directory in the service's runspace seam - its Import-Module recorded beside the calls, every
/// directory call shown to the test - and the credential answered at once. Every wait bounded. No Exchange, no vault, no
/// directory.
/// </remarks>
public sealed class ConferenceRoomSetupCancellationTests : IDisposable
{
    private const string Typed = "room1@contoso.test";
    private const string Building = "Building 1";
    private const string CheckedObject = "5a5a5a5a-0000-4000-8000-000000000001";
    private static readonly Guid Checked = Guid.Parse("0f0f0f0f-1111-4222-8333-444444444444");
    private static readonly string List = ConferenceRoomService.BuildRoomListName(Building);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>"At once": far inside the pool's two-minute wait for a connection.</summary>
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(10);

    /// <summary>The connections <see cref="ExoConnectionPool"/> admits at once: its slots.</summary>
    private const int PoolConnections = 5;

    // Exchange Online's refusal of a cloud add to a list synchronized from on-premises.
    private const string OnPremMastered =
        "The operation on the group failed because it's out of the current user's write scope. The action can't be "
        + "performed on the object because the object is being synchronized from your on-premises organization.";

    private readonly ExchangeWriteHarness _h = new();
    private readonly CapturingLogger _log = new();

    public ConferenceRoomSetupCancellationTests()
    {
        _h.AddMailbox(Typed, Checked, recipientTypeDetails: "RoomMailbox");
        _h.DirectoryObjects[CheckedObject] = new Hashtable
        {
            ["ObjectGUID"] = Guid.Parse(CheckedObject),
            ["DistinguishedName"] = "CN=Room 1,OU=Rooms,DC=contoso,DC=test",
        };
    }

    public void Dispose() => _h.Dispose();

    private static ValidatedMailbox Room() => ExchangeWriteHarness.Validated(Checked, CheckedObject);

    private static Task<RoomOperationResult> Apply(ConferenceRoomService service, CancellationToken cancellationToken, string city = "") =>
        service.SetRoomMetadataAndListAsync(Typed, Room(), city, Building, 8, "2", "Second", "TV", "Polycom", "", "",
            "GMT Standard Time", cancellationToken);

    /// <summary>A list synchronized from on-premises, so the room-list preflight reads its membership and Active Directory.</summary>
    private void TheListIsSynced()
    {
        _h.ListSyncState[List] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
    }

    // ---- the room read ----------------------------------------------------------------------------------------------------

    // Every connection the pool admits held elsewhere, the row's first pooled call - the room read - waits for one, and the
    // operator cancels. It ends at once, every connection still held: nothing asked of Exchange or the directory, nothing
    // written, and no warning that the room could not be read. Before, the read waited for a connection, ran, and the row
    // went on to its preflights, where a credential fetch or the look before Set-Place saw the cancel.
    [Fact]
    public async Task ARowCancelledWhileTheRoomReadWaitsForAPoolConnection_EndsAtOnce_NothingAsked()
    {
        using var pool = Pool();
        var service = Service(pool);
        using var cancel = new CancellationTokenSource();
        var held = await HoldEveryPoolConnection(pool);
        var apply = Task.FromResult<RoomOperationResult>(null!);
        Exception? outcome;
        try
        {
            apply = Apply(service, cancel.Token);
            await WaitUntil(() => BorrowsWaitingForAPoolConnection() == 1, "the room read waits for a pool connection");
            await cancel.CancelAsync();

            outcome = await Record.ExceptionAsync(() => apply.WaitAsync(Promptly, TestContext.Current.CancellationToken)); // every connection still held
        }
        finally
        {
            ReturnAll(pool, held);
            await Record.ExceptionAsync(() => apply.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        Assert.IsAssignableFrom<OperationCanceledException>(outcome);
        Assert.Empty(_h.Calls); // nothing asked of Exchange or the directory
        Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains("reading the room failed", StringComparison.Ordinal));
    }

    // Cancelled between the room read's commands - as Get-Place runs - the read ends before its next command, the time
    // zone's: the cancel is not swallowed by that read's best-effort catch.
    [Fact]
    public async Task ARoomReadCancelledBetweenItsCommands_EndsThere()
    {
        using var cancel = new CancellationTokenSource();
        using var pool = Pool(call =>
        {
            if (Command(call) == "Get-Place")
                cancel.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(pool).GetRoomInfoAsync(Room(), cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken));

        Assert.NotEmpty(_h.CallsTo("Get-Place"));
        Assert.Empty(_h.CallsTo("Get-MailboxRegionalConfiguration"));
    }

    // ---- the room list ----------------------------------------------------------------------------------------------------

    // The building's list is not there under its own name, so its resolution reads the legacy name next; cancelled as the
    // first read runs, it ends before the second. The row ends as a cancellation - not the refusal a resolution that failed
    // is turned into, and no refusal logged - nothing written.
    [Fact]
    public async Task ARowCancelledDuringTheRoomListsResolution_EndsThere_AsACancelNotARefusal()
    {
        _h.MissingLists.Add(List);
        using var cancel = new CancellationTokenSource();
        using var pool = Pool(call =>
        {
            if (Command(call) == "Get-DistributionGroup")
                cancel.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Apply(Service(pool), cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken));

        Assert.Single(_h.CallsTo("Get-DistributionGroup")); // the legacy name never asked for
        Assert.Empty(_h.Writes);
        Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains("Room-list resolution", StringComparison.Ordinal));
    }

    // A list synchronized from on-premises: cancelled as the resolution's one read runs - that read runs to its end - the
    // room-list preflight's membership read is never sent, and nothing after it runs.
    [Fact]
    public async Task ARowCancelledBeforeTheRoomListsMembershipRead_NeverSendsIt()
    {
        TheListIsSynced();
        using var cancel = new CancellationTokenSource();
        using var pool = Pool(call =>
        {
            if (Command(call) == "Get-DistributionGroup")
                cancel.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Apply(Service(pool), cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken));

        Assert.Single(_h.CallsTo("Get-DistributionGroup"));
        Assert.Empty(_h.CallsTo("Get-DistributionGroupMember"));
        Assert.Empty(_h.Writes);
    }

    // ---- the preflights' directory steps ----------------------------------------------------------------------------------

    /// <summary>
    /// Where a preflight's directory steps are cancelled: the City/State/Country preflight's (a row with a city) and the
    /// room-list preflight's (a list synchronized from on-premises) - as its credential comes back, as its directory runspace
    /// opens, as the ActiveDirectory module is imported, and at each read.
    /// </summary>
    public static TheoryData<string> DirectorySteps =>
    [
        "City/State/Country: its credential back", "City/State/Country: its runspace open", "City/State/Country: the module imported",
        "City/State/Country: the room's object read", "room list: its credential back", "room list: its runspace open",
        "room list: the module imported", "room list: the room's object read", "room list: the list's group read",
    ];

    // Each step of a preflight in Active Directory is looked at before it starts (E7), so a cancel at one ends the row before
    // the next - as a cancellation, nothing written, and no error logged for a step that "failed": each preflight's catch lets
    // the operator's cancel through. Before, only the preflights' credential fetches took the token, and the steps after
    // them ran to their end.
    [Theory]
    [MemberData(nameof(DirectorySteps))]
    public async Task ARowCancelledAtAPreflightsDirectoryStep_EndsBeforeTheNext(string step)
    {
        var cityPreflight = step.StartsWith("City/State/Country", StringComparison.Ordinal);
        if (!cityPreflight)
            TheListIsSynced();
        using var cancel = new CancellationTokenSource();
        var at = step[(step.IndexOf(':', StringComparison.Ordinal) + 2)..];
        var service = Service(_h.Pool,
            afterTheCredentialFor: at == "its credential back" ? (cityPreflight ? "Set-ADUser preflight" : "on-prem room-list membership preflight") : null,
            afterTheCredential: () => { cancel.Cancel(); return Task.CompletedTask; },
            asTheDirectoryRunspaceOpens: at == "its runspace open" ? cancel.Cancel : null,
            asADirectoryCallIsMade: call =>
            {
                var cancelHere = at switch
                {
                    "the module imported" => Command(call) == "Import-Module",
                    "the room's object read" => Command(call) == "Get-ADUser",
                    "the list's group read" => Command(call) == "Get-ADGroup" && Properties(call).Contains("mail"),
                    _ => false,
                };
                if (cancelHere)
                    cancel.Cancel();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Apply(service, cancel.Token, city: cityPreflight ? "Dublin" : "").WaitAsync(Bound, TestContext.Current.CancellationToken));

        switch (at)
        {
            case "its credential back":
                Assert.Equal(0, service.DirectoryRunspaces);
                break;
            case "its runspace open":
                Assert.Equal(1, service.DirectoryRunspaces);
                Assert.Empty(_h.CallsTo("Import-Module"));
                break;
            case "the module imported":
                Assert.Single(_h.CallsTo("Import-Module"));
                Assert.Empty(_h.CallsTo("Get-ADUser"));
                break;
            case "the room's object read" when cityPreflight:
                Assert.Single(_h.CallsTo("Get-ADUser")); // its read through the object's own domain controller never sent
                break;
            case "the room's object read":
                Assert.Single(_h.CallsTo("Get-ADUser"));
                Assert.Empty(_h.CallsTo("Get-ADForest")); // the list's group never looked for
                Assert.Empty(_h.CallsTo("Get-ADGroup"));
                break;
            default:
                Assert.NotEmpty(_h.CallsTo("Get-ADGroup"));
                Assert.DoesNotContain(_h.CallsTo("Get-ADGroup"), call => Properties(call).Contains("member")); // its membership never read
                break;
        }
        Assert.Empty(_h.CallsTo("Set-Place"));
        Assert.Empty(_h.Writes);
        Assert.DoesNotContain(_log.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    // ---- Set-Place's wait -----------------------------------------------------------------------------------------------

    // Every connection the pool admits taken once the last preflight has its credential, Set-Place's call waits for one, and
    // the operator cancels: the row ends at once, every connection still held, Set-Place never sent. Before, the wait took no
    // token, and kd-75's look inside the call ended the row once a connection freed.
    [Fact]
    public async Task ARowCancelledWhileSetPlaceWaitsForAPoolConnection_EndsAtOnce()
    {
        TheListIsSynced();
        using var pool = Pool();
        using var cancel = new CancellationTokenSource();
        var held = new List<PooledRunspace>();
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(pool,
            afterTheCredentialFor: "on-prem room-list membership preflight",
            afterTheCredential: async () =>
            {
                held.AddRange(await HoldEveryPoolConnection(pool));
                holding.SetResult();
            });
        var apply = Task.FromResult<RoomOperationResult>(null!);
        Exception? outcome;
        try
        {
            apply = Apply(service, cancel.Token);
            // First the five held - a borrow of the test's own is "waiting" for an instant as it is granted - then Set-Place's.
            await holding.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            await WaitUntil(() => BorrowsWaitingForAPoolConnection() == 1, "Set-Place's call waits for a pool connection");
            await cancel.CancelAsync();

            outcome = await Record.ExceptionAsync(() => apply.WaitAsync(Promptly, TestContext.Current.CancellationToken)); // every connection still held
        }
        finally
        {
            ReturnAll(pool, held);
            await Record.ExceptionAsync(() => apply.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        Assert.IsAssignableFrom<OperationCanceledException>(outcome);
        Assert.Empty(_h.CallsTo("Set-Place"));
        Assert.Empty(_h.Writes);
    }

    // ---- the service ------------------------------------------------------------------------------------------------------

    private WatchedRooms Service(ExoConnectionPool pool, string? afterTheCredentialFor = null, Func<Task>? afterTheCredential = null,
        Action? asTheDirectoryRunspaceOpens = null, Action<Hashtable>? asADirectoryCallIsMade = null) =>
        new(_h, pool, _log)
        {
            AfterTheCredentialFor = afterTheCredentialFor,
            AfterTheCredential = afterTheCredential,
            AsTheDirectoryRunspaceOpens = asTheDirectoryRunspaceOpens,
            AsADirectoryCallIsMade = asADirectoryCallIsMade,
        };

    /// <summary>
    /// The real room service over <paramref name="pool"/>, its seams watched: the credential answered at once - first awaiting
    /// <see cref="AfterTheCredential"/> for a purpose naming <see cref="AfterTheCredentialFor"/> - and each directory runspace
    /// counted as it opens, its Import-Module recorded beside the calls, every directory call shown to
    /// <see cref="AsADirectoryCallIsMade"/>, then <see cref="AsTheDirectoryRunspaceOpens"/> run.
    /// </summary>
    private sealed class WatchedRooms(ExchangeWriteHarness h, ExoConnectionPool pool, ILogger<ConferenceRoomService> logger)
        : ConferenceRoomService(pool, null!, h.ModuleConfig, null!, h.Trace, h.Config, logger)
    {
        private int _directoryRunspaces;

        public string? AfterTheCredentialFor { get; init; }

        public Func<Task>? AfterTheCredential { get; init; }

        public Action? AsTheDirectoryRunspaceOpens { get; init; }

        public Action<Hashtable>? AsADirectoryCallIsMade { get; init; }

        public int DirectoryRunspaces => Volatile.Read(ref _directoryRunspaces);

        protected override async Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken = default)
        {
            if (AfterTheCredentialFor is { } text && purpose.Contains(text, StringComparison.Ordinal) && AfterTheCredential is { } then)
                await then();
            return ("svc-rooms", "not-a-secret", "CONTOSO");
        }

        protected override Runspace OpenDirectoryRunspace()
        {
            var runspace = h.OpenDirectoryRunspace();
            Interlocked.Increment(ref _directoryRunspaces);
            runspace.SessionStateProxy.SetVariable("Calls", new ObservedCalls(h.Calls, call => AsADirectoryCallIsMade?.Invoke(call)));
            using (var define = PowerShell.Create())
            {
                define.Runspace = runspace;
                define.AddScript("""function global:Import-Module { [CmdletBinding()] param($Name) [void]$global:Calls.Add(@{ '__Command' = 'Import-Module'; Name = "$Name" }) }""").Invoke();
                Assert.False(define.HadErrors, "The recording Import-Module could not be defined.");
            }
            AsTheDirectoryRunspaceOpens?.Invoke();
            return runspace;
        }
    }

    /// <summary>A pool of the test's own over the harness's stand-ins: every call recorded as before, then shown to <paramref name="observe"/>.</summary>
    private ExoConnectionPool Pool(Action<Hashtable>? observe = null) =>
        new(_h.Config, _h.ModuleConfig, _h.Enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace,
            (_, generation, _) =>
            {
                var runspace = _h.OpenExchangeRunspace();
                if (observe is not null)
                    runspace.SessionStateProxy.SetVariable("Calls", new ObservedCalls(_h.Calls, observe));
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

    private sealed class CapturingLogger : ILogger<ConferenceRoomService>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private static string Command(Hashtable call) => (string)call["__Command"]!;

    /// <summary>The properties a recorded directory call asked for, joined - empty when it asked for none.</summary>
    private static string Properties(Hashtable call) =>
        ExchangeWriteHarness.Bound(call, "Properties") is IEnumerable properties and not string
            ? string.Join(",", properties.Cast<object>().Select(p => (p is PSObject wrapped ? wrapped.BaseObject : p)?.ToString()))
            : "";

    // ---- the pool, saturated ----------------------------------------------------------------------------------------------

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
                    using var step = System.Text.Json.JsonDocument.Parse(line);
                    stage = step.RootElement.TryGetProperty("stage", out var s) ? s.GetString() : null;
                    result = step.RootElement.TryGetProperty("result", out var r) ? r.GetString() : null;
                }
                catch (System.Text.Json.JsonException)
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
}
