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

    // ----- fixtures -----

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
}
