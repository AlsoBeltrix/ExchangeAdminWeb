using System.Net.Http.Headers;
using System.Text.Json;

namespace ExchangeAdminWeb.Services;

public sealed class GraphTokenClient
{
    private readonly HttpClient _httpClient;
    private readonly string _tenantId;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    public bool IsConfigured => !string.IsNullOrEmpty(_tenantId) && !string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret);

    public GraphTokenClient(string tenantId, string clientId, string clientSecret, HttpClient httpClient)
    {
        _httpClient = httpClient;
        _tenantId = tenantId;
        _clientId = clientId;
        _clientSecret = clientSecret ?? "";
    }

    public async Task<JsonDocument?> GetAsync(string endpoint)
    {
        var token = await GetAccessTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphBaseUrl}{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }

    public async Task<bool> DeleteAsync(string endpoint)
    {
        var token = await GetAccessTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{GraphBaseUrl}{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> PostNoContentAsync(string endpoint, object? body = null)
    {
        var token = await GetAccessTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GraphBaseUrl}{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body != null)
        {
            var json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        var response = await _httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<JsonDocument?> PostAsync(string endpoint, object? body = null)
    {
        var token = await GetAccessTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GraphBaseUrl}{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body != null)
        {
            var json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? null : JsonDocument.Parse(content);
    }

    public async Task<bool> PatchAsync(string endpoint, object body)
    {
        var token = await GetAccessTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{GraphBaseUrl}{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var json = JsonSerializer.Serialize(body);
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    private async Task<string> GetAccessTokenAsync()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Graph API credentials not configured for this module.");

        await _tokenLock.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
                return _accessToken;

            var tokenUrl = $"https://login.microsoftonline.com/{_tenantId}/oauth2/v2.0/token";
            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", _clientId),
                new KeyValuePair<string, string>("client_secret", _clientSecret),
                new KeyValuePair<string, string>("scope", "https://graph.microsoft.com/.default")
            });

            var response = await _httpClient.PostAsync(tokenUrl, body);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Graph token request failed: {response.StatusCode}");

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

}
