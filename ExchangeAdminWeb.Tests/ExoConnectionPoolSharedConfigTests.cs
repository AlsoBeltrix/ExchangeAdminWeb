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
public class ExoConnectionPoolSharedConfigTests : IDisposable
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

    private ExoConnectionPool NewPool(Action<ExoConnectionPool>? duringConnect = null)
    {
        ExoConnectionPool? self = null;
        var pool = new ExoConnectionPool(_config, _moduleConfig, _enablement, Substitute.For<ILogger<ExoConnectionPool>>(), _trace,
            connect: (cfg, generation) =>
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

    [Fact]
    public async Task DrainIfConnectionConfigChanged_FirstCallRecordsWithoutDraining()
    {
        using var pool = NewPool();

        Assert.False(pool.DrainIfConnectionConfigChanged(new ExoConnectionConfig(AppId1, "contoso.onmicrosoft.com", "CN=EXO-Automation")));
        Assert.False(pool.DrainIfConnectionConfigChanged(new ExoConnectionConfig(AppId1, "contoso.onmicrosoft.com", "CN=EXO-Automation")));
        Assert.True(pool.DrainIfConnectionConfigChanged(new ExoConnectionConfig(AppId1, "contoso.onmicrosoft.com", "CN=Other")));
        Assert.False(pool.DrainIfConnectionConfigChanged(new ExoConnectionConfig(AppId1, "contoso.onmicrosoft.com", "CN=Other")));
        Assert.Equal(1, pool.ConfigGeneration);
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
