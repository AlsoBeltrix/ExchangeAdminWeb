using System.Collections.Concurrent;

namespace ExchangeAdminWeb.Services;

public class ClientInfoService
{
    private static readonly ConcurrentDictionary<string, ClientInfo> _cache = new();
    private static DateTime _lastCleanup = DateTime.UtcNow;

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

        if ((DateTime.UtcNow - _lastCleanup).TotalMinutes > 30)
        {
            _lastCleanup = DateTime.UtcNow;
            foreach (var key in _cache.Keys)
            {
                if (_cache.TryGetValue(key, out var entry) && (DateTime.UtcNow - entry.Timestamp).TotalHours > 1)
                    _cache.TryRemove(key, out _);
            }
        }
    }

    public string GetIpForUser(string username)
    {
        if (_cache.TryGetValue(username, out var info))
        {
            if ((DateTime.UtcNow - info.Timestamp).TotalHours < 1)
                return info.IpAddress;
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
