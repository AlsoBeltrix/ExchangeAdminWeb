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

    /// <summary>
    /// GET that surfaces the HTTP status so callers can distinguish "empty result"
    /// from "request failed" (403/404/429/5xx). Document is null on non-success.
    /// </summary>
    public async Task<(JsonDocument? Document, System.Net.HttpStatusCode StatusCode)> GetWithStatusAsync(string endpoint)
    {
        var token = await GetAccessTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphBaseUrl}{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return (null, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        return (JsonDocument.Parse(content), response.StatusCode);
    }

    // Null collapses failure and "no content" into one value - prefer
    // GetWithStatusAsync anywhere the caller reports success/failure to a user.
    public async Task<JsonDocument?> GetAsync(string endpoint)
    {
        var (document, _) = await GetWithStatusAsync(endpoint);
        return document;
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

    /// <summary>
    /// PATCH that surfaces the HTTP status and a sanitized Graph error so callers can report
    /// WHY a write failed (e.g. an on-premises-mastered property rejection on a synced user)
    /// instead of a bare bool. SafeError contains only the Graph error.code/message, never the
    /// token or raw response body.
    /// </summary>
    public async Task<(bool Ok, System.Net.HttpStatusCode StatusCode, string? SafeError)> PatchWithStatusAsync(string endpoint, object body)
    {
        var token = await GetAccessTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{GraphBaseUrl}{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var json = JsonSerializer.Serialize(body);
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        if (response.IsSuccessStatusCode)
            return (true, response.StatusCode, null);

        var content = await response.Content.ReadAsStringAsync();
        return (false, response.StatusCode, ExtractGraphError(content));
    }

    public async Task<bool> PatchAsync(string endpoint, object body)
    {
        var (ok, _, _) = await PatchWithStatusAsync(endpoint, body);
        return ok;
    }

    /// <summary>
    /// Pulls the Graph <c>error.code</c>/<c>error.message</c> out of an error response body for
    /// safe logging. Returns null when the body is empty or unparseable. Never returns tokens or
    /// other request data — input is only the response body.
    /// </summary>
    internal static string? ExtractGraphError(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                var combined = string.Join(": ", new[] { code, message }.Where(s => !string.IsNullOrWhiteSpace(s)));
                return string.IsNullOrWhiteSpace(combined) ? null : combined;
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body — fall through to null rather than echo arbitrary content.
        }

        return null;
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
