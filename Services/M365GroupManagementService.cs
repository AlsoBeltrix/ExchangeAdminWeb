using System.Text.Json;

namespace ExchangeAdminWeb.Services;

public class M365GroupManagementService
{
    private readonly ILogger<M365GroupManagementService> _logger;
    private readonly ModuleConfigService _moduleConfig;
    private readonly DelineaService _delineaService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OperationTraceService _operationTrace;
    private readonly AuditService _audit;

    public M365GroupManagementService(
        ModuleConfigService moduleConfig,
        DelineaService delineaService,
        IHttpClientFactory httpClientFactory,
        OperationTraceService operationTrace,
        AuditService audit,
        ILogger<M365GroupManagementService> logger)
    {
        _moduleConfig = moduleConfig;
        _delineaService = delineaService;
        _httpClientFactory = httpClientFactory;
        _operationTrace = operationTrace;
        _audit = audit;
        _logger = logger;
    }

    private async Task<GraphTokenClient> GetGraphClientAsync()
    {
        var secretIdStr = _moduleConfig.GetValue("M365GroupManagement", "GraphDelineaSecretId")
                       ?? _moduleConfig.GetValue("M365GroupManagement", "DelineaSecretId");
        if (!int.TryParse(secretIdStr, out var secretId) || secretId <= 0)
            throw new InvalidOperationException("M365 Group Management module is not configured. Set Graph App Delinea Secret ID in Module Config.");

        var fields = await _delineaService.GetSecretFieldsAsync(secretId);
        if (fields == null)
            throw new InvalidOperationException($"Cannot retrieve M365 Group Management Graph app secret {secretId} from Secret Server. Verify this is the correct Secret ID and that the Delinea SDK client can view it.");

        var tenantId = fields.GetValueOrDefault("Tenant ID") ?? "";
        var clientId = fields.GetValueOrDefault("Application ID") ?? "";
        var clientSecret = fields.GetValueOrDefault("Client Secret") ?? "";

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            throw new InvalidOperationException("Graph API credentials incomplete in Secret Server.");

        return new GraphTokenClient(tenantId, clientId, clientSecret, _httpClientFactory.CreateClient("MicrosoftGraph"));
    }

    public bool IsAvailable
    {
        get
        {
            var secretIdStr = _moduleConfig.GetValue("M365GroupManagement", "GraphDelineaSecretId")
                       ?? _moduleConfig.GetValue("M365GroupManagement", "DelineaSecretId");
            return int.TryParse(secretIdStr, out var id) && id > 0;
        }
    }

    public async Task<List<M365Group>> SearchGroupsAsync(string searchTerm)
    {
        var client = await GetGraphClientAsync();
        var groups = new List<M365Group>();

        var odataEscaped = searchTerm.Replace("'", "''");
        var filterQuery = Uri.EscapeDataString($"groupTypes/any(g:g eq 'Unified') and startsWith(displayName,'{odataEscaped}')");
        var endpoint = $"/groups?$filter={filterQuery}&$select=id,displayName,mail,description,visibility,createdDateTime&$top=50";

        var (doc, status) = await client.GetWithStatusAsync(endpoint);
        if (doc == null)
        {
            // A failed search must not render as "no groups found".
            throw new InvalidOperationException($"Graph search for M365 groups failed: {(int)status} {status}.");
        }
        using var _ = doc;

        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            groups.Add(ParseGroup(item));
        }

        return groups.OrderBy(g => g.DisplayName).ToList();
    }

    public async Task<M365Group?> GetGroupDetailsAsync(string groupId)
    {
        var client = await GetGraphClientAsync();
        using var doc = await client.GetAsync($"/groups/{Uri.EscapeDataString(groupId)}?$select=id,displayName,mail,description,visibility,groupTypes,mailEnabled,securityEnabled,createdDateTime");
        if (doc == null) return null;
        return ParseGroup(doc.RootElement);
    }

    public async Task<M365GroupResult> CreateGroupAsync(string displayName, string mailNickname, string? description, string visibility)
    {
        var client = await GetGraphClientAsync();
        var body = new Dictionary<string, object>
        {
            ["displayName"] = displayName,
            ["mailNickname"] = mailNickname,
            ["description"] = description ?? "",
            ["visibility"] = visibility,
            ["groupTypes"] = new[] { "Unified" },
            ["mailEnabled"] = true,
            ["securityEnabled"] = false
        };

        using var doc = await client.PostAsync("/groups", body);
        if (doc == null)
            return new M365GroupResult { Success = false, Message = "Failed to create M365 group. Check module configuration and Graph app permissions (Group.ReadWrite.All)." };

        var id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        _logger.LogInformation("Created M365 group '{Name}' (id={Id})", displayName, id);
        return new M365GroupResult { Success = true, Message = $"Created M365 group '{displayName}'." };
    }

    public async Task<M365GroupResult> UpdateGroupAsync(string groupId, string displayName, string? description, string visibility)
    {
        var client = await GetGraphClientAsync();
        var body = new Dictionary<string, object>
        {
            ["displayName"] = displayName,
            ["description"] = description ?? "",
            ["visibility"] = visibility
        };

        var success = await client.PatchAsync($"/groups/{Uri.EscapeDataString(groupId)}", body);
        if (!success)
            return new M365GroupResult { Success = false, Message = "Failed to update M365 group." };

        _logger.LogInformation("Updated M365 group '{Name}' (id={Id})", displayName, groupId);
        return new M365GroupResult { Success = true, Message = $"Updated '{displayName}'." };
    }

    public async Task<M365GroupResult> DeleteGroupAsync(string groupId, string displayName)
    {
        var client = await GetGraphClientAsync();
        var success = await client.DeleteAsync($"/groups/{Uri.EscapeDataString(groupId)}");
        if (!success)
            return new M365GroupResult { Success = false, Message = "Failed to delete M365 group." };

        _logger.LogInformation("Deleted M365 group '{Name}' (id={Id})", displayName, groupId);
        return new M365GroupResult { Success = true, Message = $"Deleted '{displayName}'." };
    }

    public async Task<List<M365GroupMember>> GetMembersAsync(string groupId)
    {
        return await GetPagedMembersAsync($"/groups/{Uri.EscapeDataString(groupId)}/members?$select=displayName,mail,userPrincipalName&$top=999");
    }

    public async Task<List<M365GroupMember>> GetOwnersAsync(string groupId)
    {
        return await GetPagedMembersAsync($"/groups/{Uri.EscapeDataString(groupId)}/owners?$select=displayName,mail,userPrincipalName&$top=999");
    }

    private async Task<List<M365GroupMember>> GetPagedMembersAsync(string initialUrl)
    {
        var client = await GetGraphClientAsync();
        var results = new List<M365GroupMember>();
        var url = initialUrl;

        while (!string.IsNullOrEmpty(url))
        {
            using var doc = await client.GetAsync(url);
            if (doc == null) break;

            foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
                results.Add(ParseMember(item));

            if (doc.RootElement.TryGetProperty("@odata.nextLink", out var next))
            {
                var nextUrl = next.GetString();
                if (nextUrl != null && nextUrl.StartsWith("https://graph.microsoft.com/v1.0", StringComparison.OrdinalIgnoreCase))
                    nextUrl = nextUrl["https://graph.microsoft.com/v1.0".Length..];
                url = nextUrl;
            }
            else
            {
                url = null;
            }
        }

        return results.OrderBy(m => m.DisplayName).ToList();
    }

    private static M365Group ParseGroup(JsonElement item)
    {
        return new M365Group
        {
            Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            DisplayName = item.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
            Mail = item.TryGetProperty("mail", out var mail) ? mail.GetString() ?? "" : "",
            Description = item.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
            Visibility = item.TryGetProperty("visibility", out var vis) ? vis.GetString() ?? "" : "",
            CreatedDateTime = item.TryGetProperty("createdDateTime", out var cd) ? cd.GetString() ?? "" : "",
            MailEnabled = item.TryGetProperty("mailEnabled", out var me) && me.ValueKind == JsonValueKind.True,
            SecurityEnabled = item.TryGetProperty("securityEnabled", out var se) && se.ValueKind == JsonValueKind.True
        };
    }

    private static M365GroupMember ParseMember(JsonElement item)
    {
        return new M365GroupMember
        {
            DisplayName = item.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
            Mail = item.TryGetProperty("mail", out var mail) ? mail.GetString() ?? "" : "",
            UserPrincipalName = item.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString() ?? "" : ""
        };
    }
}

public class M365Group
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Mail { get; set; } = "";
    public string Description { get; set; } = "";
    public string Visibility { get; set; } = "";
    public string CreatedDateTime { get; set; } = "";
    public bool MailEnabled { get; set; }
    public bool SecurityEnabled { get; set; }
}

public class M365GroupMember
{
    public string DisplayName { get; set; } = "";
    public string Mail { get; set; } = "";
    public string UserPrincipalName { get; set; } = "";
}

public class M365GroupResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
