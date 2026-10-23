using System.Reflection;
using System.Security.Claims;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.ConferenceRooms;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The Conference Rooms page's own decisions, driven for real: a real <see cref="Page"/> instance with its injected services
/// set through reflection (the <see cref="ConferenceRoomDirectoryAuditTests"/> idiom) and its handler invoked.
/// </summary>
/// <remarks>
/// The room service is the real one on <see cref="ExchangeWriteHarness"/>, whose stand-in Exchange records every call, and the
/// audit is the real one, read back from its file. Nothing here reaches Exchange, a directory or the network.
/// </remarks>
public sealed class ConferenceRoomPageTests : IDisposable
{
    private const string Operator = @"CONTOSO\operator";
    private const string OperatorIp = "10.0.0.5";

    private readonly ExchangeWriteHarness _h = new();
    private readonly ActivityProgressService _progress = new();

    public void Dispose()
    {
        _progress.Dispose();
        _h.Dispose();
    }

    // ---- aud-a2y (3): the page's refusal of a room type it cannot parse says nothing was written ----

    [Fact]
    public async Task ARoomTypeThatDoesNotParse_IsRefused_SayingNothingWasWritten()
    {
        // Refused after the ticket and the authorization check and before the protection gate or the service is reached, so
        // nothing was written - and the banner now says so, as every refusal the service decides before a write does.
        var page = BuildPage();
        SetField(page, "typeRoomEmail", "room1@contoso.test");
        SetField(page, "selectedRoomType", "Boardroom");
        SetField(page, "typeTicketNumber", "INC100");
        SetField(page, "typeTimezone", "GMT Standard Time");

        await (Task)Method("SetSingleRoomType").Invoke(page, null)!;

        var result = Assert.IsType<RoomOperationResult>(GetField(page, "result"));
        Assert.False(result.Success);
        Assert.Equal("Invalid room type: Boardroom. Nothing was written.", result.Message);
        Assert.Empty(_h.Calls);
    }

    // ---- the page ------------------------------------------------------------------------------

    private Page BuildPage()
    {
        var page = new Page();
        SetProperty(page, "RoomService", new ConferenceRoomService(_h.Pool, null!, _h.ModuleConfig, null!, _h.Trace, _h.Config,
            NullLogger<ConferenceRoomService>.Instance, _h.ProtectedPrincipals, _h.Servicers));
        SetProperty(page, "Audit", _h.Audit);
        SetProperty(page, "Email", Substitute.For<EmailService>(_h.Config, NullLogger<EmailService>.Instance));
        SetProperty(page, "ServiceNow", new ServiceNowService(_h.Config, NullLogger<ServiceNowService>.Instance, HttpClients()));
        SetProperty(page, "AuthStateProvider", Authentication());
        SetProperty(page, "AuthorizationService", Authorization());
        SetProperty(page, "OpTrace", _h.Trace);
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetProperty(page, "Progress", _progress);

        SetField(page, "currentUser", Operator);
        SetField(page, "clientIpAddress", OperatorIp);
        return page;
    }

    private static IHttpClientFactory HttpClients()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        return factory;
    }

    private static AuthenticationStateProvider Authentication()
    {
        var state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, Operator)], "test")));
        var provider = Substitute.For<AuthenticationStateProvider>();
        provider.GetAuthenticationStateAsync().Returns(Task.FromResult(state));
        return provider;
    }

    private static IAuthorizationService Authorization()
    {
        var service = Substitute.For<IAuthorizationService>();
        service.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        return service;
    }

    private static MethodInfo Method(string name) =>
        typeof(Page).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static object? GetField(Page page, string name) =>
        typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page);
}
