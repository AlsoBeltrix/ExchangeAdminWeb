using System.Net.Http.Headers;
using System.Text.Json;
using Windows.Security.Credentials;

namespace ExchangeAdminWeb.Services;

public sealed class GraphClientService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GraphClientService> _logger;
    private string _tenantId = "";
    private string _clientId = "";
    private string _clientSecret = "";
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    private readonly ModuleConfigService _moduleConfig;

    public GraphClientService(ModuleConfigService moduleConfig, IHttpClientFactory httpClientFactory, ILogger<GraphClientService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("MicrosoftGraph");
        _logger = logger;
        _moduleConfig = moduleConfig;

        LoadConfig();
    }

    private void LoadConfig()
    {
        // Try GroupManagement config first, then MfaReset (shared credential pattern)
        _tenantId = _moduleConfig.GetValue("GroupManagement", "GraphTenantId")
            ?? _moduleConfig.GetValue("MfaReset", "TenantId") ?? "";
        _clientId = _moduleConfig.GetValue("GroupManagement", "GraphClientId")
            ?? _moduleConfig.GetValue("MfaReset", "ClientId") ?? "";
        var credentialTarget = _moduleConfig.GetValue("GroupManagement", "GraphCredentialTarget")
            ?? _moduleConfig.GetValue("MfaReset", "CredentialTarget") ?? "Graph_MFAResets";

        if (string.IsNullOrEmpty(_tenantId) || string.IsNullOrEmpty(_clientId))
        {
            _clientSecret = string.Empty;
            _logger.LogWarning("MfaReset module config incomplete (TenantId/ClientId). Configure via Admin Settings.");
            return;
        }

        var (_, secret) = ReadCredential(credentialTarget);

        if (string.IsNullOrEmpty(secret))
        {
            _clientSecret = string.Empty;
            _logger.LogWarning("Graph API credentials not found in PasswordVault (target: {Target}).", credentialTarget);
        }
        else
        {
            _clientSecret = secret;
            _logger.LogInformation("Graph API credentials loaded from PasswordVault");
        }
    }

    public void ReloadConfig() => LoadConfig();

    public bool IsConfigured => !string.IsNullOrEmpty(_clientSecret);

    public async Task<JsonDocument?> GetAsync(string endpoint)
    {
        var token = await GetAccessTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphBaseUrl}{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Graph GET {Endpoint} failed: {Status} {Error}", endpoint, response.StatusCode, error);
            return null;
        }

        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }

    public async Task<bool> DeleteAsync(string endpoint)
    {
        var token = await GetAccessTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{GraphBaseUrl}{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Graph DELETE {Endpoint} failed: {Status} {Error}", endpoint, response.StatusCode, error);
            return false;
        }

        return true;
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
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Graph POST {Endpoint} failed: {Status} {Error}", endpoint, response.StatusCode, error);
            return false;
        }
        return true;
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
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Graph POST {Endpoint} failed: {Status} {Error}", endpoint, response.StatusCode, error);
            return null;
        }

        var content = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(content))
            return null;

        return JsonDocument.Parse(content);
    }

    private async Task<string> GetAccessTokenAsync()
    {
        if (string.IsNullOrEmpty(_clientSecret))
            throw new InvalidOperationException("Graph API credentials not configured. Store client secret in PasswordVault.");

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
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Graph token request failed: {Status} {Error}", response.StatusCode, error);
                throw new InvalidOperationException($"Failed to authenticate to Microsoft Graph: {response.StatusCode}");
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

            _logger.LogInformation("Graph API token acquired (expires in {Seconds}s)", expiresIn);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static (string? clientId, string? secret) ReadCredential(string target)
    {
        try
        {
            var vault = new PasswordVault();
            var results = vault.FindAllByResource(target);
            var cred = results.FirstOrDefault();
            if (cred is null) return (null, null);
            cred.RetrievePassword();
            return (cred.UserName, cred.Password);
        }
        catch
        {
            return (null, null);
        }
    }
}
