using System.Net;
using System.Text;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class DefenderEnrichmentCancellationTests
{
    [Theory]
    [InlineData("secret")]
    [InlineData("refresh-auth")]
    [InlineData("retry-auth")]
    [InlineData("retry-secret")]
    [InlineData("secret-body")]
    [InlineData("auth-body")]
    public async Task ProductionHuntingSetupCancelsVaultRequestsRetriesAndBodyReads(string phase)
    {
        var clock = new D7Clock();
        var reached = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = 0;
        var secret = 0;
        var graph = 0;
        using var http = D7.AsyncHttp(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.security.microsoft.com")
                return D7.Json(D7.Machines("a"));
            if (request.RequestUri.Host == "login.microsoftonline.com")
                return D7.Token();
            if (request.RequestUri.Host == "graph.microsoft.com")
            {
                graph++;
                return D7.Json("{\"results\":[]}");
            }
            var isAuth = request.RequestUri.AbsolutePath == "/oauth2/token";
            var call = isAuth ? ++auth : ++secret;
            var target = isAuth
                ? phase == "refresh-auth" && call == 2 || phase == "retry-auth" && call == 3 || phase == "auth-body" && call == 2
                : phase is "secret" or "secret-body" && call == 2 || phase == "retry-secret" && call == 3;
            if (target)
            {
                var body = isAuth ? "{\"access_token\":\"vault-token\",\"expires_in\":3600}" : Fields;
                if (phase.EndsWith("-body", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ControlledBody(body, reached, release.Task) };
                reached.TrySetResult(token);
                await release.Task;
                token.ThrowIfCancellationRequested();
                return D7.Json(body);
            }
            if (!isAuth && call == 2 && phase != "secret" && phase != "secret-body")
                return D7.Json("{}", HttpStatusCode.Unauthorized);
            if (isAuth && call == 2 && phase == "retry-auth")
                return D7.Json("{\"error\":\"invalid_client\"}", HttpStatusCode.Unauthorized);
            return isAuth ? D7.Json("{\"access_token\":\"vault-token\",\"expires_in\":3600}") : D7.Json(Fields);
        });
        using var fixture = new VaultFixture(http);
        var service = new DefenderEndpointDeviceService(fixture.Config, fixture.Vault, fixture.HttpFactory,
            new AdSiteDirectory(() => new D7Commands(D7.RootOrSubnet)))
        { EnrichmentTimeProvider = clock };
        var listing = service.ListDevicesAsync();
        try
        {
            var ioToken = await reached.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMinutes(4));
            Assert.True(ioToken.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();
        }
        var result = await listing;
        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Single(result.Devices);
        Assert.Equal(DefenderDiscoveryReasons.DeadlineReached, result.EnrichmentStopReason);
        Assert.Equal(0, result.DiscoveryAttemptedBatches);
        Assert.Equal(1, result.DiscoveryUnattemptedBatches);
        Assert.Equal(0, graph);
        Assert.Equal(phase == "retry-auth" ? 3 : phase is "refresh-auth" or "retry-secret" or "auth-body" ? 2 : 1, auth);
        Assert.Equal(phase == "retry-secret" ? 3 : 2, secret);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("request")]
    [InlineData("body")]
    public async Task ApiRequestedCancellationReachesActualIoAndIsNotAnIndependentTimeout(string phase)
    {
        var reached = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var requests = 0;
        using var http = D7.AsyncHttp(async (request, token) =>
        {
            var isToken = request.RequestUri!.Host == "login.microsoftonline.com";
            if (!isToken)
                requests++;
            if (isToken != (phase == "token"))
                return D7.Token();
            if (phase == "body")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ControlledBody("{\"value\":[]}", reached, release.Task) };
            reached.TrySetResult(token);
            await release.Task;
            token.ThrowIfCancellationRequested();
            return isToken ? D7.Token() : D7.Json("{\"value\":[]}");
        });
        var call = D7.Client(http).GetWithStatusAsync("/api/machines", cancellation.Token);
        try
        {
            var token = await reached.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            Assert.True(token.IsCancellationRequested);
        }
        finally { release.TrySetResult(); }
        var error = await Record.ExceptionAsync(async () => await call);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(phase == "token" ? 0 : 1, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledTokenLockWaiterCannotSendALaterRequest(bool vault)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<string>();
        using var http = D7.AsyncHttp(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            if (path.EndsWith("/token", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await release.Task;
                return D7.Token();
            }
            return D7.Json(vault ? Fields : "{\"value\":[]}");
        });
        using var fixture = new VaultFixture(http);
        var api = D7.Client(http);
        using var cancellation = new CancellationTokenSource();
        async Task Invoke(bool second)
        {
            if (vault)
                await fixture.Vault.GetSecretFieldsAsync(second ? 2 : 1, second ? cancellation.Token : default);
            else
                await api.GetWithStatusAsync(second ? "/second" : "/first", second ? cancellation.Token : default);
        }
        var first = Invoke(false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var second = Invoke(true);
        cancellation.Cancel();
        // Controlled release, independent of scheduler speed; no later send may occur.
        release.TrySetResult();
        await first;
        var error = await Record.ExceptionAsync(() => second);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(2, requests.Count);
        Assert.DoesNotContain(requests, path => path is "/second" or "/api/v1/secrets/2");
    }

    [Fact]
    public async Task IndependentHttpTimeoutRetainsTimedOutResultWithOptionalToken()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = D7.Http((_, _) => throw new TaskCanceledException("synthetic independent HTTP timeout"));
        var result = await D7.Client(http).GetWithStatusAsync("/api/machines", cancellation.Token);
        Assert.True(result.TimedOut);
        Assert.Equal(HttpStatusCode.RequestTimeout, result.StatusCode);
        Assert.False(cancellation.IsCancellationRequested);
    }

    private const string Fields = "{\"items\":[{\"fieldName\":\"Tenant ID\",\"itemValue\":\"tenant\"},{\"fieldName\":\"Application ID\",\"itemValue\":\"client\"},{\"fieldName\":\"Client Secret\",\"itemValue\":\"secret\"}]}";

    private sealed class ControlledBody(string body, TaskCompletionSource<CancellationToken> reached, Task release) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, default);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            reached.TrySetResult(cancellationToken);
            await release;
            cancellationToken.ThrowIfCancellationRequested();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(body), cancellationToken);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    internal sealed class VaultFixture : IDisposable
    {
        private readonly TempDir _temp = new();
        private readonly ExtendedLogService _extended;
        internal ModuleConfigService Config { get; }
        internal DelineaService Vault { get; }
        internal IHttpClientFactory HttpFactory { get; } = Substitute.For<IHttpClientFactory>();
        internal VaultFixture(HttpClient http)
        {
            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(_temp.Path);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delinea:SecretServerUrl"] = "https://vault.example",
                ["Audit:LogRoot"] = _temp.Path,
                ["OperationTrace:Enabled"] = "false"
            }).Build();
            var repository = TestConfigStore.CreateModuleConfig(_temp.Path);
            repository.SaveModule("DefenderEndpointDevices", new Dictionary<string, string> { ["GraphDelineaSecretId"] = "1" });
            Config = new ModuleConfigService(new ModuleCatalog(), environment, repository, NullLogger<ModuleConfigService>.Instance);
            HttpFactory.CreateClient(Arg.Any<string>()).Returns(http);
            _extended = new ExtendedLogService(config, environment, new AppSettingRepository(TestConfigStore.Create(_temp.Path)), NullLogger<ExtendedLogService>.Instance);
            var trace = new OperationTraceService(config, new JsonlLogService(config, NullLogger<JsonlLogService>.Instance));
            Vault = new DelineaService(HttpFactory, config, NullLogger<DelineaService>.Instance, _extended, trace, _ => ("fixture-client", "fixture-key"));
        }
        public void Dispose() { _extended.Dispose(); _temp.Dispose(); }
    }
}
