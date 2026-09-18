namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The committed record of how every converted page is gated, iterated by
/// <see cref="ClickGateTests"/>. One entry per page; the assertions read this and the page
/// source and fail when they disagree.
/// </summary>
/// <remarks>
/// <para>
/// Why a registry rather than per-page test classes: the precedent
/// (<c>MigrationStatusPageTests</c>) needed seven bespoke tripwires for one page. Copied across
/// the nine approved pages that is roughly two hundred near-identical tests. Here the assertions
/// are written once and each page contributes data, which also makes the rule self-enforcing:
/// <see cref="ClickGateTests.EveryPageIsRegisteredOrDeclaredUnconverted"/> means a page added
/// later cannot quietly skip the gate.
/// </para>
/// <para>
/// Every exemption is explicit and carries its reason. An inferred exemption is an oversight
/// that looks like a decision.
/// </para>
/// <para>
/// The field set is not speculative. Each one was forced by a specific page during the
/// reconnaissance recorded in docs/ClickGatingAudit-Plan.md Revision 1, and the forcing page is
/// named on the member so none is dropped later as unused.
/// </para>
/// </remarks>
public static class ClickGateRegistry
{
    /// <summary>Pages that have been converted. The assertions run against these.</summary>
    public static readonly IReadOnlyList<PageGateEntry> Pages = new[]
    {
        Migration,
    };

    /// <summary>
    /// Pages not yet converted, each with the reason. Nothing is asserted about these beyond
    /// their being declared: the point is that the union of this list and <see cref="Pages"/>
    /// must cover every .razor under Components/Pages, so a NEW page has to be placed in one
    /// list or the other and cannot arrive ungated and unnoticed.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> NotYetConverted =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Approved for conversion, in the order of docs/ClickGatingAudit-Plan.md Revision 1.
            ["DhcpAuthorization.razor"] = "tier 1, page 1 of 9",
            ["NamedLocations.razor"] = "tier 1, page 2 of 9",
            ["MailboxPermissions.razor"] = "tier 1, page 3 of 9",
            ["CalendarPermissions.razor"] = "tier 1, page 4 of 9",
            ["IntuneDevices.razor"] = "tier 1, page 5 of 9; has a partial ActionsDisabled already",
            ["GroupManagement.razor"] = "tier 1, page 6 of 9; needs a flag and a finally created in SelectGroup",
            ["M365GroupManagement.razor"] = "tier 1, page 7 of 9",
            ["ConferenceRooms.razor"] = "tier 1, page 8 of 9",
            ["SelfServiceGroups.razor"] = "tier 1, page 9 of 9; needs view-scoped predicates",

            // Prerequisite fixes (slice 1), converted after their flags are made safe.
            ["BlockedSenders.razor"] = "tier 3, not approved; slice 1 prerequisite done - ConfirmUnblock's "
                + "preflight now fails closed in a catch, guarded by ClickGateStuckFlagTests",
            ["MessageTrace.razor"] = "slice 1 prerequisite: ToggleDetail lowers detailLoading inside an if",

            // Tiers 2-4: audited, NOT approved for conversion. Do not convert without an owner go.
            ["ADAttributeEditor.razor"] = "tier 2, not approved",
            ["AdminSettings.razor"] = "tier 2, not approved",
            ["ExchangeOnlineConfig.razor"] = "tier 2, not approved",
            ["ModuleConfig.razor"] = "tier 2, not approved",
            ["Comms10k.razor"] = "tier 3, not approved",
            ["EmergencyDisable.razor"] = "tier 3, not approved",
            ["LicensingUpdates.razor"] = "tier 3, not approved",
            ["MfaReset.razor"] = "tier 3, not approved",
            ["OutOfOffice.razor"] = "tier 3, not approved",
            ["RiskyUsers.razor"] = "tier 3, not approved; has a partial ActionsDisabled already",
            ["AdminBulkJobs.razor"] = "tier 4, not approved",
            ["AdminEventLog.razor"] = "tier 4, not approved",
            ["BitLockerRecovery.razor"] = "tier 4, not approved",
            ["DelegationReport.razor"] = "tier 4, not approved",
            ["MessageTraceReports.razor"] = "tier 4, not approved",
            ["RecipientLookup.razor"] = "tier 4, not approved",
            ["ServiceHealth.razor"] = "tier 4, not approved",

            // Parked and out of scope entirely.
            ["AccountLockoutRemediation.razor"] = "module is disabled and parked; not in scope",

            // No clickable controls.
            ["AccessDenied.razor"] = "no clickable controls",
            ["Error.razor"] = "no clickable controls",
            ["Home.razor"] = "no clickable controls",
        };

    /// <summary>
    /// Mailbox Migrations: the worked precedent, converted 2026-09-17 under
    /// docs/MigrationButtonGating-Plan.md. Seeded here as the reference entry - it is the only
    /// page whose gating is already known-good, so it is what proves the shared assertions
    /// actually pass correct code before any other page is touched.
    /// </summary>
    private static PageGateEntry Migration => new()
    {
        Page = "Migration.razor",
        ExpectedLineCount = 2025,

        Predicates =
        [
            new PredicateScope("IsBusy",
            [
                "isLoading", "isCreating", "isLoadingStatus", "isSearching",
                "loadingBatchUsers", "actionInProgress", "loadingReport", "isDownloadingCsv",
            ],
                AppliesWhen: "the whole page"),
        ],

        ScannerFalsePositives =
        [
            new ScannerFalsePositive("expandedBatch",
                "the name of the batch whose detail row is open. A nullable raised and nulled in "
                + "one awaiting method, which is the in-flight shape, but it holds a selection and "
                + "is never a busy signal. Reported as a stuck flag by Get-ClickGateAudit.ps1."),
        ],

        // Staged confirmation state. Folding any of these into IsBusy disables Confirm at the only
        // moment it is rendered, and no destructive action could ever be executed again. Caught by
        // review of the plan, before code, when all three then-proposed tests passed the broken shape.
        ExcludedFields =
        [
            new ExcludedField("pendingActionLabel", "the PendingActionConfirm fragment",
                "means 'waiting for the operator to type a ticket', not 'busy'"),
            new ExcludedField("pendingActionTicket", "the PendingActionConfirm fragment",
                "the staged ticket input; a form value, not an operation"),
            new ExcludedField("pendingActionTarget", "the PendingActionConfirm fragment",
                "which batch or user the staged action names"),
            new ExcludedField("pendingActionCallback", "the PendingActionConfirm fragment",
                "the staged continuation; set while waiting, not while working"),
        ],

        ExemptControls =
        [
            new ExemptControl(223, "@onclick=\"DownloadSampleCsv\"",
                "serves a compile-time constant; touches no page state and no in-flight operation",
                ConditionThatKeepsItTrue:
                "DownloadSampleCsv must not grow a call into Exchange; asserted separately"),
            new ExemptControl(442, "@onclick=\"() => batchActionResult = null\"",
                "dismisses a result banner; gating it would trap the message on screen for the "
                + "whole of the next operation"),
            new ExemptControl(784, "@onclick=\"CloseUserReport\"",
                "rendered only once a report has landed, so there is no pull for it to interrupt"),
            new ExemptControl(836, "@onclick=\"CancelPendingAction\"",
                "the operator must always be able to back out of a staged action. It keeps a "
                + "narrower guard of its own instead of IsBusy",
                KeepsItsOwnGuard: "disabled=\"@(actionInProgress != null)\""),
        ],

        // An <a> ignores the disabled attribute entirely, so the refusal lives in the handler and
        // the greying is styling only. A handler guard is safe here precisely because an anchor
        // renders no server state into an attribute - unlike a checkbox or a bound select, where
        // a refusing handler desyncs the browser from the server. See RefusalMechanism.
        NonButtonTargets =
        [
            new NonButtonTarget(30, "@onclick=\"() => SelectTab(0)\"", "a",
                RefusalMechanism.HandlerGuard, "SelectTab"),
            new NonButtonTarget(33, "@onclick=\"() => SelectTab(1)\"", "a",
                RefusalMechanism.HandlerGuard, "SelectTab"),
            new NonButtonTarget(36, "@onclick=\"SelectStatusTab\"", "a",
                RefusalMechanism.HandlerGuard, "SelectStatusTab"),
        ],

        ForbiddenGuardSites =
        [
            new ForbiddenGuardSite("LoadMigrationStatus", ["ExecuteBulkBatchAction"],
                "ExecuteBulkBatchAction calls it to refresh the table while itself still busy. A "
                + "guard here makes every bulk action's confirming refresh a silent no-op, and the "
                + "operator concludes the action failed."),
        ],
    };
}

/// <summary>One page's gating contract. Only Page, ExpectedLineCount and Predicates are required;
/// every collection defaults to empty so an entry names only what applies to it.</summary>
public sealed record PageGateEntry
{
    /// <summary>File name under Components/Pages, e.g. "Migration.razor".</summary>
    public required string Page { get; init; }

    /// <summary>
    /// Line count fingerprint. Registered line numbers drift faster than anyone expects - three of
    /// the eleven reconnaissance reports already carried stale coordinates - so a page that grows
    /// or shrinks fails loudly and the entry is re-checked rather than silently re-pointing at the
    /// wrong control.
    /// </summary>
    public required int ExpectedLineCount { get; init; }

    /// <summary>
    /// The busy predicate(s). A list rather than one name because two approved pages cannot use a
    /// single page-wide predicate: SelfServiceGroups has two mutually exclusive views with disjoint
    /// flag sets, and ConferenceRooms has a jobs panel driven by a background callback the form
    /// predicate has no authority over.
    /// </summary>
    public required IReadOnlyList<PredicateScope> Predicates { get; init; }

    /// <summary>Fields a naive structural scan reports as in-flight flags but which are not.</summary>
    public IReadOnlyList<ScannerFalsePositive> ScannerFalsePositives { get; init; } = [];

    /// <summary>Staged state that must never enter a predicate.</summary>
    public IReadOnlyList<ExcludedField> ExcludedFields { get; init; } = [];

    /// <summary>Controls that must stay clickable while the page is busy, each with its reason.</summary>
    public IReadOnlyList<ExemptControl> ExemptControls { get; init; } = [];

    /// <summary>Every clickable control that is not a &lt;button&gt;, and how each is refused.</summary>
    public IReadOnlyList<NonButtonTarget> NonButtonTargets { get; init; } = [];

    /// <summary>
    /// Methods that must NOT carry a guard, because a busy handler calls them. Six of the eleven
    /// reconnoitred pages call a refresh or shared helper from a handler that is already busy, and
    /// a guard there turns the confirming refresh into a silent no-op.
    /// </summary>
    public IReadOnlyList<ForbiddenGuardSite> ForbiddenGuardSites { get; init; } = [];

    /// <summary>
    /// Gated controls needing annotation: a non-busy clause that must survive, or reachability the
    /// scanner cannot see. Controls that simply consult the predicate need no entry.
    /// </summary>
    public IReadOnlyList<AnnotatedControl> AnnotatedControls { get; init; } = [];

    /// <summary>
    /// Spinner conditions that read flags in non-busy combinations, recorded verbatim so a
    /// mechanical "simplify to the predicate" pass fails instead of showing two spinners or none.
    /// </summary>
    public IReadOnlyList<string> SpinnerExpressions { get; init; } = [];
}

/// <summary>A busy predicate and the flags it ORs together.</summary>
/// <param name="AppliesWhen">Prose scope, e.g. "the manage view (selected != null)".</param>
public sealed record PredicateScope(
    string Name,
    IReadOnlyList<string> Members,
    string AppliesWhen);

public sealed record ScannerFalsePositive(string Field, string Why);

/// <param name="RendersAt">Where the control this field stages is rendered.</param>
public sealed record ExcludedField(string Field, string RendersAt, string Reason);

/// <param name="Snippet">
/// A distinctive substring of the live tag. The line alone is not a key: two controls on one page
/// can share a label and a handler across different loops.
/// </param>
/// <param name="KeepsItsOwnGuard">
/// A narrower gate the control keeps instead of the predicate, asserted verbatim.
/// </param>
/// <param name="ConditionThatKeepsItTrue">
/// What must remain true for the exemption to stay valid, so it cannot lapse silently.
/// </param>
public sealed record ExemptControl(
    int Line,
    string Snippet,
    string Reason,
    string? KeepsItsOwnGuard = null,
    string? ConditionThatKeepsItTrue = null);

/// <summary>How a non-button control refuses a click while the page is busy.</summary>
public enum RefusalMechanism
{
    /// <summary>
    /// The disabled attribute. The ONLY safe mechanism for a control that renders server state
    /// into the DOM - a checkbox, a radio, a bound select, an InputFile.
    /// </summary>
    DisabledAttribute,

    /// <summary>
    /// An early return in the handler. Safe only where nothing rendered mirrors server state. On a
    /// DOM-synced control this corrupts data: the backing field is unchanged, so the render diff
    /// emits no correction and the browser keeps the operator's action while the server never took
    /// it.
    /// </summary>
    HandlerGuard,

    /// <summary>A Disabled= parameter passed to a child component.</summary>
    ChildComponentParameter,

    /// <summary>
    /// The gap is in a shared component and cannot be closed from this page. ADIdentityAutocomplete
    /// gates its input but not its suggestion rows, and is instantiated at nine sites.
    /// </summary>
    SharedComponentContract,

    /// <summary>
    /// Gating cannot close it. A value read after an await must be captured at handler entry
    /// instead; narrowing the window is not the same as closing it.
    /// </summary>
    SnapshotNotGate,
}

/// <param name="WhyNotHandlerGuard">Required whenever the mechanism is not a handler guard.</param>
public sealed record NonButtonTarget(
    int Line,
    string Snippet,
    string Tag,
    RefusalMechanism Mechanism,
    string Handler,
    string? WhyNotHandlerGuard = null);

public sealed record ForbiddenGuardSite(
    string Method,
    IReadOnlyList<string> CalledWhileBusyFrom,
    string Consequence);

/// <param name="PreserveClauses">
/// Non-busy clauses that must remain in the disabled expression, OR-ed with the predicate rather
/// than replaced by it. IntuneDevices line 403 is why this exists: its typed-device-name clause is
/// the only enforcement of that confirmation anywhere, and a mechanical rewrite would delete it.
/// </param>
/// <param name="RendersOnlyWhen">Reachability the scanner cannot see, as prose for a reviewer.</param>
public sealed record AnnotatedControl(
    int Line,
    string Snippet,
    IReadOnlyList<string> PreserveClauses,
    string? RendersOnlyWhen = null);
