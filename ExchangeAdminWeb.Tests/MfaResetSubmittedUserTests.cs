using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using Page = ExchangeAdminWeb.Components.Pages.MfaReset;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-72 on MFA Reset: Delegation Report's production defect in the (b) shape. The UPN box was bound on change with an
/// Enter handler, and Enter's keydown reaches the server before the box's change event, so Enter listed the user the
/// box held before - and the reset that follows acts on the listed user.
/// </summary>
/// <remarks>
/// <c>ListMethods</c> is driven through its compiled code over the real <see cref="MfaResetService"/> with no Graph
/// credential configured, so its read fails at once without reaching Graph and the failure is audited - which is the
/// record this asserts. The handler yields before its read; <see cref="SubmittedValueHarness.HeldContinuations"/> holds
/// it there while the test changes the box.
/// </remarks>
public sealed class MfaResetSubmittedUserTests : IDisposable
{
    private const string UserA = "first.user@contoso.com";
    private const string UserB = "second.user@contoso.com";

    private readonly TempDir _temp = new();
    private readonly AuditService _audit;
    private readonly RecordingProgress _progress = new();
    private readonly MfaResetService _mfa;

    public MfaResetSubmittedUserTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
            ["Delinea:SecretServerUrl"] = "https://fake.local",
        }).Build();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, log);
        _audit = new AuditService(log, trace);

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env,
            new ModuleConfigRepository(TestConfigStore.Create(_temp.Path)), Substitute.For<ILogger<ModuleConfigService>>());
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var extendedLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_temp.Path),
            Substitute.For<ILogger<ExtendedLogService>>());
        var delinea = new DelineaService(http, config, Substitute.For<ILogger<DelineaService>>(), extendedLog, trace);
        _mfa = new MfaResetService(Substitute.For<ILogger<MfaResetService>>(), moduleConfig, delinea, http);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheUpnBox_BindsOnInput_SoEnterListsTheUserItShows()
    {
        var tag = BoundInputTag("MfaReset.razor", "userPrincipalName");

        Assert.Contains("@bind:event=\"oninput\"", tag, StringComparison.Ordinal);
        Assert.Contains("@onkeydown=\"HandleKeyDown\"", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangeLandingWhileTheListingWaits_LeavesTheLabelAndTheAuditRowOnTheUserSubmitted()
    {
        var page = PageFor(UserA);

        RunWithChangeWhileSuspended(() => Call(page, "ListMethods"), () => SetField(page, "userPrincipalName", UserB));

        var handle = Assert.Single(_progress.Handles);
        Assert.Equal($"Reading registered MFA methods for {UserA}", handle.Label);
        var row = Assert.Single(AuditRows(_temp.Path));
        Assert.Equal("MfaReset_ListMethods", Text(row, "action"));
        Assert.Equal(UserA, Text(row, "target"));
        Assert.Equal("Failed", Text(row, "result"));
    }

    [Fact]
    public void ASecondSubmitWhileAListingRuns_IsIgnored_NotRunBesideIt()
    {
        var page = PageFor(UserA);

        RunWithChangeWhileSuspended(() => Call(page, "ListMethods"), () =>
        {
            // A second Enter, or a click the browser sent before the disabled button rendered.
            var second = Call(page, "ListMethods");
            Assert.True(second.IsCompleted, "a second submit during a listing started a second listing");
        });

        Assert.Single(_progress.Handles);
        Assert.Equal(UserA, Text(Assert.Single(AuditRows(_temp.Path)), "target"));
    }

    private Page PageFor(string upn)
    {
        var page = new Page();
        Set(page, "MfaService", _mfa);
        Set(page, "Progress", _progress);
        Set(page, "Audit", _audit);
        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "userPrincipalName", upn);
        return page;
    }
}
