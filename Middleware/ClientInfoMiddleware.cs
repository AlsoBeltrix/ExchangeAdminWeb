using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Middleware;

/// <summary>
/// Middleware that captures client IP address and user agent during the initial HTTP request
/// before the connection upgrades to SignalR WebSocket
/// </summary>
public class ClientInfoMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ClientInfoMiddleware> _logger;

    public ClientInfoMiddleware(RequestDelegate next, ILogger<ClientInfoMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ClientInfoService clientInfo)
    {
        var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";

        var userAgent = context.Request.Headers["User-Agent"].FirstOrDefault() ?? string.Empty;
        var username = context.User.Identity?.Name ?? "Anonymous";

        // Store in scoped instance (for same-request access)
        clientInfo.IpAddress = ipAddress;
        clientInfo.UserAgent = userAgent;

        // Store in static cache (for cross-scope access in SignalR)
        clientInfo.StoreForUser(username, ipAddress, userAgent);

        _logger.LogDebug("[ClientInfoMiddleware] Captured IP: {IP} for path {Path}, User: {User}",
            ipAddress, context.Request.Path, username);

        await _next(context);
    }
}
