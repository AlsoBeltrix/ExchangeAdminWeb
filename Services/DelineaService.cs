using System.Text;
using System.Text.Json;

namespace ExchangeAdminWeb.Services;

public class DelineaService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DelineaService> _logger;
    private readonly string _secretServerUrl;
    private string _apiUsername = string.Empty;
    private string _apiKey = string.Empty;
    private readonly int _exchangeSecretId;
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public DelineaService(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<DelineaService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger;
        _secretServerUrl = config["Delinea:SecretServerUrl"] ?? throw new InvalidOperationException("Delinea:SecretServerUrl not configured");
        _exchangeSecretId = int.Parse(config["Delinea:ExchangeSecretId"] ?? throw new InvalidOperationException("Delinea:ExchangeSecretId not configured"));

        _credentialTarget = config["Delinea:CredentialTarget"] ?? "Delinea_Client";
        LoadCredentials();
    }

    private readonly string _credentialTarget;

    private void LoadCredentials()
    {
        var (username, password) = CredentialManagerService.ReadCredential(_credentialTarget);

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            _apiUsername = string.Empty;
            _apiKey = string.Empty;
        }
        else
        {
            _apiUsername = username;
            _apiKey = password;
        }
    }

    public Task<(string username, string password, string domain)?> GetExchangeCredentialsAsync()
        => GetCredentialsBySecretIdAsync(_exchangeSecretId);

    public async Task<Dictionary<string, string>?> GetSecretFieldsAsync(int secretId)
    {
        if (string.IsNullOrEmpty(_apiUsername) || string.IsNullOrEmpty(_apiKey))
        {
            LoadCredentials();
            if (string.IsNullOrEmpty(_apiUsername) || string.IsNullOrEmpty(_apiKey))
                return null;
        }

        try
        {
            var token = await GetAccessTokenAsync();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_secretServerUrl}/api/v1/secrets/{secretId}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            using var secret = JsonDocument.Parse(content);

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in secret.RootElement.GetProperty("items").EnumerateArray())
            {
                var slug = item.TryGetProperty("slug", out var s) ? s.GetString() : null;
                var fieldName = item.GetProperty("fieldName").GetString();
                var itemValue = item.GetProperty("itemValue").GetString();

                if (slug != null && itemValue != null)
                    fields[slug] = itemValue;
                if (fieldName != null && itemValue != null)
                    fields[fieldName] = itemValue;
            }
            return fields;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving secret fields from Delinea for secret {SecretId}", secretId);
            return null;
        }
    }

    public async Task<(string username, string password, string domain)?> GetCredentialsBySecretIdAsync(int secretId)
    {
        if (string.IsNullOrEmpty(_apiUsername) || string.IsNullOrEmpty(_apiKey))
        {
            LoadCredentials();
            if (string.IsNullOrEmpty(_apiUsername) || string.IsNullOrEmpty(_apiKey))
            {
                _logger.LogWarning("Delinea credentials not found in Credential Manager (target: {Target})", _credentialTarget);
                return null;
            }
            _logger.LogInformation("Delinea credentials loaded on retry from Credential Manager");
        }

        try
        {
            var token = await GetAccessTokenAsync();

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_secretServerUrl}/api/v1/secrets/{secretId}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to retrieve secret {SecretId}: {StatusCode}", secretId, response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var secret = JsonDocument.Parse(content);

            var items = secret.RootElement.GetProperty("items");
            string? username = null;
            string? password = null;
            string? domain = null;

            foreach (var item in items.EnumerateArray())
            {
                var fieldName = item.GetProperty("fieldName").GetString();
                var itemValue = item.GetProperty("itemValue").GetString();

                if (fieldName?.Equals("Username", StringComparison.OrdinalIgnoreCase) == true)
                    username = itemValue;
                else if (fieldName?.Equals("Password", StringComparison.OrdinalIgnoreCase) == true)
                    password = itemValue;
                else if (fieldName?.Equals("Domain", StringComparison.OrdinalIgnoreCase) == true)
                    domain = itemValue;
            }

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                _logger.LogError("Secret {SecretId} missing username or password fields", secretId);
                return null;
            }

            if (string.IsNullOrEmpty(domain))
            {
                _logger.LogError("Secret {SecretId} missing Domain field — cannot construct credential", secretId);
                return null;
            }

            return (username!, password!, domain);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving credentials from Delinea Secret Server for secret {SecretId}", secretId);
            return null;
        }
    }

    private async Task<string> GetAccessTokenAsync()
    {
        await _tokenLock.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
                return _accessToken;

            var authBody = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", $"sdk-client-{_apiUsername}"),
                new KeyValuePair<string, string>("client_secret", _apiKey)
            });

            var authResponse = await _httpClient.PostAsync($"{_secretServerUrl}/oauth2/token", authBody);

            if (!authResponse.IsSuccessStatusCode)
            {
                var errorBody = await authResponse.Content.ReadAsStringAsync();
                _logger.LogError("Delinea auth failed: {Status} | ClientId: sdk-client-{User} | Response: {Body}",
                    authResponse.StatusCode, _apiUsername, errorBody);
                throw new InvalidOperationException($"Failed to authenticate to Delinea Secret Server: {authResponse.StatusCode}");
            }

            var tokenContent = await authResponse.Content.ReadAsStringAsync();
            using var tokenDoc = JsonDocument.Parse(tokenContent);

            _accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = tokenDoc.RootElement.GetProperty("expires_in").GetInt32();
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

            _logger.LogInformation("Successfully authenticated to Delinea Secret Server");
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
