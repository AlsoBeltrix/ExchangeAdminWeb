using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28p: the protected-principal check probed the Mailbox Permissions module config for corruption and
/// then read its ExcludedUsers list a second time, fail-open - so a read that failed between the two was
/// "no exclusions", and a write against a principal on that list went through.
/// </summary>
/// <remarks>
/// The real <see cref="ProtectedPrincipalService"/>, over a module-config store on the harness's database
/// file that answers a set number of reads and fails every later one. The gate is the AD Attribute
/// Editor's save, built by <see cref="AttributeEditorHarness"/> as its own tests build it - the LocalAd
/// stand-ins, no directory, no Delinea - except that its protection check is this real service rather than
/// the harness's scripted one. Nothing here reaches a directory.
/// </remarks>
public sealed class ProtectedPrincipalExclusionsReadTests : IDisposable
{
    private const string Actor = "EXAMPLE\\operator";
    private const string Ip = "10.0.0.5";
    private const string Ticket = "CHG0200";

    private readonly AttributeEditorHarness _h = new();
    private readonly ExtendedLogService _extendedLog;
    private readonly DelineaService _delinea;

    public ProtectedPrincipalExclusionsReadTests()
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new NoNetwork()));
        _extendedLog = new ExtendedLogService(
            _h.Config, _h.Env, TestConfigStore.CreateAppSettings(Root), NullLogger<ExtendedLogService>.Instance);
        _delinea = new DelineaService(http, _h.Config, NullLogger<DelineaService>.Instance, _extendedLog, _h.Trace);
    }

    public void Dispose()
    {
        _extendedLog.Dispose();
        _h.Dispose();
    }

    private string Root => _h.Env.ContentRootPath;

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    /// <summary>The Mailbox Permissions module's ExcludedUsers list, written past every switch.</summary>
    private void SeedExclusions(string excludedUsers) =>
        new ModuleConfigRepository(TestConfigStore.Create(Root)).UpdateModule(
            "MailboxPermissions", new Dictionary<string, string> { ["ExcludedUsers"] = excludedUsers });

    private ProtectedPrincipalService RealProtection(IConfigStore moduleConfigStore) =>
        new(_h.Env, _h.Config,
            new ModuleConfigService(new ModuleCatalog(), _h.Env, new ModuleConfigRepository(moduleConfigStore), NullLogger<ModuleConfigService>.Instance),
            TestConfigStore.CreateProtectedPrincipal(Root), _delinea, NullLogger<ProtectedPrincipalService>.Instance);

    private AttributeEditorHarness.HarnessService EditorOver(ProtectedPrincipalService protection) =>
        new(_h,
            new ModuleCredentialService(_h.ModuleConfig, _delinea, NullLogger<ModuleCredentialService>.Instance),
            protection, _h.Servicers, _h.Trace, _h.Audit, _h.Email, _h.ModuleConfig, _h.Allowlist, _h.Env, _h.Logger);

    private static Task<AttributeSaveResult> Save(AttributeEditorHarness.HarnessService editor, LocalAd.User user) =>
        editor.SaveAsync(
            AttributeEditorHarness.PrincipalFor(user), new() { ["title"] = "Lead Analyst" }, Actor, Ip, Ticket,
            AttributeEditorHarness.Operator());

    /// <summary>Refused at the protection check: no session, nothing set, and one record that says so.</summary>
    private void AssertRefusedAtTheProtectionCheck(AttributeEditorHarness.HarnessService editor, AttributeSaveResult result, string error)
    {
        Assert.False(result.Success);
        Assert.Equal(error, result.Error);
        Assert.Equal(0, editor.SessionsOpened);
        Assert.Empty(_h.Ad.Sets);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEdit_Refused", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal("ProtectedPrincipalCheck", Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(error, Text(record, "error"));
    }

    [Fact]
    public async Task AStoreThatFailsAfterItsFirstRead_DoesNotUnprotectAnExcludedTarget()
    {
        // The first read answers and every later one fails: the shape of a failure landing between a
        // corruption probe and a second read of the list. The list must come from a read that answered,
        // or the check must fail - never "no exclusions".
        var alex = _h.Ad.AddAlex();
        SeedExclusions("someone@example.test, " + alex.Upn);
        var store = new FailsAfterReadsStore(TestConfigStore.Create(Root), answered: 1);
        var editor = EditorOver(RealProtection(store));

        var result = await Save(editor, alex);

        AssertRefusedAtTheProtectionCheck(editor, result, "Target is a protected principal and cannot be modified.");
    }

    [Fact]
    public async Task AStoreThatCannotBeRead_RefusesTheCheck_AsCorrupt()
    {
        var alex = _h.Ad.AddAlex();
        SeedExclusions(alex.Upn);
        var editor = EditorOver(RealProtection(new FailsAfterReadsStore(TestConfigStore.Create(Root), answered: 0)));

        var result = await Save(editor, alex);

        AssertRefusedAtTheProtectionCheck(editor, result,
            "MailboxPermissions module configuration is corrupt - protected-principal exclusions unavailable. Contact your administrator.");
    }

    [Fact]
    public async Task AnEmptyListOverAHealthyStore_PassesTheCheck()
    {
        // The control: the refusals above are about the list, not a check that refuses everything. Asked of the
        // service rather than through a save, so this class opens no PowerShell session of its own.
        var alex = _h.Ad.AddAlex();
        SeedExclusions("");

        var result = await RealProtection(TestConfigStore.Create(Root)).CheckAsync(AttributeEditorHarness.PrincipalFor(alex));

        Assert.False(result.CheckFailed, result.Reason);
        Assert.False(result.IsProtected);
    }

    [Fact]
    public void OneLoad_ReadsTheModuleConfigOnce_FreshAndFromTheCache()
    {
        // The probe and the list are one read, so no failure can fall between them. Rules are configured so
        // that the second load is answered from the rules' cache - the list is read either way.
        SeedExclusions("ceo@example.test");
        new ProtectedPrincipalRepository(TestConfigStore.Create(Root)).Save(new ProtectedPrincipalData(["cfo@example.test"], [], [], [], []));
        var store = new FailsAfterReadsStore(TestConfigStore.Create(Root), answered: int.MaxValue);
        var protection = RealProtection(store);

        var fresh = protection.LoadEffectiveConfig();
        Assert.Null(fresh.error);
        Assert.Equal(new[] { "ceo@example.test" }, fresh.legacyExclusions);
        Assert.Equal(1, store.Reads);

        var cached = protection.LoadEffectiveConfig();
        Assert.Same(fresh.config, cached.config);
        Assert.Null(cached.error);
        Assert.Equal(new[] { "ceo@example.test" }, cached.legacyExclusions);
        Assert.Equal(2, store.Reads);
    }

    /// <summary>Answers its first <paramref name="answered"/> reads and fails every later one; writes pass.</summary>
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
