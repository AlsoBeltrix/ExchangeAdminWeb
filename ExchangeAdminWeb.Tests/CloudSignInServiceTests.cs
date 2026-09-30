using System.Net;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The Graph I/O behind the cloud half of True Last Logon (docs/TrueLastLogon-Plan.md S2).
/// Every test runs against a locally declared HTTP stub: this slice makes no live call and
/// cannot, because the module descriptor that would let anyone enter a Secret ID arrives in S3.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing tests here are the ones about an ABSENCE. The rules under test all reduce to
/// one sentence: a source that failed and a source that reported nothing are different facts, and
/// only the second one is evidence. Every test that asserts a <see cref="CloudVerification"/>
/// value is guarding that sentence at a different failure point.
/// </para>
/// <para>
/// The wire assertions are not ceremony either. The list form of the user query and the beta
/// endpoint for non-interactive sign-ins are both measurements - the single-user form returned no
/// activity for users who had it, and v1.0 carries interactive sign-ins only - so a refactor that
/// quietly "tidied" either one would silently remove the module's coverage of the exact sign-ins
/// signInActivity is known to miss.
/// </para>
/// </remarks>
public class CloudSignInServiceTests
{
    private const string Upn = "jane.doe@example.test";

    /// <summary>
    /// Serves a canned token for login.microsoftonline.com, then answers each Graph request from
    /// whichever per-route responder the test set, recording URLs exactly as they went on the
    /// wire. Token requests are deliberately not recorded: every URL assertion here counts Graph
    /// calls, and a recorded token request would shift each of those counts.
    /// </summary>
    private sealed class GraphStub : HttpMessageHandler
    {
        public List<string> RequestUrls { get; } = [];

        public Func<HttpResponseMessage> Activity { get; set; } =
            () => Json(HttpStatusCode.OK, NoUserMatched);

        public Func<HttpResponseMessage> Interactive { get; set; } =
            () => Json(HttpStatusCode.OK, NoRows);

        public Func<HttpResponseMessage> NonInteractive { get; set; } =
            () => Json(HttpStatusCode.OK, NoRows);

        /// <summary>
        /// Holds the interactive log query open past the HttpClient's own timeout, so the timeout
        /// test exercises a REAL client-side cancellation rather than a hand-made result.
        /// </summary>
        public TimeSpan InteractiveDelay { get; set; } = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;

            if (uri.Host == "login.microsoftonline.com")
                return Json(HttpStatusCode.OK, """{"access_token":"test-token","expires_in":3600}""");

            var url = uri.OriginalString;
            RequestUrls.Add(url);

            if (!url.Contains("/auditLogs/signIns", StringComparison.Ordinal))
                return Activity();

            if (url.Contains("/beta/", StringComparison.Ordinal))
                return NonInteractive();

            if (InteractiveDelay > TimeSpan.Zero)
                await Task.Delay(InteractiveDelay, cancellationToken);

            return Interactive();
        }

        public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body) };
    }

    private static (CloudSignInService Service, GraphStub Stub) CreateService(TimeSpan? httpTimeout = null)
    {
        var stub = new GraphStub();
        var http = new HttpClient(stub);
        if (httpTimeout != null)
            http.Timeout = httpTimeout.Value;

        var service = new CloudSignInService(baseUrl => Task.FromResult<DefenderApiClient?>(
            new DefenderApiClient(
                "tenant", "client", "secret", baseUrl, CloudSignInService.GraphTokenScope, http)));

        return (service, stub);
    }

    private const string NoUserMatched = """{"value":[]}""";
    private const string NoRows = """{"value":[]}""";

    /// <summary>The account exists and Graph consulted it, but it has no recorded activity.</summary>
    private const string UserWithNoActivityProperty =
        """{"value":[{"userPrincipalName":"jane.doe@example.test"}]}""";

    private static string ActivityBody(string? interactive, string? nonInteractive)
    {
        var fields = new List<string>();
        if (interactive != null)
            fields.Add("\"lastSignInDateTime\":\"" + interactive + "\"");
        if (nonInteractive != null)
            fields.Add("\"lastNonInteractiveSignInDateTime\":\"" + nonInteractive + "\"");

        return "{\"value\":[{\"userPrincipalName\":\"jane.doe@example.test\",\"signInActivity\":{"
            + string.Join(",", fields) + "}}]}";
    }

    private static string LogBody(
        string createdDateTime,
        string ip = "203.0.113.9",
        string app = "Outlook",
        string clientApp = "Browser",
        string resource = "Office 365 Exchange Online",
        string caStatus = "success") =>
        "{\"value\":[{"
        + "\"createdDateTime\":\"" + createdDateTime + "\","
        + "\"ipAddress\":\"" + ip + "\","
        + "\"appDisplayName\":\"" + app + "\","
        + "\"clientAppUsed\":\"" + clientApp + "\","
        + "\"resourceDisplayName\":\"" + resource + "\","
        + "\"location\":{\"city\":\"Boston\",\"state\":\"MA\",\"countryOrRegion\":\"US\"},"
        + "\"conditionalAccessStatus\":\"" + caStatus + "\"}]}";

    private static HttpResponseMessage Forbidden() =>
        GraphStub.Json(
            HttpStatusCode.Forbidden,
            "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges.\"}}");

    // ---- Query shape: the two measurements that must not be refactored away -------------------

    [Fact]
    public void TheActivityQueryUsesTheUsersCollectionFormNotTheSingleUserForm()
    {
        // The source script records that /users/{id}?$select=signInActivity returned no activity
        // for users who demonstrably had it, while the collection form with an eq filter works.
        // A "simplification" to the single-user form would read as dormancy, not as an error.
        var query = CloudSignInService.ActivityQuery(Upn);

        Assert.StartsWith("/users?$filter=", query, StringComparison.Ordinal);
        Assert.Contains(
            Uri.EscapeDataString("userPrincipalName eq 'jane.doe@example.test'"),
            query,
            StringComparison.Ordinal);
        Assert.Contains("$select=userPrincipalName,signInActivity", query, StringComparison.Ordinal);
        Assert.DoesNotContain("/users/" + Upn, query, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheNonInteractiveLogQueryCarriesTheSignInEventTypesFilter()
    {
        var interactive = CloudSignInService.LogQuery(Upn, nonInteractive: false);
        var nonInteractive = CloudSignInService.LogQuery(Upn, nonInteractive: true);

        Assert.DoesNotContain("signInEventTypes", interactive, StringComparison.Ordinal);
        Assert.Contains(
            Uri.EscapeDataString("signInEventTypes/any(t: t eq 'nonInteractiveUser')"),
            nonInteractive,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BothLogQueriesAskForTheNewestSingleRow()
    {
        foreach (var query in new[]
                 {
                     CloudSignInService.LogQuery(Upn, nonInteractive: false),
                     CloudSignInService.LogQuery(Upn, nonInteractive: true),
                 })
        {
            Assert.Contains("$top=1", query, StringComparison.Ordinal);
            Assert.Contains(Uri.EscapeDataString("createdDateTime desc"), query, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheUpnFilterLowercasesAndDoublesSingleQuotes()
    {
        // O'Brien is a real surname and an OData string terminator. Doubling is the escape.
        Assert.Equal(
            "userPrincipalName eq 'o''brien@example.test'",
            CloudSignInService.UpnFilter("  O'Brien@Example.Test  "));
    }

    [Fact]
    public void AGuestUpnsHashIsPercentEncodedRatherThanTruncatingTheQuery()
    {
        // An unescaped '#' starts a fragment, so the filter would reach Graph cut in half and the
        // guest would come back as "no account matched" - which reads as dormancy.
        var query = CloudSignInService.ActivityQuery("guest_contoso.com#EXT#@example.test");

        Assert.DoesNotContain("#", query, StringComparison.Ordinal);
        Assert.Contains("%23ext%23", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNonInteractiveQueryGoesToBetaAndTheOtherTwoGoToV1()
    {
        var (service, stub) = CreateService();
        stub.Activity = () => GraphStub.Json(HttpStatusCode.OK, UserWithNoActivityProperty);

        await service.GetCloudSignInAsync(Upn);

        var users = Assert.Single(stub.RequestUrls, u => u.Contains("/users?", StringComparison.Ordinal));
        var logs = stub.RequestUrls.Where(u => u.Contains("/auditLogs/signIns", StringComparison.Ordinal)).ToList();

        Assert.StartsWith(CloudSignInService.GraphV1BaseUrl, users, StringComparison.Ordinal);
        Assert.Equal(2, logs.Count);
        Assert.Equal(1, logs.Count(u => u.StartsWith(CloudSignInService.GraphV1BaseUrl + "/auditLogs", StringComparison.Ordinal)));
        Assert.Equal(1, logs.Count(u => u.StartsWith(CloudSignInService.GraphBetaBaseUrl + "/auditLogs", StringComparison.Ordinal)));
    }

    // ---- The answer, and how much it can be trusted -------------------------------------------

    [Fact]
    public async Task WhenBothSourcesAnswerTheResultIsLogVerifiedAndTakesTheLaterDate()
    {
        var (service, stub) = CreateService();
        stub.Activity = () => GraphStub.Json(
            HttpStatusCode.OK, ActivityBody("2026-09-01T10:00:00Z", "2026-09-02T10:00:00Z"));
        stub.Interactive = () => GraphStub.Json(HttpStatusCode.OK, LogBody("2026-09-20T08:00:00Z"));
        stub.NonInteractive = () => GraphStub.Json(HttpStatusCode.OK, LogBody("2026-09-21T09:00:00Z"));

        var lookup = await service.GetCloudSignInAsync(Upn);

        Assert.Equal(CloudVerification.LogVerified, lookup.Result.Verified);
        Assert.Equal(new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc), lookup.Result.Interactive);
        Assert.Equal(new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc), lookup.Result.NonInteractive);
        Assert.Null(lookup.ActivityError);
        Assert.Null(lookup.LogError);
    }

    [Fact]
    public async Task AnAccountWithNoActivityPropertyStillCountsAsSignInActivityHavingAnswered()
    {
        // The user object came back, so the source WAS consulted. An absent property legitimately
        // means "no recorded activity" - which is the only reading that lets both sources agree.
        var (service, stub) = CreateService();
        stub.Activity = () => GraphStub.Json(HttpStatusCode.OK, UserWithNoActivityProperty);

        var lookup = await service.GetCloudSignInAsync(Upn);

        Assert.Equal(CloudVerification.LogVerified, lookup.Result.Verified);
        Assert.True(lookup.Result.NoSignInReported);
        Assert.Null(lookup.Result.Latest);
    }

    [Fact]
    public async Task ALostNonInteractiveQueryIsNotLogVerifiedEvenThoughTheInteractiveOneAnswered()
    {
        // THE test of this file. The non-interactive log is the only source that catches what
        // signInActivity under-reports, so a run that lost it has not verified anything. Relaxing
        // the log's Answered rule to "either query" makes this account read as confirmed dormant.
        var (service, stub) = CreateService();
        stub.Activity = () => GraphStub.Json(HttpStatusCode.OK, UserWithNoActivityProperty);
        stub.NonInteractive = Forbidden;

        var lookup = await service.GetCloudSignInAsync(Upn);

        Assert.Equal(CloudVerification.ActivityOnly, lookup.Result.Verified);
        Assert.NotNull(lookup.LogError);
        Assert.Contains(CloudSignInService.LogPermissions, lookup.LogError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADateFromAPartiallyLostLogPassIsStillReported()
    {
        // Degrading the trust level must not throw away a date that did arrive: the interactive
        // sign-in happened whether or not the other query answered.
        var (service, stub) = CreateService();
        stub.Activity = () => GraphStub.Json(HttpStatusCode.OK, UserWithNoActivityProperty);
        stub.Interactive = () => GraphStub.Json(HttpStatusCode.OK, LogBody("2026-09-18T07:30:00Z"));
        stub.NonInteractive = () => GraphStub.Json(
            HttpStatusCode.ServiceUnavailable, """{"error":{"code":"UnknownError"}}""");

        var lookup = await service.GetCloudSignInAsync(Upn);

        Assert.Equal(new DateTime(2026, 9, 18, 7, 30, 0, DateTimeKind.Utc), lookup.Result.Interactive);
        Assert.Equal(CloudVerification.ActivityOnly, lookup.Result.Verified);
        Assert.False(lookup.Result.NoSignInReported);
    }

    [Fact]
    public async Task A403OnSignInActivityNamesBothPermissionsAndDropsToLogOnly()
    {
        // A missing permission rendering as "never signed in" is the worst failure this module
        // can have, so the 403 has to reach the caller as a named cause.
        var (service, stub) = CreateService();
        stub.Activity = Forbidden;

        var lookup = await service.GetCloudSignInAsync(Upn);

        Assert.Equal(CloudVerification.LogOnly30d, lookup.Result.Verified);
        Assert.NotNull(lookup.ActivityError);
        Assert.Contains(CloudSignInService.ActivityPermissions, lookup.ActivityError, StringComparison.Ordinal);
        Assert.Contains("Insufficient privileges.", lookup.ActivityError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenNoSourceAnswersTheResultIsUnverifiedAndReportsNoAbsence()
    {
        var (service, stub) = CreateService();
        stub.Activity = Forbidden;
        stub.Interactive = Forbidden;
        stub.NonInteractive = Forbidden;

        var lookup = await service.GetCloudSignInAsync(Upn);

        Assert.Equal(CloudVerification.Unverified, lookup.Result.Verified);
        Assert.False(lookup.Result.NoSignInReported);
        Assert.NotNull(lookup.ActivityError);
        Assert.NotNull(lookup.LogError);
    }

    [Fact]
    public async Task AnAccountNoQueryMatchedIsNotAnAccountThatNeverSignedIn()
    {
        var (service, stub) = CreateService();
        stub.Activity = () => GraphStub.Json(HttpStatusCode.OK, NoUserMatched);

        var lookup = await service.GetCloudSignInAsync(Upn);

        Assert.Equal(CloudVerification.LogOnly30d, lookup.Result.Verified);
        Assert.NotNull(lookup.ActivityError);
        Assert.Contains("not evidence", lookup.ActivityError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALogRowWithAnUnreadableDateIsAFailedCheckNotAnEmptyOne()
    {
        // A row exists, so there IS a sign-in. Counting the query as answered-with-nothing would
        // manufacture the dormancy the module exists to disprove.
        var (service, stub) = CreateService();
        stub.Activity = () => GraphStub.Json(HttpStatusCode.OK, UserWithNoActivityProperty);
        stub.Interactive = () => GraphStub.Json(
            HttpStatusCode.OK, """{"value":[{"createdDateTime":"not-a-date"}]}""");

        var lookup = await service.GetCloudSignInAsync(Upn);

        Assert.Equal(CloudVerification.ActivityOnly, lookup.Result.Verified);
        Assert.NotNull(lookup.LogError);
        Assert.Contains("no readable date", lookup.LogError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableSignInActivityDateDiscardsThatSourceRatherThanReadingAsNull()
    {
        var (service, stub) = CreateService();
        stub.Activity = () => GraphStub.Json(HttpStatusCode.OK, ActivityBody("yesterday-ish", null));

        var lookup = await service.GetCloudSignInAsync(Upn);

        Assert.Equal(CloudVerification.LogOnly30d, lookup.Result.Verified);
        Assert.NotNull(lookup.ActivityError);
    }

    // ---- tll-1: three different facts must not share one answer -------------------------------

    [Fact]
    public async Task ASignInActivityInAShapeWeCannotReadIsNotAVerifiedAbsence()
    {
        // tll-1. The dangerous composition: an unparseable activity value plus two empty log
        // queries. Folding the unreadable shape in with "absent" reports LogVerified and
        // NoSignInReported - the strongest claim the module can make, from a body it could not
        // parse, and the one state the plan calls safe to act on.
        var (service, stub) = CreateService();
        stub.Activity = () => GraphStub.Json(
            HttpStatusCode.OK, """{"value":[{"userPrincipalName":"jane.doe@example.test","signInActivity":"bad"}]}""");

        var lookup = await service.GetCloudSignInAsync(Upn);

        // The log still answered, so LogOnly30d with a reported absence is the honest result -
        // "nothing in the retained window", bounded. What must not survive is LogVerified, which
        // would claim both sources agreed when one of them was never read.
        Assert.NotEqual(CloudVerification.LogVerified, lookup.Result.Verified);
        Assert.Equal(CloudVerification.LogOnly30d, lookup.Result.Verified);
        Assert.NotNull(lookup.ActivityError);
        Assert.Contains("cannot read", lookup.ActivityError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExplicitlyNullSignInActivityIsStillARealAnswer()
    {
        // The other side of tll-1, and the reason the fix is a split rather than a tightening:
        // Graph really does send "signInActivity": null for an account with no activity. Treating
        // that as a failed source would make every genuinely dormant account unverifiable.
        var (service, stub) = CreateService();
        stub.Activity = () => GraphStub.Json(
            HttpStatusCode.OK, """{"value":[{"userPrincipalName":"jane.doe@example.test","signInActivity":null}]}""");

        var lookup = await service.GetCloudSignInAsync(Upn);

        Assert.Equal(CloudVerification.LogVerified, lookup.Result.Verified);
        Assert.True(lookup.Result.NoSignInReported);
        Assert.Null(lookup.ActivityError);
    }

    [Fact]
    public async Task APresentButBlankActivityDateIsUnreadableRatherThanAbsent()
    {
        // tll-1, second instance. An empty string is not the field being missing.
        var (service, stub) = CreateService();
        stub.Activity = () => GraphStub.Json(HttpStatusCode.OK, ActivityBody("", null));

        var lookup = await service.GetCloudSignInAsync(Upn);

        Assert.NotEqual(CloudVerification.LogVerified, lookup.Result.Verified);
        Assert.Equal(CloudVerification.LogOnly30d, lookup.Result.Verified);
        Assert.NotNull(lookup.ActivityError);
    }

    [Fact]
    public async Task ATimeoutIsReportedAsATimeoutAndNotAsAnAbsence()
    {
        var (service, stub) = CreateService(TimeSpan.FromMilliseconds(300));
        stub.Activity = () => GraphStub.Json(HttpStatusCode.OK, UserWithNoActivityProperty);
        stub.InteractiveDelay = TimeSpan.FromSeconds(30);

        var lookup = await service.GetCloudSignInAsync(Upn);

        Assert.Equal(CloudVerification.ActivityOnly, lookup.Result.Verified);
        Assert.NotNull(lookup.LogError);
        Assert.Contains("timed out", lookup.LogError, StringComparison.Ordinal);
    }

    // ---- Sign-in detail -----------------------------------------------------------------------

    [Fact]
    public async Task TheDetailComesFromTheNewestRowAcrossBothLogQueriesAndCarriesItsOwnTimestamp()
    {
        var (service, stub) = CreateService();
        stub.Activity = () => GraphStub.Json(HttpStatusCode.OK, UserWithNoActivityProperty);
        stub.Interactive = () => GraphStub.Json(
            HttpStatusCode.OK, LogBody("2026-09-10T08:00:00Z", ip: "203.0.113.1"));
        stub.NonInteractive = () => GraphStub.Json(
            HttpStatusCode.OK, LogBody("2026-09-19T08:00:00Z", ip: "203.0.113.2"));

        var lookup = await service.GetCloudSignInAsync(Upn);

        Assert.NotNull(lookup.NewestLogRow);
        var detail = lookup.NewestLogRow;
        Assert.Equal(new DateTime(2026, 9, 19, 8, 0, 0, DateTimeKind.Utc), detail.When);
        Assert.Equal("203.0.113.2", detail.IpAddress);
        Assert.Equal("Outlook (Browser)", detail.App);
        Assert.Equal("Office 365 Exchange Online", detail.Resource);
        Assert.Equal("Boston, MA, US", detail.Location);
        Assert.Equal("success", detail.ConditionalAccess);
    }

    [Fact]
    public async Task ThereIsNoDetailWhenNeitherLogQueryReturnedARow()
    {
        var (service, stub) = CreateService();
        stub.Activity = () => GraphStub.Json(HttpStatusCode.OK, ActivityBody("2026-01-05T09:00:00Z", null));

        var lookup = await service.GetCloudSignInAsync(Upn);

        Assert.Null(lookup.NewestLogRow);
        Assert.Equal(new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc), lookup.Result.Latest);
    }

    // ---- Refusals -----------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyIdentityIsRefusedRatherThanQueried(string identity)
    {
        var (service, stub) = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetCloudSignInAsync(identity));
        Assert.Empty(stub.RequestUrls);
    }

    [Fact]
    public async Task MissingCredentialsThrowRatherThanReturningAnEmptyCloudAnswer()
    {
        // An unconfigured module must not render as an account with no cloud sign-ins.
        var service = new CloudSignInService(_ => Task.FromResult<DefenderApiClient?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCloudSignInAsync(Upn));
    }
}
