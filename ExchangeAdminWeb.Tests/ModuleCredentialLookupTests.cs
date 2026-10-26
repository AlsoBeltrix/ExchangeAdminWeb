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
