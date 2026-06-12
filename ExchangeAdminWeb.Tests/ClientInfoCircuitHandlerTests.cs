using System.Net;
using System.Security.Claims;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class ClientInfoCircuitHandlerTests
{
    private static (ClientInfoCircuitHandler Handler, ClientInfoService ClientInfo) CreateHandler(HttpContext? context)
    {
        var clientInfo = new ClientInfoService();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        var handler = new ClientInfoCircuitHandler(
            clientInfo, accessor, Substitute.For<ILogger<ClientInfoCircuitHandler>>());
        return (handler, clientInfo);
    }

    private static DefaultHttpContext MakeContext(string ip, string? username = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Request.Headers.UserAgent = "TestAgent/1.0";
        if (username != null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, username)], "TestAuth"));
        }
        return context;
    }

    [Fact]
    public void Capture_PopulatesCircuitScopedClientInfo()
    {
        var (handler, clientInfo) = CreateHandler(MakeContext("10.1.2.3", "CONTOSO\\alice"));

        handler.Capture();

        Assert.Equal("10.1.2.3", clientInfo.IpAddress);
        Assert.Equal("TestAgent/1.0", clientInfo.UserAgent);
    }

    [Fact]
    public void Capture_ConcurrentSessionsOfSameAccount_KeepTheirOwnIp()
    {
        // The old static username-keyed cache was last-write-wins: session B's
        // login overwrote session A's IP in every subsequent audit record.
        // Circuit-scoped capture isolates them.
        var (handlerA, clientInfoA) = CreateHandler(MakeContext("10.0.0.1", "CONTOSO\\alice"));
        var (handlerB, clientInfoB) = CreateHandler(MakeContext("10.0.0.2", "CONTOSO\\alice"));

        handlerA.Capture();
        handlerB.Capture();

        Assert.Equal("10.0.0.1", clientInfoA.IpAddress);
        Assert.Equal("10.0.0.2", clientInfoB.IpAddress);
    }

    [Fact]
    public void Capture_NoHttpContext_LeavesDefaultsAndDoesNotThrow()
    {
        var (handler, clientInfo) = CreateHandler(context: null);

        handler.Capture();

        Assert.Equal("Unknown", clientInfo.IpAddress);
    }

    [Fact]
    public void Capture_NoRemoteIp_RecordsUnknown()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "TestAgent/1.0";
        var (handler, clientInfo) = CreateHandler(context);

        handler.Capture();

        Assert.Equal("Unknown", clientInfo.IpAddress);
    }
}
