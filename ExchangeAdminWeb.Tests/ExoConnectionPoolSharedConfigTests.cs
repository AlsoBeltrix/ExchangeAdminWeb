using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Review finding scdi-1 (docs/SharedConfigDb-Plan.md section 10). With one config database
/// shared by dev and prod, an ExchangeOnline connection setting saved on the OTHER instance is
/// live here at once for reads, but only the saving process's page calls
/// <see cref="ExoConnectionPool.DrainPool"/>. The pool must therefore retire its own pooled
/// runspaces when the connection fields (AppId, Organization, CertificateSubject) it reads on a
/// borrow differ from the ones they connected under - and must NOT retire them when an unrelated
/// module's save is what changed the database.
///
/// The pool is hosted for real over a temp SQLite store; only the Connect-ExchangeOnline step is
/// replaced through the internal connect seam (a live tenant cannot be unit-hosted). "The other
/// instance" writes through a second repository over the same database file.
/// </summary>
public partial class ExoConnectionPoolSharedConfigTests : IDisposable
{
    private const string AppId1 = "11111111-1111-1111-1111-111111111111";
    private const string AppId2 = "22222222-2222-2222-2222-222222222222";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eaw-exopool-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConfigStore _store;
    private readonly ModuleConfigService _moduleConfig;
    private readonly ModuleEnablementService _enablement;
    private readonly IConfiguration _config;
    private readonly OperationTraceService _trace;
    private readonly List<ExoConnectionConfig> _connectedWith = new();

    public ExoConnectionPoolSharedConfigTests()
    {
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _dir
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_dir);

        _store = TestConfigStore.Create(_dir);
        var catalog = new ModuleCatalog();
        _moduleConfig = new ModuleConfigService(catalog, env, new ModuleConfigRepository(_store), Substitute.For<ILogger<ModuleConfigService>>());
        _enablement = new ModuleEnablementService(catalog, env, _moduleConfig, new ModuleEnablementRepository(_store), _config, Substitute.For<ILogger<ModuleEnablementService>>());
        _enablement.SaveEnablement(new Dictionary<string, bool> { [ExoConnectionPool.ConfigModuleKey] = true });
        _moduleConfig.SaveModuleConfig(ExoConnectionPool.ConfigModuleKey, Connection(AppId1, "contoso.onmicrosoft.com", "CN=EXO-Automation"));

        var jsonl = new JsonlLogService(_config, Substitute.For<ILogger<JsonlLogService>>());
        _trace = new OperationTraceService(_config, jsonl);
    }

    private static Dictionary<string, string> Connection(string appId, string org, string cert) => new()
    {
        [ExoConnectionPool.ConfigAppIdKey] = appId,
        [ExoConnectionPool.ConfigOrganizationKey] = org,
        [ExoConnectionPool.ConfigCertSubjectKey] = cert,
    };

    /// <summary>
    /// A logger that hands each formatted message to a callback. Audit finding A6 needs one:
    /// the pool logs from INSIDE the drain window, so this is the seam that lets a test hold a
    /// draining borrow there while a second borrow arrives.
    /// </summary>
    private sealed class CallbackLogger(Action<string> onMessage) : ILogger<ExoConnectionPool>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => onMessage(formatter(state, exception));
    }

    /// <summary>
    /// Audit finding A6B, and the review of its first fix. Builds a runspace that is actually
    /// OPEN and carries a stub <c>Disconnect-ExchangeOnline</c> recording the parameters it was
    /// given, so the disconnect the pool issues can be asserted as behaviour instead of scanned
    /// for in source. PowerShell resolves a function ahead of a cmdlet, so the pool's real call
    /// runs unmodified.
    /// </summary>
    private static PooledRunspace StubbedRunspace(long generation, string? connectionId, System.Collections.IList log)
    {
        var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        rs.SessionStateProxy.SetVariable("disconnectLog", log);

        using (var init = PowerShell.Create())
        {
            init.Runspace = rs;
            init.AddScript("""
                function Disconnect-ExchangeOnline {
                    param($ConnectionId, $Confirm)
                    $global:disconnectLog.Add("ConnectionId=$ConnectionId")
                }
                """);
            init.Invoke();
        }

        var ps = PowerShell.Create();
        ps.Runspace = rs;
        return new PooledRunspace(rs, ps, generation, connectionId);
    }

    private ExoConnectionPool NewPool(
        Action<ExoConnectionPool>? duringConnect = null,
        ILogger<ExoConnectionPool>? logger = null,
        Func<ExoConnectionConfig, long, CancellationToken, PooledRunspace>? connect = null)
    {
        if (connect is not null)
            return new ExoConnectionPool(_config, _moduleConfig, _enablement,
                logger ?? Substitute.For<ILogger<ExoConnectionPool>>(), _trace, connect);

        ExoConnectionPool? self = null;
        var pool = new ExoConnectionPool(_config, _moduleConfig, _enablement, logger ?? Substitute.For<ILogger<ExoConnectionPool>>(), _trace,
            connect: (cfg, generation, _) =>
            {
                _connectedWith.Add(cfg);
                duringConnect?.Invoke(self!);
                // An unopened runspace: DestroyRunspace's Disconnect-ExchangeOnline fails fast and is swallowed.
                var rs = RunspaceFactory.CreateRunspace();
                var ps = PowerShell.Create();
                ps.Runspace = rs;
                return new PooledRunspace(rs, ps, generation);
            });
        self = pool;
        return pool;
    }

    /// <summary>"The other instance": a separate store over the same database file.</summary>
    private void ForeignWrite(string moduleId, Dictionary<string, string> values)
        => new ModuleConfigRepository(TestConfigStore.Create(_dir)).SaveModule(moduleId, values);

    private static async Task<PooledRunspace> BorrowAndReturn(ExoConnectionPool pool)
    {
        var first = await pool.BorrowAsync(TestContext.Current.CancellationToken);
        pool.Return(first);
        return first;
    }

    [Theory]
    [InlineData(ExoConnectionPool.ConfigAppIdKey, AppId2)]
    [InlineData(ExoConnectionPool.ConfigOrganizationKey, "fabrikam.onmicrosoft.com")]
    [InlineData(ExoConnectionPool.ConfigCertSubjectKey, "CN=EXO-Automation-2")]
    public async Task ForeignWriteToConnectionField_PooledRunspaceIsNotReused(string key, string newValue)
    {
        using var pool = NewPool();
        var first = await BorrowAndReturn(pool);
        Assert.Equal(1, pool.AvailableCount);

        var changed = Connection(AppId1, "contoso.onmicrosoft.com", "CN=EXO-Automation");
        changed[key] = newValue;
        ForeignWrite(ExoConnectionPool.ConfigModuleKey, changed);

        var second = await pool.BorrowAsync(TestContext.Current.CancellationToken);

        Assert.NotSame(first, second);
        Assert.Equal(1, pool.ConfigGeneration);
        Assert.Equal(2, _connectedWith.Count);
        Assert.Equal(newValue, key switch
        {
            ExoConnectionPool.ConfigAppIdKey => _connectedWith[1].AppId,
            ExoConnectionPool.ConfigOrganizationKey => _connectedWith[1].Organization,
            _ => _connectedWith[1].CertificateSubject,
        });
        Assert.Equal(1, second.ConfigGeneration);
        pool.Return(second);
        Assert.Equal(1, pool.AvailableCount);
    }

    [Fact]
    public async Task ForeignWriteToAnotherModule_PooledRunspaceIsStillReused()
    {
        using var pool = NewPool();
        var first = await BorrowAndReturn(pool);

        ForeignWrite("MessageTrace", new Dictionary<string, string> { ["SomeSetting"] = "changed" });
        ForeignWrite("Intune", new Dictionary<string, string> { ["TenantId"] = Guid.NewGuid().ToString() });

        var second = await pool.BorrowAsync(TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal(0, pool.ConfigGeneration);
        Assert.Single(_connectedWith);
        pool.Return(second);
    }

    // The same values written again (a no-op save on the other instance) must not drain either:
    // the comparison is by value, not by "the database changed".
    [Fact]
    public async Task ForeignRewriteOfIdenticalConnection_PooledRunspaceIsStillReused()
    {
        using var pool = NewPool();
        var first = await BorrowAndReturn(pool);

        ForeignWrite(ExoConnectionPool.ConfigModuleKey, Connection(AppId1, "contoso.onmicrosoft.com", "CN=EXO-Automation"));

        var second = await pool.BorrowAsync(TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal(0, pool.ConfigGeneration);
        pool.Return(second);
    }

    [Fact]
    public async Task InProcessDrainPool_StillRetiresPooledRunspace()
    {
        using var pool = NewPool();
        var first = await BorrowAndReturn(pool);

        // The config page's save path: write, then DrainPool in this process.
        _moduleConfig.SaveModuleConfig(ExoConnectionPool.ConfigModuleKey, Connection(AppId2, "contoso.onmicrosoft.com", "CN=EXO-Automation"));
        pool.DrainPool();

        Assert.Equal(1, pool.ConfigGeneration);
        Assert.Equal(0, pool.AvailableCount);

        var second = await pool.BorrowAsync(TestContext.Current.CancellationToken);

        Assert.NotSame(first, second);
        Assert.Equal(AppId2, _connectedWith[1].AppId);
        // The in-process drain recorded the saved config, so the borrow did not drain a second time.
        Assert.Equal(1, pool.ConfigGeneration);
        pool.Return(second);
        Assert.Equal(1, pool.AvailableCount);
    }

    // A runspace connected under the config a drain retired must come back stale, even when the
    // drain landed while Connect-ExchangeOnline was still running: the generation is read before
    // the connect, not stamped after it.
    [Fact]
    public async Task DrainDuringConnect_LeavesTheNewRunspaceStale()
    {
        var drained = false;
        using var pool = NewPool(duringConnect: p =>
        {
            if (drained) return;
            drained = true;
            p.DrainPool();
        });

        var created = await pool.BorrowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, created.ConfigGeneration);
        Assert.Equal(1, pool.ConfigGeneration);

        pool.Return(created);
        Assert.Equal(0, pool.AvailableCount);
    }

    // Audit finding A6. The drain publishes the new connection identity and retires the
    // generation; it used to do them as two steps with the lock released in between. A borrow
    // arriving in that gap read the NEW identity - so it decided no drain was needed - and then
    // read the OLD generation, which a runspace connected to the PREVIOUS organization still
    // matched. It was handed out, and its commands ran against the wrong tenant.
    //
    // This drives that exact interleaving through the pool's own log call, which sits inside
    // the old window.
    [Fact]
    public async Task ABorrowArrivingDuringTheDrain_NeverGetsTheOldOrganizationsRunspace()
    {
        using var reachedWindow = new SemaphoreSlim(0, 1);
        using var secondFinished = new SemaphoreSlim(0, 1);
        var heldTheWindow = false;

        var logger = new CallbackLogger(message =>
        {
            if (!message.Contains("connection config changed", StringComparison.Ordinal)) return;
            reachedWindow.Release();
            // Hold the draining borrow here, between publishing the identity and destroying the
            // pooled runspaces, for as long as the second borrow needs. The result is asserted
            // below: review of aud-a6 noted that letting this time out silently would let the
            // buggy ordering escape the schedule and pass.
            heldTheWindow = secondFinished.Wait(TimeSpan.FromSeconds(30));
        });

        using var pool = NewPool(logger: logger);
        var first = await BorrowAndReturn(pool);
        Assert.Equal(1, pool.AvailableCount);

        ForeignWrite(ExoConnectionPool.ConfigModuleKey,
            Connection(AppId1, "fabrikam.onmicrosoft.com", "CN=EXO-Automation"));

        // Captured out here: TestContext.Current is async-local and does not reliably flow into
        // the Task.Run body.
        var ct = TestContext.Current.CancellationToken;
        var drainer = Task.Run(async () => await pool.BorrowAsync(ct), ct);

        Assert.True(await reachedWindow.WaitAsync(TimeSpan.FromSeconds(30), ct),
            "The draining borrow never reached the window - this test is stale.");

        PooledRunspace second;
        try
        {
            second = await pool.BorrowAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            secondFinished.Release();
        }

        var drainersRunspace = await drainer;

        Assert.True(heldTheWindow,
            "The draining borrow's wait timed out, so the second borrow did not actually run inside the window - this result proves nothing.");

        // The defect: `second` was `first`, still connected to contoso.
        Assert.NotSame(first, second);
        Assert.Equal(1, second.ConfigGeneration);
        Assert.Equal("fabrikam.onmicrosoft.com", _connectedWith[1].Organization);

        pool.Return(second);
        pool.Return(drainersRunspace);
    }

    // Review of aud-a6, the second half. Pairing the config with the generation is only worth
    // anything if the end result holds: a runspace connected to one organization must never
    // become reusable by a borrower of another. Here the switch happens while the first connect
    // is still running, which is the widest version of that window.
    [Fact]
    public async Task AConnectionMadeForOneOrganization_IsNeverPooledForAnother()
    {
        var switched = false;
        using var pool = NewPool(duringConnect: p =>
        {
            if (switched) return;
            switched = true;

            // The other instance repoints the tenant, and another borrow notices it and retires
            // the generation - all while the first connect is still in flight.
            ForeignWrite(ExoConnectionPool.ConfigModuleKey,
                Connection(AppId2, "fabrikam.onmicrosoft.com", "CN=EXO-Automation"));
            var other = p.BorrowAsync(CancellationToken.None).GetAwaiter().GetResult();
            p.Return(other);
        });

        var contosoRunspace = await pool.BorrowAsync(TestContext.Current.CancellationToken);
        pool.Return(contosoRunspace);

        // Only the fabrikam runspace is poolable; the contoso one was destroyed on return.
        Assert.Equal(1, pool.AvailableCount);
        Assert.Equal(1, pool.ConfigGeneration);

        var next = await pool.BorrowAsync(TestContext.Current.CancellationToken);
        Assert.NotSame(contosoRunspace, next);
        Assert.Equal("fabrikam.onmicrosoft.com", _connectedWith[1].Organization);
        pool.Return(next);
    }

    /// <summary>
    /// Review of aud-a6. This one IS a source scan, and deliberately so: the defect needs a
    /// borrow paused between comparing the config and reading the generation, and once those are
    /// a single call there is no seam left in that gap to pause at. Nothing executable can
    /// distinguish "uses the generation it was handed" from "re-reads it" without one.
    ///
    /// So the property is pinned where it lives - BorrowAsync must take the generation from the
    /// call that compared the config, and must not read it again. Return legitimately does read
    /// the live generation and is not in scope here.
    /// </summary>
    [Fact]
    public void BorrowAsync_TakesTheGenerationFromTheConfigComparison_AndNeverRereadsIt()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Services", "ExoConnectionPool.cs"));

        var start = text.IndexOf("public async Task<PooledRunspace> BorrowAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "BorrowAsync not found - this tripwire is stale.");
        var end = text.IndexOf("\n    public void Return(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound BorrowAsync - this tripwire is stale.");

        var code = ProgressScan.CodeView(text[start..end]);

        Assert.Contains("= DrainIfConnectionConfigChanged(current);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Interlocked.Read(ref _configGeneration)", code, StringComparison.Ordinal);
    }

    // Review of the A6B fix, finding 4. The source scans it shipped with passed against a
    // foreign selector value and against a disconnect moved outside its guard - the reviewer
    // reproduced both. These drive the real call instead.

    [Fact]
    public async Task TheDisconnectNamesThisRunspacesOwnConnection()
    {
        var log = new System.Collections.ArrayList();
        using var pool = NewPool(connect: (_, generation, _) => StubbedRunspace(generation, "conn-abc", log));

        var borrowed = await pool.BorrowAsync(TestContext.Current.CancellationToken);
        pool.Return(borrowed);
        pool.DrainPool();

        // Not just "a ConnectionId was passed" - the right one. A foreign value satisfied the
        // scan this replaces.
        Assert.Equal(["ConnectionId=conn-abc"], log.Cast<string>());
    }

    [Fact]
    public async Task ARunspaceWithNoKnownConnectionIdIsNeverDisconnected()
    {
        // The whole point of the fallback: with no id there is no safe selector, and the
        // unselected form would disconnect every other operator in this process.
        var log = new System.Collections.ArrayList();
        using var pool = NewPool(connect: (_, generation, _) => StubbedRunspace(generation, null, log));

        var borrowed = await pool.BorrowAsync(TestContext.Current.CancellationToken);
        pool.Return(borrowed);
        pool.DrainPool();

        Assert.Empty(log);
    }

    [Fact]
    public async Task DrainIfConnectionConfigChanged_FirstCallRecordsWithoutDraining()
    {
        using var pool = NewPool();

        Assert.Equal((false, 0L), pool.DrainIfConnectionConfigChanged(new ExoConnectionConfig(AppId1, "contoso.onmicrosoft.com", "CN=EXO-Automation")));
        Assert.Equal((false, 0L), pool.DrainIfConnectionConfigChanged(new ExoConnectionConfig(AppId1, "contoso.onmicrosoft.com", "CN=EXO-Automation")));
        // The generation comes back from the same call that compared the config, so a caller can
        // never pair one organization's identity with another's generation (aud-a6 review).
        Assert.Equal((true, 1L), pool.DrainIfConnectionConfigChanged(new ExoConnectionConfig(AppId1, "contoso.onmicrosoft.com", "CN=Other")));
        Assert.Equal((false, 1L), pool.DrainIfConnectionConfigChanged(new ExoConnectionConfig(AppId1, "contoso.onmicrosoft.com", "CN=Other")));
        Assert.Equal(1, pool.ConfigGeneration);
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
