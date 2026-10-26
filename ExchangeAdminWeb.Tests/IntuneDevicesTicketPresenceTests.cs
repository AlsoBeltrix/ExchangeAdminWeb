using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.IntuneDevices;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-45. The device action's ticket presence is the shared <see cref="TicketPresence"/> rule, so a ticket
/// made only of control or zero-width characters is refused as a blank one is - it used to pass
/// <c>IsNullOrWhiteSpace</c> and then the dormant ServiceNow client, and reach Graph.
/// </summary>
/// <remarks>
/// The compiled page's handler is driven by reflection with its injected services set (this branch's
/// page-handler pattern). The device has no primary user, so the protected-principal check has nothing to
/// resolve and the ticket is the last thing between the confirm and the write; the stand-in Graph client
/// factory counts the write's attempts and then hands back no client, so a write that IS reached fails
/// closed and nothing leaves the process.
/// </remarks>
public sealed class IntuneDevicesTicketPresenceTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Operator = "CONTOSO\\operator";

    private readonly TempDir _temp = new();
    private readonly IConfiguration _config;
    private readonly AuditService _audit;
    private readonly ActivityProgressService _progress = new();
    private int _graphWrites;

    public IntuneDevicesTicketPresenceTests()
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
    [InlineData("\u200B")] // kd-48: zero-width only
    [InlineData("\u200E\u200F")] // bidi marks only
    public async Task ATicketWithNothingVisibleInIt_IsRefusedAndAudited_AndNeverReachesGraph(string ticket)
    {
        var device = Laptop();
        var page = PageConfirming(device, ticket);

        await Execute(page, device);

        Assert.Equal(0, _graphWrites);
        var row = Assert.Single(AuditRows());
        Assert.Equal("IntuneDevices_Retire", row.GetProperty("action").GetString());
        Assert.Equal("IntuneDevices", row.GetProperty("category").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal("Ticket number missing", row.GetProperty("error").GetString());
        Assert.Contains("A ticket number is required.", Shown(page, device.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATicketAPersonTyped_ReachesTheWrite()
    {
        // The control for the theory above: the same fixture with a ticket the rule calls present and the
        // dormant client passes, and the write IS attempted - so a zero above is the presence rule refusing,
        // not a fixture that never reaches Graph.
        var device = Laptop();
        var page = PageConfirming(device, "INC0042");

        await Execute(page, device);

        Assert.Equal(1, _graphWrites);
        Assert.DoesNotContain(AuditRows(), row => row.GetProperty("error").GetString() == "Ticket number missing");
    }

    // ---- the page -----------------------------------------------------------------------------

    private static IntuneDevice Laptop() => new()
    {
        Id = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0",
        DeviceName = "LAPTOP-FIN-0042",
        UserPrincipalName = "",
    };

    private Page PageConfirming(IntuneDevice device, string ticket)
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
            new ExchangeAdminWeb.Services.Storage.SectionAccessRepository(TestConfigStore.Create(_temp.Path)));

        SetProperty(page, "IntuneDeviceService", new IntuneDeviceService(() =>
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
        SetProperty(page, "ProtectedPrincipalService", new ProtectedPrincipalService(
            env, _config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_temp.Path), delinea,
            NullLogger<ProtectedPrincipalService>.Instance));
        SetProperty(page, "Servicers", new ProtectedPrincipalServicerService(
            sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance));
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetProperty(page, "Progress", _progress);
        SetField(page, "currentUser", Operator);
        SetField(page, "clientIpAddress", "10.0.0.5");

        typeof(Page).GetMethod("BeginAction", PrivateInstance)!.Invoke(page, [device, IntuneDeviceAction.Retire]);
        SetField(page, "actionTicket", ticket);
        return page;
    }

    private static Task Execute(Page page, IntuneDevice device) =>
        (Task)typeof(Page).GetMethod("ExecuteActionAsync", PrivateInstance)!.Invoke(page, [device, IntuneDeviceAction.Retire])!;

    /// <summary>The message the page shows for the device.</summary>
    private static string Shown(Page page, string deviceId)
    {
        var outcomes = (System.Collections.IDictionary)typeof(Page)
            .GetField("deviceOutcomes", PrivateInstance)!.GetValue(page)!;
        var outcome = outcomes[deviceId]!;
        return (string)outcome.GetType().GetProperty("Message")!.GetValue(outcome)!;
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
