using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// One response from <see cref="DefenderApiClient"/>: the parsed document, the HTTP status, a
/// sanitized error, and whether the request timed out.
/// </summary>
/// <remarks>
/// <para>
/// <b>TimedOut is a separate field on purpose.</b> An HttpClient timeout arrives as a
/// <see cref="TaskCanceledException"/>, not as a status code, so there is no status value that
/// means "timed out". Folding it onto 408 RequestTimeout would make a client-side timeout
/// indistinguishable from a service-issued 408, and the plan (T7) requires timeout to be a reason
/// of its own that no other failure can collapse into.
/// </para>
/// <para>
/// StatusCode is still set to 408 on a timeout so that a caller switching on status alone cannot
/// read a timeout as a success; TimedOut is what names it.
/// </para>
/// </remarks>
/// <param name="Document">The parsed body, or null on any failure. The caller owns disposal.</param>
/// <param name="StatusCode">The HTTP status, or 408 when the request timed out client-side.</param>
/// <param name="SafeError">
/// Only the service's own error code and message, via
/// <see cref="GraphTokenClient.ExtractGraphError"/>. Never a token, a secret or a raw body.
/// </param>
/// <param name="TimedOut">True only for a client-side timeout, never for a service-issued 408.</param>
public readonly record struct DefenderApiResult(
    JsonDocument? Document,
    HttpStatusCode StatusCode,
    string? SafeError,
    bool TimedOut);

/// <summary>
/// A module-local OAuth client-credentials HTTP client for the Defender for Endpoint module
/// (docs/DefenderEndpointDevices-Plan.md T1). Modelled on <see cref="GraphTokenClient"/> but taking
/// its base URL and token scope as CONSTRUCTOR ARGUMENTS instead of hardcoding them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class exists at all, rather than a change to the shared client.</b>
/// <see cref="GraphTokenClient"/> hardcodes both the base URL (graph.microsoft.com/v1.0) and the
/// token scope (graph.microsoft.com/.default), so it cannot reach api.security.microsoft.com; and
/// its <c>PostAsync</c> returns a bare JsonDocument, collapsing 403, 429 and 5xx all to null, so
/// the distinct failure reasons this module must show an operator would be unimplementable over it.
/// Adding a status-returning POST to the shared client is the tidier long-term fix and is recorded
/// in the plan as the rejected alternative: that file is shared infrastructure used by three other
/// modules and editing it would bump the base app version, which adding a module must not do.
/// </para>
/// <para>
/// <b>Why one class is constructed twice.</b> The module talks to two different APIs from ONE app
/// registration and one Delinea secret: the Defender host for the device inventory, and Microsoft
/// Graph for the advanced-hunting query that supplies discovery sources. Same client id and secret,
/// two tokens, two audiences. Two instances of one client class is the intended shape here, not a
/// copy-paste error:
/// </para>
/// <list type="table">
///   <item><term>Defender inventory</term>
///     <description>https://api.security.microsoft.com, scope
///     https://api.securitycenter.microsoft.com/.default</description></item>
///   <item><term>Graph hunting (S4)</term>
///     <description>https://graph.microsoft.com/v1.0, scope
///     https://graph.microsoft.com/.default</description></item>
/// </list>
/// <para>
/// <b>The audience really is the other hostname.</b> Learn: "Some Microsoft Defender for Endpoint
/// APIs continue to require access tokens issued for the legacy resource
/// https://api.securitycenter.microsoft.com. If the token audience doesn't match the resource
/// expected by the API, requests fail with 403 Forbidden, even if the API endpoint uses
/// https://api.security.microsoft.com." Getting it wrong is a 403, not a 404, which is why the
/// caller's 403 message names both causes (T2).
/// </para>
/// <para>
/// <b>The sanitizer is reused, not duplicated.</b> SafeError comes from
/// <see cref="GraphTokenClient.ExtractGraphError"/>, which is <c>internal static</c> and therefore
/// callable from this class in the same assembly with a ZERO-LINE diff to that file. Only
/// error.code and error.message can escape - never a token or a raw body. If that member ever has
/// to be moved or widened, this stops being a module-local change and the versioning call in the
/// plan has to be re-read.
/// </para>
/// <para>
/// <b>Nothing here logs.</b> The class takes no logger, which is the cheapest way to guarantee the
/// promise that it never logs the token, the secret or a raw auth response body.
/// </para>
/// </remarks>
public sealed class DefenderApiClient
{
    private readonly HttpClient _httpClient;
    private readonly string _tenantId;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _baseUrl;
    private readonly string _tokenScope;
    private readonly Uri _baseUri;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    /// <summary>The Defender for Endpoint request host. The global one: a regional host would be a
    /// fact about where this tenant sits, which repo-guidance invariant 7 keeps out of source.</summary>
    public const string DefenderBaseUrl = "https://api.security.microsoft.com";

    /// <summary>The Defender token audience - the LEGACY hostname, deliberately not the request
    /// host. See the class remarks; a mismatch is a 403.</summary>
    public const string DefenderTokenScope = "https://api.securitycenter.microsoft.com/.default";

    /// <summary>Microsoft Graph v1.0, for the S4 advanced-hunting call.</summary>
    public const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    /// <summary>Microsoft Graph's token audience, for the S4 advanced-hunting call.</summary>
    public const string GraphTokenScope = "https://graph.microsoft.com/.default";

    public bool IsConfigured =>
        !string.IsNullOrEmpty(_tenantId) && !string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret);

    /// <summary>The host this client will talk to. Requests to any other host are refused.</summary>
    public string Host => _baseUri.Host;

    public DefenderApiClient(
        string tenantId,
        string clientId,
        string clientSecret,
        string baseUrl,
        string tokenScope,
        HttpClient httpClient)
    {
        _tenantId = tenantId ?? "";
        _clientId = clientId ?? "";
        _clientSecret = clientSecret ?? "";
        _baseUrl = (baseUrl ?? "").TrimEnd('/');
        _tokenScope = tokenScope ?? "";
        _httpClient = httpClient;
        _baseUri = new Uri(_baseUrl, UriKind.Absolute);
    }

    /// <summary>
    /// GET that surfaces the status, a sanitized error and a client-side timeout, and that accepts
    /// EITHER a relative path or an absolute <c>@odata.nextLink</c>.
    /// </summary>
    /// <remarks>
    /// Following an absolute continuation URL is the capability the shared GraphTokenClient does not
    /// have - it prepends its base URL to whatever it is handed, so an absolute nextLink becomes a
    /// broken request. An absolute URL given here is sent unmodified: its path, query and escaping
    /// reach the service exactly as the service wrote them, because a continuation token that has
    /// been re-encoded is not the token the service issued.
    /// </remarks>
    public Task<DefenderApiResult> GetWithStatusAsync(string endpointOrAbsoluteUrl) =>
        SendAsync(HttpMethod.Get, endpointOrAbsoluteUrl, body: null);

    /// <summary>
    /// POST that surfaces the status, a sanitized error and a client-side timeout. Used by S4 for
    /// the Graph advanced-hunting query, whose 403 (permission not consented), 429 (CPU quota) and
    /// timeout must each render as a different reason.
    /// </summary>
    public Task<DefenderApiResult> PostWithStatusAsync(string endpointOrAbsoluteUrl, object? body = null) =>
        SendAsync(HttpMethod.Post, endpointOrAbsoluteUrl, body);

    private async Task<DefenderApiResult> SendAsync(HttpMethod method, string endpointOrAbsoluteUrl, object? body)
    {
        if (!TryResolveRequestUri(endpointOrAbsoluteUrl, out var uri, out var rejection))
            return new DefenderApiResult(null, HttpStatusCode.BadRequest, rejection, false);

        try
        {
            // Acquired INSIDE the try so a timeout on the token request is reported as a timeout
            // rather than escaping as an unhandled exception from a different call.
            var token = await GetAccessTokenAsync();

            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            if (body != null)
            {
                var json = JsonSerializer.Serialize(body);
                request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            }

            var response = await _httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return new DefenderApiResult(null, response.StatusCode, GraphTokenClient.ExtractGraphError(content), false);

            if (string.IsNullOrWhiteSpace(content))
                return new DefenderApiResult(null, response.StatusCode, null, false);

            try
            {
                return new DefenderApiResult(JsonDocument.Parse(content), response.StatusCode, null, false);
            }
            catch (JsonException)
            {
                // A 200 carrying a body we cannot parse is a FAILED request, not an empty one. The
                // body itself is never echoed - it is the one thing here that could contain data
                // the operator must not be shown.
                return new DefenderApiResult(
                    null,
                    response.StatusCode,
                    "The service returned a response that could not be parsed as JSON.",
                    false);
            }
        }
        catch (TaskCanceledException)
        {
            // No CancellationToken is passed to SendAsync, so the only thing that cancels this task
            // is the HttpClient's own timeout. Returned as a result rather than thrown: T7 requires
            // a timeout to be one of the named reasons on the page, not a stack trace.
            return new DefenderApiResult(null, HttpStatusCode.RequestTimeout, null, true);
        }
    }

    /// <summary>
    /// Resolves a relative path against the configured base URL, or accepts an absolute URL as-is -
    /// but only when it is HTTPS and on the SAME HOST this client was configured for.
    /// </summary>
    /// <remarks>
    /// The host check is not ceremony. Every request carries a bearer token for this app
    /// registration, and the absolute-URL path exists so the client can follow a continuation link
    /// that came out of a response body. Following an arbitrary host from a response body would
    /// hand that token to whatever host the body named. Refusing off-host and non-HTTPS URLs means
    /// no token is ever acquired, let alone sent, for one.
    /// </remarks>
    private bool TryResolveRequestUri(string endpointOrAbsoluteUrl, out Uri? uri, out string? rejection)
    {
        uri = null;
        rejection = null;

        if (string.IsNullOrWhiteSpace(endpointOrAbsoluteUrl))
        {
            rejection = "An empty request path cannot be sent.";
            return false;
        }

        if (Uri.TryCreate(endpointOrAbsoluteUrl, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme != Uri.UriSchemeHttps)
            {
                rejection = "Refused a continuation URL that was not HTTPS.";
                return false;
            }

            if (!string.Equals(absolute.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                rejection = "Refused a continuation URL pointing at a different host than the API being called.";
                return false;
            }

            // The Uri is built from the ORIGINAL string, so what goes on the wire is what the
            // service issued - no re-escaping of a continuation token.
            uri = absolute;
            return true;
        }

        uri = new Uri(_baseUrl + endpointOrAbsoluteUrl, UriKind.Absolute);
        return true;
    }

    /// <summary>
    /// Client-credentials token for the CONFIGURED scope, cached with the same lock-and-expiry shape
    /// as GraphTokenClient (five minutes of headroom before expiry).
    /// </summary>
    private async Task<string> GetAccessTokenAsync()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Defender API credentials not configured for this module.");

        await _tokenLock.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
                return _accessToken;

            var tokenUrl = $"https://login.microsoftonline.com/{_tenantId}/oauth2/v2.0/token";
            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", _clientId),
                new KeyValuePair<string, string>("client_secret", _clientSecret),
                new KeyValuePair<string, string>("scope", _tokenScope)
            });

            var response = await _httpClient.PostAsync(tokenUrl, body);
            if (!response.IsSuccessStatusCode)
                // Status only. The auth response body can carry correlation data and is never
                // echoed, logged or returned.
                throw new InvalidOperationException($"Defender API token request failed: {response.StatusCode}");

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
