using System.Text.Json;

namespace ExchangeAdminWeb.Services;

public class MfaResetService
{
    private readonly ILogger<MfaResetService> _logger;
    private readonly ModuleConfigService _moduleConfig;
    private readonly IHttpClientFactory _httpClientFactory;
    private GraphTokenClient? _cachedClient;
    private string _cachedConfigKey = "";

    public MfaResetService(ILogger<MfaResetService> logger, ModuleConfigService moduleConfig, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _moduleConfig = moduleConfig;
        _httpClientFactory = httpClientFactory;
    }

    private GraphTokenClient GetGraphClient()
    {
        var tenantId = _moduleConfig.GetValue("MfaReset", "TenantId") ?? "";
        var clientId = _moduleConfig.GetValue("MfaReset", "ClientId") ?? "";
        var credTarget = _moduleConfig.GetValue("MfaReset", "CredentialTarget") ?? "Graph_MFAResets";

        var configKey = $"{tenantId}|{clientId}|{credTarget}";
        if (_cachedClient != null && _cachedConfigKey == configKey)
            return _cachedClient;

        _cachedClient = new GraphTokenClient(tenantId, clientId, credTarget, _httpClientFactory.CreateClient("MicrosoftGraph"));
        _cachedConfigKey = configKey;
        return _cachedClient;
    }

    public bool IsAvailable => GetGraphClient().IsConfigured;

    public async Task<List<AuthMethod>> GetUserMethodsAsync(string userPrincipalName)
    {
        var methods = new List<AuthMethod>();
        using var doc = await GetGraphClient().GetAsync($"/users/{Uri.EscapeDataString(userPrincipalName)}/authentication/methods");
        if (doc == null) return methods;

        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var type = item.GetProperty("@odata.type").GetString() ?? "";
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

            if (string.IsNullOrEmpty(id)) continue;

            var method = new AuthMethod
            {
                Id = id,
                ODataType = type,
                MethodType = ParseMethodType(type),
                Detail = ExtractDetail(item, type)
            };

            if (method.MethodType != "Password")
                methods.Add(method);
        }

        return methods;
    }

    public async Task<MfaResetResult> ResetAllMethodsAsync(string userPrincipalName)
    {
        var methods = await GetUserMethodsAsync(userPrincipalName);
        if (methods.Count == 0)
            return new MfaResetResult { Success = true, Message = "No MFA methods to remove.", RemovedCount = 0 };

        var removed = 0;
        var errors = new List<string>();

        var ordered = OrderForRemoval(methods);

        foreach (var method in ordered)
        {
            var endpoint = GetDeleteEndpoint(userPrincipalName, method);
            if (endpoint == null)
            {
                errors.Add($"{method.MethodType}: no delete endpoint");
                continue;
            }

            var success = await GetGraphClient().DeleteAsync(endpoint);
            if (success)
            {
                removed++;
                _logger.LogInformation("Removed {Type} for {User}", method.MethodType, userPrincipalName);
            }
            else
            {
                errors.Add($"{method.MethodType}: delete failed");
                _logger.LogWarning("Failed to remove {Type} ({Id}) for {User}", method.MethodType, method.Id, userPrincipalName);
            }
        }

        return new MfaResetResult
        {
            Success = errors.Count == 0,
            Message = errors.Count == 0
                ? $"Successfully removed {removed} MFA method(s). User will be prompted to re-register at next sign-in."
                : $"Removed {removed} method(s). Errors: {string.Join("; ", errors)}",
            RemovedCount = removed,
            TotalMethods = methods.Count
        };
    }

    private static List<AuthMethod> OrderForRemoval(List<AuthMethod> methods)
    {
        var order = new[] { "AlternateMobile", "Mobile", "Phone", "MicrosoftAuthenticator", "Fido2", "Email", "SoftwareOath", "TemporaryAccessPass", "WindowsHello" };
        return methods.OrderBy(m => Array.IndexOf(order, m.MethodType) is var idx && idx >= 0 ? idx : 99).ToList();
    }

    private static string? GetDeleteEndpoint(string upn, AuthMethod method)
    {
        var escaped = Uri.EscapeDataString(upn);
        return method.ODataType switch
        {
            "#microsoft.graph.phoneAuthenticationMethod" => $"/users/{escaped}/authentication/phoneMethods/{method.Id}",
            "#microsoft.graph.microsoftAuthenticatorAuthenticationMethod" => $"/users/{escaped}/authentication/microsoftAuthenticatorMethods/{method.Id}",
            "#microsoft.graph.fido2AuthenticationMethod" => $"/users/{escaped}/authentication/fido2Methods/{method.Id}",
            "#microsoft.graph.emailAuthenticationMethod" => $"/users/{escaped}/authentication/emailMethods/{method.Id}",
            "#microsoft.graph.softwareOathAuthenticationMethod" => $"/users/{escaped}/authentication/softwareOathMethods/{method.Id}",
            "#microsoft.graph.temporaryAccessPassAuthenticationMethod" => $"/users/{escaped}/authentication/temporaryAccessPassMethods/{method.Id}",
            "#microsoft.graph.windowsHelloForBusinessAuthenticationMethod" => $"/users/{escaped}/authentication/windowsHelloForBusinessMethods/{method.Id}",
            _ => null
        };
    }

    private static string ParseMethodType(string odataType) => odataType switch
    {
        "#microsoft.graph.phoneAuthenticationMethod" => "Phone",
        "#microsoft.graph.microsoftAuthenticatorAuthenticationMethod" => "MicrosoftAuthenticator",
        "#microsoft.graph.fido2AuthenticationMethod" => "Fido2",
        "#microsoft.graph.emailAuthenticationMethod" => "Email",
        "#microsoft.graph.softwareOathAuthenticationMethod" => "SoftwareOath",
        "#microsoft.graph.temporaryAccessPassAuthenticationMethod" => "TemporaryAccessPass",
        "#microsoft.graph.windowsHelloForBusinessAuthenticationMethod" => "WindowsHello",
        "#microsoft.graph.passwordAuthenticationMethod" => "Password",
        _ => odataType
    };

    private static string ExtractDetail(JsonElement item, string odataType)
    {
        if (odataType.Contains("phone") && item.TryGetProperty("phoneNumber", out var phone))
            return phone.GetString() ?? "";
        if (odataType.Contains("email") && item.TryGetProperty("emailAddress", out var email))
            return email.GetString() ?? "";
        if (odataType.Contains("microsoftAuthenticator") && item.TryGetProperty("displayName", out var display))
            return display.GetString() ?? "";
        if (odataType.Contains("fido2") && item.TryGetProperty("displayName", out var fido))
            return fido.GetString() ?? "";
        return "";
    }
}

public class AuthMethod
{
    public string Id { get; set; } = "";
    public string ODataType { get; set; } = "";
    public string MethodType { get; set; } = "";
    public string Detail { get; set; } = "";
}

public class MfaResetResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int RemovedCount { get; set; }
    public int TotalMethods { get; set; }
}
