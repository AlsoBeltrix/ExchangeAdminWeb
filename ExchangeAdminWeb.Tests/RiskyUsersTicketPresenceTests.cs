using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.RiskyUsers;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-45. A remediation's ticket presence is the shared <see cref="TicketPresence"/> rule, so a ticket made
/// only of control or zero-width characters is refused as a blank one is - it used to pass
/// <c>IsNullOrWhiteSpace</c> and then the dormant ServiceNow client, and reach Graph.
/// </summary>
/// <remarks>
/// The compiled page's handler is driven by reflection with its injected services set. Two counters stand
/// behind the ticket: the protected-principal resolve, scripted here to find nothing and refuse nothing, and
/// the risk service's Graph client factory, which counts the write's attempts and then hands back no client -
/// so a write that IS reached fails closed and nothing leaves the process.
/// </remarks>
public sealed class RiskyUsersTicketPresenceTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Operator = "CONTOSO\\operator";
    private const string Target = "jo.doe@contoso.com";

    private readonly TempDir _temp = new();
    private readonly IConfiguration _config;
    private readonly AuditService _audit;
    private readonly ActivityProgressService _progress = new();
    private CountingProtection _protection = default!;
    private int _graphWrites;

    public RiskyUsersTicketPresenceTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
            ["Delinea:SecretServerUrl"] = "https://fake.local",
        }).Build();
        var log = new JsonlLogService(_config, NullLogger<JsonlLogService>.Instance);
        _audit = new AuditService(log, new OperationTraceService(_config, log));
    }

    public void Dispose()
    {
        _progress.Dispose();
        _temp.Dispose();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0001")] // kd-24: control only
    [InlineData("​")] // kd-48: zero-width only
    [InlineData("‎‏")] // bidi marks only
    public async Task ATicketWithNothingVisibleInIt_IsRefusedAndAudited_AndNeverReachesGraph(string ticket)
    {
        var page = PageFor(ticket);

        await Execute(page);

        Assert.Equal(0, _graphWrites);
        Assert.Equal(0, _protection.Resolves);
        var row = Assert.Single(AuditRows());
        Assert.Equal("RiskyUsers_Dismiss", row.GetProperty("action").GetString());
        Assert.Equal("RiskyUsers", row.GetProperty("category").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal(Target, row.GetProperty("target").GetString());
        Assert.Equal("Ticket number missing", row.GetProperty("error").GetString());
        Assert.Equal("A ticket number is required.", Shown(page));
    }

    [Fact]
    public async Task ATicketAPersonTyped_GoesOnToTheProtectionCheckAndTheWrite()
    {
        // The control: the same fixture with a ticket the rule calls present and the dormant client
        // passes. The handler goes on to resolve the principal and attempt the write - so the zeroes
        // above are the presence rule refusing, not a fixture that never gets that far.
        var page = PageFor("INC0042");

        await Execute(page);

        Assert.Equal(1, _protection.Resolves);
        Assert.Equal(1, _graphWrites);
        Assert.DoesNotContain(AuditRows(), row => row.GetProperty("error").GetString() == "Ticket number missing");
    }

    // ---- the page -----------------------------------------------------------------------------

    private static RiskyUser Jo() => new()
    {
        Id = "6a7b8c9d-0e1f-4a2b-8c3d-4e5f60718293",
        UserPrincipalName = Target,
        UserDisplayName = "Jo Doe",
        RiskLevel = "high",
        RiskState = "atRisk",
    };

    private Page PageFor(string ticket)
    {
        var page = new Page();
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, Operator)], "Negotiate"))));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(Task.FromResult(AuthorizationResult.Success()));
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(
            catalog, env, TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);
        var log = new JsonlLogService(_config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(_config, log);
        var extendedLog = new ExtendedLogService(
            _config, env, TestConfigStore.CreateAppSettings(_temp.Path), NullLogger<ExtendedLogService>.Instance);
        var delinea = new DelineaService(
            http, _config, NullLogger<DelineaService>.Instance, extendedLog, trace, _ => ("fixture-client", "fixture-key"));
        var sectionAccess = new SectionAccessService(
            _config, NullLogger<SectionAccessService>.Instance, env, catalog,
            new SectionAccessRepository(TestConfigStore.Create(_temp.Path)));
        _protection = new CountingProtection(
            env, _config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_temp.Path), delinea);

        SetProperty(page, "RiskyUsersService", new RiskyUsersService(() =>
        {
            Interlocked.Increment(ref _graphWrites);
            return Task.FromResult<GraphTokenClient?>(null);
        }));
        SetProperty(page, "Audit", _audit);
        SetProperty(page, "AuthStateProvider", authState);
        SetProperty(page, "AuthorizationService", authorization);
        SetProperty(page, "Email", Substitute.For<EmailService>(_config, NullLogger<EmailService>.Instance));
        // ServiceNow:Enabled is unset, so the integration is dormant and every ticket it is asked about
        // validates - which is why the presence rule is what has to refuse these.
        SetProperty(page, "ServiceNow", new ServiceNowService(_config, NullLogger<ServiceNowService>.Instance, http));
        SetProperty(page, "ProtectedPrincipalService", _protection);
        SetProperty(page, "Servicers", new ProtectedPrincipalServicerService(
            sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance));
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetProperty(page, "Progress", _progress);
        SetField(page, "currentUser", Operator);
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "actionTicket", ticket);
        return page;
    }

    private static Task Execute(Page page) =>
        (Task)typeof(Page).GetMethod("ExecuteActionAsync", PrivateInstance)!
            .Invoke(page, [Jo(), RiskyUserAction.Dismiss])!;

    /// <summary>The message the page shows for the row.</summary>
    private static string Shown(Page page)
    {
        var outcomes = (System.Collections.IDictionary)typeof(Page)
            .GetField("rowOutcomes", PrivateInstance)!.GetValue(page)!;
        var outcome = outcomes[Jo().Id]!;
        return (string)outcome.GetType().GetProperty("Message")!.GetValue(outcome)!;
    }

    /// <summary>Finds nothing and refuses nothing, and counts the resolves the handler asks for.</summary>
    private sealed class CountingProtection(IWebHostEnvironment env, IConfiguration config,
        ModuleConfigService moduleConfig, ProtectedPrincipalRepository repository, DelineaService delinea)
        : ProtectedPrincipalService(env, config, moduleConfig, repository, delinea,
            NullLogger<ProtectedPrincipalService>.Instance)
    {
        private int _resolves;

        public int Resolves => _resolves;

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity)
        {
            Interlocked.Increment(ref _resolves);
            return Task.FromResult<(ResolvedDirectoryPrincipal?, ResolutionStatus)>((null, ResolutionStatus.NotFound));
        }

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target) =>
            Task.FromResult(ProtectedPrincipalResult.NotProtected());
    }

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, PrivateInstance)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, PrivateInstance)!.SetValue(page, value);

    private List<JsonElement> AuditRows()
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
}
