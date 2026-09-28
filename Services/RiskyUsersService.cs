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
        // Captured BEFORE the UPN filter narrows the list. The notice's job is to say how much of
        // the TENANT was retrieved, which is not the same as how many rows matched the operator's
        // text - and the ceiling is a third number again, since rows arrive a page at a time and
        // the run overshoots the limit on the page that crosses it.
        var retrieved = users.Count;

        if (!string.IsNullOrWhiteSpace(filter.UpnContains))
            users = users.Where(u => u.UserPrincipalName.Contains(filter.UpnContains!, StringComparison.OrdinalIgnoreCase)).ToList();

        users = SortRiskyUsers(users);

        return new RiskyUserPage(users, outcome, ceiling, pages, retrieved);
    }


    /// <summary>
    /// Answers "is this one named person risky?" directly, instead of searching a list for them
    /// (docs/RiskyUsersCompleteResults-Plan.md S5).
    ///
    /// This is the better answer to the defect that opened the plan. A list can be capped, its
    /// order is unspecified, and the UPN match runs in memory; asking Graph about one user is
    /// none of those things and costs two calls at any tenant size. No ceiling can hide anybody
    /// from it.
    ///
    /// **Two calls, by design, and the first one is not optional.** A `riskyUsers` query that
    /// returns nothing means *this UPN has no risk record* - it cannot say whether the UPN belongs
    /// to anybody. Only the directory read separates "Entra says this person is fine" from "you
    /// mistyped the name", and a typo silently reading as a clean bill of health is the worst
    /// outcome this method could produce. That is why it needs a directory read unconditionally.
    ///
    /// **`User.ReadBasic.All` is enough, and that is deliberate.** The call selects `id` and
    /// nothing else - it asks whether the account exists, not who it belongs to - so the basic
    /// profile scope covers it. Verified against the live tenant 2026-09-28: all three outcomes
    /// (no such user, no risk record, risky) pass with `User.ReadBasic.All` alone. The plan
    /// originally specified `User.Read.All`, which reads every user's full profile tenant-wide
    /// and is more privilege than one existence check needs.
    /// </summary>
    public async Task<RiskyUserLookup> LookupAsync(string userPrincipalName)
    {
        var client = await _graphClientFactory() ?? throw new InvalidOperationException("Risky Users Graph credentials not available.");

        var upn = userPrincipalName.Trim();
        if (upn.Length == 0)
            return new RiskyUserLookup(RiskyUserLookupOutcome.NoSuchUser, null, upn);

        // Step 1: does this person exist? 404 stops here and no risk query is made.
        var (userDoc, userStatus) = await client.GetWithStatusAsync($"/users/{Uri.EscapeDataString(upn)}?$select=id");

        if (userStatus == HttpStatusCode.NotFound)
            return new RiskyUserLookup(RiskyUserLookupOutcome.NoSuchUser, null, upn);

        if (userDoc == null)
            throw BuildDirectoryFailure(userStatus);

        string objectId;
        using (userDoc)
        {
            objectId = GetString(userDoc.RootElement, "id");
        }

        // A 200 with no id is not an answer. Failing here beats querying /riskyUsers/ with an
        // empty key, which would 404 and render as "this person has no risk record" - a wrong
        // negative dressed as a real one.
        if (objectId.Length == 0)
            throw new InvalidOperationException($"The directory returned no object id for {upn}, so their risk state could not be looked up.");

        // Step 2: does Entra hold a risk record for them?
        var (riskDoc, riskStatus) = await client.GetWithStatusAsync($"{RiskyUsersEndpoint}/{Uri.EscapeDataString(objectId)}");

        // **404 here is an ANSWER, not a failure.** It is the answer an operator came for: Entra
        // holds no risk record for this person. Rendering it as an error, or as "no risky users
        // found", is the Cloud Password Reset failure class inverted - there an unanswered
        // question was read as a negative answer; here a real negative must not be dressed up as
        // a failure.
        if (riskStatus == HttpStatusCode.NotFound)
            return new RiskyUserLookup(RiskyUserLookupOutcome.NoRiskRecord, null, upn);

        if (riskDoc == null)
            throw BuildFailure(riskStatus, "risky user");

        using (riskDoc)
        {
            return new RiskyUserLookup(RiskyUserLookupOutcome.Risky, ParseRiskyUser(riskDoc.RootElement), upn);
        }
    }

    /// <summary>
    /// A failure of the DIRECTORY read, which names a different missing permission than a failure
    /// of the risk read. Reporting `IdentityRiskyUser.Read.All` when the directory scope is what
    /// is absent sends an administrator to grant the wrong thing and then to disbelieve the
    /// message.
    ///
    /// It names `User.ReadBasic.All` because that is what this call actually requires - see
    /// LookupAsync. Naming `User.Read.All` here, as it did until 2026-09-28, sent an
    /// administrator to grant tenant-wide full-profile read for an existence check.
    /// </summary>
    private static InvalidOperationException BuildDirectoryFailure(HttpStatusCode status)
    {
        if (status == HttpStatusCode.Forbidden)
            return new InvalidOperationException(
                "The directory lookup was refused - verify the app registration has User.ReadBasic.All consent. This is a different permission from the one the risky-user list uses.");

        return new InvalidOperationException($"Graph request for the directory lookup failed: {(int)status} {status}.");
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
    /// The operator-facing label for an action. **The single source** - `RiskyUsers.razor` calls
    /// this rather than keeping its own copy.
    ///
    /// It used to be duplicated, page and service, kept in step by a comment. The service's copy
    /// reaches the outcome message and the admin notification while the page's reaches the button,
    /// so a half-done rename would have had an operator click one name and a different one
    /// reported back. Delegation removes the failure mode instead of testing for it.
    ///
    /// **One word each, and it is the word that DIFFERS in Microsoft's three labels.** The owner,
    /// 2026-09-25, on a draft that put Microsoft's full toolbar strings on every button: *"we do
    /// not need the same long string on every button. that's not UI, that's your context leaking
    /// into the product."* Two of those three open with "Confirm" and all three contain "user", so
    /// the distinguishing token landed last, on every row. Entra can afford them because its
    /// toolbar acts on a selection and renders once; a per-row group cannot.
    ///
    /// The subject comes from the row and the verb category from the column header. What a person
    /// needs to match this against the Entra portal is <see cref="ActionAccessibleName"/>, which
    /// rides along as the accessible name and costs no pixels.
    ///
    /// This supersedes the 2026-09-02 L2-plain wording ("Close as handled" / "This was the real
    /// user" / "Account was breached"). That ruling's REASON still holds - Microsoft's vocabulary
    /// is ambiguous to an L2 operator - but it was answered by making one string do two jobs. The
    /// consequence sentence now does the explaining, once, at the confirmation step.
    /// </summary>
    internal static string ActionDisplayName(RiskyUserAction action) => action switch
    {
        RiskyUserAction.Dismiss => "Dismiss",
        RiskyUserAction.ConfirmSafe => "Safe",
        RiskyUserAction.ConfirmCompromised => "Compromised",
        // Fail loud. A member added to RiskyUserAction without updating this map must not
        // reach an operator, an outcome message or an admin notification under a generic name
        // that hides which action ran.
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown risky user action.")
    };

    /// <summary>
    /// Microsoft's own name for the action, as the Entra admin center toolbar writes it.
    ///
    /// Rendered as the button's `aria-label` and `title` rather than its text: a screen reader
    /// gets the whole phrase, a person can match the control to the portal and to Microsoft's
    /// documentation, and the row costs no extra width. This is the mapping whose absence the
    /// owner reported - *"it's unclear what specific, mapped to Microsoft's page, these options
    /// are"* - and nothing rendered on the page carried it before.
    /// </summary>
    internal static string ActionAccessibleName(RiskyUserAction action) => action switch
    {
        RiskyUserAction.Dismiss => "Dismiss user risk",
        RiskyUserAction.ConfirmSafe => "Confirm user safe",
        RiskyUserAction.ConfirmCompromised => "Confirm user compromised",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown risky user action.")
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

/// <summary>
/// What a direct lookup found. Three outcomes, and two of them are NEGATIVES that must read
/// differently: "this person exists and Entra holds nothing on them" is a clean answer, while
/// "no such person" usually means a typo. Collapsing them turns a mistyped name into a clean
/// bill of health.
/// </summary>
public enum RiskyUserLookupOutcome
{
    /// <summary>Entra holds a risk record. <c>User</c> is populated.</summary>
    Risky,

    /// <summary>The person exists; Entra holds no risk record for them. A clean no.</summary>
    NoRiskRecord,

    /// <summary>No directory user matches that name.</summary>
    NoSuchUser
}

public sealed record RiskyUserLookup(
    RiskyUserLookupOutcome Outcome,
    RiskyUser? User,
    string UserPrincipalName);

public sealed record RiskyUserPage(
    IReadOnlyList<RiskyUser> Users,
    RiskyUserFetchOutcome Outcome,
    int Ceiling,
    int PagesFetched,
    int RetrievedCount)
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
