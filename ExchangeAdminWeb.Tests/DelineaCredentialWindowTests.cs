using System.Net;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-88, the addition of 2026-10-30 (the supervisor's call to meet the owner's near-instant requirement; the owner may veto).
/// Each secret-store fetch takes 2.7-4.1 s on the dev host, so with one fetch per operation every grant still waited on the
/// store. <see cref="DelineaService.GetCredentialsBySecretIdAsync"/> now keeps what it fetched for five minutes after its last
/// use, by Secret ID, in process memory only.
/// </summary>
/// <remarks>
/// The real service over a stand-in vault (a local message handler: a token endpoint and a secret endpoint that counts its
/// requests by Secret ID) and a clock the test moves. The extended log at Debug and the operation trace are the real
/// services writing to this fixture's folder, so every line either writes can be read back. No vault is reached.
/// </remarks>
public sealed class DelineaCredentialWindowTests : IDisposable
{
    private const int SecretId = 31;
    private const int OtherSecretId = 45;
    private const string Account = "svc-kd88-dirread";
    private const string Password = "kd88-P@ss-not-for-any-log";

    private readonly TempDir _temp = new();
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ExtendedLogService _extendedLog;
    private readonly OperationTraceService _trace;
    private readonly Vault _vault = new();
    private readonly ManualClock _clock = new();
    private readonly CapturingLogger _logger = new();

    public DelineaCredentialWindowTests()
    {
        _env = Substitute.For<IWebHostEnvironment>();
        _env.ContentRootPath.Returns(_temp.Path);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://vault.invalid/SecretServer",
            ["Audit:LogRoot"] = _temp.Path,
            ["Audit:RotationPeriod"] = "daily",
        }).Build();
        _extendedLog = new ExtendedLogService(_config, _env, new AppSettingRepository(TestConfigStore.Create(_temp.Path)),
            NullLogger<ExtendedLogService>.Instance);
        _extendedLog.SetLevel("Debug");
        _trace = new OperationTraceService(_config, new JsonlLogService(_config, NullLogger<JsonlLogService>.Instance));
    }

    public void Dispose()
    {
        _extendedLog.Dispose();
        _temp.Dispose();
    }

    private DelineaService Store(TimeSpan? window = null)
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_vault, disposeHandler: false));
        return new DelineaService(http, _config, _logger, _extendedLog, _trace, _ => ("fixture-client", "fixture-key"))
        {
            Clock = _clock,
            CredentialWindow = window ?? DelineaService.DefaultCredentialWindow,
        };
    }

    // One operation's fetch, as the protected-principal check and the self-grant check make it (kd-88 item 2).
    private static async Task<(string username, string password, string domain)?> InAnOperation(DelineaService store, int secretId)
    {
        using var operation = DirectoryReadOperation.Begin();
        return await DirectoryReadOperation.DirectoryReadSecretAsync(secretId, id => store.GetCredentialsBySecretIdAsync(id));
    }

    // The brief's. A second operation inside the window makes no request to the store, and gets the same credential.
    [Fact]
    public async Task ASecondOperationInsideTheWindow_MakesNoFetch()
    {
        var store = Store();

        var first = await InAnOperation(store, SecretId);
        _clock.Advance(TimeSpan.FromMinutes(4));
        var second = await InAnOperation(store, SecretId);

        Assert.Equal((Account, Password, "CONTOSO"), first);
        Assert.Equal(first, second);
        Assert.Equal(1, _vault.SecretRequests(SecretId));
    }

    // Five minutes with no use, and the next operation asks the store again.
    [Fact]
    public async Task AfterTheWindow_ItFetches()
    {
        var store = Store();

        await InAnOperation(store, SecretId);
        _clock.Advance(DelineaService.DefaultCredentialWindow);
        var again = await InAnOperation(store, SecretId);

        Assert.Equal((Account, Password, "CONTOSO"), again);
        Assert.Equal(2, _vault.SecretRequests(SecretId));
    }

    // rev-a2. Not sliding: the window runs from the retrieval, so a use inside it does not start it again - a credential in
    // steady use is fetched afresh five minutes after it was fetched, however busy the instance is.
    [Fact]
    public async Task AUseInsideTheWindow_DoesNotSlideIt_TheWindowRunsFromTheRetrieval()
    {
        var store = Store();

        await InAnOperation(store, SecretId);
        _clock.Advance(TimeSpan.FromMinutes(4));
        await InAnOperation(store, SecretId);
        Assert.Equal(1, _vault.SecretRequests(SecretId));

        _clock.Advance(TimeSpan.FromMinutes(1)); // five minutes from the retrieval, one from the last use
        await InAnOperation(store, SecretId);
        Assert.Equal(2, _vault.SecretRequests(SecretId));
    }

    // rev-a2. The secret rotated under the same ID while the old password was kept: a use every minute, as on a busy instance,
    // gets the new one five minutes after the old one was fetched - never the old one indefinitely.
    [Fact]
    public async Task ARotatedSecret_IsServedFreshWithinTheWindow_HoweverOftenItIsUsed()
    {
        var store = Store();
        await InAnOperation(store, SecretId);
        _vault.Password = "rotated-P@ss";

        var served = new List<string?>();
        for (var minute = 1; minute <= 6; minute++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            served.Add((await InAnOperation(store, SecretId))?.password);
        }

        Assert.Equal([Password, Password, Password, Password, "rotated-P@ss", "rotated-P@ss"], served);
        Assert.Equal(2, _vault.SecretRequests(SecretId));
    }

    // rev-a2. A caller's report that the directory rejected the kept credential drops it: the next fetch asks the store, and
    // gets the rotated password, inside the window.
    [Fact]
    public async Task AnInvalidatedCredential_IsFetchedAfresh_InsideTheWindow()
    {
        var store = Store();
        await InAnOperation(store, SecretId);
        await InAnOperation(store, OtherSecretId);
        _vault.Password = "rotated-P@ss";
        _clock.Advance(TimeSpan.FromMinutes(1));

        store.Invalidate(SecretId);
        var fresh = await InAnOperation(store, SecretId);
        var other = await InAnOperation(store, OtherSecretId);

        Assert.Equal("rotated-P@ss", fresh?.password);
        Assert.Equal(2, _vault.SecretRequests(SecretId));
        // Another Secret ID's kept credential is untouched.
        Assert.Equal(Password, other?.password);
        Assert.Equal(1, _vault.SecretRequests(OtherSecretId));
    }

    // The configured Secret ID changed: the new one is not answered from the old one's credential - it is fetched - and each
    // ID keeps its own.
    [Fact]
    public async Task AChangedSecretId_Fetches()
    {
        var store = Store();

        var before = await InAnOperation(store, SecretId);
        var after = await InAnOperation(store, OtherSecretId);
        await InAnOperation(store, OtherSecretId);

        Assert.Equal(1, _vault.SecretRequests(SecretId));
        Assert.Equal(1, _vault.SecretRequests(OtherSecretId));
        Assert.Equal(Account, before?.username);
        Assert.Equal(Account + "-45", after?.username);
    }

    // A fetch that fails is never kept: the next asks the store again. And a failure after the window is the failure - the
    // credential that lapsed is not handed out in its place.
    [Fact]
    public async Task AFetchThatFails_IsNotKept_AndALapsedCredentialIsNotServedForIt()
    {
        var store = Store();
        _vault.Failing.Add(SecretId);

        Assert.Null(await InAnOperation(store, SecretId));
        _vault.Failing.Remove(SecretId);
        Assert.NotNull(await InAnOperation(store, SecretId));
        Assert.NotNull(await InAnOperation(store, SecretId));
        Assert.Equal(2, _vault.SecretRequests(SecretId));

        _clock.Advance(DelineaService.DefaultCredentialWindow);
        _vault.Failing.Add(SecretId);
        Assert.Null(await InAnOperation(store, SecretId));
        Assert.Equal(3, _vault.SecretRequests(SecretId));
    }

    // Module credentials go the same way: Group Management's credential, looked up twice through the module-credential
    // service inside the window, is one request to the store.
    [Fact]
    public async Task AModuleCredential_InsideTheWindow_IsOneFetch()
    {
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), _env, new ModuleConfigRepository(TestConfigStore.Create(_temp.Path)),
            NullLogger<ModuleConfigService>.Instance);
        moduleConfig.SaveModuleConfig("GroupManagement", new Dictionary<string, string> { ["DelineaSecretId"] = "31" });
        var credentials = new ModuleCredentialService(moduleConfig, Store(), NullLogger<ModuleCredentialService>.Instance);

        var first = await credentials.GetCredentialsAsync("GroupManagement", "the first change", TestContext.Current.CancellationToken);
        var second = await credentials.GetCredentialsAsync("GroupManagement", "the second change", TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Equal(1, _vault.SecretRequests(SecretId));
    }

    // A window of zero keeps nothing - what the harnesses that count every request use.
    [Fact]
    public async Task AWindowOfZero_KeepsNothing()
    {
        var store = Store(TimeSpan.Zero);

        await InAnOperation(store, SecretId);
        await InAnOperation(store, SecretId);

        Assert.Equal(2, _vault.SecretRequests(SecretId));
    }

    // Process memory only: the account and the password appear in no log line - the service's own logger, the extended
    // log at Debug, the operation trace - whether the credential was fetched, reused, or lapsed and fetched again.
    [Fact]
    public async Task TheCredential_IsInNoLogLine()
    {
        var store = Store();

        await InAnOperation(store, SecretId);
        await InAnOperation(store, SecretId);
        _clock.Advance(DelineaService.DefaultCredentialWindow);
        await InAnOperation(store, SecretId);
        _extendedLog.Dispose();

        var written = Directory.GetFiles(Path.Combine(_temp.Path, "ExchangeAdminWeb"), "*.jsonl").Select(File.ReadAllText).ToList();
        Assert.Contains(written, text => text.Contains("VaultCredentialReused", StringComparison.Ordinal));
        Assert.Contains(written, text => text.Contains("Using kept Delinea credential", StringComparison.Ordinal));
        foreach (var line in written.Concat(_logger.Lines))
        {
            Assert.DoesNotContain(Password, line, StringComparison.Ordinal);
            Assert.DoesNotContain(Account, line, StringComparison.Ordinal);
        }
    }

    // ---- the stand-ins ----------------------------------------------------------------------------

    /// <summary>A token endpoint, and a secret endpoint answering <see cref="Account"/> - "-45" for the other ID.</summary>
    private sealed class Vault : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _requests = new();

        public HashSet<int> Failing { get; } = [];

        /// <summary>rev-a2. The password every secret answers with: a test rotates it.</summary>
        public string Password { get; set; } = DelineaCredentialWindowTests.Password;

        public int SecretRequests(int secretId) => _requests.GetValueOrDefault(secretId);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth2/token", StringComparison.Ordinal))
                return Answer(HttpStatusCode.OK, """{"access_token":"fixture-token","expires_in":3600}""");

            var secretId = int.Parse(path[(path.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            _requests.AddOrUpdate(secretId, 1, (_, count) => count + 1);
            if (Failing.Contains(secretId))
                return Answer(HttpStatusCode.InternalServerError, "{}");

            var account = secretId == SecretId ? Account : $"{Account}-{secretId}";
            return Answer(HttpStatusCode.OK,
                $$"""{"items":[{"fieldName":"Username","itemValue":"{{account}}"},{"fieldName":"Password","itemValue":"{{this.Password}}"},{"fieldName":"Domain","itemValue":"CONTOSO"}]}""");
        }

        private static Task<HttpResponseMessage> Answer(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    /// <summary>A clock that moves only when the test says.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class CapturingLogger : ILogger<DelineaService>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
