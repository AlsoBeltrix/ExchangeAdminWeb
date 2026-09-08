using System.Net;
using System.Text.Json;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Read-path, cache and sanitizer tests for ServiceHealthService (docs/ServiceHealth-Plan.md),
/// driven through the internal Func&lt;Task&lt;GraphTokenClient?&gt;&gt; seam against a locally
/// declared HTTP stub. GraphTokenClientTests.StubHandler is private sealed to that class and is
/// not reachable here, so this file declares its own, matching RiskyUsersServiceTests.
/// </summary>
public class ServiceHealthServiceTests
{
    /// <summary>
    /// Serves a canned token for login.microsoftonline.com and routes Graph calls by path, so a
    /// test can fail the health overview while the issues call succeeds (and the reverse) and
    /// count how many times each endpoint was actually hit - which is how the cache assertions
    /// are made without waiting out the TTL.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> HealthResponse { get; set; } =
            () => Json("""{"value":[]}""");

        public Func<HttpResponseMessage> IssuesResponse { get; set; } =
            () => Json("""{"value":[]}""");

        public int HealthRequests { get; private set; }
        public int IssuesRequests { get; private set; }
        public List<Uri> GraphRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Task.FromResult(Json("""{"access_token":"test-token","expires_in":3600}"""));

            GraphRequests.Add(request.RequestUri);

            if (request.RequestUri.AbsolutePath.Contains("healthOverviews", StringComparison.OrdinalIgnoreCase))
            {
                HealthRequests++;
                return Task.FromResult(HealthResponse());
            }

            IssuesRequests++;
            return Task.FromResult(IssuesResponse());
        }

        public static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private static (ServiceHealthService Service, StubHandler Handler) CreateService()
    {
        var handler = new StubHandler();
        var client = new GraphTokenClient("tenant", "client", "secret", new HttpClient(handler));
        var service = new ServiceHealthService(() => Task.FromResult<GraphTokenClient?>(client));
        return (service, handler);
    }

    // ---- Services (AC1, AC3) ---------------------------------------------------------------

    [Fact]
    public async Task GetStatus_MapsKnownServiceIdsToFriendlyNames()
    {
        var (service, handler) = CreateService();
        handler.HealthResponse = () => StubHandler.Json("""
            {"value":[{"id":"Exchange","service":"Exchange Online","status":"serviceOperational"}]}
            """);

        var status = await service.GetStatusAsync();

        var entry = Assert.Single(status.Services);
        Assert.Equal("Exchange Online", entry.DisplayName);
    }

    [Fact]
    public async Task GetStatus_UnknownServiceIdFallsBackToGraphNameThenId()
    {
        // A service Microsoft adds after this map was written must still appear, not vanish.
        var (service, handler) = CreateService();
        handler.HealthResponse = () => StubHandler.Json("""
            {"value":[
                {"id":"SomeNewThing","service":"Some New Thing","status":"serviceOperational"},
                {"id":"NamelessThing","status":"serviceOperational"}
            ]}
            """);

        var status = await service.GetStatusAsync();

        Assert.Contains(status.Services, s => s.DisplayName == "Some New Thing");
        Assert.Contains(status.Services, s => s.DisplayName == "NamelessThing");
    }

    [Theory]
    [InlineData("serviceOperational", true)]
    [InlineData("serviceDegradation", false)]
    [InlineData("investigating", false)]
    [InlineData("extendedRecovery", false)]
    [InlineData("someStatusMicrosoftAddsLater", false)]
    public async Task GetStatus_OnlyServiceOperationalCountsAsHealthy(string graphStatus, bool expectedHealthy)
    {
        var (service, handler) = CreateService();
        handler.HealthResponse = () => StubHandler.Json($$"""
            {"value":[{"id":"Exchange","service":"Exchange Online","status":"{{graphStatus}}"}]}
            """);

        var status = await service.GetStatusAsync();

        Assert.Equal(expectedHealthy, Assert.Single(status.Services).IsHealthy);
        Assert.Equal(expectedHealthy ? 1 : 0, status.HealthyServices);
        Assert.Equal(expectedHealthy ? 0 : 1, status.DegradedServices);
    }

    [Fact]
    public async Task GetStatus_CountsSummariseServicesAndIssues()
    {
        var (service, handler) = CreateService();
        handler.HealthResponse = () => StubHandler.Json("""
            {"value":[
                {"id":"Exchange","status":"serviceOperational"},
                {"id":"Teams","status":"serviceDegradation"}
            ]}
            """);
        handler.IssuesResponse = () => StubHandler.Json("""
            {"value":[{"id":"EX1","title":"Mail delays","service":"Exchange Online","classification":"incident"}]}
            """);

        var status = await service.GetStatusAsync();

        Assert.Equal(2, status.TotalServices);
        Assert.Equal(1, status.HealthyServices);
        Assert.Equal(1, status.DegradedServices);
        Assert.Equal(1, status.ActiveIssues);
    }

    // ---- Issues and timeline (AC2, AC11) ---------------------------------------------------

    [Fact]
    public async Task GetStatus_RequestsOnlyUnresolvedIssues()
    {
        var (service, handler) = CreateService();

        await service.GetStatusAsync();

        var issuesUri = Assert.Single(handler.GraphRequests, u => u.AbsolutePath.Contains("issues"));
        Assert.Contains("isResolved", Uri.UnescapeDataString(issuesUri.Query));
        Assert.Contains("eq false", Uri.UnescapeDataString(issuesUri.Query));
    }

    [Fact]
    public async Task GetStatus_SortsIssuesNewestFirst()
    {
        var (service, handler) = CreateService();
        handler.IssuesResponse = () => StubHandler.Json("""
            {"value":[
                {"id":"OLD","startDateTime":"2026-09-01T00:00:00Z"},
                {"id":"NEW","startDateTime":"2026-09-07T00:00:00Z"},
                {"id":"MID","startDateTime":"2026-09-04T00:00:00Z"}
            ]}
            """);

        var status = await service.GetStatusAsync();

        Assert.Equal(["NEW", "MID", "OLD"], status.Issues.Select(i => i.Id));
    }

    [Fact]
    public async Task GetStatus_ReadsPostTimelineFromTheIssuesResponse()
    {
        // The posts arrive inline on the list response; the module makes no per-issue call.
        var (service, handler) = CreateService();
        handler.IssuesResponse = () => StubHandler.Json("""
            {"value":[{"id":"EX1","posts":[
                {"createdDateTime":"2026-09-01T10:00:00Z","postType":"regular","description":{"contentType":"html","content":"<p>First</p>"}},
                {"createdDateTime":"2026-09-03T10:00:00Z","postType":"regular","description":{"contentType":"html","content":"<p>Latest</p>"}}
            ]}]}
            """);

        var status = await service.GetStatusAsync();

        var issue = Assert.Single(status.Issues);
        Assert.Equal(2, issue.Posts.Count);
        Assert.Contains("Latest", issue.Posts[0].ContentHtml);
        Assert.Contains("First", issue.Posts[1].ContentHtml);
        Assert.Equal(1, handler.IssuesRequests);
    }

    [Fact]
    public async Task GetStatus_IssueWithoutPostsHasEmptyTimeline()
    {
        var (service, handler) = CreateService();
        handler.IssuesResponse = () => StubHandler.Json("""{"value":[{"id":"EX1","title":"No updates yet"}]}""");

        var status = await service.GetStatusAsync();

        Assert.Empty(Assert.Single(status.Issues).Posts);
    }

    [Fact]
    public async Task GetStatus_MapsIssueServiceNameToHealthServiceId()
    {
        var (service, handler) = CreateService();
        handler.IssuesResponse = () => StubHandler.Json("""
            {"value":[{"id":"EX1","service":"Microsoft Teams"}]}
            """);

        var status = await service.GetStatusAsync();

        Assert.Equal("microsoftteams", Assert.Single(status.Issues).ServiceId);
    }

    // ---- Failure surfaces (AC6) ------------------------------------------------------------

    [Fact]
    public async Task GetStatus_ThrowsWhenHealthOverviewsFails()
    {
        // Never render "all healthy" off a failed request.
        var (service, handler) = CreateService();
        handler.HealthResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetStatusAsync());
        Assert.Contains("service health", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStatus_ThrowsWhenIssuesFails()
    {
        // Never render "no open incidents" off a failed request.
        var (service, handler) = CreateService();
        handler.IssuesResponse = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetStatusAsync());
        Assert.Contains("incidents", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStatus_ThrowsWhenNoGraphClientIsAvailable()
    {
        var service = new ServiceHealthService(() => Task.FromResult<GraphTokenClient?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetStatusAsync());
    }

    [Fact]
    public void IsAvailable_IsFalseWithoutAConfiguredSecretId()
    {
        // AC5: unconfigured must say so. The seam constructor carries no ModuleConfigService,
        // which is the same answer the DI path gives for a missing, blank or non-numeric value.
        var service = new ServiceHealthService(() => Task.FromResult<GraphTokenClient?>(null));

        Assert.False(service.IsAvailable);
    }

    // ---- Cache (AC7) -----------------------------------------------------------------------

    [Fact]
    public async Task GetStatus_SecondCallInsideTtlDoesNotHitGraph()
    {
        var (service, handler) = CreateService();

        await service.GetStatusAsync();
        await service.GetStatusAsync();

        Assert.Equal(1, handler.HealthRequests);
        Assert.Equal(1, handler.IssuesRequests);
    }

    [Fact]
    public async Task GetStatus_ForceRefreshBypassesTheCache()
    {
        var (service, handler) = CreateService();

        await service.GetStatusAsync();
        await service.GetStatusAsync(forceRefresh: true);

        Assert.Equal(2, handler.HealthRequests);
        Assert.Equal(2, handler.IssuesRequests);
    }

    [Fact]
    public async Task GetStatus_FailedFetchIsNotCachedAsSuccess()
    {
        var (service, handler) = CreateService();
        handler.HealthResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetStatusAsync());

        handler.HealthResponse = () => StubHandler.Json("""{"value":[{"id":"Exchange","status":"serviceOperational"}]}""");
        var status = await service.GetStatusAsync();

        Assert.Single(status.Services);
    }

    // ---- Sanitizer (AC12, AC13) ------------------------------------------------------------

    [Fact]
    public void Sanitize_PreservesMicrosoftFormatting()
    {
        // AC12: this text is forwarded to executives. Formatting is the feature, not decoration.
        const string input = "<p>Users may see <strong>delays</strong>.</p><ul><li>Item one</li></ul><p>See <a href=\"https://admin.microsoft.com\">the portal</a>.</p>";

        var result = ServiceHealthService.Sanitize(input);

        Assert.Contains("<p>", result);
        Assert.Contains("<strong>delays</strong>", result);
        Assert.Contains("<li>Item one</li>", result);
        Assert.Contains("https://admin.microsoft.com", result);
    }

    [Fact]
    public void Sanitize_AddsSafeTargetAndRelToLinks()
    {
        var result = ServiceHealthService.Sanitize("<a href=\"https://example.com\">x</a>");

        Assert.Contains("target=\"_blank\"", result);
        Assert.Contains("noopener", result);
        Assert.Contains("noreferrer", result);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "alert")]
    [InlineData("<img src=x onerror=\"alert(1)\">", "onerror")]
    [InlineData("<p onclick=\"alert(1)\">hi</p>", "onclick")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD4=\">x</a>", "data:")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>", "iframe")]
    [InlineData("<object data=\"https://evil.example\"></object>", "object")]
    [InlineData("<embed src=\"https://evil.example\">", "embed")]
    [InlineData("<style>body{display:none}</style>", "display:none")]
    [InlineData("<p style=\"position:fixed;top:0\">hi</p>", "position:fixed")]
    [InlineData("<svg onload=\"alert(1)\"></svg>", "onload")]
    [InlineData("<form action=\"https://evil.example\"><input name=\"p\"></form>", "<form")]
    public void Sanitize_StripsHostileMarkup(string input, string forbidden)
    {
        var result = ServiceHealthService.Sanitize(input);

        Assert.DoesNotContain(forbidden, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_NullOrBlankBecomesEmpty()
    {
        Assert.Equal("", ServiceHealthService.Sanitize(null));
        Assert.Equal("", ServiceHealthService.Sanitize("   "));
    }

    [Fact]
    public async Task GetStatus_SanitizesBothImpactDescriptionAndPosts()
    {
        // The boundary is the service, not the page: neither field may reach the page raw.
        var (service, handler) = CreateService();
        handler.IssuesResponse = () => StubHandler.Json("""
            {"value":[{
                "id":"EX1",
                "impactDescription":"<p>Impact<script>alert(1)</script></p>",
                "posts":[{"createdDateTime":"2026-09-01T10:00:00Z","description":{"contentType":"html","content":"<p>Update<script>alert(2)</script></p>"}}]
            }]}
            """);

        var status = await service.GetStatusAsync();

        var issue = Assert.Single(status.Issues);
        Assert.DoesNotContain("script", issue.ImpactDescriptionHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Impact", issue.ImpactDescriptionHtml);
        Assert.DoesNotContain("script", Assert.Single(issue.Posts).ContentHtml, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Presentation helpers --------------------------------------------------------------

    [Theory]
    [InlineData("serviceOperational", "Service Operational")]
    [InlineData("serviceDegradation", "Service Degradation")]
    [InlineData("postIncidentReviewPublished", "Post Incident Review Published")]
    [InlineData("", "")]
    public void HumanizeStatus_SplitsCamelCase(string input, string expected)
    {
        Assert.Equal(expected, ServiceHealthService.HumanizeStatus(input));
    }

    [Fact]
    public void ParseIncident_ToleratesAMissingDescriptionObject()
    {
        using var doc = JsonDocument.Parse("""
            {"id":"EX1","posts":[{"createdDateTime":"2026-09-01T10:00:00Z"}]}
            """);

        var incident = ServiceHealthService.ParseIncident(doc.RootElement);

        Assert.Equal("", Assert.Single(incident.Posts).ContentHtml);
    }
}
