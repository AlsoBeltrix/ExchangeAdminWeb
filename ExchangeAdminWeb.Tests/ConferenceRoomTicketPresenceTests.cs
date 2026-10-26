using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.ConferenceRooms;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-7. The four Conference Rooms writes already asked ServiceNow about their ticket, but the dormant
/// client passes every ticket - a blank one included - so on a deployment without the integration the
/// buttons' emptiness clauses were the only thing requiring one. A blank ticket is now refused in the
/// handler, before the authorization re-check and anything after it, with an audited denial that says
/// nothing was attempted.
/// </summary>
/// <remarks>
/// The compiled page's handlers are driven by reflection with only what runs up to and including
/// the ticket check set: the progress frame, ServiceNow, the audit log, and the authorization
/// service the next step calls. A substitute authorization service that is never called is the proof
/// the handler stopped at the ticket; the control lets it be called, and refuses there, so nothing
/// past it - the protection gate, Exchange, the job queue - is reached in any test.
/// </remarks>
public sealed class ConferenceRoomTicketPresenceTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Operator = "CONTOSO\\operator";
    private const string Room = "room1@contoso.com";
    private const string Ticket = "INC0042";

    private readonly TempDir _temp = new();
    private readonly IConfiguration _config;
    private readonly AuditService _audit;
    private readonly ActivityProgressService _progress = new();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();

    public ConferenceRoomTicketPresenceTests()
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

    /// <summary>The four writes, by handler, with the audit action and target each records.</summary>
    public static TheoryData<string> Writes() => new() { "SetupSingleRoom", "ApplyFinderCsv", "SetSingleRoomType", "ApplyTypeCsv" };

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
    public async Task AWriteWithNoTicket_IsRefusedAndAudited_BeforeAnythingElseRuns(string write, string ticket)
    {
        // ServiceNow is dormant here - the deployment today - and the dormant client passes a blank
        // ticket, so only the handler's own presence check can refuse this.
        var page = PageFor(write, ticket, DormantServiceNow());

        await Run(page, write);

        AssertRefused(page, write, ticket, "Ticket validation failed: Ticket number is required");
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task AWriteWithATicketServiceNowRejects_IsRefusedWithTheSameAuditedDenial(string write)
    {
        // The refusal that already existed, on an enabled deployment: it now says, as the blank one
        // does, that nothing was attempted.
        var (serviceNow, requests) = EnabledServiceNow("""{"result":[]}""");
        var page = PageFor(write, Ticket, serviceNow);

        await Run(page, write);

        AssertRefused(page, write, Ticket, $"Ticket validation failed: Ticket {Ticket} not found in ServiceNow");
        Assert.Single(requests);
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task AWriteWithATicket_GoesOnToTheAuthorizationCheck(string write)
    {
        // The control: the same fixture with a ticket the dormant client passes. The handler goes on
        // to its authorization re-check - called once, refusing - so an uncalled check above is the
        // ticket refusal, not a fixture that never gets that far. Since kd-52 that refusal is itself
        // audited, so the row here must be the access one, never the ticket one.
        var page = PageFor(write, Ticket, DormantServiceNow());

        await Run(page, write);

        Assert.Single(_authorization.ReceivedCalls());
        var row = Assert.Single(AuditRows());
        Assert.Equal(IsType(write) ? "ConferenceRooms_SetType" : "ConferenceRooms_SetMetadata", row.GetProperty("action").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal("Authorization", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal("Authorization denied.", row.GetProperty("error").GetString());
        Assert.Equal(Ticket, row.GetProperty("ticket").GetString());
        var shown = Shown(page);
        Assert.False(shown.Success);
        Assert.Equal("Authorization denied.", shown.Message);
    }

    [Fact]
    public void RequireTicket_RefusesOnlyABlankTicket()
    {
        Assert.False(ConferenceRoomService.RequireTicket(null)!.IsValid);
        Assert.False(ConferenceRoomService.RequireTicket("")!.IsValid);
        Assert.False(ConferenceRoomService.RequireTicket(" \t ")!.IsValid);
        Assert.False(ConferenceRoomService.RequireTicket("\u0000")!.IsValid); // kd-24
        Assert.False(ConferenceRoomService.RequireTicket("\u0001")!.IsValid);
        Assert.Null(ConferenceRoomService.RequireTicket(Ticket));
    }

    // ---- assertions ---------------------------------------------------------------------------

    private void AssertRefused(Page page, string write, string ticket, string refusal)
    {
        var row = Assert.Single(AuditRows());
        Assert.Equal(IsType(write) ? "ConferenceRooms_SetType" : "ConferenceRooms_SetMetadata", row.GetProperty("action").GetString());
        Assert.Equal("ConferenceRooms", row.GetProperty("category").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal(IsBulk(write) ? "(bulk)" : Room, row.GetProperty("target").GetString());
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

    private static bool IsType(string write) => write is "SetSingleRoomType" or "ApplyTypeCsv";

    private static bool IsBulk(string write) => write is "ApplyFinderCsv" or "ApplyTypeCsv";

    private static RoomOperationResult Shown(Page page) =>
        (RoomOperationResult)typeof(Page).GetField("result", PrivateInstance)!.GetValue(page)!;

    // ---- the page -----------------------------------------------------------------------------

    private Page PageFor(string write, string ticket, ServiceNowService serviceNow)
    {
        var page = new Page();
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, Operator)], "Negotiate"))));

        SetProperty(page, "Audit", _audit);
        SetProperty(page, "ServiceNow", serviceNow);
        SetProperty(page, "AuthStateProvider", authState);
        SetProperty(page, "AuthorizationService", _authorization);
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetProperty(page, "Progress", _progress);
        SetField(page, "currentUser", Operator);
        SetField(page, "clientIpAddress", "10.0.0.5");

        switch (write)
        {
            case "SetupSingleRoom":
                SetField(page, "roomEmail", Room);
                SetField(page, "ticketNumber", ticket);
                break;
            case "ApplyFinderCsv":
                SetField(page, "csvFinderTicket", ticket);
                break;
            case "SetSingleRoomType":
                SetField(page, "typeRoomEmail", Room);
                SetField(page, "selectedRoomType", "Standard");
                SetField(page, "typeTicketNumber", ticket);
                break;
            case "ApplyTypeCsv":
                SetField(page, "csvTypeTicket", ticket);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(write), write, null);
        }

        return page;
    }

    private static Task Run(Page page, string write) =>
        (Task)typeof(Page).GetMethod(write, PrivateInstance)!.Invoke(page, null)!;

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
