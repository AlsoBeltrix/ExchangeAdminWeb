using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28x: the Exchange Online pool read each connection setting through a read that takes a failure for an absent
/// value, and an absent value falls back to appsettings. A read that failed while the probe after it answered therefore
/// connected under the appsettings tenant, app id or certificate instead of the module config's - a fallback the
/// Constitution allows only when the module config is absent, never when it cannot be read.
/// </summary>
/// <remarks>
/// A real <see cref="ExoConnectionPool"/> over a temp database: Exchange Online enabled on its own store, the
/// ExchangeOnline rows read through a store that fails as each case needs, and appsettings holding a different tenant's
/// values, so a value that came from appsettings shows. The connect seam records the config it is given and refuses, so
/// no session can open. Nothing here reaches Exchange.
/// </remarks>
public sealed class ExoConnectionPoolConfigReadTests : IDisposable
{
    private const string RowsAppId = "11111111-1111-1111-1111-111111111111";
    private const string RowsOrganization = "rows.onmicrosoft.com";
    private const string RowsCertificate = "CN=Rows-Certificate";
    private const string AppsettingsAppId = "22222222-2222-2222-2222-222222222222";
    private const string AppsettingsOrganization = "appsettings.onmicrosoft.com";
    private const string AppsettingsCertificate = "CN=Appsettings-Certificate";

    private static readonly ExoConnectionConfig Rows = new(RowsAppId, RowsOrganization, RowsCertificate);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eaw-exocfg-" + Guid.NewGuid().ToString("N"));
    private readonly IWebHostEnvironment _env = Substitute.For<IWebHostEnvironment>();
    private readonly IConfiguration _config;
    private readonly OperationTraceService _trace;
    private readonly List<ExoConnectionConfig> _connectedWith = [];
    private readonly List<ExoConnectionPool> _pools = [];

    public ExoConnectionPoolConfigReadTests()
    {
        Directory.CreateDirectory(_dir);
        _env.ContentRootPath.Returns(_dir);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _dir,
            ["ExchangeOnline:AppId"] = AppsettingsAppId,
            ["ExchangeOnline:Organization"] = AppsettingsOrganization,
            ["ExchangeOnline:CertificateSubject"] = AppsettingsCertificate,
        }).Build();
        _trace = new OperationTraceService(_config, new JsonlLogService(_config, NullLogger<JsonlLogService>.Instance));

        var store = TestConfigStore.Create(_dir);
        new ModuleEnablementService(new ModuleCatalog(), _env,
                new ModuleConfigService(new ModuleCatalog(), _env, new ModuleConfigRepository(store), NullLogger<ModuleConfigService>.Instance),
                new ModuleEnablementRepository(store), _config, NullLogger<ModuleEnablementService>.Instance)
            .SaveEnablement(new Dictionary<string, bool> { [ExoConnectionPool.ConfigModuleKey] = true });
    }

    public void Dispose()
    {
        foreach (var pool in _pools)
            pool.Dispose();

        SqliteConnection.ClearPool(new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "exchangeadmin.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString()));
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort: a pooled connection can still hold the file */ }
    }

    /// <summary>The ExchangeOnline rows, written past every failing store; a null value leaves its key out.</summary>
    private void SeedRows(string? appId, string? organization, string? certificate)
    {
        var values = new Dictionary<string, string>();
        if (appId is not null) values[ExoConnectionPool.ConfigAppIdKey] = appId;
        if (organization is not null) values[ExoConnectionPool.ConfigOrganizationKey] = organization;
        if (certificate is not null) values[ExoConnectionPool.ConfigCertSubjectKey] = certificate;
        new ModuleConfigRepository(TestConfigStore.Create(_dir)).UpdateModule(ExoConnectionPool.ConfigModuleKey, values);
    }

    /// <summary>A real pool whose ExchangeOnline rows are read through <paramref name="rowsStore"/>.</summary>
    private ExoConnectionPool PoolOver(IConfigStore rowsStore, ILogger<ExoConnectionPool>? logger = null)
    {
        var moduleConfig = new ModuleConfigService(
            new ModuleCatalog(), _env, new ModuleConfigRepository(rowsStore), NullLogger<ModuleConfigService>.Instance);
        var enablement = new ModuleEnablementService(
            new ModuleCatalog(), _env, moduleConfig, new ModuleEnablementRepository(TestConfigStore.Create(_dir)), _config,
            NullLogger<ModuleEnablementService>.Instance);
        var pool = new ExoConnectionPool(_config, moduleConfig, enablement, logger ?? NullLogger<ExoConnectionPool>.Instance, _trace,
            connect: (config, _, _) =>
            {
                lock (_connectedWith)
                    _connectedWith.Add(config);
                throw new InvalidOperationException("the connect seam refuses: no live call from a test");
            });
        _pools.Add(pool);
        return pool;
    }

    private static Task<InvalidOperationException> RefusedBySeam(ExoConnectionPool pool) =>
        Assert.ThrowsAsync<InvalidOperationException>(() => pool.BorrowAsync(TestContext.Current.CancellationToken));

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    public async Task RowsThatCannotBeRead_RefuseTheBorrow_AndNothingComesFromAppsettings(int failing)
    {
        // The rows cannot be read at the borrow's read - from then on, or that read only, as a confined fault would. No
        // slot is taken and nothing is connected: under appsettings least of all.
        SeedRows(RowsAppId, RowsOrganization, RowsCertificate);
        var logger = new CapturingLogger();
        var pool = PoolOver(new FaultyStore(TestConfigStore.Create(_dir), answered: 0, failing), logger);

        var refused = await Assert.ThrowsAsync<ExoConfigUnreadableException>(() => pool.BorrowAsync(TestContext.Current.CancellationToken));

        Assert.Contains(SettingsStoreText.ModuleConfig(ExoConnectionPool.ConfigModuleKey), refused.Message, StringComparison.Ordinal);
        Assert.Empty(_connectedWith);
        Assert.Single(logger.Errors);
    }

    [Fact]
    public async Task AReadableBorrow_ConnectsUnderTheRows_FromOneRead()
    {
        // Every read after the borrow's one fails: a borrow that read its settings once, as the rows hold them, never asks
        // again and never mixes in an appsettings value.
        SeedRows(RowsAppId, RowsOrganization, RowsCertificate);
        var store = new FaultyStore(TestConfigStore.Create(_dir), answered: 1);
        var pool = PoolOver(store);

        await RefusedBySeam(pool);

        Assert.Equal(Rows, Assert.Single(_connectedWith));
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task NoRows_ConnectUnderAppsettings_TheUpgradeFallback()
    {
        // The control: the module config is absent, which is when the Constitution allows the legacy fallback.
        var pool = PoolOver(TestConfigStore.Create(_dir));

        await RefusedBySeam(pool);

        Assert.Equal(new ExoConnectionConfig(AppsettingsAppId, AppsettingsOrganization, AppsettingsCertificate), Assert.Single(_connectedWith));
    }

    [Fact]
    public async Task AKeyTheRowsDoNotHold_ComesFromAppsettings_TheRestFromTheRows()
    {
        SeedRows(RowsAppId, RowsOrganization, certificate: null);
        var pool = PoolOver(TestConfigStore.Create(_dir));

        await RefusedBySeam(pool);

        Assert.Equal(new ExoConnectionConfig(RowsAppId, RowsOrganization, AppsettingsCertificate), Assert.Single(_connectedWith));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    public void IsConfigured_OverRowsThatCannotBeRead_IsFalse_AndDoesNotThrow(int failing)
    {
        // A bool for the callers that cannot show more. It used to answer an unreadable value from appsettings - so
        // "configured", under another tenant's values - or throw, when the presence read after it failed too.
        SeedRows(RowsAppId, RowsOrganization, RowsCertificate);

        Assert.False(PoolOver(new FaultyStore(TestConfigStore.Create(_dir), answered: 0, failing)).IsConfigured);
        Assert.False(PoolOver(new FaultyStore(TestConfigStore.Create(_dir), answered: 0, failing)).TryGetIsConfigured(out _));
        Assert.True(PoolOver(TestConfigStore.Create(_dir)).IsConfigured);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    public void DrainPool_OverRowsThatCannotBeRead_FailsBeforeItRetiresAnything(int failing)
    {
        // The drain follows a save on this instance, and the Exchange Online Config page reports a drain that throws as
        // its own failed step, not a failed save (SettingsStoreTextTests). Unable to read what the next borrow will
        // connect under, it records nothing - least of all appsettings' values in their place - and retires nothing;
        // the next borrow that reads the rows retires whatever was connected under other values (scdi-1).
        SeedRows(RowsAppId, RowsOrganization, RowsCertificate);
        var pool = PoolOver(new FaultyStore(TestConfigStore.Create(_dir), answered: 0, failing));
        var generation = pool.ConfigGeneration;

        var refused = Assert.Throws<ExoConfigUnreadableException>(pool.DrainPool);

        Assert.Contains(SettingsStoreText.ModuleConfig(ExoConnectionPool.ConfigModuleKey), refused.Message, StringComparison.Ordinal);
        Assert.Equal(generation, pool.ConfigGeneration);
    }

    [Fact]
    public void ThePool_ReadsNoAppsettingsConnectionValueItself_TheOneRuleDoes()
    {
        // aud-a26s moved the fallback rule into ExoConfigFallback and pinned it to what the pool connects under
        // (ExoConfigFallbackTests); folded into aud-a28x, the pool calls it rather than keep a copy. Comments blanked.
        var code = RazorSyntax.CommentsBlanked(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "ExoConnectionPool.cs")));

        Assert.DoesNotContain("\"ExchangeOnline:", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CN=EXO-Automation", code, StringComparison.Ordinal);
        Assert.Equal(3, code.Split("ExoConfigFallback.Resolve(").Length - 1);
    }

    [Fact]
    public async Task TheResolver_OverRowsThatCannotBeRead_SaysSo_NotThatExchangeIsNotConfigured()
    {
        SeedRows(RowsAppId, RowsOrganization, RowsCertificate);
        var pool = PoolOver(new FaultyStore(TestConfigStore.Create(_dir)) { Down = true });
        var resolver = new ExchangeIdentityResolver(pool, null!, NullLogger<ExchangeIdentityResolver>.Instance, _config);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveRecipientAsync("anyone@contoso.test"));

        Assert.Equal(
            "Exchange Online's module enablement or its module_config rows could not be read - cannot resolve recipient.",
            refused.Message);
        Assert.Empty(_connectedWith);
    }

    [Fact]
    public async Task TheResolver_OverExchangeOnlineReadablyNotConfigured_SaysNotConfigured()
    {
        SeedRows(appId: "", RowsOrganization, RowsCertificate);
        var pool = PoolOver(TestConfigStore.Create(_dir));
        var resolver = new ExchangeIdentityResolver(pool, null!, NullLogger<ExchangeIdentityResolver>.Instance, _config);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveRecipientAsync("anyone@contoso.test"));

        Assert.Equal("Exchange Online is not configured - cannot resolve recipient.", refused.Message);
        Assert.Empty(_connectedWith);
    }

    /// <summary>
    /// Answers its first <paramref name="answered"/> reads, fails the next <paramref name="failing"/>, and answers every
    /// later one - and fails every read while <see cref="Down"/> is set. Writes pass. aud-a28t's fixture, kept per class.
    /// </summary>
    private sealed class FaultyStore(IConfigStore inner, int answered = int.MaxValue, int failing = int.MaxValue) : IConfigStore
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public bool Down { get; set; }

        public long GetChangeToken() => inner.GetChangeToken();

        public T Read<T>(Func<SqliteConnection, T> read)
        {
            var ordinal = Interlocked.Increment(ref _reads);
            return Down || (ordinal > answered && ordinal - answered <= failing)
                ? throw new InvalidOperationException("config store unreadable")
                : inner.Read(read);
        }

        public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write) => inner.Write(write);

        public void Write(Action<SqliteConnection, SqliteTransaction> write) => inner.Write(write);
    }

    /// <summary>Keeps the text of every warning and error the pool logs.</summary>
    private sealed class CapturingLogger : ILogger<ExoConnectionPool>
    {
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var list = logLevel switch
            {
                LogLevel.Warning => Warnings,
                LogLevel.Error => Errors,
                _ => null,
            };
            if (list is null)
                return;

            lock (list)
                list.Add(formatter(state, exception));
        }
    }
}
