namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The committed record of what every operator-initiated operation on every page reports to the
/// status frame, iterated by <see cref="ProgressRegistryTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: the predecessor guard
/// (<c>GlobalProgressWiringTests.AnAdoptedModuleReportsItsWorkToTheStatusFrame</c>) is a [Theory]
/// over a hand-maintained list of page names that asserts one <c>Progress.Begin</c> ANYWHERE in
/// the file. BlockedSenders.razor passes it today while its only mutating operation reports
/// nothing, and three pages that report nothing at all are absent from the list and therefore
/// unchecked. Per-file adoption was never the thing anyone wanted; per-OPERATION coverage is
/// (docs/ProgressCoverage-Plan.md).
/// </para>
/// <para>
/// Two deliberate differences from <see cref="ClickGateRegistry"/>, the registry this borrows its
/// shape from:
/// </para>
/// <para>
/// 1. The covered page set comes from the filesystem, not from this file. A page under
/// Components/Pages that is absent here FAILS, so forgetting is not one of the options.
/// </para>
/// <para>
/// 2. Entries are keyed by METHOD NAME, not by line number. ClickGateRegistry is line-keyed, which
/// is why any page edit invalidates every entry below it and needs the five-trap manual procedure
/// in .agents/playbooks/clickgate-reanchor.md. A name-keyed registry survives every edit that does
/// not rename the method. Lines appear only inside a <see cref="KnownGap"/>, where they are a
/// human pointer to a defect and nothing is asserted about them.
/// </para>
/// <para>
/// THE RULE THAT MAKES THIS GUARD REAL: a <see cref="Reported"/> entry NAMES the slow or mutating
/// call it covers, and the test proves the activity opens before that call and is still open at
/// it. An earlier draft would have credited "this method, or something it directly calls", and
/// <c>BlockedSenders.ConfirmUnblock</c> calls <c>LoadBlockedSenders</c>, which HAS an activity -
/// so the reported defect would have passed the very guard written to catch it. A helper's
/// activity is credited only where an entry says explicitly that the helper IS the operation,
/// which is what <see cref="Exempted.DelegatesTo"/> records for a thin dispatcher.
/// </para>
/// <para>
/// WHAT IT CANNOT DO, stated here rather than discovered later. It cannot prove an activity
/// RENDERS - there is no bUnit harness in this repo and every check here is a source scan; three
/// defects found in Emergency Disable on 2026-10-05 were all presentation-layer and all invisible
/// to this kind of test. Source-order dominance is a WEAKER claim than runtime coverage: it cannot
/// prove the named call is reached on the path that opened the activity, nor catch a no-op
/// Begin/Complete pair above an unreachable branch -
/// <c>GlobalProgressWiringTests.CloudPasswordResetReportsItsDirectoryLookupInsteadOfFinishingBeforeIt</c>
/// remains the precedent for a bespoke per-page assertion where that matters. And an entry proves
/// only the calls it NAMES: a <see cref="Reported"/> entry may name several and every one of them
/// is pinned at both ends, but slow work inside a callee, or a call nobody thought to list, is
/// held by nothing.
/// </para>
/// <para>
/// Discovery covers @onclick, @onsubmit, @onkeydown, the DOM's @onchange, InputFile's OnChange,
/// @bind:after and the three lifecycle entry points. @onchange was excluded when this file was
/// written, recorded as a limitation outside the plan's named surfaces, and the exclusion was
/// wrong: Migration.razor wires tick boxes straight at AdoptSelectionAsOpenBatch, so the same
/// work was classified when a click reached it and unclassified when a checkbox did (review
/// finding prog-2). It surfaced 25 further handlers across seven pages, every one of them a tick
/// box, radio, select or text input editing page state - which is what that surface is used for
/// here, and is a finding about the shape of the app rather than a clean bill of health.
/// </para>
/// </remarks>
public static class ProgressRegistry
{
    /// <summary>
    /// An operation whose work IS reported. Each entry in <see cref="CoveredCalls"/> is a literal
    /// source fragment that must appear in the method's own body, inside the activity's window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AN ENTRY MAY NAME MORE THAN ONE CALL, and that is not a convenience. One covered call
    /// cannot hold both ends of the window. Naming the LAST heavy call pins Complete into the
    /// finally - an early Complete on a refusal path lands before it and fails condition 3 - but
    /// proves nothing about anything above it. Naming the FIRST pins Begin and proves nothing
    /// about the tail. A handler that does two slow things therefore needs both named.
    /// </para>
    /// <para>
    /// CloudPasswordReset.ExecuteResetAsync is why this exists. It was registered against the
    /// irreversible PATCH alone, deliberately and for the right reason, while the ~30 second
    /// destination derive above it was held by nothing: moving that derive above the
    /// Progress.Begin, which is exactly the behaviour the fix removed, left every test passing
    /// (review finding prog-1).
    /// </para>
    /// <para>
    /// Every named call is checked independently by all three conditions, so Begin must dominate
    /// ALL of them and the activity must still be open at ALL of them. Naming a call costs one
    /// string and is the only thing that pins it, so name every slow or mutating call the
    /// activity is supposed to cover rather than the one that reads worst.
    /// </para>
    /// </remarks>
    public sealed record Reported
    {
        /// <summary>The single-call form, which is most entries.</summary>
        public Reported(string method, string coveredCall, string note = "")
            : this(method, [coveredCall], note)
        {
        }

        /// <summary>
        /// The multi-call form, for a handler whose cost is spread across more than one call.
        /// </summary>
        public Reported(string method, IReadOnlyList<string> coveredCalls, string note = "")
        {
            Method = method;
            CoveredCalls = coveredCalls;
            Note = note;
        }

        /// <summary>The page method this entry is about.</summary>
        public string Method { get; }

        /// <summary>
        /// Every slow or mutating call the activity claims to cover. Never empty - an entry that
        /// named nothing would satisfy all three conditions vacuously, which is the failure class
        /// this whole registry exists to stop.
        /// </summary>
        public IReadOnlyList<string> CoveredCalls { get; }

        /// <summary>Free text for the reader; nothing is asserted about it.</summary>
        public string Note { get; }
    }

    /// <summary>
    /// A defect recorded rather than fixed, so the suite is green on the repo as it stands today
    /// and each later fix is "move this entry to <see cref="Reported"/> and watch the test demand
    /// the call". <paramref name="Line"/> is a pointer for the reader; nothing asserts it.
    /// </summary>
    public sealed record KnownGap(string Method, int Line, string Defect);

    /// <summary>
    /// An operation that needs no activity, with the reason written down. An inferred exemption is
    /// an oversight that looks like a decision. <paramref name="DelegatesTo"/>, when set, names the
    /// page method this handler hands the work to; the test then requires the handler to call it
    /// and requires that method to be registered in its own right, so "it is only a dispatcher"
    /// cannot be asserted about a method that actually does the work.
    /// </summary>
    public sealed record Exempted(string Method, string Reason, string DelegatesTo = "");

    /// <summary>One page's record. Every operation discovered on it must appear in one list.</summary>
    public sealed record PageEntry
    {
        public required string Page { get; init; }
        public IReadOnlyList<Reported> Reports { get; init; } = [];
        public IReadOnlyList<KnownGap> KnownGaps { get; init; } = [];
        public IReadOnlyList<Exempted> Exempt { get; init; } = [];
    }

    /// <summary>Shared reasons, so the same judgement is not re-worded page by page.</summary>
    private const string AuthPreambleOnly =
        "the authorization re-check and the operator/client-IP lookup only. The house rule "
        + "adopted by docs/ProgressCoverage-Plan.md puts that preamble outside the reported "
        + "window on every mutating handler in this app, so a handler that is ONLY the preamble "
        + "has nothing to report";

    private const string LocalUiState = "local page state only; no remote, disk or JS call";

    private const string ConfigStoreWrite =
        "a write to the local SQLite configuration store, not a directory or Exchange call "
        + "(docs/SqliteConfigStore-Plan.md). ModuleConfig's Save* handlers are the reference case "
        + "the plan registers as exempt so a later agent does not 'fix' them";

    private const string ConfigStoreRead =
        "reads the local SQLite configuration store; no directory, Graph or Exchange call";

    private const string SampleCsv =
        "serves a compile-time constant; no backend call and no row count to scale with";

    private const string JobEnqueue =
        "enqueues a durable job. Components/Shared/GlobalProgress.razor already renders active "
        + "jobs for the submitter via BulkJobService, a channel separate from IActivityProgress, "
        + "so the long part IS visible by that route and the enqueue itself is fast";

    /// <summary>
    /// The DOM @onchange tick-box shape, which is most of what widening discovery to that
    /// surface turned up (review finding prog-2). A tick box is an input to a later confirm
    /// step: it adds or removes a row key already on the page and calls nothing.
    /// </summary>
    /// <summary>
    /// ModuleConfig's attribute-allowlist grid: one cell edit on an in-memory row, plus the
    /// section's dirty counter. Nothing reaches the configuration store until SaveAllowlistAsync.
    /// </summary>
    private const string AllowlistRowEdit =
        "edits one cell of an attribute-allowlist row held in memory and bumps the section's "
        + "dirty count; the write is SaveAllowlistAsync";

    private const string TickBoxSelection =
        "ticks rows already in the page's result set; adds or removes a key in the selection "
        + "set and calls nothing. The work it arms is registered on the handler that runs it";

    private const string StagesConfirmation =
        "stages a confirmation; nothing runs until the operator confirms, and the execution path "
        + "reports through ExecuteUserAction / ExecuteBatchAction / ExecuteBulkMailboxAction";

    /// <summary>The defect text shared by the unreported CSV-download shape. See the class remarks.</summary>
    private const string SilentCsvExport =
        "builds a CSV from rows already on the page and pushes the bytes over JS interop with no "
        + "activity. No backend call, so cost scales with row count. SILENT by the plan's own "
        + "rubric, which classifies this shape as a gap because the house pattern elsewhere wraps "
        + "an export";

    private const string SilentCsvExportUnsurveyed = SilentCsvExport
        + ". NOT in docs/ProgressCoverage-Plan.md's list of four - the survey named only "
        + "BlockedSenders, DhcpAuthorization, NamedLocations and AdminEventLog and missed the rest "
        + "of the identical shape";

    public static readonly IReadOnlyList<PageEntry> Pages =
    [
        ADAttributeEditor,
        AccessDenied,
        AccountLockoutRemediation,
        AdminBulkJobs,
        AdminEventLog,
        AdminSettings,
        BitLockerRecovery,
        BlockedSenders,
        CalendarPermissions,
        CloudPasswordReset,
        Comms10k,
        ConferenceRooms,
        DefenderEndpointDevices,
        DelegationReport,
        DhcpAuthorization,
        EmergencyDisable,
        Error,
        ExchangeOnlineConfig,
        GroupManagement,
        Home,
        IntuneDevices,
        LicensingUpdates,
        M365GroupManagement,
        MailboxPermissions,
        MessageTrace,
        MessageTraceReports,
        MfaReset,
        Migration,
        ModuleConfig,
        NamedLocations,
        OutOfOffice,
        RecipientLookup,
        RiskyUsers,
        SelfServiceGroups,
        ServiceHealth,
        TrueLastLogon,
    ];

    private static PageEntry ADAttributeEditor => new()
    {
        Page = "ADAttributeEditor.razor",
        Reports =
        [
            new("ConfirmSave",
                [
                    "EditorService.SaveAsync(",
                    "Email.SendAdminNotificationAsync(",
                ],
                "the survey's third silent write (docs/ProgressCoverage-Plan.md S2): an "
                + "authorization re-check, the allowlist read, a live on-prem AD attribute write "
                + "and a notification email, with no activity open at any point. One activity "
                + "now opens above the try - so the refusals inside it are covered too - and "
                + "completes in the finally after the email, over three honest steps. "
                + "TWO calls are named, not four as on OutOfOffice.SetOof, and the difference is "
                + "a measurement rather than a lighter touch: everything above the write here is "
                + "genuinely local. GroupAuthorizationHandler decides from the Windows token's "
                + "group SIDs and a cached section-access read, so AuthorizeAsync and the three "
                + "level probes in DetermineMaxLevelAsync make no remote call - unlike "
                + "OutOfOffice's validator, which reaches a 10-15 second Exchange round trip and "
                + "had to be pinned - and GetAllowlistForLevel is a cached local SQLite read on "
                + "a synchronous path. Naming any of them would pad the entry with calls that "
                + "prove nothing about a wait. EditorService.SaveAsync is the OPENING pin and is "
                + "the first call with real latency: inside it sit the protected-principal "
                + "check, a Delinea credential fetch and an AD throttle that waits up to two "
                + "minutes before the write even starts. SendAdminNotificationAsync is the "
                + "CLOSING pin, chosen over the write the way OutOfOffice chose its OOF mail: it "
                + "is the last remote call in the handler, at all three of its occurrences - the "
                + "success branch, the failure branch and the catch - so it is what forbids a "
                + "Complete hoisted up to the write and leaves the reference shape's actual "
                + "promise asserted rather than merely commented"),
        ],
        KnownGaps =
        [
            new("PerformSearch", 291,
                "PARTIAL. The activity completes at :313, before the protected-principal check at "
                + ":329, so the frame reads finished while the operator is still waiting on it"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("OnSearchKeyDown", "Enter key; the search is PerformSearch", "PerformSearch"),
            new("SetEditValue",
                LocalUiState + "; buffers one attribute's typed value in editValues until "
                + "ConfirmSave writes it"),
            new("ResetAttribute", LocalUiState),
            new("ShowPreview", LocalUiState),
            new("CancelSave", LocalUiState),
        ],
    };

    private static PageEntry AccessDenied => new()
    {
        Page = "AccessDenied.razor",
        Exempt =
        [
            new("OnInitializedAsync",
                "reads the authentication state to name the operator on a static refusal page; "
                + "no remote call"),
        ],
    };

    /// <remarks>
    /// The shared-wrapper shape, and the reason <see cref="Exempted.DelegatesTo"/> exists. Every
    /// action funnels through RunAsync, which opens one activity around the delegate it is handed,
    /// so the service call sits lexically in the HANDLER but the activity lives in the wrapper.
    /// The handlers are therefore exempt by delegation and RunAsync carries the proof.
    /// </remarks>
    private static PageEntry AccountLockoutRemediation => new()
    {
        Page = "AccountLockoutRemediation.razor",
        Reports =
        [
            new("RunAsync", "work(",
                "the shared wrapper. Its activity spans the delegate invocation, so every action "
                + "routed through it is reported by construction rather than by remembering to"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly + ", plus the configured throttle default"),
            new("DiscoverSources", "runs inside RunAsync's activity", "RunAsync"),
            new("DryRunSourceLogoff", "runs inside RunAsync's activity", "RunAsync"),
            new("ExecuteSourceLogoff", "runs inside RunAsync's activity", "RunAsync"),
            new("DryRunSweep", "runs inside RunAsync's activity", "RunAsync"),
            new("ExecuteSweep", "runs inside RunAsync's activity", "RunAsync"),
        ],
    };

    private static PageEntry AdminBulkJobs => new()
    {
        Page = "AdminBulkJobs.razor",
        KnownGaps =
        [
            new("ToggleDetails", 218,
                "SILENT, READ. BulkJobs.GetRows has no LIMIT, so expanding a 10,000-row job "
                + "blocks the render thread with the bar idle. The missing LIMIT is a separate "
                + "defect, recorded as a candidate and NOT fixed by this plan"),
            new("RefreshJobs", 187,
                "SILENT, READ. The same unbounded GetRows runs here whenever a details row is "
                + "open, on every refresh. NOT in docs/ProgressCoverage-Plan.md's survey, which "
                + "named only ToggleDetails on this page"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly + ", then the job list", "RefreshJobs"),
            new("CancelJob",
                "a single local job-store row update; the view refresh it triggers is registered "
                + "on RefreshJobs"),
            new("RemoveJob",
                "a single local job-store delete; the view refresh it triggers is registered on "
                + "RefreshJobs"),
        ],
    };

    private static PageEntry AdminEventLog => new()
    {
        Page = "AdminEventLog.razor",
        Reports =
        [
            new("ShowUndoPreview", "handler.PreviewUndoAsync"),
            new("ExecuteUndo", "handler.ExecuteUndoAsync"),
        ],
        KnownGaps =
        [
            new("LoadEvents", 486,
                "SILENT, READ. Synchronously scans and parses every JSONL audit, trace and "
                + "extended log in the selected date range"),
            new("LoadUsage", 1271,
                "SILENT, READ. Three SQLite aggregates over the usage database"),
            new("OnInitializedAsync", 468, "SILENT, READ. The initial LoadEvents is unreported"),
            new("ShowEventsView", 1232, "SILENT, READ. Reaches the unreported LoadEvents"),
            new("ShowUsageView", 1242, "SILENT, READ. Reaches the unreported LoadUsage"),
            new("OnDateRangeChanged", 1254,
                "SILENT, READ. @bind:after entry point; reaches both unreported loaders"),
            new("DownloadCsv", 1115, SilentCsvExport),
        ],
        Exempt =
        [
            new("ApplyFilters", LocalUiState + "; filters rows already in memory"),
            new("SetPage", LocalUiState),
            new("ToggleExpand", LocalUiState),
            new("CloseUndoPanel", LocalUiState),
        ],
    };

    private static PageEntry AdminSettings => new()
    {
        Page = "AdminSettings.razor",
        KnownGaps =
        [
            new("OnAfterRenderAsync", 546,
                "SILENT, READ. Fires the protected-principal staleness sweep on a page that "
                + "injects IActivityProgress nowhere, so no existing test looks at it"),
            new("SweepExistingEntriesAsync", 771,
                "SILENT, READ. N serialized ADSearch.ValidateExists calls, each able to block for "
                + "30 seconds, with the bar idle throughout. The worst unreported read in the survey"),
            new("AddPpUser", 700, "SILENT, READ. Reaches the same unreported AD validation path"),
            new("AddPpGroup", 701, "SILENT, READ. Reaches the same unreported AD validation path"),
            new("AddPpOu", 702, "SILENT, READ. Reaches the same unreported AD validation path"),
            new("AddPpTarget", 707, "SILENT, READ. Reaches the same unreported AD validation path"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly + ", plus " + ConfigStoreRead),
            new("SaveCurrentTabAsync", ConfigStoreWrite),
            new("DiscardChanges", "reloads the page; pure navigation"),
            new("RemovePp", LocalUiState + "; the save is a separate action"),
            new("AddPpPattern",
                "a pattern is a string, not a directory object, so this one Add does NOT reach "
                + "the AD validation path its four siblings do"),
            new("ToggleModule",
                LocalUiState + "; flips one module's enablement in the in-memory map and recounts "
                + "the section against its baseline. Like AddPpPattern and unlike the four "
                + "AddPp* handlers, it reaches no directory call; the write is "
                + "SaveCurrentTabAsync"),
            new("OnPpPatternKey", "Enter key; the add is AddPpPattern", "AddPpPattern"),
        ],
    };

    private static PageEntry BitLockerRecovery => new()
    {
        Page = "BitLockerRecovery.razor",
        Reports = [new("SearchAsync", "RecoveryService.SearchByKeyIdAsync")],
        KnownGaps = [new("DownloadCsvAsync", 483, SilentCsvExportUnsurveyed)],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("OnSearchKeyDown", "Enter key; the search is SearchAsync", "SearchAsync"),
            new("RevealAsync",
                "reveals a key already fetched into the page's result set and audits the reveal; "
                + "no backend call"),
        ],
    };

    /// <remarks>
    /// The reported case, and the reason conditions 1 to 3 exist. ConfirmUnblock calls
    /// LoadBlockedSenders on its success path, and LoadBlockedSenders HAS an activity - so a guard
    /// that credited "this method or something it calls" would pass this page while the live
    /// Exchange Online write beside it stays silent.
    /// </remarks>
    private static PageEntry BlockedSenders => new()
    {
        Page = "BlockedSenders.razor",
        Reports =
        [
            new("LoadBlockedSenders", "BlockedSenderSvc.GetBlockedSendersAsync"),
            new("ConfirmUnblock",
                [
                    "ProtectionGate.EvaluateAsync(",
                    "BlockedSenderSvc.UnblockSenderAsync(",
                    "Email.SendAdminNotificationAsync(",
                ],
                "the fifth of the survey's six silent writes, and the one the operator actually "
                + "reported (docs/ProgressCoverage-Plan.md S2). It had an OperationTrace scope "
                + "and nothing else, which is the same confusion ConferenceRooms.SetSingleRoomType "
                + "shipped: the trace is the diagnostic record read afterwards, the activity is "
                + "the status frame watched now. The activity opens above the preflight try, so "
                + "the authorization and protected-principal refusals are covered too. The three "
                + "remote calls in the handler's own body are named, and each was measured by "
                + "reading the callee rather than assumed from its shape. "
                + "ProtectionGate.EvaluateAsync is the OPENING pin: BlockedSenderProtectionGate "
                + "resolves through ProtectedPrincipalService.ResolveWithExchangeFallbackAsync "
                + "unconditionally - deliberately, because a blocked sender is often cloud-only "
                + "or alias-addressed - so it reaches the 10-15 second Exchange round trip that "
                + "OutOfOffice.SetOof and ConferenceRooms.SetSingleRoomType name for the same "
                + "reason, and it is the only call that forbids a Begin hoisted into the try. "
                + "BlockedSenderSvc.UnblockSenderAsync is the live Exchange Online write, the "
                + "operation itself. Email.SendAdminNotificationAsync is the CLOSING pin, named "
                + "directly here rather than through a helper, because it is the last remote "
                + "call in the body and is what forbids a Complete hoisted up to the write. "
                + "Two calls were read and deliberately NOT named: "
                + "AuthStateProvider.GetAuthenticationStateAsync and "
                + "AuthorizationService.AuthorizeAsync decide from the Windows token's group SIDs "
                + "plus a cached section-access read and make no remote hop, the same answer "
                + "ADAttributeEditor.ConfirmSave and ConferenceRooms.SetSingleRoomType got for "
                + "their own prechecks. The window closes BEFORE the trailing LoadBlockedSenders "
                + "refresh, which is registered and reported in its own right: spanning it would "
                + "put two bars in the frame for one click, the older one still claiming a write "
                + "that has already committed. And the Complete reads the LOCAL opResult, not the "
                + "result field, because this page's banner renders a dismiss button wired at "
                + "() => result = null. Shape note: this is the one mutating handler in the repo "
                + "that may NOT complete in a finally - ClickGateStuckFlagTests."
                + "ConfirmUnblock_HasNoFinally refuses one, because a finally would move the "
                + "isLoading clear past the refresh and make the refresh a silent no-op - so the "
                + "single Complete sits on the straight-line path and the refusal returns end the "
                + "activity by disposing the using"),
        ],
        KnownGaps = [new("DownloadCsvAsync", 239, SilentCsvExport)],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("OnAfterRenderAsync", "the initial load is LoadBlockedSenders", "LoadBlockedSenders"),
            new("BeginUnblock", LocalUiState + "; stages the confirm dialog"),
            new("CancelUnblock", LocalUiState),
        ],
    };

    private static PageEntry CalendarPermissions => new()
    {
        Page = "CalendarPermissions.razor",
        Reports =
        [
            new("ProcessBulk", "CalendarService.ProcessCalendarPermissionsCsvAsync"),
            new("ExecuteOnPrem", "CalendarService.SetCalendarPermissionOnPremAsync"),
        ],
        KnownGaps =
        [
            new("SubmitSingle", 346,
                "PARTIAL. The activity opens at :420, after the ServiceNow ticket call and the "
                + "target-mailbox validation the operator is already waiting on"),
            new("DownloadCsvReport", 686, SilentCsvExportUnsurveyed),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("SetTab", LocalUiState),
            new("ConfirmOnPrem", "the on-premises write is ExecuteOnPrem", "ExecuteOnPrem"),
            new("CancelOnPrem", LocalUiState),
            new("HandleCsvUpload",
                "records the selected file reference; nothing is read until ProcessBulk"),
            new("DownloadSampleCsv", SampleCsv),
        ],
    };

    private static PageEntry CloudPasswordReset => new()
    {
        Page = "CloudPasswordReset.razor",
        Reports =
        [
            new("LookupAsync", "ResetService.ResolveTargetAsync",
                "the destination lookup that follows is additionally guarded by a bespoke "
                + "assertion in GlobalProgressWiringTests, which this registry does not replace"),
            new("ExecuteResetAsync",
                [
                    "ResetService.DeriveDestination(",
                    "ResetService.ResetPasswordAsync(",
                    "NotifyAdminsAsync(",
                ],
                "was the worst finding in the survey (docs/ProgressCoverage-Plan.md S2): the "
                + "ticket check, the protection gate, the fresh Graph resolve, DeriveDestination, "
                + "the PATCH and the delivery email all ran with the bar reading Idle. The "
                + "activity now opens before the try, so the refusals are covered too, and "
                + "completes in the finally after the admin notification. BOTH heavy calls are "
                + "named, and that is the fix for review finding prog-1. The PATCH alone was the "
                + "original entry, chosen because it is the LAST heavy call and so pins the "
                + "single Complete into the finally - an early Complete on any refusal path lands "
                + "before it and fails condition 3. That reasoning was right and incomplete: it "
                + "held the closing end and nothing else, so the ~30 second DeriveDestination "
                + "above it could have been moved back above the Progress.Begin with the whole "
                + "suite still green. Naming the derive as well pins the opening end at the call "
                + "that actually costs the operator the wait. Fixing the reporting was not "
                + "sufficient on its own either: DeriveDestination was a blocking forest search "
                + "on the renderer thread, and a bar cannot paint on a frozen circuit, so it "
                + "moved to Task.Run in the same commit. THE PATCH IS NOT THE LAST REMOTE CALL, "
                + "and the two-call entry still stopped there (review finding prog-5): the "
                + "handler goes on to the delivery email and then to the admin notification on "
                + "every outcome, and an activity.Complete added immediately after "
                + "ResetPasswordAsync passed the whole registry suite. NotifyAdminsAsync( is the "
                + "third covered call because its LAST occurrence - the one on the sent-and-"
                + "delivered path - is the last remote call anywhere in this method's own body, "
                + "so it is the only thing that forbids that hoist and makes the reference "
                + "shape's promise (the bar does not read Idle while an email is in flight) "
                + "asserted rather than merely commented. Email.SendAdminNotificationAsync "
                + "cannot be named in its place: it runs inside the NotifyAdminsAsync helper, "
                + "not in this body, and condition 1 requires the named call to be in the "
                + "method's own code. Email.SendCloudPasswordResetAsync( is deliberately NOT "
                + "named either: it sits between the PATCH and the last notification, strictly "
                + "inside the window those two already pin at both ends, so it would add a "
                + "string and no constraint"),
        ],
        Exempt = [new("OnInitializedAsync", AuthPreambleOnly)],
    };

    private static PageEntry Comms10k => new()
    {
        Page = "Comms10k.razor",
        KnownGaps =
        [
            new("LoadPreview", 202, "SILENT, READ. AD credential fetch plus the group query"),
            new("DownloadFull", 211,
                "SILENT, READ. Resolves the full 10,000-member AD group and builds a CSV"),
            new("HandleFileUpload", 234,
                "SILENT, READ. Parses a browser upload; the malformed-upload guard allows roughly "
                + "half a million rows"),
            new("ValidateEmails", 296,
                "PARTIAL. The ServiceNow ticket call runs BEFORE the activity opens at :319"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("ExecuteReplace", JobEnqueue),
            new("DownloadSampleCsv", SampleCsv),
        ],
    };

    private static PageEntry ConferenceRooms => new()
    {
        Page = "ConferenceRooms.razor",
        Reports =
        [
            new("ApplyFinderCsv", "EnqueueConferenceRoomJob"),
            new("ApplyTypeCsv", "EnqueueConferenceRoomJob"),
            new("SetSingleRoomType",
                [
                    "ServiceNow.ValidateTicketAsync(",
                    "ProtectionGate.GuardThenRunAsync(",
                    "RoomService.SetRoomTypeAsync(",
                    "NotifyRoomAdminAsync(",
                ],
                "the fourth of the survey's six silent writes (docs/ProgressCoverage-Plan.md S2). "
                + "It had an OperationTrace scope and nothing else, and those are not the same "
                + "thing: the trace is the diagnostic record read afterwards, the activity is the "
                + "status frame watched now, so a live Set-Place ran with the bar reading Idle. "
                + "The activity opens above the try, so the ticket, authorization and "
                + "invalid-type refusals are covered too, and completes in the finally after the "
                + "admin notification. ALL FOUR remote calls in the handler's own body are named, "
                + "which is the registry's stated policy rather than a flourish, and each was "
                + "measured by reading the callee rather than assumed from its shape: "
                + "ServiceNow.ValidateTicketAsync is an HttpClient GET against the ServiceNow "
                + "table API and is the OPENING pin, the one call that forbids a Begin hoisted "
                + "into the try; GuardThenRunAsync reaches "
                + "ProtectedPrincipalService.ResolveWithExchangeFallbackAsync, the 10-15 second "
                + "Exchange round trip OutOfOffice.SetOof names for the same reason, and is the "
                + "slowest thing the operator waits on; SetRoomTypeAsync is the live Set-Place "
                + "write, the operation itself; NotifyRoomAdminAsync is the CLOSING pin, because "
                + "its catch-block occurrence is the last remote call anywhere in the body - it "
                + "reaches Email.SendAdminNotificationAsync, which cannot be named directly "
                + "because it runs in the helper and condition 1 requires the method's own code. "
                + "Two calls were read and deliberately NOT named: ReauthorizeAsync and "
                + "CurrentUserAsync both resolve from the Windows token's group SIDs plus a "
                + "cached section-access read and make no remote hop, exactly as "
                + "ADAttributeEditor.ConfirmSave found for its own prechecks. Complete reads a "
                + "LOCAL, not the result field: the banner on this page renders a dismiss button "
                + "wired at () => result = null, so the field can be nulled while the handler is "
                + "suspended at an email await"),
        ],
        KnownGaps =
        [
            new("SetupSingleRoom", 905,
                "PARTIAL. The activity opens at :980, inside onAllowed - after the ticket check, "
                + "the protection gate and the room read"),
            new("HandleFinderCsvUpload", 1015,
                "SILENT, READ. Per-row Exchange lookups while building the preview"),
            new("HandleTypeCsvUpload", 1337,
                "SILENT, READ. Per-row Exchange lookups while building the preview"),
            new("ToggleJobDetails", 804,
                "SILENT, READ. The unbounded BulkJobs.GetRows, identical to the shape the plan "
                + "records on AdminBulkJobs.ToggleDetails. NOT in the plan's survey"),
            new("RefreshJobs", 757,
                "SILENT, READ. Reads the job store and calls GetRows for both selected jobs on "
                + "every job event. NOT in the plan's survey"),
        ],
        Exempt =
        [
            new("OnInitializedAsync",
                AuthPreambleOnly + ", plus the section-access group read, then the job panel",
                "RefreshJobs"),
            new("CancelJob", "a single local job-store row update"),
            new("RemoveJob",
                "a single local job-store delete; the view refresh it triggers is registered on "
                + "RefreshJobs"),
            new("DownloadFinderSampleCsv", SampleCsv),
            new("DownloadTypeSampleCsv", SampleCsv),
        ],
    };

    private static PageEntry DefenderEndpointDevices => new()
    {
        Page = "DefenderEndpointDevices.razor",
        Reports = [new("LoadAsync", "DeviceService.ListDevicesAsync")],
        KnownGaps = [new("DownloadCsvAsync", 708, SilentCsvExportUnsurveyed)],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("OnAfterRenderAsync", "the initial load is LoadAsync", "LoadAsync"),
            new("ClearFilters", LocalUiState),
            new("ToggleDetail", LocalUiState + "; the device is already in the result set"),
        ],
    };

    private static PageEntry DelegationReport => new()
    {
        Page = "DelegationReport.razor",
        Reports = [new("RunReport", "DelegationService.GetMailboxDelegationAsync")],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("HandleKeyDown", "Enter key; the report is RunReport", "RunReport"),
        ],
    };

    private static PageEntry DhcpAuthorization => new()
    {
        Page = "DhcpAuthorization.razor",
        Reports =
        [
            new("LoadServers", "DhcpService.GetAuthorizedServersAsync"),
            new("AuthorizeServer", "DhcpService.AuthorizeServerAsync"),
            new("RemoveServer", "DhcpService.DeauthorizeServerAsync"),
        ],
        KnownGaps = [new("DownloadCsvAsync", 199, SilentCsvExport)],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly + ", then the server list", "LoadServers"),
        ],
    };

    private static PageEntry EmergencyDisable => new()
    {
        Page = "EmergencyDisable.razor",
        Reports = [new("ExecuteDisable", "DisableService.DisableAsync")],
        KnownGaps =
        [
            new("PerformLookup", 293,
                "SILENT, READ. The AD resolve plus the Exchange fallback - documented elsewhere "
                + "in this codebase as a 10 to 15 second round trip - in the module that shipped "
                + "two days before the survey"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly + ", plus one module-config read"),
            new("OnSearchKeyDown", "Enter key; the lookup is PerformLookup", "PerformLookup"),
            new("ResetForm", LocalUiState),
        ],
    };

    /// <remarks>A static error page. No operator-initiated operation is discovered on it.</remarks>
    private static PageEntry Error => new() { Page = "Error.razor" };

    private static PageEntry ExchangeOnlineConfig => new()
    {
        Page = "ExchangeOnlineConfig.razor",
        KnownGaps =
        [
            new("SaveExoConfig", 237,
                "SILENT, MUTATING. Saves the module configuration and then runs "
                + "ExoPool.DrainPool, a synchronous teardown of the pooled runspaces, on a page "
                + "that injects IActivityProgress nowhere"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly + ", plus " + ConfigStoreRead),
            new("SaveModuleStatus", ConfigStoreWrite),
        ],
    };

    private static PageEntry GroupManagement => new()
    {
        Page = "GroupManagement.razor",
        Reports =
        [
            new("Search", "GroupService.SearchGroupsAsync"),
            new("SelectGroup", "GroupService.CheckTargetProtectionAsync"),
            new("LoadMembers", "GroupService.GetMembersAsync"),
            new("AddMember", "AddOneAsync",
                "AddOneAsync is named as the covered call deliberately: it is the shared "
                + "single-row pipeline that does the write, the audit and the admin email"),
            new("RemoveMember", "RemoveOneAsync"),
            new("RemoveSelectedAsync", "RemoveOneAsync"),
            new("ResolvePasteAsync", "GroupService.ResolveBatchAsync"),
            new("AddResolvedAsync", "AddOneAsync"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("HandleSearchKey", "Enter key; the search is Search", "Search"),
            new("ToggleSelected", TickBoxSelection),
            new("ToggleSelectAll", TickBoxSelection),
            new("ClearBulkState", LocalUiState),
        ],
    };

    private static PageEntry Home => new()
    {
        Page = "Home.razor",
        Exempt =
        [
            new("OnInitializedAsync",
                "reads the authentication state to greet the operator; no remote call"),
        ],
    };

    private static PageEntry IntuneDevices => new()
    {
        Page = "IntuneDevices.razor",
        Reports =
        [
            new("SearchAsync", "IntuneDeviceService.SearchDevicesAsync"),
            new("ToggleDetailAsync", "IntuneDeviceService.GetDeviceAsync"),
        ],
        KnownGaps =
        [
            new("ExecuteActionAsync", 941,
                "PARTIAL, and a documented-intent violation. The page's own comment at :984-986 "
                + "states the activity is completed in the finally AFTER the admin notification, "
                + "because the bar must not read Idle while an email is still in flight. "
                + "Complete() is at :1216 and the two emails are at :1229 and :1247. A page whose "
                + "comment and code disagree is a defect regardless of which behaviour is "
                + "preferred; ConferenceRooms.SetupSingleRoom does it the way the comment describes"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("OnSearchKeyDown", "Enter key; the search is SearchAsync", "SearchAsync"),
            new("BeginAction", LocalUiState + "; stages the confirm panel"),
            new("CancelAction", LocalUiState),
        ],
    };

    private static PageEntry LicensingUpdates => new()
    {
        Page = "LicensingUpdates.razor",
        Reports = [new("RunApply", "LicenseService.ApplyCsvAsync")],
        KnownGaps =
        [
            new("RunPreview", 270,
                "SILENT, READ. Fetches Delinea credentials, waits out a two-minute AD throttle "
                + "and resolves every row against the directory, all with the bar idle"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly + ", plus the configured license-type list"),
            new("OnFileSelected",
                "records the selected file reference; nothing is read until RunPreview"),
            new("ResetForm", LocalUiState),
        ],
    };

    private static PageEntry M365GroupManagement => new()
    {
        Page = "M365GroupManagement.razor",
        Reports =
        [
            new("SearchGroups", "GroupService.SearchGroupsAsync"),
            new("SelectGroup", "GroupService.GetGroupDetailsAsync"),
            new("SaveGroup", "GroupService.UpdateGroupAsync"),
            new("DeleteGroup", "GroupService.DeleteGroupAsync"),
            new("RunMemberOpAsync", "op(",
                "the shared member/owner pipeline. Its activity spans the delegate the four "
                + "membership handlers hand it, which is where the Graph write actually happens"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("HandleSearchKeyDown", "Enter key; the search is SearchGroups", "SearchGroups"),
            new("ShowCreateForm", LocalUiState),
            new("EditGroup", LocalUiState),
            new("CancelForm", LocalUiState),
            new("AddMember", "runs inside RunMemberOpAsync's activity", "RunMemberOpAsync"),
            new("AddOwner", "runs inside RunMemberOpAsync's activity", "RunMemberOpAsync"),
            new("RemoveMember", "runs inside RunMemberOpAsync's activity", "RunMemberOpAsync"),
            new("RemoveOwner", "runs inside RunMemberOpAsync's activity", "RunMemberOpAsync"),
        ],
    };

    private static PageEntry MailboxPermissions => new()
    {
        Page = "MailboxPermissions.razor",
        Reports =
        [
            new("ProcessBulk", "MailboxService.ProcessMailboxPermissionsCsvAsync"),
            new("ExecuteOnPrem", "MailboxService.AddMailboxPermissionsOnPremAsync"),
        ],
        KnownGaps =
        [
            new("SubmitSingle", 350,
                "PARTIAL. The activity opens at :450, after the ServiceNow ticket call and the "
                + "target-mailbox validation the operator is already waiting on"),
            new("DownloadCsvReport", 743, SilentCsvExportUnsurveyed),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("SetTab", LocalUiState),
            new("ConfirmOnPrem", "the on-premises write is ExecuteOnPrem", "ExecuteOnPrem"),
            new("CancelOnPrem", LocalUiState),
            new("HandleCsvUpload",
                "records the selected file reference; nothing is read until ProcessBulk"),
            new("DownloadSampleCsv", SampleCsv),
        ],
    };

    private static PageEntry MessageTrace => new()
    {
        Page = "MessageTrace.razor",
        Reports =
        [
            new("RunTrace", "RunRealtimeTrace"),
            new("AnalyzeHeadersAsync", "analyze()",
                "the shared analysis wrapper; its activity spans the delegate the paste and "
                + "upload handlers hand it"),
            new("ToggleDetail", "MsgTrace.GetMessageDetailAsync"),
            new("DownloadSelectedDetails", "MsgTrace.GetMessageDetailAsync",
                "completes AFTER its download rather than before, which is the opposite of "
                + "MessageTraceReports.Download and is what made that defect visible"),
            new("EmailSelectedDetails", "BulkJobs.Enqueue"),
        ],
        KnownGaps =
        [
            new("OnAfterRenderAsync", 661,
                "SILENT, READ. Resolves the operator's address from AD behind a lock this "
                + "codebase documents as able to hold for 30 seconds"),
            new("ExportCsv", 917, SilentCsvExportUnsurveyed),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("ShowTraceTab", LocalUiState),
            new("ShowHeadersTab", LocalUiState),
            new("TogglePasteInput", LocalUiState),
            new("AnalyzePastedHeaders",
                "runs inside AnalyzeHeadersAsync's activity", "AnalyzeHeadersAsync"),
            new("AnalyzeUploadedFile",
                "runs inside AnalyzeHeadersAsync's activity", "AnalyzeHeadersAsync"),
            new("UseHeaderTraceSuggestion", "fills the form and runs RunTrace", "RunTrace"),
            new("ClearHeaderAnalysis", LocalUiState),
            new("ToggleRowSelection", TickBoxSelection + " - here DownloadSelectedDetails and "
                + "EmailSelectedDetails, both of which report"),
            new("ToggleSelectAll", TickBoxSelection + "; caps itself at MessageTraceDetailReport"
                + ".EmailMax rows, so it cannot grow unbounded either"),
            new("ClearSelection", LocalUiState),
        ],
    };

    private static PageEntry MessageTraceReports => new()
    {
        Page = "MessageTraceReports.razor",
        KnownGaps =
        [
            new("Download", 144,
                "PARTIAL. The activity completes at :159 and the base64 transfer to the browser "
                + "runs at :163, outside it"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly + ", then the export list", "Refresh"),
            new("Refresh", "lists the export store from local disk; no remote call"),
        ],
    };

    private static PageEntry MfaReset => new()
    {
        Page = "MfaReset.razor",
        KnownGaps =
        [
            new("ListMethods", 176,
                "SILENT, READ. A Graph round trip for the user's registered methods"),
            new("ExecuteReset", 226,
                "PARTIAL. The activity opens at :382, but ResolveWithExchangeFallbackAsync runs "
                + "before it - a round trip this codebase's own comments document as 10 to 15 "
                + "seconds - and the email tail sits in a finally with no reported activity after it"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("HandleKeyDown", "Enter key; the lookup is ListMethods", "ListMethods"),
            new("ConfirmReset", LocalUiState + "; stages the confirm panel"),
            new("CancelReset", LocalUiState),
        ],
    };

    private static PageEntry Migration => new()
    {
        Page = "Migration.razor",
        Reports =
        [
            new("CheckSingleEligibility", "MigrationSvc.CheckMigrationEligibilityAsync"),
            new("CreateSingleMigrationBatch", "MigrationSvc.CreateMigrationBatchAsync"),
            new("CheckBulkEligibility", "MigrationSvc.CheckBulkMigrationEligibilityAsync"),
            new("CreateMigrationBatch", "MigrationSvc.CreateMigrationBatchAsync"),
            new("SearchUser", "MigrationSvc.FindMigrationUserBatchAsync"),
            new("LoadBatchList", "MigrationSvc.GetMigrationBatchesAsync"),
            new("LoadMailboxesFor", "MigrationSvc.GetMigrationBatchUsersAsync"),
            new("SyncOpenBatchFromUrl", "MigrationSvc.GetMigrationBatchUsersAsync"),
            new("FetchUserReport", "MigrationSvc.GetMigrationUserReportAsync"),
            new("ExecuteBatchAction", "action("),
            new("ExecuteUserAction", "action("),
            new("ExecuteBulkBatchAction", "action("),
            new("ExecuteBulkMailboxAction", "action("),
        ],
        KnownGaps =
        [
            new("DownloadReportExportAsync", 2745,
                "SILENT, READ. Builds the export zip from the report store on disk and pushes it "
                + "over JS interop with no activity. NOT in the plan's survey"),
            new("DownloadCsvAsync", 3954, SilentCsvExportUnsurveyed),
            new("ExportOpenReportAsync", 3995, SilentCsvExportUnsurveyed),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("OnParametersSetAsync",
                "adopts the batch named in the URL; the load is SyncOpenBatchFromUrl",
                "SyncOpenBatchFromUrl"),
            new("HandleCsvUpload",
                "records the selected file reference; nothing is read until CheckBulkEligibility"),
            new("SelectTab", LocalUiState),
            new("SelectStatusTab", "the load is LoadMigrationStatus", "LoadMigrationStatus"),
            new("LoadMigrationStatus", "the batch read is LoadBatchList", "LoadBatchList"),
            new("AdoptSelectionAsOpenBatch",
                "re-points the open batch; the mailbox read is LoadMailboxesFor",
                "LoadMailboxesFor"),
            new("SelectOnlyBatch", "selection only", "AdoptSelectionAsOpenBatch"),
            new("RemoveFromSelection", "selection only", "AdoptSelectionAsOpenBatch"),
            new("ClearBatchSelection", "selection only", "AdoptSelectionAsOpenBatch"),
            // The two handlers review finding prog-2 was written about. They reach
            // LoadMailboxesFor by exactly the route SelectOnlyBatch does, and were invisible to
            // the scanner only because the control is a tick box rather than a div.
            new("ToggleBatchSelected", "selection only", "AdoptSelectionAsOpenBatch"),
            new("ToggleSelectAllBatches", "selection only", "AdoptSelectionAsOpenBatch"),
            new("ToggleMailboxSelected", TickBoxSelection + "; also clamps the two mailbox "
                + "pagers, because the row moves between the pinned block and the list"),
            new("ToggleSelectAllMailboxes", TickBoxSelection + "; FilteredSortedMailboxes is an "
                + "in-memory filter and sort over batchUsers, not a re-read"),
            new("OnBatchSortColumnChanged",
                LocalUiState + "; re-orders the batches already loaded and returns to page one"),
            new("OnMailboxSortColumnChanged",
                LocalUiState + "; re-orders the mailboxes already loaded and returns to page one"),
            new("RefreshBatchUsers", "the mailbox read is LoadMailboxesFor", "LoadMailboxesFor"),
            new("StepSelectionPage", LocalUiState),
            new("StepPinnedMailboxPage", LocalUiState),
            new("StepMailboxPage", LocalUiState),
            new("StepBatchPage", LocalUiState),
            new("ToggleMailboxSortDirection", LocalUiState),
            new("ToggleBatchSortDirection", LocalUiState),
            new("ClearMailboxSelection", LocalUiState),
            new("ClearSearch", LocalUiState),
            new("DismissReportModal", LocalUiState),
            new("ToggleBatchActionsMenu", LocalUiState),
            new("CloseBatchActionsMenu", LocalUiState),
            new("ToggleMailboxActionsMenu", LocalUiState),
            new("CloseMailboxActionsMenu", LocalUiState),
            new("CancelPendingAction", LocalUiState + "; backs out of a staged action"),
            new("ConfirmPendingAction",
                "invokes the callback staged by one of the Stage* handlers; the execution it "
                + "starts is reported by ExecuteUserAction, ExecuteBatchAction, "
                + "ExecuteBulkBatchAction or ExecuteBulkMailboxAction"),
            new("HandleConfirmKeyDown", "Enter key on the confirm box", "ConfirmPendingAction"),
            new("HandleSearchKeyDown", "Enter key; the search is SearchUser", "SearchUser"),
            new("StageCompleteMailboxes", StagesConfirmation),
            new("StageApproveMailboxes", StagesConfirmation),
            new("StagePauseMailboxes", StagesConfirmation),
            new("StageResumeMailboxes", StagesConfirmation),
            new("StageRemoveMailboxes", StagesConfirmation),
            new("StageDeleteSelected", StagesConfirmation),
            new("StageRemoveCompletedSelected", StagesConfirmation),
            new("StageResumeSelected", StagesConfirmation),
            new("StageCompleteSelected", StagesConfirmation),
            new("StageStopSelected", StagesConfirmation),
            new("StageScheduleSelected", StagesConfirmation),
            new("CompleteUser", StagesConfirmation),
            new("ApproveUser", StagesConfirmation),
            new("PauseUser", StagesConfirmation),
            new("ResumeUser", StagesConfirmation),
            new("ClearUser", StagesConfirmation),
            new("StartReportExport", JobEnqueue),
            new("LoadUserReport",
                "reads the cached report from disk and otherwise defers to FetchUserReport",
                "FetchUserReport"),
            new("RefetchUserReport",
                "drops the cached report and defers to FetchUserReport", "FetchUserReport"),
            new("DownloadSampleCsv", SampleCsv),
        ],
    };

    /// <remarks>
    /// Correct as it stands and must not be "fixed": its single activity covers
    /// OUService.GetOrganizationalUnits, the page's only remote call. Every Save* here is a write
    /// to the configuration store.
    /// </remarks>
    private static PageEntry ModuleConfig => new()
    {
        Page = "ModuleConfig.razor",
        Reports = [new("LoadOUsAsync", "OUService.GetOrganizationalUnits")],
        Exempt =
        [
            new("OnParametersSetAsync", AuthPreambleOnly + ", plus " + ConfigStoreRead),
            new("SaveCurrentTabAsync", ConfigStoreWrite),
            new("ReloadAsync", "reloads the page; pure navigation"),
            new("AddAccessGroup", LocalUiState + "; the save is a separate action"),
            new("RemoveAccessGroup", LocalUiState + "; the save is a separate action"),
            new("AddModuleAdmin", LocalUiState + "; the save is a separate action"),
            new("RemoveModuleAdmin", LocalUiState + "; the save is a separate action"),
            new("AddAttribute", LocalUiState + "; the save is a separate action"),
            new("RemoveAttribute", LocalUiState + "; the save is a separate action"),
            // The DOM @onchange handlers on this page, all of the same shape: a field edit on an
            // in-memory row plus a dirty recount. ModuleConfig is the page the plan names as
            // correct-as-it-stands, and these are why: nothing here reaches the configuration
            // store, let alone the directory, until a Save* runs.
            new("ToggleModuleEnabled", LocalUiState + "; the save is a separate action"),
            new("SetBooleanConfigValue",
                LocalUiState + "; writes one key into currentConfigState. The save is a separate "
                + "action"),
            new("ToggleOU",
                LocalUiState + "; a tick in the OU browser is not a config edit until "
                + "ApplySelectedOUs writes DefaultSearchBase"),
            new("UpdateAttrName", AllowlistRowEdit),
            new("UpdateAttrLabel", AllowlistRowEdit),
            new("UpdateAttrType", AllowlistRowEdit),
            new("UpdateAttrRequired", AllowlistRowEdit),
            new("UpdateAttrAllowClear", AllowlistRowEdit),
            new("UpdateAttrMaxLength", AllowlistRowEdit),
            new("UpdateAttrLevel", AllowlistRowEdit),
            new("UpdateAttrChoices", AllowlistRowEdit),
            new("ApplySelectedOUs", LocalUiState),
            new("ToggleOUBrowser", "the directory read is LoadOUsAsync", "LoadOUsAsync"),
            new("RefreshOUs", "drops the cache; the directory read is LoadOUsAsync", "LoadOUsAsync"),
        ],
    };

    /// <remarks>
    /// The reference implementation for the mutating shape: Begin before the try, Complete in the
    /// finally after the email. Later slices copy this rather than inventing one.
    /// </remarks>
    private static PageEntry NamedLocations => new()
    {
        Page = "NamedLocations.razor",
        Reports =
        [
            new("LoadLocations", "LocationService.GetAllAsync"),
            new("SaveLocation", "LocationService.UpdateIpLocationAsync"),
            new("DeleteLocation", "LocationService.DeleteAsync"),
        ],
        KnownGaps = [new("DownloadCsvAsync", 525, SilentCsvExport)],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly + ", then the list", "LoadLocations"),
            new("ShowCreateForm", LocalUiState),
            new("EditLocation", LocalUiState),
            new("CancelForm", LocalUiState),
        ],
    };

    private static PageEntry OutOfOffice => new()
    {
        Page = "OutOfOffice.razor",
        Reports =
        [
            new("CheckStatus", "OofService.GetOutOfOfficeAsync"),
            new("SetOof",
                [
                    "Validator.ValidateTargetMailboxAsync(",
                    "OofService.GetOutOfOfficeAsync(",
                    "OofService.SetOutOfOfficeAsync(",
                    "Email.SendOofNotificationAsync(",
                ],
                "was the survey's second-worst finding (docs/ProgressCoverage-Plan.md S2), and "
                + "rated worse than the operator-reported BlockedSenders case: that one at least "
                + "lights the bar late via its trailing refresh, while this resolved the target, "
                + "read the current state, wrote a live EXO mailbox configuration and sent two "
                + "emails without ever lighting it - on a page whose READ path reported "
                + "correctly. The activity now opens above the protected-principal resolve and "
                + "completes in the finally after both emails. FOUR calls are named because no "
                + "one of them holds the window, which is review finding prog-1 applied here "
                + "rather than rediscovered. ValidateTargetMailboxAsync is the OPENING pin and is "
                + "not ceremony: it reaches ResolveWithExchangeFallbackAsync whenever a Group, OU "
                + "or pattern rule is configured, the 10-15 second Exchange round trip this plan "
                + "rates MfaReset.ExecuteReset PARTIAL for leaving outside its window, so the "
                + "'authorization re-check sits outside' rule does not cover it. "
                + "SendOofNotificationAsync is the CLOSING pin, chosen over the EXO write that "
                + "CloudPasswordReset's entry stops at: it is the last remote call in the "
                + "handler, so it is what forbids a Complete moved up to the write and leaves the "
                + "reference shape's actual promise - the bar does not read Idle while an email "
                + "is still in flight - asserted rather than merely commented. The EXO read and "
                + "the write are named between them because each is a remote call an entry that "
                + "did not name it would hold with nothing"),
        ],
        Exempt = [new("OnInitializedAsync", AuthPreambleOnly)],
    };

    private static PageEntry RecipientLookup => new()
    {
        Page = "RecipientLookup.razor",
        Reports = [new("RunLookup", "LookupService.GetRecipientInfoAsync")],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("HandleKeyDown", "Enter key; the lookup is RunLookup", "RunLookup"),
        ],
    };

    private static PageEntry RiskyUsers => new()
    {
        Page = "RiskyUsers.razor",
        Reports =
        [
            new("LookupUserAsync", "RiskyUsersService.LookupAsync"),
            new("LoadRiskyUsersAsync", "RiskyUsersService.GetRiskyUsersAsync"),
            new("ToggleHistoryAsync", "RiskyUsersService.GetHistoryAsync"),
        ],
        KnownGaps =
        [
            new("ExecuteActionAsync", 816,
                "PARTIAL. The activity completes before the admin notification email, the same "
                + "ordering IntuneDevices.ExecuteActionAsync has - here with no comment promising "
                + "otherwise"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("OnFilterKeyDown", "Enter key; the load is LoadRiskyUsersAsync", "LoadRiskyUsersAsync"),
            new("OnLookupKeyDown", "Enter key; the lookup is LookupUserAsync", "LookupUserAsync"),
            new("SetPage", LocalUiState),
            new("ClearLookup", LocalUiState),
            new("BeginAction", LocalUiState + "; stages the confirm panel"),
            new("CancelAction", LocalUiState),
        ],
    };

    private static PageEntry SelfServiceGroups => new()
    {
        Page = "SelfServiceGroups.razor",
        Reports =
        [
            new("LoadMembers", "GroupService.GetGroupMembersAsync"),
            new("LoadOwnedGroups", "GroupService.GetOwnedGroupsAsync"),
            new("SearchGroup", "GroupService.SearchManageableGroupAsync"),
            new("ResolvePasteAsync", "GroupService.ResolveBatchAsync"),
            new("RemoveListedMember", "RemoveOneAsync"),
            new("RemoveSelectedAsync", "RemoveOneAsync"),
            new("ChangeMember", "ChangeOneAsync"),
            new("AddResolvedAsync", "ChangeOneAsync"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("SelectGroup", "the member read is LoadMembers", "LoadMembers"),
            new("BackToGroups", LocalUiState),
            new("RemoveMember", "the removal is RemoveListedMember", "RemoveListedMember"),
            new("ConfirmGroupRemoval", "the removal is RemoveListedMember", "RemoveListedMember"),
            new("BeginGroupRemoval", LocalUiState + "; stages the confirm panel"),
            new("ToggleSelected", TickBoxSelection),
            new("ToggleSelectAll", TickBoxSelection),
            new("OnSearchKeyDown", "Enter key; the search is SearchGroup", "SearchGroup"),
        ],
    };

    private static PageEntry ServiceHealth => new()
    {
        Page = "ServiceHealth.razor",
        Reports = [new("LoadAsync", "HealthService.GetStatusAsync")],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("OnAfterRenderAsync", "the initial load is LoadAsync", "LoadAsync"),
            new("RefreshAsync", "the refresh is LoadAsync", "LoadAsync"),
            new("SelectService", LocalUiState),
            new("ToggleIssue", LocalUiState),
            new("ClearFilters", LocalUiState),
        ],
    };

    private static PageEntry TrueLastLogon => new()
    {
        Page = "TrueLastLogon.razor",
        Reports =
        [
            new("SearchAsync", ["LookupOnPremAsync(", "LookupCloudAsync("],
                "uses ActivitySize.Steps(2) across its on-premises and cloud halves, which start "
                + "together inside the one activity. Both halves are named: an entry holding only "
                + "the on-premises lookup would have let the cloud half drift out of the window "
                + "unnoticed, which is review finding prog-1 on a second page"),
        ],
        Exempt =
        [
            new("OnInitializedAsync", AuthPreambleOnly),
            new("OnIdentityKeyDown", "Enter key; the search is SearchAsync", "SearchAsync"),
        ],
    };
}
