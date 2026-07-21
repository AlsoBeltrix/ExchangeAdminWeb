using System.Text;
using System.Text.Json;
using Serilog.Events;

namespace ExchangeAdminWeb.Services;

public class DelineaService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DelineaService> _logger;
    private readonly ExtendedLogService _extLog;
    private readonly OperationTraceService _operationTrace;
    private readonly string _secretServerUrl;
    private string _apiUsername = string.Empty;
    private string _apiKey = string.Empty;
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public DelineaService(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<DelineaService> logger, ExtendedLogService extLog, OperationTraceService operationTrace)
    {
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger;
        _extLog = extLog;
        _operationTrace = operationTrace;
        _secretServerUrl = config["Delinea:SecretServerUrl"] ?? throw new InvalidOperationException("Delinea:SecretServerUrl not configured");

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

    public async Task<Dictionary<string, string>?> GetSecretFieldsAsync(int secretId)
    {
        if (string.IsNullOrEmpty(_apiUsername) || string.IsNullOrEmpty(_apiKey))
        {
            LoadCredentials();
            if (string.IsNullOrEmpty(_apiUsername) || string.IsNullOrEmpty(_apiKey))
            {
                _operationTrace.Step("VaultSecretFieldsRequested", "Failed", backend: "Delinea", details: new Dictionary<string, object?> { ["secretId"] = secretId, ["reason"] = "Bootstrap credentials unavailable" });
                return null;
            }
        }

        try
        {
            _operationTrace.Step("VaultSecretFieldsRequested", backend: "Delinea", command: "GET /api/v1/secrets/{secretId}", details: new Dictionary<string, object?> { ["secretId"] = secretId });
            var token = await GetAccessTokenAsync();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_secretServerUrl}/api/v1/secrets/{secretId}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var response = await _httpClient.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Delinea returned {Status} for secret {SecretId} - forcing token refresh", response.StatusCode, secretId);
                InvalidateToken();
                token = await GetAccessTokenAsync();

                using var retryRequest = new HttpRequestMessage(HttpMethod.Get, $"{_secretServerUrl}/api/v1/secrets/{secretId}");
                retryRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                response = await _httpClient.SendAsync(retryRequest);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to retrieve secret fields for secret {SecretId}: {StatusCode}", secretId, response.StatusCode);
                _extLog.Write(LogEventLevel.Error, "Delinea secret field request failed", "Delinea", () => $"SecretId={secretId}; Status={response.StatusCode}");
                _operationTrace.Step("VaultSecretFieldsRetrieved", "Failed", backend: "Delinea", command: "GET /api/v1/secrets/{secretId}", details: new Dictionary<string, object?> { ["secretId"] = secretId, ["status"] = response.StatusCode.ToString() });
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var secret = JsonDocument.Parse(content);

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in secret.RootElement.GetProperty("items").EnumerateArray())
            {
                var fieldName = item.GetProperty("fieldName").GetString();
                var itemValue = item.GetProperty("itemValue").GetString();

                if (fieldName != null && itemValue != null)
                    fields[fieldName] = itemValue;
            }
            _operationTrace.Step("VaultSecretFieldsRetrieved", backend: "Delinea", details: new Dictionary<string, object?> { ["secretId"] = secretId, ["fieldCount"] = fields.Count });
            return fields;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving secret fields from Delinea for secret {SecretId}", secretId);
            _operationTrace.Step("VaultSecretFieldsRetrieved", "Failed", backend: "Delinea", details: new Dictionary<string, object?> { ["secretId"] = secretId }, exception: ex);
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
                _extLog.Write(LogEventLevel.Error, "Delinea bootstrap credentials not found", "Delinea", () => $"Target={_credentialTarget}");
                _operationTrace.Step("VaultCredentialRequested", "Failed", backend: "Delinea", details: new Dictionary<string, object?> { ["credentialTarget"] = _credentialTarget, ["reason"] = "Bootstrap credentials unavailable" });
                return null;
            }
            _logger.LogInformation("Delinea credentials loaded on retry from Credential Manager");
            _extLog.Write(LogEventLevel.Information, "Delinea bootstrap credentials loaded on retry", "Delinea", () => $"Target={_credentialTarget}; ClientCredentialLoaded=true");
        }

        try
        {
            _operationTrace.Step("VaultCredentialRequested", backend: "Delinea", command: "GET /api/v1/secrets/{secretId}", details: new Dictionary<string, object?> { ["secretId"] = secretId });
            var token = await GetAccessTokenAsync();

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_secretServerUrl}/api/v1/secrets/{secretId}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var response = await _httpClient.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Delinea returned {Status} for secret {SecretId} - forcing token refresh and retrying", response.StatusCode, secretId);
                InvalidateToken();
                token = await GetAccessTokenAsync();

                using var retryRequest = new HttpRequestMessage(HttpMethod.Get, $"{_secretServerUrl}/api/v1/secrets/{secretId}");
                retryRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                response = await _httpClient.SendAsync(retryRequest);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to retrieve secret {SecretId}: {StatusCode}", secretId, response.StatusCode);
                _extLog.Write(LogEventLevel.Error, "Delinea credential request failed", "Delinea", () => $"SecretId={secretId}; Status={response.StatusCode}");
                _operationTrace.Step("VaultCredentialRequested", "Failed", backend: "Delinea", command: "GET /api/v1/secrets/{secretId}", details: new Dictionary<string, object?> { ["secretId"] = secretId, ["status"] = response.StatusCode.ToString() });
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
                _extLog.Write(LogEventLevel.Error, "Delinea credential secret missing username or password", "Delinea", () => $"SecretId={secretId}");
                _operationTrace.Step("VaultCredentialParsed", "Failed", backend: "Delinea", details: new Dictionary<string, object?> { ["secretId"] = secretId, ["reason"] = "Missing username or password" });
                return null;
            }

            if (string.IsNullOrEmpty(domain))
            {
                _logger.LogError("Secret {SecretId} missing Domain field - cannot construct credential", secretId);
                _extLog.Write(LogEventLevel.Error, "Delinea credential secret missing Domain field", "Delinea", () => $"SecretId={secretId}");
                _operationTrace.Step("VaultCredentialParsed", "Failed", backend: "Delinea", details: new Dictionary<string, object?> { ["secretId"] = secretId, ["reason"] = "Missing domain" });
                return null;
            }

            _extLog.Write(LogEventLevel.Information, "Delinea credentials retrieved", "Delinea", () => $"SecretId={secretId}; CredentialFields=Username,Password,Domain");
            _operationTrace.Step("VaultCredentialRetrieved", backend: "Delinea", details: new Dictionary<string, object?> { ["secretId"] = secretId, ["credentialFields"] = "Username,Password,Domain" });
            return (username!, password!, domain);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving credentials from Delinea Secret Server for secret {SecretId}", secretId);
            _extLog.Write(LogEventLevel.Error, "Exception retrieving Delinea credentials", "Delinea", () => $"SecretId={secretId}; ErrorType={ex.GetType().Name}");
            _operationTrace.Step("VaultCredentialRetrieved", "Failed", backend: "Delinea", details: new Dictionary<string, object?> { ["secretId"] = secretId }, exception: ex);
            return null;
        }
    }

    private static string GetOAuthErrorCode(string errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody))
            return "none";

        try
        {
            using var document = JsonDocument.Parse(errorBody);
            if (document.RootElement.TryGetProperty("error", out var error))
                return error.GetString() ?? "unknown";
        }
        catch (JsonException)
        {
        }

        return "unavailable";
    }

    private void InvalidateToken()
    {
        _accessToken = null;
        _tokenExpiry = DateTime.MinValue;
    }

    private async Task<string> GetAccessTokenAsync()
    {
        await _tokenLock.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
            {
                _extLog.Write(LogEventLevel.Debug, "Using cached Delinea access token", "Delinea", () => $"Target={_credentialTarget}; ExpiresUtc={_tokenExpiry:o}");
                _operationTrace.Step("VaultTokenReused", backend: "Delinea", command: "oauth2/token", details: new Dictionary<string, object?> { ["credentialTarget"] = _credentialTarget });
                return _accessToken;
            }

            _extLog.Write(LogEventLevel.Debug, "Authenticating to Delinea Secret Server", "Delinea", () => $"Target={_credentialTarget}");
            _operationTrace.Step("VaultTokenRequested", backend: "Delinea", command: "oauth2/token", details: new Dictionary<string, object?> { ["credentialTarget"] = _credentialTarget });

            var clientId = _apiUsername.StartsWith("sdk-client-", StringComparison.OrdinalIgnoreCase)
                ? _apiUsername
                : $"sdk-client-{_apiUsername}";
            var authBody = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", _apiKey)
            });

            var authResponse = await _httpClient.PostAsync($"{_secretServerUrl}/oauth2/token", authBody);

            if (!authResponse.IsSuccessStatusCode)
            {
                var errorBody = await authResponse.Content.ReadAsStringAsync();
                var oauthError = GetOAuthErrorCode(errorBody);
                _logger.LogWarning("Delinea auth failed: {Status} | OAuthError: {OAuthError} - reloading credentials and retrying",
                    authResponse.StatusCode, oauthError);

                LoadCredentials();
                if (!string.IsNullOrEmpty(_apiUsername) && !string.IsNullOrEmpty(_apiKey))
                {
                    var retryClientId = _apiUsername.StartsWith("sdk-client-", StringComparison.OrdinalIgnoreCase)
                        ? _apiUsername
                        : $"sdk-client-{_apiUsername}";
                    var retryAuthBody = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("grant_type", "client_credentials"),
                        new KeyValuePair<string, string>("client_id", retryClientId),
                        new KeyValuePair<string, string>("client_secret", _apiKey)
                    });
                    var retryResponse = await _httpClient.PostAsync($"{_secretServerUrl}/oauth2/token", retryAuthBody);
                    if (retryResponse.IsSuccessStatusCode)
                    {
                        var retryContent = await retryResponse.Content.ReadAsStringAsync();
                        using var retryDoc = JsonDocument.Parse(retryContent);
                        _accessToken = retryDoc.RootElement.GetProperty("access_token").GetString()!;
                        var retryExpiresIn = retryDoc.RootElement.GetProperty("expires_in").GetInt32();
                        _tokenExpiry = DateTime.UtcNow.AddSeconds(retryExpiresIn);
                        _logger.LogInformation("Delinea authentication succeeded after credential reload");
                        _operationTrace.Step("VaultTokenAcquired", backend: "Delinea", command: "oauth2/token (retry)", details: new Dictionary<string, object?> { ["credentialTarget"] = _credentialTarget });
                        return _accessToken;
                    }
                }

                _logger.LogError("Delinea auth failed after retry: {Status} | CredentialTarget: {Target} | OAuthError: {OAuthError}",
                    authResponse.StatusCode, _credentialTarget, oauthError);
                _extLog.Write(LogEventLevel.Error, "Delinea authentication failed", "Delinea", () => $"Target={_credentialTarget}; Status={authResponse.StatusCode}; OAuthError={oauthError}");
                _operationTrace.Step("VaultTokenRequested", "Failed", backend: "Delinea", command: "oauth2/token", details: new Dictionary<string, object?> { ["credentialTarget"] = _credentialTarget, ["status"] = authResponse.StatusCode.ToString(), ["oauthError"] = oauthError });
                throw new InvalidOperationException($"Failed to authenticate to Delinea Secret Server: {authResponse.StatusCode}");
            }

            var tokenContent = await authResponse.Content.ReadAsStringAsync();
            using var tokenDoc = JsonDocument.Parse(tokenContent);

            _accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = tokenDoc.RootElement.GetProperty("expires_in").GetInt32();
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

            _logger.LogInformation("Successfully authenticated to Delinea Secret Server");
            _extLog.Write(LogEventLevel.Information, "Authenticated to Delinea Secret Server", "Delinea", () => $"Target={_credentialTarget}; ExpiresUtc={_tokenExpiry:o}");
            _operationTrace.Step("VaultTokenAcquired", backend: "Delinea", command: "oauth2/token", details: new Dictionary<string, object?> { ["credentialTarget"] = _credentialTarget, ["expiresUtc"] = _tokenExpiry.ToString("O") });
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
