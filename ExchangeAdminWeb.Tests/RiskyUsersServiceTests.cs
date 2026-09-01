using System.Net;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Read-path tests for RiskyUsersService (docs/RiskyUsersModule-Plan.md, S2/S4), driven through
/// the internal Func&lt;Task&lt;GraphTokenClient?&gt;&gt; seam (RiskyUsersService.cs:34-37) against a
/// locally declared HTTP stub. GraphTokenClientTests.StubHandler is private sealed to that class
/// and is not reachable here (plan S2, "the HTTP stub must be declared locally").
/// </summary>
public class RiskyUsersServiceTests
{
    /// <summary>
    /// Serves a canned token for login.microsoftonline.com and a configurable response for every
    /// Graph call, and records the last Graph request URI so tests can assert on the emitted
    /// query string (e.g. $top, $filter) without borrowing GraphTokenClientTests' handler.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> GraphResponse { get; set; } =
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"value":[]}""") };

        public Uri? LastGraphRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"test-token","expires_in":3600}""")
                });
            }

            LastGraphRequestUri = request.RequestUri;
            return Task.FromResult(GraphResponse());
        }
    }

    private static (RiskyUsersService Service, StubHandler Handler) CreateService()
    {
        var handler = new StubHandler();
        var client = new GraphTokenClient("tenant", "client", "secret", new HttpClient(handler));
        var service = new RiskyUsersService(() => Task.FromResult<GraphTokenClient?>(client));
        return (service, handler);
    }

    private static readonly RiskyUserFilter NoFilter = new(null, null, null);

    // Rule 1 (S2): a failed request must never render as "no risky users." 403/404/429/500 must
    // all throw, never collapse into an empty success.
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetRiskyUsersAsync_NonSuccessStatus_ThrowsRatherThanReturningEmpty(HttpStatusCode status)
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(status);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetRiskyUsersAsync(NoFilter));
    }

    [Fact]
    public async Task GetRiskyUsersAsync_Forbidden_NamesP2AndConsentAsLikelyCause()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetRiskyUsersAsync(NoFilter));

        Assert.Contains("P2", ex.Message);
        Assert.Contains("IdentityRiskyUser.Read.All", ex.Message);
    }

    // Inverse of the failure tests above: a genuinely empty result must still succeed, proving
    // the failure tests are not simply "everything fails."
    [Fact]
    public async Task GetRiskyUsersAsync_EmptyValueArray_ReturnsEmptySuccessNotFailure()
    {
        var (service, _) = CreateService();

        var page = await service.GetRiskyUsersAsync(NoFilter);

        Assert.Empty(page.Users);
        Assert.False(page.Truncated);
    }

    // Rule 2 (S2): truncation must be visible, since GraphTokenClient cannot follow @odata.nextLink.
    [Fact]
    public async Task GetRiskyUsersAsync_NextLinkPresent_SetsTruncatedTrue()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/identityProtection/riskyUsers?$skiptoken=abc"}""")
        };

        var page = await service.GetRiskyUsersAsync(NoFilter);

        Assert.True(page.Truncated);
    }

    [Fact]
    public async Task GetRiskyUsersAsync_NextLinkAbsent_SetsTruncatedFalse()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[{"id":"1","userPrincipalName":"a@b.com","riskLevel":"high"}]}""")
        };

        var page = await service.GetRiskyUsersAsync(NoFilter);

        Assert.False(page.Truncated);
        Assert.Single(page.Users);
    }

    // Rule 4 (S2): unknown enum values pass through unchanged, never dropped as unrecognised.
    [Fact]
    public async Task GetRiskyUsersAsync_UnknownRiskLevelValues_SurviveUnfiltered()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"value":[
                    {"id":"1","userPrincipalName":"a@b.com","riskLevel":"unknownFutureValue"},
                    {"id":"2","userPrincipalName":"c@d.com","riskLevel":"somethingGraphHasNotDocumentedYet"}
                ]}
                """)
        };

        var page = await service.GetRiskyUsersAsync(NoFilter);

        Assert.Equal(2, page.Users.Count);
        Assert.Contains(page.Users, u => u.RiskLevel == "unknownFutureValue");
        Assert.Contains(page.Users, u => u.RiskLevel == "somethingGraphHasNotDocumentedYet");
    }

    // Rule 3 (S2): UpnContains filters client-side and must never be emitted into $filter.
    [Fact]
    public async Task GetRiskyUsersAsync_UpnContains_FiltersClientSideAndIsNotEmittedInFilter()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"value":[
                    {"id":"1","userPrincipalName":"match@contoso.com","riskLevel":"high"},
                    {"id":"2","userPrincipalName":"other@contoso.com","riskLevel":"high"}
                ]}
                """)
        };

        var page = await service.GetRiskyUsersAsync(new RiskyUserFilter(null, null, "match@"));

        Assert.Single(page.Users);
        Assert.Equal("match@contoso.com", page.Users[0].UserPrincipalName);
        Assert.DoesNotContain("filter", handler.LastGraphRequestUri!.Query, StringComparison.OrdinalIgnoreCase);
    }

    // Rules 2/5 (S2): $top is always clamped to Graph's 500 cap; unparseable/non-positive falls
    // back to the same cap rather than an unbounded or zero-row request.
    [Theory]
    [InlineData("5000", 500)]
    [InlineData("0", 500)]
    [InlineData("not-a-number", 500)]
    [InlineData(null, 500)]
    [InlineData("50", 50)]
    [InlineData("1", 1)]
    public void ClampMaxRows_ClampsOrFallsBackTo500(string? rawMaxRows, int expected)
    {
        Assert.Equal(expected, RiskyUsersService.ClampMaxRows(rawMaxRows));
    }

    // Rule 3 (S2): server-side filter fields only, combined with "and".
    [Fact]
    public void BuildFilterExpression_RiskLevelAndRiskState_CombinedWithAnd()
    {
        var expression = RiskyUsersService.BuildFilterExpression(new RiskyUserFilter("high", "atRisk", "ignored@contoso.com"));

        Assert.Equal("riskLevel eq 'high' and riskState eq 'atRisk'", expression);
    }

    [Fact]
    public void BuildFilterExpression_UpnContainsOnly_ProducesNoFilter()
    {
        var expression = RiskyUsersService.BuildFilterExpression(new RiskyUserFilter(null, null, "contoso"));

        Assert.Null(expression);
    }

    // Rule 3 (S2): single quotes are doubled, never interpolated raw into the filter literal.
    [Fact]
    public void BuildFilterExpression_SingleQuoteInValue_IsDoubledNotInjectedRaw()
    {
        var expression = RiskyUsersService.BuildFilterExpression(new RiskyUserFilter("o'brien", null, null));

        Assert.Equal("riskLevel eq 'o''brien'", expression);
    }

    // Rule 5 (S2): deterministic client-side sort; unrecognised levels sort last without throwing.
    [Fact]
    public void SortRiskyUsers_OrdersBySeverityThenUnrecognisedLast()
    {
        var users = new List<RiskyUser>
        {
            new() { Id = "1", RiskLevel = "low" },
            new() { Id = "2", RiskLevel = "unrecognisedFutureValue" },
            new() { Id = "3", RiskLevel = "high" },
            new() { Id = "4", RiskLevel = "medium" }
        };

        var sorted = RiskyUsersService.SortRiskyUsers(users);

        Assert.Equal(new[] { "3", "4", "1", "2" }, sorted.Select(u => u.Id));
    }
}
