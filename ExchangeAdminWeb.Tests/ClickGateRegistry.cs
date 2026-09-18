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
        DhcpAuthorization,
        NamedLocations,
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
            // Pages 1 and 2 of 9, DhcpAuthorization.razor and NamedLocations.razor, are converted
            // and live in Pages above.
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
            ["MessageTrace.razor"] = "tier 4, not approved; slice 1 prerequisite done - ToggleDetail now "
                + "lowers detailLoading in a token-guarded finally, guarded by ClickGateStuckFlagTests",

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

    /// <summary>
    /// DHCP Authorization: tier 1, page 1 of 9, converted 2026-09-18 under
    /// docs/ClickGatingAudit-Plan.md Revision 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sequenced first because it is the only tier-1 page with no non-button click target of any
    /// kind. That census was re-run against the file here rather than trusted from the plan, since
    /// Revision 1 falsification 3 is that the scanner's non-button count is an @onclick-only
    /// undercount: the page has no @onchange, no InputFile, no @onkeydown, no @onsubmit and no
    /// child-component Disabled= parameter, and every @onclick in it is on one of its seven buttons.
    /// Its three @bind text inputs DO render server state into the DOM, so they take the disabled
    /// attribute - the only safe mechanism for a DOM-synced control - and now name IsBusy rather
    /// than isOperating alone. They are absent from NonButtonTargets on purpose: that list is keyed
    /// to what ClickGateSource can locate, and an input element is in neither of its tag sets, so
    /// registering one would fail EveryNonButtonTargetIsRefusedByItsDeclaredMechanism rather than
    /// document anything.
    /// </para>
    /// <para>
    /// isDownloadingCsv is new. DownloadCsvAsync owned no in-flight flag at all, and its raise has
    /// to sit BELOW the early return on an empty list: a flag raised above that guard is never
    /// lowered, and this one is in a page-wide predicate, so it would deaden the whole page rather
    /// than one button. That ordering is now in RaiseMustFollowEarlyReturn below and asserted by
    /// <see cref="ClickGateTests.EveryRaiseThatMustFollowAnEarlyReturnStillDoes"/>; it used to rest
    /// on the comment at the raise and on this note alone.
    /// </para>
    /// <para>
    /// PostAwaitLiveReads below is the other half of this page's fix and the half no gate can do.
    /// The six obligations are what falsification 6 names for this page, and they are the reason
    /// the banner dismiss at line 40 is safe to leave clickable at all.
    /// </para>
    /// </remarks>
    private static PageGateEntry DhcpAuthorization => new()
    {
        Page = "DhcpAuthorization.razor",
        ExpectedLineCount = 411,

        Predicates =
        [
            new PredicateScope("IsBusy",
                ["isLoading", "isOperating", "isDownloadingCsv"],
                AppliesWhen: "the whole page"),
        ],

        // Get-ClickGateAudit.ps1 reports THREE flags and ONE stuck flag on this page as of the
        // conversion (isDownloadingCsv, isLoading, isOperating; stuck: operationResult). An earlier
        // revision of this comment said two and zero, and this slice's own fix is what changed it -
        // see the operationResult entry below. Worth knowing for the next reader: confirmRemove and operationResult
        // are both nullables raised and nulled inside one awaiting method, which is most of the
        // in-flight shape. They escape the detector only because neither is nulled in a finally.
        // Moving either clear into a finally would turn it into a false positive AND, for
        // confirmRemove, into the page-killing mistake ExcludedFields exists to refuse.
        ScannerFalsePositives =
        [
            new ScannerFalsePositive("operationResult",
                "the result banner's backing field, not a busy signal. It became a reported stuck "
                + "flag as a DIRECT CONSEQUENCE of this page's own snapshot fix, which is worth "
                + "understanding before anyone 'corrects' it: the scanner counts a bare-token "
                + "assignment to a nullable as a raise (Get-ClickGateAudit.ps1:209), so the new "
                + "`operationResult = result;` publish reads as one, the `operationResult = null;` "
                + "at handler entry reads as the lowering, and that lowering is not in a finally. "
                + "Moving it into a finally to silence the scanner would clear the banner at the "
                + "END of the handler that just set it, so the operator would never see the "
                + "outcome. The scanner is wrong here and must stay wrong."),
        ],

        ExcludedFields =
        [
            new ExcludedField("confirmRemove", "the Yes/No pair at 82-83",
                "names the row whose deauthorization is staged; it is not an operation in flight. "
                + "The pair is rendered only while it is set, so folding it into IsBusy would "
                + "disable Yes at the only moment it is ever shown and no server could be "
                + "deauthorized again - the exact regression a review of the Migration plan caught "
                + "before any code was written"),
        ],

        ExemptControls =
        [
            new ExemptControl(40, "@onclick=\"() => operationResult = null\"",
                "dismisses the result banner. Gating it on IsBusy would trap the previous "
                + "operation's message on screen for the whole of a refresh or a CSV export, which "
                + "is the same reason Migration's banner dismiss at 442 is exempt. Neither "
                + "LoadServers nor DownloadCsvAsync reads operationResult, so a dismiss during "
                + "either is definitively executed",
                KeepsItsOwnGuard: "disabled=\"@isOperating\"",
                ConditionThatKeepsItTrue:
                "the handler stays a pure field reset. The moment anything else is wired to this "
                + "button it is an operation and belongs behind IsBusy like the rest",
                PrerequisiteBeforeExemptionHolds:
                "AuthorizeServer and RemoveServer must keep the write result in a local - "
                + "var result = await ...; operationResult = result; - and read that local "
                + "afterwards. Before they did, this control could null operationResult while "
                + "either handler was suspended at its admin-notification await, and the following "
                + "operationResult.Success read threw a NullReferenceException into the handler's "
                + "own catch, which then audited and emailed a forest-level AD write that had "
                + "SUCCEEDED as a failure and skipped the confirming refresh. The narrower guard "
                + "above is belt and braces only: the browser's copy of a disabled attribute is one "
                + "round trip stale, so the guard narrows that window and the local is what closes "
                + "it. Reinstate the field reads and this exemption is a live defect again"),

            new ExemptControl(83, "@onclick=\"() => confirmRemove = null\"",
                "backs out of a staged deauthorization, and the operator must always be able to do "
                + "that. The pair can still be on screen during another operation because "
                + "AuthorizeServer does not clear confirmRemove, and cancelling only resets a "
                + "staged selection - the Yes button beside it is gated, so nothing can be executed "
                + "from this state while the page is busy",
                ConditionThatKeepsItTrue:
                "nothing reads confirmRemove after an await. The moment a handler does, this "
                + "control can null it mid-flight and the exemption stops holding"),
        ],

        // Verified by exhaustive census, not inherited from the plan: this page has none. It is the
        // only tier-1 page of which that is true, which is why it was taken first.
        NonButtonTargets = [],

        ForbiddenGuardSites =
        [
            new ForbiddenGuardSite("LoadServers", ["AuthorizeServer", "RemoveServer"],
                "Both write handlers call it to refresh the server table while isOperating is still "
                + "true, so IsBusy is true at that call. A guard here makes every confirming "
                + "refresh a silent no-op: the banner reports the forest-level write succeeded "
                + "while the table still lists the old set of servers, and the operator concludes "
                + "it failed and retries."),
        ],

        AnnotatedControls =
        [
            new AnnotatedControl(50, "@onclick=\"DownloadCsvAsync\"",
                ["servers.Count == 0"],
                RendersOnlyWhen:
                "always. The clause is the only thing stopping an export of an empty list being "
                + "offered, and a mechanical rewrite to the bare predicate would delete it"),

            new AnnotatedControl(82, "@onclick=\"() => RemoveServer(server)\"",
                ["string.IsNullOrWhiteSpace(ticketNumber)"],
                RendersOnlyWhen:
                "only while confirmRemove == server. RemoveServer re-checks the ticket itself, so "
                + "this clause is not the sole enforcement the way IntuneDevices 403 is, but it is "
                + "still a precondition and not a busy condition"),

            new AnnotatedControl(121, "@onclick=\"AuthorizeServer\"",
                [
                    "string.IsNullOrWhiteSpace(newDnsName)",
                    "string.IsNullOrWhiteSpace(newIpAddress)",
                    "string.IsNullOrWhiteSpace(ticketNumber)",
                ],
                RendersOnlyWhen:
                "always. Three form-validity clauses that IsBusy is OR-ed in front of, never "
                + "substituted for"),
        ],

        SpinnerExpressions =
        [
            // Two spinners on this page and neither condition is the predicate. The refresh spinner
            // suppresses itself during an operation because the write handlers call LoadServers
            // themselves and the Authorize button is already showing one; collapsing either of these
            // to IsBusy shows two at once, and adding isDownloadingCsv to them puts a spinner on the
            // Refresh button during a CSV export.
            "@if (isLoading && !isOperating)",
            "@if (isOperating)",
        ],

        // Falsification 6 for this page, made executable. Two distinct shapes, both real here.
        //
        // operationResult is PublishedFromLocal: the dismiss at line 40 can null it while either
        // write handler is suspended at its admin-notification await. RemoveServer is the worse of
        // the two - a deauthorization is destructive - but both end the same way.
        //
        // The three form fields are CapturedAtEntry: the inputs carry disabled="@IsBusy", but the
        // browser's copy of that attribute is one round trip stale, so a keystroke landing in the
        // gap between the click and the render would otherwise reach the audit row and the admin
        // email. The capture is also what lets the catch audit these values at all - as locals
        // inside the try they would be out of scope there.
        PostAwaitLiveReads =
        [
            new PostAwaitLiveRead("AuthorizeServer", "operationResult", "result",
                SnapshotShape.PublishedFromLocal,
                "the dismiss control nulls operationResult, so reading the field after the write "
                + "await throws a NullReferenceException into this handler's own catch, which then "
                + "audits and emails a forest-level AD write that SUCCEEDED as a failure and skips "
                + "the confirming refresh"),

            new PostAwaitLiveRead("RemoveServer", "operationResult", "result",
                SnapshotShape.PublishedFromLocal,
                "same path as AuthorizeServer, on the destructive half: a successful "
                + "deauthorization reported and emailed as failed, and the table left showing the "
                + "server that is no longer authorized"),

            new PostAwaitLiveRead("AuthorizeServer", "newDnsName", "dns",
                SnapshotShape.CapturedAtEntry,
                "the DNS name the operator saw when the click was accepted is the one that must be "
                + "authorized, audited and emailed; a later keystroke must not retarget the write "
                + "or desync the audit row from it"),

            new PostAwaitLiveRead("AuthorizeServer", "newIpAddress", "ip",
                SnapshotShape.CapturedAtEntry,
                "as newDnsName: the address written to AD and the address in the audit row and the "
                + "admin email must be the same one"),

            new PostAwaitLiveRead("AuthorizeServer", "ticketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the ticket validated against ServiceNow must be the ticket recorded. Reading it "
                + "live also reads it back blank on the success path, where the handler clears the "
                + "form - the exact defect falsification 6 records on NamedLocations and "
                + "M365GroupManagement"),

            new PostAwaitLiveRead("RemoveServer", "ticketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "same obligation on the destructive handler, whose catch audits the ticket after "
                + "several awaits"),
        ],

        RaiseMustFollowEarlyReturn =
        [
            new RaiseAfterEarlyReturn("DownloadCsvAsync", "isDownloadingCsv", "servers.Count == 0",
                "the early return leaves no finally behind it, so a raise above the guard is never "
                + "lowered. isDownloadingCsv is a member of IsBusy, so it would not grey one button "
                + "- it would deaden every control on the page, permanently, the first time the "
                + "operator hit Download CSV on an empty list."),
        ],
    };

    /// <summary>
    /// Named Locations: tier 1, page 2 of 9, converted 2026-09-18 under
    /// docs/ClickGatingAudit-Plan.md Revision 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sequenced second because all eleven of its click targets are buttons. That was re-checked
    /// against the file rather than trusted from the plan, since Revision 1 falsification 3 is that
    /// the scanner's non-button count is an @onclick-only undercount. The hand census of everything
    /// the scanner cannot see finds no anchor, no div/span/td handler, no @onkeydown, no @onsubmit
    /// and no InputFile; what it does find is seven DOM-synced controls and one child component:
    /// the display-name input (136), the type select (143), the IP-ranges textarea (161), the
    /// trusted checkbox (164), the include-unknown checkbox (175) and the ticket input (182), plus
    /// CountryCodePicker at 172. All seven render server state into the DOM, so all seven take the
    /// disabled attribute - the only safe mechanism for a DOM-synced control - and all seven now
    /// name IsBusy rather than isOperating alone. They are absent from NonButtonTargets on purpose,
    /// following the DhcpAuthorization precedent: that list is keyed to what ClickGateSource can
    /// locate, and an input, a select, a textarea and a child component are in neither Tags("a")
    /// nor NonButtonClickTargets(), so registering one would fail
    /// <see cref="ClickGateTests.EveryNonButtonTargetIsRefusedByItsDeclaredMechanism"/> rather than
    /// document anything.
    /// </para>
    /// <para>
    /// CountryCodePicker was the hazard Revision 1 falsification 2 singled out as the worst in
    /// tier 1: it latches _initialized on its first parameter push (Components/Shared/
    /// CountryCodePicker.razor 45, 49-56) and Apply is the sole ValueChanged path (79-85), so a
    /// handler guard on it would desync the picker permanently and write the OLD country set to
    /// Graph. It needs no page-level workaround and no contract change: it already declares
    /// [Parameter] public bool Disabled and already applies it to all four of its interactive
    /// elements - the filter input (5), every country checkbox (15), Apply (32) and Clear (33).
    /// It is instantiated at exactly one site in the whole repo, NamedLocations.razor:172, so
    /// nothing outside this page is reached. The change here is one word: Disabled="@isOperating"
    /// became Disabled="@IsBusy".
    /// </para>
    /// <para>
    /// isDownloadingCsv is new, on the same shape as DhcpAuthorization's: DownloadCsvAsync owned no
    /// in-flight flag, and its raise has to sit BELOW the empty-list early return or the flag leaks
    /// true forever and, being a member of a page-wide predicate, deadens the whole page. See
    /// RaiseMustFollowEarlyReturn below.
    /// </para>
    /// <para>
    /// PostAwaitLiveReads is the larger half of this page's fix and the half no gate can do. Eleven
    /// obligations: the eight that make SaveLocation act on the form as it was when the click was
    /// accepted, the two on DeleteLocation, and the one that stops a mid-export refresh putting a
    /// different row count in the audit row than the file the operator received. The ticket capture
    /// is the defect falsification 6 names for this page by name - a blanked ticket read into the
    /// audit row - and the operationResult pair is what makes the banner dismiss at line 45 safe to
    /// leave clickable at all.
    /// </para>
    /// </remarks>
    private static PageGateEntry NamedLocations => new()
    {
        Page = "NamedLocations.razor",
        ExpectedLineCount = 538,

        Predicates =
        [
            new PredicateScope("IsBusy",
                ["isLoading", "isOperating", "isDownloadingCsv"],
                AppliesWhen: "the whole page"),
        ],

        // One page-wide predicate is right here, and that was tested rather than assumed: Revision
        // 1 found a single predicate provably wrong on two OTHER pages (SelfServiceGroups' two
        // mutually exclusive views, ConferenceRooms' background-callback jobs panel). This page has
        // one view. The list panel and the form panel render side by side and share every flag:
        // both write handlers finish by calling LoadLocations, which replaces the collection the
        // list renders AND invalidates editingLocation, which is a reference into the old list. So
        // there is no control here that is safe to leave live while another is working.
        ScannerFalsePositives =
        [
            new ScannerFalsePositive("operationResult",
                "the result banner's model. Get-ClickGateAudit.ps1 reports it as a stuck flag on "
                + "this page, and the report is an artefact of the fix rather than of a defect: the "
                + "field is nullable, so the scanner counts ANY bare-token assignment as a raise "
                + "(Get-ClickGateAudit.ps1:209), and the snapshot publish 'operationResult = result;' "
                + "is exactly that, while 'operationResult = null;' at handler entry is the lowering. "
                + "Raised and lowered in one awaiting method with the lowering outside a finally is "
                + "the stuck-flag shape. Moving the clear into a finally would wipe the banner the "
                + "instant the operation that wrote it finished. Note for whoever reads the audit "
                + "next: DhcpAuthorization now reports the same thing for the same reason, and the "
                + "'zero stuck flags' note on its entry above predates its own snapshot fix."),
        ],

        ExcludedFields =
        [
            new ExcludedField("confirmDelete", "the Confirm Delete / Cancel pair at 191-192",
                "holds the id of the location whose deletion is staged; it is not an operation in "
                + "flight. The pair renders only while confirmDelete == editingLocation.Id, so "
                + "folding it into IsBusy would disable Confirm Delete at the only moment it is "
                + "ever shown and no named location could be deleted again - the regression a "
                + "review of the Migration plan caught before any code was written"),

            new ExcludedField("showForm", "the whole right-hand form, 134-205",
                "means 'the operator is filling in a form', not 'the page is working'. Every "
                + "control that IsBusy exists to protect - the name, type, ranges, countries, "
                + "ticket and Save - is inside the block showForm gates, so folding it in would "
                + "disable the form at the only moment it renders"),
        ],

        ExemptControls =
        [
            new ExemptControl(45, "@onclick=\"() => operationResult = null\"",
                "dismisses the result banner. Gating it on IsBusy would trap the previous "
                + "operation's message on screen for the whole of a refresh or a CSV export, the "
                + "same reason Migration's dismiss at 442 and DhcpAuthorization's at 40 are exempt. "
                + "Neither LoadLocations nor DownloadCsvAsync reads operationResult, so a dismiss "
                + "during either is definitively executed",
                KeepsItsOwnGuard: "disabled=\"@isOperating\"",
                ConditionThatKeepsItTrue:
                "the handler stays a pure field reset. The moment anything else is wired to this "
                + "button it is an operation and belongs behind IsBusy with the rest",
                PrerequisiteBeforeExemptionHolds:
                "SaveLocation and DeleteLocation must keep the write result in a local - "
                + "var result = await ...; operationResult = result; - and read that local "
                + "afterwards. Before they did, this control could null operationResult while "
                + "either handler was suspended at its admin-notification await, and the following "
                + "operationResult.Success read threw a NullReferenceException into the handler's "
                + "own catch, which then audited and emailed a Conditional Access write that had "
                + "SUCCEEDED as a failure and skipped the confirming refresh. On DeleteLocation "
                + "that is a named location that really is gone being reported as still present. "
                + "The narrower guard above is belt and braces only: the browser's copy of a "
                + "disabled attribute is one round trip stale, so the guard narrows the window and "
                + "the local is what closes it. Reinstate the field reads and this exemption is a "
                + "live defect again"),

            new ExemptControl(192, "@onclick=\"() => confirmDelete = null\"",
                "backs out of a staged deletion, and the operator must always be able to do that. "
                + "It can still be on screen during a refresh or an export because neither clears "
                + "confirmDelete, and cancelling only resets a staged selection - Confirm Delete "
                + "beside it at 191 is gated, so nothing can be executed from this state while the "
                + "page is busy",
                KeepsItsOwnGuard: "disabled=\"@isOperating\"",
                ConditionThatKeepsItTrue:
                "nothing reads confirmDelete after an await. Both write handlers null it at entry "
                + "and again on success; the moment one READS it mid-flight, this control can null "
                + "it under them and the exemption stops holding"),
        ],

        // Verified by hand census, not inherited from the plan: the scanner finds no @onclick off a
        // button, and the seven DOM-synced controls it cannot see are covered by the disabled
        // attribute rather than by an entry here. See the remarks above for why they are not listed.
        NonButtonTargets = [],

        ForbiddenGuardSites =
        [
            new ForbiddenGuardSite("LoadLocations", ["SaveLocation", "DeleteLocation"],
                "Both write handlers call it to refresh the list while isOperating is still true, "
                + "so IsBusy is true at that call. A guard here makes every confirming refresh a "
                + "silent no-op: the banner says the Conditional Access write succeeded while the "
                + "table still shows the old set of named locations, and the operator concludes it "
                + "failed and retries. It is also the Refresh button's own handler, which is "
                + "refused in markup by disabled=\"@IsBusy\" and must not be refused twice."),
        ],

        AnnotatedControls =
        [
            new AnnotatedControl(55, "@onclick=\"DownloadCsvAsync\"",
                ["locations.Count == 0"],
                RendersOnlyWhen:
                "always. The clause is the only thing stopping an export of an empty list being "
                + "offered, and a mechanical rewrite to the bare predicate would delete it"),

            new AnnotatedControl(130, "@onclick=\"ShowCreateForm\"",
                [],
                RendersOnlyWhen:
                "only while !showForm - it is the placeholder panel's New Location button, and the "
                + "header carries an identical one at 60 that renders unconditionally. Registered "
                + "with no clause to preserve because the reachability is what the scanner cannot "
                + "see: 60 and 130 have byte-identical markup, so a reviewer reading either in "
                + "isolation cannot tell they are two controls rather than one moved line"),

            new AnnotatedControl(191, "@onclick=\"() => DeleteLocation(editingLocation)\"",
                ["string.IsNullOrWhiteSpace(formTicketNumber)"],
                RendersOnlyWhen:
                "only while confirmDelete == editingLocation.Id. DeleteLocation does not re-check "
                + "the ticket, so unlike DhcpAuthorization 82 this clause IS the only enforcement "
                + "of the ticket requirement on a deletion - the IntuneDevices 403 shape. It is "
                + "OR-ed with IsBusy and must never be replaced by it"),

            new AnnotatedControl(200, "@onclick=\"SaveLocation\"",
                [
                    "string.IsNullOrWhiteSpace(formDisplayName)",
                    "string.IsNullOrWhiteSpace(formTicketNumber)",
                ],
                RendersOnlyWhen:
                "always, while the form is open. Two form-validity clauses that IsBusy is OR-ed in "
                + "front of, never substituted for; SaveLocation re-checks neither"),
        ],

        SpinnerExpressions =
        [
            // Two spinners and neither condition is the predicate. The refresh spinner suppresses
            // itself while a location is being edited, because a save or a delete ends by calling
            // LoadLocations itself and the Save button is already showing one; collapsing either to
            // IsBusy shows two at once, and adding isDownloadingCsv to them puts a spinner on the
            // Refresh button during a CSV export.
            "@if (isLoading && editingLocation == null)",
            "@if (isOperating)",
        ],

        // Falsification 6 for this page, made executable. Two shapes, both real here.
        //
        // operationResult is PublishedFromLocal on both write handlers: the dismiss at line 45 is
        // exempt and can null it while either is suspended at its admin-notification await.
        //
        // The rest are CapturedAtEntry. The inputs carry disabled="@IsBusy", but the browser's copy
        // of that attribute is one round trip stale, and formTicketNumber is worse than stale - it
        // is blanked outright by EditLocation (308) and ShowCreateForm (295), which is exactly the
        // "blanked ticket read into the audit row" falsification 6 names for this page. The capture
        // is also what lets the catch audit these values: as locals inside the try they would be out
        // of scope there, which is why the pre-conversion catch re-read the live fields.
        PostAwaitLiveReads =
        [
            new PostAwaitLiveRead("SaveLocation", "operationResult", "result",
                SnapshotShape.PublishedFromLocal,
                "the dismiss control nulls operationResult, so reading the field after the write "
                + "await throws a NullReferenceException into this handler's own catch, which then "
                + "audits and emails a Conditional Access write that SUCCEEDED as a failure and "
                + "skips the confirming refresh"),

            new PostAwaitLiveRead("DeleteLocation", "operationResult", "result",
                SnapshotShape.PublishedFromLocal,
                "same path on the destructive half: a named location that really was deleted "
                + "reported and emailed as still present, and the table left listing it"),

            new PostAwaitLiveRead("SaveLocation", "editingLocation", "existing",
                SnapshotShape.CapturedAtEntry,
                "editingLocation decides Create versus Update, supplies the id the write targets "
                + "and the id in the audit row. CancelForm and EditLocation both reassign it, and "
                + "so does this handler's own success path - so a live read after an await could "
                + "audit one location's id against another's write, or dereference null inside the "
                + "catch, where an escaping NullReferenceException tears the circuit down"),

            new PostAwaitLiveRead("SaveLocation", "formDisplayName", "name",
                SnapshotShape.CapturedAtEntry,
                "the display name the operator saw when the click was accepted is the one that "
                + "must be written to Graph, audited and emailed; a later keystroke must not "
                + "rename the target or desync the audit row from it"),

            new PostAwaitLiveRead("SaveLocation", "formIpRanges", "values",
                SnapshotShape.CapturedAtEntry,
                "the CIDR list actually written to the Conditional Access policy. Read live, a "
                + "keystroke landing during the authorization round trip silently changes which "
                + "ranges become trusted"),

            new PostAwaitLiveRead("SaveLocation", "formCountryCodes", "values",
                SnapshotShape.CapturedAtEntry,
                "the country list written to the policy, and the field CountryCodePicker writes "
                + "through ValueChanged. Same hazard as formIpRanges; only one of the two is used, "
                + "chosen by isIp, and both are captured on the one line"),

            new PostAwaitLiveRead("SaveLocation", "formIsTrusted", "trusted",
                SnapshotShape.CapturedAtEntry,
                "whether the IP location is marked trusted, which is what makes it bypass policy. "
                + "A checkbox toggled during the authorization round trip must not change the "
                + "write the operator confirmed"),

            new PostAwaitLiveRead("SaveLocation", "formIncludeUnknown", "includeUnknown",
                SnapshotShape.CapturedAtEntry,
                "the country location's include-unknown flag, on the same footing as formIsTrusted"),

            new PostAwaitLiveRead("SaveLocation", "formTicketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the ticket recorded against the change. This is the defect falsification 6 names "
                + "for this page: EditLocation and ShowCreateForm blank the field, and the gate "
                + "cannot stop a keystroke in the round trip before the disabled attribute lands, "
                + "so the audit row and the admin email recorded a ticket that was not the one the "
                + "operator typed - or no ticket at all"),

            new PostAwaitLiveRead("DeleteLocation", "formTicketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the same obligation on the destructive handler, whose catch audits the ticket "
                + "after several awaits. loc needs no entry: it is a parameter, so it is already "
                + "captured at the click"),

            new PostAwaitLiveRead("DownloadCsvAsync", "locations", "rows",
                SnapshotShape.CapturedAtEntry,
                "LoadLocations replaces the collection wholesale, so a refresh landing while the "
                + "JS interop is in flight would put a row count in the audit row that does not "
                + "match the file the operator received. The CSV bytes were always built from a "
                + "local; the audit line was not"),
        ],

        RaiseMustFollowEarlyReturn =
        [
            new RaiseAfterEarlyReturn("DownloadCsvAsync", "isDownloadingCsv", "locations.Count == 0",
                "the early return leaves no finally behind it, so a raise above the guard is never "
                + "lowered. isDownloadingCsv is a member of IsBusy, so it would not grey one button "
                + "- it would deaden every control on the page, permanently, the first time the "
                + "operator hit Download CSV before the list had loaded."),
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

    /// <summary>
    /// Values a handler must NOT read live after its first await, each with the local that carries
    /// the value instead. Forced by docs/ClickGatingAudit-Plan.md Revision 1 falsification 6: on six
    /// of eleven pages the sharpest defect is not a gating defect at all but a post-await read of a
    /// field another control can change mid-flight. Gating narrows that window - the browser's copy
    /// of a disabled attribute is one round trip stale - and only the snapshot closes it, so the
    /// obligation needs its own slot rather than riding on a code comment.
    /// </summary>
    public IReadOnlyList<PostAwaitLiveRead> PostAwaitLiveReads { get; init; } = [];

    /// <summary>
    /// Raises that must sit BELOW an early return, because the flag is not lowered on that path.
    /// Forced by DhcpAuthorization.DownloadCsvAsync: its flag is a member of a page-wide predicate,
    /// so a raise above the empty-list guard leaks true forever and deadens the entire page rather
    /// than the one button it belongs to.
    /// </summary>
    public IReadOnlyList<RaiseAfterEarlyReturn> RaiseMustFollowEarlyReturn { get; init; } = [];
}

/// <summary>Where the local that replaces a live read gets its value.</summary>
public enum SnapshotShape
{
    /// <summary>
    /// Captured from the live field at handler entry, above the first await. The form-field case:
    /// the operator can still be typing, and the value the handler acts on and audits must be the
    /// one that was on screen when the click was accepted.
    /// </summary>
    CapturedAtEntry,

    /// <summary>
    /// Produced by the await itself and then published to the field. The result-banner case: the
    /// field exists to be rendered and can be nulled from outside by a dismiss control, so the
    /// handler keeps its own copy and never dereferences the field it just wrote.
    /// </summary>
    PublishedFromLocal,
}

/// <param name="Handler">The method that must not read <paramref name="LiveField"/> after its first await.</param>
/// <param name="LiveField">The page field another control can change while this handler is suspended.</param>
/// <param name="Snapshot">The local that carries the value instead.</param>
/// <param name="Shape">Where <paramref name="Snapshot"/> gets its value; see <see cref="SnapshotShape"/>.</param>
/// <param name="Why">What goes wrong if the live read comes back, in operator-visible terms.</param>
public sealed record PostAwaitLiveRead(
    string Handler,
    string LiveField,
    string Snapshot,
    SnapshotShape Shape,
    string Why);

/// <param name="EarlyReturn">
/// The guard condition verbatim, as it appears inside <c>if (...)</c>. Verbatim rather than
/// reconstructed, in the same spirit as <see cref="PageGateEntry.SpinnerExpressions"/>: if the
/// guard is reworded the assertion fails and someone re-checks the ordering by hand.
/// </param>
public sealed record RaiseAfterEarlyReturn(
    string Handler,
    string Flag,
    string EarlyReturn,
    string Consequence);

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
/// <param name="PrerequisiteBeforeExemptionHolds">
/// A change elsewhere in the page that had to land BEFORE this exemption was safe to grant, named
/// so the exemption cannot outlive its own precondition. Forced by DhcpAuthorization line 40: its
/// banner dismiss nulls operationResult, which both write handlers used to dereference after an
/// await, so leaving it clickable was a live NullReferenceException path and not merely a gating
/// gap. It is exempt only because those two handlers now read a local snapshot instead.
/// <para>
/// This is prose, but it is not only prose. Setting it obliges the page to carry a
/// <see cref="PostAwaitLiveRead"/> for the field the control writes, and
/// <see cref="ClickGateTests.EveryExemptionPrerequisiteIsEnforcedAndNotJustDescribed"/> re-runs
/// that obligation's assertion here, so the exemption and the snapshot that justifies it cannot
/// drift apart. The tie is the field name, read out of <paramref name="Snippet"/>.
/// </para>
/// </param>
public sealed record ExemptControl(
    int Line,
    string Snippet,
    string Reason,
    string? KeepsItsOwnGuard = null,
    string? ConditionThatKeepsItTrue = null,
    string? PrerequisiteBeforeExemptionHolds = null);

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
