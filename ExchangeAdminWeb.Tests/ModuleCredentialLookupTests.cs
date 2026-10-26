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
/// aud-a2x: a module credential lookup says why it returned no credential, and the lookup every module already calls
/// returns what it did.
/// </summary>
/// <remarks>
/// GetCredentialsAsync returned null for three different reasons - the module's config unreadable, no secret ID set,
/// and the PAM provider returning nothing for the ID that is set - so a caller could only tell its operator one of
/// them, and Conference Rooms told every one "set the AD Delinea Secret ID". Everything here is offline: the Delinea
/// service is real, with no bootstrap credential, so it refuses before any request, and its HTTP client fails any
/// request that is sent.
/// </remarks>
public sealed class ModuleCredentialLookupTests : IDisposable
{
    private const string Module = "ConferenceRooms";
    private const string Purpose = "a test of the lookup";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eaw-a2x-" + Guid.NewGuid().ToString("N"));

    public ModuleCredentialLookupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("an unreadable module configuration", ModuleCredentialFailure.ConfigUnreadable)]
    [InlineData("no secret ID", ModuleCredentialFailure.SecretIdNotSet)]
    [InlineData("a secret ID of 0", ModuleCredentialFailure.SecretIdNotSet)]
    [InlineData("a secret ID that is not a number", ModuleCredentialFailure.SecretIdNotSet)]
    [InlineData("a secret ID Delinea returns no credential for", ModuleCredentialFailure.NotReturned)]
    public async Task TheLookup_SaysWhyItReturnedNoCredential_AndTheOldLookupReturnsNoneAsBefore(
        string configured, ModuleCredentialFailure expected)
    {
        var config = Config(_dir);
        var moduleConfig = configured == "an unreadable module configuration"
            ? new ModuleConfigService(new ModuleCatalog(), Environment(_dir), new ModuleConfigRepository(new UnreadableModuleConfigStore()),
                NullLogger<ModuleConfigService>.Instance)
            : new ModuleConfigService(new ModuleCatalog(), Environment(_dir), TestConfigStore.CreateModuleConfig(_dir),
                NullLogger<ModuleConfigService>.Instance);
        var secretId = configured switch
        {
            "a secret ID of 0" => "0",
            "a secret ID that is not a number" => "not-a-number",
            "a secret ID Delinea returns no credential for" => "4242",
            _ => null,
        };
        if (secretId is not null)
            moduleConfig.SaveModuleConfig(Module, new Dictionary<string, string> { ["DelineaSecretId"] = secretId });
        var logger = new CapturingLogger();
        using var offline = new OfflineCredentials(_dir, config, moduleConfig, logger);

        var lookup = await offline.Service.LookupCredentialsAsync(Module, Purpose, TestContext.Current.CancellationToken);
        var old = await offline.Service.GetCredentialsAsync(Module, Purpose, TestContext.Current.CancellationToken);

        Assert.Null(lookup.Credentials);
        Assert.Equal(expected, lookup.Failure);
        Assert.Null(old);
        Assert.Equal(0, offline.RequestsSent);
        // The service's own lines are as they were: one per lookup where it refuses, none where Delinea does (Delinea logs
        // its own reason).
        var lines = expected switch
        {
            ModuleCredentialFailure.ConfigUnreadable => new[] { $"Cannot retrieve credentials for {Module}: module config is corrupt" },
            ModuleCredentialFailure.SecretIdNotSet => new[] { $"Cannot retrieve credentials for {Module}: DelineaSecretId is not configured. Purpose: {Purpose}" },
            _ => Array.Empty<string>(),
        };
        Assert.Equal(lines.Concat(lines), logger.Entries.Select(e => e.Message));
        Assert.All(logger.Entries, e => Assert.Equal(LogLevel.Error, e.Level));
    }

    // Finding kd-61. The two configuration paths above answer before the vault is reached, and looked at no token: a caller
    // whose operator had already cancelled was told its credentials could not be read, and carried on. They are the same two
    // cases as the theory above, with the token cancelled.
    [Theory]
    [InlineData("an unreadable module configuration")]
    [InlineData("no secret ID")]
    public async Task ACancelledToken_EndsTheLookup_BeforeItAnswersFromTheConfiguration(string configured)
    {
        var moduleConfig = configured == "an unreadable module configuration"
            ? new ModuleConfigService(new ModuleCatalog(), Environment(_dir), new ModuleConfigRepository(new UnreadableModuleConfigStore()),
                NullLogger<ModuleConfigService>.Instance)
            : new ModuleConfigService(new ModuleCatalog(), Environment(_dir), TestConfigStore.CreateModuleConfig(_dir),
                NullLogger<ModuleConfigService>.Instance);
        var logger = new CapturingLogger();
        using var offline = new OfflineCredentials(_dir, Config(_dir), moduleConfig, logger);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => offline.Service.LookupCredentialsAsync(Module, Purpose, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => offline.Service.GetCredentialsAsync(Module, Purpose, cancel.Token));

        Assert.Empty(logger.Entries); // it did not read the configuration, so it has nothing to say about it
        Assert.Equal(0, offline.RequestsSent);
    }

    // Finding kd-69. The narrower window kd-61 left: the two configuration reads are synchronous and take as long as the
    // store does, so a cancel can arrive while one of them runs. Before: the failure that read found was returned, the
    // caller read it as a read it could not make, and a cancelled run finished, succeeded and was audited.
    [Theory]
    [InlineData("the read that says the configuration is unreadable")]
    [InlineData("the read that answers with no secret ID")]
    public async Task ACancelArrivingDuringAConfigurationRead_EndsTheLookup(string during)
    {
        var unreadable = during == "the read that says the configuration is unreadable";
        using var cancel = new CancellationTokenSource();
        var store = TestConfigStore.Create(_dir);
        var cancelling = new CancellingStore(store, cancel, cancelAtRead: unreadable ? 1 : 2, thenUnreadable: unreadable);
        // Saved so the module exists with no DelineaSecretId: the second read is the one that answers that, and the
        // cancel lands inside it.
        new ModuleConfigRepository(store).UpdateModule(Module, new Dictionary<string, string>());
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), Environment(_dir), new ModuleConfigRepository(cancelling),
            NullLogger<ModuleConfigService>.Instance);
        var logger = new CapturingLogger();
        using var offline = new OfflineCredentials(_dir, Config(_dir), moduleConfig, logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => offline.Service.LookupCredentialsAsync(Module, Purpose, cancel.Token));

        Assert.True(cancel.IsCancellationRequested); // the store cancelled from inside the read, where the operator's cancel lands
        Assert.True(cancelling.Reads >= (unreadable ? 1 : 2));
        Assert.Empty(logger.Entries); // the refusal it found is never reported: the lookup ended as a cancel
        Assert.Equal(0, offline.RequestsSent);
    }

    internal static IConfiguration Config(string dir) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Audit:LogRoot"] = dir,
        ["OperationTrace:Enabled"] = "false",
        ["Delinea:SecretServerUrl"] = "https://vault.invalid",
    }).Build();

    internal static IWebHostEnvironment Environment(string dir)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(dir);
        return env;
    }

    private sealed class CapturingLogger : ILogger<ModuleCredentialService>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}

/// <summary>A config store that cannot be read or written: a module config over it is corrupt (aud-a2x).</summary>
internal sealed class UnreadableModuleConfigStore : IConfigStore
{
    public long GetChangeToken() => throw new InvalidOperationException("the store is unreadable");
    public T Read<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> read) => throw new InvalidOperationException("the store is unreadable");
    public T Write<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, T> write) => throw new InvalidOperationException("the store is unreadable");
    public void Write(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction> write) => throw new InvalidOperationException("the store is unreadable");
}

/// <summary>
/// kd-69. A config store that cancels the lookup's token from inside a read - the cancel arriving while the synchronous
/// configuration read runs, which is the window the finding is about - and then fails that read, or answers it, as the case
/// needs. Every other operation is the real store's.
/// </summary>
internal sealed class CancellingStore(IConfigStore inner, CancellationTokenSource cancel, int cancelAtRead, bool thenUnreadable) : IConfigStore
{
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    public long GetChangeToken() => inner.GetChangeToken();

    public T Read<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> read)
    {
        if (Interlocked.Increment(ref _reads) >= cancelAtRead)
        {
            cancel.Cancel();
            if (thenUnreadable)
                throw new InvalidOperationException("the store could not be read");
        }

        return inner.Read(read);
    }

    public T Write<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, T> write) => inner.Write(write);

    public void Write(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction> write) => inner.Write(write);
}

/// <summary>
/// A real <see cref="ModuleCredentialService"/> that cannot reach anything (aud-a2x): its Delinea service has no
/// bootstrap credential, so every secret lookup is refused before a request is built, and the HTTP client it was given
/// counts and fails any request that is sent anyway.
/// </summary>
internal sealed class OfflineCredentials : IDisposable
{
    private readonly ExtendedLogService _extendedLog;
    private readonly CountingHandler _handler = new();

    public OfflineCredentials(string dir, IConfiguration config, ModuleConfigService moduleConfig, ILogger<ModuleCredentialService>? logger = null)
    {
        var env = ModuleCredentialLookupTests.Environment(dir);
        _extendedLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(dir), NullLogger<ExtendedLogService>.Instance);
        var trace = new OperationTraceService(config, new JsonlLogService(config, NullLogger<JsonlLogService>.Instance));
        var clients = Substitute.For<IHttpClientFactory>();
        clients.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_handler, disposeHandler: false));
        var delinea = new DelineaService(clients, config, NullLogger<DelineaService>.Instance, _extendedLog, trace,
            _ => (null, null));
        Service = new ModuleCredentialService(moduleConfig, delinea, logger ?? NullLogger<ModuleCredentialService>.Instance);
    }

    public ModuleCredentialService Service { get; }

    public int RequestsSent => _handler.Requests;

    public void Dispose() => _extendedLog.Dispose();

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            throw new InvalidOperationException("No request may leave this test.");
        }
    }
}
