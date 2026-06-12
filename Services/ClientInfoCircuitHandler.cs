using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Captures the client IP and user agent into the circuit's scoped
/// ClientInfoService when the Blazor Server circuit opens. The SignalR
/// connection's HttpContext is available at circuit initialization, so the
/// scoped instance carries the correct per-session values for the whole
/// circuit lifetime. This replaces reliance on the static username-keyed
/// cache, which expires after an hour (audits from long-lived tabs recorded
/// IP "Unknown") and is last-write-wins per username (concurrent sessions of
/// one account cross-attributed each other's IPs in audit records).
/// </summary>
public sealed class ClientInfoCircuitHandler : CircuitHandler
{
    private readonly ClientInfoService _clientInfo;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ClientInfoCircuitHandler> _logger;

    public ClientInfoCircuitHandler(
        ClientInfoService clientInfo,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ClientInfoCircuitHandler> logger)
    {
        _clientInfo = clientInfo;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Capture();
        return base.OnCircuitOpenedAsync(circuit, cancellationToken);
    }

    /// <summary>
    /// Copies client info from the connection's HttpContext into the
    /// circuit-scoped ClientInfoService. Failure leaves the existing values
    /// ("Unknown" at worst) - IP capture must never block an operation.
    /// </summary>
    public void Capture()
    {
        try
        {
            var context = _httpContextAccessor.HttpContext;
            if (context == null)
            {
                _logger.LogDebug("Circuit opened without an HttpContext; client info falls back to middleware-captured values");
                return;
            }

            var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
            var userAgent = context.Request.Headers.UserAgent.ToString();

            _clientInfo.IpAddress = ipAddress;
            _clientInfo.UserAgent = userAgent;

            var username = context.User.Identity?.Name;
            if (!string.IsNullOrEmpty(username))
                _clientInfo.StoreForUser(username, ipAddress, userAgent);

            _logger.LogDebug("Captured client IP {IP} for circuit", ipAddress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture client info at circuit open; audits may record IP Unknown");
        }
    }
}
