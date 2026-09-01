using System.Net;
using System.Text.Json;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Read and write paths for Microsoft Entra ID Protection risky users
/// (docs/RiskyUsersModule-Plan.md, S2 and S5). Graph v1.0. Reads use application permission
/// IdentityRiskyUser.Read.All; the write actions (dismiss, confirm safe, confirm compromised)
/// use IdentityRiskyUser.ReadWrite.All. The ticket, confirmation, protected-principal, audit
/// and notification gates around the write actions are page-level (S6), not this service.
/// </summary>
public sealed class RiskyUsersService
{
    private readonly ILogger<RiskyUsersService>? _logger;
    private readonly ModuleConfigService? _moduleConfig;
    private readonly DelineaService? _delineaService;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly Func<Task<GraphTokenClient?>> _graphClientFactory;

    public RiskyUsersService(ILogger<RiskyUsersService> logger, ModuleConfigService moduleConfig, DelineaService delineaService, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _moduleConfig = moduleConfig;
        _delineaService = delineaService;
        _httpClientFactory = httpClientFactory;
        _graphClientFactory = GetGraphClientAsync;
    }

    /// <summary>
    /// Test seam: drives GetRiskyUsersAsync/GetHistoryAsync's parse, filter, truncation and sort
    /// logic against a canned GraphTokenClient without exercising ModuleConfigService or
    /// DelineaService (which would otherwise need a live Secret Server call). Does not change the
    /// public DI constructor above or its Program.cs registration.
    /// </summary>
    internal RiskyUsersService(Func<Task<GraphTokenClient?>> graphClientFactory)
    {
        _graphClientFactory = graphClientFactory;
    }

    private async Task<GraphTokenClient?> GetGraphClientAsync()
    {
        if (_moduleConfig == null || _delineaService == null || _httpClientFactory == null)
            return null;

        var secretIdStr = _moduleConfig.GetValue("RiskyUsers", "GraphDelineaSecretId");
        if (!int.TryParse(secretIdStr, out var secretId) || secretId <= 0)
            return null;

        var fields = await _delineaService.GetSecretFieldsAsync(secretId);
        if (fields == null) return null;

        var tenantId = fields.GetValueOrDefault("Tenant ID") ?? "";
        var clientId = fields.GetValueOrDefault("Application ID") ?? "";
        var clientSecret = fields.GetValueOrDefault("Client Secret") ?? "";

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            return null;

        return new GraphTokenClient(tenantId, clientId, clientSecret, _httpClientFactory.CreateClient("MicrosoftGraph"));
    }

    public bool IsAvailable
    {
        get
        {
            var secretIdStr = _moduleConfig?.GetValue("RiskyUsers", "GraphDelineaSecretId");
            return int.TryParse(secretIdStr, out var id) && id > 0;
        }
    }

    private const string RiskyUsersEndpoint = "/identityProtection/riskyUsers";
    private const string SelectFields = "id,isDeleted,isProcessing,riskLastUpdatedDateTime,riskLevel,riskState,riskDetail,userDisplayName,userPrincipalName";

    public async Task<RiskyUserPage> GetRiskyUsersAsync(RiskyUserFilter filter)
    {
        var client = await _graphClientFactory() ?? throw new InvalidOperationException("Risky Users Graph credentials not available.");

        var top = ClampMaxRows(_moduleConfig?.GetValue("RiskyUsers", "MaxRows"));

        var query = $"$top={top}&$select={SelectFields}";
        var filterExpression = BuildFilterExpression(filter);
        if (filterExpression != null)
            query += $"&$filter={Uri.EscapeDataString(filterExpression)}";

        var (doc, status) = await client.GetWithStatusAsync($"{RiskyUsersEndpoint}?{query}");

        // A failed request must never render as "no risky users" - rule 1 (S2). 403 is the
        // expected shape on a tenant with no P2 or without consent, and gets its own message
        // because it is the single most likely first-run outcome.
        if (doc == null)
            throw BuildFailure(status, "risky users");

        using var responseDoc = doc;

        // @odata.nextLink is absolute and GraphTokenClient prepends a relative path to a
        // hardcoded base URL, so it cannot be followed (S2 rule 2). Its presence must still be
        // surfaced so a capped list never looks like a complete one.
        var truncated = responseDoc.RootElement.TryGetProperty("@odata.nextLink", out _);

        var users = new List<RiskyUser>();
        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
            users.Add(ParseRiskyUser(item));

        // UpnContains is not documented as a supported $filter on this resource, so it is applied
        // client-side after the fetch (S2 rule 3).
        if (!string.IsNullOrWhiteSpace(filter.UpnContains))
            users = users.Where(u => u.UserPrincipalName.Contains(filter.UpnContains!, StringComparison.OrdinalIgnoreCase)).ToList();

        users = SortRiskyUsers(users);

        return new RiskyUserPage(users, truncated, top);
    }

    public async Task<IReadOnlyList<RiskyUserHistoryEntry>> GetHistoryAsync(string userId)
    {
        var client = await _graphClientFactory() ?? throw new InvalidOperationException("Risky Users Graph credentials not available.");

        var (doc, status) = await client.GetWithStatusAsync($"{RiskyUsersEndpoint}/{Uri.EscapeDataString(userId)}/history");
        if (doc == null)
            throw BuildFailure(status, "risky user history");

        using var _ = doc;

        var entries = new List<RiskyUserHistoryEntry>();
        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
            entries.Add(ParseHistoryEntry(item));

        return entries;
    }

    /// <summary>
    /// Dismiss, confirm safe, or confirm compromised for a single risky user (S5). One HTTP call
    /// per user: all three Graph endpoints accept a userIds array and return one bare 204 for the
    /// whole batch with no per-user body, so posting more than one id per call would make it
    /// impossible to say which user succeeded - Known Failure Class 2 written into the API
    /// itself. Calling once per user gives every caller (the S6 page, one row at a time) its own
    /// named outcome instead. Does not check riskState or isProcessing before posting - Graph's
    /// own refusal, if any, is this call's failure result; no client-side eligibility allowlist.
    /// </summary>
    public async Task<RiskyUserActionResult> ApplyActionAsync(string userId, RiskyUserAction action)
    {
        var client = await _graphClientFactory() ?? throw new InvalidOperationException("Risky Users Graph credentials not available.");

        var ok = await client.PostNoContentAsync(ActionEndpoint(action), new { userIds = new[] { userId } });

        return ok
            ? new RiskyUserActionResult(userId, true, $"{ActionDisplayName(action)} succeeded.")
            : new RiskyUserActionResult(userId, false, $"{ActionDisplayName(action)} failed. Graph rejected the request for this user - check the user's current riskState.");
    }

    private static string ActionEndpoint(RiskyUserAction action) => action switch
    {
        RiskyUserAction.Dismiss => $"{RiskyUsersEndpoint}/dismiss",
        RiskyUserAction.ConfirmSafe => $"{RiskyUsersEndpoint}/confirmSafe",
        RiskyUserAction.ConfirmCompromised => $"{RiskyUsersEndpoint}/confirmCompromised",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown risky user action.")
    };

    private static string ActionDisplayName(RiskyUserAction action) => action switch
    {
        RiskyUserAction.Dismiss => "Dismiss",
        RiskyUserAction.ConfirmSafe => "Confirm safe",
        RiskyUserAction.ConfirmCompromised => "Confirm compromised",
        _ => action.ToString()
    };

    private static InvalidOperationException BuildFailure(HttpStatusCode status, string context)
    {
        if (status == HttpStatusCode.Forbidden)
            return new InvalidOperationException(
                "Risky Users is not available for this tenant - verify Entra ID P2 licensing and the app registration's IdentityRiskyUser.Read.All consent.");

        return new InvalidOperationException($"Graph request for {context} failed: {(int)status} {status}.");
    }

    /// <summary>
    /// $top clamp for the risky users list. Graph caps $top at 500 on this resource; an
    /// unparseable or non-positive MaxRows config value falls back to the same cap rather than a
    /// silently unbounded or zero-row request.
    /// </summary>
    internal static int ClampMaxRows(string? rawMaxRows)
    {
        if (!int.TryParse(rawMaxRows, out var parsed) || parsed <= 0)
            parsed = 500;

        return Math.Clamp(parsed, 1, 500);
    }

    /// <summary>
    /// Builds the $filter expression from the server-side-supported fields only (riskLevel,
    /// riskState). Single quotes in either value are doubled, not interpolated raw, per the
    /// M365GroupManagementService.cs OData literal-escaping shape. The caller URL-escapes the
    /// whole returned expression via Uri.EscapeDataString.
    /// </summary>
    internal static string? BuildFilterExpression(RiskyUserFilter filter)
    {
        var clauses = new List<string>();

        if (!string.IsNullOrWhiteSpace(filter.RiskLevel))
            clauses.Add($"riskLevel eq '{EscapeODataLiteral(filter.RiskLevel)}'");

        if (!string.IsNullOrWhiteSpace(filter.RiskState))
            clauses.Add($"riskState eq '{EscapeODataLiteral(filter.RiskState)}'");

        return clauses.Count == 0 ? null : string.Join(" and ", clauses);
    }

    private static string EscapeODataLiteral(string value) => value.Replace("'", "''");

    // Default order: high, medium, low, hidden, none, then anything unrecognised (S2 rule 5).
    // riskLevel/riskState are stored as plain strings, never parsed into a C# enum or filtered
    // against this list (S2 rule 4) - unrecognised values still render and still sort, last.
    private static readonly string[] RiskLevelSeverityOrder = ["high", "medium", "low", "hidden", "none"];

    internal static List<RiskyUser> SortRiskyUsers(IEnumerable<RiskyUser> users) =>
        users
            .OrderBy(u => RiskLevelRank(u.RiskLevel))
            .ThenByDescending(u => u.RiskLastUpdatedDateTime)
            .ToList();

    internal static int RiskLevelRank(string riskLevel)
    {
        var idx = Array.IndexOf(RiskLevelSeverityOrder, riskLevel.ToLowerInvariant());
        return idx >= 0 ? idx : RiskLevelSeverityOrder.Length;
    }

    private static RiskyUser ParseRiskyUser(JsonElement item) => new()
    {
        Id = GetString(item, "id"),
        UserPrincipalName = GetString(item, "userPrincipalName"),
        UserDisplayName = GetString(item, "userDisplayName"),
        RiskLevel = GetString(item, "riskLevel"),
        RiskState = GetString(item, "riskState"),
        RiskDetail = GetString(item, "riskDetail"),
        RiskLastUpdatedDateTime = GetDateTimeOffset(item, "riskLastUpdatedDateTime"),
        IsProcessing = GetBool(item, "isProcessing"),
        IsDeleted = GetBool(item, "isDeleted")
    };

    private static RiskyUserHistoryEntry ParseHistoryEntry(JsonElement item) => new()
    {
        Id = GetString(item, "id"),
        UserPrincipalName = GetString(item, "userPrincipalName"),
        UserDisplayName = GetString(item, "userDisplayName"),
        RiskLevel = GetString(item, "riskLevel"),
        RiskState = GetString(item, "riskState"),
        RiskDetail = GetString(item, "riskDetail"),
        RiskLastUpdatedDateTime = GetDateTimeOffset(item, "riskLastUpdatedDateTime"),
        IsProcessing = GetBool(item, "isProcessing"),
        IsDeleted = GetBool(item, "isDeleted")
    };

    private static string GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? "" : "";

    private static bool GetBool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? GetDateTimeOffset(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;

        return DateTimeOffset.TryParse(prop.GetString(), out var value) ? value : null;
    }
}

public sealed record RiskyUserFilter(string? RiskLevel, string? RiskState, string? UpnContains);

/// <summary>Outcome of a single-user write action (S5). One per Graph call, never a batch verdict.</summary>
public sealed record RiskyUserActionResult(string UserId, bool Success, string Message);

/// <summary>The three v1.0 remediation actions this module supports (S5). No riskState-based
/// eligibility allowlist is derived from this - Graph decides what it will accept.</summary>
public enum RiskyUserAction
{
    Dismiss,
    ConfirmSafe,
    ConfirmCompromised
}

public sealed record RiskyUserPage(
    IReadOnlyList<RiskyUser> Users,
    bool Truncated,
    int RequestedMax);

public sealed class RiskyUser
{
    public string Id { get; set; } = "";
    public string UserPrincipalName { get; set; } = "";
    public string UserDisplayName { get; set; } = "";
    public string RiskLevel { get; set; } = "";
    public string RiskState { get; set; } = "";
    public string RiskDetail { get; set; } = "";
    public DateTimeOffset? RiskLastUpdatedDateTime { get; set; }
    public bool IsProcessing { get; set; }
    public bool IsDeleted { get; set; }
}

public sealed class RiskyUserHistoryEntry
{
    public string Id { get; set; } = "";
    public string UserPrincipalName { get; set; } = "";
    public string UserDisplayName { get; set; } = "";
    public string RiskLevel { get; set; } = "";
    public string RiskState { get; set; } = "";
    public string RiskDetail { get; set; } = "";
    public DateTimeOffset? RiskLastUpdatedDateTime { get; set; }
    public bool IsProcessing { get; set; }
    public bool IsDeleted { get; set; }
}
