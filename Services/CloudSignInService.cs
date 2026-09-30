using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// One row out of the raw sign-in log, with the time it happened.
/// </summary>
/// <remarks>
/// <b><see cref="When"/> travels with the detail on purpose.</b> The log retains only ~30 days
/// while <c>signInActivity</c> keeps older dates, so the newest row the log can show is not
/// always the account's latest sign-in. A caller that renders the IP, app and location without
/// rendering the timestamp beside them is asserting something this record does not claim.
/// </remarks>
public sealed record CloudSignInDetail(
    DateTime When,
    string? IpAddress,
    string? App,
    string? Resource,
    string? Location,
    string? ConditionalAccess);

/// <summary>
/// The cloud half of a true-last-logon answer: the combined result, and what each source said
/// when it did not answer.
/// </summary>
/// <param name="Result">The dates and the verification state, from <see cref="CloudSignInAggregator"/>.</param>
/// <param name="ActivityError">
/// Why <c>signInActivity</c> did not answer, or null when it did. Non-null here is exactly what
/// turns a <see cref="CloudVerification.LogVerified"/> answer into a weaker one, so it must be
/// shown, not logged and dropped.
/// </param>
/// <param name="LogError">Why one or both raw-log queries did not answer, or null when both did.</param>
/// <param name="NewestLogRow">
/// The newest row either log query returned, or null when neither returned a usable one.
/// </param>
public sealed record CloudSignInLookup(
    CloudSignInResult Result,
    string? ActivityError,
    string? LogError,
    CloudSignInDetail? NewestLogRow);

/// <summary>
/// The cloud half of True Last Logon (docs/TrueLastLogon-Plan.md S2): the Graph I/O that feeds
/// <see cref="CloudSignInAggregator"/>. Three queries for one user, run concurrently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three queries, because no one of them is a superset of the others.</b>
/// </para>
/// <list type="number">
/// <item><c>signInActivity</c> on the user, which keeps dates older than 30 days but is a
/// materialized aggregate that under-reports - measured by the source script on 2026-08-20, of
/// 557 accounts it called dormant, 9 had sign-ins the next day.</item>
/// <item>The raw sign-in log, interactive.</item>
/// <item>The raw sign-in log, NON-interactive, which is the query that catches the
/// under-reporting above and is therefore not optional.</item>
/// </list>
/// <para>
/// <b>Why the list form of the user query and not <c>/users/{id}</c>.</b> The source script
/// records that <c>/users/{id}?$select=signInActivity</c> returned no activity for users who
/// demonstrably had it, while the <c>/users</c> collection form with an <c>eq</c> filter is
/// confirmed working. That is a measurement, not a preference, so it is reproduced here.
/// </para>
/// <para>
/// <b>Why the non-interactive query goes to beta.</b> Graph v1.0 documents its sign-in list as
/// carrying interactive sign-ins only ("Sign-ins that are interactive in nature ... are currently
/// included in the sign-in logs"), and the v1.0 <c>signIn</c> resource has no
/// <c>signInEventTypes</c> property to filter on. Non-interactive sign-ins are reachable only on
/// the beta endpoint. The shared <see cref="GraphTokenClient"/> is confined to v1.0 by a
/// deliberate guard, so it cannot make this call.
/// </para>
/// <para>
/// <b>Why <see cref="DefenderApiClient"/> is the client here, despite its name.</b> It is the
/// only client in this assembly that takes its base URL and token scope as constructor
/// arguments, which is exactly what a v1.0 call and a beta call from one app registration need.
/// The alternative was widening the shared <see cref="GraphTokenClient"/>, and that is a change
/// to shared infrastructure used by eleven services, which would bump the base app version -
/// something adding a module must not do (.agents/decisions.md 2026-07-21). Its own class
/// remarks already record that it exists for that reason and that it is constructed more than
/// once against different APIs. No credential is shared: this module reads its OWN
/// <c>GraphDelineaSecretId</c>.
/// </para>
/// <para>
/// <b>A value that cannot be read is a source that did not answer.</b> That single rule covers a
/// non-200, a timeout, a body with no result collection, and a date string that will not parse.
/// It is the same rule <see cref="TrueLastLogonService.MapRow"/> applies on-prem, and it is what
/// keeps a broken response out of the one reading that matters - "this account is dormant".
/// </para>
/// <para>
/// <b>No retries.</b> The source script retries transient failures because it processes hundreds
/// of users in one run and a lost sub-request is a lost row. One user is not that: a transient
/// failure here degrades the verification state, which the page shows, and the operator can look
/// again. A retry loop would add a failure mode without removing one.
/// </para>
/// </remarks>
public sealed class CloudSignInService
{
    /// <summary>Module id for config lookups. The descriptor that declares the field arrives in S3.</summary>
    public const string ModuleId = "TrueLastLogon";

    /// <summary>This module's own Graph secret. Never another module's (Constitution, Credential Isolation).</summary>
    public const string SecretIdConfigKey = "GraphDelineaSecretId";

    /// <summary>
    /// The named HttpClient S3 registers. The sign-in log is slow - the source script measures
    /// roughly ten seconds per query - so it wants a longer timeout than the shared
    /// "MicrosoftGraph" client's thirty seconds. Until that registration exists the factory hands
    /// back a default-configured client, which is slower to give up, never wrong.
    /// </summary>
    public const string HttpClientName = "TrueLastLogonGraph";

    /// <summary>Graph v1.0: the user query and the interactive sign-in log.</summary>
    public const string GraphV1BaseUrl = "https://graph.microsoft.com/v1.0";

    /// <summary>Graph beta: the ONLY endpoint that exposes non-interactive sign-ins.</summary>
    public const string GraphBetaBaseUrl = "https://graph.microsoft.com/beta";

    /// <summary>One audience for both base URLs - beta is the same service, not another one.</summary>
    public const string GraphTokenScope = "https://graph.microsoft.com/.default";

    /// <summary>What <c>signInActivity</c> needs. Both, not either: a user-read scope alone returns a 403.</summary>
    internal const string ActivityPermissions = "AuditLog.Read.All and User.Read.All";

    /// <summary>What the raw sign-in log needs.</summary>
    internal const string LogPermissions = "AuditLog.Read.All";

    private const string LogSelect =
        "createdDateTime,ipAddress,appDisplayName,clientAppUsed,resourceDisplayName,location,conditionalAccessStatus";

    private readonly ILogger<CloudSignInService>? _logger;
    private readonly ModuleConfigService? _moduleConfig;
    private readonly DelineaService? _delineaService;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly Func<string, Task<DefenderApiClient?>> _clientFactory;

    public CloudSignInService(
        ILogger<CloudSignInService> logger,
        ModuleConfigService moduleConfig,
        DelineaService delineaService,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _moduleConfig = moduleConfig;
        _delineaService = delineaService;
        _httpClientFactory = httpClientFactory;
        _clientFactory = BuildClientAsync;
    }

    /// <summary>
    /// Test seam: drives the query shapes, the per-source answer rules and the failure messages
    /// against canned HTTP, without a Secret Server call or a tenant. The argument is the base URL
    /// being asked for, so a test can hand the v1.0 and beta calls different responses.
    /// </summary>
    internal CloudSignInService(Func<string, Task<DefenderApiClient?>> clientFactory) =>
        _clientFactory = clientFactory;

    /// <summary>Whether a Graph secret has been pointed at this module at all.</summary>
    public bool IsAvailable
    {
        get
        {
            var secretIdStr = _moduleConfig?.GetValue(ModuleId, SecretIdConfigKey);
            return int.TryParse(secretIdStr, out var id) && id > 0;
        }
    }

    /// <summary>
    /// Asks the cloud when <paramref name="userPrincipalName"/> last signed in.
    /// </summary>
    /// <remarks>
    /// <b>This needs a UPN, not a sAMAccountName.</b> The filter is an equality match on
    /// <c>userPrincipalName</c>, so a sAMAccountName matches nothing - which is reported as "no
    /// account matched" and a source that did not answer, never as an account that has never
    /// signed in.
    /// </remarks>
    public async Task<CloudSignInLookup> GetCloudSignInAsync(string userPrincipalName)
    {
        if (string.IsNullOrWhiteSpace(userPrincipalName))
            throw new ArgumentException("A user principal name is required.", nameof(userPrincipalName));

        var upn = userPrincipalName.Trim();

        var v1 = await _clientFactory(GraphV1BaseUrl);
        var beta = await _clientFactory(GraphBetaBaseUrl);

        if (v1 == null || beta == null)
        {
            throw new InvalidOperationException(
                "True Last Logon has no Graph credentials. Set Graph App Delinea Secret ID in Module Config.");
        }

        // Started together, awaited one at a time. Three sequential queries at roughly ten
        // seconds each is half a minute of an operator watching a spinner; concurrently it is one
        // query's worth. Nothing below throws, so no task is left with an unobserved exception.
        var activityTask = ReadActivityAsync(v1, upn);
        var interactiveTask = ReadLogAsync(v1, upn, nonInteractive: false);
        var nonInteractiveTask = ReadLogAsync(beta, upn, nonInteractive: true);

        var activity = await activityTask;
        var interactive = await interactiveTask;
        var nonInteractive = await nonInteractiveTask;

        // The log counts as having ANSWERED only when both of its queries did. A run that read
        // the interactive log and lost the non-interactive one has not checked the very sign-ins
        // signInActivity is known to miss, so calling that LogVerified would put the module's
        // strongest claim behind its weakest evidence. Dates that did arrive are still carried:
        // the aggregator takes the later of each pair regardless of the verification state.
        var log = new CloudSignInAnswer(
            interactive.Answer.Answered && nonInteractive.Answer.Answered,
            interactive.Answer.Interactive,
            nonInteractive.Answer.NonInteractive);

        var logError = Join(interactive.Error, nonInteractive.Error);

        foreach (var failure in new[] { activity.Error, logError }.Where(e => e != null))
            _logger?.LogDebug("True last logon, cloud half: {Failure}", failure);

        return new CloudSignInLookup(
            CloudSignInAggregator.Combine(activity.Answer, log),
            activity.Error,
            logError,
            Newer(interactive.Row, nonInteractive.Row));
    }

    /// <summary>The <c>signInActivity</c> query, as it goes on the wire.</summary>
    internal static string ActivityQuery(string upn) =>
        "/users?$filter=" + Uri.EscapeDataString(UpnFilter(upn))
        + "&$select=userPrincipalName,signInActivity";

    /// <summary>One sign-in log query, newest row only.</summary>
    internal static string LogQuery(string upn, bool nonInteractive)
    {
        var filter = UpnFilter(upn);
        if (nonInteractive)
            filter += " and signInEventTypes/any(t: t eq 'nonInteractiveUser')";

        return "/auditLogs/signIns?$filter=" + Uri.EscapeDataString(filter)
            + "&$top=1&$orderby=" + Uri.EscapeDataString("createdDateTime desc")
            + "&$select=" + LogSelect;
    }

    /// <summary>
    /// The UPN equality filter. Lowercased because the sign-in log stores UPNs that way, with
    /// single quotes doubled per OData. The caller escapes the whole expression afterwards, which
    /// matters for B2B guest UPNs: an unescaped '#' truncates the query string at the fragment.
    /// </summary>
    internal static string UpnFilter(string upn) =>
        "userPrincipalName eq '" + upn.Trim().ToLowerInvariant().Replace("'", "''") + "'";

    private async Task<DefenderApiClient?> BuildClientAsync(string baseUrl)
    {
        if (_moduleConfig == null || _delineaService == null || _httpClientFactory == null)
            return null;

        var secretIdStr = _moduleConfig.GetValue(ModuleId, SecretIdConfigKey);
        if (!int.TryParse(secretIdStr, out var secretId) || secretId <= 0)
            return null;

        var fields = await _delineaService.GetSecretFieldsAsync(secretId);
        if (fields == null)
            return null;

        var tenantId = fields.GetValueOrDefault("Tenant ID") ?? "";
        var clientId = fields.GetValueOrDefault("Application ID") ?? "";
        var clientSecret = fields.GetValueOrDefault("Client Secret") ?? "";

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            return null;

        return new DefenderApiClient(
            tenantId,
            clientId,
            clientSecret,
            baseUrl,
            GraphTokenScope,
            _httpClientFactory.CreateClient(HttpClientName));
    }

    private static async Task<(CloudSignInAnswer Answer, string? Error)> ReadActivityAsync(
        DefenderApiClient client, string upn)
    {
        var response = await client.GetWithStatusAsync(ActivityQuery(upn));
        using var doc = response.Document;

        if (doc == null)
            return (CloudSignInAnswer.DidNotAnswer, DescribeFailure(response, "signInActivity", ActivityPermissions));

        if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return (CloudSignInAnswer.DidNotAnswer,
                "The signInActivity query returned a body with no user collection in it.");
        }

        foreach (var user in value.EnumerateArray())
        {
            // The account came back, so signInActivity WAS consulted - even when the property is
            // absent, which legitimately means no recorded activity. Telling that apart from a
            // query that never succeeded is the entire point of the Answered flag.
            if (!user.TryGetProperty("signInActivity", out var activity) || activity.ValueKind != JsonValueKind.Object)
                return (new CloudSignInAnswer(true, null, null), null);

            var interactive = ReadDate(activity, "lastSignInDateTime", out var interactiveWhen);
            var nonInteractive = ReadDate(activity, "lastNonInteractiveSignInDateTime", out var nonInteractiveWhen);

            if (interactive == DateRead.Malformed || nonInteractive == DateRead.Malformed)
            {
                return (CloudSignInAnswer.DidNotAnswer,
                    "signInActivity returned a date that could not be read, so its answer was discarded.");
            }

            return (new CloudSignInAnswer(true, interactiveWhen, nonInteractiveWhen), null);
        }

        return (CloudSignInAnswer.DidNotAnswer,
            $"No cloud account matched '{upn}'. That is not evidence that the account has never signed in.");
    }

    private static async Task<(CloudSignInAnswer Answer, CloudSignInDetail? Row, string? Error)> ReadLogAsync(
        DefenderApiClient client, string upn, bool nonInteractive)
    {
        var kind = nonInteractive ? "non-interactive" : "interactive";
        var response = await client.GetWithStatusAsync(LogQuery(upn, nonInteractive));
        using var doc = response.Document;

        if (doc == null)
        {
            return (CloudSignInAnswer.DidNotAnswer, null,
                DescribeFailure(response, $"the {kind} sign-in log", LogPermissions));
        }

        if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return (CloudSignInAnswer.DidNotAnswer, null,
                $"The {kind} sign-in log query returned a body with no result collection in it.");
        }

        foreach (var row in value.EnumerateArray())
        {
            var read = ReadDate(row, "createdDateTime", out var when);

            // A row exists, so there IS a sign-in; we just cannot date it. Reporting that as an
            // answered query with no sign-ins would invent the dormancy this module exists to
            // disprove.
            if (read != DateRead.Ok || when == null)
            {
                return (CloudSignInAnswer.DidNotAnswer, null,
                    $"The {kind} sign-in log returned an entry with no readable date, so it was discarded.");
            }

            var detail = new CloudSignInDetail(
                when.Value,
                ReadString(row, "ipAddress"),
                ReadApp(row),
                ReadString(row, "resourceDisplayName"),
                ReadLocation(row),
                ReadString(row, "conditionalAccessStatus"));

            var answer = nonInteractive
                ? new CloudSignInAnswer(true, null, when)
                : new CloudSignInAnswer(true, when, null);

            return (answer, detail, null);
        }

        // Answered, and it reported nothing. Good evidence only when the other source answered
        // too, which CloudSignInAggregator, not this method, decides.
        return (new CloudSignInAnswer(true, null, null), null, null);
    }

    private static string DescribeFailure(DefenderApiResult response, string what, string permissions)
    {
        if (response.TimedOut)
            return $"The {what} query timed out. A timeout is not an absence of sign-ins.";

        var said = response.SafeError == null ? "" : $" Graph said: {response.SafeError}";

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return $"Graph refused the {what} query ({(int)response.StatusCode}). The app registration behind "
                + $"this module's secret needs {permissions} consented.{said}";
        }

        return $"The {what} query failed: {(int)response.StatusCode} {response.StatusCode}.{said}";
    }

    /// <summary>Whether a date field was absent, present but unreadable, or read.</summary>
    private enum DateRead
    {
        Absent,
        Malformed,
        Ok,
    }

    private static DateRead ReadDate(JsonElement element, string name, out DateTime? when)
    {
        when = null;

        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return DateRead.Absent;

        if (value.ValueKind != JsonValueKind.String)
            return DateRead.Malformed;

        var raw = value.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return DateRead.Absent;

        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return DateRead.Malformed;

        when = parsed.ToUniversalTime();
        return DateRead.Ok;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? NullIfBlank(value.GetString())
            : null;

    /// <summary>The app, with the client that reached it in brackets when Graph names one.</summary>
    private static string? ReadApp(JsonElement row)
    {
        var app = ReadString(row, "appDisplayName");
        var client = ReadString(row, "clientAppUsed");

        if (app == null) return client;
        return client == null ? app : $"{app} ({client})";
    }

    private static string? ReadLocation(JsonElement row)
    {
        if (!row.TryGetProperty("location", out var location) || location.ValueKind != JsonValueKind.Object)
            return null;

        var parts = new[] { "city", "state", "countryOrRegion" }
            .Select(name => ReadString(location, name))
            .Where(part => part != null);

        return NullIfBlank(string.Join(", ", parts));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static CloudSignInDetail? Newer(CloudSignInDetail? a, CloudSignInDetail? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return a.When >= b.When ? a : b;
    }

    private static string? Join(string? a, string? b)
    {
        if (a == null) return b;
        return b == null ? a : $"{a} {b}";
    }
}
