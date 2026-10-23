using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.NamedLocations;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-7. Both writes on Named Locations require a ticket in the handler, not only in the Save and
/// Confirm Delete emptiness clauses: a blank ticket, or one ServiceNow rejects where that
/// integration is enabled, is refused with an audited denial before anything is sent to Graph.
/// </summary>
/// <remarks>
/// The compiled page's handlers are driven by reflection with their injected services set, as in
/// <see cref="M365GroupManagementTicketGateTests"/>. Behind them is a real
/// <see cref="NamedLocationsService"/> with no Graph secret, so a write that IS reached throws "not
/// configured" into the handler's catch, which audits it WITHOUT mutationAttempted and emails it. A
/// refusal is therefore one row, carrying mutationAttempted false, and no email.
/// </remarks>
public sealed class NamedLocationsTicketGateTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Operator = "CONTOSO\\operator";
    private const string Ticket = "INC0042";

    private static readonly NamedLocation Office = new()
    {
        Id = "7c1d2e3f-4a5b-6c7d-8e9f-a0b1c2d3e4f5",
        DisplayName = "Head office",
        LocationType = NamedLocationType.Ip,
        IsTrusted = true,
        IpRanges = ["10.0.0.0/8"],
    };

    private readonly TempDir _temp = new();
    private readonly IConfiguration _config;
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly NamedLocationsService _locations;
    private readonly ActivityProgressService _progress = new();

    public NamedLocationsTicketGateTests()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "config"));
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
            ["Delinea:SecretServerUrl"] = "https://fake.local",
        }).Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        var moduleConfig = new ModuleConfigService(
            new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var log = new JsonlLogService(_config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(_config, log);
        var extendedLog = new ExtendedLogService(
            _config, env, TestConfigStore.CreateAppSettings(_temp.Path), NullLogger<ExtendedLogService>.Instance);
        var delinea = new DelineaService(http, _config, NullLogger<DelineaService>.Instance, extendedLog, trace);

        _audit = new AuditService(log, trace);
        _email = Substitute.For<EmailService>(_config, NullLogger<EmailService>.Instance);
        _locations = new NamedLocationsService(NullLogger<NamedLocationsService>.Instance, moduleConfig, delinea, http);
    }

    public void Dispose()
    {
        _progress.Dispose();
        _temp.Dispose();
    }

    public static TheoryData<string> Writes() => new() { "Create", "Update", "Delete" };

    public static TheoryData<string, string> WritesWithABlankTicket()
    {
        var data = new TheoryData<string, string>();
        foreach (var write in Writes())
        {
            data.Add(write, "");
            data.Add(write, "   ");
            data.Add(write, "\u0001"); // kd-24: a control-only ticket is no ticket either
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WritesWithABlankTicket))]
    public async Task AWriteWithNoTicket_IsRefusedAndAudited_AndNeverReachesTheService(string write, string ticket)
    {
        // ServiceNow is dormant here - the deployment today - and the dormant client passes every
        // ticket, a blank one included. Only the handler's own presence check can refuse this.
        var page = PageFor(ticket, DormantServiceNow());

        await Run(page, write);

        AssertRefused(page, write, ticket, "A ticket number is required.");
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task AWriteWithATicketServiceNowRejects_IsRefusedAndAudited_AndNeverReachesTheService(string write)
    {
        var (serviceNow, requests) = EnabledServiceNow("""{"result":[]}""");
        var page = PageFor(Ticket, serviceNow);

        await Run(page, write);

        AssertRefused(page, write, Ticket, $"Ticket validation failed: Ticket {Ticket} not found in ServiceNow");
        Assert.Single(requests);
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task AMalformedTicket_IsRefusedWithoutAQuery_WhereServiceNowIsEnabled(string write)
    {
        const string malformed = "INC0042^ORnumberISNOTEMPTY";
        var (serviceNow, requests) = EnabledServiceNow("""{"result":[]}""");
        var page = PageFor(malformed, serviceNow);

        await Run(page, write);

        AssertRefused(page, write, malformed, "Ticket validation failed: Ticket number must be letters and digits only.");
        Assert.Empty(requests);
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task AWriteWithATicket_ReachesTheService(string write)
    {
        // The control for the three above: the same fixture with a ticket the dormant client
        // passes, and the write IS attempted - so one row and no email above is the ticket check
        // refusing, not a fixture that never reaches the service.
        var page = PageFor(Ticket, DormantServiceNow());

        await Run(page, write);

        var row = Assert.Single(AuditRows());
        Assert.Equal(ActionOf(write), row.GetProperty("action").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal(Ticket, row.GetProperty("ticket").GetString());
        AssertTarget(write, row);
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
        Assert.False(row.TryGetProperty("refusedAt", out _));
        Assert.Contains("not configured", row.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, AdminEmails());
    }

    // ---- assertions ---------------------------------------------------------------------------

    private void AssertRefused(Page page, string write, string ticket, string refusal)
    {
        var row = Assert.Single(AuditRows());
        Assert.Equal(ActionOf(write), row.GetProperty("action").GetString());
        Assert.Equal("NamedLocations", row.GetProperty("category").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal(refusal, row.GetProperty("error").GetString());
        Assert.Equal("TicketValidation", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        AssertTarget(write, row);

        // A blank ticket is recorded as none: the writer leaves a null field out of the row.
        if (string.IsNullOrWhiteSpace(ticket))
            Assert.False(row.TryGetProperty("ticket", out var recorded) && recorded.ValueKind != JsonValueKind.Null);
        else
            Assert.Equal(ticket, row.GetProperty("ticket").GetString());

        Assert.Equal(0, AdminEmails());

        var shown = (NamedLocationResult?)typeof(Page).GetField("operationResult", PrivateInstance)!.GetValue(page);
        Assert.NotNull(shown);
        Assert.False(shown.Success);
        Assert.Equal(refusal, shown.Message);
    }

    /// <summary>The row names the location, and its id once it has one.</summary>
    private static void AssertTarget(string write, JsonElement row)
    {
        var target = row.GetProperty("target").GetString() ?? "";
        Assert.Contains(Office.DisplayName, target, StringComparison.Ordinal);
        if (write != "Create")
            Assert.Contains(Office.Id, target, StringComparison.Ordinal);
    }

    private static string ActionOf(string write) => "NamedLocations_" + write;

    private int AdminEmails() =>
        _email.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(EmailService.SendAdminNotificationAsync));

    // ---- the page -----------------------------------------------------------------------------

    private Page PageFor(string ticket, ServiceNowService serviceNow)
    {
        var page = new Page();
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, Operator)], "Negotiate"))));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(Task.FromResult(AuthorizationResult.Success()));

        SetProperty(page, "LocationService", _locations);
        SetProperty(page, "Audit", _audit);
        SetProperty(page, "Email", _email);
        SetProperty(page, "AuthStateProvider", authState);
        SetProperty(page, "AuthorizationService", authorization);
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetProperty(page, "Progress", _progress);
        SetProperty(page, "ServiceNow", serviceNow);
        SetField(page, "currentUser", Operator);
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "formTicketNumber", ticket);
        return page;
    }

    private static Task Run(Page page, string write)
    {
        switch (write)
        {
            case "Create":
                SetField(page, "editingLocation", null);
                SetField(page, "formType", "ip");
                SetField(page, "formDisplayName", Office.DisplayName);
                SetField(page, "formIpRanges", "10.0.0.0/8");
                return Invoke(page, "SaveLocation");
            case "Update":
                SetField(page, "editingLocation", Office);
                SetField(page, "formDisplayName", Office.DisplayName);
                SetField(page, "formIpRanges", "10.0.0.0/8");
                return Invoke(page, "SaveLocation");
            case "Delete":
                return Invoke(page, "DeleteLocation", Office);
            default:
                throw new ArgumentOutOfRangeException(nameof(write), write, null);
        }
    }

    private static Task Invoke(Page page, string method, params object[] args) =>
        (Task)typeof(Page).GetMethod(method, PrivateInstance)!.Invoke(page, args)!;

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, PrivateInstance)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, PrivateInstance)!.SetValue(page, value);

    // ---- ServiceNow ---------------------------------------------------------------------------

    /// <summary>ServiceNow:Enabled unset: the integration is dormant and every ticket validates.</summary>
    private ServiceNowService DormantServiceNow()
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        return new ServiceNowService(_config, NullLogger<ServiceNowService>.Instance, http);
    }

    private static (ServiceNowService Service, List<string> Requests) EnabledServiceNow(string responseBody)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceNow:InstanceUrl"] = "https://snow.local",
            ["ServiceNow:Username"] = "u",
            ["ServiceNow:Password"] = "p",
            ["ServiceNow:Enabled"] = "true",
        }).Build();

        var handler = new Stub(responseBody);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return (new ServiceNowService(config, NullLogger<ServiceNowService>.Instance, http), handler.Requests);
    }

    private sealed class Stub(string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    // ---- the audit log ------------------------------------------------------------------------

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
