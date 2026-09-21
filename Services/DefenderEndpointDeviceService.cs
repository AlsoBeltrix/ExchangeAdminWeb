using System.Globalization;
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
    private readonly int? _pageSizeOverride;

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

    /// <summary>
    /// The MaxDevices default the descriptor declares. Raised from 20000 to 100000 in Revision 3:
    /// the old default was below the size of a real tenant, so the very first load refused on the
    /// ceiling. The ceiling is a runaway stop on how much this process will hold in memory for one
    /// operator, not a statement about how many devices are allowed to exist.
    /// </summary>
    internal const int DefaultMaxDevices = 100000;

    /// <summary>
    /// Hard stop on requests per run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raised from 100 to 250 in Revision 3, because 100 was chosen for a design that issued ONE
    /// request. The partition issues one per part: a balanced division of a 100,000-device tenant
    /// at 10,000 rows a part is 16 leaves, so 31 requests plus the no-lastSeen part. 250 leaves
    /// roughly eight times that headroom for an unbalanced division while staying a sixth of the
    /// endpoint's documented 1,500-calls-per-hour limit, so a run that hits it has not spent the
    /// tenant's hourly budget.
    /// </para>
    /// <para>
    /// It is a STOP, not a promise. The endpoint also documents 100 calls per minute, and a run
    /// that needs more than that in one minute will be throttled by the service before it gets
    /// here - which refuses with the 429 reason and its own operator action. This limit exists for
    /// the other case: a partition that terminates but does not converge, where an unbounded loop
    /// against the API is how a read module becomes an outage.
    /// </para>
    /// </remarks>
    internal const int MaxRequestsPerRun = 250;

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
    /// <param name="pageSizeOverride">
    /// The $top each request asks for, overriding the ceiling-derived value. Exercising the
    /// partition needs a response that comes back AT the cap while the ceiling stays out of the
    /// way, and the only alternative is a fixture of ten thousand and one devices per part.
    /// </param>
    internal DefenderEndpointDeviceService(
        Func<Task<DefenderApiClient?>> apiClientFactory,
        string? maxDevicesOverride = null,
        Func<Task<DefenderApiClient?>>? huntingClientFactory = null,
        string? includeDiscoverySourcesOverride = null,
        int? pageSizeOverride = null)
    {
        _apiClientFactory = apiClientFactory;
        _maxDevicesOverride = maxDevicesOverride;
        _huntingClientFactory = huntingClientFactory ?? (() => Task.FromResult<DefenderApiClient?>(null));
        _includeDiscoverySourcesOverride = includeDiscoverySourcesOverride;
        _pageSizeOverride = pageSizeOverride;
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
    /// <b>The shape of the fetch (Revision 3).</b> The live run of 2026-09-21 settled R1(g): this
    /// collection returns NO <c>@odata.nextLink</c>. There is no cursor and no continuation, so one
    /// request can never return more than the documented per-request maximum of 10,000 rows, and a
    /// tenant with more devices than that could never be listed by asking once. The fetch therefore
    /// divides the question instead of the answer: the inventory is partitioned by <c>lastSeen</c>
    /// into parts that are DISJOINT and together EXHAUSTIVE, each part is asked for on its own, and
    /// any part that comes back at the cap is split in two and asked again. The union of the parts
    /// is every matching device.
    /// </para>
    /// <para>
    /// <b>Why this is a proof and paging by <c>$skip</c> is not.</b> Nothing here depends on the
    /// service's ordering, on a stable window, or on a cursor the service issues. Each part carries
    /// its own server-side predicate; two sibling parts are <c>lastSeen lt X</c> and
    /// <c>lastSeen ge X</c> for one literal X, which no device can satisfy both of and no device
    /// with a lastSeen value can satisfy neither of; and every part proves its own completeness by
    /// the UNCHANGED P1 rule - it asked for N rows and got fewer than N, with no continuation link.
    /// A part that returns exactly N proves nothing and is split rather than believed.
    /// </para>
    /// <para>
    /// <b>The devices with no lastSeen at all.</b> Null satisfies neither <c>ge</c> nor <c>lt</c>,
    /// so intervals alone would silently omit them - the exact failure this design exists to
    /// prevent. Two things stop that. While the root part needs no dividing, it carries no lastSeen
    /// clause of any kind, so those devices are in the one request like everyone else. The moment
    /// the root has to be split, a separate part asking <c>lastSeen eq null</c> is queued - unless
    /// the OPERATOR set a Last seen window, in which case devices with no Last seen value are
    /// outside what they asked for and must not be added to the answer.
    /// </para>
    /// <para>
    /// <b>The parts are visited in ascending time order, and that is load-bearing.</b> A device
    /// reports in while the run is in flight and its lastSeen moves FORWARD. Visiting low parts
    /// first means such a device is either still in the part being read, or has moved into a part
    /// not yet read - never into one already read. The stack is pushed high-then-low so the low
    /// child pops first, and the no-lastSeen part is pushed last so it pops first of all: a device
    /// that acquires its first lastSeen mid-run lands in the final, unbounded-above part. This
    /// depends on nothing about this environment - only on lastSeen being non-decreasing for a
    /// device, which is what "last seen" means.
    /// </para>
    /// <para>
    /// <b>Termination.</b> A split point is chosen from the timestamps the ambiguous response
    /// actually returned, and only from the ones STRICTLY INSIDE the part, so both children are
    /// strictly narrower than their parent. Widths are whole numbers of ticks, so the recursion
    /// cannot descend forever; a part with no timestamp strictly inside it cannot be divided and
    /// REFUSES rather than looping. <see cref="MaxRequestsPerRun"/> is the second stop, for a
    /// partition that terminates but does not converge.
    /// </para>
    /// <para>
    /// <b>What did not change.</b> The completion rule (T3), the refusal machinery, the ceiling,
    /// the 404-is-empty reading, and the rule that a refusal carries zero devices. A partial list
    /// presented as whole is still the one result this module must never produce; what changed is
    /// that the common case no longer reaches that refusal.
    /// </para>
    /// </remarks>
    public async Task<DefenderDeviceListResult> ListDevicesAsync(DefenderDeviceFilters? filters = null)
    {
        filters ??= new DefenderDeviceFilters();

        var client = await _apiClientFactory()
            ?? throw new InvalidOperationException("Defender for Endpoint Devices credentials not available.");

        var ceiling = ClampMaxDevices(_maxDevicesOverride ?? _moduleConfig?.GetValue(ModuleId, MaxDevicesConfigKey));
        var top = PageSize(ceiling);

        // Two collections, and the second one is the invariant. byId is every distinct device SEEN,
        // which is what the ceiling is measured against; proven holds only the keys that came out of
        // a part that proved itself exhaustive. A device read from an AMBIGUOUS part is not in the
        // answer, because that part proved nothing - it is re-read by whichever child part ends up
        // proving it, so nothing is lost, and the answer is by construction the union of parts that
        // each proved themselves. Keeping ambiguous rows instead would still be correct, and would
        // quietly mask a partition whose two children do not meet: the device on the boundary would
        // already be in hand from the parent.
        var byId = new Dictionary<string, DefenderDevice>(StringComparer.OrdinalIgnoreCase);
        var proven = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requests = 0;
        var rangesCompleted = 0;

        // Depth-first, low child first. See the remarks: the visit order is part of the correctness
        // argument, not a performance choice.
        var pending = new Stack<DefenderLastSeenRange>();
        pending.Push(DefenderLastSeenRange.Between(filters.LastSeenFrom, filters.LastSeenTo));

        // Queued at the first split and never afterwards - the root part is the only one that ever
        // carried no lastSeen clause, so it is the only one whose splitting strands the nulls.
        var noLastSeenPartPending = !filters.HasLastSeenBound;

        while (pending.Count > 0)
        {
            var range = pending.Pop();

            var part = await FetchPartAsync(
                client, filters, range, top, MaxRequestsPerRun - requests, ceiling, byId);
            requests += part.Requests;

            if (part.RefusalOutcome != null)
                return DefenderDeviceListResult.Refusal(
                    part.RefusalOutcome.Value,
                    part.RefusalReason,
                    byId.Count,
                    requests,
                    rangesCompleted,
                    ceiling);

            if (!part.Ambiguous)
            {
                rangesCompleted++;
                foreach (var key in part.Keys)
                    proven.Add(key);

                continue;
            }

            // The part returned exactly what it asked for and offered no cursor, so it has proved
            // nothing. Its rows still count against the ceiling - they are real devices that matched
            // - but they are NOT promoted into the answer, and the part is asked again as two
            // narrower ones that will re-read them.
            var split = ChooseSplit(range, part.Rows);
            if (split == null)
                return DefenderDeviceListResult.Refusal(
                    DefenderDeviceListOutcome.IncompletePaging,
                    UnsplittableRefusal(range, part.Rows, part.RequestedTop),
                    byId.Count,
                    requests,
                    rangesCompleted,
                    ceiling);

            pending.Push(DefenderLastSeenRange.Between(split, range.To));
            pending.Push(DefenderLastSeenRange.Between(range.From, split));

            if (noLastSeenPartPending)
            {
                noLastSeenPartPending = false;
                pending.Push(DefenderLastSeenRange.NoLastSeen());
            }
        }

        var union = byId.Where(entry => proven.Contains(entry.Key)).Select(entry => entry.Value).ToList();
        var distinctCount = union.Count;

        // The client-side filters are applied AFTER the whole partition, never during it. Filtering
        // while accumulating would make a full response of non-matching rows look short and turn the
        // ambiguous case into a false proof of exhaustion - the completion rule has to be measured
        // against what the API returned, not against what survives a client-side test.
        var devices = union
            .Where(device => MatchesClientSideFilters(device, filters))
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
            requests,
            rangesCompleted,
            ceiling,
            enrichment.State,
            enrichment.Reason);
    }

    /// <summary>
    /// What one part of the partition returned: its rows, what it cost, the $top the LAST request
    /// in it asked for, whether it came back ambiguous, and the refusal when it failed.
    /// </summary>
    /// <param name="Ambiguous">
    /// True when the part returned exactly the rows it asked for and carried no continuation link.
    /// Not a failure and not a success - a question that has to be asked again, more narrowly.
    /// </param>
    /// <param name="RequestedTop">
    /// The $top of the request that ended the part, which is what "short" is measured against and
    /// what the refusal text quotes. A continuation link carries its own $top, so this is not
    /// always the $top the part started with.
    /// </param>
    /// <param name="Keys">
    /// The de-duplication keys this part contributed to the run's seen-device dictionary, in the
    /// order they arrived. The caller promotes them into the proven set only when the part came
    /// back unambiguous.
    /// </param>
    private sealed record PartFetch(
        List<DefenderDevice> Rows,
        List<string> Keys,
        int Requests,
        int RequestedTop,
        bool Ambiguous,
        DefenderDeviceListOutcome? RefusalOutcome,
        string RefusalReason);

    /// <summary>
    /// Asks for one part of the partition, following any continuation links it does carry.
    /// </summary>
    /// <remarks>
    /// The cursor loop is kept although the live service was observed to issue no continuation link
    /// at all (R1(g), 2026-09-21). It costs one branch, it is the only correct thing to do if the
    /// service ever starts issuing one, and deleting it would make the P2 half of the completion
    /// rule unimplementable. What it is NOT is the answer to the 10,000-row cap - that is the
    /// partition's job, and this method's ambiguity flag is how it asks for it.
    /// </remarks>
    /// <param name="requestBudget">
    /// How many requests this part may issue before the whole run gives up. Zero or less refuses
    /// immediately without calling anything, so the run's ceiling on requests is a ceiling on the
    /// RUN and not on each part.
    /// </param>
    /// <param name="byId">
    /// The run's seen-device dictionary, written to as each response arrives rather than at the end
    /// of the part. The ceiling has to be tested per RESPONSE, not per part: a cursor chain that
    /// keeps issuing continuations would otherwise run all the way to the request budget before
    /// anyone noticed the ceiling was passed on its second response.
    /// </param>
    private static async Task<PartFetch> FetchPartAsync(
        DefenderApiClient client,
        DefenderDeviceFilters filters,
        DefenderLastSeenRange range,
        int top,
        int requestBudget,
        int ceiling,
        Dictionary<string, DefenderDevice> byId)
    {
        var rows = new List<DefenderDevice>();
        var keys = new List<string>();
        var requests = 0;
        var requestedTop = top;
        var url = BuildMachinesUrl(filters, range, top);

        while (true)
        {
            if (requests >= requestBudget)
                return Refused(rows, keys, requests, requestedTop,
                    DefenderDeviceListOutcome.IncompletePaging, BudgetRefusal(range));

            var result = await client.GetWithStatusAsync(url);
            requests++;

            if (result.Document == null)
            {
                // T5, and it inverts this repo's usual rule for a reason: Learn documents 404 as the
                // EMPTY result for this collection ("If there are no recent machines, you see 404
                // Not Found"). That holds for the first request of a PART - a part covering a slice
                // of time with no devices in it is an ordinary, expected outcome of dividing the
                // inventory - and not for a 404 partway through a cursor chain, which is a broken
                // chain rather than an empty inventory and refuses below.
                if (requests == 1 && !result.TimedOut && result.StatusCode == HttpStatusCode.NotFound)
                    return new PartFetch(rows, keys, requests, requestedTop, false, null, "");

                // The one failure with an operator action of its own. Every other 400 is a filter
                // the service would not accept; this one is the request that reaches the devices no
                // interval can reach, so losing it means the union could silently miss devices.
                if (requests == 1 && range.IsNullBucket && !result.TimedOut
                    && result.StatusCode == HttpStatusCode.BadRequest)
                    return Refused(rows, keys, requests, requestedTop,
                        DefenderDeviceListOutcome.RequestFailed, NoLastSeenPartRejected(result));

                return Refused(rows, keys, requests, requestedTop,
                    DefenderDeviceListOutcome.RequestFailed, DescribeFailure(result, requests));
            }

            using var document = result.Document;

            if (!TryReadPage(document, out var page, out var nextLink))
                return Refused(rows, keys, requests, requestedTop,
                    DefenderDeviceListOutcome.RequestFailed,
                    "The Defender for Endpoint API returned a response with no device collection in it. Load "
                    + "again; report it if it keeps happening.");

            rows.AddRange(page);

            foreach (var device in page)
            {
                var key = DeduplicationKey(device, byId.Count);
                byId[key] = device;
                keys.Add(key);
            }

            if (byId.Count > ceiling)
                return Refused(rows, keys, requests, requestedTop,
                    DefenderDeviceListOutcome.CeilingExceeded, CeilingRefusal(ceiling));

            if (string.IsNullOrWhiteSpace(nextLink))
                // The decisive test, unchanged from S1. A SHORT response with no cursor is P1/P2 -
                // positive proof this part is exhausted. A FULL one proves nothing at all, and is
                // handed back as ambiguous for the caller to divide.
                return new PartFetch(rows, keys, requests, requestedTop, page.Count >= requestedTop, null, "");

            url = nextLink;

            // The continuation carries its own $top; "short in the P1 sense" is measured against what
            // THAT request asked for, not against what the first one did.
            requestedTop = ExtractTop(nextLink) ?? top;
        }
    }

    private static PartFetch Refused(
        List<DefenderDevice> rows,
        List<string> keys,
        int requests,
        int requestedTop,
        DefenderDeviceListOutcome outcome,
        string reason) => new(rows, keys, requests, requestedTop, false, outcome, reason);

    /// <summary>
    /// Where to divide a part that came back at the cap, or null when it cannot be divided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The split point comes from the DATA rather than from arithmetic on the clock, and that is
    /// what makes the partition converge. Bisecting wall-clock time between an epoch and now would
    /// spend a dozen requests walking down to the few days most devices were last seen in; the
    /// median of the timestamps a capped response actually returned lands in the middle of the
    /// population instead. The rows are an arbitrary subset of the part, so balance is not
    /// guaranteed - but correctness never depended on balance, only the request count does.
    /// </para>
    /// <para>
    /// Only values STRICTLY INSIDE the part are eligible, which is the whole termination argument:
    /// the chosen point is greater than the lower bound and less than the upper, so both children
    /// are strictly narrower than the parent. A part with no eligible value - the no-lastSeen part,
    /// a part whose rows carry no timestamp at all, or a part where every row sits exactly on the
    /// lower bound - returns null and the caller refuses. Returning the bound itself instead would
    /// produce a child identical to its parent and loop forever.
    /// </para>
    /// </remarks>
    internal static DateTimeOffset? ChooseSplit(DefenderLastSeenRange range, IReadOnlyList<DefenderDevice> rows)
    {
        if (range.IsNullBucket)
            return null;

        var inside = rows
            .Where(device => device.LastSeen.HasValue)
            .Select(device => device.LastSeen!.Value)
            .Where(value => (range.From == null || value > range.From.Value)
                && (range.To == null || value < range.To.Value))
            .OrderBy(value => value)
            .ToList();

        return inside.Count == 0 ? null : inside[inside.Count / 2];
    }

    /// <summary>
    /// The $top one request asks for: one more than the ceiling so a breach is decided in a single
    /// round trip, clamped to the documented per-request maximum.
    /// </summary>
    /// <remarks>
    /// The override exists for tests and for nothing else. Driving the partition needs a response
    /// that comes back AT the cap while the ceiling stays out of the way, and the only alternative
    /// is a fixture of ten thousand and one devices per part.
    /// </remarks>
    internal int PageSize(int ceiling) =>
        _pageSizeOverride is > 0
            ? Math.Min(_pageSizeOverride.Value, MaxTop)
            : Math.Min(ceiling + 1, MaxTop);

    /// <summary>
    /// The ceiling refusal: what happened, and the filters that would actually make the fetch
    /// smaller - plus the ones that would not, because sending an operator to narrow a filter that
    /// cannot reduce the fetch is a loop with no exit.
    /// </summary>
    internal static string CeilingRefusal(int ceiling) =>
        $"More than {ceiling} devices match. Raise Maximum Devices in Module Config, or narrow onboarding "
        + "status, platform, health status, risk score, exposure level, device name or the Last seen window. "
        + "Machine tag, machine group, First seen and Any Windows are applied after the fetch and do not "
        + "reduce it.";

    /// <summary>
    /// The refusal for a part that came back at the cap and cannot be divided any further.
    /// </summary>
    internal static string UnsplittableRefusal(
        DefenderLastSeenRange range,
        IReadOnlyList<DefenderDevice> rows,
        int requestedTop)
    {
        if (range.IsNullBucket)
            return $"More than {requestedTop} devices have no Last seen timestamp. Set a Last seen window - "
                + "From, To, or both - then load again.";

        var why = rows.Any(device => device.LastSeen.HasValue)
            ? "every device returned shares one Last seen timestamp"
            : "no device returned has a Last seen timestamp";

        return $"The API returned {requestedTop} devices for {range.Describe()} with no continuation link, and "
            + $"{why}, so that range cannot be divided further. Narrow onboarding status, platform, health "
            + "status, risk score, exposure level or device name, then load again.";
    }

    /// <summary>
    /// The refusal for a run that hit <see cref="MaxRequestsPerRun"/> with parts still unresolved.
    /// </summary>
    internal static string BudgetRefusal(DefenderLastSeenRange range) =>
        $"Stopped after {MaxRequestsPerRun} requests with {range.Describe()} still unresolved. Narrow "
        + "onboarding status, platform, health status, risk score, exposure level or device name, or set a "
        + "Last seen window, then load again.";

    /// <summary>
    /// The refusal for a service that will not accept the request reaching devices with no lastSeen.
    /// </summary>
    /// <remarks>
    /// <c>lastSeen eq null</c> is core OData v4 and this endpoint is an OData v4 surface, but it is
    /// not documented by worked example on this collection the way <c>lastSeen gt</c> and
    /// <c>startswith</c> are. If the service refuses it, the honest answer is a refusal with the
    /// operator action attached - not a list quietly missing every device the interval parts cannot
    /// reach.
    /// </remarks>
    internal static string NoLastSeenPartRejected(DefenderApiResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.SafeError) ? "" : $" The API said: {result.SafeError}";

        return "The API rejected the filter for devices with no Last seen timestamp (400 Bad Request). Set a "
            + "Last seen window - From, To, or both - then load again." + detail;
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

        DefenderApiClient? client;
        try
        {
            client = await _huntingClientFactory();
        }
        catch (Exception ex) when (IsHuntingSideFailure(ex))
        {
            // BuildClientAsync throws three ways - no Secret ID configured, the secret unreadable,
            // the fields incomplete - and the Secret Server call it makes can fail on its own. None
            // of those is a fact about the device list, which has ALREADY completed by the time this
            // runs. The reason is the fixed constant and never ex.Message: those messages name a
            // Secret ID and a Secret Server condition, and this string is rendered to an operator.
            return DefenderDiscoveryEnrichment.NotAttempted(DefenderDiscoveryReasons.CredentialsUnavailable);
        }

        if (client == null)
            return DefenderDiscoveryEnrichment.NotAttempted(DefenderDiscoveryReasons.CredentialsUnavailable);

        DefenderApiResult response;
        try
        {
            response = await client.PostWithStatusAsync(HuntingEndpoint, new { Query = HuntingQuery });
        }
        catch (Exception ex) when (IsHuntingSideFailure(ex))
        {
            // PostWithStatusAsync turns a STATUS into a result, but a request that never received a
            // status still throws: DefenderApiClient.cs throws on a token request that came back
            // non-2xx, and its send catches only TaskCanceledException, so a transport failure or an
            // unreadable token body escapes it. Fixed constant again - the token exception carries a
            // status and a transport exception can carry a host name and a certificate subject.
            return DefenderDiscoveryEnrichment.Failed(DefenderDiscoveryReasons.SendFailed);
        }

        if (response.Document == null)
            return DefenderDiscoveryEnrichment.Failed(DescribeEnrichmentFailure(response));

        using var document = response.Document;

        // Deliberately OUTSIDE both catches. Reading the response and building the row dictionary is
        // this module's own code, not the hunting side: a NullReferenceException or a duplicate-key
        // ArgumentException in there is a BUG, and a bug that greys five columns and says "the
        // request failed" is a bug nobody ever finds. Only the two calls that leave this process are
        // wrapped, and each is wrapped alone so the two reasons cannot borrow each other's sentence.
        return TryReadHuntingResults(document, out var rows)
            ? DefenderDiscoveryEnrichment.Succeeded(rows)
            : DefenderDiscoveryEnrichment.Failed(DefenderDiscoveryReasons.MalformedResponse);
    }

    /// <summary>
    /// The exception types a hunting-side failure actually arrives as, and nothing wider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not <c>catch (Exception)</c>.</b> A blanket catch around the enrichment would also swallow
    /// a defect in this module's own parsing and merging code and report it to the operator as a
    /// Graph failure - the reason for the filter, and the reason the read of the response body sits
    /// outside both try blocks. Anything not on this list still escapes and still takes the page
    /// down, which is what an unexpected exception should do.
    /// </para>
    /// <para>
    /// Each entry is a path that exists today, not a defensive guess:
    /// <list type="bullet">
    ///   <item><description><see cref="InvalidOperationException"/> - BuildClientAsync's three
    ///   credential throws, and DefenderApiClient's throw on a token request that came back
    ///   non-2xx.</description></item>
    ///   <item><description><see cref="HttpRequestException"/> - DNS, connect and TLS failures,
    ///   which HttpClient throws and the client's own send does not catch.</description></item>
    ///   <item><description><see cref="JsonException"/> - a token response body that is not JSON;
    ///   the hunting response body's parse is already guarded inside the client.</description></item>
    ///   <item><description><see cref="KeyNotFoundException"/> - a 200 token response carrying no
    ///   access_token property.</description></item>
    ///   <item><description><see cref="TaskCanceledException"/> - an HttpClient timeout on the
    ///   Secret Server read. On the POST side the client already converts one into the TimedOut
    ///   reason, so this entry only bites on the factory.</description></item>
    /// </list>
    /// Catching TaskCanceledException is safe here only because no CancellationToken is plumbed
    /// through this call chain, so it cannot be a caller's cancellation being swallowed. If one is
    /// ever added, this entry has to be revisited.
    /// </para>
    /// </remarks>
    private static bool IsHuntingSideFailure(Exception ex) =>
        ex is InvalidOperationException
            or HttpRequestException
            or JsonException
            or KeyNotFoundException
            or TaskCanceledException;

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
            return $"Microsoft Graph was unavailable for the advanced hunting query ({(int)status} {status}). "
                + "Load again." + detail;

        return $"Microsoft Graph rejected the advanced hunting query ({(int)status} {status})." + detail;
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
    /// The filters the API cannot apply, applied to the complete set once it is in hand.
    /// </summary>
    /// <remarks>
    /// Each one is here because of a documented fact about the endpoint, recorded on the property
    /// it belongs to in <see cref="DefenderDeviceFilters"/>: firstSeen is not in Learn's filterable
    /// list at all, rbacGroupName is not filterable (only the numeric rbacGroupId is), a tag filter
    /// would be an undocumented collection query, and there is no osPlatform value meaning "any
    /// Windows". Nothing is here for convenience, and the page labels every one of them.
    /// </remarks>
    internal static bool MatchesClientSideFilters(DefenderDevice device, DefenderDeviceFilters filters)
    {
        if (!string.IsNullOrWhiteSpace(filters.OsPlatformPrefix)
            && !device.OsPlatform.StartsWith(filters.OsPlatformPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(filters.MachineTag)
            && !device.MachineTags.Any(tag => tag.Contains(filters.MachineTag, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!string.IsNullOrWhiteSpace(filters.MachineGroup)
            && !device.MachineGroup.Contains(filters.MachineGroup, StringComparison.OrdinalIgnoreCase))
            return false;

        // A device with no firstSeen fails a firstSeen bound rather than passing it. The operator
        // asked for devices first seen inside a window, and "no value" is not a yes; letting it
        // through would put devices in the answer that were never shown to satisfy the question.
        if (filters.FirstSeenFrom.HasValue
            && (!device.FirstSeen.HasValue || device.FirstSeen.Value < filters.FirstSeenFrom.Value))
            return false;

        if (filters.FirstSeenTo.HasValue
            && (!device.FirstSeen.HasValue || device.FirstSeen.Value >= filters.FirstSeenTo.Value))
            return false;

        return true;
    }

    /// <summary>
    /// The machines request for one part of the partition: every server-side filter, the part's own
    /// lastSeen clause, and <c>$top</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The properties this builder is allowed to name come from Learn's List machines page, which
    /// lists what <c>$filter</c> accepts on this collection: computerDnsName, id, version,
    /// deviceValue, aadDeviceId, machineTags, lastSeen, exposureLevel, onboardingStatus,
    /// lastIpAddress, healthStatus, osPlatform, riskScore, rbacGroupId. A clause naming anything
    /// else is a 400 at best and a silently wrong 200 at worst.
    /// </para>
    /// <para>
    /// The spaces around the operators are emitted as %20 rather than left literal, so the string
    /// this builder returns is the string that goes on the wire and a test can pin it.
    /// </para>
    /// </remarks>
    internal static string BuildMachinesUrl(DefenderDeviceFilters filters, DefenderLastSeenRange range, int top)
    {
        var clauses = new List<string>();

        AddEqualsClause(clauses, "onboardingStatus", filters.OnboardingStatus);
        AddEqualsClause(clauses, "osPlatform", filters.OsPlatform);
        AddEqualsClause(clauses, "healthStatus", filters.HealthStatus);
        AddEqualsClause(clauses, "riskScore", filters.RiskScore);
        AddEqualsClause(clauses, "exposureLevel", filters.ExposureLevel);

        var namePrefix = filters.DeviceNameStartsWith?.Trim() ?? "";
        if (namePrefix.Length > 0)
            clauses.Add($"startswith(computerDnsName,{ODataStringLiteral(namePrefix)})");

        if (range.IsNullBucket)
        {
            clauses.Add("lastSeen eq null");
        }
        else
        {
            if (range.From.HasValue)
                clauses.Add($"lastSeen ge {ODataDateTimeLiteral(range.From.Value)}");

            if (range.To.HasValue)
                clauses.Add($"lastSeen lt {ODataDateTimeLiteral(range.To.Value)}");
        }

        if (clauses.Count == 0)
            return $"{MachinesEndpoint}?$top={top}";

        // Only SYNTAX spaces survive to this point - every operator-supplied value went through
        // ODataStringLiteral, which percent-escapes its own contents - so replacing them wholesale
        // cannot touch a value.
        var filter = string.Join(" and ", clauses).Replace(" ", "%20");
        return $"{MachinesEndpoint}?$filter={filter}&$top={top}";
    }

    private static void AddEqualsClause(List<string> clauses, string property, string? value)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length > 0)
            clauses.Add($"{property} eq {ODataStringLiteral(trimmed)}");
    }

    /// <summary>
    /// One OData date-time literal: UNQUOTED, UTC, and at the tick precision the service itself
    /// emits (Learn's own machine samples carry seven fractional digits).
    /// </summary>
    /// <remarks>
    /// Unquoted is not a style choice - a quoted date is a string literal in OData v4 and will not
    /// compare against a DateTimeOffset property. Tick precision matters for a different reason: the
    /// same literal string is the low child's upper bound and the high child's lower bound, so any
    /// rounding applies identically to both and the partition stays disjoint and exhaustive
    /// whatever the precision. What rounding WOULD cost is convergence, and there is no need to pay
    /// it when the service round-trips this precision already.
    /// </remarks>
    internal static string ODataDateTimeLiteral(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

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
        var chain = pageNumber > 1 ? $" Failed on request {pageNumber} of this part." : "";
        var detail = string.IsNullOrWhiteSpace(result.SafeError) ? "" : $" The API said: {result.SafeError}";

        if (result.TimedOut)
            return "The Defender for Endpoint API did not respond before the request timed out. Load again."
                + chain;

        var status = result.StatusCode;

        if (status == HttpStatusCode.Unauthorized)
            return "The Defender for Endpoint API rejected the credentials (401 Unauthorized). Check that the "
                + "client secret in the Secret Server record has not expired." + chain + detail;

        if (status == HttpStatusCode.Forbidden)
            return "The Defender for Endpoint API refused the request (403 Forbidden). Grant the app "
                + "registration Machine.Read.All on WindowsDefenderATP with admin consent, or - if it already "
                + "holds it - check that the token audience is https://api.securitycenter.microsoft.com."
                + chain + detail;

        if (status == HttpStatusCode.NotFound)
            return "A continuation link in the device list returned 404 Not Found. Load again." + chain + detail;

        if (status == HttpStatusCode.TooManyRequests)
            return "The Defender for Endpoint API throttled this request (429 Too Many Requests). The endpoint "
                + "allows 100 calls per minute and 1,500 per hour. Wait, then load again." + chain + detail;

        if (status == HttpStatusCode.BadRequest)
            return "The Defender for Endpoint API would not accept the filter (400 Bad Request). Change the "
                + "filters, then load again." + chain + detail;

        if ((int)status >= 500)
            return $"The Defender for Endpoint API was unavailable ({(int)status} {status}). Load again."
                + chain + detail;

        return $"The Defender for Endpoint API rejected the request ({(int)status} {status})." + chain + detail;
    }
}
