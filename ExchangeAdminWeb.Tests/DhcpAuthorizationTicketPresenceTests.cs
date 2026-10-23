using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.DhcpAuthorization;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-18. Authorizing a DHCP server asked ServiceNow about its ticket, but the dormant client passes
/// every ticket - a blank one included - so on a deployment without the integration the Authorize
/// button's emptiness clause was the only thing requiring one. A blank ticket is now refused in the
/// handler, before the authorization re-check and the forest write, with a Failed row that says
/// nothing was attempted. Deauthorize already checked presence itself and is not changed.
/// </summary>
/// <remarks>
/// AuthorizeServer is driven by reflection with only what runs up to the next step set: ServiceNow,
/// the audit log, and a refusing authorization service. An authorization service that is never called
/// is the proof the handler stopped at the ticket; the control lets it be called and refuses there, so
/// the DHCP service is never reached in any test.
/// </remarks>
public sealed class DhcpAuthorizationTicketPresenceTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Ticket = "INC0042";
    private const string Server = "dhcp01.contoso.com";
    private const string Address = "10.0.0.67";

    private readonly TempDir _temp = new();
    private readonly IConfiguration _config;
    private readonly AuditService _audit;
    private readonly ActivityProgressService _progress = new();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();

    public DhcpAuthorizationTicketPresenceTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
        }).Build();
        var log = new JsonlLogService(_config, NullLogger<JsonlLogService>.Instance);
        _audit = new AuditService(log, new OperationTraceService(_config, log));

        // The step after the ticket check refuses, so a control that gets past it stops there.
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(Task.FromResult(AuthorizationResult.Failed()));
    }

    public void Dispose()
    {
        _progress.Dispose();
        _temp.Dispose();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0001")] // kd-24: a control-only ticket is no ticket either
    public async Task AnAuthorizeWithNoTicket_IsRefusedAndAudited_BeforeAnythingElseRuns(string ticket)
    {
        // ServiceNow is dormant here - the deployment today - and the dormant client passes a blank
        // ticket, so only the handler's own presence check can refuse this.
        var page = PageFor(ticket, DormantServiceNow());

        await Authorize(page);

        AssertRefused(page, ticket, "Ticket validation failed: Ticket number is required");
    }

    [Fact]
    public async Task AnAuthorizeWithATicketServiceNowRejects_IsRefusedWithTheSameAuditedDenial()
    {
        var (serviceNow, requests) = EnabledServiceNow("""{"result":[]}""");
        var page = PageFor(Ticket, serviceNow);

        await Authorize(page);

        AssertRefused(page, Ticket, $"Ticket validation failed: Ticket {Ticket} not found in ServiceNow");
        Assert.Single(requests);
    }

    [Fact]
    public async Task AnAuthorizeWithATicket_GoesOnToTheAuthorizationCheck()
    {
        // The control: a ticket the dormant client passes, and the handler goes on to its
        // authorization re-check - called once, refusing.
        var page = PageFor(Ticket, DormantServiceNow());

        await Authorize(page);

        Assert.Single(_authorization.ReceivedCalls());
        Assert.DoesNotContain(AuditRows(), row => row.TryGetProperty("refusedAt", out _));
        var shown = Shown(page);
        Assert.False(shown.Success);
        Assert.Equal("Authorization denied.", shown.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\u0001")]
    public async Task ADeauthorizeWithNoTicket_IsRefusedBeforeAnythingElseRuns(string ticket)
    {
        // RemoveServer always checked presence itself; since kd-24 by the shared rule, so a
        // control-only ticket - which IsNullOrWhiteSpace let through to the dormant client - is
        // refused there as well.
        var page = PageFor(ticket, DormantServiceNow());

        await (Task)typeof(Page).GetMethod("RemoveServer", PrivateInstance)!.Invoke(
            page, [new DhcpServerEntry { DnsName = Server, IpAddress = Address }])!;

        Assert.Empty(_authorization.ReceivedCalls());
        var row = Assert.Single(AuditRows());
        Assert.Equal("DHCP_Deauthorize", row.GetProperty("action").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal("Ticket number not provided", row.GetProperty("error").GetString());
        Assert.False(Shown(page).Success);
    }

    // ---- assertions ---------------------------------------------------------------------------

    private void AssertRefused(Page page, string ticket, string refusal)
    {
        var row = Assert.Single(AuditRows());
        Assert.Equal("DHCP_Authorize", row.GetProperty("action").GetString());
        Assert.Equal("DhcpAuthorization", row.GetProperty("category").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal($"{Server} ({Address})", row.GetProperty("target").GetString());
        Assert.Equal(refusal, row.GetProperty("error").GetString());
        Assert.Equal("TicketValidation", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());

        // A blank ticket is recorded as none: the writer leaves a null field out of the row.
        if (string.IsNullOrWhiteSpace(ticket))
            Assert.False(row.TryGetProperty("ticket", out var recorded) && recorded.ValueKind != JsonValueKind.Null);
        else
            Assert.Equal(ticket, row.GetProperty("ticket").GetString());

        Assert.Empty(_authorization.ReceivedCalls());
        var shown = Shown(page);
        Assert.False(shown.Success);
        Assert.Equal(refusal, shown.Message);
    }

    private static DhcpOperationResult Shown(Page page) =>
        (DhcpOperationResult)typeof(Page).GetField("operationResult", PrivateInstance)!.GetValue(page)!;

    // ---- the page -----------------------------------------------------------------------------

    private Page PageFor(string ticket, ServiceNowService serviceNow)
    {
        var page = new Page();
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "CONTOSO\\operator")], "Negotiate"))));

        SetProperty(page, "Audit", _audit);
        SetProperty(page, "ServiceNow", serviceNow);
        SetProperty(page, "AuthStateProvider", authState);
        SetProperty(page, "AuthorizationService", _authorization);
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetProperty(page, "Progress", _progress);
        SetField(page, "currentUser", "CONTOSO\\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "ticketNumber", ticket);
        SetField(page, "newDnsName", Server);
        SetField(page, "newIpAddress", Address);
        return page;
    }

    private static Task Authorize(Page page) =>
        (Task)typeof(Page).GetMethod("AuthorizeServer", PrivateInstance)!.Invoke(page, null)!;

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
