using System.Net;
using System.Text.Json;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Read-only Microsoft Defender for Endpoint device inventory
/// (docs/DefenderEndpointDevices-Plan.md). Lists the machines matching the active filters -
/// including network-discovered devices that can be onboarded but are not - and either returns the
/// COMPLETE matching set or refuses in words. It never returns a partial set that looks whole.
/// </summary>
/// <remarks>
/// <para>
/// The module mutates nothing. It reads GET /api/machines and never GET /api/machines/{id}, because
/// Learn documents only Machine.ReadWrite.All for get-machine-by-id and this module's registration
/// must not carry a write scope (T4). The detail view renders from the row the list already
/// returned.
/// </para>
/// <para>
/// Credentials are per-module: the tenant id, application id and client secret come from THIS
/// module's own Delinea secret at call time, named by its own GraphDelineaSecretId config field.
/// Nothing is defaulted in code, in appsettings, or from another module's configuration.
/// </para>
/// </remarks>
public sealed class DefenderEndpointDeviceService
{
    private readonly ModuleConfigService? _moduleConfig;
    private readonly DelineaService? _delineaService;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly Func<Task<DefenderApiClient?>> _apiClientFactory;
    private readonly Func<Task<DefenderApiClient?>> _huntingClientFactory;
    private readonly string? _maxDevicesOverride;
    private readonly string? _includeDiscoverySourcesOverride;

    internal const string ModuleId = "DefenderEndpointDevices";
    internal const string SecretIdConfigKey = "GraphDelineaSecretId";
    internal const string MaxDevicesConfigKey = "MaxDevices";
    internal const string IncludeDiscoverySourcesConfigKey = "IncludeDiscoverySources";

    /// <summary>The named HttpClient this module's inventory calls use.</summary>
    public const string HttpClientName = "DefenderEndpoint";

    /// <summary>
    /// The named HttpClient the advanced hunting call uses - a SECOND registration, and deliberate
    /// (T7). The inventory client times out at 30 seconds, which is right for a machines page and
    /// wrong for a hunting query: Graph allows a single hunting request up to three minutes of its
    /// own, so a 30-second client would cancel legitimate work and surface it as an intermittent,
    /// load-dependent "timeout" that reads like a service fault. This one is set above Graph's own
    /// ceiling so the service's answer wins and a client-side timeout means something has genuinely
    /// hung.
    /// </summary>
    public const string HuntingHttpClientName = "DefenderEndpointHunting";

    internal const string MachinesEndpoint = "/api/machines";

    /// <summary>
    /// The Graph advanced hunting endpoint. Relative on purpose - it is resolved against the HUNTING
    /// client's base URL (graph.microsoft.com/v1.0), not the Defender host.
    /// </summary>
    internal const string HuntingEndpoint = "/security/runHuntingQuery";

    /// <summary>
    /// The advanced hunting query. A CONSTANT in source: no operator input is interpolated into KQL,
    /// ever, and there is no code path that builds this string from anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ThreatHunting.Read.All is scoped to the WHOLE hunting schema - mail, identity, process events,
    /// everything - and the only thing keeping this module inside DeviceInfo is this string. That is
    /// why a test asserts it names DeviceInfo and no other table: the permission cannot enforce the
    /// narrowing, so the query text has to.
    /// </para>
    /// <para>
    /// isempty(MergedToDeviceId) is not decoration. Without it, merged and invalidated duplicate
    /// records come back and one physical device appears twice; arg_max(Timestamp, *) takes the
    /// latest state row per device.
    /// </para>
    /// </remarks>
    internal const string HuntingQuery =
        "DeviceInfo | summarize arg_max(Timestamp, *) by DeviceId | where isempty(MergedToDeviceId)"
        + " | project DeviceId, DiscoverySources, DeviceType, DeviceCategory, Vendor, Model";

    /// <summary>
    /// What an enrichment cell shows when the hunting query did not run or failed. Never a blank: an
    /// empty cell must not be how an operator learns that half the report did not execute.
    /// </summary>
    public const string EnrichmentUnavailable = "(unavailable)";

    /// <summary>Documented maximum page size and maximum $top for the machines collection.</summary>
    internal const int MaxTop = 10000;

    /// <summary>The MaxDevices default the descriptor declares.</summary>
    internal const int DefaultMaxDevices = 20000;

    /// <summary>
    /// Hard stop on requests per run. Derived, not invented: the endpoint documents a limit of 100
    /// calls per minute, so a single run that issues more than that has already exhausted the
    /// tenant's per-minute budget - an unbounded loop against a paged API is how a read module
    /// becomes an outage. Hitting it refuses, because a run that stopped early has not proved
    /// exhaustion.
    /// </summary>
    internal const int MaxRequestsPerRun = 100;

    public DefenderEndpointDeviceService(
        ModuleConfigService moduleConfig,
        DelineaService delineaService,
        IHttpClientFactory httpClientFactory)
    {
        _moduleConfig = moduleConfig;
        _delineaService = delineaService;
        _httpClientFactory = httpClientFactory;
        _apiClientFactory = GetApiClientAsync;
        _huntingClientFactory = GetHuntingClientAsync;
    }

    /// <summary>
    /// Test seam, the same shape as ServiceHealthService's: drives the paging, completion, parsing
    /// and enrichment logic against canned DefenderApiClients with no live Secret Server call. Does
    /// not change the public DI constructor or its Program.cs registration.
    /// </summary>
    /// <param name="huntingClientFactory">
    /// Defaults to a factory returning null, which is NOT a quiet success: the enrichment reports
    /// NotAttempted with <see cref="DefenderDiscoveryReasons.CredentialsUnavailable"/>, so a test
    /// that forgot to supply one sees an explicit reason rather than a silently empty column.
    /// </param>
    internal DefenderEndpointDeviceService(
        Func<Task<DefenderApiClient?>> apiClientFactory,
        string? maxDevicesOverride = null,
        Func<Task<DefenderApiClient?>>? huntingClientFactory = null,
        string? includeDiscoverySourcesOverride = null)
    {
        _apiClientFactory = apiClientFactory;
        _maxDevicesOverride = maxDevicesOverride;
        _huntingClientFactory = huntingClientFactory ?? (() => Task.FromResult<DefenderApiClient?>(null));
        _includeDiscoverySourcesOverride = includeDiscoverySourcesOverride;
    }

    /// <summary>
    /// Builds the Defender-host client from this module's own Delinea secret. Same shape as
    /// ServiceHealthService.GetGraphClientAsync, reading DefenderEndpointDevices /
    /// GraphDelineaSecretId and never another module's configuration.
    /// </summary>
    private async Task<DefenderApiClient?> GetApiClientAsync() =>
        await BuildClientAsync(
            DefenderApiClient.DefenderBaseUrl,
            DefenderApiClient.DefenderTokenScope,
            HttpClientName);

    /// <summary>
    /// Builds the GRAPH-host client for the advanced hunting query, from the SAME Delinea secret and
    /// the same app registration - one client id and secret, two audiences (T1).
    /// </summary>
    /// <remarks>
    /// This reads the secret a second time within a run rather than sharing one read with
    /// <see cref="GetApiClientAsync"/>. That is deliberate: this service is a singleton, so a shared
    /// credential would have to live in a field, and a cached secret on a singleton is how a rotated
    /// client secret keeps failing until the app pool recycles. Two reads of a cached-token Secret
    /// Server client, once per operator click, is the cheaper side of that trade.
    /// </remarks>
    private async Task<DefenderApiClient?> GetHuntingClientAsync() =>
        await BuildClientAsync(
            DefenderApiClient.GraphBaseUrl,
            DefenderApiClient.GraphTokenScope,
            HuntingHttpClientName);

    private async Task<DefenderApiClient?> BuildClientAsync(string baseUrl, string tokenScope, string httpClientName)
    {
        if (_moduleConfig == null || _delineaService == null || _httpClientFactory == null)
            return null;

        var secretIdStr = _moduleConfig.GetValue(ModuleId, SecretIdConfigKey);
        if (!int.TryParse(secretIdStr, out var secretId) || secretId <= 0)
            throw new InvalidOperationException(
                "Defender for Endpoint Devices is not configured. Set Graph App Delinea Secret ID in Module Config.");

        var fields = await _delineaService.GetSecretFieldsAsync(secretId);
        if (fields == null)
            throw new InvalidOperationException(
                $"Cannot retrieve the Defender for Endpoint Devices app secret {secretId} from Secret Server. "
                + "Verify this is the correct Secret ID and that the Delinea SDK client can view it.");

        var tenantId = fields.GetValueOrDefault("Tenant ID") ?? "";
        var clientId = fields.GetValueOrDefault("Application ID") ?? "";
        var clientSecret = fields.GetValueOrDefault("Client Secret") ?? "";

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            throw new InvalidOperationException("Defender for Endpoint app credentials incomplete in Secret Server.");

        return new DefenderApiClient(
            tenantId,
            clientId,
            clientSecret,
            baseUrl,
            tokenScope,
            _httpClientFactory.CreateClient(httpClientName));
    }

    /// <summary>
    /// Whether the advanced hunting query runs at all - the IncludeDiscoverySources config switch.
    /// </summary>
    /// <remarks>
    /// Absent or blank is the descriptor's documented default of ON, not a mistype. A non-blank
    /// value that will not parse reads as OFF, following the precedent in
    /// <see cref="UsageTelemetryService"/>: the module config page renders an unparseable Boolean as
    /// an UNCHECKED box, so defaulting it to ON would make the switch and the screen that shows it
    /// disagree. OFF is also the fail-closed direction here - it is the value that does not call a
    /// permission the registration may not hold.
    /// </remarks>
    internal bool IncludeDiscoverySources
    {
        get
        {
            var configured = _includeDiscoverySourcesOverride
                ?? _moduleConfig?.GetValue(ModuleId, IncludeDiscoverySourcesConfigKey);

            if (string.IsNullOrWhiteSpace(configured))
                return true;

            return bool.TryParse(configured, out var enabled) && enabled;
        }
    }

    public bool IsAvailable
    {
        get
        {
            var secretIdStr = _moduleConfig?.GetValue(ModuleId, SecretIdConfigKey);
            return int.TryParse(secretIdStr, out var id) && id > 0;
        }
    }

    /// <summary>
    /// Lists every device matching the filters, or refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The completion rule (T3).</b> A run is complete only on a POSITIVE PROOF of exhaustion,
    /// and absence of evidence is never proof:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>P1</b> - the request asked for $top = N, the response returned FEWER
    ///   than N rows and carried no @odata.nextLink. Complete.</description></item>
    ///   <item><description><b>P2</b> - every continuation was an @odata.nextLink followed as an
    ///   absolute URL, and the final response carried none and was short in the P1
    ///   sense. Complete.</description></item>
    ///   <item><description><b>Refuse</b> when a response returns EXACTLY the $top it asked for and
    ///   carries no cursor; when the accumulated set would exceed MaxDevices; or when any request in
    ///   the chain fails.</description></item>
    /// </list>
    /// <para>
    /// The full-page-without-a-cursor case is the one that looks like success and is not. A response
    /// holding exactly as many rows as we asked for is byte-identical whether exactly that many
    /// devices exist or the service capped us. There is no way to tell them apart, so this does not
    /// guess - it refuses, and the caller shows no table and no export button.
    /// </para>
    /// <para>
    /// <b>$skip is not used, deliberately.</b> De-duplication removes overlaps and cannot detect a
    /// gap: skipped devices leave no trace in the accumulated set. A short page is exactly what a
    /// moved window produces, so "page until a short page arrives" terminates identically on a
    /// complete collection and a truncated one. And $skip is order-dependent while this collection
    /// documents no $orderby. Reintroducing it needs a cited source that $skip paging is stable for
    /// this endpoint - not an argument from de-duplication, which is the argument already tried.
    /// De-duplication below is cheap insurance against a duplicate row inside a cursor chain, and is
    /// NOT a completeness argument.
    /// </para>
    /// </remarks>
    public async Task<DefenderDeviceListResult> ListDevicesAsync(DefenderDeviceFilters? filters = null)
    {
        filters ??= new DefenderDeviceFilters();

        var client = await _apiClientFactory()
            ?? throw new InvalidOperationException("Defender for Endpoint Devices credentials not available.");

        var ceiling = ClampMaxDevices(_maxDevicesOverride ?? _moduleConfig?.GetValue(ModuleId, MaxDevicesConfigKey));

        // Asking for ONE MORE than the ceiling is what makes the ceiling test decisive in a single
        // round trip: if ceiling + 1 rows come back, more than ceiling devices match, and that is a
        // refusal without a second call. Clamped to the documented $top maximum.
        var top = Math.Min(ceiling + 1, MaxTop);

        var byId = new Dictionary<string, DefenderDevice>(StringComparer.OrdinalIgnoreCase);
        var pages = 0;
        var requestedTop = top;
        string? nextUrl = BuildMachinesUrl(filters, top);

        while (nextUrl != null)
        {
            var result = await client.GetWithStatusAsync(nextUrl);
            pages++;

            if (result.Document == null)
            {
                // T5, and it inverts this repo's usual rule for a reason: Learn documents 404 as
                // the EMPTY result for this collection ("If there are no recent machines, you see
                // 404 Not Found"). That holds for the first request only - a 404 partway through a
                // cursor chain is a broken chain, not an empty inventory, and refuses below.
                if (pages == 1 && !result.TimedOut && result.StatusCode == HttpStatusCode.NotFound)
                    return DefenderDeviceListResult.CompleteRun(
                        [], 0, pages, ceiling,
                        DefenderDiscoveryEnrichmentState.NotAttempted, DefenderDiscoveryReasons.NoDevices);

                return DefenderDeviceListResult.Refusal(
                    DefenderDeviceListOutcome.RequestFailed,
                    DescribeFailure(result, pages),
                    byId.Count,
                    pages,
                    ceiling);
            }

            using var document = result.Document;

            if (!TryReadPage(document, out var rows, out var nextLink))
                return DefenderDeviceListResult.Refusal(
                    DefenderDeviceListOutcome.RequestFailed,
                    "The Defender for Endpoint API returned a response with no device collection in it, so the "
                    + "report could not be completed. Nothing is being shown, because an unreadable response is "
                    + "not an empty inventory.",
                    byId.Count,
                    pages,
                    ceiling);

            foreach (var device in rows)
                byId[DeduplicationKey(device, byId.Count)] = device;

            if (byId.Count > ceiling)
                return DefenderDeviceListResult.Refusal(
                    DefenderDeviceListOutcome.CeilingExceeded,
                    $"More devices match these filters than the configured maximum of {ceiling}, so this report "
                    + "would be incomplete. No devices are shown and no export is offered. Raise Maximum Devices "
                    + "in Module Config, or narrow the filters.",
                    byId.Count,
                    pages,
                    ceiling);

            if (string.IsNullOrWhiteSpace(nextLink))
            {
                // The decisive test. A SHORT page with no cursor is P1/P2 - positive proof the
                // collection is exhausted. A FULL page with no cursor proves nothing at all.
                if (rows.Count >= requestedTop)
                    return DefenderDeviceListResult.Refusal(
                        DefenderDeviceListOutcome.IncompletePaging,
                        $"The Defender for Endpoint API returned exactly the {requestedTop} devices this request "
                        + "asked for and gave no continuation link, so there is no way to tell whether that is "
                        + "every matching device or only the first page of more. No devices are shown and no "
                        + "export is offered, because an unprovable list must not be presented as a complete one. "
                        + (ceiling >= MaxTop
                            ? $"Maximum Devices is {ceiling}, at or above the API's documented maximum of {MaxTop} "
                              + "devices per request, so a set this large cannot be proved complete in a single "
                              + "request. Narrow the filters."
                            : "Narrow the filters, or raise Maximum Devices in Module Config."),
                        byId.Count,
                        pages,
                        ceiling);

                break;
            }

            if (pages >= MaxRequestsPerRun)
                return DefenderDeviceListResult.Refusal(
                    DefenderDeviceListOutcome.IncompletePaging,
                    $"The Defender for Endpoint API was still returning continuation links after {pages} requests, "
                    + "which is the most one run may issue against its documented rate limit of 100 calls per "
                    + "minute. The run was stopped before it could prove it had seen every device, so nothing is "
                    + "shown and no export is offered. Narrow the filters.",
                    byId.Count,
                    pages,
                    ceiling);

            nextUrl = nextLink;

            // The continuation carries its own $top; "short in the P1 sense" is measured against
            // what THAT request asked for, not against what the first one did.
            requestedTop = ExtractTop(nextLink) ?? top;
        }

        var distinctCount = byId.Count;

        // The Windows rule is applied AFTER paging, never during it. Filtering while accumulating
        // would make a full page of non-Windows rows look short and turn the ambiguous case into a
        // false proof of exhaustion - the completion rule has to be measured against what the API
        // returned, not against what survives a client-side test.
        var devices = byId.Values
            .Where(device => !filters.WindowsOnly || IsWindows(device))
            .OrderBy(device => device.ComputerDnsName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The enrichment runs ONLY after the listing has proved itself complete, and its failure is
        // independent of the listing's success: a 403, a 429 or a timeout here leaves every device
        // row above fully rendered and turns five columns into "(unavailable)" with a reason. It
        // must never take the page down, and it must never leave those columns merely blank.
        var enrichment = devices.Count == 0
            ? DefenderDiscoveryEnrichment.NotAttempted(DefenderDiscoveryReasons.NoDevices)
            : await GetDiscoveryEnrichmentAsync();

        ApplyEnrichment(devices, enrichment);

        return DefenderDeviceListResult.CompleteRun(
            devices,
            distinctCount,
            pages,
            ceiling,
            enrichment.State,
            enrichment.Reason);
    }

    /// <summary>
    /// Runs the advanced hunting query once for the whole run and returns its rows keyed by device
    /// id, or a NAMED reason why they are not available (T7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This goes through the module-local client's PostWithStatusAsync, never
    /// GraphTokenClient.PostAsync.</b> That method is GraphTokenClient.cs:109-126 and line 122 is
    /// <c>if (!response.IsSuccessStatusCode) return null;</c> - it discards the status code and the
    /// body, so a 403 for an un-consented permission, a 429 for an exhausted quota and a 500 are all
    /// the same value. Over it, the distinct reasons below would be unimplementable and the 403 and
    /// 429 tests could not be written at all. That is the whole reason this module has a client of
    /// its own (T1).
    /// </para>
    /// <para>
    /// One query per refresh, never one per device. Graph's hunting quotas - 30 days of data,
    /// 100,000 rows, at least 45 calls per minute per tenant, a 50 MB result cap - none of which
    /// bite at one query per page load, all of which would bite at one per device.
    /// </para>
    /// </remarks>
    internal async Task<DefenderDiscoveryEnrichment> GetDiscoveryEnrichmentAsync()
    {
        if (!IncludeDiscoverySources)
            return DefenderDiscoveryEnrichment.NotAttempted(DefenderDiscoveryReasons.SwitchedOff);

        var client = await _huntingClientFactory();
        if (client == null)
            return DefenderDiscoveryEnrichment.NotAttempted(DefenderDiscoveryReasons.CredentialsUnavailable);

        var response = await client.PostWithStatusAsync(HuntingEndpoint, new { Query = HuntingQuery });

        if (response.Document == null)
            return DefenderDiscoveryEnrichment.Failed(DescribeEnrichmentFailure(response));

        using var document = response.Document;

        return TryReadHuntingResults(document, out var rows)
            ? DefenderDiscoveryEnrichment.Succeeded(rows)
            : DefenderDiscoveryEnrichment.Failed(DefenderDiscoveryReasons.MalformedResponse);
    }

    /// <summary>
    /// Copies the hunting values onto the devices that are ON THE LIST, and only those.
    /// </summary>
    /// <remarks>
    /// <b>A hunting row is not a device.</b> DeviceInfo answers for the whole tenant over 30 days,
    /// so it returns rows for devices the machines API did not list - filtered out by the operator's
    /// onboarding-status filter, out of scope for this registration, or simply gone. Those rows are
    /// DROPPED, never injected: this method iterates the devices and looks rows up, never the other
    /// way round, so a row with no device cannot become one. Inventing a device row out of hunting
    /// data would put a machine on an inventory report that the inventory API never returned.
    /// </remarks>
    private static void ApplyEnrichment(List<DefenderDevice> devices, DefenderDiscoveryEnrichment enrichment)
    {
        if (enrichment.State != DefenderDiscoveryEnrichmentState.Succeeded)
            return;

        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.Id) || !enrichment.Rows.TryGetValue(device.Id, out var row))
                continue;

            device.DiscoverySources = row.DiscoverySources;
            device.DeviceType = row.DeviceType;
            device.DeviceCategory = row.DeviceCategory;
            device.Vendor = row.Vendor;
            device.Model = row.Model;
        }
    }

    /// <summary>
    /// Reads the hunting response into rows keyed by DeviceId. False means the body was not a
    /// hunting result at all, which is a FAILED enrichment and not an empty one.
    /// </summary>
    private static bool TryReadHuntingResults(
        JsonDocument document,
        out Dictionary<string, DefenderDeviceDiscovery> rows)
    {
        rows = new Dictionary<string, DefenderDeviceDiscovery>(StringComparer.OrdinalIgnoreCase);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryGetProperty(root, "results", out var results) || results.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var entry in results.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var deviceId = GetString(entry, "DeviceId");
            if (string.IsNullOrWhiteSpace(deviceId))
                continue;

            // arg_max by DeviceId means the service already collapsed duplicates; last-wins here is
            // insurance, not a completeness argument, exactly as the device de-duplication is.
            rows[deviceId] = new DefenderDeviceDiscovery
            {
                DiscoverySources = GetFlexibleString(entry, "DiscoverySources"),
                DeviceType = GetFlexibleString(entry, "DeviceType"),
                DeviceCategory = GetFlexibleString(entry, "DeviceCategory"),
                Vendor = GetFlexibleString(entry, "Vendor"),
                Model = GetFlexibleString(entry, "Model")
            };
        }

        return true;
    }

    /// <summary>
    /// A hunting column value as display text, whatever JSON shape it arrives in.
    /// </summary>
    /// <remarks>
    /// DiscoverySources is a multi-valued column and the hunting API is not consistent about whether
    /// such a column serialises as a JSON array or as a string holding one, so both are handled and
    /// arrays are joined with "; " - the same separator the rest of this module uses for multi-value
    /// cells. Reading only the string shape would silently blank the one column the owner actually
    /// asked for, which is the failure this whole slice exists to prevent.
    /// </remarks>
    private static string GetFlexibleString(JsonElement item, string name)
    {
        if (!TryGetProperty(item, name, out var value))
            return "";

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString() ?? "";

            case JsonValueKind.Number:
                return value.GetRawText();

            case JsonValueKind.True:
            case JsonValueKind.False:
                return value.GetRawText();

            case JsonValueKind.Array:
                return string.Join("; ", value.EnumerateArray()
                    .Select(element => element.ValueKind == JsonValueKind.String
                        ? element.GetString() ?? ""
                        : element.GetRawText())
                    .Where(text => !string.IsNullOrWhiteSpace(text)));

            default:
                return "";
        }
    }

    /// <summary>
    /// Turns a failed hunting call into a reason an operator can act on. Every branch is a DIFFERENT
    /// sentence, because "discovery sources are unavailable" leaves an operator with no idea whether
    /// to chase a consent grant, wait out a quota, or retry.
    /// </summary>
    internal static string DescribeEnrichmentFailure(DefenderApiResult result)
    {
        if (result.TimedOut)
            return DefenderDiscoveryReasons.TimedOut;

        var status = result.StatusCode;
        var detail = string.IsNullOrWhiteSpace(result.SafeError) ? "" : $" Microsoft Graph said: {result.SafeError}";

        if (status == HttpStatusCode.Forbidden)
            return DefenderDiscoveryReasons.Forbidden + detail;

        if (status == HttpStatusCode.TooManyRequests)
            return DefenderDiscoveryReasons.Throttled + detail;

        if (status == HttpStatusCode.Unauthorized)
            return DefenderDiscoveryReasons.Unauthorized + detail;

        // A 2xx that produced no document means the body was there and unreadable - the client
        // already refused to parse it. "Rejected the query (200 OK)" would be nonsense; this is the
        // malformed case arriving by a different door.
        if ((int)status >= 200 && (int)status <= 299)
            return DefenderDiscoveryReasons.MalformedResponse + detail;

        if ((int)status >= 500)
            return $"Microsoft Graph was unavailable for the advanced hunting query ({(int)status} {status}), so "
                + "these columns are empty for this run. The device list itself is unaffected. Retry." + detail;

        return $"Microsoft Graph rejected the advanced hunting query ({(int)status} {status}), so these columns "
            + "are empty for this run. The device list itself is unaffected." + detail;
    }

    /// <summary>
    /// What one enrichment cell shows: the value when the query succeeded, "(unavailable)" when it
    /// did not.
    /// </summary>
    /// <remarks>
    /// The whole point of routing every enrichment cell through one function. A blank on a
    /// SUCCEEDED run means this device genuinely has no value for that column - Learn says Vendor
    /// and Model are populated only when discovery found enough - while a blank on any other state
    /// means no device has one and half the report did not execute. Those two are indistinguishable
    /// at the cell, so the run state decides, not the value.
    /// </remarks>
    public static string DescribeEnrichmentCell(DefenderDiscoveryEnrichmentState state, string? value) =>
        state != DefenderDiscoveryEnrichmentState.Succeeded
            ? EnrichmentUnavailable
            : string.IsNullOrWhiteSpace(value) ? "-" : value;

    /// <summary>
    /// "All Windows devices" has no single filter value: osPlatform carries Windows10, Windows11 and
    /// the server variants as separate values, so <c>osPlatform eq 'Windows'</c> matches nothing.
    /// Ordinal case-insensitive prefix match, client-side (T6.3).
    /// </summary>
    internal static bool IsWindows(DefenderDevice device) =>
        device.OsPlatform.StartsWith(DefenderDeviceFilters.WindowsPlatformPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The machines request. The server-side filter is onboardingStatus only; an empty status means
    /// no $filter at all. The spaces around <c>eq</c> are emitted as %20 rather than left literal, so
    /// the string this builder returns is the string that goes on the wire and a test can pin it.
    /// </summary>
    internal static string BuildMachinesUrl(DefenderDeviceFilters filters, int top)
    {
        var status = filters.OnboardingStatus?.Trim() ?? "";
        return string.IsNullOrEmpty(status)
            ? $"{MachinesEndpoint}?$top={top}"
            : $"{MachinesEndpoint}?$filter=onboardingStatus%20eq%20{ODataStringLiteral(status)}&$top={top}";
    }

    /// <summary>
    /// One OData string literal: the quotes delimiting it are SYNTAX and stay literal, and only the
    /// value is percent-escaped - after doubling any single quote it contains (T6.1).
    /// </summary>
    /// <remarks>
    /// Escaping the whole expression instead is a defect this repo has already shipped once, on a
    /// different endpoint: the parentheses, commas and delimiting quotes reach the service
    /// percent-encoded, the service answers 200, and the filter silently stops meaning what it says.
    /// </remarks>
    internal static string ODataStringLiteral(string value) =>
        $"'{Uri.EscapeDataString(value.Replace("'", "''"))}'";

    internal static int ClampMaxDevices(string? configured)
    {
        if (!int.TryParse(configured, out var value) || value <= 0)
            return DefaultMaxDevices;

        // One below int.MaxValue so that ceiling + 1 (the decisive $top) cannot overflow.
        return Math.Min(value, int.MaxValue - 1);
    }

    /// <summary>
    /// Reads the $top a continuation URL asks for, so "short in the P1 sense" is measured against
    /// that request rather than the first one. Null when the link carries no $top.
    /// </summary>
    internal static int? ExtractTop(string url)
    {
        var query = url.IndexOf('?');
        if (query < 0)
            return null;

        foreach (var pair in url[(query + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;

            var name = Uri.UnescapeDataString(pair[..eq]);
            if (!string.Equals(name, "$top", StringComparison.OrdinalIgnoreCase))
                continue;

            if (int.TryParse(Uri.UnescapeDataString(pair[(eq + 1)..]), out var parsed) && parsed > 0)
                return parsed;
        }

        return null;
    }

    /// <summary>
    /// A device id is the de-duplication key. A row without one cannot be de-duplicated, so it gets
    /// a unique synthetic key instead of being dropped: dropping it would be a silent under-report,
    /// which is the one thing this module must not do.
    /// </summary>
    private static string DeduplicationKey(DefenderDevice device, int ordinal) =>
        string.IsNullOrWhiteSpace(device.Id) ? $"(no-id):{ordinal}" : device.Id;

    private static bool TryReadPage(JsonDocument document, out List<DefenderDevice> devices, out string? nextLink)
    {
        devices = [];
        nextLink = null;

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryGetProperty(root, "value", out var value) || value.ValueKind != JsonValueKind.Array)
            return false;

        if (TryGetProperty(root, "@odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String)
            nextLink = link.GetString();

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
                devices.Add(ParseDevice(item));
        }

        return true;
    }

    private static DefenderDevice ParseDevice(JsonElement item) => new()
    {
        Id = GetString(item, "id"),
        ComputerDnsName = GetString(item, "computerDnsName"),
        OnboardingStatus = GetString(item, "onboardingStatus"),
        OsPlatform = GetString(item, "osPlatform"),
        OsVersion = GetString(item, "version"),
        OsBuild = GetString(item, "osBuild"),
        OsArchitecture = GetString(item, "osArchitecture"),
        HealthStatus = GetString(item, "healthStatus"),
        LastIpAddress = GetString(item, "lastIpAddress"),
        LastExternalIpAddress = GetString(item, "lastExternalIpAddress"),
        IpAddresses = ParseIpAddresses(item),
        FirstSeen = GetDateTimeOffset(item, "firstSeen"),
        LastSeen = GetDateTimeOffset(item, "lastSeen"),
        RiskScore = GetString(item, "riskScore"),
        ExposureLevel = GetString(item, "exposureLevel"),
        DeviceValue = GetString(item, "deviceValue"),
        MachineTags = ParseStringArray(item, "machineTags"),
        MachineGroup = GetString(item, "rbacGroupName"),
        IsAadJoined = GetBool(item, "isAadJoined"),
        AadDeviceId = GetString(item, "aadDeviceId")
    };

    private static IReadOnlyList<DefenderDeviceIpAddress> ParseIpAddresses(JsonElement item)
    {
        // Whether list responses carry ipAddresses at all is an open question settled by R1(e), so
        // an absent or null collection parses to empty rather than throwing.
        if (!TryGetProperty(item, "ipAddresses", out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        var addresses = new List<DefenderDeviceIpAddress>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            addresses.Add(new DefenderDeviceIpAddress
            {
                IpAddress = GetString(entry, "ipAddress"),
                MacAddress = GetString(entry, "macAddress"),
                OperationalStatus = GetString(entry, "operationalStatus")
            });
        }

        return addresses;
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement item, string name)
    {
        if (!TryGetProperty(item, name, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<string>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var text = entry.GetString();
                if (!string.IsNullOrEmpty(text))
                    values.Add(text);
            }
        }

        return values;
    }

    /// <summary>
    /// Case-insensitive property lookup (T6.2). Microsoft's own documentation spells these
    /// properties inconsistently - the machine resource table says <c>onboardingstatus</c>, the
    /// filterable-properties list says <c>onboardingStatus</c>, and the OData samples page shows
    /// <c>ComputerDnsName</c> and <c>OsPlatform</c> with leading capitals. Which casing the service
    /// actually emits is R1(b)'s question; reading insensitively means the answer cannot break the
    /// parser either way. Exact match is tried first so the common case costs no enumeration.
    /// </summary>
    private static bool TryGetProperty(JsonElement item, string name, out JsonElement value)
    {
        if (item.TryGetProperty(name, out value))
            return true;

        foreach (var property in item.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string GetString(JsonElement item, string name) =>
        TryGetProperty(item, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool GetBool(JsonElement item, string name) =>
        TryGetProperty(item, name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? GetDateTimeOffset(JsonElement item, string name) =>
        TryGetProperty(item, name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Turns a failed request into a reason an operator can act on. Every branch is a DIFFERENT
    /// sentence: "failed" tells an operator nothing, and a 403 here has two live causes that look
    /// identical from the outside.
    /// </summary>
    internal static string DescribeFailure(DefenderApiResult result, int pageNumber)
    {
        var chain = pageNumber > 1
            ? $" This was continuation request {pageNumber} of the run, so the devices already read are not a "
              + "complete list and are not being shown."
            : "";

        var detail = string.IsNullOrWhiteSpace(result.SafeError) ? "" : $" The API said: {result.SafeError}";

        if (result.TimedOut)
            return "The Defender for Endpoint API did not respond before the request timed out, so the device "
                + "list could not be completed." + chain;

        var status = result.StatusCode;

        if (status == HttpStatusCode.Unauthorized)
            return "The Defender for Endpoint API rejected this module's credentials (401 Unauthorized). Check "
                + "that the client secret in the module's Secret Server record has not expired." + chain + detail;

        if (status == HttpStatusCode.Forbidden)
            return "The Defender for Endpoint API refused the request (403 Forbidden). Either the app "
                + "registration is missing the Machine.Read.All application permission on WindowsDefenderATP "
                + "and its admin consent, or the access token was issued for the wrong audience - Defender "
                + "requires a token for https://api.securitycenter.microsoft.com even though the endpoint host "
                + "is https://api.security.microsoft.com." + chain + detail;

        if (status == HttpStatusCode.NotFound)
            return "A continuation page of the device list returned 404 Not Found. A 404 on the first request "
                + "means the inventory is empty, but partway through a paged run it means the continuation link "
                + "no longer resolves, so the list is incomplete and is not being shown." + chain + detail;

        if (status == HttpStatusCode.TooManyRequests)
            return "The Defender for Endpoint API throttled this request (429 Too Many Requests). The endpoint "
                + "allows 100 calls per minute and 1,500 per hour. Wait and retry." + chain + detail;

        if (status == HttpStatusCode.BadRequest)
            return "The Defender for Endpoint API rejected the request (400 Bad Request), which on this endpoint "
                + "means it would not accept the filter. This is a FAILED report, not an inventory with no "
                + "matching devices." + chain + detail;

        if ((int)status >= 500)
            return $"The Defender for Endpoint API was unavailable ({(int)status} {status}); no device list was "
                + "retrieved. Retry." + chain + detail;

        return $"The Defender for Endpoint API rejected the request ({(int)status} {status})." + chain + detail;
    }
}
