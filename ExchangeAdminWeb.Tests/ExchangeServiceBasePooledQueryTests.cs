using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// psl-4. A pooled query that throws after an invoke helper has already classified the session
/// from its error stream (connection / retriable precheck) must reach the pool with that
/// classification: the suspect connection is discarded and an eligible precheck is retried once
/// on a fresh connection, as for a query that returns with the same tracker flags. When no retry
/// is taken, the exception that surfaces is the original one.
///
/// The real pool runs over a temp SQLite config store. Only Connect-ExchangeOnline is replaced,
/// through the internal connect seam, by opened local runspaces whose functions stand in for the
/// EXO cmdlets. No Exchange module, credential or network connection is used.
/// </summary>
public sealed class ExchangeServiceBasePooledQueryTests : IDisposable
{
    private const string Mailbox = "user@example.test";
    private const string Room = "room@example.test";

    // Stream-only classification: the thrown message carries neither classifier signature.
    private const string Precheck = "GetCurrentConnectionContext requires authentication";
    private const string MidFlight = "The remote session was unexpectedly closed";
    private const string Unclassified = "ordinary mailbox failure";

    private const string Found = "[pscustomobject]@{ PrimarySmtpAddress = $Identity }";
    private const string RoomMailbox =
        "[pscustomobject]@{ PrimarySmtpAddress = $Identity; DisplayName = 'Room'; RecipientTypeDetails = 'RoomMailbox' }";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eaw-pooled-query-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;
    private readonly ModuleConfigService _moduleConfig;
    private readonly ModuleEnablementService _enablement;
    private readonly OperationTraceService _trace;

    // Shared by every stand-in runspace: "<connection id>|<command>|<identity>", in call order.
    private readonly List<string> _calls = new();
    // ConnectionIds the pool destroyed: DestroyRunspace disconnects by id before disposing.
    private readonly List<string> _disconnects = new();
    // The SDK's own Failed-state reasons, so a surfaced exception can be compared by reference.
    private readonly List<Exception> _sdkFailures = new();
    private int _connects;

    public ExchangeServiceBasePooledQueryTests()
    {
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _dir
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_dir);

        var store = TestConfigStore.Create(_dir);
        var catalog = new ModuleCatalog();
        _moduleConfig = new ModuleConfigService(catalog, env, new ModuleConfigRepository(store), NullLogger<ModuleConfigService>.Instance);
        _enablement = new ModuleEnablementService(catalog, env, _moduleConfig, new ModuleEnablementRepository(store), _config, NullLogger<ModuleEnablementService>.Instance);
        _enablement.SaveEnablement(new Dictionary<string, bool> { [ExoConnectionPool.ConfigModuleKey] = true });
        _moduleConfig.SaveModuleConfig(ExoConnectionPool.ConfigModuleKey, new Dictionary<string, string>
        {
            [ExoConnectionPool.ConfigAppIdKey] = "11111111-1111-1111-1111-111111111111",
            [ExoConnectionPool.ConfigOrganizationKey] = "contoso.onmicrosoft.com",
            [ExoConnectionPool.ConfigCertSubjectKey] = "CN=EXO-Automation",
        });
        _trace = new OperationTraceService(_config, new JsonlLogService(_config, NullLogger<JsonlLogService>.Instance));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task HasCloudMailbox_StreamOnlyPrecheckThenUnrelatedThrow_DiscardsAndRetriesOnce()
    {
        using var pool = NewPool(Fn("Get-Mailbox", Fails(Precheck)), Fn("Get-Mailbox", Found));
        var service = new Harness(pool);

        var found = false;
        var failure = await Record.ExceptionAsync(async () => found = await service.HasCloudMailbox(Mailbox));

        Assert.Null(failure);
        Assert.True(found);
        Assert.Equal([$"conn-1|Get-Mailbox|{Mailbox}", $"conn-2|Get-Mailbox|{Mailbox}"], _calls);
        // The suspect connection was destroyed, not pooled; only the fresh one went back.
        Assert.Equal(["conn-1"], _disconnects);
        Assert.Equal(1, pool.AvailableCount);
    }

    [Fact]
    public async Task GetRoomInfo_StreamOnlyPrecheckFromGetPlaceThenUnrelatedThrow_DiscardsAndRetriesOnce()
    {
        using var pool = NewPool(
            Fn("Get-Mailbox", RoomMailbox) + Fn("Get-Place", Fails(Precheck)),
            Fn("Get-Mailbox", RoomMailbox)
                + Fn("Get-Place", "[pscustomobject]@{ City = 'Dublin'; Building = 'B1'; Capacity = 8 }")
                + Fn("Get-MailboxRegionalConfiguration", "[pscustomobject]@{ TimeZone = 'GMT Standard Time' }"));
        var service = new ConferenceRoomService(pool, null!, _moduleConfig, null!, null!, _config,
            NullLogger<ConferenceRoomService>.Instance);

        RoomInfo? room = null;
        var failure = await Record.ExceptionAsync(async () => room = await service.GetRoomInfoAsync(Room));

        Assert.Null(failure);
        Assert.NotNull(room);
        Assert.Equal("Dublin", room.City);
        Assert.Equal(8, room.Capacity);
        Assert.Equal("GMT Standard Time", room.TimeZone);
        Assert.Equal([
            $"conn-1|Get-Mailbox|{Room}", $"conn-1|Get-Place|{Room}",
            $"conn-2|Get-Mailbox|{Room}", $"conn-2|Get-Place|{Room}", $"conn-2|Get-MailboxRegionalConfiguration|{Room}"
        ], _calls);
        Assert.Equal(["conn-1"], _disconnects);
        Assert.Equal(1, pool.AvailableCount);
    }

    [Fact]
    public async Task HasCloudMailbox_StreamOnlyPrecheckOnBothAttempts_SurfacesSecondFailure_NoThirdAttempt()
    {
        using var pool = NewPool(Fn("Get-Mailbox", Fails(Precheck)), Fn("Get-Mailbox", Fails(Precheck)));
        var service = new Harness(pool);

        var failure = await Record.ExceptionAsync(() => service.HasCloudMailbox(Mailbox));

        Assert.NotNull(failure);
        Assert.Equal(2, _sdkFailures.Count);
        Assert.Same(_sdkFailures[1], failure);
        Assert.Equal([$"conn-1|Get-Mailbox|{Mailbox}", $"conn-2|Get-Mailbox|{Mailbox}"], _calls);
        Assert.Equal(["conn-1", "conn-2"], _disconnects);
        Assert.Equal(0, pool.AvailableCount);
    }

    [Fact]
    public async Task HasCloudMailbox_StreamOnlyMidFlightDropThenUnrelatedThrow_DiscardsWithoutRetry()
    {
        using var pool = NewPool(Fn("Get-Mailbox", Fails(MidFlight)));
        var service = new Harness(pool);

        var failure = await Record.ExceptionAsync(() => service.HasCloudMailbox(Mailbox));

        // A mid-flight drop may follow a committed write, so it never replays.
        Assert.NotNull(failure);
        Assert.Same(Assert.Single(_sdkFailures), failure);
        Assert.Equal([$"conn-1|Get-Mailbox|{Mailbox}"], _calls);
        Assert.Equal(["conn-1"], _disconnects);
        Assert.Equal(0, pool.AvailableCount);
    }

    [Fact]
    public async Task PooledQuery_NotEligible_StreamOnlyPrecheckThenUnrelatedThrow_DiscardsWithoutRetry()
    {
        using var pool = NewPool(Fn("Get-Mailbox", Fails(Precheck)));
        var service = new Harness(pool);

        var failure = await Record.ExceptionAsync(() => service.OptionalMailboxQuery(Mailbox, allowRetry: false));

        Assert.NotNull(failure);
        Assert.Same(Assert.Single(_sdkFailures), failure);
        Assert.Equal([$"conn-1|Get-Mailbox|{Mailbox}"], _calls);
        Assert.Equal(["conn-1"], _disconnects);
        Assert.Equal(0, pool.AvailableCount);
    }

    [Fact]
    public async Task HasCloudMailbox_UnclassifiedStreamErrorThenThrow_ReturnsConnectionWithoutRetry()
    {
        using var pool = NewPool(Fn("Get-Mailbox", Fails(Unclassified)));
        var service = new Harness(pool);

        var failure = await Record.ExceptionAsync(() => service.HasCloudMailbox(Mailbox));

        Assert.NotNull(failure);
        Assert.Same(Assert.Single(_sdkFailures), failure);
        Assert.Equal([$"conn-1|Get-Mailbox|{Mailbox}"], _calls);
        Assert.Empty(_disconnects);
        Assert.Equal(1, pool.AvailableCount);
    }

    [Fact]
    public async Task PooledQuery_ThrownPrecheckAfterTrackerMidFlight_StillRetriesOnce()
    {
        using var pool = NewPool(
            Fn("Get-Mailbox", $"Write-Error '{MidFlight}' -ErrorAction Continue; 'stale'"),
            Fn("Get-Mailbox", "'fresh'"));
        var service = new Harness(pool);

        string? value = null;
        var failure = await Record.ExceptionAsync(async () => value = await service.ThrowAfterOptional(Mailbox));

        Assert.Null(failure);
        Assert.Equal("fresh", value);
        Assert.Equal([$"conn-1|Get-Mailbox|{Mailbox}", $"conn-2|Get-Mailbox|{Mailbox}"], _calls);
        Assert.Equal(["conn-1"], _disconnects);
        Assert.Equal(1, pool.AvailableCount);
    }

    private sealed class Harness : ExchangeServiceBase
    {
        public Harness(ExoConnectionPool pool) : base(pool, null!, NullLogger.Instance, "") { }

        // The actual caller, unchanged (eligible: allowRetry true).
        public Task<bool> HasCloudMailbox(string identity) => HasCloudMailboxAsync(identity);

        // HasCloudMailboxAsync's query with the eligibility as a parameter, for the
        // non-eligible control.
        public Task<bool> OptionalMailboxQuery(string identity, bool allowRetry) =>
            RunPooledQueryAsync((ps, tracker) =>
            {
                ps.AddCommand("Get-Mailbox").AddParameter("Identity", identity).AddParameter("ErrorAction", "Ignore");
                return InvokeOptional(ps, tracker).Any();
            }, allowRetry);

        // An optional call flags a mid-flight drop, then the query itself throws the precheck
        // signature without going through a helper: the throw's own signature still counts.
        public Task<string> ThrowAfterOptional(string identity) =>
            RunPooledQueryAsync((ps, tracker) =>
            {
                ps.AddCommand("Get-Mailbox").AddParameter("Identity", identity).AddParameter("ErrorAction", "Ignore");
                var value = InvokeOptional(ps, tracker).Single().BaseObject.ToString()!;
                if (value == "stale")
                    throw new InvalidOperationException("You must call Connect-ExchangeOnline before calling any other cmdlet.");
                return value;
            }, allowRetry: true);
    }

    private static string Fails(string streamError) =>
        $"Write-Error '{streamError}' -ErrorAction Continue; throw 'optional fatal'";

    // Plain (non-advanced) functions: ErrorAction binds as an ordinary parameter, so the injected
    // throw stays terminating at the SDK boundary, as in the psl-1 stand-ins.
    private static string Fn(string name, string body) => $$"""
        function global:{{name}} {
            param($Identity, $ErrorAction)
            $global:calls.Add("$global:connectionId|{{name}}|$Identity")
            {{body}}
        }

        """;

    // Stand-ins for a connect the test did not script: any call fails loudly.
    private static readonly string Unscripted =
        Fn("Get-Mailbox", "throw 'unscripted connection'")
        + Fn("Get-Place", "throw 'unscripted connection'")
        + Fn("Get-MailboxRegionalConfiguration", "throw 'unscripted connection'");

    /// <summary>The n-th new connection defines the n-th script's stand-in functions.</summary>
    private ExoConnectionPool NewPool(params string[] attempts) =>
        new(_config, _moduleConfig, _enablement, NullLogger<ExoConnectionPool>.Instance, _trace,
            connect: (_, generation, _) =>
            {
                var n = Interlocked.Increment(ref _connects);
                return StandIn(generation, $"conn-{n}", n <= attempts.Length ? attempts[n - 1] : Unscripted);
            });

    private PooledRunspace StandIn(long generation, string connectionId, string definitions)
    {
        var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        rs.SessionStateProxy.SetVariable("calls", _calls);
        rs.SessionStateProxy.SetVariable("disconnects", _disconnects);
        rs.SessionStateProxy.SetVariable("connectionId", connectionId);
        using (var init = PowerShell.Create())
        {
            init.Runspace = rs;
            init.AddScript(definitions + """
                function global:Disconnect-ExchangeOnline {
                    param($ConnectionId, $Confirm)
                    $global:disconnects.Add($ConnectionId)
                }
                """);
            init.Invoke();
            Assert.Empty(init.Streams.Error);
        }

        var ps = PowerShell.Create();
        ps.Runspace = rs;
        ps.InvocationStateChanged += (_, args) =>
        {
            if (args.InvocationStateInfo.State == PSInvocationState.Failed)
                lock (_sdkFailures) _sdkFailures.Add(args.InvocationStateInfo.Reason);
        };
        return new PooledRunspace(rs, ps, generation, connectionId);
    }
}
