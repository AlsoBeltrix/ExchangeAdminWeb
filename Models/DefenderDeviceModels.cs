using System.Globalization;

namespace ExchangeAdminWeb.Models;

/// <summary>
/// One entry of a Defender for Endpoint machine's <c>ipAddresses</c> collection
/// (docs/DefenderEndpointDevices-Plan.md, Field mapping). MAC addresses arrive here rather than as
/// a top-level property, which is why the CSV's MacAddresses column is sourced from this list.
/// </summary>
/// <remarks>
/// Whether list responses populate ipAddresses at all is Assumption 3 in the plan and is settled by
/// R1(e), not here: every property is optional and an absent collection parses to empty rather than
/// throwing.
/// </remarks>
public sealed class DefenderDeviceIpAddress
{
    public string IpAddress { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string OperationalStatus { get; set; } = "";
}

/// <summary>
/// A Defender for Endpoint <c>machine</c>, narrowed to the fields the module's table and CSV use
/// (docs/DefenderEndpointDevices-Plan.md, Field mapping). Read-only data: nothing in this module
/// writes a machine, and the plan's T4 keeps the registration off Machine.ReadWrite.All by never
/// calling Get-machine-by-id - the detail view renders from the row the list already returned.
/// </summary>
/// <remarks>
/// There is deliberately no AD domain or OU property. The machine resource exposes no domain at
/// all; DnsDomain below is DERIVED from computerDnsName and is a DNS suffix, not directory
/// membership. Naming it accurately here is what stops a later reader from presenting it as AD
/// domain membership on the page.
/// </remarks>
public sealed class DefenderDevice
{
    public string Id { get; set; } = "";
    public string ComputerDnsName { get; set; } = "";
    public string OnboardingStatus { get; set; } = "";
    public string OsPlatform { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string OsBuild { get; set; } = "";
    public string OsArchitecture { get; set; } = "";
    public string HealthStatus { get; set; } = "";
    public string LastIpAddress { get; set; } = "";
    public string LastExternalIpAddress { get; set; } = "";
    public IReadOnlyList<DefenderDeviceIpAddress> IpAddresses { get; set; } = [];
    public DateTimeOffset? FirstSeen { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public string RiskScore { get; set; } = "";
    public string ExposureLevel { get; set; } = "";
    public string DeviceValue { get; set; } = "";
    public IReadOnlyList<string> MachineTags { get; set; } = [];
    public string MachineGroup { get; set; } = "";
    public bool IsAadJoined { get; set; }
    public string AadDeviceId { get; set; } = "";

    /// <summary>
    /// Products or services that have seen or reported this device - the portal's "discovery
    /// sources" column. NOT from the machines API: it comes from the advanced hunting DeviceInfo
    /// table and is merged in by device id after the listing completes (S4).
    /// </summary>
    /// <remarks>
    /// <b>An empty value here means two different things and the caller must not conflate them.</b>
    /// Either the hunting query ran and this device had no DeviceInfo row, or the hunting query
    /// never ran or failed and NO device has a value. The second case is a half-executed report and
    /// must read "(unavailable)" rather than blank, which is why the answer lives on the RESULT
    /// (<see cref="DefenderDeviceListResult.DiscoveryEnrichment"/>) and not on the device: a
    /// per-device blank cannot tell them apart, and Known Failure Class 2 in
    /// .agents/repo-guidance.md is exactly the shape where a blank cell reports a failed run.
    /// </remarks>
    public string DiscoverySources { get; set; } = "";

    /// <summary>DeviceInfo.DeviceType, merged in by S4. See <see cref="DiscoverySources"/> for what
    /// an empty value does and does not mean.</summary>
    public string DeviceType { get; set; } = "";

    /// <summary>DeviceInfo.DeviceCategory, merged in by S4.</summary>
    public string DeviceCategory { get; set; } = "";

    /// <summary>
    /// DeviceInfo.Vendor, merged in by S4. Learn flags this as populated "only available if device
    /// discovery finds enough information about this attribute", so blanks are expected even on a
    /// wholly successful run.
    /// </summary>
    public string Vendor { get; set; } = "";

    /// <summary>DeviceInfo.Model, merged in by S4. Same "expect blanks" caveat as
    /// <see cref="Vendor"/>.</summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// The DNS suffix of <see cref="ComputerDnsName"/> - everything after the first dot, empty when
    /// there is no dot. Derived, never read from the API: the machine resource has no domain
    /// property. Environment-neutral by construction (repo-guidance invariant 7) - no suffix is
    /// known, defaulted or validated, whatever the API returns is what is shown.
    /// </summary>
    public string DnsDomain
    {
        get
        {
            var dot = ComputerDnsName.IndexOf('.');
            return dot < 0 || dot == ComputerDnsName.Length - 1 ? "" : ComputerDnsName[(dot + 1)..];
        }
    }
}

/// <summary>
/// Why a device listing run ended. Exactly one value means the returned set is complete and may be
/// rendered or exported; every other value is a REFUSAL and carries no devices at all
/// (docs/DefenderEndpointDevices-Plan.md T3, "the report is complete or it refuses").
/// </summary>
public enum DefenderDeviceListOutcome
{
    /// <summary>
    /// Positive proof of exhaustion - P1 (one request, short, no continuation cursor) or P2 (a
    /// cursor chain whose final response was short and cursor-free) for EVERY part of the Last seen
    /// partition, where the parts are disjoint and together cover every device. The only value that
    /// may be rendered as a device list.
    /// </summary>
    Complete,

    /// <summary>More devices matched than the configured MaxDevices ceiling.</summary>
    CeilingExceeded,

    /// <summary>
    /// A part of the inventory returned exactly the $top it asked for and carried no continuation
    /// cursor - byte-identical whether exactly that many devices exist or the service capped the
    /// request - and the run could not resolve it: either that part cannot be divided any further
    /// on the Last seen axis, or the run exhausted its request budget before every part was proved
    /// complete. Completeness is unprovable either way, so the run refuses rather than guessing.
    /// </summary>
    IncompletePaging,

    /// <summary>A request in the chain failed (403, 429, 5xx, a mid-chain 404, or a timeout).</summary>
    RequestFailed
}

/// <summary>
/// One row of the advanced hunting DeviceInfo projection (S4) - the five enrichment values for one
/// device, keyed outside this type by its DeviceId.
/// </summary>
/// <remarks>
/// A type of its own rather than a partly filled <see cref="DefenderDevice"/>, because a hunting row
/// is NOT a device: rows arrive for devices the machines API never listed, and those are dropped
/// rather than injected. Keeping the two shapes distinct is what makes that a compile-time fact
/// instead of a review comment.
/// </remarks>
public sealed class DefenderDeviceDiscovery
{
    public string DiscoverySources { get; set; } = "";
    public string DeviceType { get; set; } = "";
    public string DeviceCategory { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Model { get; set; } = "";
}

/// <summary>
/// Whether the discovery-source enrichment columns hold real data.
/// </summary>
/// <remarks>
/// The distinction this enum exists for: <see cref="Succeeded"/> with an empty cell means that
/// device genuinely has no value, and anything else means NO device has one and the cell must read
/// "(unavailable)". A boolean could not carry that, and a blank cell cannot report a failed run.
/// </remarks>
public enum DefenderDiscoveryEnrichmentState
{
    /// <summary>
    /// The hunting query did not run - the Include Discovery Sources switch is off, the module's
    /// credentials were unavailable, no devices matched, or the listing itself refused.
    /// </summary>
    NotAttempted,

    /// <summary>The hunting query ran and its columns are populated.</summary>
    Succeeded,

    /// <summary>The hunting query was attempted and failed; the reason names which failure.</summary>
    Failed
}

/// <summary>
/// The outcome of one advanced hunting enrichment attempt: its state, the reason when there is one,
/// and the rows it produced keyed by device id.
/// </summary>
/// <remarks>
/// The factories are the invariant. A non-succeeded enrichment carries ZERO rows, and a succeeded
/// one carries an EMPTY reason - so there is no value of this type that both claims a failure and
/// offers data to merge, and none that populates columns while telling the operator they are
/// unavailable.
/// </remarks>
public sealed class DefenderDiscoveryEnrichment
{
    public DefenderDiscoveryEnrichmentState State { get; private init; }

    /// <summary>Why the columns read "(unavailable)". Empty when <see cref="State"/> is Succeeded.</summary>
    public string Reason { get; private init; } = "";

    /// <summary>The hunting rows by device id. Always empty unless <see cref="State"/> is Succeeded.</summary>
    public IReadOnlyDictionary<string, DefenderDeviceDiscovery> Rows { get; private init; }
        = new Dictionary<string, DefenderDeviceDiscovery>();

    public static DefenderDiscoveryEnrichment Succeeded(IReadOnlyDictionary<string, DefenderDeviceDiscovery> rows) =>
        new() { State = DefenderDiscoveryEnrichmentState.Succeeded, Reason = "", Rows = rows };

    public static DefenderDiscoveryEnrichment NotAttempted(string reason) =>
        new() { State = DefenderDiscoveryEnrichmentState.NotAttempted, Reason = reason };

    public static DefenderDiscoveryEnrichment Failed(string reason) =>
        new() { State = DefenderDiscoveryEnrichmentState.Failed, Reason = reason };
}

/// <summary>
/// Why the five discovery-source columns read "(unavailable)" for a run
/// (docs/DefenderEndpointDevices-Plan.md T7, S4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every string here is a different sentence naming a different operator action, and
/// DefenderEndpointDeviceServiceTests pins that no two of them collapse.</b> "Not available" tells
/// an operator nothing. "Nobody consented the permission", "the tenant's hunting quota is
/// exhausted" and "the query timed out" are three different problems with three different
/// responses, and an operator looking at a column of "(unavailable)" has to be able to tell which
/// one they have.
/// </para>
/// <para>
/// They live on the model rather than on the service because the refusal factory below uses one of
/// them and the service uses the rest; one copy of each string is the only way the two cannot drift
/// apart.
/// </para>
/// </remarks>
public static class DefenderDiscoveryReasons
{
    /// <summary>The Include Discovery Sources config switch is off.</summary>
    public const string SwitchedOff =
        "Include Discovery Sources is turned off in Module Config. Turn it on once the app "
        + "registration holds ThreatHunting.Read.All on Microsoft Graph.";

    /// <summary>The module's own credentials could not be built, so nothing was called.</summary>
    public const string CredentialsUnavailable =
        "App credentials could not be built. Check Graph App Delinea Secret ID in "
        + "Module Config.";

    /// <summary>No devices matched, so there was nothing to enrich.</summary>
    public const string NoDevices = "No devices matched, so there was nothing to enrich.";

    /// <summary>The device listing itself refused, so enrichment was never reached.</summary>
    public const string ListRefused = "The device list did not complete.";

    /// <summary>A 2xx whose body carried no readable results collection.</summary>
    public const string MalformedResponse =
        "Microsoft Graph returned an advanced hunting response with no results collection. Load "
        + "again; report it if it keeps happening.";

    /// <summary>403: the permission was never granted, or consent was never given for it.</summary>
    public const string Forbidden =
        "Microsoft Graph refused the advanced hunting query (403 Forbidden). Grant the app "
        + "registration ThreatHunting.Read.All on Microsoft Graph, with admin consent from a "
        + "Privileged Role Administrator or a Global Administrator.";

    /// <summary>429: the tenant's advanced hunting CPU quota is exhausted.</summary>
    public const string Throttled =
        "Microsoft Graph throttled the advanced hunting query (429 Too Many Requests): the tenant's "
        + "advanced hunting quota is exhausted. Wait, then load again.";

    /// <summary>A client-side timeout, which arrives as an exception and not as a status.</summary>
    public const string TimedOut =
        "The advanced hunting query timed out. Load again, and narrow the filters if it keeps "
        + "happening.";

    /// <summary>
    /// The request threw before any status came back - a sign-in that was refused, a transport
    /// failure, or a token response that could not be read. There is no status to name, which is
    /// exactly what separates this from <see cref="Unauthorized"/> and the 5xx text.
    /// </summary>
    /// <remarks>
    /// Named SendFailed and deliberately NOT RequestFailed:
    /// <see cref="DefenderDeviceListOutcome.RequestFailed"/> already owns that word in this module
    /// and means the DEVICE LIST failed, which is the one thing this reason promises did not happen.
    /// </remarks>
    public const string SendFailed =
        "The advanced hunting query never reached Microsoft Graph. Check that the app registration "
        + "can sign in and that this server can reach graph.microsoft.com.";

    /// <summary>401: the credentials themselves were rejected.</summary>
    public const string Unauthorized =
        "Microsoft Graph rejected the credentials for the advanced hunting query (401 Unauthorized). "
        + "Check that the client secret in the Secret Server record has not expired.";
}

/// <summary>
/// The outcome of one device listing run (docs/DefenderEndpointDevices-Plan.md S1, Revision 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>There is deliberately no Truncated flag.</b> The only two outcomes are a complete set and a
/// refusal (T3). A flag saying "some of the data" is how a partial result gets rendered as a table
/// by the next person to touch the page, and a partial CSV handed to someone else carries no hint
/// that it was cut short.
/// </para>
/// <para>
/// The invariant this type enforces in its own factories: a refusal carries ZERO devices. A caller
/// that ignores <see cref="Outcome"/> and binds <see cref="Devices"/> straight to a table renders
/// an empty table on a refusal - wrong, but not a silent under-report presented as the answer.
/// </para>
/// </remarks>
public sealed class DefenderDeviceListResult
{
    /// <summary>Why the run ended. Only Complete may be rendered or exported.</summary>
    public DefenderDeviceListOutcome Outcome { get; private init; }

    /// <summary>
    /// The devices to show, after the client-side filters. Always empty unless
    /// <see cref="Outcome"/> is <see cref="DefenderDeviceListOutcome.Complete"/>.
    /// </summary>
    public IReadOnlyList<DefenderDevice> Devices { get; private init; } = [];

    /// <summary>
    /// Distinct devices the API returned across the whole run, de-duplicated on device id and
    /// counted BEFORE the client-side filters. This is the number the ceiling is measured against,
    /// because the fetch is what has to be bounded.
    /// </summary>
    public int DistinctCount { get; private init; }

    /// <summary>How many HTTP requests the run issued, including the one that refused.</summary>
    public int PagesFetched { get; private init; }

    /// <summary>
    /// How many parts of the Last seen partition were proved complete
    /// (docs/DefenderEndpointDevices-Plan.md Revision 3). One on a small inventory that needed no
    /// dividing; one per leaf part, plus the no-Last-seen part, on a large one. Zero on a refusal
    /// that never proved a part.
    /// </summary>
    /// <remarks>
    /// Reported to the operator rather than kept internal, because it is the only visible evidence
    /// that the partition ran at all: a run that says "48112 devices in 17 requests across 9 Last
    /// seen ranges" has demonstrably not answered out of one capped request.
    /// </remarks>
    public int RangesCompleted { get; private init; }

    /// <summary>True when the run refused because more devices matched than <see cref="Ceiling"/>.</summary>
    public bool CeilingExceeded => Outcome == DefenderDeviceListOutcome.CeilingExceeded;

    /// <summary>The MaxDevices ceiling in force for this run.</summary>
    public int Ceiling { get; private init; }

    /// <summary>
    /// Why the run refused, in words an operator can act on. Empty when <see cref="Outcome"/> is
    /// Complete. Never contains a token, a secret or a raw response body.
    /// </summary>
    public string RefusalReason { get; private init; } = "";

    public DefenderDiscoveryEnrichmentState DiscoveryEnrichment { get; private init; }
        = DefenderDiscoveryEnrichmentState.NotAttempted;

    /// <summary>Why the enrichment columns read "(unavailable)". Empty when they are populated.</summary>
    public string DiscoveryEnrichmentReason { get; private init; } = "";

    public static DefenderDeviceListResult CompleteRun(
        IReadOnlyList<DefenderDevice> devices,
        int distinctCount,
        int pagesFetched,
        int rangesCompleted,
        int ceiling,
        DefenderDiscoveryEnrichmentState enrichment,
        string enrichmentReason) => new()
        {
            Outcome = DefenderDeviceListOutcome.Complete,
            Devices = devices,
            DistinctCount = distinctCount,
            PagesFetched = pagesFetched,
            RangesCompleted = rangesCompleted,
            Ceiling = ceiling,
            DiscoveryEnrichment = enrichment,
            DiscoveryEnrichmentReason = enrichmentReason
        };

    /// <summary>
    /// A refusal. Takes no device list on purpose: the rows fetched so far are not an answer, and
    /// the way to guarantee they are never rendered as one is for them not to be in the result.
    /// </summary>
    public static DefenderDeviceListResult Refusal(
        DefenderDeviceListOutcome outcome,
        string reason,
        int distinctCount,
        int pagesFetched,
        int rangesCompleted,
        int ceiling)
    {
        if (outcome == DefenderDeviceListOutcome.Complete)
            throw new ArgumentException("A refusal cannot carry the Complete outcome.", nameof(outcome));

        return new DefenderDeviceListResult
        {
            Outcome = outcome,
            Devices = [],
            DistinctCount = distinctCount,
            PagesFetched = pagesFetched,
            RangesCompleted = rangesCompleted,
            Ceiling = ceiling,
            RefusalReason = reason,
            DiscoveryEnrichment = DefenderDiscoveryEnrichmentState.NotAttempted,
            DiscoveryEnrichmentReason = DefenderDiscoveryReasons.ListRefused
        };
    }
}

/// <summary>
/// One part of the Last seen partition: a half-open interval from <see cref="From"/> up to but not
/// including <see cref="To"/>, with either end optionally unbounded - or the part holding the
/// devices that carry no Last seen value at all
/// (docs/DefenderEndpointDevices-Plan.md Revision 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a type rather than a pair of nullable dates.</b> The three shapes are not
/// interchangeable and the difference between them is the whole correctness argument. An unbounded
/// end means "no clause at all on that side", which is how the partition covers timestamps older
/// than anything retained and newer than anything yet written, without naming an epoch or a
/// horizon. And the no-Last-seen part is NOT an interval: null satisfies neither <c>ge</c> nor
/// <c>lt</c>, so a partition assembled only from intervals silently omits every device whose
/// lastSeen is absent. Making that part a distinct, constructible state is what stops it being
/// forgotten.
/// </para>
/// <para>
/// Half-open, always. The low child of a split ends EXCLUSIVE at the split point and the high child
/// starts INCLUSIVE at it, so two children exactly cover their parent with no overlap and - the
/// part that matters - no gap. A device whose lastSeen is exactly the split point belongs to the
/// high child and to nothing else.
/// </para>
/// </remarks>
public readonly record struct DefenderLastSeenRange
{
    private DefenderLastSeenRange(DateTimeOffset? from, DateTimeOffset? to, bool isNullBucket)
    {
        From = from;
        To = to;
        IsNullBucket = isNullBucket;
    }

    /// <summary>Inclusive lower bound, or null for no lower bound at all.</summary>
    public DateTimeOffset? From { get; }

    /// <summary>Exclusive upper bound, or null for no upper bound at all.</summary>
    public DateTimeOffset? To { get; }

    /// <summary>
    /// True for the part that asks for devices with no lastSeen value. Mutually exclusive with
    /// every interval part, and never produced by a split.
    /// </summary>
    public bool IsNullBucket { get; }

    /// <summary>An interval part. Either or both ends may be null, meaning unbounded on that side.</summary>
    public static DefenderLastSeenRange Between(DateTimeOffset? from, DateTimeOffset? to) =>
        new(from, to, isNullBucket: false);

    /// <summary>The part holding devices whose lastSeen is absent or null.</summary>
    public static DefenderLastSeenRange NoLastSeen() => new(null, null, isNullBucket: true);

    /// <summary>The part in words, so a refusal can name which part of the inventory defeated it.</summary>
    public string Describe()
    {
        if (IsNullBucket)
            return "devices with no Last seen timestamp";

        if (From == null && To == null)
            return "all devices";

        if (To == null)
            return $"Last seen from {Format(From!.Value)} UTC";

        if (From == null)
            return $"Last seen before {Format(To.Value)} UTC";

        return $"Last seen {Format(From.Value)} to {Format(To.Value)} UTC";
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// The operator-changeable filters for one listing run (docs/DefenderEndpointDevices-Plan.md
/// Revision 3; S2 renders the controls, S1 owns their meaning).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every property here is either server-side or client-side, and which one it is is a fact about
/// the API rather than a preference.</b> Learn's List machines page names the properties
/// <c>$filter</c> accepts on this collection: computerDnsName, id, version, deviceValue,
/// aadDeviceId, machineTags, lastSeen, exposureLevel, onboardingStatus, lastIpAddress, healthStatus,
/// osPlatform, riskScore, rbacGroupId. Anything not on that list cannot be pushed to the service,
/// and pretending otherwise is how a filter that looks server-side quietly fetches the whole tenant.
/// </para>
/// <list type="table">
///   <item><term>Server-side</term><description><see cref="OnboardingStatus"/>,
///   <see cref="OsPlatform"/>, <see cref="HealthStatus"/>, <see cref="RiskScore"/>,
///   <see cref="ExposureLevel"/>, <see cref="DeviceNameStartsWith"/>, <see cref="LastSeenFrom"/>,
///   <see cref="LastSeenTo"/>.</description></item>
///   <item><term>Client-side</term><description><see cref="OsPlatformPrefix"/>,
///   <see cref="MachineTag"/>, <see cref="MachineGroup"/>, <see cref="FirstSeenFrom"/>,
///   <see cref="FirstSeenTo"/> - each carrying the reason on its own member.</description></item>
/// </list>
/// <para>
/// A client-side filter narrows what is SHOWN and never what is FETCHED, so it cannot rescue a run
/// that is refusing on the ceiling. The refusal text says so in as many words, because "Narrow the
/// filters" was useless advice the one time the module gave it.
/// </para>
/// </remarks>
public sealed record DefenderDeviceFilters
{
    /// <summary>
    /// The API literal for the state the portal labels "Can be onboarded" - verified from the
    /// machine resource's onboardingStatus enumeration, which is <c>onboarded</c>,
    /// <c>CanBeOnboarded</c>, <c>Unsupported</c>, <c>InsufficientInfo</c>.
    /// </summary>
    public const string CanBeOnboarded = "CanBeOnboarded";

    /// <summary>
    /// The osPlatform prefix the "Any Windows" platform choice matches. Not a filter value - a
    /// prefix, and applied client-side. See <see cref="OsPlatformPrefix"/>.
    /// </summary>
    public const string WindowsPlatformPrefix = "Windows";

    /// <summary>
    /// SERVER-SIDE. The API literal, NOT the portal label: the portal shows "Can be onboarded" and
    /// the REST value is <c>CanBeOnboarded</c>; sending the portal spelling matches nothing. Empty
    /// means no onboarding clause at all. Escaped as an OData string literal (T6.1).
    /// </summary>
    public string OnboardingStatus { get; init; } = CanBeOnboarded;

    /// <summary>
    /// SERVER-SIDE, exact. One osPlatform value - Windows10, Windows11, WindowsServer2019, Linux,
    /// macOS and so on - compared with <c>eq</c>. Empty means no platform clause.
    /// </summary>
    public string OsPlatform { get; init; } = "";

    /// <summary>
    /// CLIENT-SIDE, prefix. There is no single osPlatform value meaning "any Windows": the property
    /// carries Windows10, Windows11 and each server edition as separate values, so
    /// <c>osPlatform eq 'Windows'</c> matches nothing. <c>startswith</c> is documented on this
    /// collection for computerDnsName and for no other property, so pushing
    /// <c>startswith(osPlatform,'Windows')</c> to the service would be a guess - and a guess the
    /// service answers 200 to while matching nothing is indistinguishable from an empty inventory.
    /// Applied here instead, over the complete set, where it is correct whatever the service would
    /// have done with it.
    /// </summary>
    public string OsPlatformPrefix { get; init; } = "";

    /// <summary>SERVER-SIDE. Active, Inactive, ImpairedCommunication, NoSensorData,
    /// NoSensorDataImpairedCommunication, Unknown. Empty means no clause.</summary>
    public string HealthStatus { get; init; } = "";

    /// <summary>SERVER-SIDE. None, Informational, Low, Medium, High. Empty means no clause.</summary>
    public string RiskScore { get; init; } = "";

    /// <summary>SERVER-SIDE. None, Low, Medium, High. Empty means no clause.</summary>
    public string ExposureLevel { get; init; } = "";

    /// <summary>
    /// SERVER-SIDE, <c>startswith(computerDnsName, ...)</c> - the one function Learn documents by
    /// worked example on this collection. A prefix and not a substring: typing the middle of a name
    /// finds nothing, and the page says so beside the box.
    /// </summary>
    public string DeviceNameStartsWith { get; init; } = "";

    /// <summary>
    /// CLIENT-SIDE, case-insensitive substring over the device's tags. machineTags does appear in
    /// Learn's filterable list for this collection, but a tag filter is a collection query
    /// (<c>machineTags/any(...)</c>) and no worked example of one exists for this endpoint; a
    /// server-side guess fails silently as an empty result rather than loudly as an error, which is
    /// the worst failure shape this module has.
    /// </summary>
    public string MachineTag { get; init; } = "";

    /// <summary>
    /// CLIENT-SIDE, case-insensitive substring over rbacGroupName. The filterable property is
    /// <c>rbacGroupId</c>, a numeric id; the name an operator knows, and the one the portal shows,
    /// is <c>rbacGroupName</c>, which is not filterable. Matching the name the operator typed is
    /// worth one client-side pass; asking them for a group id is not.
    /// </summary>
    public string MachineGroup { get; init; } = "";

    /// <summary>
    /// SERVER-SIDE, <c>lastSeen ge</c>. Also the lower bound of the partition's root part, so an
    /// operator who sets it makes the run cheaper as well as narrower.
    /// </summary>
    public DateTimeOffset? LastSeenFrom { get; init; }

    /// <summary>SERVER-SIDE, <c>lastSeen lt</c>. Exclusive, and the partition's root upper bound.</summary>
    public DateTimeOffset? LastSeenTo { get; init; }

    /// <summary>
    /// CLIENT-SIDE. firstSeen is absent from Learn's filterable list for this collection - the one
    /// timestamp on the machine resource that is not filterable - so it can only be applied to the
    /// set after it arrives. A device with no firstSeen value is excluded once either bound is set.
    /// </summary>
    public DateTimeOffset? FirstSeenFrom { get; init; }

    /// <summary>CLIENT-SIDE, exclusive upper bound. Same reason as <see cref="FirstSeenFrom"/>.</summary>
    public DateTimeOffset? FirstSeenTo { get; init; }

    /// <summary>Whether the operator bounded Last seen themselves.</summary>
    /// <remarks>
    /// Load-bearing, and not a convenience. When it is true every request already carries a lastSeen
    /// clause, so devices with NO lastSeen value are outside what the operator asked for and the
    /// no-Last-seen part of the partition must not be fetched. When it is false the report claims to
    /// cover every device, and that part MUST be fetched or the union silently misses them - the
    /// failure the whole partition design exists to prevent.
    /// </remarks>
    public bool HasLastSeenBound => LastSeenFrom.HasValue || LastSeenTo.HasValue;
}
