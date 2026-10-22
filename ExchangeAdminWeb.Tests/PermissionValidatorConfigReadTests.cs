using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    private static readonly Guid MemberMailbox = Guid.Parse("0e0e0e0e-1111-4222-8333-555555555555");

    private const string ListUnavailable = "Access denied: Protected-user list is unavailable. Contact your administrator.";
    private const string SelfGrantRefusal = "Access denied: You cannot grant permissions to yourself.";

    private readonly ExchangeWriteHarness _h = new();
    private readonly IWebHostEnvironment _env = Substitute.For<IWebHostEnvironment>();
    private readonly ExtendedLogService _extendedLog;
    private readonly DelineaService _delinea;
    private readonly List<ExoConnectionPool> _pools = [];
    private int _connects;

    public PermissionValidatorConfigReadTests()
    {
        _env.ContentRootPath.Returns(_h.Dir);
        _h.AddMailbox(Member, MemberMailbox);

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
    /// </summary>
    private PermissionValidator ValidatorOver(IConfigStore moduleConfigStore, IConfigStore? enablementStore = null,
        string? preventSelfGrant = null)
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

        var resolver = ExchangeWriteHarness.ExchangeKnows(Member, Member, MemberMailbox);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(IIdentityResolver)).Returns(resolver);

        return new PermissionValidator(config, moduleConfig, pool, protection, servicers,
            NullLogger<PermissionValidator>.Instance, scopeFactory);
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
            _h.Enablement.SetModulesEnabled(new Dictionary<string, bool> { [ExoConnectionPool.ConfigModuleKey] = false });
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

        Assert.Equal(SelfGrantRefusal, validator.ValidateSelfGrant(Self, SelfAddress));
        Assert.Equal(SelfGrantRefusal, await validator.ValidateSelfGrantAsync(Self, SelfAddress));
    }

    [Theory]
    [InlineData(null, "false", false)]
    [InlineData(null, null, true)]
    [InlineData("", "false", false)]
    [InlineData("false", null, false)]
    [InlineData("true", "false", true)]
    [InlineData("yes", "false", true)]
    public async Task TheSelfGrantSetting_FallsBackToAppsettings_OnlyWhenTheModuleConfigHoldsNoValue(
        string? stored, string? appsettings, bool refused)
    {
        // Over a readable store: no value (absent or blank) is appsettings' to answer, default on; a stored value wins
        // over appsettings either way; a stored value that is neither true nor false is present, so it is not
        // appsettings' to answer, and it refuses.
        if (stored is not null)
            SeedMailboxPermissions("PreventSelfGrant", stored);
        var validator = ValidatorOver(TestConfigStore.Create(_h.Dir), preventSelfGrant: appsettings);

        var expected = refused ? SelfGrantRefusal : null;
        Assert.Equal(expected, validator.ValidateSelfGrant(Self, SelfAddress));
        Assert.Equal(expected, await validator.ValidateSelfGrantAsync(Self, SelfAddress));
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

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("this test has no network");
    }
}
