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

    /// <summary>
    /// Graph's documented maximum page size on this collection. Always asked for: there is no
    /// reason to request smaller pages when the ceiling governs the total.
    /// </summary>
    private const int GraphPageSize = 500;

    /// <summary>
    /// Hard bound on requests per query, independent of the row ceiling.
    ///
    /// The row ceiling cannot guarantee the loop ends: a page carrying a continuation link and an
    /// empty <c>value</c> advances the link without advancing the row count, so the ceiling is
    /// never reached and the loop runs until the process dies. This bound rises on every
    /// iteration regardless of what the body contains, which is what makes termination provable.
    ///
    /// 2,000 pages is 1,000,000 rows at the 500 Graph allows - far above any usable ceiling, so it
    /// never fires on a healthy service and never truncates a legitimate result. It exists for the
    /// unhealthy one.
    /// </summary>
    internal const int MaxPages = 2000;

    /// <summary>
    /// Retrieves the COMPLETE set of risky users matching the server-side filters, following
    /// <c>@odata.nextLink</c> to exhaustion (docs/RiskyUsersCompleteResults-Plan.md S2).
    ///
    /// Before this, the module asked for one page of 500 and stopped. Graph documents no default
    /// order for this collection and supports no <c>$orderby</c>, so those 500 were an arbitrary
    /// slice - and `UpnContains` then searched only that slice. A High-risk account visible in the
    /// Entra portal was absent from the page, which is the defect this method exists to fix.
    /// </summary>
    public async Task<RiskyUserPage> GetRiskyUsersAsync(RiskyUserFilter filter)
    {
        var client = await _graphClientFactory() ?? throw new InvalidOperationException("Risky Users Graph credentials not available.");

        var ceiling = ClampCeiling(_moduleConfig?.GetValue("RiskyUsers", "MaxTotalRows"));

        var query = $"$top={GraphPageSize}&$select={SelectFields}";
        var filterExpression = BuildFilterExpression(filter);
        if (filterExpression != null)
            query += $"&$filter={Uri.EscapeDataString(filterExpression)}";

        var users = new List<RiskyUser>();
        var outcome = RiskyUserFetchOutcome.Complete;
        var pages = 0;
        string? next = $"{RiskyUsersEndpoint}?{query}";

        while (next != null)
        {
            var (doc, status) = await client.GetWithStatusAsync(next);

            // A failed request must never render as "no risky users" - S2 rule 1 of the module
            // plan. This holds on page 7 exactly as it holds on page 1: a run that failed partway
            // has NOT produced a complete list, and returning the pages that did arrive would be
            // the same defect wearing a smaller number. 403 keeps its own message because it is
            // the likely first-run outcome on a tenant without P2 or consent.
            if (doc == null)
                throw BuildFailure(status, pages == 0 ? "risky users" : $"risky users (page {pages + 1})");

            using var responseDoc = doc;
            pages++;

            foreach (var item in responseDoc.RootElement.GetProperty("value").EnumerateArray())
                users.Add(ParseRiskyUser(item));

            next = null;
            if (responseDoc.RootElement.TryGetProperty("@odata.nextLink", out var link)
                && link.ValueKind == JsonValueKind.String)
            {
                // TWO independent stops, because the row ceiling alone does not guarantee
                // termination and an earlier version of this loop did not terminate.
                //
                // A page that carries a continuation link but NO rows advances the link without
                // advancing the count, so a service that keeps offering one spins forever - the
                // row ceiling is never reached because rows never arrive. That is not
                // hypothetical: it hung a test host at 24 GB before this guard existed. The page
                // budget is what makes progress monotonic, since the page count rises on every
                // iteration whatever the body contains.
                //
                // The row ceiling is checked AFTER accumulating on purpose: this API exposes no
                // count, so it can only ever be reached on rows already fetched and already paid
                // for. Nothing is discarded.
                if (users.Count >= ceiling || pages >= MaxPages)
                {
                    outcome = RiskyUserFetchOutcome.CeilingReached;
                }
                else
                {
                    // Passed through absolutely and unmodified. GraphTokenClient accepts a Graph
                    // /v1.0 absolute URL for exactly this, and re-encoding a skiptoken would make
                    // it a different token than the one the service issued.
                    next = link.GetString();
                }
            }
        }

        // UpnContains is applied here, after the fetch, because Graph documents no contains() on
        // this collection and a filter it rejects is a 400 that the rule above turns into a hard
        // failure. It now runs over the COMPLETE set rather than over an arbitrary first page,
        // which is what makes the reported "charles" case work.
        if (!string.IsNullOrWhiteSpace(filter.UpnContains))
            users = users.Where(u => u.UserPrincipalName.Contains(filter.UpnContains!, StringComparison.OrdinalIgnoreCase)).ToList();

        users = SortRiskyUsers(users);

        return new RiskyUserPage(users, outcome, ceiling, pages);
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

    /// <summary>
    /// Owner-approved (2026-09-02) L2-plain wording, matching RiskyUsers.razor's ActionLabel, so
    /// the outcome message and audit record echo the same label the operator saw and clicked
    /// rather than the Graph verb.
    /// </summary>
    private static string ActionDisplayName(RiskyUserAction action) => action switch
    {
        RiskyUserAction.Dismiss => "Close as handled",
        RiskyUserAction.ConfirmSafe => "This was the real user",
        RiskyUserAction.ConfirmCompromised => "Account was breached",
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
    /// Total-row ceiling across all pages, read from <c>MaxTotalRows</c>.
    ///
    /// **This is a NEW config key on purpose and must not be folded back into the old
    /// <c>MaxRows</c>.** `MaxRows` meant "$top page size" and this deployment has a stored value
    /// of 500 saved on 2026-09-02. `ModuleConfigService.GetValue` returns the stored row and a
    /// descriptor default never overwrites one, so redefining `MaxRows` as the ceiling would read
    /// back 500, stop after one page and reproduce the exact defect this work removes - from code
    /// that is otherwise correct. A new key has no stored row, so the default below is what is
    /// read on the first query after deploy.
    ///
    /// An unparseable or non-positive value falls back to the default rather than meaning
    /// "unbounded": an unbounded loop against a paged API is how a read module becomes an outage.
    /// </summary>
    internal static int ClampCeiling(string? rawMaxTotalRows)
    {
        if (!int.TryParse(rawMaxTotalRows, out var parsed) || parsed <= 0)
            parsed = DefaultCeiling;

        return Math.Clamp(parsed, 1, MaxCeiling);
    }

    internal const int DefaultCeiling = 10000;
    internal const int MaxCeiling = 200000;

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

/// <summary>
/// How a fetch ended. Three outcomes, never collapsed into two: an incomplete answer must be
/// distinguishable from a complete one, and a failure from both.
/// </summary>
public enum RiskyUserFetchOutcome
{
    /// <summary>Graph stopped offering pages. The set is the whole tenant, for these filters.</summary>
    Complete,

    /// <summary>
    /// More exist; the module stopped asking at the ceiling. The rows fetched are KEPT and
    /// rendered - nothing is discarded, because this API has no count endpoint, so the ceiling can
    /// only ever be reached on rows already retrieved. But the set is a partial one AND the
    /// severity sort ran only over that partial set, so the top of the list is the worst of a
    /// sample rather than the worst in the tenant. Both facts have to reach the operator.
    /// </summary>
    CeilingReached
}

public sealed record RiskyUserPage(
    IReadOnlyList<RiskyUser> Users,
    RiskyUserFetchOutcome Outcome,
    int Ceiling,
    int PagesFetched)
{
    /// <summary>True when rows exist that this fetch did not retrieve.</summary>
    public bool Truncated => Outcome == RiskyUserFetchOutcome.CeilingReached;
}

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
