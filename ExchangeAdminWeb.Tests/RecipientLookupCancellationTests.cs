using System.Net;
using System.Reflection;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using Page = ExchangeAdminWeb.Components.Pages.RecipientLookup;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-37. Recipient Lookup ran under an activity whose token the navigation guard cancels, but the token stopped at
/// the page: the lookup service, the module credential lookup and the vault request never saw it, so an operator's cancel
/// during a credential read was not one - the vault request ran on, and the lookup finished with a warning as if nothing had
/// been asked of it. The token now goes from the page to the vault; a vault request that times out with nobody cancelling is
/// still a read that failed, a warning (kd-14's rule).
/// </summary>
/// <remarks>
/// The real Recipient Lookup service over <see cref="MigrationStubExchange"/> - a recipient Exchange Online holds as a mail
/// user with no mailbox there, so the location read asks on-premises Exchange - with its on-premises credentials fetched
/// through a real <see cref="ModuleCredentialService"/> and a real <see cref="DelineaService"/> over a stand-in vault, whose
/// secret request waits until its caller cancels. No vault, no Exchange.
/// </remarks>
public sealed class RecipientLookupCancellationTests : IDisposable
{
    private const string Address = "alice@contoso.test";

    /// <summary>Far above any scheduling stall, and short enough that a test which will not finish says so.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private readonly MigrationStubExchange _exchange = new() { OnPremConfigured = true };
    private readonly TempDir _temp = new();
    private readonly ExtendedLogService _extendedLog;
    private readonly Vault _vault = new();
    private readonly DelineaService _delinea;
    private readonly RecipientLookupService _service;

    public RecipientLookupCancellationTests()
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "MailUser"));
        _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://vault.invalid",
            ["Audit:LogRoot"] = _temp.Path,
            ["OperationTrace:Enabled"] = "false",
        }).Build();
        _extendedLog = new ExtendedLogService(config, _exchange.Environment, new AppSettingRepository(TestConfigStore.Create(_temp.Path)),
            NullLogger<ExtendedLogService>.Instance);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_vault, disposeHandler: false));
        _delinea = new DelineaService(http, config, NullLogger<DelineaService>.Instance, _extendedLog, _exchange.Trace,
            _ => ("fixture-client", "fixture-key"));
        _service = _exchange.RecipientLookup(credentials: _exchange.Credentials("RecipientLookup", _delinea, secretId: 7));
    }

    public void Dispose()
    {
        _exchange.Dispose();
        _extendedLog.Dispose();
        _vault.Dispose();
        _temp.Dispose();
    }

    // The finding, at the service, at each on-premises read's credential fetch - the location read's, and the size read's for
    // a mailbox the location read has just placed on-premises. Before: the vault request ran its course and the lookup came
    // back with a warning, as if nothing had been asked of it.
    [Theory]
    [InlineData("the location read", 1)]
    [InlineData("the on-premises size read", 2)]
    public async Task ACancelDuringACredentialRead_SurfacesAsACancel(string read, int secretRequest)
    {
        Assert.NotEmpty(read);
        _vault.WaitsAtSecretRequest = secretRequest;
        using var cancel = new CancellationTokenSource();
        var lookup = _service.GetRecipientInfoAsync(Address, cancel.Token);
        await _vault.Waiting.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
    }

    // The finding, at the page: the activity's token is the one the navigation guard cancels, and the page passes it. Before:
    // the lookup finished with a location warning and was audited as a lookup that had been made.
    [Fact]
    public async Task ACancelOfTheLookupsActivity_EndsTheLookupAsCancelled()
    {
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var page = new Page();
        Inject(page, "LookupService", _service);
        Inject(page, "Audit", _exchange.Audit);
        Inject(page, "Logger", NullLogger<Page>.Instance);
        Inject(page, "Progress", progress);
        PageField("emailAddress").SetValue(page, Address);
        PageField("currentUser").SetValue(page, "CONTOSO\\operator");

        var run = (Task)typeof(Page).GetMethod("RunLookup", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [])!;
        await _vault.Waiting.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await run;

        var shown = Assert.IsType<RecipientInfoResult>(PageField("result").GetValue(page));
        Assert.Contains("cancel", shown.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(shown.Warnings);
        Assert.Equal((false, shown.Error), Assert.Single(progress.Handles).Outcome);
        Assert.Equal(shown.Error, Assert.Single(_exchange.AuditEvents()).GetProperty("error").GetString());
    }

    // Finding kd-61. The credential paths that answer before the vault is reached - no secret ID set here - looked at no
    // token, so a lookup the operator had already cancelled ran to the end: a warning shown, the activity completed as
    // succeeded, and a success row in the audit log.
    [Fact]
    public async Task ACancelBeforeACredentialReadThatNeverReachesTheVault_IsStillACancel()
    {
        var service = _exchange.RecipientLookup(credentials: _exchange.Credentials("RecipientLookup", _delinea, secretId: null));
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var progress = new RecordingProgress(cancel.Token);
        var page = new Page();
        Inject(page, "LookupService", service);
        Inject(page, "Audit", _exchange.Audit);
        Inject(page, "Logger", NullLogger<Page>.Instance);
        Inject(page, "Progress", progress);
        PageField("emailAddress").SetValue(page, Address);
        PageField("currentUser").SetValue(page, "CONTOSO\\operator");

        await (Task)typeof(Page).GetMethod("RunLookup", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [])!;

        var shown = Assert.IsType<RecipientInfoResult>(PageField("result").GetValue(page));
        Assert.Contains("cancel", shown.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(shown.Warnings);
        Assert.Equal((false, shown.Error), Assert.Single(progress.Handles).Outcome);
        var row = Assert.Single(_exchange.AuditEvents());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal(shown.Error, row.GetProperty("error").GetString());
        Assert.False(_vault.Waiting.Task.IsCompleted); // nothing was asked of the vault
    }

    // The control, kd-14's rule: a vault request that gives up on its own, with nobody cancelling, is a read that failed - a
    // warning naming it, and the lookup finishes.
    [Fact]
    public async Task AVaultRequestThatTimesOutUnasked_IsStillAWarning()
    {
        _vault.TimesOut = true;
        using var cancel = new CancellationTokenSource();

        var result = await _service.GetRecipientInfoAsync(Address, cancel.Token);

        Assert.Null(result.Error);
        Assert.Equal("Unknown", result.MailboxLocation);
        Assert.Contains("Could not determine mailbox location: on-premises Exchange could not be asked: "
            + "the module's on-premises Exchange credentials could not be retrieved", result.Warnings);
    }

    private static void Inject(Page page, string property, object value) =>
        typeof(Page).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(page, value);

    private static FieldInfo PageField(string name) => typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    // The vault: a token for the asking; the secret request it is told to wait at waits for its caller to cancel, the ones
    // before it answer, and with TimesOut the first gives up by itself. Every wait is bounded, so a token dropped on the way
    // fails the test rather than hanging it.
    private sealed class Vault : HttpMessageHandler
    {
        private int _secretRequests;

        /// <summary>Signalled once the secret request named by <see cref="WaitsAtSecretRequest"/> has been made.</summary>
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WaitsAtSecretRequest { get; set; } = 1;

        public bool TimesOut { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal))
                return Answer("""{"access_token":"test-token","expires_in":3600}""");

            if (TimesOut)
                throw new TaskCanceledException("the vault request timed out"); // HttpClient's own timeout: nobody asked for it

            if (Interlocked.Increment(ref _secretRequests) < WaitsAtSecretRequest)
                return Answer("""{"items":[{"fieldName":"Username","itemValue":"svc-lookup"},{"fieldName":"Password","itemValue":"not-a-secret"},{"fieldName":"Domain","itemValue":"CONTOSO"}]}""");

            Waiting.TrySetResult();
            await Task.Delay(Bound, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
        }

        private static HttpResponseMessage Answer(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
