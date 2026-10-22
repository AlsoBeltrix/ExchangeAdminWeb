using System.Text;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28s: the permission validator probed the Mailbox Permissions module config for corruption and then read
/// its ExcludedUsers list again, fail-open - so a read that failed after the probe was "no protected users", and
/// Mailbox Permissions, Calendar Permissions and Out of Office writes against a mailbox on that list went through.
/// </summary>
/// <remarks>
/// A real <see cref="PermissionValidator"/> whose module config sits on <see cref="ExchangeWriteHarness"/>'s
/// database file through a store that answers a set number of reads and fails every later one. Its
/// protected-principal and section-access stores are on the same file with nothing configured, so the
/// ExcludedUsers list is the only protection; its own Exchange pool is left unconfigured, as
/// <see cref="PermissionValidatorTests"/>' validators are, so listed entries stay literals and nothing is borrowed
/// while the check runs; Exchange answers through a stand-in resolver. The write is the Mailbox Permissions CSV
/// bulk loop over the harness's pool and stand-in cmdlets. Nothing here reaches Exchange or a directory.
/// </remarks>
public sealed class PermissionValidatorExclusionsReadTests : IDisposable
{
    private const string Vip = "vip@contoso.test";
    private const string Other = "other@contoso.test";
    private const string Trustee = "jdoe@contoso.test";
    private const string ModuleId = "MailboxPermissions";
    private static readonly Guid VipMailbox = Guid.Parse("0c0c0c0c-1111-4222-8333-444444444444");
    private static readonly Guid OtherMailbox = Guid.Parse("0d0d0d0d-5555-4666-8777-888888888888");

    private static readonly string ProtectedRefusal =
        $"Access denied: {Vip} is protected and cannot be modified through this interface.";

    private readonly ExchangeWriteHarness _h = new();
    private readonly IWebHostEnvironment _env = Substitute.For<IWebHostEnvironment>();
    private readonly ExtendedLogService _extendedLog;
    private readonly DelineaService _delinea;
    private readonly ExoConnectionPool _unconfiguredPool;

    public PermissionValidatorExclusionsReadTests()
    {
        _env.ContentRootPath.Returns(_h.Dir);

        // The mailboxes the stand-in cmdlets know, so a write that got through would land, and show.
        _h.AddMailbox(Vip, VipMailbox);
        _h.AddMailbox(Other, OtherMailbox);

        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new NoNetwork()));
        _extendedLog = new ExtendedLogService(
            _h.Config, _env, TestConfigStore.CreateAppSettings(_h.Dir), NullLogger<ExtendedLogService>.Instance);
        _delinea = new DelineaService(http, _h.Config, NullLogger<DelineaService>.Instance, _extendedLog, _h.Trace);

        var poolDir = Path.Combine(_h.Dir, "unconfigured-pool");
        Directory.CreateDirectory(poolDir);
        var poolEnv = Substitute.For<IWebHostEnvironment>();
        poolEnv.ContentRootPath.Returns(poolDir);
        var poolStore = TestConfigStore.Create(poolDir);
        var poolConfig = new ModuleConfigService(
            new ModuleCatalog(), poolEnv, new ModuleConfigRepository(poolStore), NullLogger<ModuleConfigService>.Instance);
        var poolEnablement = new ModuleEnablementService(
            new ModuleCatalog(), poolEnv, poolConfig, new ModuleEnablementRepository(poolStore), _h.Config,
            NullLogger<ModuleEnablementService>.Instance);
        _unconfiguredPool = new ExoConnectionPool(
            _h.Config, poolConfig, poolEnablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace);
    }

    public void Dispose()
    {
        _unconfiguredPool.Dispose();
        _extendedLog.Dispose();
        _h.Dispose();
    }

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    /// <summary>The Mailbox Permissions module's ExcludedUsers list, written past every failing store.</summary>
    private void SeedExclusions(string excludedUsers) =>
        new ModuleConfigRepository(TestConfigStore.Create(_h.Dir)).UpdateModule(
            ModuleId, new Dictionary<string, string> { ["ExcludedUsers"] = excludedUsers });

    /// <summary>Exchange knows both mailboxes and nobody else, and never matches self-grant object ids.</summary>
    private static IIdentityResolver ExchangeKnowsBoth()
    {
        var resolver = ExchangeWriteHarness.ExchangeKnows(Vip, Vip, VipMailbox);
        resolver.ResolveRecipientAsync(Other).Returns(
            new ResolvedRecipient(Other, "ext-" + Other, "UserMailbox", ExistsOnPrem: false, ExchangeGuid: OtherMailbox));
        return resolver;
    }

    /// <summary>A real validator whose Mailbox Permissions module config is read through <paramref name="moduleConfigStore"/>.</summary>
    private PermissionValidator ValidatorOver(IConfigStore moduleConfigStore)
    {
        var moduleConfig = new ModuleConfigService(
            new ModuleCatalog(), _env, new ModuleConfigRepository(moduleConfigStore), NullLogger<ModuleConfigService>.Instance);
        var protection = new ProtectedPrincipalService(
            _env, _h.Config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_h.Dir), _delinea,
            NullLogger<ProtectedPrincipalService>.Instance);
        var servicers = new ProtectedPrincipalServicerService(
            new SectionAccessService(_h.Config, NullLogger<SectionAccessService>.Instance, _env, new ModuleCatalog(),
                new SectionAccessRepository(TestConfigStore.Create(_h.Dir))),
            NullLogger<ProtectedPrincipalServicerService>.Instance);

        var resolver = ExchangeKnowsBoth();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(IIdentityResolver)).Returns(resolver);

        return new PermissionValidator(_h.Config, moduleConfig, _unconfiguredPool, protection, servicers,
            NullLogger<PermissionValidator>.Instance, scopeFactory);
    }

    /// <summary>One CSV row granting <see cref="Trustee"/> FullAccess on <paramref name="target"/>, through the bulk loop.</summary>
    private Task<BulkOperationResult> GrantInBulk(PermissionValidator validator, string target)
    {
        var email = Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>());
        var csv = "Target,User,FullAccess,SendAs,AutoMapping\n" + $"{target},{Trustee},True,False,False\n";
        return new MailboxPermissionService(_h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, _h.Config, null!, _h.Trace)
            .ProcessMailboxPermissionsCsvAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(csv)), isAdd: true, validator, @"CONTOSO\operator",
                _h.Audit, email, "10.0.0.5", "INC100", actingUser: null);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AStoreThatFailsAfterItsFirstReads_DoesNotUnprotectAListedMailbox(int answered)
    {
        // The store answers its first reads and fails every later one: the shape of a failure landing after a
        // probe that passed and before a second read of the list. One read is the whole load since aud-a28s; two
        // were the presence read and the corruption probe before it. The list must come from a read that answered,
        // or the check must refuse - never "no protected users".
        SeedExclusions("someone@contoso.test, " + Vip);
        var validator = ValidatorOver(new FailsAfterReadsStore(TestConfigStore.Create(_h.Dir), answered));

        var bulk = await GrantInBulk(validator, Vip);

        Assert.Equal(0, bulk.SuccessCount);
        Assert.Equal(1, bulk.FailedCount);
        Assert.Empty(_h.CallsTo("Add-MailboxPermission"));
        Assert.Empty(_h.Writes);
        Assert.Equal(0, _h.Connects);

        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("BulkAdd_ExcludedTarget", Text(record, "action"));
        Assert.Equal(Vip, Text(record, "target"));
        Assert.Equal(ProtectedRefusal, Text(record, "error"));
    }

    [Fact]
    public async Task AStoreThatCannotBeRead_RefusesEveryTarget_AsTheListUnavailable()
    {
        // The corrupt-store refusal, which held before aud-a28s as well: a target the list does not name is still
        // refused, because the list could not be read to say so.
        SeedExclusions(Vip);
        var validator = ValidatorOver(new FailsAfterReadsStore(TestConfigStore.Create(_h.Dir), answered: 0));

        var result = await validator.ValidateTargetMailboxAsync(Other, actingUser: null, ModuleId);

        Assert.Equal("Access denied: Protected-user list is unavailable. Contact your administrator.", result.Error);
        Assert.Null(result.Mailbox);
        Assert.True(await validator.IsUserExcludedAsync(Other));
    }

    [Fact]
    public async Task AnEmptyListOverAHealthyStore_Allows_BoundToTheMailboxExchangeNames()
    {
        // The control: the refusals above are about the list, not a check that refuses everything. Asked of the
        // validator rather than through the bulk loop, so this class opens no PowerShell session of its own.
        SeedExclusions("");
        var validator = ValidatorOver(TestConfigStore.Create(_h.Dir));

        var result = await validator.ValidateTargetMailboxAsync(Vip, actingUser: null, ModuleId);

        Assert.Null(result.Error);
        Assert.Equal(VipMailbox, result.Mailbox?.ExchangeGuid);
    }

    [Fact]
    public async Task OneLoad_ReadsTheModuleConfigOnce_AndACachedValidationNotAtAll()
    {
        // The probe and the list are one read, so no failure can fall between them.
        SeedExclusions(Vip);
        var store = new FailsAfterReadsStore(TestConfigStore.Create(_h.Dir), answered: int.MaxValue);
        var validator = ValidatorOver(store);

        var unlisted = await validator.ValidateTargetMailboxAsync(Other, actingUser: null, ModuleId);
        Assert.Null(unlisted.Error);
        Assert.Equal(OtherMailbox, unlisted.Mailbox?.ExchangeGuid);
        Assert.Equal(1, store.Reads);

        var listed = await validator.ValidateTargetMailboxAsync(Vip, actingUser: null, ModuleId);
        Assert.Equal(ProtectedRefusal, listed.Error);
        Assert.Equal(1, store.Reads);
    }

    /// <summary>
    /// Answers its first <paramref name="answered"/> reads and fails every later one; writes pass. aud-a28p's fixture
    /// (<see cref="ProtectedPrincipalExclusionsReadTests"/>), kept per class as the suite's other failing stores are.
    /// </summary>
    private sealed class FailsAfterReadsStore(IConfigStore inner, int answered) : IConfigStore
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public long GetChangeToken() => inner.GetChangeToken();

        public T Read<T>(Func<SqliteConnection, T> read) =>
            Interlocked.Increment(ref _reads) > answered
                ? throw new InvalidOperationException("config store unreadable")
                : inner.Read(read);

        public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write) => inner.Write(write);

        public void Write(Action<SqliteConnection, SqliteTransaction> write) => inner.Write(write);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("this test has no network");
    }
}
