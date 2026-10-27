using System.Security.Claims;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using Page = ExchangeAdminWeb.Components.Pages.EmergencyDisable;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-72 on Emergency Disable: <c>ExecuteDisable</c> read the ticket, the lockdown choice and the resolved account
/// after its yield and the authorization re-check, so a keystroke or a tick landing there - or a lookup, which clears
/// all three - changed the account disabled and what the write and its record carried. They are now read above the
/// yield, and a second click during a disable is ignored.
/// </summary>
/// <remarks>
/// Driven through the compiled handler over the real <see cref="EmergencyDisableService"/>: the account is not
/// protected and no AD credential is configured, so the service refuses at the credential step with a row naming the
/// account and the ticket it was handed - before any directory or Graph call.
/// <see cref="SubmittedValueHarness.HeldContinuations"/> holds the handler at its yield. The lockdown choice is
/// recorded only by the write path, so its capture is pinned by reading the handler.
/// </remarks>
public sealed class EmergencyDisableSubmittedDisableTests : IDisposable
{
    private const string TicketA = "INC0001";

    private static readonly ResolvedDirectoryPrincipal AccountA = new(
        "Test", "First Account", "first.account@contoso.com", "first.account", "first.account@contoso.com",
        "CN=First Account,DC=contoso,DC=test", "11111111-2222-4333-8444-555555555555", null);

    private readonly TempDir _temp = new();
    private readonly AuditService _audit;
    private readonly RecordingProgress _progress = new();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();
    private readonly EmergencyDisableService _service;
    private readonly NobodyProtected _protection;

    public EmergencyDisableSubmittedDisableTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
            ["Delinea:SecretServerUrl"] = "https://secrets.invalid",
        }).Build();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, log);
        _audit = new AuditService(log, trace);

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env,
            TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var delinea = new DelineaService(http, config, NullLogger<DelineaService>.Instance,
            new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_temp.Path), NullLogger<ExtendedLogService>.Instance),
            trace);

        // Reached in order until the credential step refuses: the ticket check, the protection check (nobody is
        // protected) and the credential lookup (none configured). Nothing past it - servicers, email, Graph - is used.
        _service = new EmergencyDisableService(
            new ModuleCredentialService(moduleConfig, delinea, NullLogger<ModuleCredentialService>.Instance),
            moduleConfig,
            _protection = new NobodyProtected(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_temp.Path), delinea),
            null!, trace, _audit, null!, delinea, http, env, config, NullLogger<EmergencyDisableService>.Instance);

        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(Task.FromResult(AuthorizationResult.Success()));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ALookupOrAKeystrokeLandingInTheYield_LeavesTheDisableOnTheAccountAndTicketConfirmed()
    {
        var page = PageFor();

        RunWithChangeWhileSuspended(() => Call(page, "ExecuteDisable"), () =>
        {
            // A lookup's BeginNewOperation, which clears the account, the ticket and the lockdown choice - or a
            // keystroke in the ticket box and a tick of the lockdown box.
            SetField(page, "resolvedPrincipal", null);
            SetField(page, "ticketNumber", "INC9999");
            SetField(page, "moveToLockdownOu", false);
        });

        Assert.Equal($"Disabling {AccountA.DisplayName}", Assert.Single(_progress.Handles).Label);
        var row = Assert.Single(AuditRows(_temp.Path));
        Assert.Equal("EmergencyDisable_Refused", Text(row, "action"));
        Assert.Equal(AccountA.UserPrincipalName, Text(row, "target"));
        Assert.Equal(TicketA, Text(row, "ticket"));
        Assert.Equal("GetADCredentials", Text(row, "refusedAt"));
    }

    [Fact]
    public void ASecondClickWhileADisableRuns_IsIgnored_NotRunBesideIt()
    {
        var page = PageFor();

        RunWithChangeWhileSuspended(() => Call(page, "ExecuteDisable"), () =>
        {
            // A click the browser sent before the disabled button rendered.
            var second = Call(page, "ExecuteDisable");
            Assert.True(second.IsCompleted, "a second click during a disable started a second disable");
        });

        Assert.Single(_progress.Handles);
        Assert.Single(AuditRows(_temp.Path));
        Assert.Single(_authorization.ReceivedCalls());
    }

    [Fact]
    public void ASecondLookupWhileALookupRuns_IsIgnored_NotRunBesideIt()
    {
        var page = PageFor();
        SetField(page, "searchIdentity", AccountA.UserPrincipalName);

        RunWithChangeWhileSuspended(() => Call(page, "PerformLookup"), () =>
        {
            var second = Call(page, "PerformLookup");
            Assert.True(second.IsCompleted, "a second lookup during a lookup started a second lookup");
        });

        Assert.Equal($"Looking up {AccountA.UserPrincipalName}", Assert.Single(_progress.Handles).Label);
        Assert.Equal(1, _protection.Resolves);
    }

    [Fact]
    public void TheLockdownChoice_IsTheOneConfirmed_ReadAboveTheYieldAndHandedToTheWrite()
    {
        // The choice travels only into the write, which the runtime test above stops short of, so its capture is
        // read from the handler: taken above the first await, and the local - not the box - handed to DisableAsync.
        var body = ClickGateSource.Load("EmergencyDisable.razor").MethodBody("ExecuteDisable");
        var capture = body.IndexOf("var moveToLockdown = moveToLockdownOu;", StringComparison.Ordinal);
        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);

        Assert.True(capture >= 0, "ExecuteDisable no longer captures the lockdown choice");
        Assert.True(capture < firstAwait, "the lockdown choice is captured below the first await");
        Assert.Matches(new Regex(@"DisableService\.DisableAsync\(principal, ticket, [^;]*, moveToLockdown\);"), body);
    }

    private Page PageFor()
    {
        var page = new Page();
        var auth = Substitute.For<AuthenticationStateProvider>();
        auth.GetAuthenticationStateAsync().Returns(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, @"CONTOSO\operator")], "Negotiate"))));
        Set(page, "DisableService", _service);
        Set(page, "ProtectedPrincipalService", _protection);
        Set(page, "Progress", _progress);
        Set(page, "Audit", _audit);
        Set(page, "AuthStateProvider", auth);
        Set(page, "AuthorizationService", _authorization);
        Set(page, "Logger", NullLogger<Page>.Instance);
        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "resolvedPrincipal", AccountA);
        SetField(page, "confirmed", true);
        SetField(page, "ticketNumber", TicketA);
        SetField(page, "moveToLockdownOu", true);
        return page;
    }

    /// <summary>
    /// Nobody is protected, so the service goes on to its credential step; a lookup finds nobody, and is counted.
    /// </summary>
    private sealed class NobodyProtected(IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
        ExchangeAdminWeb.Services.Storage.ProtectedPrincipalRepository repository, DelineaService delinea)
        : ProtectedPrincipalService(env, config, moduleConfig, repository, delinea, NullLogger<ProtectedPrincipalService>.Instance)
    {
        private int _resolves;

        public int Resolves => Volatile.Read(ref _resolves);

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target) =>
            Task.FromResult(ProtectedPrincipalResult.NotProtected());

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity)
        {
            Interlocked.Increment(ref _resolves);
            return Task.FromResult<(ResolvedDirectoryPrincipal?, ResolutionStatus)>((null, ResolutionStatus.NotFound));
        }
    }
}
