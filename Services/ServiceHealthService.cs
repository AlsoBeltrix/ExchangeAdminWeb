using System.Text;
using System.Text.Json;
using Ganss.Xss;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Read-only Microsoft 365 service health: current per-service status and the tenant's open
/// service incidents, including each incident's running update timeline
/// (docs/ServiceHealth-Plan.md). Graph v1.0, application permission ServiceHealth.Read.All.
/// Ported from the standalone Flask app at D:\source\servicehealthmonitor; the module mutates
/// nothing.
/// </summary>
public sealed class ServiceHealthService
{
    private readonly ILogger<ServiceHealthService>? _logger;
    private readonly ModuleConfigService? _moduleConfig;
    private readonly DelineaService? _delineaService;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly Func<Task<GraphTokenClient?>> _graphClientFactory;

    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private ServiceHealthStatus? _cached;

    /// <summary>
    /// Matches CACHE_TTL_SECONDS in the source app: the dashboard is a status board, not a live
    /// feed, and every operator opening the page must not cost a Graph round trip.
    /// </summary>
    internal const int CacheTtlSeconds = 600;

    public ServiceHealthService(ILogger<ServiceHealthService> logger, ModuleConfigService moduleConfig, DelineaService delineaService, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _moduleConfig = moduleConfig;
        _delineaService = delineaService;
        _httpClientFactory = httpClientFactory;
        _graphClientFactory = GetGraphClientAsync;
    }

    /// <summary>
    /// Test seam, same shape as RiskyUsersService.cs:34-37: drives the parse, sanitize, sort and
    /// cache logic against a canned GraphTokenClient without a live Secret Server call. Does not
    /// change the public DI constructor or its Program.cs registration.
    /// </summary>
    internal ServiceHealthService(Func<Task<GraphTokenClient?>> graphClientFactory)
    {
        _graphClientFactory = graphClientFactory;
    }

    private async Task<GraphTokenClient?> GetGraphClientAsync()
    {
        if (_moduleConfig == null || _delineaService == null || _httpClientFactory == null)
            return null;

        var secretIdStr = _moduleConfig.GetValue("ServiceHealth", "GraphDelineaSecretId");
        if (!int.TryParse(secretIdStr, out var secretId) || secretId <= 0)
            throw new InvalidOperationException("Service Health is not configured. Set Graph App Delinea Secret ID in Module Config.");

        var fields = await _delineaService.GetSecretFieldsAsync(secretId);
        if (fields == null)
            throw new InvalidOperationException($"Cannot retrieve Service Health Graph app secret {secretId} from Secret Server. Verify this is the correct Secret ID and that the Delinea SDK client can view it.");

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
            var secretIdStr = _moduleConfig?.GetValue("ServiceHealth", "GraphDelineaSecretId");
            return int.TryParse(secretIdStr, out var id) && id > 0;
        }
    }

    private const string HealthOverviewsEndpoint = "/admin/serviceAnnouncement/healthOverviews";
    private const string IssuesEndpoint = "/admin/serviceAnnouncement/issues?$filter=isResolved%20eq%20false";

    /// <summary>
    /// Returns the cached status when it is still inside the TTL, otherwise fetches both Graph
    /// collections. A failed fetch throws: a status board that renders "all healthy" off a failed
    /// request is worse than one that renders an error (Known Failure Class 2, and
    /// NamedLocationsService.cs:61-64 for the same rule on a list read).
    /// </summary>
    public async Task<ServiceHealthStatus> GetStatusAsync(bool forceRefresh = false)
    {
        if (!forceRefresh)
        {
            var snapshot = _cached;
            if (snapshot != null && !IsStale(snapshot))
                return snapshot;
        }

        await _cacheLock.WaitAsync();
        try
        {
            // Re-check inside the lock: a queued caller must not repeat a fetch that the caller
            // ahead of it just completed.
            if (!forceRefresh)
            {
                var snapshot = _cached;
                if (snapshot != null && !IsStale(snapshot))
                    return snapshot;
            }

            var client = await _graphClientFactory()
                ?? throw new InvalidOperationException("Service Health Graph credentials not available.");

            var services = await FetchServicesAsync(client);
            var issues = await FetchIssuesAsync(client);

            var status = new ServiceHealthStatus
            {
                Services = services,
                Issues = issues,
                LastUpdatedUtc = DateTime.UtcNow
            };

            _cached = status;
            return status;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static bool IsStale(ServiceHealthStatus status) =>
        (DateTime.UtcNow - status.LastUpdatedUtc).TotalSeconds >= CacheTtlSeconds;

    private static async Task<List<ServiceHealthEntry>> FetchServicesAsync(GraphTokenClient client)
    {
        var (doc, status) = await client.GetWithStatusAsync(HealthOverviewsEndpoint);
        if (doc == null)
            throw new InvalidOperationException($"Graph request for service health failed: {(int)status} {status}.");

        using var _ = doc;
        var services = new List<ServiceHealthEntry>();

        if (!doc.RootElement.TryGetProperty("value", out var value))
            return services;

        foreach (var item in value.EnumerateArray())
        {
            var id = GetString(item, "id");
            var serviceName = GetString(item, "service");
            var statusValue = GetString(item, "status");

            services.Add(new ServiceHealthEntry
            {
                Id = id,
                DisplayName = ServiceDisplayNames.TryGetValue(id, out var friendly)
                    ? friendly
                    : (string.IsNullOrEmpty(serviceName) ? id : serviceName),
                Status = statusValue,
                StatusText = HumanizeStatus(statusValue)
            });
        }

        return services.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<List<ServiceIncident>> FetchIssuesAsync(GraphTokenClient client)
    {
        var (doc, status) = await client.GetWithStatusAsync(IssuesEndpoint);
        if (doc == null)
            throw new InvalidOperationException($"Graph request for service incidents failed: {(int)status} {status}.");

        using var _ = doc;
        var issues = new List<ServiceIncident>();

        if (!doc.RootElement.TryGetProperty("value", out var value))
            return issues;

        foreach (var item in value.EnumerateArray())
        {
            issues.Add(ParseIncident(item));
        }

        return issues.OrderByDescending(i => i.StartDateTime ?? DateTimeOffset.MinValue).ToList();
    }

    internal static ServiceIncident ParseIncident(JsonElement item)
    {
        var serviceName = GetString(item, "service");

        var incident = new ServiceIncident
        {
            Id = GetString(item, "id"),
            Title = GetString(item, "title"),
            ServiceName = serviceName,
            ServiceId = IssueServiceToId.TryGetValue(serviceName, out var mapped) ? mapped : serviceName,
            Classification = GetString(item, "classification"),
            Status = GetString(item, "status"),
            StatusText = HumanizeStatus(GetString(item, "status")),
            Feature = GetString(item, "feature"),
            StartDateTime = GetDate(item, "startDateTime"),
            LastModifiedDateTime = GetDate(item, "lastModifiedDateTime"),
            ImpactDescriptionHtml = Sanitize(GetString(item, "impactDescription"))
        };

        if (item.TryGetProperty("posts", out var posts) && posts.ValueKind == JsonValueKind.Array)
        {
            foreach (var post in posts.EnumerateArray())
            {
                var content = "";
                if (post.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.Object)
                    content = GetString(description, "content");

                incident.Posts.Add(new ServiceIncidentPost
                {
                    CreatedDateTime = GetDate(post, "createdDateTime"),
                    PostType = GetString(post, "postType"),
                    ContentHtml = Sanitize(content)
                });
            }

            incident.Posts.Sort((a, b) =>
                (b.CreatedDateTime ?? DateTimeOffset.MinValue).CompareTo(a.CreatedDateTime ?? DateTimeOffset.MinValue));
        }

        return incident;
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? ""
            : "";

    private static DateTimeOffset? GetDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(prop.GetString(), out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Turns a Graph status token into something an L1 can read at a glance:
    /// "serviceOperational" becomes "Service Operational".
    /// </summary>
    internal static string HumanizeStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "";

        var sb = new StringBuilder(status.Length + 8);
        for (var i = 0; i < status.Length; i++)
        {
            var c = status[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(status[i - 1]))
                sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }

        return sb.ToString();
    }

    // ---- HTML sanitizing -------------------------------------------------------------------
    //
    // Microsoft ships incident text as HTML and the formatting is load-bearing: L1/L2 forward
    // this to executives and it has to read at a glance (plan section 1). So it is rendered as
    // markup, and this is the module's one genuinely risky surface. The allow-list below is the
    // whole trust boundary: sanitizing happens here in the service, never in the page, so the
    // page cannot later be edited into rendering a raw Graph field.

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();

        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[]
        {
            "p", "br", "b", "strong", "i", "em", "u", "ul", "ol", "li", "a", "span", "div",
            "h1", "h2", "h3", "h4", "h5", "h6",
            "table", "thead", "tbody", "tr", "th", "td",
            "code", "pre", "blockquote"
        })
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in new[] { "href", "title", "colspan", "rowspan" })
        {
            sanitizer.AllowedAttributes.Add(attribute);
        }

        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");

        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowDataAttributes = false;

        // A Microsoft KB link must not navigate the admin console away from itself.
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Html.Dom.IHtmlAnchorElement anchor)
            {
                anchor.SetAttribute("target", "_blank");
                anchor.SetAttribute("rel", "noopener noreferrer");
            }
        };

        return sanitizer;
    }

    /// <summary>
    /// Allow-list sanitize of third-party HTML. Everything not named in
    /// <see cref="CreateSanitizer"/> is dropped, including every event-handler attribute, style,
    /// script, iframe, object, embed, and any javascript: or data: URL.
    /// </summary>
    internal static string Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? "" : Sanitizer.Sanitize(html);

    // ---- Name maps, ported verbatim from the source app ------------------------------------
    //
    // Both maps are cosmetic. An unmapped value falls back to what Graph returned rather than
    // being hidden, so a service Microsoft adds tomorrow still appears.

    /// <summary>Service id to friendly name (app.py:208-224).</summary>
    internal static readonly Dictionary<string, string> ServiceDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Exchange"] = "Exchange Online",
        ["OrgLiveID"] = "Microsoft Entra",
        ["OSDPPlatform"] = "Microsoft 365 suite",
        ["SharePoint"] = "SharePoint Online",
        ["Teams"] = "Microsoft Teams",
        ["Skype"] = "Skype for Business",
        ["OneDrive"] = "OneDrive for Business",
        ["Yammer"] = "Yammer",
        ["PowerApps"] = "Power Apps",
        ["PowerAutomate"] = "Power Automate",
        ["PowerBI"] = "Power BI",
        ["Intune"] = "Microsoft Intune",
        ["Planner"] = "Microsoft Planner",
        ["Stream"] = "Microsoft Stream",
        ["Forms"] = "Microsoft Forms"
    };

    /// <summary>Issue service name back to the health service id (app.py:344-362), so an
    /// incident can be tied to the service card it belongs to.</summary>
    internal static readonly Dictionary<string, string> IssueServiceToId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Exchange Online"] = "Exchange",
        ["Microsoft Teams"] = "microsoftteams",
        ["SharePoint Online"] = "SharePoint",
        ["OneDrive for Business"] = "OneDriveForBusiness",
        ["Microsoft 365 suite"] = "OSDPPlatform",
        ["Microsoft 365 for the web"] = "officeonline",
        ["Microsoft 365 apps"] = "O365Client",
        ["Power BI"] = "PowerBIcom",
        ["Microsoft Defender XDR"] = "Microsoft365Defender",
        ["Microsoft Copilot (Microsoft 365)"] = "Copilot",
        ["Microsoft Viva"] = "Viva",
        ["Microsoft Entra"] = "OrgLiveID",
        ["Microsoft Intune"] = "Intune",
        ["Microsoft Planner"] = "Planner",
        ["Microsoft Forms"] = "Forms",
        ["Power Apps"] = "PowerApps",
        ["Power Automate"] = "MicrosoftFlow"
    };
}

public sealed class ServiceHealthStatus
{
    public List<ServiceHealthEntry> Services { get; init; } = [];
    public List<ServiceIncident> Issues { get; init; } = [];
    public DateTime LastUpdatedUtc { get; init; }

    public int TotalServices => Services.Count;
    public int HealthyServices => Services.Count(s => s.IsHealthy);
    public int DegradedServices => Services.Count(s => !s.IsHealthy);
    public int ActiveIssues => Issues.Count;
}

public sealed class ServiceHealthEntry
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Status { get; init; } = "";
    public string StatusText { get; init; } = "";

    /// <summary>
    /// Only serviceOperational counts as healthy. Every other Graph status - investigating,
    /// serviceDegradation, extendedRecovery and the rest - is something an operator needs to see,
    /// so an unrecognised future status reads as not-healthy rather than being quietly counted
    /// as green.
    /// </summary>
    public bool IsHealthy => string.Equals(Status, "serviceOperational", StringComparison.OrdinalIgnoreCase);
}

public sealed class ServiceIncident
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string ServiceId { get; init; } = "";
    public string ServiceName { get; init; } = "";
    public string Classification { get; init; } = "";
    public string Status { get; init; } = "";
    public string StatusText { get; init; } = "";
    public string Feature { get; init; } = "";
    public DateTimeOffset? StartDateTime { get; init; }
    public DateTimeOffset? LastModifiedDateTime { get; init; }

    /// <summary>Sanitized HTML. Safe to render with MarkupString; never assign a raw Graph
    /// string to this.</summary>
    public string ImpactDescriptionHtml { get; init; } = "";

    public List<ServiceIncidentPost> Posts { get; init; } = [];

    public bool IsIncident => string.Equals(Classification, "incident", StringComparison.OrdinalIgnoreCase);
}

public sealed class ServiceIncidentPost
{
    public DateTimeOffset? CreatedDateTime { get; init; }
    public string PostType { get; init; } = "";

    /// <summary>Sanitized HTML. Safe to render with MarkupString; never assign a raw Graph
    /// string to this.</summary>
    public string ContentHtml { get; init; } = "";
}
