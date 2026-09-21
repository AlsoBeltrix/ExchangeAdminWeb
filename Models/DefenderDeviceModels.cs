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
    /// cursor chain whose final response was short and cursor-free). The only value that may be
    /// rendered as a device list.
    /// </summary>
    Complete,

    /// <summary>More devices matched than the configured MaxDevices ceiling.</summary>
    CeilingExceeded,

    /// <summary>
    /// A response returned exactly the $top it asked for and carried no continuation cursor. That
    /// is byte-identical whether exactly that many devices exist or the service capped the request,
    /// so completeness is unprovable and the run refuses rather than guessing.
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
        "Include Discovery Sources is turned off in this module's configuration, so the advanced "
        + "hunting query that supplies these columns was not run. Turn it on in Module Config once "
        + "the app registration holds ThreatHunting.Read.All on Microsoft Graph.";

    /// <summary>The module's own credentials could not be built, so nothing was called.</summary>
    public const string CredentialsUnavailable =
        "This module's app credentials could not be built, so the advanced hunting query that "
        + "supplies these columns was not run.";

    /// <summary>No devices matched, so there was nothing to enrich.</summary>
    public const string NoDevices =
        "No devices matched these filters, so there was nothing to enrich and the advanced hunting "
        + "query was not run.";

    /// <summary>The device listing itself refused, so enrichment was never reached.</summary>
    public const string ListRefused =
        "The device list did not complete, so no enrichment was attempted.";

    /// <summary>A 2xx whose body carried no readable results collection.</summary>
    public const string MalformedResponse =
        "Microsoft Graph answered the advanced hunting query with a response this module could not "
        + "read - it carried no results collection - so these columns are empty for this run. The "
        + "device list itself is unaffected.";

    /// <summary>403: the permission was never granted, or consent was never given for it.</summary>
    public const string Forbidden =
        "Microsoft Graph refused the advanced hunting query (403 Forbidden). The app registration "
        + "does not hold the ThreatHunting.Read.All application permission on Microsoft Graph, or "
        + "nobody has granted admin consent for it - that consent has to come from a Privileged "
        + "Role Administrator or a Global Administrator. The device list itself is unaffected.";

    /// <summary>429: the tenant's advanced hunting CPU quota is exhausted.</summary>
    public const string Throttled =
        "Microsoft Graph throttled the advanced hunting query (429 Too Many Requests), which on "
        + "this endpoint means the tenant's advanced hunting CPU quota is currently exhausted. "
        + "Nothing is misconfigured - wait and load again. The device list itself is unaffected.";

    /// <summary>A client-side timeout, which arrives as an exception and not as a status.</summary>
    public const string TimedOut =
        "The advanced hunting query did not finish before the request timed out, so these columns "
        + "are empty for this run. The device list itself is unaffected. Load again, and narrow the "
        + "filters if it keeps happening.";

    /// <summary>401: the credentials themselves were rejected.</summary>
    public const string Unauthorized =
        "Microsoft Graph rejected this module's credentials for the advanced hunting query (401 "
        + "Unauthorized). Check that the client secret in the module's Secret Server record has not "
        + "expired. The device list itself is unaffected.";
}

/// <summary>
/// The outcome of one device listing run (docs/DefenderEndpointDevices-Plan.md S1).
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
    /// The devices to show, after the client-side Windows rule. Always empty unless
    /// <see cref="Outcome"/> is <see cref="DefenderDeviceListOutcome.Complete"/>.
    /// </summary>
    public IReadOnlyList<DefenderDevice> Devices { get; private init; } = [];

    /// <summary>
    /// Distinct devices the API returned across the whole run, de-duplicated on device id and
    /// counted BEFORE the client-side Windows rule. This is the number the ceiling is measured
    /// against, because the fetch is what has to be bounded.
    /// </summary>
    public int DistinctCount { get; private init; }

    /// <summary>How many HTTP requests the run issued, including the one that refused.</summary>
    public int PagesFetched { get; private init; }

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
        int ceiling,
        DefenderDiscoveryEnrichmentState enrichment,
        string enrichmentReason) => new()
        {
            Outcome = DefenderDeviceListOutcome.Complete,
            Devices = devices,
            DistinctCount = distinctCount,
            PagesFetched = pagesFetched,
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
            Ceiling = ceiling,
            RefusalReason = reason,
            DiscoveryEnrichment = DefenderDiscoveryEnrichmentState.NotAttempted,
            DiscoveryEnrichmentReason = DefenderDiscoveryReasons.ListRefused
        };
    }
}

/// <summary>
/// The operator-changeable filters for one listing run (docs/DefenderEndpointDevices-Plan.md S2
/// renders the controls; S1 owns their meaning).
/// </summary>
/// <param name="OnboardingStatus">
/// The API literal, NOT the portal label: the portal shows "Can be onboarded" and the REST value is
/// <c>CanBeOnboarded</c>; sending the portal spelling matches nothing. Empty means no server-side
/// onboarding filter at all. Whatever arrives here is escaped as an OData string literal (T6.1).
/// </param>
/// <param name="WindowsOnly">
/// Applied CLIENT-side as an ordinal case-insensitive StartsWith("Windows") on osPlatform, because
/// osPlatform carries Windows10, Windows11 and the server variants as separate values, so
/// <c>osPlatform eq 'Windows'</c> matches nothing. Whether <c>startswith</c> works server-side on
/// this property is undocumented and is R1(d)'s question; the client-side rule is correct either
/// way (T6.3).
/// </param>
public sealed record DefenderDeviceFilters(
    string OnboardingStatus = DefenderDeviceFilters.CanBeOnboarded,
    bool WindowsOnly = true)
{
    /// <summary>
    /// The API literal for the state the portal labels "Can be onboarded" - verified from the
    /// machine resource's onboardingStatus enumeration, which is <c>onboarded</c>,
    /// <c>CanBeOnboarded</c>, <c>Unsupported</c>, <c>InsufficientInfo</c>.
    /// </summary>
    public const string CanBeOnboarded = "CanBeOnboarded";

    /// <summary>The osPlatform prefix the Windows rule matches. Not a filter value - a prefix.</summary>
    public const string WindowsPlatformPrefix = "Windows";
}
