using System.Security.Claims;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28t: two more reads on the permission gate took a store they could not read for an answer. The group
/// expansion asked <see cref="ExoConnectionPool.IsConfigured"/>, which reads a failure as "not configured", so a listed
/// group stayed a name and its members were unprotected for the cache lifetime; and the self-grant setting fell back
/// to appsettings when its read failed, so a host whose appsettings says false allowed self-grants.
/// </summary>
/// <remarks>
/// A real <see cref="PermissionValidator"/> whose module config and Exchange pool share one
/// <see cref="ModuleConfigService"/>, as they do in the app, over <see cref="ExchangeWriteHarness"/>'s database file -
/// where Exchange Online is enabled and configured - through a store that answers a set number of reads, fails a set
/// number, and answers again. The enablement sits on its own store over the same file, the protected-principal and
/// section-access stores hold nothing, Exchange answers through a stand-in resolver, and the pool's connect seam
/// counts and throws, so no case here can open a session. Nothing reaches Exchange or a directory.
/// </remarks>
public sealed class PermissionValidatorConfigReadTests : IDisposable
{
    private const string Group = "vips@contoso.test";
    private const string Member = "member@contoso.test";
    private const string ModuleId = "MailboxPermissions";
    private const string Self = @"CONTOSO\jdoe";
    private const string SelfAddress = "jdoe@contoso.test";
    private const string SelfAlias = "john.doe@contoso.test";
    private static readonly Guid MemberMailbox = Guid.Parse("0e0e0e0e-1111-4222-8333-555555555555");

    private const string ListUnavailable = "Access denied: Protected-user list is unavailable. Contact your administrator.";
    private const string SelfGrantRefusal = "Access denied: You cannot grant permissions to yourself.";

    private readonly ExchangeWriteHarness _h = new();
    private readonly IWebHostEnvironment _env = Substitute.For<IWebHostEnvironment>();
    private readonly ExtendedLogService _extendedLog;
    private readonly DelineaService _delinea;
    private readonly List<ExoConnectionPool> _pools = [];
    private readonly ClaimsPrincipal _self;
    private int _connects;

    public PermissionValidatorConfigReadTests()
    {
        _env.ContentRootPath.Returns(_h.Dir);
        _h.AddMailbox(Member, MemberMailbox);

        // aud-a43: the self-grant check is handed the signed-in principal and finds the operator in the directory.
        var account = _h.Forest.AddUser("jdoe", upn: SelfAddress, mail: SelfAddress, proxies: [$"SMTP:{SelfAddress}"]);
        _self = SelfGrantDirectoryStandIn.Principal(Self, account.Sid);

        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new NoNetwork()));
        _extendedLog = new ExtendedLogService(
            _h.Config, _env, TestConfigStore.CreateAppSettings(_h.Dir), NullLogger<ExtendedLogService>.Instance);
        _delinea = new DelineaService(http, _h.Config, NullLogger<DelineaService>.Instance, _extendedLog, _h.Trace);
    }

    public void Dispose()
    {
        foreach (var pool in _pools)
            pool.Dispose();
        _extendedLog.Dispose();
        _h.Dispose();
    }

    private int Connects => Volatile.Read(ref _connects);

    /// <summary>Writes a Mailbox Permissions key past every failing store.</summary>
    private void SeedMailboxPermissions(string key, string value) =>
        new ModuleConfigRepository(TestConfigStore.Create(_h.Dir)).UpdateModule(
            ModuleId, new Dictionary<string, string> { [key] = value });

    private IConfiguration ConfigWith(string? preventSelfGrant)
    {
        var values = new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _h.Dir,
            ["Delinea:SecretServerUrl"] = "https://vault.invalid",
        };
        if (preventSelfGrant is not null)
            values["Security:PreventSelfGrant"] = preventSelfGrant;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>
    /// A real validator whose module config - its own reads and its pool's - goes through
    /// <paramref name="moduleConfigStore"/>, and whose pool reads enablement through <paramref name="enablementStore"/>.
    /// Exchange answers through <paramref name="resolverFor"/>, given that pool, or a stand-in that knows the member.
    /// </summary>
    private PermissionValidator ValidatorOver(IConfigStore moduleConfigStore, IConfigStore? enablementStore = null,
        string? preventSelfGrant = null, Func<ExoConnectionPool, IConfiguration, IIdentityResolver>? resolverFor = null,
        ILogger<PermissionValidator>? logger = null)
    {
        var config = ConfigWith(preventSelfGrant);
        var moduleConfig = new ModuleConfigService(
            new ModuleCatalog(), _env, new ModuleConfigRepository(moduleConfigStore), NullLogger<ModuleConfigService>.Instance);
        var enablement = new ModuleEnablementService(
            new ModuleCatalog(), _env, moduleConfig,
            new ModuleEnablementRepository(enablementStore ?? TestConfigStore.Create(_h.Dir)), config,
            NullLogger<ModuleEnablementService>.Instance);
        var pool = new ExoConnectionPool(config, moduleConfig, enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace,
            connect: (_, _, _) =>
            {
                Interlocked.Increment(ref _connects);
                throw new InvalidOperationException("this test opens no Exchange session");
            });
        _pools.Add(pool);

        var protection = new ProtectedPrincipalService(
            _env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_h.Dir), _delinea,
            NullLogger<ProtectedPrincipalService>.Instance);
        var servicers = new ProtectedPrincipalServicerService(
            new SectionAccessService(config, NullLogger<SectionAccessService>.Instance, _env, new ModuleCatalog(),
                new SectionAccessRepository(TestConfigStore.Create(_h.Dir))),
            NullLogger<ProtectedPrincipalServicerService>.Instance);

        var resolver = resolverFor?.Invoke(pool, config) ?? ExchangeWriteHarness.ExchangeKnows(Member, Member, MemberMailbox);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(IIdentityResolver)).Returns(resolver);

        return new PermissionValidator(config, moduleConfig, pool, protection, servicers,
            logger ?? NullLogger<PermissionValidator>.Instance, scopeFactory)
        {
            SelfGrantDirectoryRunspace = _h.Forest.Open,
        };
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    public async Task AStoreThatFailsTheExchangeOnlineRead_RefusesEveryValidation_NotNamesOnly(int failing)
    {
        // The store answers the ExcludedUsers read and fails the Exchange Online read after it - from then on, or that
        // read only, as a confined failure would. Either way the listed group cannot be expanded, so the load fails and
        // every target is refused; the group's member is never let through as unprotected.
        SeedMailboxPermissions("ExcludedUsers", Group);
        var validator = ValidatorOver(new FailsAfterReadsStore(TestConfigStore.Create(_h.Dir), answered: 1, failing));

        var result = await validator.ValidateTargetMailboxAsync(Member, actingUser: null, ModuleId);

        Assert.Equal(ListUnavailable, result.Error);
        Assert.Null(result.Mailbox);
        Assert.True(await validator.IsUserExcludedAsync(Member));
        Assert.Equal(0, Connects);
    }

    [Fact]
    public async Task AnEnablementStoreThatCannotBeRead_RefusesEveryValidation_NotNamesOnly()
    {
        // An unreadable enablement store reads as every module disabled - here, as Exchange Online not configured.
        SeedMailboxPermissions("ExcludedUsers", Group);
        var validator = ValidatorOver(TestConfigStore.Create(_h.Dir),
            enablementStore: new FailsAfterReadsStore(TestConfigStore.Create(_h.Dir), answered: 0));

        var result = await validator.ValidateTargetMailboxAsync(Member, actingUser: null, ModuleId);

        Assert.Equal(ListUnavailable, result.Error);
        Assert.Null(result.Mailbox);
        Assert.Equal(0, Connects);
    }

    [Theory]
    [InlineData("AppId blank")]
    [InlineData("module disabled")]
    public async Task ExchangeOnlineReadablyNotConfigured_StillKeepsTheEntryALiteral(string shape)
    {
        // The control: the refusals above are about stores that cannot be read. An Exchange Online that reads as not
        // configured keeps a listed entry a name, as before - the name refused, everything else allowed and bound.
        SeedMailboxPermissions("ExcludedUsers", Group);
        if (shape == "AppId blank")
        {
            new ModuleConfigRepository(TestConfigStore.Create(_h.Dir)).UpdateModule(
                ExoConnectionPool.ConfigModuleKey, new Dictionary<string, string> { [ExoConnectionPool.ConfigAppIdKey] = "" });
        }
        else
        {
            _h.Enablement.SetModulesEnabled(new Dictionary<string, bool> { [ExoConnectionPool.ConfigModuleKey] = false }, TestContext.Current.CancellationToken);
        }

        var validator = ValidatorOver(TestConfigStore.Create(_h.Dir));

        var member = await validator.ValidateTargetMailboxAsync(Member, actingUser: null, ModuleId);
        Assert.Null(member.Error);
        Assert.Equal(MemberMailbox, member.Mailbox?.ExchangeGuid);

        var listed = await validator.ValidateTargetMailboxAsync(Group, actingUser: null, ModuleId);
        Assert.Equal($"Access denied: {Group} is protected and cannot be modified through this interface.", listed.Error);
        Assert.Equal(0, Connects);
    }

    [Fact]
    public async Task AModuleConfigThatCannotBeRead_RefusesASelfGrant_EvenWithAppsettingsFalse()
    {
        // Appsettings is a fallback for a setting that is absent, not for one that could not be read.
        var validator = ValidatorOver(new FailsAfterReadsStore(TestConfigStore.Create(_h.Dir), answered: 0),
            preventSelfGrant: "false");

        Assert.Equal(SelfGrantRefusal, await validator.ValidateSelfGrantAsync(_self, SelfAddress));
    }

    [Theory]
    [InlineData(null, "false", false)]
    [InlineData(null, null, true)]
    [InlineData("", "false", false)]
    [InlineData("false", null, false)]
    [InlineData("true", "false", true)]
    [InlineData("yes", "false", true)]
    [InlineData(null, "yes", true)]
    [InlineData("", "yes", true)]
    [InlineData(null, " ", true)]
    public async Task TheSelfGrantSetting_FallsBackToAppsettings_OnlyWhenTheModuleConfigHoldsNoValue(
        string? stored, string? appsettings, bool refused)
    {
        // Over a readable store: no value (absent or blank) is appsettings' to answer, default on; a stored value wins
        // over appsettings either way; a stored value that is neither true nor false is present, so it is not
        // appsettings' to answer, and it refuses. aud-a28y: an appsettings value that is neither refuses too, where it
        // used to throw from every check.
        if (stored is not null)
            SeedMailboxPermissions("PreventSelfGrant", stored);
        var validator = ValidatorOver(TestConfigStore.Create(_h.Dir), preventSelfGrant: appsettings);

        var expected = refused ? SelfGrantRefusal : null;
        Assert.Equal(expected, await validator.ValidateSelfGrantAsync(_self, SelfAddress));
    }

    [Fact]
    public async Task AModuleConfigThatCannotBeRead_LogsTheRowsAndTheirFix_WithNoPageToReload()
    {
        // aud-a26t. The unreadable self-grant setting's log line (aud-a28t) names the rows and their fix as the admin
        // pages do, without their reload clause: it is a service's log line, and no page is involved.
        var logger = new CapturingLogger();
        var validator = ValidatorOver(new FailsAfterReadsStore(TestConfigStore.Create(_h.Dir), answered: 0), logger: logger);

        Assert.Equal(SelfGrantRefusal, await validator.ValidateSelfGrantAsync(_self, SelfAddress));

        var error = Assert.Single(logger.Errors);
        Assert.Contains(SettingsStoreText.ServiceModuleConfig(ModuleId), error, StringComparison.Ordinal);
        Assert.DoesNotContain("reload this page", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAppsettingsValueThatIsNeitherTrueNorFalse_IsNamedInTheLog()
    {
        // A value of this test's own: the warning is written once per process for each value (aud-a43), and the table
        // above refuses on "yes" in the same process.
        var value = "yes-" + Guid.NewGuid().ToString("N");
        var logger = new CapturingLogger();
        var validator = ValidatorOver(TestConfigStore.Create(_h.Dir), preventSelfGrant: value, logger: logger);

        Assert.Equal(SelfGrantRefusal, await validator.ValidateSelfGrantAsync(_self, SelfAddress));

        var warning = Assert.Single(logger.Warnings, w => w.Contains("Security:PreventSelfGrant", StringComparison.Ordinal));
        Assert.Contains($"'{value}'", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAppsettingsValueThatIsNeitherTrueNorFalse_IsNamedOnce_NotOnEveryValidation()
    {
        // aud-a43, folded in (codex perm-batch-4). A typo in appsettings is static: it was named on every self-grant check,
        // one warning per row of a bulk upload. It still refuses every time, and is named once per process - so a second
        // validator over the same value says nothing either.
        var value = "maybe-" + Guid.NewGuid().ToString("N");
        var first = new CapturingLogger();
        var second = new CapturingLogger();
        var validator = ValidatorOver(TestConfigStore.Create(_h.Dir), preventSelfGrant: value, logger: first);
        var another = ValidatorOver(TestConfigStore.Create(_h.Dir), preventSelfGrant: value, logger: second);

        Assert.Equal(SelfGrantRefusal, await validator.ValidateSelfGrantAsync(_self, SelfAddress));
        Assert.Equal(SelfGrantRefusal, await validator.ValidateSelfGrantAsync(_self, SelfAddress));
        Assert.Equal(SelfGrantRefusal, await another.ValidateSelfGrantAsync(_self, SelfAddress));

        Assert.Single(first.Warnings, w => w.Contains("Security:PreventSelfGrant", StringComparison.Ordinal));
        Assert.DoesNotContain(second.Warnings, w => w.Contains("Security:PreventSelfGrant", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStoredValueThatIsNeitherTrueNorFalse_IsNamedOnce_NotOnEveryValidation()
    {
        // aud-a50, part 2. A stored value that is neither true nor false was named on every self-grant check - one warning per
        // row of a bulk upload - where the appsettings one is named once (aud-a43). It still refuses every time, is named
        // once per process for each value, so a second validator over it says nothing, and a new value is named in its turn.
        const string Named = "The self-grant setting holds";
        var value = "maybe-" + Guid.NewGuid().ToString("N");
        SeedMailboxPermissions("PreventSelfGrant", value);
        var first = new CapturingLogger();
        var second = new CapturingLogger();
        var validator = ValidatorOver(TestConfigStore.Create(_h.Dir), logger: first);
        var another = ValidatorOver(TestConfigStore.Create(_h.Dir), logger: second);

        Assert.Equal(SelfGrantRefusal, await validator.ValidateSelfGrantAsync(_self, SelfAddress));
        Assert.Equal(SelfGrantRefusal, await validator.ValidateSelfGrantAsync(_self, SelfAddress));
        Assert.Equal(SelfGrantRefusal, await another.ValidateSelfGrantAsync(_self, SelfAddress));

        var warning = Assert.Single(first.Warnings, w => w.Contains(Named, StringComparison.Ordinal));
        Assert.Contains($"'{value}'", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(second.Warnings, w => w.Contains(Named, StringComparison.Ordinal));

        var next = "perhaps-" + Guid.NewGuid().ToString("N");
        SeedMailboxPermissions("PreventSelfGrant", next);
        Assert.Equal(SelfGrantRefusal, await validator.ValidateSelfGrantAsync(_self, SelfAddress));
        Assert.Equal(SelfGrantRefusal, await validator.ValidateSelfGrantAsync(_self, SelfAddress));
        Assert.Single(first.Warnings, w => w.Contains($"'{next}'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExchangeOnlineRowsThatCannotBeRead_RefuseASelfGrant_AsACheckThatCouldNotBeCompleted()
    {
        // aud-a28y. The store answers the self-grant setting's read and fails every later one: the Exchange Online rows
        // the real resolver's pool reads to put the two names to Exchange. The lookup cannot run, so the comparison the
        // string match leaves to it cannot be made, and the grant is refused rather than cleared. Nothing is connected.
        // aud-a43: the operator is put to Exchange as the directory's UPN for their SID, so that is the name refused on.
        var validator = ValidatorOver(new FailsAfterReadsStore(TestConfigStore.Create(_h.Dir), answered: 1),
            resolverFor: (pool, config) =>
                new ExchangeIdentityResolver(pool, _delinea, NullLogger<ExchangeIdentityResolver>.Instance, config));

        var result = await validator.ValidateSelfGrantAsync(_self, SelfAlias);

        Assert.Equal(
            $"Access denied: The self-grant check could not be completed: Exchange Online could not resolve '{SelfAddress}'; nothing was written. Contact your administrator.",
            result);
        Assert.Equal(0, Connects);
    }

    /// <summary>
    /// Answers its first <paramref name="answered"/> reads, fails the next <paramref name="failing"/>, and answers every
    /// later one; writes pass. aud-a28p's fixture (<see cref="ProtectedPrincipalExclusionsReadTests"/>), kept per class
    /// as the suite's other failing stores are, with the failure allowed to end.
    /// </summary>
    private sealed class FailsAfterReadsStore(IConfigStore inner, int answered, int failing = int.MaxValue) : IConfigStore
    {
        private int _reads;

        public long GetChangeToken() => inner.GetChangeToken();

        public T Read<T>(Func<SqliteConnection, T> read)
        {
            var ordinal = Interlocked.Increment(ref _reads);
            return ordinal > answered && ordinal - answered <= failing
                ? throw new InvalidOperationException("config store unreadable")
                : inner.Read(read);
        }

        public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write) => inner.Write(write);

        public void Write(Action<SqliteConnection, SqliteTransaction> write) => inner.Write(write);
    }

    /// <summary>Keeps the text of every warning and error the validator logs.</summary>
    private sealed class CapturingLogger : ILogger<PermissionValidator>
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

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("this test has no network");
    }
}
