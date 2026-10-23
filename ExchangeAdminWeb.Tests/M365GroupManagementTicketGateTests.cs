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
using Page = ExchangeAdminWeb.Components.Pages.M365GroupManagement;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-7. Every write on M365 Group Management requires a ticket in the handler, not only in the
/// buttons' emptiness clauses: a blank ticket, or one ServiceNow rejects where that integration is
/// enabled, is refused with an audited denial before anything is sent to Graph.
/// </summary>
/// <remarks>
/// The compiled page's handlers are driven themselves, by reflection with their injected services
/// set - the kd-4 and AttributeEditor page-handler pattern; no renderer is needed because no handler
/// renders. Behind them is a real <see cref="M365GroupManagementService"/> with no Graph secret and
/// no directory-read secret, so a write that IS reached fails without leaving the process: a group
/// write throws "not configured" into the handler's catch, a membership write fails closed at the
/// service's protection gate, and either way the handler audits a failure WITHOUT mutationAttempted
/// and emails it. A refusal is therefore one row, carrying mutationAttempted false, and no email.
/// </remarks>
public sealed class M365GroupManagementTicketGateTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Operator = "CONTOSO\\operator";
    private const string Ticket = "INC0042";

    private static readonly M365Group Group = new()
    {
        Id = "5f0a1b2c-3d4e-5f60-7182-93a4b5c6d7e8",
        DisplayName = "Finance Team",
        Mail = "finance.team@contoso.com",
    };

    private static readonly M365GroupMember Member = new()
    {
        Id = "0d1e2f30-4152-6374-8596-a7b8c9d0e1f2",
        DisplayName = "Jo Doe",
        Mail = "jo.doe@contoso.com",
        UserPrincipalName = "jo.doe@contoso.com",
    };

    private readonly TempDir _temp = new();
    private readonly IConfiguration _config;
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly M365GroupManagementService _groups;
    private readonly ActivityProgressService _progress = new();

    public M365GroupManagementTicketGateTests()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "config"));
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
            ["Delinea:SecretServerUrl"] = "https://fake.local",
        }).Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(
            catalog, env, TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var log = new JsonlLogService(_config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(_config, log);
        var extendedLog = new ExtendedLogService(
            _config, env, TestConfigStore.CreateAppSettings(_temp.Path), NullLogger<ExtendedLogService>.Instance);
        var delinea = new DelineaService(http, _config, NullLogger<DelineaService>.Instance, extendedLog, trace);
        var protectedPrincipals = new ProtectedPrincipalService(
            env, _config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_temp.Path), delinea,
            NullLogger<ProtectedPrincipalService>.Instance);
        var sectionAccess = new SectionAccessService(
            _config, NullLogger<SectionAccessService>.Instance, env, catalog,
            new ExchangeAdminWeb.Services.Storage.SectionAccessRepository(TestConfigStore.Create(_temp.Path)));
        var servicers = new ProtectedPrincipalServicerService(
            sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance);

        _audit = new AuditService(log, trace);
        _email = Substitute.For<EmailService>(_config, NullLogger<EmailService>.Instance);
        _groups = new M365GroupManagementService(
            moduleConfig, delinea, http, trace, _audit, protectedPrincipals, servicers,
            NullLogger<M365GroupManagementService>.Instance);
    }

    public void Dispose()
    {
        _progress.Dispose();
        _temp.Dispose();
    }

    /// <summary>Every write the page can make, by the handler the markup calls.</summary>
    public static TheoryData<string> Writes() =>
        new() { "Create", "Update", "Delete", "AddMember", "AddOwner", "RemoveMember", "RemoveOwner" };

    public static TheoryData<string, string> WritesWithABlankTicket()
    {
        var data = new TheoryData<string, string>();
        foreach (var write in Writes())
        {
            data.Add(write, "");
            data.Add(write, "   ");
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
        Assert.Contains(
            IsMembership(write) ? "Protection check unavailable" : "not configured",
            row.GetProperty("error").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(1, AdminEmails());
    }

    // ---- assertions ---------------------------------------------------------------------------

    private void AssertRefused(Page page, string write, string ticket, string refusal)
    {
        var row = Assert.Single(AuditRows());
        Assert.Equal(ActionOf(write), row.GetProperty("action").GetString());
        Assert.Equal("M365GroupManagement", row.GetProperty("category").GetString());
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

        var shown = (M365GroupResult?)typeof(Page).GetField("operationResult", PrivateInstance)!.GetValue(page);
        Assert.NotNull(shown);
        Assert.False(shown.Success);
        Assert.Equal(refusal, shown.Message);
    }

    /// <summary>The row names what the click was for: the group, its id once it has one, and the member.</summary>
    private static void AssertTarget(string write, JsonElement row)
    {
        var target = row.GetProperty("target").GetString() ?? "";
        Assert.Contains(Group.DisplayName, target, StringComparison.Ordinal);
        if (write != "Create")
            Assert.Contains(Group.Id, target, StringComparison.Ordinal);
        if (IsMembership(write))
            Assert.StartsWith(Member.UserPrincipalName, target, StringComparison.Ordinal);
    }

    private static bool IsMembership(string write) => write is "AddMember" or "AddOwner" or "RemoveMember" or "RemoveOwner";

    private static string ActionOf(string write) => "M365GroupManagement_" + write;

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

        SetProperty(page, "GroupService", _groups);
        SetProperty(page, "Audit", _audit);
        SetProperty(page, "Email", _email);
        SetProperty(page, "AuthStateProvider", authState);
        SetProperty(page, "AuthorizationService", authorization);
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetProperty(page, "Progress", _progress);
        SetProperty(page, "ServiceNow", serviceNow);
        SetField(page, "currentUser", Operator);
        SetField(page, "clientIpAddress", "10.0.0.5");

        // Both ticket boxes: the form's (create, update, delete) and the member panel's.
        SetField(page, "formTicketNumber", ticket);
        SetField(page, "memberTicketNumber", ticket);
        SetField(page, "selectedGroup", Group);
        SetField(page, "members", new List<M365GroupMember> { Member });
        SetField(page, "owners", new List<M365GroupMember> { Member });
        return page;
    }

    private static Task Run(Page page, string write)
    {
        switch (write)
        {
            case "Create":
                SetField(page, "editingGroup", null);
                SetField(page, "formDisplayName", Group.DisplayName);
                SetField(page, "formMailNickname", "financeteam");
                return Invoke(page, "SaveGroup");
            case "Update":
                SetField(page, "editingGroup", Group);
                SetField(page, "formDisplayName", Group.DisplayName);
                return Invoke(page, "SaveGroup");
            case "Delete":
                return Invoke(page, "DeleteGroup", Group);
            case "AddMember":
                SetField(page, "addMemberIdentity", Member.UserPrincipalName);
                return Invoke(page, "AddMember");
            case "AddOwner":
                SetField(page, "addOwnerIdentity", Member.UserPrincipalName);
                return Invoke(page, "AddOwner");
            case "RemoveMember":
                return Invoke(page, "RemoveMember", Member);
            case "RemoveOwner":
                return Invoke(page, "RemoveOwner", Member);
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
