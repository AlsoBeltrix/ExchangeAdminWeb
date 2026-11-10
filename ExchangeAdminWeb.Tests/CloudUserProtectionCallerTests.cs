using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.CloudUserProtectionTests;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Every module that writes an Entra user, driven through its compiled handler or its service over the real protection
/// service: the user its write touches lives in the other domain of the forest, has no Exchange recipient, and is protected by
/// a user row on that account. Resolved by its address it is found nowhere and the write went ahead; resolved by its object id
/// its account is found by SID and the write is refused, in the module's own words, before anything is written.
/// </summary>
public sealed class CloudUserProtectionCallerTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string FabrikamSid = "S-1-5-21-444444444-555555555-666666666";
    private const string Operator = "CONTOSO\\operator";

    private readonly ExchangeWriteHarness _h = new();
    private readonly ActivityProgressService _progress = new();
    private readonly AuditService _audit;
    private readonly ProtectedPrincipalService _protection;
    private readonly ProtectedPrincipalServicerService _servicers;
    private readonly ModuleConfigService _moduleConfig;
    private readonly DelineaService _delinea;
    private readonly IHttpClientFactory _http = Substitute.For<IHttpClientFactory>();

    public CloudUserProtectionCallerTests()
    {
        _h.Forest.AddGroup("Fabrikam Domain Users", domainDn: SelfGrantDirectoryStandIn.OtherDomainDn, domainSid: FabrikamSid,
            rid: SelfGrantDirectoryStandIn.DomainUsersRid);
        var far = _h.Forest.AddUser("far", upn: FarUpn, domainDn: SelfGrantDirectoryStandIn.OtherDomainDn, domainSid: FabrikamSid);
        _h.GraphSids[FarObjectId] = far.Sid;
        var exchange = Substitute.For<IIdentityResolver>();
        exchange.ResolveRecipientAsync(Arg.Any<string>()).Returns((ResolvedRecipient?)null);
        _h.ProtectedValidator(exchange, new ProtectedPrincipalConfig { Users = [FarUpn], UserIds = [$"{far.Sid}|{FarUpn}"] });
        _protection = _h.ProtectedPrincipals!;

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_h.Dir);
        _servicers = new ProtectedPrincipalServicerService(
            new SectionAccessService(_h.Config, NullLogger<SectionAccessService>.Instance, env, new ModuleCatalog(),
                new SectionAccessRepository(TestConfigStore.Create(_h.Dir))),
            NullLogger<ProtectedPrincipalServicerService>.Instance);
        _moduleConfig = _h.ModuleConfig;
        _http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var extendedLog = new ExtendedLogService(_h.Config, env, TestConfigStore.CreateAppSettings(_h.Dir), NullLogger<ExtendedLogService>.Instance);
        _delinea = new DelineaService(_http, _h.Config, NullLogger<DelineaService>.Instance, extendedLog, _h.Trace);
        _audit = _h.Audit;
    }

    public void Dispose()
    {
        _progress.Dispose();
        _h.Dispose();
    }

    // ----- the pages -----

    [Fact]
    public async Task RiskyUsers_RefusesTheProtectedUserInTheOtherDomain_BeforeTheWrite()
    {
        var writes = 0;
        var page = new Components.Pages.RiskyUsers();
        InjectCommon(page);
        Inject(page, "RiskyUsersService", new RiskyUsersService(() =>
        {
            Interlocked.Increment(ref writes);
            return Task.FromResult<GraphTokenClient?>(null);
        }));
        SetField(page, "actionTicket", "INC0042");
        var user = new RiskyUser { Id = FarObjectId, UserPrincipalName = FarUpn, RiskLevel = "high", RiskState = "atRisk" };

        await (Task)page.GetType().GetMethod("ExecuteActionAsync", PrivateInstance)!.Invoke(page, [user, RiskyUserAction.Dismiss])!;

        Assert.Equal(0, writes);
        Assert.Equal("This user is a protected principal. Risk remediation is not permitted.", Outcome(page, "rowOutcomes", FarObjectId));
        Assert.Equal($"Protected principal - matched rules: User:{FarUpn}", RefusalError());
    }

    [Fact]
    public async Task IntuneDevices_RefusesTheProtectedPrimaryUserInTheOtherDomain_BeforeTheWrite()
    {
        var writes = 0;
        var page = new Components.Pages.IntuneDevices();
        InjectCommon(page);
        Inject(page, "IntuneDeviceService", new IntuneDeviceService(() =>
        {
            Interlocked.Increment(ref writes);
            return Task.FromResult<GraphTokenClient?>(null);
        }));
        var device = new IntuneDevice
        {
            Id = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", DeviceName = "LAPTOP-0042", UserPrincipalName = FarUpn, UserId = FarObjectId,
        };
        page.GetType().GetMethod("BeginAction", PrivateInstance)!.Invoke(page, [device, IntuneDeviceAction.Retire]);
        SetField(page, "actionTicket", "INC0042");

        await (Task)page.GetType().GetMethod("ExecuteActionAsync", PrivateInstance)!.Invoke(page, [device, IntuneDeviceAction.Retire])!;

        Assert.Equal(0, writes);
        Assert.StartsWith("The primary user of this device is a protected principal.", Outcome(page, "deviceOutcomes", device.Id));
        Assert.Equal($"Protected principal - matched rules: User:{FarUpn}", RefusalError());
    }

    [Fact]
    public async Task MfaReset_ChecksTheUserGraphNamesByItsUpn_AndRefusesTheProtectedUserInTheOtherDomain()
    {
        _h.GraphUsersByUpn[FarUpn] = FarObjectId;
        var page = MfaPage();

        await (Task)page.GetType().GetMethod("ExecuteReset", PrivateInstance)!.Invoke(page, null)!;

        Assert.Equal("This user is a protected principal. MFA reset is not permitted.", MfaMessage(page));
        Assert.Equal($"Protected principal - matched rules: User:{FarUpn}", RefusalError());
    }

    [Fact]
    public async Task MfaReset_AUserGraphDoesNotHold_IsRefusedAsNotFound_BeforeAnyWrite()
    {
        var page = MfaPage();

        await (Task)page.GetType().GetMethod("ExecuteReset", PrivateInstance)!.Invoke(page, null)!;

        Assert.Equal("Could not read MFA methods - nothing was reset. Graph request for far@fabrikam.test failed: 404 NotFound.", MfaMessage(page));
        Assert.Equal(MfaMessage(page), RefusalError());
    }

    [Fact]
    public void CloudPasswordReset_ChecksTheAccountByTheObjectIdGraphRead()
    {
        using var run = new CloudPasswordResetPageRun(new StandInForest("a.example.test", "b.example.test"));

        run.Preflight();
        run.Confirm();

        Assert.Contains((CloudPasswordResetPageRun.AccountObjectId, CloudPasswordResetPageRun.AccountUpn), run.Protection.Asked);
    }

    [Fact]
    public async Task RiskyUsers_GraphNotAnswered_RefusesAsUnavailable_BeforeTheWrite()
    {
        _h.GraphSids.Remove(FarObjectId);
        var writes = 0;
        var page = new Components.Pages.RiskyUsers();
        InjectCommon(page);
        Inject(page, "RiskyUsersService", new RiskyUsersService(() =>
        {
            Interlocked.Increment(ref writes);
            return Task.FromResult<GraphTokenClient?>(null);
        }));
        SetField(page, "actionTicket", "INC0042");
        var user = new RiskyUser { Id = FarObjectId, UserPrincipalName = FarUpn, RiskLevel = "high", RiskState = "atRisk" };

        await (Task)page.GetType().GetMethod("ExecuteActionAsync", PrivateInstance)!.Invoke(page, [user, RiskyUserAction.Dismiss])!;

        Assert.Equal(0, writes);
        Assert.Equal("Protection check unavailable.", Outcome(page, "rowOutcomes", FarObjectId));
        Assert.Equal("Protection check unavailable. - blocked", RefusalError());
    }

    [Fact]
    public async Task IntuneDevices_GraphNotAnswered_RefusesAsUnavailable_BeforeTheWrite()
    {
        _h.GraphSids.Remove(FarObjectId);
        var writes = 0;
        var page = new Components.Pages.IntuneDevices();
        InjectCommon(page);
        Inject(page, "IntuneDeviceService", new IntuneDeviceService(() =>
        {
            Interlocked.Increment(ref writes);
            return Task.FromResult<GraphTokenClient?>(null);
        }));
        var device = new IntuneDevice
        {
            Id = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", DeviceName = "LAPTOP-0042", UserPrincipalName = FarUpn, UserId = FarObjectId,
        };
        page.GetType().GetMethod("BeginAction", PrivateInstance)!.Invoke(page, [device, IntuneDeviceAction.Retire]);
        SetField(page, "actionTicket", "INC0042");

        await (Task)page.GetType().GetMethod("ExecuteActionAsync", PrivateInstance)!.Invoke(page, [device, IntuneDeviceAction.Retire])!;

        Assert.Equal(0, writes);
        Assert.Equal("Protection check unavailable for the device's primary user.", Outcome(page, "deviceOutcomes", device.Id));
        Assert.Equal("Protection check unavailable for the device's primary user. - blocked", RefusalError());
    }

    // ----- Microsoft 365 groups -----

    [Fact]
    public async Task M365_AddingTheProtectedUserByUpn_ChecksTheObjectGraphResolved_AndRefuses()
    {
        var graph = new StandInGraph { [$"/users/{FarUpn}?$select=id"] = (HttpStatusCode.OK, $$"""{"id":"{{FarObjectId}}"}""") };
        var service = M365(graph);

        var result = await service.AddMemberAsync("group-1", FarUpn, _h.Operator);

        Assert.False(result.Success);
        Assert.Equal("This member is a protected principal. Operation not permitted.", result.Message);
        Assert.DoesNotContain(graph.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task M365_RemovingTheProtectedUser_ChecksTheObjectIdItDeletes_WhateverStringNamesIt()
    {
        var graph = new StandInGraph { [$"/users/{FarObjectId}?$select=id"] = (HttpStatusCode.OK, $$"""{"id":"{{FarObjectId}}"}""") };
        var service = M365(graph);

        var result = await service.RemoveMemberAsync("group-1", FarObjectId, FarUpn, _h.Operator);

        Assert.False(result.Success);
        Assert.Equal("This member is a protected principal. Operation not permitted.", result.Message);
        Assert.DoesNotContain(graph.Requests, r => r.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task M365_ABlankIdentity_Refuses_WithNoGraphCall()
    {
        var graph = new StandInGraph();
        var service = M365(graph);

        var added = await service.AddMemberAsync("group-1", "  ", _h.Operator);
        var removed = await service.RemoveMemberAsync("group-1", "", "", _h.Operator);

        Assert.False(added.Success);
        Assert.False(removed.Success);
        Assert.Contains("Protection check unavailable", added.Message, StringComparison.Ordinal);
        Assert.Contains("Protection check unavailable", removed.Message, StringComparison.Ordinal);
        Assert.Empty(graph.Requests);
    }

    [Fact]
    public async Task M365_AnIdentityGraphDoesNotFind_NeverProceeds()
    {
        var graph = new StandInGraph();
        var service = M365(graph);

        var result = await service.AddMemberAsync("group-1", "nobody", _h.Operator);

        Assert.False(result.Success);
        Assert.Contains("was not found in the directory", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(graph.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task M365_AnObjectThatIsNoUser_HasNoUserToProtect_AndIsRemoved()
    {
        const string device = "aaaaaaaa-1111-4222-8333-444444444444";
        var graph = new StandInGraph { [$"/groups/group-1/members/{device}/$ref"] = (HttpStatusCode.NoContent, "") };
        var service = M365(graph);

        var result = await service.RemoveMemberAsync("group-1", device, "", _h.Operator);

        Assert.True(result.Success, result.Message);
        Assert.Contains($"DELETE /groups/group-1/members/{device}/$ref", graph.Requests);
        Assert.Equal(0, _h.GraphSidReads);
    }

    [Fact]
    public async Task M365_AnObjectGraphCannotClassify_Refuses()
    {
        const string member = "bbbbbbbb-1111-4222-8333-444444444444";
        var graph = new StandInGraph { [$"/users/{member}?$select=id"] = (HttpStatusCode.Forbidden, "") };
        var service = M365(graph);

        var result = await service.RemoveMemberAsync("group-1", member, "someone@contoso.test", _h.Operator);

        Assert.False(result.Success);
        Assert.Contains("Protection check unavailable", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(graph.Requests, r => r.StartsWith("DELETE", StringComparison.Ordinal));
    }

    // ----- fixtures -----

    private M365GroupManagementService M365(StandInGraph graph) =>
        new(_moduleConfig, _delinea, _http, _h.Trace, _audit, _protection, _servicers, NullLogger<M365GroupManagementService>.Instance)
        {
            GraphClientForTests = () => new GraphTokenClient("tenant", "client", "secret", new HttpClient(graph, disposeHandler: false)),
        };

    private Components.Pages.MfaReset MfaPage()
    {
        var page = new Components.Pages.MfaReset();
        InjectCommon(page);
        Inject(page, "MfaService", new MfaResetService(NullLogger<MfaResetService>.Instance, _moduleConfig, _delinea, _http));
        SetField(page, "listedUpn", FarUpn);
        SetField(page, "ticketNumber", "INC0042");
        SetField(page, "resetConfirmPending", true);
        return page;
    }

    private static string MfaMessage(object page) =>
        ((MfaResetResult)page.GetType().GetField("result", PrivateInstance)!.GetValue(page)!).Message;

    private void InjectCommon(object page)
    {
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(_h.Operator));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(Task.FromResult(AuthorizationResult.Success()));
        Inject(page, "Audit", _audit);
        Inject(page, "AuthStateProvider", authState);
        Inject(page, "AuthorizationService", authorization);
        Inject(page, "Email", Substitute.For<EmailService>(_h.Config, NullLogger<EmailService>.Instance));
        Inject(page, "ServiceNow", new ServiceNowService(_h.Config, NullLogger<ServiceNowService>.Instance, _http));
        Inject(page, "ProtectedPrincipalService", _protection);
        Inject(page, "Servicers", _servicers);
        Inject(page, "Progress", _progress);
        var logger = typeof(NullLogger<>).MakeGenericType(page.GetType()).GetField("Instance")!.GetValue(null)!;
        Inject(page, "Logger", logger);
        SetField(page, "currentUser", Operator);
        SetField(page, "clientIpAddress", "10.0.0.5");
    }

    private static void Inject(object page, string name, object value) =>
        (page.GetType().GetProperty(name, PrivateInstance) ?? throw new InvalidOperationException($"{name} not found - this test is stale"))
            .SetValue(page, value);

    private static void SetField(object page, string name, object? value) =>
        (page.GetType().GetField(name, PrivateInstance) ?? throw new InvalidOperationException($"{name} not found - this test is stale"))
            .SetValue(page, value);

    private static string Outcome(object page, string field, string key)
    {
        var outcomes = (System.Collections.IDictionary)page.GetType().GetField(field, PrivateInstance)!.GetValue(page)!;
        var outcome = outcomes[key]!;
        return (string)outcome.GetType().GetProperty("Message")!.GetValue(outcome)!;
    }

    /// <summary>The error of the one failed audit row the refusal wrote.</summary>
    private string RefusalError()
    {
        var folder = Path.Combine(_h.Dir, "ExchangeAdminWeb");
        var rows = Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(file => !Path.GetFileNameWithoutExtension(file).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(row => row.TryGetProperty("result", out var result) && result.GetString() == "Failed")
            .ToList();
        return Assert.Single(rows).GetProperty("error").GetString()!;
    }

    /// <summary>A Graph that answers the paths it is given and 404 to every other; it records each request.</summary>
    private sealed class StandInGraph : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public (HttpStatusCode Status, string Body) this[string path]
        {
            set => _answers[path] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsoluteUri.Contains("/oauth2/v2.0/token", StringComparison.Ordinal))
                return Task.FromResult(Answer(HttpStatusCode.OK, """{"access_token":"t","expires_in":3600}"""));

            var path = Uri.UnescapeDataString(uri.PathAndQuery).Replace("/v1.0", "", StringComparison.Ordinal);
            lock (Requests)
                Requests.Add($"{request.Method} {path}");
            return Task.FromResult(_answers.TryGetValue(path, out var answer)
                ? Answer(answer.Status, answer.Body)
                : Answer(HttpStatusCode.NotFound, "{}"));
        }

        private static HttpResponseMessage Answer(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
