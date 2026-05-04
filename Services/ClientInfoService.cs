using System.Collections.Concurrent;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Service that captures and stores client information across HTTP and SignalR scopes
/// Uses a static cache keyed by username to persist IP addresses across scope boundaries
/// </summary>
public class ClientInfoService
{
    private static readonly ConcurrentDictionary<string, ClientInfo> _cache = new();

    public string IpAddress { get; set; } = "Unknown";
    public string UserAgent { get; set; } = string.Empty;

    public void StoreForUser(string username, string ipAddress, string userAgent)
    {
        _cache[username] = new ClientInfo
        {
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Timestamp = DateTime.UtcNow
        };
    }

    public string GetIpForUser(string username)
    {
        if (_cache.TryGetValue(username, out var info))
        {
            // Return cached IP if less than 1 hour old
            if ((DateTime.UtcNow - info.Timestamp).TotalHours < 1)
            {
                return info.IpAddress;
            }
        }
        return "Unknown";
    }

    private class ClientInfo
    {
        public string IpAddress { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }
}
