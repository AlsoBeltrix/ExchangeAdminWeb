using System.Net;
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
/// The real <see cref="CloudPasswordResetService"/> over a real module config store in a temp directory, a
/// <see cref="StandInForest"/> for the directory, and a recording handler for Graph. Nothing here reaches a directory,
/// Graph or Secret Server: the forest stands in, every Graph request lands in <see cref="Graph"/>, and a save that needs
/// the module's Graph secret finds none configured unless a test sets one, so Secret Server is never asked.
/// </summary>
internal sealed class CloudPasswordResetHarness : IDisposable
{
    private readonly TempDir _dir = new();

    public CloudPasswordResetHarness(
        StandInForest forest, IReadOnlyDictionary<string, string>? moduleConfig = null, bool corruptConfig = false)
    {
        Forest = forest;

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_dir.Path);

        var repository = corruptConfig
            ? new ModuleConfigRepository(new UnreadableConfigStore())
            : TestConfigStore.CreateModuleConfig(_dir.Path);
        ModuleConfig = new ModuleConfigService(new ModuleCatalog(), env, repository, NullLogger<ModuleConfigService>.Instance);

        if (!corruptConfig && moduleConfig is { Count: > 0 })
            ModuleConfig.SaveModuleConfig(CloudPasswordResetService.ModuleId, new Dictionary<string, string>(moduleConfig));

        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://secretserver.example.invalid",
            ["Audit:LogRoot"] = _dir.Path,
        }).Build();

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(Graph));

        var jsonl = new JsonlLogService(settings, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(settings, jsonl);
        var extendedLog = new ExtendedLogService(
            settings, env, TestConfigStore.CreateAppSettings(_dir.Path), NullLogger<ExtendedLogService>.Instance);
        var delinea = new DelineaService(httpClientFactory, settings, NullLogger<DelineaService>.Instance, extendedLog, trace);

        Service = new CloudPasswordResetService(
            Log, ModuleConfig, delinea, httpClientFactory, forest.Service(), Substitute.For<ITicketValidator>(), new PasswordGenerator());
    }

    public StandInForest Forest { get; }

    public ModuleConfigService ModuleConfig { get; }

    public GraphRecorder Graph { get; } = new();

    public CapturingLogger Log { get; } = new();

    public CloudPasswordResetService Service { get; }

    /// <summary>A Graph client over <see cref="Graph"/>, for driving the save directly.</summary>
    public GraphTokenClient GraphClient() => new("tenant", "client", "secret", new HttpClient(Graph));

    public void Dispose() => _dir.Dispose();

    /// <summary>Answers the token endpoint itself and records every other request before <see cref="Respond"/> answers it.</summary>
    internal sealed class GraphRecorder : HttpMessageHandler
    {
        public sealed record Request(HttpMethod Method, Uri Uri, string? Body);

        public List<Request> Requests { get; } = [];

        /// <summary>How Graph answers; a test may throw from here to stand for a request lost in flight.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"test-token","expires_in":3600}"""),
                };
            }

            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Request(request.Method, request.RequestUri, body));
            return Respond(request);
        }
    }

    internal sealed class CapturingLogger : ILogger<CloudPasswordResetService>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>A config store that cannot be read: the corrupt or unreadable module config.</summary>
    private sealed class UnreadableConfigStore : IConfigStore
    {
        public long GetChangeToken() => throw new InvalidOperationException("store unreadable");

        public T Read<T>(Func<SqliteConnection, T> read) => throw new InvalidOperationException("store unreadable");

        public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write) => throw new InvalidOperationException("store unreadable");

        public void Write(Action<SqliteConnection, SqliteTransaction> write) => throw new InvalidOperationException("store unreadable");
    }
}
