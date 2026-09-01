using System.Net;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Read- and write-path tests for RiskyUsersService (docs/RiskyUsersModule-Plan.md, S2/S4/S5),
/// driven through the internal Func&lt;Task&lt;GraphTokenClient?&gt;&gt; seam
/// (RiskyUsersService.cs:34-37) against a locally declared HTTP stub.
/// GraphTokenClientTests.StubHandler is private sealed to that class and is not reachable here
/// (plan S2, "the HTTP stub must be declared locally").
/// </summary>
public class RiskyUsersServiceTests
{
    /// <summary>
    /// Serves a canned token for login.microsoftonline.com and a configurable response for every
    /// Graph call. Records every non-token Graph request (URI and body) so tests can assert on
    /// the emitted query string (e.g. $top, $filter) or the posted body (e.g. one userIds array
    /// per call, S5) without borrowing GraphTokenClientTests' handler. GraphResponseForBody, when
    /// set, lets a test vary the response per call based on the posted body - e.g. to prove one
    /// user's refusal does not change another user's outcome (Known Failure Class 2).
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> GraphResponse { get; set; } =
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"value":[]}""") };

        public Func<string?, HttpResponseMessage>? GraphResponseForBody { get; set; }

        public Uri? LastGraphRequestUri { get; private set; }

        public List<(Uri Uri, string? Body)> GraphRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"test-token","expires_in":3600}""")
                };
            }

            LastGraphRequestUri = request.RequestUri;
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            GraphRequests.Add((request.RequestUri, body));

            return GraphResponseForBody != null ? GraphResponseForBody(body) : GraphResponse();
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

    // Write path (S5). One user per HTTP call: every action posts a single-element userIds
    // array to its own endpoint, never a batch.
    [Theory]
    [InlineData(RiskyUserAction.Dismiss, "/identityProtection/riskyUsers/dismiss")]
    [InlineData(RiskyUserAction.ConfirmSafe, "/identityProtection/riskyUsers/confirmSafe")]
    [InlineData(RiskyUserAction.ConfirmCompromised, "/identityProtection/riskyUsers/confirmCompromised")]
    public async Task ApplyActionAsync_PostsOneUserIdToTheActionsOwnEndpoint(RiskyUserAction action, string expectedPath)
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.NoContent);

        var result = await service.ApplyActionAsync("user-1", action);

        Assert.True(result.Success);
        Assert.Equal("user-1", result.UserId);
        var (uri, body) = Assert.Single(handler.GraphRequests);
        Assert.Equal($"https://graph.microsoft.com/v1.0{expectedPath}", uri.ToString());
        using var doc = System.Text.Json.JsonDocument.Parse(body!);
        var ids = doc.RootElement.GetProperty("userIds").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "user-1" }, ids);
    }

    [Fact]
    public async Task ApplyActionAsync_GraphRejects_ReturnsNamedFailure_DoesNotThrow()
    {
        var (service, handler) = CreateService();
        handler.GraphResponse = () => new HttpResponseMessage(HttpStatusCode.BadRequest);

        var result = await service.ApplyActionAsync("user-1", RiskyUserAction.Dismiss);

        Assert.False(result.Success);
        Assert.Equal("user-1", result.UserId);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task ApplyActionAsync_GraphClientUnavailable_ThrowsRatherThanSilentlySucceeding()
    {
        var service = new RiskyUsersService(() => Task.FromResult<GraphTokenClient?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyActionAsync("user-1", RiskyUserAction.Dismiss));
    }

    // Known Failure Class 2 (aggregation): a refusal on one user's call must not flip another
    // user's result to failed, and must not itself be reported as success.
    [Fact]
    public async Task ApplyActionAsync_CalledForThreeUsersInTurn_EachGetsItsOwnOutcome()
    {
        var (service, handler) = CreateService();
        handler.GraphResponseForBody = body =>
            body != null && body.Contains("user-2")
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : new HttpResponseMessage(HttpStatusCode.NoContent);

        var r1 = await service.ApplyActionAsync("user-1", RiskyUserAction.Dismiss);
        var r2 = await service.ApplyActionAsync("user-2", RiskyUserAction.Dismiss);
        var r3 = await service.ApplyActionAsync("user-3", RiskyUserAction.Dismiss);

        Assert.True(r1.Success);
        Assert.False(r2.Success);
        Assert.True(r3.Success);
        Assert.Equal(3, handler.GraphRequests.Count);
    }
}
