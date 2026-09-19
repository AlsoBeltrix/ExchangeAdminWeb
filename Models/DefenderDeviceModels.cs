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
/// Whether the discovery-source enrichment columns hold real data. Slice S1 never runs the hunting
/// query, so it always reports <see cref="NotAttempted"/>; S4 sets the other two.
/// </summary>
public enum DefenderDiscoveryEnrichmentState
{
    /// <summary>The hunting query did not run - switched off, or not implemented in this slice.</summary>
    NotAttempted,

    /// <summary>The hunting query ran and its columns are populated.</summary>
    Succeeded,

    /// <summary>The hunting query was attempted and failed; the reason names which failure.</summary>
    Failed
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
            DiscoveryEnrichmentReason = "The device list did not complete, so no enrichment was attempted."
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
