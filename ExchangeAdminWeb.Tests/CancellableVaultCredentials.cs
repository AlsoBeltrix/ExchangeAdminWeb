using System.Net;
using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-59. A real <see cref="ModuleCredentialService"/> over a real <see cref="DelineaService"/>, whose secret request waits
/// until its caller cancels, for the modules that fetch an on-premises credential: Message Trace, Conference Rooms and the
/// two permission services. A test starts its module's work, waits for <see cref="SecretRequested"/>, cancels the
/// activity's token, and expects the work to end as the operator's cancel.
/// </summary>
/// <remarks>
/// The stand-in vault answers the token request, then waits at the secret request <see cref="WaitsAtSecretRequest"/> names
/// (the first, unless a test says otherwise) and answers every other with a credential; with <see cref="TimesOut"/> the
/// secret request gives up by itself, as HttpClient's own timeout does, with nobody cancelling - kd-14's and kd-37's "an
/// unasked vault timeout is still a read that failed". The module configuration holds the module's secret ID. Every wait is bounded
/// at <see cref="Bound"/>, so a caller that passes no token fails its test on the bound instead of hanging it. The audit log
/// is the fixture's own folder. No vault, no directory, no Exchange.
/// </remarks>
internal sealed class CancellableVaultCredentials : IDisposable
{
    /// <summary>Far above any scheduling stall, and short enough that a call which never cancels says so.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private readonly TempDir _temp = new();
    private readonly ExtendedLogService _extendedLog;
    private readonly Vault _vault = new();

    public CancellableVaultCredentials(string moduleId, int secretId = 7)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://vault.invalid",
            ["Audit:LogRoot"] = _temp.Path,
            ["Audit:RotationPeriod"] = "daily",
            ["OperationTrace:Enabled"] = "false",
            ["OnPremExchange:ServerUri"] = "https://exchange.contoso.test/PowerShell/",
        }).Build();
        var store = TestConfigStore.Create(_temp.Path);
        _extendedLog = new ExtendedLogService(Configuration, env, new AppSettingRepository(store), NullLogger<ExtendedLogService>.Instance);
        var log = new JsonlLogService(Configuration, NullLogger<JsonlLogService>.Instance);
        Trace = new OperationTraceService(Configuration, log);
        Audit = new AuditService(log, Trace);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_vault, disposeHandler: false));
        var delinea = new DelineaService(http, Configuration, NullLogger<DelineaService>.Instance, _extendedLog, Trace,
            _ => ("fixture-client", "fixture-key"));
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, new ModuleConfigRepository(store),
            NullLogger<ModuleConfigService>.Instance);
        moduleConfig.SaveModuleConfig(moduleId, new Dictionary<string, string>
        {
            ["DelineaSecretId"] = secretId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        Service = new ModuleCredentialService(moduleConfig, delinea, NullLogger<ModuleCredentialService>.Instance);
    }

    /// <summary>The credential lookup a service under test is built with.</summary>
    public ModuleCredentialService Service { get; }

    /// <summary>Configuration naming the stand-in vault, an on-premises server and the audit folder.</summary>
    public IConfiguration Configuration { get; }

    public OperationTraceService Trace { get; }

    /// <summary>An audit service writing to this fixture's folder; <see cref="AuditRows"/> reads it back.</summary>
    public AuditService Audit { get; }

    /// <summary>Signalled once the vault has been asked for a secret, which is where the test cancels.</summary>
    public Task SecretRequested => _vault.Requested.Task;

    /// <summary>True once a secret request that was waiting has been ended by its caller's cancel.</summary>
    public bool RequestCancelled => _vault.WasCancelled;

    /// <summary>The secret request gives up by itself, with nobody cancelling: HttpClient's own timeout.</summary>
    public bool TimesOut
    {
        get => _vault.TimesOut;
        set => _vault.TimesOut = value;
    }

    /// <summary>
    /// Which secret request waits for its caller's cancel, counting from 1; every other one is answered with a credential.
    /// <see cref="int.MaxValue"/> answers them all.
    /// </summary>
    public int WaitsAtSecretRequest
    {
        get => _vault.WaitsAtSecretRequest;
        set => _vault.WaitsAtSecretRequest = value;
    }

    /// <summary>How many secret requests the vault has been sent.</summary>
    public int SecretRequests => _vault.SecretRequests;

    /// <summary>Every audit row written so far, oldest first.</summary>
    public IReadOnlyList<JsonElement> AuditRows()
    {
        var folder = Path.Combine(_temp.Path, "ExchangeAdminWeb");
        if (!Directory.Exists(folder))
            return [];
        return Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(file => !Path.GetFileNameWithoutExtension(file).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(row => row.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
            .ToList();
    }

    public void Dispose()
    {
        _extendedLog.Dispose();
        _vault.Dispose();
        _temp.Dispose();
    }

    private sealed class Vault : HttpMessageHandler
    {
        private int _cancelled;
        private int _secretRequests;

        public bool WasCancelled => Volatile.Read(ref _cancelled) == 1;

        public int SecretRequests => Volatile.Read(ref _secretRequests);

        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TimesOut { get; set; }

        public int WaitsAtSecretRequest { get; set; } = 1;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"test-token","expires_in":3600}""") };

            if (Interlocked.Increment(ref _secretRequests) != WaitsAtSecretRequest && !TimesOut)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"items":[{"fieldName":"Username","itemValue":"svc-module"},{"fieldName":"Password","itemValue":"not-a-secret"},{"fieldName":"Domain","itemValue":"CONTOSO"}]}"""),
                };

            Requested.TrySetResult();
            if (TimesOut)
                throw new TaskCanceledException("the vault request timed out"); // HttpClient's own timeout: nobody asked for it

            try
            {
                await Task.Delay(Bound, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _cancelled, 1);
                throw;
            }

            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
        }
    }
}
