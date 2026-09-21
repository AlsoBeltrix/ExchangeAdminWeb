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
        MailboxPermissions,
        CalendarPermissions,
        IntuneDevices,
        DefenderEndpointDevices,
        GroupManagement,
        M365GroupManagement,
        ConferenceRooms,
        SelfServiceGroups,
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
            // All nine tier-1 pages - DhcpAuthorization.razor, NamedLocations.razor,
            // MailboxPermissions.razor, CalendarPermissions.razor, IntuneDevices.razor,
            // GroupManagement.razor, M365GroupManagement.razor, ConferenceRooms.razor and
            // SelfServiceGroups.razor - are converted and live in Pages above. Nothing below this
            // line is approved for conversion without an owner go.

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
    /// <remarks>
    /// One thing this entry records as FOUND rather than as approved, so nobody reads the reference
    /// page as the pattern for the new field. Twelve of its sixteen DOM-synced controls are gated on
    /// a single flag - disabled="@isLoading" or disabled="@isCreating" - not on IsBusy, because this
    /// page was converted under docs/MigrationButtonGating-Plan.md before the page-wide ruling that
    /// the tier-1 pages are being converted under. Its buttons DO name IsBusy, as do the two
    /// Enter-key inputs gated at 390 and 821; those twelve form controls do not.
    /// <see cref="ClickGateTests.EveryDomSyncedControlStillCarriesItsRegisteredGate"/> therefore
    /// requires the registered expression to name a busy signal this page registers - a predicate
    /// OR one of its flags - rather than the page predicate itself. Requiring the predicate would
    /// fail this page, and the fix for that is a decision about Migration.razor, not a test.
    /// </remarks>
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

        // Sixteen DOM-synced controls, censused against the file when DomSyncedControls was added.
        // Fourteen are gated and two are not; see the remarks on this entry for what that means and
        // what it does not mean. The twelve form-control gates here name ONE flag rather than
        // IsBusy, which is this page's own pre-ruling shape and is registered as found rather than
        // as approved. The two Enter-key inputs at 390 and 821 are the exception: they name IsBusy,
        // because they were gated later to close the keydown paths recorded below.
        DomSyncedControls =
        [
            new DomSyncedControl(50, "@bind=\"singleEmail\"", "input", "disabled=\"@isLoading\""),
            new DomSyncedControl(54, "@bind=\"singleMigrationDirection\"", "select",
                "disabled=\"@isLoading\""),
            new DomSyncedControl(61, "@bind=\"singleTicketNumber\"", "input", "disabled=\"@isLoading\""),
            new DomSyncedControl(154, "@bind=\"singleBatchName\"", "input", "disabled=\"@isCreating\""),
            new DomSyncedControl(160, "@bind=\"singleAutoStart\"", "input", "disabled=\"@isCreating\""),
            new DomSyncedControl(164, "@bind=\"singleAutoComplete\"", "input", "disabled=\"@isCreating\""),
            new DomSyncedControl(222, "OnChange=\"HandleCsvUpload\"", "InputFile",
                "disabled=\"@isLoading\""),
            new DomSyncedControl(228, "@bind=\"migrationDirection\"", "select", "disabled=\"@isLoading\""),
            new DomSyncedControl(235, "@bind=\"bulkTicketNumber\"", "input", "disabled=\"@isLoading\""),
            new DomSyncedControl(324, "@bind=\"bulkBatchName\"", "input", "disabled=\"@isCreating\""),
            new DomSyncedControl(330, "@bind=\"bulkAutoStart\"", "input", "disabled=\"@isCreating\""),
            new DomSyncedControl(334, "@bind=\"bulkAutoComplete\"", "input", "disabled=\"@isCreating\""),

            // Gated 2026-09-18, closing the two Enter-key holes this entry had recorded as found
            // rather than granted. Each input carries an @onkeydown whose handler reaches the very
            // operation the button beside it refuses - HandleSearchKeyDown (1742) calls SearchUser
            // with no busy guard while Find at 400 is gated, and HandleConfirmKeyDown (1429) calls
            // ConfirmPendingAction with no busy guard while Confirm at 825 is gated. A disabled
            // input fires no keydown, so the attribute closes the handler path as well as the
            // typing path; a handler guard would not, and on a control that renders server state it
            // would leave the browser holding a change the server refused (docs/ClickGatingAudit-
            // Plan.md Revision 1, falsification 2). Neither input had a gate to widen or OR - both
            // were bare - so both take the page predicate whole.
            //
            // IsBusy and not IsBusy || pendingActionLabel != null for 821: pendingActionLabel is a
            // registered ExcludedField, and folding it in would disable the ticket box at the only
            // moment it is ever rendered. The gate matches Confirm at 825 exactly, less that
            // button's own emptiness clause, which a field cannot apply to itself.
            new DomSyncedControl(390, "placeholder=\"Search batch or user email...\"", "input",
                "disabled=\"@IsBusy\""),
            new DomSyncedControl(821, "placeholder=\"Ticket # (required)\"", "input",
                "disabled=\"@IsBusy\""),
        ],

        UngatedDomSyncedControls =
        [
            new UngatedDomSyncedControl(521, "title=\"Select all loaded batches\"", "input",
                "left live on purpose, and the purpose is written into the page. The selection "
                + "toolbar comment at 455-459 records owner ruling D2(a): the bulk-action buttons "
                + "are never conditioned on eligibility, and the staged callback re-plans from the "
                + "LIVE selection (1346) rather than from a snapshot, so the tick boxes are an input "
                + "to the confirm step and not a value in flight. Nothing can be executed from this "
                + "state while the page is busy - all three action buttons carry "
                + "disabled=\"@(IsBusy || pendingActionLabel != null)\" (464, 469, 474)"),

            new UngatedDomSyncedControl(544, "title=\"Select for a bulk action\"", "input",
                "the per-row half of the same selection, on the same owner ruling and the same "
                + "re-plan at 1346. ToggleBatchSelected (1281) writes only to selectedBatches and "
                + "nulls the result banner; it makes no call and awaits nothing"),
        ],

        // The two paths commit 2eb8c15 closed, now data rather than a comment on the DomSyncedControl
        // entries above. This is the only converted page with a keyboard handler of any kind: the
        // hand census behind DhcpAuthorization and NamedLocations found none on either, and that
        // claim is now enforced by the forward direction of
        // EveryKeyboardPathIsRegisteredOrRecordedHarmless rather than resting on prose.
        //
        // Both take the disabled attribute rather than a handler guard. A disabled input fires no
        // key events, so one attribute closes the key path and the typing path together; a handler
        // guard would close only the key path, leave the operator pressing Enter into silence, and
        // still need the attribute for the @bind. Neither input had a gate to widen or OR - both
        // were bare - so both take the page predicate whole.
        KeyboardPaths =
        [
            new KeyboardPath(390, "placeholder=\"Search batch or user email...\"", "input",
                "keydown", "HandleSearchKeyDown", "SearchUser",
                KeyboardRefusal.DisabledAttribute, "disabled=\"@IsBusy\"",
                "Enter runs the batch/user search that the Find button at 400 refuses while the page "
                + "is busy. SearchUser raises isSearching, replaces the expanded batch's user rows "
                + "and calls into Exchange twice, so a second one started from the keyboard during "
                + "the first races it and the later reply wins",
                GatedTwinButtonLine: 400),

            new KeyboardPath(821, "placeholder=\"Ticket # (required)\"", "input",
                "keydown", "HandleConfirmKeyDown", "ConfirmPendingAction",
                KeyboardRefusal.DisabledAttribute, "disabled=\"@IsBusy\"",
                "Enter EXECUTES the staged action - a batch start, stop or remove, the destructive "
                + "half of this page - which the Confirm button at 825 refuses while busy. It "
                + "avoided double execution only by accident, and the accident is worth writing "
                + "down: ConfirmPendingAction calls CancelPendingAction (1435) BEFORE awaiting the "
                + "staged callback, which nulls pendingActionLabel and so unrenders the whole "
                + "PendingActionConfirm fragment, and blanks pendingActionTicket so a second press "
                + "fails the handler's own emptiness check. Nothing in that is a busy gate, and "
                + "nothing in it survives a handler that stages differently",
                GatedTwinButtonLine: 825),
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
    /// document anything. That much was right; the conclusion drawn from it - that nothing could
    /// check these - was not. They are in <see cref="PageGateEntry.DomSyncedControls"/> below, which
    /// is NonButtonTargets' counterpart for the controls the disabled attribute does reach.
    /// </para>
    /// <para>
    /// The "no @onkeydown" half of that census is no longer prose either. This page registers no
    /// <see cref="PageGateEntry.KeyboardPaths"/> and no
    /// <see cref="PageGateEntry.HarmlessKeyboardPaths"/>, and the forward direction of
    /// <see cref="ClickGateTests.EveryKeyboardPathIsRegisteredOrRecordedHarmless"/> is what keeps
    /// that true: a keyboard handler added here later fails the suite and names itself.
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

        // The three @bind inputs the remarks above describe in prose. They are here rather than in
        // NonButtonTargets because that list is keyed to what ClickGateSource can locate and an
        // input is in neither of its tag sets; the prose was correct about the mechanism and wrong
        // only in concluding that nothing could check it.
        DomSyncedControls =
        [
            new DomSyncedControl(110, "@bind=\"newDnsName\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(114, "@bind=\"newIpAddress\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(118, "@bind=\"ticketNumber\"", "input", "disabled=\"@IsBusy\""),
        ],

        UngatedDomSyncedControls = [],

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
    /// document anything. True, and the reason all seven were left in prose - but the prose was the
    /// only thing holding them, and a mutation that stripped 172's Disabled="@IsBusy" left the whole
    /// suite green. They are now in <see cref="PageGateEntry.DomSyncedControls"/> below.
    /// </para>
    /// <para>
    /// The "no @onkeydown" half of that census is no longer prose either, on the same footing as
    /// DhcpAuthorization: this page registers no <see cref="PageGateEntry.KeyboardPaths"/> and no
    /// <see cref="PageGateEntry.HarmlessKeyboardPaths"/>, and the forward direction of
    /// <see cref="ClickGateTests.EveryKeyboardPathIsRegisteredOrRecordedHarmless"/> fails if one
    /// arrives. CountryCodePicker is worth a word here because it is the one child component on a
    /// converted page: it carries no keyboard handler of its own either, so there is nothing this
    /// page-level scan is failing to see through it today.
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

        // The seven controls the remarks above enumerate in prose, now data. 172 is the one that
        // forced this field to exist: the M6 mutation of the page-2 conversion stripped its
        // Disabled="@IsBusy" and the whole suite stayed green.
        DomSyncedControls =
        [
            new DomSyncedControl(136, "@bind=\"formDisplayName\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(143, "@bind=\"formType\"", "select", "disabled=\"@IsBusy\""),
            new DomSyncedControl(161, "@bind=\"formIpRanges\"", "textarea", "disabled=\"@IsBusy\""),
            new DomSyncedControl(164, "@bind=\"formIsTrusted\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(172, "@bind-Value=\"formCountryCodes\"", "CountryCodePicker",
                "Disabled=\"@IsBusy\""),
            new DomSyncedControl(175, "@bind=\"formIncludeUnknown\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(182, "@bind=\"formTicketNumber\"", "input", "disabled=\"@IsBusy\""),
        ],

        UngatedDomSyncedControls =
        [
            new UngatedDomSyncedControl(153, "NamedLocationType.Ip ? \"IP Ranges\"", "input",
                "inert. It is the read-only echo of an existing location's type, rendered only in "
                + "the editingLocation != null branch: no @bind, no @onchange, no handler of any "
                + "kind, and a static `disabled` with no expression. There is nothing here for a "
                + "click to desync, and gating it on IsBusy would change nothing an operator can "
                + "see. Its value attribute reads editingLocation, which is a registered "
                + "PostAwaitLiveReads field - that obligation is what protects the value, not this "
                + "control"),
        ],

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

    /// <summary>
    /// Mailbox Permissions: tier 1, page 3 of 9, converted 2026-09-18 under
    /// docs/ClickGatingAudit-Plan.md Revision 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The snapshots matter more than the gate on this page, and that is not the usual ordering.</b>
    /// Both write targets are chosen through a RecipientAutocomplete, which gates only its inner
    /// input: its suggestion rows are &lt;li @onmousedown="() =&gt; SelectResult(result)"&gt; and
    /// consult the Disabled parameter nowhere. Passing Disabled stops the operator typing and stops
    /// the component's own Enter path, but a dropdown that was already open when the gate closed
    /// keeps clickable rows, and clicking one rewrites targetMailbox or affectedUser mid-write. The
    /// blur-that-would-close-it on disable is browser-dependent and no safety argument here rests on
    /// it. What actually stops that click retargeting the write, the audit row and the two
    /// notification emails is the entry capture in
    /// <see cref="PageGateEntry.PostAwaitLiveReads"/> - after which the worst a late suggestion
    /// click can do is dirty the form for the NEXT submission.
    /// </para>
    /// <para>
    /// <b>CLOSED since this entry was written. The shared components are fixed; the paragraph above
    /// describes the hazard as it stood at page 3 and is kept because the reasoning still explains
    /// why the entry captures exist.</b> The contract change landed as its own slice: all three
    /// autocompletes now withhold their suggestion rows while Disabled AND refuse in SelectResult
    /// on entry, belt and braces, because withholding alone loses the round-trip race and a handler
    /// guard alone leaves visibly clickable rows that silently do nothing. Base app version bumped;
    /// no module version moved. What follows is the census as found, and it was short by one.
    /// Revision 1 schedules one contract change, ADIdentityAutocomplete, at page 6. There were three.
    /// ADIdentityAutocomplete is instantiated 9 times across 4 pages (AdminSettings 144/172/200/259,
    /// GroupManagement 111, ModuleConfig 217/317/615, SelfServiceGroups 202) and is NOT on this page
    /// at all. RecipientAutocomplete - the one this page uses - is instantiated 8 times across 4
    /// pages: MailboxPermissions 58/66, CalendarPermissions 46/53, ConferenceRooms 70/260,
    /// OutOfOffice 40/103. Both have the identical shape: gated input at their line 13, ungated
    /// suggestion rows just below it. So page 6 inherits two contract changes, and
    /// RecipientAutocomplete's reaches two more tier-1 pages (CalendarPermissions, ConferenceRooms)
    /// plus OutOfOffice, which is tier 3 and unapproved. Counts verified against the repo, not
    /// inherited from the plan.
    /// </para>
    /// <para>
    /// <b>Ten DOM-synced controls, not the six Revision 1 predicted.</b> The prediction named three
    /// @bind checkboxes, two radios and one InputFile. The hand census adds the single-mailbox
    /// ticket input (74), the bulk ticket input (127) and both RecipientAutocomplete instances,
    /// which take a Disabled parameter and were being passed nothing. All ten now carry the disabled
    /// attribute, which for a control rendering server state is the only safe refusal: refuse in the
    /// handler instead and the backing field is unchanged, the render diff emits no correction, and
    /// the browser keeps a permission the operator visibly deselected (falsification 2).
    /// </para>
    /// <para>
    /// <b>The exactly-one-guard constraint, resolved: ConfirmOnPrem carries the guard, ExecuteOnPrem
    /// must never carry one.</b> Said as a rule rather than as a preference, because the next reader
    /// has to be able to tell a correct page from a broken one without re-deriving the flow.
    /// ConfirmOnPrem is the method the Continue click reaches and the method whose button carries
    /// disabled="@IsBusy", so belt and braces sit on one control instead of being split across two
    /// methods; ExecuteOnPrem raises isLoading as its own first statement, so a guard above that
    /// raise would be a re-entrancy guard on itself, which is the shape the 2026-09-17 ruling says
    /// is not enough. Both halves are now enforced, and both were queued rather than shipped when
    /// this entry was first written. The ForbiddenGuardSite below is the negative half - no guard in
    /// ExecuteOnPrem - and <see cref="PageGateEntry.ExactlyOneGuard"/> is the positive half, which
    /// nothing else here could state: without it, deleting ConfirmOnPrem's guard outright passed the
    /// whole suite. <b>The negative half's enforcement used to be partial, and that was measured
    /// rather than assumed:</b> <see cref="ClickGateTests.ForbiddenGuardSitesCarryNoGuard"/> matched
    /// the literal <c>if (IsBusy) return;</c>, so planting a COMPOUND guard in ExecuteOnPrem - the
    /// shape ConfirmOnPrem itself uses - slipped past it at 109 passed / 0 failed. Probe M6 of this
    /// conversion demonstrated exactly that, and the assertion now matches the guard shape rather
    /// than one spelling of it.
    /// </para>
    /// <para>
    /// ConfirmOnPrem's guard has two clauses and they refuse two different things. IsBusy is the
    /// click gate: the browser's copy of the Continue button's disabled attribute is one round trip
    /// stale, so a second Continue can still be dispatched while the first on-prem write is in
    /// flight. !onPremConfirmPending is a staleness gate and closes a live defect found during this
    /// conversion rather than a hypothetical: SetTab and CancelOnPrem both clear that field, so
    /// before this a Continue click that left the browser before the confirmation panel unrendered
    /// still executed - and because ExecuteOnPrem reads the tab at its own entry, a tab flip
    /// followed by a stale Continue ran a REMOVE against a staged ADD, on the destructive on-prem
    /// path.
    /// </para>
    /// <para>
    /// <b>SubmitSingle and ProcessBulk both had their try extended upward.</b> Each raised isLoading
    /// and then awaited the re-authorization - SubmitSingle also the ticket validation and the
    /// protected-principal check - BEFORE entering its try, so those awaits had no finally behind
    /// them and
    /// <see cref="ClickGateTests.NoRealAwaitSitsBetweenARaiseAndItsProtectingTry"/> failed the page
    /// as written. Neither handler ends by calling a refresh helper - SubmitSingle ends at
    /// result = opResult, ProcessBulk at the admin notification - so a finally is the right shape
    /// for both and the slice-1a hazard, a lowering moved past a call a future guard will no-op,
    /// does not arise. The eight scattered pre-return isLoading = false lines were traced one by one
    /// before deletion: seven are immediately followed by return with nothing between, and the
    /// eighth (the on-prem staging path) sits after onPremConfirmPending = true, where the finally
    /// preserves the relative order so Continue still renders enabled.
    /// </para>
    /// <para>
    /// <b>The obligation this entry used to record as deliberately absent is now registered.</b>
    /// autoMapping was always captured at entry into autoMap and every use below the first await
    /// always read the local - the hazard falsification 2 describes was closed in the code - but the
    /// obligation could not be written down, because AuditService.LogMailboxPermission takes a
    /// parameter of that name and the success-path call site passes it by name, and
    /// ClickGateTests.ReadOf could not tell the label <c>autoMapping:</c> from a field read. The
    /// remedy taken is the one that entry named: ReadOf now excludes <c>identifier:</c> in argument
    /// position, exactly as it already excluded assignment. The rejected alternative is recorded
    /// because it is the tempting one - bending the call site into a ninth positional boolean would
    /// have deformed readable code to satisfy a text matcher, and the matcher is what was wrong.
    /// </para>
    /// <para>
    /// <b>Note for whoever next reads Get-ClickGateAudit.ps1 output.</b> It reports this page as
    /// "Predicate: -" and counts the seven predicate-gated buttons as GateNoFlag, i.e. an 11-of-11
    /// gap, on a page that is fully converted. That is a scanner limitation and not a defect: its
    /// predicate detector requires an expression-bodied bool naming at least TWO in-flight flags,
    /// and this is the first converted page with only one. Do not "fix" the page to satisfy it, and
    /// do not read the gap column for this page as work outstanding. It reports zero stuck flags,
    /// which is why ScannerFalsePositives is empty here while pages 1 and 2 both carry an
    /// operationResult entry.
    /// </para>
    /// <para>
    /// SetTab takes no handler guard, recorded rather than overlooked. Its three buttons are real
    /// &lt;button&gt; elements, so the disabled attribute reaches them, and after the snapshots a
    /// stale tab click during a write is harmless: tabIndex no longer steers the in-flight write,
    /// result and bulkResult are re-published by the handler that is running, and
    /// onPremConfirmPending has already been cleared by ConfirmOnPrem.
    /// </para>
    /// </remarks>
    private static PageGateEntry MailboxPermissions => new()
    {
        Page = "MailboxPermissions.razor",
        ExpectedLineCount = 730,

        // One flag, one page-wide predicate, and that was checked rather than assumed: Revision 1
        // found a single predicate provably wrong on two OTHER pages. This page has one view. The
        // tab strip, the single-mailbox form, the bulk form, the on-prem confirmation and both
        // result banners render off the same state at the same time, and every write handler
        // replaces part of it - SubmitSingle can stage the on-prem panel, ProcessBulk replaces
        // bulkResult, ExecuteOnPrem nulls onPremTarget. There is no control here that is safe to
        // leave live while another is working.
        Predicates =
        [
            new PredicateScope("IsBusy", ["isLoading"], AppliesWhen: "the whole page"),
        ],

        // Empty on purpose and confirmed by running the scanner after the conversion, not before:
        // Get-ClickGateAudit.ps1 reports zero stuck flags here. result and bulkResult are both
        // nullables nulled at handler entry and assigned later in the same awaiting method, which is
        // the shape that made operationResult a reported false positive on pages 1 and 2, and
        // neither is reported here. Nothing was suppressed to achieve that.
        ScannerFalsePositives = [],

        ExcludedFields =
        [
            new ExcludedField("onPremConfirmPending", "the Continue/Cancel pair at 158-162",
                "means 'an on-premises write is staged and waiting for the operator', not 'the page "
                + "is working'. The pair renders only while it is true, so folding it into IsBusy "
                + "would disable Continue at the only moment it is ever shown and no on-prem mailbox "
                + "permission could be changed again - the regression a review of the Migration plan "
                + "caught before any code was written. ConfirmOnPrem reads it as a staleness guard, "
                + "which is a different job from the busy gate and belongs in the handler"),
        ],

        ExemptControls =
        [
            new ExemptControl(135, "@onclick=\"DownloadSampleCsv\"",
                "serves a compile-time constant string through JS interop. It touches no page state, "
                + "reads no field and calls nothing that could be in flight - the same grant as "
                + "Migration's sample CSV at 223",
                ConditionThatKeepsItTrue:
                "DownloadSampleCsv must not grow a call into Exchange or a read of any page field. "
                + "The moment it does it is an operation and belongs behind IsBusy with the rest"),

            new ExemptControl(162, "@onclick=\"CancelOnPrem\"",
                "backs out of a staged on-premises write, and the operator must always be able to do "
                + "that. Continue beside it at 158 is gated and ConfirmOnPrem refuses a stale click, "
                + "so nothing can be executed from this state while the page is busy; cancelling only "
                + "resets the staged selection",
                ConditionThatKeepsItTrue:
                "ExecuteOnPrem must keep capturing onPremTarget at entry. This control nulls that "
                + "field, and the handler's catch audits the target after several awaits, so a live "
                + "read there would dereference null inside a catch - where, with no ErrorBoundary "
                + "anywhere in this app, the escaping exception tears the circuit down. The "
                + "obligation is registered under PostAwaitLiveReads; it is recorded here rather than "
                + "as a PrerequisiteBeforeExemptionHolds because this snippet names a method rather "
                + "than assigning a field, so there is nothing for that field to tie to"),

            new ExemptControl(179, "@onclick=\"() => result = null\"",
                "dismisses the single-operation result banner. Gating it on IsBusy would trap the "
                + "previous operation's message on screen for the whole of the next one, the same "
                + "reason Migration's dismiss at 442, DhcpAuthorization's at 40 and NamedLocations' "
                + "at 45 are exempt",
                ConditionThatKeepsItTrue:
                "no handler may READ result back. Today none does: SubmitSingle and ExecuteOnPrem "
                + "both hold their outcome in a local opResult and only ever assign to result, so "
                + "this control cannot null the field out from under a dereference. That is why this "
                + "exemption carries no PrerequisiteBeforeExemptionHolds and no PostAwaitLiveReads "
                + "entry names result - there is no live read to close. The moment a handler reads "
                + "result after an await, this exemption becomes the DhcpAuthorization 40 defect: a "
                + "write that SUCCEEDED audited and emailed as failed"),

            new ExemptControl(212, "@onclick=\"() => bulkResult = null\"",
                "dismisses the bulk-operation summary, on the same footing as the banner above. The "
                + "summary can still be on screen during an unrelated single-mailbox write, because "
                + "SubmitSingle does not clear bulkResult",
                ConditionThatKeepsItTrue:
                "ProcessBulk must keep publishing its summary from a local rather than reading the "
                + "field back",
                PrerequisiteBeforeExemptionHolds:
                "ProcessBulk must keep the CSV outcome in a local - var bulk = await ...; "
                + "bulkResult = bulk; - and read that local for the audit row, the failure detail "
                + "and the admin email. Before it did, this control could null bulkResult while the "
                + "handler was suspended, and the following bulkResult.FailedCount read would throw "
                + "a NullReferenceException into ProcessBulk's own catch, which reports a bulk run "
                + "that SUCCEEDED as a bare exception message and skips the admin notification "
                + "entirely. Reinstate the field reads and this exemption is a live defect again"),
        ],

        // Verified by exhaustive census rather than inherited: Get-ClickGateAudit.ps1 finds no
        // @onclick off a button on this page, and the hand census of what it cannot see finds no
        // anchor, no div/span/td handler, no @onsubmit and no @onkeydown. What it does find is ten
        // DOM-synced controls, which are below - they are absent from this list on purpose, on the
        // DhcpAuthorization and NamedLocations precedent: this list is keyed to what
        // ClickGateSource can locate, and an input, an InputFile and a child component are in
        // neither Tags("a") nor NonButtonClickTargets(), so registering one here would fail
        // EveryNonButtonTargetIsRefusedByItsDeclaredMechanism rather than document anything.
        NonButtonTargets = [],

        DomSyncedControls =
        [
            new DomSyncedControl(47, "@bind=\"grantFullAccess\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(51, "@bind=\"grantSendAs\"", "input", "disabled=\"@IsBusy\""),

            // The two write-target pickers. Disabled= reaches their inner input and their own Enter
            // key path; it does NOT reach their suggestion rows. See the remarks on this entry.
            new DomSyncedControl(58, "@bind-Value=\"targetMailbox\"", "RecipientAutocomplete",
                "Disabled=\"@IsBusy\""),
            new DomSyncedControl(66, "@bind-Value=\"affectedUser\"", "RecipientAutocomplete",
                "Disabled=\"@IsBusy\""),

            new DomSyncedControl(74, "id=\"ticketNumber\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(83, "@bind=\"autoMapping\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(119, "id=\"radioAdd\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(121, "id=\"radioRemove\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(127, "id=\"bulkTicket\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(134, "OnChange=\"HandleCsvUpload\"", "InputFile",
                "disabled=\"@IsBusy\""),
        ],

        UngatedDomSyncedControls = [],

        ForbiddenGuardSites =
        [
            new ForbiddenGuardSite("ExecuteOnPrem", ["ConfirmOnPrem"],
                "The exactly-one-guard constraint Revision 1 records for this page and its twin. "
                + "ConfirmOnPrem owns the refusal and is ExecuteOnPrem's only caller. A guard here "
                + "is redundant today only because ConfirmOnPrem raises no flag before calling: the "
                + "moment it does - the obvious next improvement - this guard turns the entire "
                + "on-prem write into a silent no-op, and nothing says so. No banner, no audit row, "
                + "no admin email, and the operator concludes Exchange refused the change and "
                + "retries. Guarding neither instead leaves Continue open to a second dispatch in "
                + "the round trip before its disabled attribute lands, which executes the "
                + "destructive on-prem write twice."),

            new ForbiddenGuardSite("ReauthorizeAsync", ["SubmitSingle", "ProcessBulk"],
                "The shared pre-write authorization re-check, called by both write handlers AFTER "
                + "they raise isLoading, so IsBusy is true at every call. A guard here makes the "
                + "re-check return false on every single submission and every bulk run: the page "
                + "would report 'Authorization denied.' for work the operator is entitled to do, "
                + "and no mailbox permission could be changed at all."),
        ],

        // The positive half of the exactly-one-guard constraint the remarks above state as a rule.
        // The ForbiddenGuardSite for ExecuteOnPrem is the negative half and was all this registry
        // had: on its own, DELETING ConfirmOnPrem's guard passed the whole suite, because a page
        // with no guard anywhere satisfies a rule that only says where a guard may not be. Both
        // halves are now enforced, and a size-preserving move of the guard from one method to the
        // other flips them together.
        ExactlyOneGuard =
        [
            new ExactlyOneGuardOf("ConfirmOnPrem", ["ExecuteOnPrem"],
                "if (IsBusy || !onPremConfirmPending)",
                "ConfirmOnPrem is the method the Continue click reaches and the method whose button "
                + "carries disabled=\"@IsBusy\", so belt and braces sit on one control rather than "
                + "being split across two methods. ExecuteOnPrem raises isLoading as its own first "
                + "statement, so a guard above that raise would be a re-entrancy guard on itself, "
                + "which the 2026-09-17 ruling says is not enough; and because ConfirmOnPrem is its "
                + "only caller, a guard there becomes a silent no-op the moment ConfirmOnPrem raises "
                + "the flag before calling - the obvious next improvement - with no banner, no audit "
                + "row and no admin email to say the on-prem write did not happen. The two clauses "
                + "refuse two different things and both are load-bearing: IsBusy is the click gate, "
                + "because the browser's copy of the Continue button's disabled attribute is one "
                + "round trip stale and a second Continue would run the destructive write twice; "
                + "!onPremConfirmPending is the staleness gate, and it closes a live defect found "
                + "during the conversion rather than a hypothetical - SetTab and CancelOnPrem both "
                + "clear that field, and ExecuteOnPrem reads the tab at its own entry, so a tab flip "
                + "followed by a stale Continue ran a REMOVE against a staged ADD"),
        ],

        AnnotatedControls =
        [
            new AnnotatedControl(90, "@onclick=\"SubmitSingle\"",
                ["!IsFormValid"],
                RendersOnlyWhen:
                "only while tabIndex < 2 - it is the Add/Remove tab's submit, and the bulk tab "
                + "renders its own at 138 instead. IsFormValid (target, user, ticket and at least "
                + "one permission all present) is a form-validity precondition that IsBusy is OR-ed "
                + "in front of, never substituted for. SubmitSingle re-checks none of it, so this "
                + "clause is the only enforcement - the IntuneDevices 403 shape"),

            new AnnotatedControl(138, "@onclick=\"ProcessBulk\"",
                ["csvFile is null", "string.IsNullOrWhiteSpace(bulkTicketNumber)"],
                RendersOnlyWhen:
                "only while tabIndex == 2. ProcessBulk re-checks the file itself - that null check "
                + "is what the registered RaiseAfterEarlyReturn below is about - but it does NOT "
                + "re-check the ticket before validating it, so the emptiness clause is a real "
                + "precondition and not decoration"),
        ],

        // Three per-button spinners, all reading the flag rather than the predicate, registered so a
        // mechanical "simplify to the predicate" pass fails instead of quietly changing behaviour.
        // Today isLoading and IsBusy are the same value, so this pins an intention rather than a
        // difference: each spinner belongs to the operation its own button starts, and if a second
        // flag ever joins IsBusy these must NOT start spinning for it. The short string is contained
        // in the long one - that is text containment and cannot be helped - so the short entry
        // covers 91 and 139 and the long entry pins 159, which is the inline form.
        SpinnerExpressions =
        [
            "@if (isLoading)",
            "@if (isLoading) { <span class=\"spinner-border spinner-border-sm me-1\"></span> }",
        ],

        // Falsification 6 for this page, and the larger half of the fix. tabIndex is the one the
        // plan names: it chose between Add and Remove AFTER the awaits in both write handlers, so a
        // tab click landing mid-write revoked the permission the operator asked to grant and
        // mislabelled the audit row and both notification emails.
        //
        // Everything else here is CapturedAtEntry for the same reason the two prior pages needed it,
        // sharpened by the autocomplete gap described in the remarks: the browser's copy of a
        // disabled attribute is one round trip stale, and on the two pickers the attribute never
        // reaches the suggestion rows at all.
        //
        // autoMapping is here now. It was closed in the code from the start and absent from this
        // list only because ClickGateTests.ReadOf read the named-argument label autoMapping: at the
        // success-path audit call as a live field read, so registering the real obligation failed on
        // the label alone. ReadOf now excludes a label in argument position; see the remarks above.
        PostAwaitLiveReads =
        [
            new PostAwaitLiveRead("SubmitSingle", "tabIndex", "isAdd",
                SnapshotShape.CapturedAtEntry,
                "tabIndex is the Add-versus-Remove choice, read live at the write itself, at the "
                + "self-grant check that only applies to Add, at the audit action string, at the "
                + "AutoMapping argument and at the notification direction. A tab click during the "
                + "authorization or ticket round trip therefore executed the opposite operation from "
                + "the one the operator confirmed - a revocation where a grant was asked for - and "
                + "audited and emailed it under the other name"),

            new PostAwaitLiveRead("SubmitSingle", "targetMailbox", "target",
                SnapshotShape.CapturedAtEntry,
                "the mailbox whose permissions are changed. A suggestion row clicked in an already "
                + "open autocomplete dropdown rewrites this field while the handler is suspended, "
                + "and the disabled attribute does not reach those rows, so the capture is the only "
                + "thing stopping the write, the protected-principal check and the audit row from "
                + "landing on three different mailboxes"),

            new PostAwaitLiveRead("SubmitSingle", "affectedUser", "user",
                SnapshotShape.CapturedAtEntry,
                "the user being granted or removed, on the same footing as targetMailbox and "
                + "reachable the same way. It is also the address the user notification email is "
                + "sent to, so a late change would tell the wrong person their access changed"),

            new PostAwaitLiveRead("SubmitSingle", "ticketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the ticket validated against ServiceNow must be the ticket recorded in the audit "
                + "row and the admin email. Read live, a keystroke landing in the round trip before "
                + "the disabled attribute reaches the browser desyncs the validated ticket from the "
                + "recorded one"),

            new PostAwaitLiveRead("SubmitSingle", "grantFullAccess", "fullAccess",
                SnapshotShape.CapturedAtEntry,
                "whether Full Access is part of the change. This is falsification 2's worked example "
                + "on this page: the checkbox renders server state, so a click the server refuses "
                + "leaves the browser showing it unticked while the field stays true - and read "
                + "live, the write grants a permission the operator visibly deselected"),

            new PostAwaitLiveRead("SubmitSingle", "grantSendAs", "sendAs",
                SnapshotShape.CapturedAtEntry,
                "the Send As half of the same pair, with the same hazard. It also decides the "
                + "permission string in the audit row and both notification emails, so a late "
                + "toggle desyncs the record from the write"),

            new PostAwaitLiveRead("SubmitSingle", "autoMapping", "autoMap",
                SnapshotShape.CapturedAtEntry,
                "whether a Full Access grant also auto-maps the mailbox into the user's Outlook "
                + "profile. The checkbox renders server state, so it carries the same falsification 2 "
                + "hazard as grantFullAccess - read live, the write applies an auto-mapping the "
                + "operator visibly unticked. It is also the value the success audit records, and it "
                + "is recorded by NAME (autoMapping:) at that call, which is why this obligation "
                + "could not be registered until ReadOf learned to tell a named-argument label from "
                + "a field read"),

            new PostAwaitLiveRead("ProcessBulk", "csvFile", "file",
                SnapshotShape.CapturedAtEntry,
                "the uploaded file is opened, and its name is audited and emailed, after several "
                + "awaits. A second file chosen mid-run put the NEW file's name in the audit row and "
                + "the admin notification against the OLD file's results, so the record of a bulk "
                + "permission change named a file that was never processed"),

            new PostAwaitLiveRead("ProcessBulk", "bulkIsAdd", "isAdd",
                SnapshotShape.CapturedAtEntry,
                "the radio pair chooses whether every row in the CSV is a grant or a revocation, and "
                + "it was read live at the call that processes the file. A radio flipped during the "
                + "authorization round trip sent the whole CSV through the opposite operation"),

            new PostAwaitLiveRead("ProcessBulk", "bulkTicketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the ticket for every row in the run, validated and then recorded. Same obligation "
                + "as the single-mailbox ticket, over a batch"),

            new PostAwaitLiveRead("ProcessBulk", "bulkResult", "bulk",
                SnapshotShape.PublishedFromLocal,
                "the dismiss control at 212 is exempt and can null bulkResult while this handler is "
                + "suspended. Reading the field back for the audit row, the failure detail or the "
                + "admin email would then throw a NullReferenceException into ProcessBulk's own "
                + "catch, which reports a completed bulk run as a bare exception message and skips "
                + "the admin notification"),

            new PostAwaitLiveRead("ExecuteOnPrem", "tabIndex", "isAdd",
                SnapshotShape.CapturedAtEntry,
                "the same Add-versus-Remove choice on the destructive on-premises path, where it was "
                + "worse than on SubmitSingle: the action string was computed from tabIndex at entry "
                + "and the write, the audit verb and the notification direction all re-read it after "
                + "the awaits, so the handler could audit one operation while performing the other"),

            new PostAwaitLiveRead("ExecuteOnPrem", "onPremTarget", "target",
                SnapshotShape.CapturedAtEntry,
                "Cancel at 162 is exempt and nulls onPremTarget, and this handler's own finally nulls "
                + "it too. It is dereferenced with ! - a live read after an await would throw a "
                + "NullReferenceException, and the catch that would receive it audits the target, so "
                + "the throw would land inside the catch and escape the handler. With no "
                + "ErrorBoundary anywhere in this app that tears the circuit down mid-write"),
        ],

        RaiseMustFollowEarlyReturn =
        [
            new RaiseAfterEarlyReturn("ProcessBulk", "isLoading", "file is null",
                "the early return leaves no finally behind it, so a raise above the guard is never "
                + "lowered. isLoading is this page's only flag and the whole of IsBusy, so it would "
                + "not grey one button - it would deaden every control on the page, permanently, the "
                + "first time the operator hit Process CSV with no file chosen. The guard reads "
                + "'file is null' rather than 'csvFile is null' because the file is captured at entry "
                + "now; the capture sits above the guard and the raise below it."),
        ],
    };

    /// <summary>
    /// Calendar Permissions: tier 1, page 4 of 9, converted 2026-09-18 under
    /// docs/ClickGatingAudit-Plan.md Revision 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The structural twin of MailboxPermissions.razor, whose decisions are inherited here rather
    /// than re-litigated.</b> One flag, one page-wide predicate, the same tab strip over a
    /// single-mailbox form and a bulk form, the same on-prem write staged by SubmitSingle and
    /// executed by ExecuteOnPrem behind a Continue button, and the same two RecipientAutocomplete
    /// write-target pickers. Every place this page differs from its twin is called out below, because
    /// an inherited decision that quietly stopped applying is worse than one that was never made.
    /// </para>
    /// <para>
    /// <b>The lead finding is on ExecuteOnPrem, and it is sharper than the twin's.</b> The handler
    /// computed its audit action string and its level from tabIndex at entry, then re-read tabIndex
    /// at the write three statements later and again at the notification direction. A tab flip
    /// landing in that window made the audit row and BOTH emails say Set while the code performed a
    /// Remove - on the destructive on-premises path, and with no gate able to help, because
    /// ExecuteOnPrem re-enters after an operator confirmation and that is an unbounded pause. The
    /// captures registered under PostAwaitLiveReads are the whole of the fix.
    /// </para>
    /// <para>
    /// <b>DownloadSampleCsv is GATED here and not exempt, which is a real divergence from page 3.</b>
    /// MailboxPermissions exempts its sample-CSV button at 135 on the grounds that the handler serves
    /// a compile-time constant and reads no page field. The second half of that reason is false here:
    /// this page's DownloadSampleCsv reads bulkIsSet twice, once to choose the header row and once to
    /// choose the file name, so it does read live page state and the twin's grant does not transfer.
    /// It carries disabled="@IsBusy" with the rest of the page and needs no entry below.
    /// </para>
    /// <para>
    /// <b>The RecipientAutocomplete gap was inherited and is now CLOSED</b> by the shared-component
    /// slice - the rows are withheld while Disabled and SelectResult refuses on entry. The
    /// description below is kept as the reason the entry captures exist, not as a live gap.
    /// As it stood: Disabled= reached the
    /// component's inner input and its own Enter path; it does not reach its suggestion rows, which
    /// are ungated li elements carrying an @onmousedown that consults the parameter nowhere. A
    /// dropdown already open when the gate closed keeps clickable rows, and clicking one rewrites
    /// targetMailbox or affectedUser while a write is in flight. What actually stops that retargeting
    /// the write, the protected-principal check, the audit row and the two notification emails is the
    /// entry capture in PostAwaitLiveReads - after which the worst a late suggestion click can do is
    /// dirty the form for the NEXT submission. The contract change is scheduled for page 6; page 3's
    /// entry records the blast radius and it is not re-counted here.
    /// </para>
    /// <para>
    /// <b>Both write handlers gained a protecting try, by two different routes, and the difference is
    /// recorded because it looks like an inconsistency and is not.</b> ProcessBulk's existing try was
    /// extended upward over the re-authorization, exactly as on page 3. SubmitSingle could not be
    /// done that way: its pre-try region declares action inside a nested audit try and ipAddress
    /// inside the self-grant branch, and the existing try's own block declares both names directly,
    /// so merging the two scopes is a CS0136 conflict on both. It therefore gained an OUTER try that
    /// wraps the existing try/catch whole, closed by the finally that lowers the flag. That shape has
    /// a second merit worth recording: the existing catch keeps exactly the reach it had, so this
    /// conversion changed no exception semantics, only where the flag is lowered. Eight scattered
    /// pre-return isLoading = false lines were traced one by one before deletion; seven are
    /// immediately followed by return with nothing between, and the eighth sits after
    /// onPremConfirmPending = true, where the finally preserves the relative order so Continue still
    /// renders enabled.
    /// </para>
    /// <para>
    /// Note for whoever next reads Get-ClickGateAudit.ps1 output. Re-run against this page AFTER the
    /// conversion, it reports "Predicate: -", eight of eleven buttons as GateNoFlag and an 11-of-11
    /// gap on a fully converted page. That is the single-flag scanner limitation page 3 records - its
    /// predicate detector requires an expression-bodied bool naming at least TWO in-flight flags -
    /// and not work outstanding. It reports zero stuck flags, which is why ScannerFalsePositives is
    /// empty here as it is on the twin, and the three controls it lists as ungated (161, 178, 211)
    /// are exactly the three registered exemptions below.
    /// </para>
    /// </remarks>
    private static PageGateEntry CalendarPermissions => new()
    {
        Page = "CalendarPermissions.razor",
        ExpectedLineCount = 679,

        // One flag, one page-wide predicate, on the twin's reasoning re-checked against this file
        // rather than copied: the tab strip, the single-mailbox form, the bulk form, the on-prem
        // confirmation and both result banners render off the same state at the same time, and every
        // write handler replaces part of it - SubmitSingle can stage the on-prem panel, ProcessBulk
        // replaces bulkResult, ExecuteOnPrem nulls onPremTarget. There is no control here that is
        // safe to leave live while another is working, which is what made a single predicate wrong
        // on SelfServiceGroups and ConferenceRooms and right here.
        Predicates =
        [
            new PredicateScope("IsBusy", ["isLoading"], AppliesWhen: "the whole page"),
        ],

        // Empty on purpose and confirmed by running the scanner AFTER the conversion, not before:
        // Get-ClickGateAudit.ps1 reports zero stuck flags here. result and bulkResult are both
        // nullables nulled at handler entry and assigned later in the same awaiting method, which is
        // the shape that made operationResult a reported false positive on pages 1 and 2, and
        // neither is reported here. Nothing was suppressed to achieve that.
        ScannerFalsePositives = [],

        ExcludedFields =
        [
            new ExcludedField("onPremConfirmPending", "the Continue/Cancel pair at 157-161",
                "means 'an on-premises calendar write is staged and waiting for the operator', not "
                + "'the page is working'. The pair renders only while it is true, so folding it into "
                + "IsBusy would disable Continue at the only moment it is ever shown and no on-prem "
                + "calendar permission could be changed again - the regression a review of the "
                + "Migration plan caught before any code was written. ConfirmOnPrem reads it as a "
                + "staleness guard, which is a different job from the busy gate and belongs in the "
                + "handler"),
        ],

        ExemptControls =
        [
            new ExemptControl(161, "@onclick=\"CancelOnPrem\"",
                "backs out of a staged on-premises write, and the operator must always be able to do "
                + "that. Continue beside it at 157 is gated and ConfirmOnPrem refuses a stale click, "
                + "so nothing can be executed from this state while the page is busy; cancelling only "
                + "resets the staged selection",
                ConditionThatKeepsItTrue:
                "ExecuteOnPrem must keep capturing onPremTarget at entry. This control nulls that "
                + "field, and the handler's catch audits the target after several awaits, so a live "
                + "read there would dereference null inside a catch - where, with no ErrorBoundary "
                + "anywhere in this app, the escaping exception tears the circuit down. The "
                + "obligation is registered under PostAwaitLiveReads; it is recorded here rather than "
                + "as a PrerequisiteBeforeExemptionHolds because this snippet names a method rather "
                + "than assigning a field, so there is nothing for that field to tie to"),

            new ExemptControl(178, "@onclick=\"() => result = null\"",
                "dismisses the single-operation result banner. Gating it on IsBusy would trap the "
                + "previous operation's message on screen for the whole of the next one, the same "
                + "reason Migration's dismiss at 442, DhcpAuthorization's at 40, NamedLocations' at "
                + "45 and MailboxPermissions' at 179 are exempt",
                ConditionThatKeepsItTrue:
                "no handler may READ result back. Today none does: SubmitSingle and ExecuteOnPrem "
                + "both hold their outcome in a local opResult and only ever assign to result, so "
                + "this control cannot null the field out from under a dereference. That is why this "
                + "exemption carries no PrerequisiteBeforeExemptionHolds and no PostAwaitLiveReads "
                + "entry names result - there is no live read to close. The moment a handler reads "
                + "result after an await, this exemption becomes the DhcpAuthorization 40 defect: a "
                + "write that SUCCEEDED audited and emailed as failed"),

            new ExemptControl(211, "@onclick=\"() => bulkResult = null\"",
                "dismisses the bulk-operation summary, on the same footing as the banner above. The "
                + "summary can still be on screen during an unrelated single-mailbox write, because "
                + "SubmitSingle does not clear bulkResult",
                ConditionThatKeepsItTrue:
                "ProcessBulk must keep publishing its summary from a local rather than reading the "
                + "field back",
                PrerequisiteBeforeExemptionHolds:
                "ProcessBulk must keep the CSV outcome in a local - var bulk = await ...; "
                + "bulkResult = bulk; - and read that local for the audit row, the failure detail "
                + "and the admin email. Before it did, this control could null bulkResult while the "
                + "handler was suspended, and the following bulkResult.FailedCount read would throw "
                + "a NullReferenceException into ProcessBulk's own catch, which reports a bulk run "
                + "that SUCCEEDED as a bare exception message and skips the admin notification "
                + "entirely. Reinstate the field reads and this exemption is a live defect again"),
        ],

        // Verified by exhaustive census rather than inherited from the twin: Get-ClickGateAudit.ps1
        // finds no @onclick off a button on this page (OtherClick 0), and the hand census of what it
        // cannot see finds no anchor, no div/span/td handler, no @onsubmit and no @onkeydown. What it
        // does find is eight DOM-synced controls, which are below - absent from this list on purpose,
        // on the DhcpAuthorization, NamedLocations and MailboxPermissions precedent: this list is
        // keyed to what ClickGateSource can locate, and an input, a select, an InputFile and a child
        // component are in neither Tags("a") nor NonButtonClickTargets(), so registering one here
        // would fail EveryNonButtonTargetIsRefusedByItsDeclaredMechanism rather than document
        // anything.
        NonButtonTargets = [],

        DomSyncedControls =
        [
            // The two write-target pickers. Disabled= reaches their inner input and their own Enter
            // key path; it does NOT reach their suggestion rows. See the remarks on this entry.
            new DomSyncedControl(46, "@bind-Value=\"targetMailbox\"", "RecipientAutocomplete",
                "Disabled=\"@IsBusy\""),
            new DomSyncedControl(54, "@bind-Value=\"affectedUser\"", "RecipientAutocomplete",
                "Disabled=\"@IsBusy\""),

            new DomSyncedControl(62, "id=\"ticketNumber\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(72, "id=\"accessLevel\"", "select", "disabled=\"@IsBusy\""),
            new DomSyncedControl(118, "id=\"radioSet\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(120, "id=\"radioRemove\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(126, "id=\"bulkTicket\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(133, "OnChange=\"HandleCsvUpload\"", "InputFile",
                "disabled=\"@IsBusy\""),
        ],

        UngatedDomSyncedControls = [],

        ForbiddenGuardSites =
        [
            new ForbiddenGuardSite("ExecuteOnPrem", ["ConfirmOnPrem"],
                "The exactly-one-guard constraint Revision 1 records for this page and its twin. "
                + "ConfirmOnPrem owns the refusal and is ExecuteOnPrem's only caller. A guard here "
                + "is redundant today only because ConfirmOnPrem raises no flag before calling: the "
                + "moment it does - the obvious next improvement - this guard turns the entire "
                + "on-prem calendar write into a silent no-op, and nothing says so. No banner, no "
                + "audit row, no admin email, and the operator concludes Exchange refused the change "
                + "and retries. Guarding neither instead leaves Continue open to a second dispatch "
                + "in the round trip before its disabled attribute lands, which executes the "
                + "destructive on-prem write twice."),

            new ForbiddenGuardSite("ReauthorizeAsync", ["SubmitSingle", "ProcessBulk"],
                "The shared pre-write authorization re-check, called by both write handlers AFTER "
                + "they raise isLoading, so IsBusy is true at every call. A guard here makes the "
                + "re-check return false on every single submission and every bulk run: the page "
                + "would report 'Authorization denied.' for work the operator is entitled to do, "
                + "and no calendar permission could be changed at all."),
        ],

        // The positive half of the exactly-one-guard constraint, inherited from the twin along with
        // the reason it exists: the ForbiddenGuardSite above can only say where a guard may NOT be,
        // so on its own, deleting ConfirmOnPrem's guard outright passes every other assertion here.
        // Both halves together mean a size-preserving MOVE of the guard from one method to the other
        // flips them at once.
        ExactlyOneGuard =
        [
            new ExactlyOneGuardOf("ConfirmOnPrem", ["ExecuteOnPrem"],
                "if (IsBusy || !onPremConfirmPending)",
                "ConfirmOnPrem is the method the Continue click reaches and the method whose button "
                + "carries disabled=\"@IsBusy\", so belt and braces sit on one control rather than "
                + "being split across two methods. ExecuteOnPrem raises isLoading as its own first "
                + "statement, so a guard above that raise would be a re-entrancy guard on itself, "
                + "which the 2026-09-17 ruling says is not enough; and because ConfirmOnPrem is its "
                + "only caller, a guard there becomes a silent no-op the moment ConfirmOnPrem raises "
                + "the flag before calling, with no banner, no audit row and no admin email to say "
                + "the on-prem write did not happen. The two clauses refuse two different things and "
                + "both are load-bearing: IsBusy is the click gate, because the browser's copy of "
                + "the Continue button's disabled attribute is one round trip stale and a second "
                + "Continue would run the destructive write twice; !onPremConfirmPending is the "
                + "staleness gate, because SetTab and CancelOnPrem both clear that field, so a "
                + "Continue that left the browser before the confirmation panel unrendered would "
                + "otherwise execute a staged Set against a tab the operator has since flipped to "
                + "Remove"),
        ],

        AnnotatedControls =
        [
            new AnnotatedControl(88, "@onclick=\"SubmitSingle\"",
                ["!IsFormValid"],
                RendersOnlyWhen:
                "only while tabIndex < 2 - it is the Set/Remove tab's submit, and the bulk tab "
                + "renders its own at 137 instead. IsFormValid (target, user and ticket all present) "
                + "is a form-validity precondition that IsBusy is OR-ed in front of, never "
                + "substituted for. SubmitSingle re-checks none of it, so this clause is the only "
                + "enforcement - the IntuneDevices 403 shape"),

            new AnnotatedControl(137, "@onclick=\"ProcessBulk\"",
                ["csvFile is null", "string.IsNullOrWhiteSpace(bulkTicketNumber)"],
                RendersOnlyWhen:
                "only while tabIndex == 2. ProcessBulk re-checks the file itself - that null check "
                + "is what the registered RaiseAfterEarlyReturn below is about - but it does NOT "
                + "re-check the ticket before validating it, so the emptiness clause is a real "
                + "precondition and not decoration"),
        ],

        // Three per-button spinners, all reading the flag rather than the predicate, registered so a
        // mechanical "simplify to the predicate" pass fails instead of quietly changing behaviour.
        // Today isLoading and IsBusy are the same value, so this pins an intention rather than a
        // difference: each spinner belongs to the operation its own button starts, and if a second
        // flag ever joins IsBusy these must NOT start spinning for it. The short string is contained
        // in the long one - that is text containment and cannot be helped - so the short entry
        // covers 89 and 138 and the long entry pins 158, which is the inline form.
        SpinnerExpressions =
        [
            "@if (isLoading)",
            "@if (isLoading) { <span class=\"spinner-border spinner-border-sm me-1\"></span> }",
        ],

        // Falsification 6 for this page, and the larger half of the fix. tabIndex is the one the plan
        // names and ExecuteOnPrem is where it bites hardest; see the remarks above.
        //
        // Everything else is CapturedAtEntry for the reason the three prior pages needed it,
        // sharpened by the autocomplete gap: the browser's copy of a disabled attribute is one round
        // trip stale, and on the two pickers the attribute never reaches the suggestion rows at all.
        PostAwaitLiveReads =
        [
            new PostAwaitLiveRead("SubmitSingle", "tabIndex", "isSet",
                SnapshotShape.CapturedAtEntry,
                "tabIndex is the Set-versus-Remove choice, read live at the write itself, at the "
                + "self-grant check that only applies to Set, at the audit action string, at whether "
                + "the audit row records an access level AT ALL, and at the notification direction. "
                + "A tab click during the authorization or ticket round trip therefore executed the "
                + "opposite operation from the one the operator confirmed - a revocation where a "
                + "grant was asked for - and audited and emailed it under the other name"),

            new PostAwaitLiveRead("SubmitSingle", "targetMailbox", "target",
                SnapshotShape.CapturedAtEntry,
                "the mailbox whose calendar permissions are changed. A suggestion row clicked in an "
                + "already open autocomplete dropdown rewrites this field while the handler is "
                + "suspended, and the disabled attribute does not reach those rows, so the capture is "
                + "the only thing stopping the write, the protected-principal check and the audit row "
                + "from landing on three different mailboxes"),

            new PostAwaitLiveRead("SubmitSingle", "affectedUser", "user",
                SnapshotShape.CapturedAtEntry,
                "the user being granted or removed, on the same footing as targetMailbox and "
                + "reachable the same way. It is also the address the user notification email is "
                + "sent to, so a late change would tell the wrong person their calendar access "
                + "changed"),

            new PostAwaitLiveRead("SubmitSingle", "ticketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the ticket validated against ServiceNow must be the ticket recorded in the audit "
                + "row and the admin email. Read live, a keystroke landing in the round trip before "
                + "the disabled attribute reaches the browser desyncs the validated ticket from the "
                + "recorded one"),

            new PostAwaitLiveRead("SubmitSingle", "accessRight", "access",
                SnapshotShape.CapturedAtEntry,
                "which calendar permission level is granted - Reviewer, Editor, Owner and the rest. "
                + "The select renders server state, so it carries the falsification 2 hazard: read "
                + "live, the write grants a level the operator has since changed away from, and the "
                + "same live read decides the level in the audit row, the admin email and the staged "
                + "on-prem value. It is the difference between read-only free/busy and full control "
                + "over someone else's calendar"),

            new PostAwaitLiveRead("ProcessBulk", "csvFile", "file",
                SnapshotShape.CapturedAtEntry,
                "the uploaded file is opened, and its name is audited and emailed, after several "
                + "awaits. A second file chosen mid-run put the NEW file's name in the audit row and "
                + "the admin notification against the OLD file's results, so the record of a bulk "
                + "calendar change named a file that was never processed"),

            new PostAwaitLiveRead("ProcessBulk", "bulkIsSet", "isSet",
                SnapshotShape.CapturedAtEntry,
                "the radio pair chooses whether every row in the CSV is a grant or a revocation, and "
                + "it was read live both at the call that processes the file and at the audit action "
                + "string. A radio flipped during the authorization round trip sent the whole CSV "
                + "through the opposite operation"),

            new PostAwaitLiveRead("ProcessBulk", "bulkTicketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the ticket for every row in the run, validated and then recorded. Same obligation "
                + "as the single-mailbox ticket, over a batch"),

            new PostAwaitLiveRead("ProcessBulk", "bulkResult", "bulk",
                SnapshotShape.PublishedFromLocal,
                "the dismiss control at 211 is exempt and can null bulkResult while this handler is "
                + "suspended. Reading the field back for the audit row, the failure detail or the "
                + "admin email would then throw a NullReferenceException into ProcessBulk's own "
                + "catch, which reports a completed bulk run as a bare exception message and skips "
                + "the admin notification"),

            new PostAwaitLiveRead("ExecuteOnPrem", "tabIndex", "isSet",
                SnapshotShape.CapturedAtEntry,
                "this page's lead finding. The action string and the level were computed from "
                + "tabIndex at entry, and then tabIndex was read AGAIN at the write three statements "
                + "later and again at the notification direction, so a tab flip mid-write made the "
                + "audit row and both emails say Set while the code performed a Remove - on the "
                + "destructive on-premises path, and after a confirmation pause of unbounded length "
                + "that no gate can shorten"),

            new PostAwaitLiveRead("ExecuteOnPrem", "onPremTarget", "target",
                SnapshotShape.CapturedAtEntry,
                "Cancel at 161 is exempt and nulls onPremTarget, and this handler's own finally nulls "
                + "it too. It is dereferenced with ! - a live read after an await would throw a "
                + "NullReferenceException, and the catch that would receive it audits the target, so "
                + "the throw would land inside the catch and escape the handler. With no "
                + "ErrorBoundary anywhere in this app that tears the circuit down mid-write"),

            new PostAwaitLiveRead("ExecuteOnPrem", "onPremAccessRight", "access",
                SnapshotShape.CapturedAtEntry,
                "the staged permission level, read live at the on-prem Set call itself. SubmitSingle "
                + "restages this field on every run, so a second single-mailbox submission accepted "
                + "while the operator sat on the confirmation panel would rewrite the level the "
                + "on-prem write applies without touching the level already computed into the audit "
                + "row"),
        ],

        RaiseMustFollowEarlyReturn =
        [
            new RaiseAfterEarlyReturn("ProcessBulk", "isLoading", "file is null",
                "the early return leaves no finally behind it, so a raise above the guard is never "
                + "lowered. isLoading is this page's only flag and the whole of IsBusy, so it would "
                + "not grey one button - it would deaden every control on the page, permanently, the "
                + "first time the operator hit Process CSV with no file chosen. The guard reads "
                + "'file is null' rather than 'csvFile is null' because the file is captured at entry "
                + "now; the capture sits above the guard and the raise below it."),
        ],
    };

    /// <summary>
    /// Intune Devices: tier 1, page 5 of 9, converted 2026-09-18 under
    /// docs/ClickGatingAudit-Plan.md Revision 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Line 402's disabled expression carries the only enforcement of the wipe confirmation in
    /// the whole codebase, and this conversion widened it rather than rewriting it.</b> Re-checked
    /// against the file before anything was touched, because the repo settles this kind of claim by
    /// reading: WipeNameConfirmed is declared at 685-687 (pre-conversion numbering) and named at
    /// exactly one site, the disabled attribute of the confirm button; wipeConfirmName is read at
    /// exactly one site, inside WipeNameConfirmed; and ExecuteActionAsync re-checks the granular
    /// authorization, the ticket's presence, the ticket's validity and the protected principal, and
    /// never reads wipeConfirmName at all. So a mechanical "replace the disabled expression with the
    /// predicate" pass would delete the second key on the most destructive action in the app with no
    /// server-side backstop to catch it. The edit here replaced ONLY the busy clause -
    /// actingDeviceId != null became ActionsDisabled - and left both preconditions in place, OR-ed.
    /// They are registered in <see cref="PageGateEntry.AnnotatedControls"/> below, which is the
    /// assertion that fails if a later pass takes them out.
    /// </para>
    /// <para>
    /// <b>A server-side re-check of the typed name is NOT in this slice and is recorded as an open
    /// finding.</b> Adding one is a behaviour change, and the shape it would take is written down
    /// here so the decision does not have to be re-derived: ExecuteActionAsync already captures every
    /// other operator input at entry, so it would capture wipeConfirmName the same way and Refuse
    /// with "The typed device name does not match." between the ticket gate and the
    /// protected-principal check - a path that audits as it refuses, like every other refusal in that
    /// method. Until then the markup clause is load-bearing and the PreserveClauses entry is the only
    /// thing defending it.
    /// </para>
    /// <para>
    /// <b>The predicate was genuinely partial, and the missing flag was detailLoading.</b> Revision 1
    /// says so and the file agrees: ActionsDisabled read isSearching || actingDeviceId != null, and
    /// ToggleDetailAsync raises a third flag the predicate never named. The operator-visible
    /// consequence was not merely a live button. The Search button was outside the gate too, and
    /// SearchAsync clears devices, deviceOutcomes and the open confirm bar - so a search started
    /// while a wipe was queued discarded that wipe's verdict outright: the device's row is no longer
    /// in the list, so neither the "Acting on ..." spinner nor the outcome alert can render, while
    /// the audit row and the administrator email still record that it happened. The search box's
    /// Enter path reached the same SearchAsync with the same gap. Both are closed here.
    /// </para>
    /// <para>
    /// <b>Nine of the ten DOM-synced controls were gated on a clause that could never be true while
    /// they rendered, which is worth knowing before anyone reads the diff as cosmetic.</b> The whole
    /// confirm bar carried disabled="@(actingDeviceId != null)", and ExecuteActionAsync sets
    /// confirmDeviceId = null in the same synchronous run as actingDeviceId = deviceId, above its
    /// first await - so the bar unrenders on the very render that would first have shown those
    /// controls greyed. The old attribute was dead. Widened to the predicate it is live for the one
    /// case that can actually happen: a detail read in flight with a confirm bar open. The Cancel
    /// button at 407 keeps the same narrow clause on purpose and is registered exempt; it is dead in
    /// the same way, and that is recorded rather than quietly fixed, because the operator must be
    /// able to back out of a staged wipe and the honest gate for that control is no gate.
    /// </para>
    /// <para>
    /// <b>Twelve excluded staged fields, not the seven Revision 1 predicted.</b> The plan gives a
    /// count and no list, so the list is a hand census of this file and the count is corrected rather
    /// than reproduced. Four of the five wipe-option fields are what the prediction is most likely to
    /// have folded together; alsoRemoveEntra, notifyPrimaryUser and showActionHelp are each
    /// independently fatal if folded in. Every one of the twelve sits on a control that renders only
    /// while the operator is deciding, so putting any of them in the predicate disables the control
    /// at the only moment it exists - the regression a review of the Migration plan caught before any
    /// code was written, and the reason this field is asserted as a negative per field.
    /// </para>
    /// <para>
    /// <b>ExecuteActionAsync needed no snapshot, and that is unusual enough to say plainly.</b> It
    /// already binds deviceId, deviceName, upn, ticket, auditAction, target, wipeOptions,
    /// entraDeviceId, removeEntraObject, notifyRequested and notifyDefault into locals above its
    /// first await, and reads nothing but locals and its own parameters afterwards. Falsification 6
    /// bit on SearchAsync instead: it read searchTerm twice below await Task.Yield(), once for the
    /// audit target and once for the Graph call, on a box that binds on oninput - so a keystroke
    /// landing in the yield could audit one search and run another. One capture closes it.
    /// </para>
    /// <para>
    /// <b>The residual double-dispatch on Confirm is a finding, not a fix.</b> The browser's copy of
    /// the confirm button's disabled attribute is one round trip stale, so a second Confirm can still
    /// be dispatched while the first ExecuteActionAsync is suspended at its Task.Yield. Pages 3 and 4
    /// closed the same hazard with a guard in the CALLING method, and there is no such method here:
    /// ExecuteActionAsync is itself the click target and raises actingDeviceId as its first
    /// statement, which the 2026-09-17 ruling says is not enough on its own. The guard that would
    /// work - if (ActionsDisabled) return; above the raise - also swallows a legitimate click landing
    /// in the stale window during a detail read, silently, with no banner and no audit row, which is
    /// the exact harm every ForbiddenGuardSite below exists to prevent. Choosing between those two is
    /// an owner decision, so no handler guard was added and ExactlyOneGuard is empty, as it is on
    /// pages 1 and 2 for the same structural reason.
    /// </para>
    /// <para>
    /// Note for whoever next reads Get-ClickGateAudit.ps1 output. Re-run against this page AFTER the
    /// conversion it reports 9 buttons, Ungated 1 (line 151), GatePart 1 (line 407), Flags 3,
    /// Predicate ActionsDisabled, OtherClick 0 and StuckFlags 0. The two it names are exactly the two
    /// registered exemptions below, and ScannerFalsePositives is empty because nothing was reported
    /// to suppress. Unlike pages 3 and 4 the predicate detector DOES see this page, because
    /// ActionsDisabled is an expression-bodied bool naming three flags rather than one.
    /// </para>
    /// </remarks>
    private static PageGateEntry IntuneDevices => new()
    {
        Page = "IntuneDevices.razor",
        ExpectedLineCount = 1628,

        // Kept as ActionsDisabled rather than renamed to IsBusy, which is what the four pages before
        // it call their predicate. The name was already on the page and is quoted by
        // docs/ClickGatingAudit-Plan.md and .agents/state.md, neither of which this slice may edit, so
        // renaming would leave two governance files describing a member that no longer exists. The
        // assertions key on this string, not on a convention, so nothing here is ambiguous.
        //
        // One page-wide predicate is right here, and that was tested rather than assumed: Revision 1
        // found a single predicate provably wrong on two OTHER pages. This page has one view. The
        // results table, the confirm bar, the per-device outcomes and the detail panel all render off
        // state that SearchAsync replaces wholesale, ToggleDetailAsync replaces in part, and
        // ExecuteActionAsync writes into - so there is no control here that is safe to leave live
        // while another is working.
        Predicates =
        [
            new PredicateScope("ActionsDisabled",
                ["isSearching", "detailLoading", "actingDeviceId"],
                AppliesWhen: "the whole page"),
        ],

        // Empty on purpose and confirmed by running the scanner AFTER the conversion, not before:
        // Get-ClickGateAudit.ps1 reports zero stuck flags here. expandedDeviceId, confirmDeviceId,
        // detailDevice, detailError and errorMessage are all nullables raised and nulled inside
        // awaiting methods, which is most of the in-flight shape, and none is reported - they escape
        // because none is nulled in a finally. Moving any of those clears into a finally would turn
        // it into a false positive AND, for confirmDeviceId, into the page-killing mistake
        // ExcludedFields exists to refuse. Nothing was suppressed to achieve an empty list.
        ScannerFalsePositives = [],

        // Twelve, by hand census of this file. See the remarks on this entry for why the count
        // differs from Revision 1's seven.
        ExcludedFields =
        [
            new ExcludedField("confirmDeviceId", "the confirm bar at 245-413",
                "names the device whose action is staged. The whole bar renders only while it equals "
                + "the row's id, so folding it into the predicate would disable Confirm at the only "
                + "moment it is ever shown and no device could be deleted, retired or wiped again"),

            new ExcludedField("confirmAction", "the confirm bar at 245-413",
                "which of the four actions is staged; the bar's second render condition, on the same "
                + "footing as confirmDeviceId. It also chooses the prompt, the help summary and "
                + "whether the wipe options and the typed-name box appear at all"),

            new ExcludedField("actionTicket", "the ticket input at 388 and Confirm at 402",
                "the staged ticket. A form value the operator is required to fill in, not an "
                + "operation: fold it in and typing the ticket that unlocks Confirm would disable "
                + "Confirm. The button reads it as an emptiness precondition, which is a different "
                + "job and is registered under AnnotatedControls"),

            new ExcludedField("wipeConfirmName", "the typed-name input at 397 and Confirm at 402",
                "the typed device name confirming a factory wipe, and the sharpest case on the page: "
                + "folding it in means typing the device name disables the button that typing it is "
                + "supposed to enable, so no wipe could ever be confirmed. WipeNameConfirmed reads it "
                + "as a precondition; see this entry's remarks"),

            new ExcludedField("wipeKeepUserData", "the wipe option block at 263-307",
                "an operator choice about what a wipe preserves, made while deciding. Ticking it must "
                + "not disable the button that carries the decision out"),

            new ExcludedField("wipeKeepEnrollmentData", "the wipe option block at 263-307",
                "as wipeKeepUserData: a staged flag on the request, not an operation in flight"),

            new ExcludedField("wipePersistEsimDataPlan", "the wipe option block at 263-307",
                "as wipeKeepUserData; the iOS eSIM half of the same staged flag set"),

            new ExcludedField("wipeMacOsUnlockCode", "the wipe option block at 263-307",
                "the macOS recovery PIN the operator types before confirming. A form value, and one "
                + "the operator is mid-way through typing exactly when the predicate would be "
                + "consulted"),

            new ExcludedField("wipeObliterationBehavior", "the wipe option block at 263-307",
                "the macOS obliteration choice; a bound select whose value is staged, not a signal "
                + "that anything is running"),

            new ExcludedField("alsoRemoveEntra", "the Entra add-on checkbox at 362",
                "the per-action 'also remove the Entra ID device object' opt-in. It is only ever "
                + "ticked while the confirm bar is open, so a predicate naming it would disable the "
                + "confirm controls the moment the operator opts in - and the opt-in is the whole "
                + "point of the control"),

            new ExcludedField("notifyPrimaryUser", "the notification checkbox at 329",
                "the per-action 'email the device's primary user' choice, on the same footing as "
                + "alsoRemoveEntra. It is the lost-or-stolen decision the operator makes at the "
                + "moment of acting, so it is set precisely when the predicate must be false"),

            new ExcludedField("showActionHelp", "the help panel toggle at 151",
                "whether the 'What do these actions do?' panel is open. Not staged confirmation, but "
                + "in the same class and more dangerous than it looks: it is a page-level bool with "
                + "no operation behind it, so a predicate naming it would deaden every control on the "
                + "page for as long as the help panel stayed open, and the help panel is what an "
                + "operator opens BEFORE acting"),
        ],

        ExemptControls =
        [
            new ExemptControl(151, "@onclick=\"() => showActionHelp = !showActionHelp\"",
                "opens and closes the plain-English help panel. It is a pure render toggle: the "
                + "handler flips one bool, makes no call and awaits nothing, and the panel explains "
                + "what delete, retire and wipe actually do. Gating it would lock the panel in "
                + "whichever state it happened to be in for the whole of an operation - including "
                + "closed, while a wipe the operator wanted to read about is queuing. Same reason "
                + "Migration's banner dismiss at 442 and DhcpAuthorization's at 40 are exempt",
                ConditionThatKeepsItTrue:
                "the handler stays an inline field flip. The moment anything else is wired to this "
                + "button it is an operation and belongs behind ActionsDisabled with the rest"),

            new ExemptControl(407, "@onclick=\"CancelAction\"",
                "backs out of a staged action, and the operator must always be able to do that - on "
                + "this page more than most, because what is staged may be a factory wipe. Confirm "
                + "beside it at 402 is gated, so nothing can be executed from this state while the "
                + "page is busy; cancelling only clears confirmDeviceId, confirmAction, the ticket "
                + "and the wipe options, and CancelAction makes no call and awaits nothing",
                KeepsItsOwnGuard: "disabled=\"@(actingDeviceId != null)\"",
                ConditionThatKeepsItTrue:
                "nothing reads the staged fields after an await. ExecuteActionAsync captures all of "
                + "them at entry and clears confirmDeviceId before its first await, which is also why "
                + "this narrow guard is dead today: the bar cannot render while actingDeviceId is "
                + "set. It is kept rather than widened because widening it to the predicate would "
                + "refuse a cancel during a detail read, and kept rather than deleted because the "
                + "day a handler holds the bar open across an await it is the only thing standing "
                + "between a cancel and the fields that handler is using"),
        ],

        // Verified by exhaustive census rather than inherited: Get-ClickGateAudit.ps1 reports
        // OtherClick 0, and the hand census of what it cannot see finds no anchor, no div/span/td
        // handler, no @onchange, no @onsubmit and no InputFile. What it does find is one @onkeydown
        // and ten DOM-synced controls, both below - absent from this list on purpose, on the
        // precedent of the four pages before it: this list is keyed to what ClickGateSource can
        // locate, and an input and a select are in neither Tags("a") nor NonButtonClickTargets().
        NonButtonTargets = [],

        // All ten now name the page predicate. Nine of them - every control in the confirm bar -
        // previously read disabled="@(actingDeviceId != null)", a clause that cannot be true while
        // they render; see this entry's remarks. The search box is the tenth and was on isSearching
        // alone, which left it typeable and its Enter path live for the whole of a detail read or a
        // queued wipe.
        DomSyncedControls =
        [
            new DomSyncedControl(56, "id=\"searchTerm\"", "input", "disabled=\"@ActionsDisabled\""),
            new DomSyncedControl(266, "id=\"wipeKeepUserData\"", "input",
                "disabled=\"@ActionsDisabled\""),
            new DomSyncedControl(275, "id=\"wipeKeepEnrollmentData\"", "input",
                "disabled=\"@ActionsDisabled\""),
            new DomSyncedControl(281, "id=\"wipePersistEsimDataPlan\"", "input",
                "disabled=\"@ActionsDisabled\""),
            new DomSyncedControl(289, "id=\"wipeMacOsUnlockCode\"", "input",
                "disabled=\"@ActionsDisabled\""),
            new DomSyncedControl(297, "id=\"wipeObliterationBehavior\"", "select",
                "disabled=\"@ActionsDisabled\""),
            new DomSyncedControl(329, "id=\"notifyPrimaryUser\"", "input",
                "disabled=\"@ActionsDisabled\""),
            new DomSyncedControl(362, "id=\"alsoRemoveEntra\"", "input",
                "disabled=\"@ActionsDisabled\""),
            new DomSyncedControl(388, "id=\"intuneDeviceTicket\"", "input",
                "disabled=\"@ActionsDisabled\""),
            new DomSyncedControl(397, "id=\"wipeConfirmName\"", "input",
                "disabled=\"@ActionsDisabled\""),
        ],

        UngatedDomSyncedControls = [],

        // Five callees, all reached from ExecuteActionAsync AFTER it sets actingDeviceId, so
        // ActionsDisabled is true at every one of these calls. This page has more of them than any
        // converted page so far, because its write path is a chain rather than a single service call,
        // and every link in it would fail silently.
        ForbiddenGuardSites =
        [
            new ForbiddenGuardSite("PerformActionAsync", ["ExecuteActionAsync"],
                "The single Graph write for the action. A busy guard here turns every delete, retire, "
                + "wipe and standalone Entra removal into a no-op that still audits, still emails "
                + "administrators and still tells the operator the action was carried out. That is "
                + "worse than a dead button: the record says a lost laptop was wiped and it was not."),

            new ForbiddenGuardSite("RemoveEntraObjectAsync", ["ExecuteActionAsync"],
                "The second write, run only after the Intune half succeeded. A guard here skips the "
                + "directory removal while the Intune record is already gone - the half-finished case "
                + "S5 exists to make visible - and returns nothing for entraResult, so the page shows "
                + "no second verdict, writes no second audit event and sends no second notification. "
                + "The operator has no way to tell it did not run."),

            new ForbiddenGuardSite("NotifyPrimaryUserAsync", ["ExecuteActionAsync"],
                "Decides, attempts and REPORTS the affected-user notification. A guard here makes a "
                + "requested email silently not happen and returns no note, so both the screen and "
                + "the audit record fall silent on the one question AC19 exists to answer - was the "
                + "user told? A silent no is indistinguishable from a send."),

            new ForbiddenGuardSite("SafeAudit", ["SearchAsync", "ToggleDetailAsync", "ExecuteActionAsync"],
                "The wrapper every audit write on this page goes through, including the two lookup "
                + "audits and every refusal path inside ExecuteActionAsync's Refuse. A guard here "
                + "drops the audit record for exactly the actions that matter, and drops it without "
                + "the log line the catch would have written, because the guard returns before the "
                + "try. The Constitution requires the audit, not a best effort at one."),

            new ForbiddenGuardSite("SetOutcome", ["ExecuteActionAsync"],
                "The only writer of deviceOutcomes, and the only thing that puts a verdict on screen. "
                + "A guard here means a wipe runs, audits and emails while the row it belongs to "
                + "shows nothing at all - the operator concludes the click was lost and clicks "
                + "again."),
        ],

        // Empty for the same structural reason as pages 1 and 2: no handler on this page is called
        // by another handler that could carry the refusal instead. ExecuteActionAsync is its own
        // click target and raises actingDeviceId as its first statement, so a guard in it would be
        // the re-entrancy shape the 2026-09-17 ruling rejects. The residual stale-window double
        // dispatch that leaves open is recorded in this entry's remarks as an open finding.
        ExactlyOneGuard = [],

        AnnotatedControls =
        [
            new AnnotatedControl(402, "@onclick=\"() => ExecuteActionAsync(device, confirmAction.Value)\"",
                [
                    "string.IsNullOrWhiteSpace(actionTicket)",
                    "!WipeNameConfirmed(device, confirmAction.Value)",
                ],
                RendersOnlyWhen:
                "only inside the confirm bar, so only while confirmDeviceId == device.Id and "
                + "confirmAction is set and the operator holds the grant for that action. The two "
                + "clauses are NOT equivalent to each other. ExecuteActionAsync re-checks the ticket "
                + "itself - twice, for presence and for validity - so the emptiness clause is belt "
                + "and braces. It re-checks the typed device name NOWHERE, so "
                + "!WipeNameConfirmed(device, confirmAction.Value) is the only enforcement of the "
                + "wipe confirmation in the entire codebase. Both are OR-ed with ActionsDisabled and "
                + "neither may be replaced by it; see this entry's remarks for the verification and "
                + "for the server-side re-check that is NOT implemented"),

            new AnnotatedControl(523, "@onclick=\"() => BeginAction(device, IntuneDeviceAction.EntraDelete)\"",
                [],
                RendersOnlyWhen:
                "three conditions deep and none of them visible to the scanner: inside the expanded "
                + "detail row (expandedDeviceId == device.Id), inside its detailDevice != null branch, "
                + "and then only while canEntraDelete and EntraIdUsable(detailDevice). Registered "
                + "with no clause to preserve because the reachability is the point: it is the one "
                + "action button that is NOT in the per-row button group, it runs the widest-scoped "
                + "write on the page, and a reviewer reading the button group alone would not know it "
                + "exists"),
        ],

        // Five spinner and progress conditions, every one of them reading a single flag or a
        // per-device identity rather than the predicate, registered verbatim so a mechanical
        // "simplify to the predicate" pass fails instead of quietly changing what the operator sees.
        // 75 is the Search button's inline spinner and 88 the results-area banner that says a search
        // is in flight; collapsing either to ActionsDisabled puts a search spinner on screen during a
        // detail read or a wipe. 99 is the gate on the whole results table - fold it in and the table
        // vanishes during an unrelated action. 418 keys the "Acting on ..." row to the device being
        // acted on, so the predicate would spin every row at once. 483 belongs to the detail panel.
        SpinnerExpressions =
        [
            "@if (isSearching)",
            "@if (hasSearched && !isSearching)",
            "@if (actingDeviceId == device.Id)",
            "@if (detailLoading)",
        ],

        // Falsification 6 for this page, and the short half of the fix, because ExecuteActionAsync
        // was already correct - see this entry's remarks. SearchAsync was not: await Task.Yield()
        // hands the circuit back to the renderer, the search box binds on oninput, and searchTerm was
        // read live BELOW that yield at both the audit target and the Graph call.
        PostAwaitLiveReads =
        [
            new PostAwaitLiveRead("SearchAsync", "searchTerm", "term",
                SnapshotShape.CapturedAtEntry,
                "the term the operator saw when the click was accepted is the one that must be "
                + "searched for and the one that must appear in the audit row. Read live below the "
                + "yield it was read TWICE, at DescribeSearch for the audit target and again at "
                + "SearchDevicesAsync, so a keystroke landing in the gap made the "
                + "IntuneDevices_Search record name a term Graph was never asked for - and this is "
                + "the page's only record that a lookup of someone's devices happened at all"),
        ],

        // Empty, and one near miss worth naming so the next reader does not go looking. The early
        // return in ToggleDetailAsync - the collapse branch at the top - already sits ABOVE the
        // detailLoading raise, which is the correct ordering and not registrable here: the assertion
        // requires the guard to return immediately, and this one nulls expandedDeviceId first.
        RaiseMustFollowEarlyReturn = [],

        // The page's one keyboard handler, and the reason it had to move with the Search button
        // rather than after it. Before this conversion both read isSearching, so they agreed; gating
        // the button on the widened predicate without gating the input would have MANUFACTURED the
        // 2eb8c15 defect - a greyed Search button beside an Enter key that still starts a search
        // during a queued wipe.
        //
        // The disabled attribute rather than a handler guard, on the Migration precedent: a disabled
        // input fires no key events, so one attribute closes the key path and the typing path
        // together. OnSearchKeyDown's own !ActionsDisabled condition was widened from !isSearching in
        // the same edit and is belt and braces only - it covers the one round trip in which the
        // browser's copy of the attribute is stale, and it is not what this entry rests on.
        KeyboardPaths =
        [
            new KeyboardPath(56, "id=\"searchTerm\"", "input",
                "keydown", "OnSearchKeyDown", "SearchAsync",
                KeyboardRefusal.DisabledAttribute, "disabled=\"@ActionsDisabled\"",
                "Enter runs the device search that the Search button at 74 refuses while the page is "
                + "busy. SearchAsync is not a read-only refresh: it clears devices, deviceOutcomes, "
                + "the open confirm bar, the expanded detail row and the staged wipe options. Started "
                + "from the keyboard during a queued wipe it therefore throws away that wipe's "
                + "verdict - the row is gone from the new list, so neither the spinner nor the "
                + "outcome alert can render - while the audit row and the administrator email still "
                + "say the wipe was queued",
                GatedTwinButtonLine: 74),
        ],

        HarmlessKeyboardPaths = [],
    };

    /// <summary>
    /// Group Management: tier 1, page 6 of 9, converted 2026-09-18 under
    /// docs/ClickGatingAudit-Plan.md Revision 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>LoadMembers was tearing the circuit down, and that is the sharpest thing found on this
    /// page.</b> It read selectedGroup live at all three sites below its await - twice for the load
    /// and once in the catch - and dereferenced it with "!". Three controls null or replace that
    /// field while the handler is suspended: the Close button at 55 sets it to null outright, Search
    /// nulls it before its own await, and a second Manage click at 307 replaces it. So the live read
    /// threw a NullReferenceException INTO the catch, and the catch's own selectedGroup!.Name threw
    /// again and escaped the handler. With no ErrorBoundary anywhere in this app that does not show
    /// an error - it tears the circuit down and the operator loses the page. The fix is the gmn-7
    /// snapshot every write handler on this page already used: capture the group at entry, return
    /// early when it is null, and read the local everywhere below.
    /// </para>
    /// <para>
    /// <b>SelectGroup's new flag is lowered by a monotonic token, and NOT by the
    /// ReferenceEquals(selectedGroup, group) test the rest of that method is written in.</b> That
    /// looks like an inconsistency and is the opposite: a lowering conditional on this group still
    /// being the selection never runs once the operator has switched groups, and isSelecting is a
    /// member of a page-wide predicate, so the flag would stick true and deaden EVERY control on
    /// this page permanently - the exact failure the sweep exists to prevent, introduced by the
    /// sweep itself. The token is monotonic, so the newest invocation always owns the lowering and
    /// always reaches its finally, while a superseded one lowers nothing and therefore cannot open
    /// the gate under the selection that replaced it. Same shape as MessageTrace.ToggleDetail.
    /// </para>
    /// <para>
    /// <b>The four surviving ReferenceEquals(selectedGroup, group) checks are NOT registered under
    /// PostAwaitLiveReads, on purpose.</b> AddMember, RemoveMember, RemoveSelectedAsync and
    /// AddResolvedAsync each re-read selectedGroup live after their awaits to decide whether to
    /// refresh the member list. Those live reads are the gmn-7 supersession contract - the question
    /// they ask is precisely "is the operator still looking at the group we wrote to?", and a
    /// snapshot cannot answer it, because the snapshot is the thing being compared against. Register
    /// selectedGroup for those handlers and the assertion would demand the one change that breaks
    /// them: a refresh that overwrites the new group's member list with the old group's.
    /// </para>
    /// <para>
    /// <b>Deviation from Revision 1, recorded rather than quietly taken.</b> Revision 1 predicted
    /// lines 179 and 219 as AnnotatedControls carrying RendersOnlyWhen prose. They are not
    /// annotations: both are banner dismisses - "() =&gt; opResult = null" and
    /// "() =&gt; bulkOutcome = null" - with no disabled attribute at all, which makes them
    /// exemptions, and the scanner agrees (Ungated 179 219). They are registered under
    /// ExemptControls with the reachability the prediction wanted written into each Reason, so
    /// nothing is lost by the move.
    /// </para>
    /// <para>
    /// <b>The per-row Remove button at 274 was INVISIBLE to this whole assertion suite, and is
    /// registered below now that it is not.</b> That was a harness defect found by the page-6
    /// conversion, not a gating gap; it was recorded here rather than fixed, and the follow-up
    /// slice that added ClickGateSource.IndexOfTagEnd fixed it. The measurement is kept because it
    /// is what justifies the fixture now guarding the walker. The file has 14 clickable buttons.
    /// Get-ClickGateAudit.ps1 reported 13 and ClickGateSource.ClickableButtons() also returned 13 -
    /// but not the same 13, which is what made the disagreement worth writing down. Both tag
    /// walkers were quote-aware and both treated an apostrophe as an opening single quote, so the
    /// title attribute at 276 - "This is the member's primary group; ..." - opened a quote state in
    /// the middle of the button that starts at 274, after which that tag's real "&gt;" was never
    /// reached. ClickGateSource.Tags added a tag only inside the branch that finds an unquoted
    /// "&gt;", so it dropped the 274 tag outright and found 307. The PowerShell scanner emitted the
    /// tag anyway once the walk ran out of text, so its 274 tag ran from 274 to the end of the file
    /// and swallowed the Manage button at 307 whole. Each walker lost a different button and both
    /// still said 13, which is why the count alone never looked wrong. (An earlier revision of this
    /// note had the PowerShell mechanism slightly wrong - it assumed that scanner reads raw text,
    /// but it blanks comments too, so the state never re-closed and the tag reached EOF rather than
    /// line 348. The net effect, 307 swallowed, was right.)
    /// </para>
    /// <para>
    /// The fix was the second of the two candidates recorded here: a quote character opens an
    /// attribute value only where it directly follows the "=" that introduces it. The first -
    /// emit the tag when the walk ends without an unquoted "&gt;" - is exactly what the PowerShell
    /// scanner already did, and its runaway 274 tag is what that candidate buys: every gate check
    /// in this suite is a containment test over the tag text, so a tag holding the rest of the file
    /// passes all of them for the wrong reason. Three consequences landed on this entry. The
    /// per-row Remove is registered under AnnotatedControls with its three non-busy clauses -
    /// string.IsNullOrWhiteSpace(ticketNumber), string.IsNullOrEmpty(member.ObjectGuid) and
    /// member.IsPrimaryMember. The last two are lst-1 and the primary-group rule, and together they
    /// are CanRemove at 737, which the select-all and per-row checkboxes registered under
    /// DomSyncedControls both depend on; a rewrite dropping them here would leave a row's checkbox
    /// and its Remove button disagreeing about which members can be acted on. Second, NO assertion
    /// in this suite failed when the walker was fixed: the button carries IsBusy, so the predicate
    /// sweep was satisfied by it the moment it became visible, and only the AnnotatedControls entry
    /// makes the three clauses asserted - being seen and being protected are different things.
    /// Third, the scanner's StuckFlags for this page fell from 4 to 1; see the note below.
    /// </para>
    /// <para>
    /// Note for whoever next reads Get-ClickGateAudit.ps1 output. Re-run against this page with the
    /// walker fixed it reports 14 buttons, Ungated 2 (179, 219), GateNoFlag 0, GatePart 1 (206),
    /// Flags 3, Predicate IsBusy, OtherClick 0 and StuckFlags 1 (targetProtection). The three
    /// control lines it names are exactly the three registered exemptions below. StuckFlags was 4
    /// before the walker fix and three of those four were artifacts of the runaway 274 tag, not
    /// findings: the stuck test asks whether a GATED BUTTON'S TEXT names the field, and a tag
    /// holding the rest of the file names every field in the code block. Only targetProtection is
    /// real, and only because the Close button at 55 nulls it inside its own inline handler.
    /// Unlike pages 3 and 4 the predicate detector DOES see this page, because IsBusy is an
    /// expression-bodied bool naming three flags rather than one.
    /// </para>
    /// </remarks>
    private static PageGateEntry GroupManagement => new()
    {
        Page = "GroupManagement.razor",
        ExpectedLineCount = 985,

        // One page-wide predicate, tested against this file rather than assumed. This page has a
        // single view: Search replaces searchResults and nulls the selection, SelectGroup replaces
        // the selection, the member list, the protection answer and all bulk state, and every write
        // handler ends by replacing the member list. There is no control here that is safe to leave
        // live while another is working. isSelecting is new in this slice - SelectGroup owned no
        // in-flight flag at all and awaits twice - and its lowering is token-guarded; see this
        // entry's remarks for why the obvious placement is the one that deadens the page.
        Predicates =
        [
            new PredicateScope("IsBusy", ["isLoading", "isResolving", "isSelecting"],
                AppliesWhen: "the whole page"),
        ],

        // Four fields carrying the scanner's in-flight shape - nullable, nulled and re-assigned
        // inside the same awaiting method with no finally - and not one of them an operation. The
        // scanner reported all four as stuck when this page was converted; with the tag walker
        // fixed it reports only targetProtection, because the other three were named by the runaway
        // 274 tag's text rather than by any real gated button. All four are kept: the shape is what
        // makes a field a candidate, and the next reader needs the reason whether or not a given
        // run of the scanner happens to name it. Nothing asserts this list - it is the reasoning
        // record, and the remarks above explain the count change.
        ScannerFalsePositives =
        [
            new ScannerFalsePositive("bulkOutcome",
                "the per-row verdict table for a batch that has already finished. RemoveSelectedAsync "
                + "and AddResolvedAsync both null it at entry and assign the outcome list after their "
                + "awaits, which is the reported shape, but it means 'here is what happened', not "
                + "'something is happening'"),

            new ScannerFalsePositive("opResult",
                "the single-operation result banner, on the same footing as bulkOutcome and reported "
                + "for the same reason on pages 1 and 2. Four handlers null it at entry and assign a "
                + "PermissionResult later; it outlives the operation that wrote it and is dismissed "
                + "by the operator"),

            new ScannerFalsePositive("resolution",
                "the resolved preview table for a batch not yet written. ResolvePasteAsync nulls it "
                + "at entry and assigns the rows after the batched AD lookup. The flag that actually "
                + "tracks that lookup is isResolving, which IS in the predicate"),

            new ScannerFalsePositive("targetProtection",
                "the query-time protection answer, and the one that matters. SelectGroup nulls it at "
                + "entry and sets it after the await, so it looks exactly like an in-flight flag - "
                + "but it is also null before any group is selected and stays null forever if none "
                + "ever is, so a predicate naming it would deaden the page at rest. That is why this "
                + "conversion had to CREATE isSelecting rather than promote the field the scanner "
                + "pointed at"),
        ],

        // Fifteen, by hand census of this file. Every one of them sits on a control that renders
        // only while the operator is deciding or reading, so folding any into the predicate disables
        // the control at the only moment it exists.
        ExcludedFields =
        [
            new ExcludedField("selectedGroup", "the whole manage card at 47-288",
                "the group the operator is looking at, not an operation. The card renders only while "
                + "it is non-null, so folding it in would disable every control inside the card at "
                + "the only moment any of them is shown - Load Members, Add, Remove and the entire "
                + "bulk apparatus. SelectGroup also assigns it before its first await, so the "
                + "predicate would latch the instant a group was picked and never clear"),

            new ExcludedField("targetProtection", "the protection panel at 65-74 and the card body",
                "the query-time protection answer. Null means 'not answered yet', which is also its "
                + "value before any group is selected and forever after if none is; see "
                + "ScannerFalsePositives, where this is the field the scanner mistakes for a flag"),

            new ExcludedField("memberList", "the Load Members button at 84 and the table at 237-283",
                "the loaded membership. The button renders only while it is null and the table only "
                + "while it is not, so a predicate naming it would either disable the button that "
                + "loads the list or every control that acts on the rows it loaded"),

            new ExcludedField("opResult", "the result banner at 175-181",
                "the previous operation's verdict, held on screen until the next handler nulls it or "
                + "the operator dismisses it at 179. Fold it in and the page stays dead for as long "
                + "as the banner is up - which, since nothing auto-dismisses it, is indefinitely"),

            new ExcludedField("bulkOutcome", "the per-row outcome table at 214-235",
                "the finished batch's per-row verdicts, on the same footing as opResult: a report of "
                + "work already done, held until dismissed at 219"),

            new ExcludedField("showRemoveConfirm", "the confirmation block at 195-211",
                "means 'a bulk removal is staged and waiting for the operator', not 'the page is "
                + "working'. The block renders only while it is true, so folding it in would disable "
                + "Remove at 202 at the only moment it is ever shown and no batch removal could be "
                + "confirmed again - the CalendarPermissions onPremConfirmPending regression"),

            new ExcludedField("selectedGuids", "the checkboxes at 245 and 260, Remove selected at 190",
                "which rows are ticked. Ticking a row would disable the checkboxes ticking is done "
                + "with and the button that ticking exists to enable"),

            new ExcludedField("showBulkAdd", "the panel toggle at 128 and the panel at 129-171",
                "whether the bulk add panel is open. A page-level bool with no operation behind it - "
                + "the IntuneDevices showActionHelp case - so a predicate naming it would deaden "
                + "every control on the page for as long as the operator left the panel open, and "
                + "the panel is what they open in order to act"),

            new ExcludedField("pasteText", "the textarea at 134 and Resolve at 137",
                "the pasted identity list. A form value the operator is mid-way through typing "
                + "exactly when the predicate would be consulted: fold it in and pasting the list "
                + "would disable the textarea it went into and the Resolve button that reads it"),

            new ExcludedField("resolution", "the preview table at 152-168 and Add resolved at 141",
                "the resolved preview for a batch not yet written. Resolving is what enables Add "
                + "resolved, through ResolvedCount, so a predicate naming it would disable that "
                + "button at the only moment it can be used"),

            new ExcludedField("ticketNumber", "the ticket input at 101; read by 118, 141, 190 and 274",
                "the staged ticket. Four buttons read it as an emptiness precondition, registered "
                + "under AnnotatedControls; that is a different job from a busy gate. Fold it in and "
                + "typing the ticket that unlocks those four buttons would disable all four"),

            new ExcludedField("newMember", "the ADIdentityAutocomplete at 111 and Add at 118",
                "the member being typed or picked. Same shape as ticketNumber: it is the precondition "
                + "Add reads, so folding it in disables Add the instant a member is chosen, and "
                + "disables the picker the operator is still typing into"),

            new ExcludedField("newMemberSelection", "the ADIdentityAutocomplete at 111",
                "the whole picker result held beside the visible text (gmn-3), because only the DN "
                + "distinguishes same-named groups across the forest's domains. Staged identity, not "
                + "an operation - and it is written by the picker's own callback, so a predicate "
                + "naming it would disable the control that had just been used"),

            new ExcludedField("searchTerm", "the search box at 35 and Search at 39",
                "the search text. Search reads it as an emptiness precondition (AnnotatedControls "
                + "39); folding it in would disable the box being typed into and, with it, the Enter "
                + "path registered under KeyboardPaths"),

            new ExcludedField("searchResults", "the results card at 290-315 and Manage at 307",
                "the groups found. The card renders only while the list is non-empty, so a predicate "
                + "naming it would disable every Manage button at the only moment any of them is on "
                + "screen, and no group could be selected after a search"),
        ],

        // Three, exactly the three the scanner names (Ungated 179 219, GatePart 206). Two dismisses
        // and one Cancel; see this entry's remarks for why the two dismisses are here rather than
        // under AnnotatedControls, where Revision 1 predicted them.
        ExemptControls =
        [
            new ExemptControl(179, "@onclick=\"() => opResult = null\"",
                "dismisses the single-operation result banner. Gating it on IsBusy would trap the "
                + "previous operation's message on screen for the whole of the next one, the same "
                + "reason Migration's dismiss at 442, DhcpAuthorization's at 40, NamedLocations' at "
                + "45, MailboxPermissions' at 179 and CalendarPermissions' at 178 are exempt. "
                + "Reachability, which is what Revision 1 wanted recorded when it predicted this as "
                + "an annotation: it renders four conditions deep - inside the manage card "
                + "(selectedGroup != null), inside the answered-and-allowed branch of "
                + "targetProtection, inside memberList != null with no Error, and then only while "
                + "opResult != null",
                ConditionThatKeepsItTrue:
                "no handler may READ opResult back. Today none does: every handler that touches it "
                + "only assigns, and each holds its own verdict in a local outcome or summary. That "
                + "is why this exemption carries no PrerequisiteBeforeExemptionHolds and no "
                + "PostAwaitLiveReads entry names opResult - there is no live read to close. The "
                + "moment a handler reads it after an await this becomes the DhcpAuthorization 40 "
                + "defect: a write that SUCCEEDED audited and reported as failed"),

            new ExemptControl(219, "@onclick=\"() => bulkOutcome = null\"",
                "dismisses the batch outcome table, on the same footing as the banner above. The "
                + "table can still be on screen during an unrelated single-member write, because "
                + "neither AddMember nor RemoveMember clears bulkOutcome. Reachability, again as the "
                + "prediction wanted it: the same four-deep chain as 179, and then only while "
                + "bulkOutcome != null",
                ConditionThatKeepsItTrue:
                "RemoveSelectedAsync and AddResolvedAsync must keep building their outcome list in a "
                + "local and only assigning it to bulkOutcome. Both do: outcomes is the local, "
                + "BulkOutcomeSummary.Of(outcomes) reads the local, and the audit row, the summary "
                + "email and the banner text are all computed from the summary. Nothing reads the "
                + "field back, so this control cannot null it out from under a dereference"),

            new ExemptControl(206, "@onclick=\"() => showRemoveConfirm = false\"",
                "backs out of a staged bulk removal, and the operator must always be able to do "
                + "that. Remove beside it at 202 is gated on IsBusy, so nothing can be executed from "
                + "this state while the page is busy; cancelling only clears one bool and makes no "
                + "call. Reachability: inside the manage card, inside canManageOnPrem, and then only "
                + "while showRemoveConfirm && SelectedCount > 0",
                KeepsItsOwnGuard: "disabled=\"@isLoading\"",
                ConditionThatKeepsItTrue:
                "nothing reads showRemoveConfirm after an await. It is kept narrow rather than "
                + "widened to IsBusy because a cancel must not be refused during a protection check "
                + "or a paste resolution, and kept rather than deleted because - unlike the "
                + "IntuneDevices 407 twin, whose bar cannot render while its flag is set - this "
                + "block really can be on screen while the page is busy: neither AddMember nor "
                + "RemoveMember clears showRemoveConfirm, so a single-member write started with the "
                + "confirmation open leaves it open and isLoading true at the same time"),
        ],

        // Verified by exhaustive census rather than inherited: Get-ClickGateAudit.ps1 reports
        // OtherClick 0, and the hand census of what it cannot see finds no anchor, no div/span/td
        // handler, no @onsubmit and no InputFile. The two @onchange checkboxes and the one @onkeydown
        // input it does find are registered under DomSyncedControls and KeyboardPaths instead, on the
        // precedent of the five pages before it: this list is keyed to what ClickGateSource can
        // locate, which is @onclick only, so an input registered here would fail the assertion rather
        // than protect anything.
        NonButtonTargets = [],

        // Six, all naming the page predicate. The two checkboxes keep a non-busy clause OR-ed with
        // it: select-all is meaningless with nothing selectable, and a per-row box must stay refused
        // for a primary-group or unresolved member whatever the page is doing.
        DomSyncedControls =
        [
            new DomSyncedControl(35, "placeholder=\"Search groups by name or email...\"", "input",
                "disabled=\"@IsBusy\""),
            new DomSyncedControl(101, "placeholder=\"Ticket #\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(111, "ObjectKind=\"Any\"", "ADIdentityAutocomplete",
                "Disabled=\"@IsBusy\""),
            new DomSyncedControl(134, "@bind=\"pasteText\"", "textarea", "disabled=\"@IsBusy\""),
            new DomSyncedControl(245, "checked=\"@AllSelectableSelected\"", "input",
                "disabled=\"@(IsBusy || SelectableMembers().Count == 0)\""),
            new DomSyncedControl(260, "checked=\"@selectedGuids.Contains(member.ObjectGuid)\"", "input",
                "disabled=\"@(IsBusy || !CanRemove(member))\""),
        ],

        UngatedDomSyncedControls = [],

        // Four callees, every one of them reached from a handler that has already raised a flag in
        // the predicate. Two of them are the shared per-member paths the bulk loops and the single
        // buttons both go through, which is what makes a guard there so expensive: it would fire once
        // per row.
        ForbiddenGuardSites =
        [
            new ForbiddenGuardSite("AddOneAsync", ["AddMember", "AddResolvedAsync"],
                "The ONE per-member add path - authorization re-check, protection-gated write, audit "
                + "and admin email - shared byte-for-byte by the single Add button and the bulk add "
                + "loop, and both callers raise isLoading before reaching it. A busy guard here makes "
                + "every add a silent no-op that still audits, still emails administrators and still "
                + "tells the operator the member was added. In the bulk loop it would do that once "
                + "per row, so a whole pasted batch reports Done for members that were never written."),

            new ForbiddenGuardSite("RemoveOneAsync", ["RemoveMember", "RemoveSelectedAsync"],
                "The ONE per-member remove path, on the same footing as AddOneAsync and reached from "
                + "both callers after isLoading is raised. A guard here leaves members in a group the "
                + "audit record says they were removed from - and in the batch it does so silently "
                + "for every ticked row at once, which is precisely the per-item aggregation this "
                + "page was built to make honest."),

            new ForbiddenGuardSite("AuditBatch", ["RemoveSelectedAsync", "AddResolvedAsync"],
                "The only writer of the batch summary audit record, called from inside both batch "
                + "handlers below their raise - on the ticket-refusal path, the authorization-refusal "
                + "path and the completed path. A guard here drops the Constitution-required record "
                + "for exactly the runs that matter, and drops it without even the log line the catch "
                + "would have written, because the guard returns before the try."),

            new ForbiddenGuardSite("ClearBulkState", ["Search", "SelectGroup"],
                "Clears the ticked rows, the pending confirmation and both outcome tables when the "
                + "view changes. Search calls it after raising isLoading and SelectGroup after "
                + "raising isSelecting, so a guard there leaves a previous group's ticked GUIDs, its "
                + "staged confirmation and its outcome table on screen under a different group. The "
                + "ticked GUIDs are what Remove selected acts on, so the next confirmed batch would "
                + "attempt removals against the group the operator has just navigated away from."),
        ],

        // Empty for the same structural reason as pages 1, 2 and 5: no handler on this page is called
        // by another handler that could carry the refusal instead. Every click target here is its own
        // entry point, and SelectGroup, Search, LoadMembers and the four write handlers each raise
        // their flag as their own first work, so a guard inside one would be the re-entrancy shape
        // the 2026-09-17 ruling rejects.
        ExactlyOneGuard = [],

        // Six gated buttons whose disabled expression keeps a non-busy clause that must survive a
        // mechanical rewrite. None of the six is equivalent to the predicate: each is a precondition
        // the operator has to satisfy before the action is meaningful, and four of them are the only
        // thing requiring a ticket number before a write.
        //
        // The sixth is the per-row Remove at 274, and it could not be registered when this page was
        // converted: the tag walkers could not see that tag, so the entry failed outright with "no
        // clickable button at annotated line 274". That failure is how the walker defect was found.
        // The entry is live now that the walker is fixed; see this entry's remarks.
        AnnotatedControls =
        [
            new AnnotatedControl(39, "@onclick=\"Search\"",
                ["string.IsNullOrWhiteSpace(searchTerm)"],
                RendersOnlyWhen:
                "always, once the authorization check has completed. The clause stops an empty search "
                + "being submitted; HandleSearchKey applies the same emptiness test to the Enter path "
                + "beside it, which is registered under KeyboardPaths"),

            new AnnotatedControl(118, "@onclick=\"AddMember\"",
                [
                    "string.IsNullOrWhiteSpace(newMember)",
                    "string.IsNullOrWhiteSpace(ticketNumber)",
                ],
                RendersOnlyWhen:
                "inside the manage card, inside the answered-and-allowed protection branch, inside "
                + "memberList != null with no Error, and then only while canManageOnPrem. AddMember "
                + "re-checks the member label itself and ServiceNow re-checks the ticket, so both "
                + "clauses are belt and braces here - but they are what keeps the operator from "
                + "submitting a write that can only be refused"),

            new AnnotatedControl(137, "@onclick=\"ResolvePasteAsync\"",
                ["string.IsNullOrWhiteSpace(pasteText)"],
                RendersOnlyWhen:
                "only inside the bulk add panel, so only while showBulkAdd is true and the operator "
                + "holds canManageOnPrem. The clause stops an empty paste being resolved"),

            // Both clauses are anchored with "|| " for the same reason as 274 below: the title at
            // 143 explains both refusals in prose naming the same two expressions - "@(string.
            // IsNullOrWhiteSpace(ticketNumber) ? ..." and "(ResolvedCount == 0 ? ...". Registered
            // bare, either would have kept passing after being deleted from the disabled attribute.
            // The "|| " occurs only in the gate, and it says something the bare clause does not:
            // this condition is OR-ed alongside the predicate, never the whole of the gate.
            new AnnotatedControl(141, "@onclick=\"AddResolvedAsync\"",
                [
                    "|| ResolvedCount == 0",
                    "|| string.IsNullOrWhiteSpace(ticketNumber)",
                ],
                RendersOnlyWhen:
                "inside the bulk add panel, as Resolve above it. ResolvedCount == 0 is the only thing "
                + "in the markup requiring the list to be resolved before it is written; "
                + "AddResolvedAsync itself returns early on an empty row set, so the clause is what "
                + "makes the refusal visible rather than silent"),

            // Anchored on the same finding as 141 above and 274 below: the title at 192 names both
            // of this gate's non-busy clauses in the prose that explains them, so bare registrations
            // were satisfied by the tooltip and would have survived deletion from the gate.
            new AnnotatedControl(190, "@onclick=\"() => showRemoveConfirm = true\"",
                [
                    "|| SelectedCount == 0",
                    "|| string.IsNullOrWhiteSpace(ticketNumber)",
                ],
                RendersOnlyWhen:
                "inside the manage card's member table region and only while canManageOnPrem. This "
                + "button stages the confirmation rather than writing, so its clauses are the "
                + "earliest point at which a batch with no ticket or no ticked rows is refused; the "
                + "confirmation block it opens renders only while SelectedCount > 0 as well"),

            // Two of these three clauses carry a "||" that the others here do not need. This tag's
            // title at 276 explains both refusals to the operator in prose that names the same two
            // expressions - "@(member.IsPrimaryMember ? ..." and "(string.IsNullOrEmpty(member.
            // ObjectGuid) ? ...". When the clause check was containment over the whole tag text,
            // both clauses registered bare kept passing after being deleted from the disabled
            // attribute, because the title alone satisfied the containment. Proven by mutation:
            // dropping "|| member.IsPrimaryMember" from 275 failed this assertion once the anchor
            // was added and did not before.
            //
            // The assertion now reads the disabled attribute VALUE rather than the tag, which
            // closes the class this entry only closed for itself - 141 and 190 above had the same
            // shape and were never swept. The anchors stay: they are cheap, and "|| clause" says
            // something the bare clause does not, that the condition is OR-ed alongside the
            // predicate rather than being the whole of the gate. ticketNumber appears once in this
            // tag and needs no anchor, which is left as the record that an anchor is a response to
            // a duplicate and not a house style.
            new AnnotatedControl(274, "@onclick=\"() => RemoveMember(member)\"",
                [
                    "string.IsNullOrWhiteSpace(ticketNumber)",
                    "|| string.IsNullOrEmpty(member.ObjectGuid)",
                    "|| member.IsPrimaryMember",
                ],
                RendersOnlyWhen:
                "once per member row, inside the manage card, inside the answered-and-allowed "
                + "protection branch, inside memberList != null with no Error, and then only while "
                + "canManageOnPrem. The ticket clause is the same precondition the other three "
                + "write buttons carry. The other two are the row's own eligibility and they are "
                + "NOT belt and braces: an empty ObjectGuid means the row could not be resolved in "
                + "its own domain and the service refuses it too (lst-1), and IsPrimaryMember means "
                + "primary-group membership, which cannot be removed by rewriting a member list at "
                + "all. Together they are exactly CanRemove at 737, which the per-row checkbox at "
                + "260 gates on as !CanRemove(member) and the select-all at 245 reaches through "
                + "SelectableMembers(). Drop either clause here and a row stays tickable for a "
                + "batch removal whose own per-row button refuses it, or becomes individually "
                + "removable while the batch path skips it - the two disagreeing about which "
                + "members may be removed, which is the state this entry exists to prevent"),
        ],

        // Five spinner and panel conditions, every one reading a single flag or a flag paired with a
        // state field rather than the predicate, registered verbatim so a mechanical "simplify to the
        // predicate" pass fails instead of quietly changing what the operator sees. 40 is the Search
        // button's spinner, narrowed by selectedGroup == null so it does not also spin during a
        // member load; 85 belongs to Load Members and 203 to the batch Remove, both plain isLoading;
        // 138 is Resolve's, on the resolve flag alone; 144 keys Add resolved's spinner to a batch
        // that has a resolution behind it. 65 is not a spinner but the same hazard: it gates the
        // whole card body on the protection answer, and folding it into IsBusy would blank the panel
        // during every unrelated operation.
        SpinnerExpressions =
        [
            "@if (isLoading && selectedGroup == null)",
            "@if (isLoading)",
            "@if (isResolving)",
            "@if (isLoading && resolution != null)",
            "@if (targetProtection is null)",
        ],

        // Ten. Falsification 6 bit harder on this page than on any converted so far: seven of these
        // are new in this slice and one of them - LoadMembers reading selectedGroup - was not a
        // desync but a circuit teardown; see this entry's remarks. The three that predate the slice
        // are registered rather than assumed, so a later edit cannot quietly reinstate the live read.
        PostAwaitLiveReads =
        [
            new PostAwaitLiveRead("Search", "searchTerm", "term",
                SnapshotShape.CapturedAtEntry,
                "the term the operator submitted must be the term searched for. The box binds on "
                + "oninput and the browser's copy of its disabled attribute is one round trip stale, "
                + "so a keystroke landing in the gap would run a search nobody asked for and leave "
                + "the results table labelled with text that is still visible in the box"),

            new PostAwaitLiveRead("LoadMembers", "selectedGroup", "group",
                SnapshotShape.CapturedAtEntry,
                "the sharpest defect on the page, and not a gating defect at all. This handler read "
                + "the field live at all three sites below its await - twice for the load and once "
                + "in the catch - and dereferenced it with '!'. Close at 55 nulls it, Search nulls "
                + "it, a second Manage at 307 replaces it, so the live read threw a "
                + "NullReferenceException INTO the catch, whose own selectedGroup!.Name threw again "
                + "and escaped the handler. With no ErrorBoundary anywhere in this app the operator "
                + "does not see an error, they lose the page"),

            new PostAwaitLiveRead("AddMember", "ticketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the ticket validated against ServiceNow must be the ticket written into the audit "
                + "row and the administrator email. Read live below the yield, a keystroke landing "
                + "in the stale-attribute window desynced the two, so the record names a ticket "
                + "nobody validated"),

            new PostAwaitLiveRead("RemoveMember", "ticketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "as AddMember, on the destructive half: the removal's audit row and notification "
                + "must carry the ticket that was actually validated for it"),

            new PostAwaitLiveRead("RemoveSelectedAsync", "ticketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "worse in a batch, because the ticket is validated ONCE and then recorded against "
                + "every row. Read live, a single keystroke in the gap put one ticket through "
                + "ServiceNow and a different one into every audit row and the summary email for the "
                + "whole batch"),

            new PostAwaitLiveRead("AddResolvedAsync", "ticketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "as RemoveSelectedAsync: one validation, many records, so the capture is what keeps "
                + "them describing the same ticket"),

            new PostAwaitLiveRead("ResolvePasteAsync", "pasteText", "paste",
                SnapshotShape.CapturedAtEntry,
                "the textarea binds on oninput, so a keystroke landing in the gap would build the "
                + "preview table - and therefore the batch AddResolvedAsync writes to AD - out of "
                + "lines that were never submitted, under a header saying how many of them resolved"),

            new PostAwaitLiveRead("AddMember", "newMember", "memberLabel",
                SnapshotShape.CapturedAtEntry,
                "the member the operator typed is what is written, audited and emailed. The picker "
                + "stays live for the one round trip in which its Disabled parameter is stale, and "
                + "the handler clears the box itself on success, so a live read below the await "
                + "could act on a different identity from the one the click was accepted for"),

            new PostAwaitLiveRead("AddMember", "newMemberSelection", "selection",
                SnapshotShape.CapturedAtEntry,
                "the held picker result, and the gmn-3 half that actually matters: only the DN "
                + "distinguishes same-named groups across the forest's domains, so a stale or "
                + "replaced selection read below the await would write to a group in the wrong "
                + "domain while the audit row named the label the operator saw"),

            new PostAwaitLiveRead("AddResolvedAsync", "resolution", "rows",
                SnapshotShape.CapturedAtEntry,
                "the resolved rows are the batch. Resolve can complete while this handler is "
                + "suspended - isResolving is in the predicate now, but the browser's copy of the "
                + "attribute is still one round trip stale - and a live read would then write a "
                + "different set of members from the one the summary audit and email describe"),
        ],

        // One, and it is new in this slice. The capture sits above the guard and the raise below it.
        RaiseMustFollowEarlyReturn =
        [
            new RaiseAfterEarlyReturn("LoadMembers", "isLoading", "group is null",
                "the early return leaves no finally behind it, so a raise above the guard is never "
                + "lowered. isLoading is a member of the page-wide predicate, so it would not grey "
                + "one button - it would deaden every control on the page permanently, the first "
                + "time Load Members was clicked with no group selected. The guard itself is new "
                + "here: before this slice the handler had no null check at all and dereferenced "
                + "selectedGroup with '!' instead."),
        ],

        // The page's one keyboard handler. Before this conversion the box carried no disabled
        // attribute at all, so Enter reached Search while the Search button beside it was already
        // gated on isLoading - the 2eb8c15 shape exactly. The disabled attribute rather than a
        // handler guard, on the Migration precedent: a disabled input fires no key events, so one
        // attribute closes the key path and the typing path together.
        KeyboardPaths =
        [
            new KeyboardPath(35, "placeholder=\"Search groups by name or email...\"", "input",
                "keydown", "HandleSearchKey", "Search",
                KeyboardRefusal.DisabledAttribute, "disabled=\"@IsBusy\"",
                "Enter runs the group search that the Search button at 39 refuses while the page is "
                + "busy. Search is not a read-only refresh: it nulls selectedGroup and memberList "
                + "and calls ClearBulkState, so started from the keyboard during a protection check "
                + "or a queued batch it throws away the selection those operations are acting on - "
                + "and LoadMembers used to dereference the field it nulls, which is how this page's "
                + "circuit-teardown defect was reachable from the keyboard as well as the mouse",
                GatedTwinButtonLine: 39),
        ],

        HarmlessKeyboardPaths = [],
    };

    /// <summary>
    /// M365 Group Management: tier 1, page 7 of 9, converted 2026-09-19 under
    /// docs/ClickGatingAudit-Plan.md Revision 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The flag that was wired to no attribute at all, and what it turned out to be.</b>
    /// Revision 1 predicted four flags with one of them gating nothing. The hand census confirms
    /// the count - isSearching, isLoadingDetails, isOperating, isMemberOp, and
    /// Get-ClickGateAudit.ps1 names the same four - and the one is isLoadingDetails. Its only
    /// appearance in the markup was the row spinner at 98; no disabled= anywhere consulted it. It
    /// is a MISSING GATE rather than a field that was never a flag, and the difference matters
    /// because the two have opposite fixes: SelectGroup raises it as its first statement, awaits
    /// GetGroupDetailsAsync, GetMembersAsync and GetOwnersAsync, and lowers it in a finally, which
    /// is the in-flight shape exactly. What the gap cost: every per-row View button was gated on
    /// isOperating alone, so a second View was live for the whole of the first load. SelectGroup
    /// assigns selectedGroup at entry and members and owners after its awaits, so two interleaved
    /// invocations leave the details panel describing one group while the member and owner tables
    /// below list another - and the ticket box, both Add buttons and every per-row Remove act on
    /// selectedGroup, not on the table they are rendered beside. Folding it into IsBusy is the
    /// whole of the fix; nothing else on the page had to change to close it.
    /// </para>
    /// <para>
    /// <b>The Cancel at 216 inverts the exemption heuristic, and the Cancel at 287 does not.</b>
    /// On every converted page so far a Cancel is exempt because the operator must always be able
    /// to back out of a staged action, and backing out is a pure reset of a field nothing reads
    /// mid-flight. This page has one of each, and it had them the wrong way round: the staged
    /// Cancel at 287 (confirmDelete = null) already carried disabled="@isOperating", while the
    /// form Cancel at 216 carried no attribute at all.
    /// </para>
    /// <para>
    /// 216 is not a staged cancel. It calls CancelForm, which nulls editingGroup - and editingGroup
    /// is what SaveGroup steers on. SaveGroup suspends at its yield and again at
    /// GetAuthenticationStateAsync and AuthorizeAsync, all of them BEFORE it used to read
    /// editingGroup to choose its branch. A Cancel accepted in any of those windows flipped the
    /// handler from Update to Create: it called CreateGroupAsync with the mail nickname EditGroup
    /// blanks at 444, and audited and emailed the click as M365GroupManagement_Create against a
    /// bare display name with no group id in it. So the usual reasoning inverts - here backing out
    /// is not a no-op on a staged selection, it retargets a write already in flight - and the
    /// control is GATED rather than exempt. It costs the operator nothing: every other control in
    /// that panel (243, 250, 264, 269, 277, 286, 287, 291, 295) was already refused during
    /// isOperating, so a live Cancel was the only thing in the form that could do anything, and
    /// the only thing it could do is the above. SaveGroup closes the form itself on success. The
    /// gate is one round trip stale, so the editingGroup capture in PostAwaitLiveReads is what
    /// actually closes the window; both landed together. 287 keeps its exemption and its narrower
    /// guard, on the DhcpAuthorization 83 and NamedLocations 192 footing.
    /// </para>
    /// <para>
    /// <b>One page-wide predicate, tested rather than assumed.</b> Revision 1 found a single
    /// predicate provably wrong on two other pages, so this one was checked: there is one view
    /// here and no control in it is safe to leave live while another works. SearchGroups nulls
    /// selectedGroup, members, owners and showForm; SelectGroup replaces all three of the last;
    /// SaveGroup and DeleteGroup end by calling SearchGroups, which throws the selection away; and
    /// RunMemberOpAsync replaces the member and owner lists. The sharpest proof is the pairing the
    /// gate closes: before this slice the Search box and button were gated on isSearching alone,
    /// so a search could be started in the middle of a member write, and RunMemberOpAsync then
    /// dereferenced the selectedGroup that search had just nulled - see PostAwaitLiveReads.
    /// </para>
    /// <para>
    /// <b>Falsification 6 on this page, and the ticket it names.</b> Thirteen obligations. Three
    /// are the operationResult publish that makes the banner dismiss at 44 safe to leave
    /// clickable; ten are entry captures. formTicketNumber is the one Revision 1 names for this
    /// page by name - EditGroup (447) and ShowCreateForm (435) both blank it, DeleteGroup audited
    /// and emailed it from the catch after several awaits, and SaveGroup read it live at four
    /// sites - and memberTicketNumber is the same defect on the member and owner half. Worth
    /// stating plainly because it changes what those clauses are worth: nothing on this page
    /// validates a ticket against ServiceNow. The emptiness clauses in AnnotatedControls are the
    /// ONLY thing requiring a ticket before a write, and the capture is the only thing making the
    /// recorded ticket the one the operator typed.
    /// </para>
    /// <para>
    /// <b>Two live reads that PostAwaitLiveReads cannot express, fixed in code and recorded
    /// here.</b> AddMember and AddOwner built a closure reading addMemberIdentity / addOwnerIdentity
    /// live, and RunMemberOpAsync invoked it two awaits later - while the eagerly evaluated
    /// targetLabel argument beside it had already snapshotted the same value at the click. A
    /// keystroke in the gap therefore wrote one identity into Graph and a different one into the
    /// audit row and the admin email. Both thunks now capture the identity into a local before
    /// calling the pipeline. The obligation cannot be registered: AddMember and AddOwner contain
    /// no await of their own, so AssertSnapshotObligationHolds would fail them for that rather
    /// than check anything. The closures also read selectedGroup live; that half IS covered,
    /// because the pipeline now passes the captured group into the op.
    /// </para>
    /// </remarks>
    private static PageGateEntry M365GroupManagement => new()
    {
        Page = "M365GroupManagement.razor",
        ExpectedLineCount = 714,

        Predicates =
        [
            new PredicateScope("IsBusy",
                ["isSearching", "isLoadingDetails", "isOperating", "isMemberOp"],
                AppliesWhen: "the whole page"),
        ],

        // Get-ClickGateAudit.ps1 reported FOUR flags and ZERO stuck flags before this slice, and
        // four flags and ONE stuck flag after it. The change is an artefact of this page's own
        // snapshot fix, exactly as on pages 1 and 2, and is recorded so nobody "corrects" it.
        ScannerFalsePositives =
        [
            new ScannerFalsePositive("operationResult",
                "the result banner's model. The scanner counts a bare-token assignment to a "
                + "nullable as a raise (Get-ClickGateAudit.ps1:209), so each new "
                + "`operationResult = result;` publish reads as one and the `operationResult = null;` "
                + "at handler entry reads as the lowering, outside a finally - the stuck-flag shape. "
                + "Moving the clear into a finally would wipe the banner at the end of the handler "
                + "that just wrote it, so the operator would never see the outcome of a create, an "
                + "update, a delete or a membership change. The scanner is wrong here and must stay "
                + "wrong. DhcpAuthorization and NamedLocations report the same thing for the same "
                + "reason."),
        ],

        // Twenty-two, by hand census of this file rather than the three Revision 1 predicted. The
        // prediction named the staged-confirmation trio; the census adds the form state that gates
        // the controls it would disable, two bools that are raised and never lowered, and the form
        // values four buttons read as preconditions. Every one of them sits on a control that
        // renders only while the operator is deciding, typing or reading.
        ExcludedFields =
        [
            new ExcludedField("confirmDelete", "the Confirm Delete / Cancel pair at 286-287",
                "holds the id of the group whose deletion is staged, not an operation in flight. "
                + "The pair renders only while confirmDelete == editingGroup.Id, so folding it in "
                + "would disable Confirm Delete at the only moment it is ever shown and no M365 "
                + "group could be deleted again - the regression a review of the Migration plan "
                + "caught before any code was written"),

            new ExcludedField("confirmRemoveMember", "the Confirm / cancel pair at 144-145",
                "the id of the member whose removal is staged. Same shape as confirmDelete, once "
                + "per member row: the pair renders only while confirmRemoveMember == m.Id"),

            new ExcludedField("confirmRemoveOwner", "the Confirm / cancel pair at 186-187",
                "the id of the owner whose removal is staged, on the same footing as "
                + "confirmRemoveMember. Removing the last owner of a group is the quietest "
                + "destructive act on this page, and it is confirmed here"),

            new ExcludedField("showForm", "the whole create/edit form at 239-301",
                "means 'the operator is filling in a form', not 'the page is working'. Every "
                + "control IsBusy exists to protect - the name, nickname, description, visibility, "
                + "ticket, Delete, Confirm Delete and Save - is inside the block showForm gates, so "
                + "folding it in would disable the form at the only moment it renders"),

            new ExcludedField("editingGroup", "the Delete cluster at 282-293 and the Save at 295",
                "which group is being edited, and the field that decides Update versus Create. The "
                + "Delete cluster renders only while it is non-null and the Save button's own "
                + "nickname clause reads it, so a predicate naming it would disable the destructive "
                + "half of the form at the only moment it is reachable. It is also a registered "
                + "PostAwaitLiveReads field; that obligation is what protects its value, not this"),

            new ExcludedField("selectedGroup", "the member/owner panel at 113-205 and details at 222-232",
                "the group the operator is looking at. Both panels render only while it is "
                + "non-null, so folding it in would disable every control inside either at the only "
                + "moment any of them is shown. SelectGroup also assigns it before its first await, "
                + "so the predicate would latch the instant a group was picked and never clear"),

            new ExcludedField("members", "the member panel at 113-205",
                "the loaded membership. Nulled at entry and re-assigned after the awaits in both "
                + "SelectGroup and RunMemberOpAsync, which is most of the in-flight shape, but the "
                + "panel renders only while it is non-null - so a predicate naming it would disable "
                + "every control that acts on the rows it just loaded"),

            new ExcludedField("owners", "the owners table at 172-199",
                "the loaded owner list, on exactly the same footing as members and written by the "
                + "same two handlers"),

            new ExcludedField("hasSearched", "the empty-state prose at 71-74 and 108-111",
                "set true by SearchGroups and never lowered anywhere. Fold it in and the page is "
                + "dead from the first search onwards, permanently, which is the same failure shape "
                + "as a raise above an early return"),

            new ExcludedField("authChecked", "the whole page; the body at 16-26 returns while it is false",
                "the one-shot initialisation gate, raised at the end of OnInitializedAsync and "
                + "never lowered. It has the same raised-in-an-awaiting-method shape as a flag and "
                + "is the opposite of one: IsBusy naming it would be true for the entire life of "
                + "every usable render, and false only while nothing it could gate is on screen"),

            new ExcludedField("operationResult", "the result banner at 40-46",
                "the previous operation's verdict, held until the next handler nulls it or the "
                + "operator dismisses it at 44. Fold it in and the page stays dead for as long as "
                + "the banner is up - which, since nothing auto-dismisses it, is indefinitely"),

            new ExcludedField("searchError", "the error alert at 67-70",
                "the search failure message, nulled at entry and set in the catch of SelectGroup "
                + "and SearchGroups. A report of work already finished, not work in progress"),

            new ExcludedField("groups", "the results table at 75-107",
                "the groups found. The table renders only while the list is non-empty, so a "
                + "predicate naming it would disable every View button at the only moment any of "
                + "them is on screen and no group could be selected after a search"),

            new ExcludedField("searchTerm", "the search box at 58 and Search at 60",
                "the search text. Search reads it as an emptiness precondition (AnnotatedControls "
                + "60); folding it in would disable the box being typed into and, with it, the "
                + "Enter path registered under KeyboardPaths"),

            new ExcludedField("memberTicketNumber", "the ticket input at 118; read by 124, 144, 166, 186",
                "the staged ticket for every member and owner change. FOUR buttons read it as an "
                + "emptiness precondition, and nothing in the handlers or the service re-checks it, "
                + "so typing the ticket that unlocks all four would disable all four"),

            new ExcludedField("addMemberIdentity", "the identity box at 123 and Add at 124",
                "the member being typed. It is the precondition Add reads, so folding it in "
                + "disables Add the instant an identity is entered, and disables the box it was "
                + "entered into"),

            new ExcludedField("addOwnerIdentity", "the identity box at 165 and Add at 166",
                "the owner being typed, on the same footing as addMemberIdentity"),

            new ExcludedField("formTicketNumber", "the ticket input at 277; read by 286 and 295",
                "the staged ticket for a create, an update or a delete. Same shape as "
                + "memberTicketNumber, and the same absence of any re-check behind it"),

            new ExcludedField("formDisplayName", "the name input at 243 and Save at 295",
                "the group name. Save reads it as an emptiness precondition, so a predicate naming "
                + "it would disable Save the moment a name was typed and disable the box as well"),

            new ExcludedField("formMailNickname", "the nickname input at 250 and Save at 295",
                "the mail nickname, required for a create and read by Save's third clause. Same "
                + "shape as formDisplayName, and it renders only in the editingGroup == null branch"),

            new ExcludedField("formDescription", "the description textarea at 264",
                "a form value the operator is mid-way through typing exactly when the predicate "
                + "would be consulted. No button reads it, which is the only thing that makes it "
                + "milder than formDisplayName - it is still a value, not an operation"),

            new ExcludedField("formVisibility", "the visibility select at 269",
                "which is Private or Public for the group being written, on the same footing as "
                + "formDescription. A bound select is the worst control to gate on its own value: "
                + "the change that set it would disable the control that shows it"),
        ],

        // Four, exactly the four the scanner names as narrow after this conversion (44, 145, 187,
        // 287). One banner dismiss and three staged cancels. The form Cancel at 216 is NOT here -
        // see this entry's remarks for why that one is gated instead.
        ExemptControls =
        [
            new ExemptControl(44, "@onclick=\"() => operationResult = null\"",
                "dismisses the result banner. Gating it on IsBusy would trap the previous "
                + "operation's message on screen for the whole of the next search, details load or "
                + "write, the same reason Migration's dismiss at 442, DhcpAuthorization's at 40, "
                + "NamedLocations' at 45, MailboxPermissions' at 179, CalendarPermissions' at 178 "
                + "and GroupManagement's at 179 are exempt. It renders only while operationResult "
                + "!= null, so a dismiss is always executed against a banner that is really there",
                KeepsItsOwnGuard: "disabled=\"@(isOperating || isMemberOp)\"",
                ConditionThatKeepsItTrue:
                "the handler stays a pure field reset. The moment anything else is wired to this "
                + "button it is an operation and belongs behind IsBusy with the rest",
                PrerequisiteBeforeExemptionHolds:
                "SaveGroup, DeleteGroup and RunMemberOpAsync must keep the write result in a local "
                + "- var result = await ...; operationResult = result; - and read that local "
                + "afterwards. Before they did, this control could null operationResult while any "
                + "of the three was suspended at its admin-notification await, and the following "
                + "operationResult.Success read threw a NullReferenceException into the handler's "
                + "own catch, which then audited and emailed a Graph write that had SUCCEEDED as a "
                + "failure and skipped the confirming refresh. On DeleteGroup that is a group that "
                + "really is gone reported as still present. The narrower guard above is belt and "
                + "braces only: the browser's copy of a disabled attribute is one round trip stale, "
                + "so the guard narrows that window and the local is what closes it. It names the "
                + "two write flags rather than IsBusy so a banner is not trapped on screen for the "
                + "whole of a search or a details load, which is the case the exemption exists for. "
                + "Reinstate the field reads and this exemption is a live defect again"),

            new ExemptControl(145, "@onclick=\"() => confirmRemoveMember = null\"",
                "backs out of a staged member removal, and the operator must always be able to do "
                + "that. Confirm beside it at 144 is gated on IsBusy, so nothing can be executed "
                + "from this state while the page is busy; cancelling only nulls one id and makes "
                + "no call. The pair can still be on screen during another operation because "
                + "nothing except a successful removal clears confirmRemoveMember",
                KeepsItsOwnGuard: "disabled=\"@isMemberOp\"",
                ConditionThatKeepsItTrue:
                "nothing reads confirmRemoveMember after an await. RemoveMember's onSuccess "
                + "callback assigns it and never reads it; the moment a handler READS it "
                + "mid-flight, this control can null it under them and the exemption stops holding"),

            new ExemptControl(187, "@onclick=\"() => confirmRemoveOwner = null\"",
                "backs out of a staged owner removal, on exactly the footing of 145 above and with "
                + "the same narrower guard. Confirm beside it at 186 is gated",
                KeepsItsOwnGuard: "disabled=\"@isMemberOp\"",
                ConditionThatKeepsItTrue:
                "nothing reads confirmRemoveOwner after an await, as for confirmRemoveMember"),

            new ExemptControl(287, "@onclick=\"() => confirmDelete = null\"",
                "backs out of a staged group deletion. THIS is the staged cancel the usual "
                + "exemption is written for, and the contrast with the form Cancel at 216 is the "
                + "point: this one resets one nullable that no handler reads after an await, while "
                + "216 nulls the editingGroup SaveGroup steers on. Confirm Delete beside it at 286 "
                + "is gated, so nothing can be executed from this state while the page is busy",
                KeepsItsOwnGuard: "disabled=\"@isOperating\"",
                ConditionThatKeepsItTrue:
                "nothing reads confirmDelete after an await. SaveGroup and DeleteGroup both null it "
                + "- DeleteGroup before its first await and again on success - and neither reads it "
                + "back. The moment one does, this control can null it under them"),
        ],

        // Verified by exhaustive census rather than inherited: Get-ClickGateAudit.ps1 reports
        // OtherClick 0, and the hand census of what it cannot see finds no anchor, no div/span/td
        // handler, no @onchange, no @onsubmit, no InputFile and no child component taking a
        // Disabled parameter. The nine @bind controls and the one @onkeydown input it cannot see
        // are registered under DomSyncedControls and KeyboardPaths, on the precedent of the six
        // pages before it: this list is keyed to what ClickGateSource can locate, which is @onclick
        // only, so an input registered here would fail the assertion rather than protect anything.
        NonButtonTargets = [],

        // Nine gated, and none of them keeps a non-busy clause: on this page every precondition
        // lives on the button, not on the control it reads. Each one renders server state into the
        // DOM, so the disabled attribute is the only safe refusal - refuse in the handler and the
        // backing field is unchanged, the render diff emits no correction, and the browser keeps a
        // value the server never took.
        DomSyncedControls =
        [
            new DomSyncedControl(58, "placeholder=\"Search by display name...\"", "input",
                "disabled=\"@IsBusy\""),
            new DomSyncedControl(118, "@bind=\"memberTicketNumber\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(123, "@bind=\"addMemberIdentity\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(165, "@bind=\"addOwnerIdentity\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(243, "@bind=\"formDisplayName\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(250, "@bind=\"formMailNickname\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(264, "@bind=\"formDescription\"", "textarea", "disabled=\"@IsBusy\""),
            new DomSyncedControl(269, "@bind=\"formVisibility\"", "select", "disabled=\"@IsBusy\""),
            new DomSyncedControl(277, "@bind=\"formTicketNumber\"", "input", "disabled=\"@IsBusy\""),
        ],

        UngatedDomSyncedControls =
        [
            new UngatedDomSyncedControl(258, "value=\"@editingGroup.Mail\"", "input",
                "inert, and the same shape as NamedLocations 153. It is the read-only echo of an "
                + "existing group's mail address, rendered only in the editingGroup != null branch: "
                + "no @bind, no @onchange, no handler of any kind, and a static `disabled` with no "
                + "expression at all. There is nothing here for a click to desync, and gating it on "
                + "IsBusy would change nothing an operator can see. Its value attribute reads "
                + "editingGroup, which is a registered PostAwaitLiveReads field - that obligation is "
                + "what protects the value, not this control"),
        ],

        // One, and it is the refresh every write ends with.
        ForbiddenGuardSites =
        [
            new ForbiddenGuardSite("SearchGroups", ["SaveGroup", "DeleteGroup"],
                "Both write handlers call it to re-run the last search while isOperating is still "
                + "true, so IsBusy is true at that call. A guard here makes every confirming "
                + "refresh a silent no-op: the banner says the Graph write succeeded while the "
                + "table still lists the group under its old name, or lists a group that has been "
                + "deleted, and the operator concludes it failed and retries. It is also the Search "
                + "button's own handler and the Enter path's, both of which are refused in markup "
                + "by a disabled attribute and must not be refused twice."),
        ],

        // Empty for the same structural reason as pages 1, 2, 5 and 6: no handler here is called by
        // another handler that could carry the refusal instead. SearchGroups is the one callee, and
        // it is a ForbiddenGuardSite rather than a guarded one. RunMemberOpAsync is reached only by
        // the four thunks, which raise nothing before calling it, so a guard there would be the
        // re-entrancy shape the 2026-09-17 ruling rejects.
        ExactlyOneGuard = [],

        // Eight. Seven keep a non-busy clause; the eighth keeps none and is registered for what the
        // scanner cannot see. No anchor carries the "|| " convention here, and that is a fact about
        // the page rather than a lapse: not one control on it has a title attribute, so no clause is
        // mirrored in prose and none can be satisfied by a tooltip. The assertion reads the disabled
        // attribute value in any case.
        //
        // Worth saying once for all seven clause-bearing entries: nothing on this page validates a
        // ticket against ServiceNow and no handler re-checks one, so the four ticket clauses below
        // are the ONLY enforcement of the ticket requirement anywhere in the module - the
        // NamedLocations 191 and IntuneDevices 403 shape, four times over.
        AnnotatedControls =
        [
            new AnnotatedControl(60, "@onclick=\"SearchGroups\"",
                ["string.IsNullOrWhiteSpace(searchTerm)"],
                RendersOnlyWhen:
                "always, once the authorization check has completed. The clause stops an empty "
                + "search being submitted; HandleSearchKeyDown applies the same emptiness test to "
                + "the Enter path on the box beside it, registered under KeyboardPaths"),

            new AnnotatedControl(124, "@onclick=\"AddMember\"",
                [
                    "string.IsNullOrWhiteSpace(addMemberIdentity)",
                    "string.IsNullOrWhiteSpace(memberTicketNumber)",
                ],
                RendersOnlyWhen:
                "only inside the member/owner panel, so only while selectedGroup != null && members "
                + "!= null && !showForm. Neither clause is belt and braces: RunMemberOpAsync checks "
                + "only that a group is selected, and the service takes no ticket at all"),

            new AnnotatedControl(144, "@onclick=\"() => RemoveMember(m)\"",
                ["string.IsNullOrWhiteSpace(memberTicketNumber)"],
                RendersOnlyWhen:
                "once per member row, inside the member panel, and then only while "
                + "confirmRemoveMember == m.Id. The clause is the only thing requiring a ticket "
                + "before a member is removed from an M365 group"),

            new AnnotatedControl(166, "@onclick=\"AddOwner\"",
                [
                    "string.IsNullOrWhiteSpace(addOwnerIdentity)",
                    "string.IsNullOrWhiteSpace(memberTicketNumber)",
                ],
                RendersOnlyWhen:
                "as AddMember at 124, on the owners half of the same panel"),

            new AnnotatedControl(186, "@onclick=\"() => RemoveOwner(o)\"",
                ["string.IsNullOrWhiteSpace(memberTicketNumber)"],
                RendersOnlyWhen:
                "once per owner row, only while confirmRemoveOwner == o.Id. Same sole enforcement "
                + "as 144, on the half where removing the last row leaves a group nobody owns"),

            new AnnotatedControl(216, "@onclick=\"CancelForm\"",
                [],
                RendersOnlyWhen:
                "only while showForm - it is the card header's Cancel, and it is registered with no "
                + "clause to preserve because what needs recording is the decision, not a clause. "
                + "This is the Cancel that inverts the usual exemption: CancelForm nulls "
                + "editingGroup, SaveGroup steers on editingGroup across three awaits, so leaving "
                + "this control live turned an update into a create. It is gated on IsBusy rather "
                + "than exempt, and the editingGroup capture in PostAwaitLiveReads closes the round "
                + "trip the gate cannot. Full reasoning on this entry's remarks; without an entry "
                + "here, a later reader sweeping the page for exempt Cancels would find this one "
                + "gated and helpfully 'fix' it"),

            new AnnotatedControl(286, "@onclick=\"() => DeleteGroup(editingGroup)\"",
                ["string.IsNullOrWhiteSpace(formTicketNumber)"],
                RendersOnlyWhen:
                "only while showForm && editingGroup != null && confirmDelete == editingGroup.Id. "
                + "DeleteGroup does not re-check the ticket, so this clause IS the only enforcement "
                + "of the ticket requirement on the most destructive action in the module"),

            new AnnotatedControl(295, "@onclick=\"SaveGroup\"",
                [
                    "string.IsNullOrWhiteSpace(formDisplayName)",
                    "string.IsNullOrWhiteSpace(formTicketNumber)",
                    "(editingGroup == null && string.IsNullOrWhiteSpace(formMailNickname))",
                ],
                RendersOnlyWhen:
                "always, while the form is open. Three clauses that IsBusy is OR-ed in front of and "
                + "never substituted for. The third is conditional on the branch: a mail nickname is "
                + "required to create a group and meaningless when updating one, which is why the "
                + "whole parenthesised clause is registered rather than its inner test"),
        ],

        SpinnerExpressions =
        [
            // Four, and not one of them is the predicate. Collapsing any to IsBusy shows a spinner
            // on a control that is not the one working: the Search button would spin during a
            // member write, the row spinner would appear on every row at once rather than on the
            // one being loaded, and both Add buttons and Save would spin during a search.
            "@if (isSearching)",
            "@if (isLoadingDetails && selectedGroup?.Id == g.Id)",
            "@if (isMemberOp)",
            "@if (isOperating)",
        ],

        // Thirteen. Ten entry captures and three publishes, and on this page the captures matter
        // more than the gate: the ticket defect falsification 6 names, the editingGroup read that
        // made the Cancel at 216 a retargeting hazard, and the selectedGroup read in
        // RunMemberOpAsync that could tear the circuit down.
        PostAwaitLiveReads =
        [
            new PostAwaitLiveRead("SearchGroups", "searchTerm", "term",
                SnapshotShape.CapturedAtEntry,
                "the term the operator submitted must be the term searched for and the term written "
                + "into the lookup audit row. The box binds on oninput and the browser's copy of its "
                + "disabled attribute is one round trip stale, so a keystroke landing in the gap "
                + "would run a search nobody asked for and label the audit row with it"),

            new PostAwaitLiveRead("SaveGroup", "editingGroup", "existing",
                SnapshotShape.CapturedAtEntry,
                "the sharpest defect on this page and the reason the Cancel at 216 is gated rather "
                + "than exempt. editingGroup decides Update versus Create, supplies the id the write "
                + "targets and the id in the audit row. CancelForm nulls it, EditGroup and "
                + "ShowCreateForm reassign it, and this handler's own success path nulls it - so a "
                + "live read below the awaits could send a create to Graph for a click the operator "
                + "made on Update, with the mail nickname EditGroup blanks, and audit and email it "
                + "as M365GroupManagement_Create against a name with no group id"),

            new PostAwaitLiveRead("SaveGroup", "formDisplayName", "name",
                SnapshotShape.CapturedAtEntry,
                "the display name the operator saw when the click was accepted is the one that must "
                + "be written to Graph, audited and emailed; a later keystroke must not rename the "
                + "target or desync the audit row from it"),

            new PostAwaitLiveRead("SaveGroup", "formMailNickname", "nickname",
                SnapshotShape.CapturedAtEntry,
                "the mail nickname becomes the group's permanent email prefix and cannot be changed "
                + "afterwards through this module, so a keystroke landing during the authorization "
                + "round trip would create a group at an address nobody chose"),

            new PostAwaitLiveRead("SaveGroup", "formDescription", "description",
                SnapshotShape.CapturedAtEntry,
                "the description written to Graph. Milder than the two above and captured on the "
                + "same footing: the value the click was accepted for is the value that is written"),

            new PostAwaitLiveRead("SaveGroup", "formVisibility", "visibility",
                SnapshotShape.CapturedAtEntry,
                "Private or Public, which is who can see and join the group. A bound select changed "
                + "during the authorization round trip must not change the write the operator "
                + "confirmed - and unlike a text box, a select can be moved with one keystroke"),

            new PostAwaitLiveRead("SaveGroup", "formTicketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the ticket recorded against the change, and the defect falsification 6 names for "
                + "this page. EditGroup (447) and ShowCreateForm (435) both blank the field, and "
                + "this handler read it live at four sites including two inside the catch, so the "
                + "audit row and the admin email could record a ticket that was not the one the "
                + "operator typed - or no ticket at all. Nothing re-validates it, so the record is "
                + "all there is"),

            new PostAwaitLiveRead("SaveGroup", "operationResult", "result",
                SnapshotShape.PublishedFromLocal,
                "the dismiss at 44 nulls operationResult, so reading the field after the write await "
                + "throws a NullReferenceException into this handler's own catch, which then audits "
                + "and emails a Graph create or update that SUCCEEDED as a failure and skips the "
                + "confirming refresh"),

            new PostAwaitLiveRead("DeleteGroup", "formTicketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the same obligation on the destructive handler, whose catch audits and emails the "
                + "ticket after several awaits. group needs no entry: it is a parameter, so the row "
                + "the operator confirmed is already captured at the click"),

            new PostAwaitLiveRead("DeleteGroup", "operationResult", "result",
                SnapshotShape.PublishedFromLocal,
                "same path as SaveGroup on the destructive half: an M365 group that really was "
                + "deleted reported and emailed as still present, and the table left listing it"),

            new PostAwaitLiveRead("RunMemberOpAsync", "selectedGroup", "group",
                SnapshotShape.CapturedAtEntry,
                "not a desync but a circuit teardown, the GroupManagement LoadMembers shape. This "
                + "handler read the field at four sites below its awaits - the admin email, the "
                + "member reload, the owner reload and, worst, the catch. SearchGroups nulls "
                + "selectedGroup and SelectGroup replaces it, and before this slice the Search box "
                + "and button were gated on isSearching alone so a search could be started in the "
                + "middle of a member write. The live read then threw a NullReferenceException into "
                + "the catch, whose own selectedGroup.DisplayName threw again and escaped the "
                + "handler. With no ErrorBoundary anywhere in this app the operator does not see an "
                + "error, they lose the page. The capture is also what lets the op be handed a "
                + "group rather than closing over the field"),

            new PostAwaitLiveRead("RunMemberOpAsync", "memberTicketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the ticket for every add and remove of a member or an owner. It was captured one "
                + "line BELOW the yield, which is the shape that looks correct and is not: the "
                + "capture has to sit above the first await or it is a snapshot of whatever the box "
                + "held after the operator had a round trip to keep typing into it"),

            new PostAwaitLiveRead("RunMemberOpAsync", "operationResult", "result",
                SnapshotShape.PublishedFromLocal,
                "the third of the three publishes the dismiss at 44 depends on. This one also "
                + "carries the serviced-override note into the audit row through "
                + "ProtectedPrincipalServicing.Extra, so a dismiss nulling the field mid-flight "
                + "would lose the record of who permitted a protected-principal write as well as "
                + "reporting a successful membership change as failed"),
        ],

        // One, and it is the ordering the conversion had to preserve rather than create.
        RaiseMustFollowEarlyReturn =
        [
            new RaiseAfterEarlyReturn("RunMemberOpAsync", "isMemberOp", "group == null",
                "the early return leaves no finally behind it, so a raise above the guard is never "
                + "lowered. isMemberOp is a member of the page-wide predicate, so it would not grey "
                + "the Add button - it would deaden every control on the page, permanently, the "
                + "first time a member operation was dispatched with no group selected. The guard "
                + "read 'selectedGroup == null' before this slice and now reads the captured local, "
                + "which is what puts the capture above it; the raise stayed where it was."),
        ],

        // The page's one keyboard handler, and it is worth being precise about what it was.
        // Unlike Migration and GroupManagement, this box was NOT the asymmetric 2eb8c15 shape: it
        // already carried disabled="@isSearching" and the Search button beside it carried the same
        // flag, so the two agreed. What they agreed on was too narrow - during a member write, a
        // details load or a save, BOTH were live and Enter reached SearchGroups exactly as the
        // button did, which is the defect PostAwaitLiveReads describes for RunMemberOpAsync. The
        // lesson from page 5 applies in the other direction here: widening the button to IsBusy
        // without widening the box would have MANUFACTURED the asymmetry, so both widen together.
        // The disabled attribute rather than a handler guard, on the Migration precedent: a
        // disabled input fires no key events, so one attribute closes the key path and the typing
        // path together.
        KeyboardPaths =
        [
            new KeyboardPath(58, "placeholder=\"Search by display name...\"", "input",
                "keydown", "HandleSearchKeyDown", "SearchGroups",
                KeyboardRefusal.DisabledAttribute, "disabled=\"@IsBusy\"",
                "Enter runs the group search the Search button at 60 refuses while the page is "
                + "busy. SearchGroups is not a read-only refresh: it nulls selectedGroup, members, "
                + "owners and showForm, so started from the keyboard during a member write it "
                + "throws away the selection that write is acting on - and RunMemberOpAsync used to "
                + "dereference the field it nulls, which is how this page's circuit-teardown path "
                + "was reachable from the keyboard as well as the mouse",
                GatedTwinButtonLine: 60),
        ],

        HarmlessKeyboardPaths = [],
    };

    /// <summary>
    /// Conference Rooms: tier 1, page 8 of 9, converted 2026-09-19 under
    /// docs/ClickGatingAudit-Plan.md Revision 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>TWO SCOPES, ONE PREDICATE - and the one predicate is the finding, not a shortfall.</b>
    /// Revision 1 falsification 4 named this page as one of the two where a single page-wide
    /// predicate is provably wrong, and that half is confirmed against the file: the Bulk Jobs panel
    /// is driven by BulkJobService.JobChanged, whose handler OnJobChanged calls RefreshJobs off the
    /// runner's thread and replaces activeJobs, recentJobs, finderJob, typeJob, finderRows and
    /// typeRows at moments no click produced and while IsBusy is false. The form predicate has no
    /// authority there. What the file ALSO shows, and what the plan did not predict, is that the
    /// second scope has nothing to build a predicate OUT of: its three handlers - ToggleJobDetails,
    /// CancelJob and RemoveJob - are synchronous, await nothing and raise no flag, so there is no
    /// in-flight state for a predicate to read. A predicate cannot gate work a server-side callback
    /// initiates, because OnJobChanged is not a click. Registering a second PredicateScope would
    /// mean inventing a member that is always false: it would need a flag with no setter (failing
    /// <see cref="ClickGateTests.EveryRegisteredFlagIsLoweredInAFinally"/>) or no flags at all, and
    /// either way the next reader would read a decoration as a gate. So the jobs scope is recorded
    /// as four <see cref="ExemptControl"/> entries plus this paragraph, and its refusal is identity,
    /// not busyness.
    /// </para>
    /// <para>
    /// <b>What each scope may and may not speak for.</b> IsBusy (isLoading, isCsvProcessing) speaks
    /// for the Room Finder tab, the Room Type tab and every control on them: twenty-three DOM-synced
    /// controls and the four operation buttons. It may not speak for the Bulk Jobs panel in either
    /// direction - it is never raised by anything the panel does, and it is never true because of
    /// what the panel's data is doing. The jobs scope may not be spoken for by any gate: the only
    /// thing that protects it is that each control acts on an identity it was rendered with.
    /// ToggleJobDetails and CancelJob are safe as found, because both take job.Id and neither reads
    /// a collection the callback replaces - the callback only swaps data, and a cancel or an expand
    /// addressed by id means the same thing whichever snapshot of the list is current. RemoveJob was
    /// not: it took job.Id and then looked the row back up in the live recentJobs to build its audit
    /// record. That is the shape the brief predicted - a control acting on a row the callback has
    /// since replaced - and it was reachable, because recentJobs is a windowed read as well as a
    /// replaced one, so a job finishing elsewhere can push the operator's row out of the window
    /// between the render and the click. The lookup then missed while DeleteJob still succeeded by
    /// id, and a durable record was destroyed with an audit row carrying no ticket and no old
    /// values. The fix is not a gate and could not have been: the control now takes the rendered
    /// BulkJob, so what the operator saw is what gets audited.
    /// </para>
    /// <para>
    /// <b>That last hazard is NOT enforced by this registry, and the gap is worth naming rather than
    /// papering over.</b> <see cref="PageGateEntry.PostAwaitLiveReads"/> is the field for "a value
    /// another control can change while this handler is suspended", and
    /// <see cref="ClickGateTests.NoHandlerReadsARegisteredFieldLiveAfterItsFirstAwait"/> asserts it
    /// by requiring a first await and scanning below it. RemoveJob has no await at all - it cannot,
    /// the whole point is that the staleness is between render and click rather than across a
    /// suspension - so registering the obligation there fails the assertion's own "no longer awaits
    /// anything" check instead of protecting anything. The paragraph above is therefore prose, and
    /// prose is unenforced: a future edit reverting RemoveJob to take an id and re-look-up the row
    /// would pass this whole suite. The page's own comment on the method carries the same warning,
    /// which is two unenforced copies rather than one enforced one.
    /// </para>
    /// <para>
    /// <b>Exemptions are ten of fourteen buttons, which is high and is deliberate.</b>
    /// Get-ClickGateAudit.ps1 reports 14 buttons, 10 ungated, 2 flags and ZERO stuck flags on this
    /// page as of the conversion, and its ungated line list - 48, 51, 54, 145, 332, 452, 508, 511,
    /// 572, 575 - is exactly the ten registered below, checked line for line rather than by count.
    /// Three are tab switches, two serve compile-time constants, one dismisses a banner and four are
    /// the jobs panel. The zero stuck flags is also verified rather than inherited: result is the
    /// banner model and has the shape that made operationResult a reported false positive on
    /// DhcpAuthorization and NamedLocations - a nullable raised and nulled in one awaiting method
    /// with the clear outside the finally - but it escapes the detector because this page never
    /// assigns it from a bare token. Every assignment is a `new RoomOperationResult { ... }` or an
    /// `await`, and Get-ClickGateAudit.ps1:209 counts only the bare-token form as a raise. So there
    /// is no ScannerFalsePositive to register here, and if one of those assignments is ever
    /// refactored into a local this page will grow one.
    /// </para>
    /// <para>
    /// <b>Twenty PostAwaitLiveReads, and five obligations the assertion cannot hold.</b> The census
    /// found both single-room handlers reading their whole form LIVE after several awaits - and
    /// reading it twice, once for the write and again, later, for the audit row, so the record of
    /// what was applied could disagree with what was applied. typeRemovePerms is the sharp one: it
    /// decides whether every existing calendar permission on the room is deleted first, so a
    /// checkbox toggled during the ServiceNow round trip turned a non-destructive Set Type into a
    /// destructive one nobody confirmed. Both CSV handlers re-read their ticket field LIVE in the
    /// catch, because the local was declared inside the try and out of scope there - the same defect
    /// falsification 6 names on NamedLocations, here on a field bound with @bind:event="oninput".
    /// The five that cannot be registered are SetupSingleRoom's city, building, capacity, floor and
    /// timezone. They ARE captured at entry and the handler reads only the locals; what stops the
    /// registration is that the old-value audit dictionary, which is necessarily built after the
    /// GetRoomInfoAsync await, uses those five words as its key literals, and the live-read matcher
    /// is textual - ["city"] matches a whole-word read of city. Registering them would fail on a
    /// string literal rather than on a read. Extracting that dictionary into a helper purely to move
    /// the literals out of the method body was considered and rejected on the precedent recorded on
    /// ReadOf: deforming readable code to satisfy a text matcher is the matcher's problem. So those
    /// five are correct in the page and unenforced by this entry, and this sentence is the only
    /// record of it.
    /// </para>
    /// <para>
    /// <b>No keyboard paths at all, censused by hand and not inherited.</b> The page has no
    /// @onkeydown, @onkeyup or @onkeypress, no @onsubmit, no @onchange and no &lt;form&gt; element,
    /// so there is no Enter path to manufacture by gating a button without its input and none left
    /// open by gating neither. Both lists below are empty and the forward direction of
    /// <see cref="ClickGateTests.EveryKeyboardPathIsRegisteredOrRecordedHarmless"/> is what keeps
    /// that true. The two RecipientAutocomplete instances at 70 and 260 carry the component's own
    /// Enter path, which commit 3c21270 closed inside the component for all eight call sites; this
    /// page passes Disabled and registers both, and nothing here reaches into a component.
    /// </para>
    /// </remarks>
    private static PageGateEntry ConferenceRooms => new()
    {
        Page = "ConferenceRooms.razor",
        ExpectedLineCount = 1536,

        Predicates =
        [
            new PredicateScope("IsBusy",
                ["isLoading", "isCsvProcessing"],
                AppliesWhen:
                "the FORM scope only: the Room Finder tab, the Room Type tab, and the twenty-three "
                + "DOM-synced controls and four operation buttons on them. Explicitly NOT the Bulk "
                + "Jobs panel, which is a second scope with no busy state of its own - see the "
                + "remarks on this entry, which are the only record of that scope"),
        ],

        // Empty, and verified rather than assumed: Get-ClickGateAudit.ps1 reports zero stuck flags
        // here. The remarks explain why result escapes the detector that catches the identical
        // banner field on two other converted pages, and what would make it start being reported.
        ScannerFalsePositives = [],

        // The hand census the brief asked for. The plan predicted "two parallel staged triples";
        // the triples are real - (finderPreview, finderApplied, finderCsvData) and (typePreview,
        // typeApplied, typeCsvData) - and they are six of forty. Precedent: page 5 predicted seven
        // and found twelve, page 7 predicted three and found twenty-two.
        //
        // Four groups, and the last two are this page's own: the tab/session state that would
        // deaden the page permanently, the two form sets, the staged CSV triples, and the JOBS VIEW
        // - collections and nullables that a background callback writes. That last group is the one
        // a reader of this page is most likely to get wrong, because "a bulk job is running" reads
        // like "the page is busy" and is not: folding activeJobs or finderJob into IsBusy would
        // disable the whole form for the life of any job, and would do it from a callback, so the
        // form would go dead mid-typing with no click involved.
        ExcludedFields =
        [
            new ExcludedField("authChecked", "the whole page body; 24-34 returns early while unset",
                "means 'authorization has been resolved', and it is set true once and never "
                + "cleared. In IsBusy the page would be permanently and irrecoverably dead from the "
                + "first render onward"),
            new ExcludedField("activeTab", "the tab strip at 46-58 and all three tab bodies",
                "which tab is open is a view selection, not an operation; it is always set"),
            new ExcludedField("result", "the shared result banner at 432-456",
                "the banner's model. It is non-null for the whole time an outcome is on screen, so "
                + "folding it in would deaden the page after every operation until the operator "
                + "dismissed it - and the dismiss at 452 is the one control that would still work"),
            new ExcludedField("authSnapshot", "nothing; it is stamped onto submitted jobs",
                "the off-circuit authorization capture, set once in OnInitializedAsync and never "
                + "nulled. A nullable that is only ever raised is the inverse of an in-flight flag"),
            new ExcludedField("_disposed", "nothing; it suppresses render after teardown",
                "set true in Dispose and never cleared. A mechanical 'OR the bools together' pass "
                + "would sweep it up and it means the opposite of busy"),

            // Room Finder form. Every one of these renders into a control that IsBusy already
            // disables, so folding any of them in disables the field the operator is typing into.
            new ExcludedField("roomEmail", "the RecipientAutocomplete at 70 and Setup Room at 125",
                "a form value. It is also the Setup Room button's own emptiness clause, which is "
                + "OR-ed with IsBusy and must never be folded into it"),
            new ExcludedField("city", "the input at 75", "a form value, not an operation"),
            new ExcludedField("countryOrRegion", "the input at 79", "a form value"),
            new ExcludedField("state", "the input at 85", "a form value"),
            new ExcludedField("building", "the input at 89",
                "a form value; it also decides the room list, which is a write target and not a "
                + "busy condition"),
            new ExcludedField("capacity", "the number input at 96", "a form value"),
            new ExcludedField("floor", "the input at 100", "a form value"),
            new ExcludedField("floorLabel", "the input at 104", "a form value"),
            new ExcludedField("displayDevice", "the input at 110", "a form value"),
            new ExcludedField("videoDevice", "the input at 114", "a form value"),
            new ExcludedField("timezone", "the TimezonePicker at 119", "a form value"),
            new ExcludedField("ticketNumber", "the input at 123 and Setup Room at 125",
                "the ticket the change is recorded against; the button's second emptiness clause"),

            // Room Type form. typeRemovePerms is the one to watch: it is a bool, so folding it in
            // COMPILES and looks plausible, and it would disable the entire Room Type form - Set
            // Type included - at the exact moment the operator ticks the destructive option.
            new ExcludedField("typeRoomEmail", "the RecipientAutocomplete at 260 and Set Type at 312",
                "a form value, and the Set Type button's own emptiness clause"),
            new ExcludedField("selectedRoomType", "the select at 264 and Set Type at 312",
                "which room type is chosen; also the button's second emptiness clause, and what "
                + "decides whether the Site select at 278 renders at all"),
            new ExcludedField("typeSite", "the select at 278", "a form value"),
            new ExcludedField("typeTimezone", "the TimezonePicker at 286", "a form value"),
            new ExcludedField("typeArbiter", "the input at 290", "a form value"),
            new ExcludedField("typeRemovePerms", "the checkbox at 293 and the warning at 302-307",
                "a BOOL form value that reads like a flag. It means 'the operator has asked for the "
                + "destructive variant', not 'the page is working'. In IsBusy, ticking it would "
                + "disable the whole Room Type form including the Set Type button beside it, so the "
                + "destructive variant could never be executed and the page would look broken"),
            new ExcludedField("typeTicketNumber", "the input at 310 and Set Type at 312",
                "the ticket; the button's third emptiness clause"),

            // The two staged CSV triples. These are the ExcludedFields the field exists for: the
            // Apply button and the preview table render ONLY while the triple is set, so folding
            // any of the six in disables Apply at the only moment it is ever shown - the Migration
            // regression a review caught before code, in its exact form, twice over.
            new ExcludedField("csvFinderTicket", "the input at 140 and Apply Changes at 158",
                "the bulk ticket; also Apply's own emptiness clause and the reason for the warning "
                + "at 152-157"),
            new ExcludedField("finderPreview", "the Apply block at 147-163 and the preview at 170-221",
                "holds the parsed rows awaiting approval. Both render only while Count > 0, so "
                + "folding it into IsBusy would disable Apply Changes at the only moment it renders "
                + "and no Finder CSV could ever be applied again"),
            new ExcludedField("finderApplied", "the same Apply block and preview, negated",
                "means 'this parse has already been submitted', which is a staged marker and the "
                + "opposite of in flight: it is set AFTER the job is enqueued and stays set"),
            new ExcludedField("finderCsvData", "nothing directly; it is the payload Apply submits",
                "the parsed rows themselves, index-aligned with finderPreview. A payload, not an "
                + "operation"),
            new ExcludedField("csvTypeTicket", "the input at 327 and Apply Changes at 339",
                "the bulk ticket; also Apply's own emptiness clause"),
            new ExcludedField("typePreview", "the Apply block at 334-344 and the preview at 351-402",
                "the Room Type half of the same shape as finderPreview, with the same consequence"),
            new ExcludedField("typeApplied", "the same Apply block and preview, negated",
                "as finderApplied"),
            new ExcludedField("typeCsvData", "nothing directly; it is the payload Apply submits",
                "as finderCsvData"),

            // The jobs view. Written by RefreshJobs, which OnJobChanged calls from the runner's
            // thread. Anything here in IsBusy is a gate driven by a background callback.
            new ExcludedField("activeJobs", "the Bulk Jobs tab badge at 55 and the table at 488-557",
                "server-side jobs that are queued or running. 'A bulk job is running' is NOT 'this "
                + "circuit is busy': the job survives the tab. Folding activeJobs.Count into IsBusy "
                + "would disable the entire form for the life of every job, would do it from a "
                + "callback rather than from a click, and would grey the form out under an operator "
                + "mid-keystroke"),
            new ExcludedField("recentJobs", "the finished-job rows at 558-622",
                "finished jobs, kept for 30 days. Same mistake as activeJobs and more obviously "
                + "wrong - these jobs are over"),
            new ExcludedField("finderJob", "the live results card at 224-247",
                "the active-or-most-recent Finder job. A nullable written by a background callback, "
                + "which is the most convincing wrong answer on this page: 'finderJob is not null' "
                + "reads exactly like an in-flight signal and is a view selection"),
            new ExcludedField("typeJob", "the live results card at 405-428", "as finderJob"),
            new ExcludedField("finderRows", "the same card at 224-247",
                "per-row results streamed from the runner; replaced wholesale on every job event"),
            new ExcludedField("typeRows", "the same card at 405-428", "as finderRows"),
            new ExcludedField("detailsJobId", "the expanded detail row at 516-556 and 581-621",
                "which job's rows are expanded. A nullable raised and nulled in one method, which "
                + "is most of the stuck-flag shape, and it is a selection. In IsBusy the page would "
                + "die whenever a detail row was open - including the Details button itself, so it "
                + "could never be closed again"),
            new ExcludedField("detailsRows", "the same expanded detail row",
                "the fetched rows for the expanded job; replaced on expand and on collapse"),
        ],

        // Ten of fourteen, matching Get-ClickGateAudit.ps1's ungated line list exactly. Four
        // categories: three tab switches, two constant downloads, one banner dismiss, and the four
        // Bulk Jobs controls that belong to the second scope.
        ExemptControls =
        [
            new ExemptControl(48, "@onclick='() => activeTab = \"finder\"'",
                "a view switch. Every control on the destination tab carries the gate, so nothing "
                + "can be executed from the new tab while the page is busy; gating it would instead "
                + "trap the operator on whichever tab a long CSV parse started from, unable to "
                + "watch the other one's preview or results",
                ConditionThatKeepsItTrue:
                "the handler stays a bare assignment to activeTab. The moment a tab switch loads "
                + "anything it is an operation and belongs behind IsBusy"),

            new ExemptControl(51, "@onclick='() => activeTab = \"type\"'",
                "the same view switch on the Room Type tab, on the same reasoning",
                ConditionThatKeepsItTrue:
                "as 48: the handler stays a bare assignment to activeTab"),

            new ExemptControl(54, "@onclick='() => activeTab = \"jobs\"'",
                "the same view switch, and the one that must NOT be gated even if the other two "
                + "ever are. Cancel at 511 is the only way to stop a running server-side job, and "
                + "it lives behind this tab: gating the tab on the form predicate would strand a "
                + "runaway bulk job - one that keeps writing to Exchange after the browser is "
                + "closed - with no way to cancel it for the whole of an unrelated CSV parse. The "
                + "registry has no BecauseOfControl field to tie those two together, so the tie is "
                + "this sentence plus the exemption registered at 511: if Cancel is removed or "
                + "moved, EveryRegisteredExemptionStillPointsAtARealControl fails and someone "
                + "re-reads this",
                ConditionThatKeepsItTrue:
                "Cancel stays behind this tab and stays clickable. If job cancellation ever moves "
                + "somewhere reachable without this tab, re-derive the exemption rather than "
                + "keeping it"),

            new ExemptControl(145, "@onclick=\"DownloadFinderSampleCsv\"",
                "serves a compile-time constant. The handler builds a string literal, base64s it "
                + "and hands it to the downloadFile interop; it reads no page state and touches no "
                + "in-flight operation, so a click during a parse is definitively executed",
                ConditionThatKeepsItTrue:
                "DownloadFinderSampleCsv must keep building its content from string literals only. "
                + "The moment it reads finderPreview, or calls into Exchange to build a real "
                + "sample, it is an operation and belongs behind IsBusy"),

            new ExemptControl(332, "@onclick=\"DownloadTypeSampleCsv\"",
                "the Room Type half of the same constant download",
                ConditionThatKeepsItTrue:
                "as 145: DownloadTypeSampleCsv must keep building its content from string literals"),

            new ExemptControl(452, "@onclick=\"() => result = null\"",
                "dismisses the shared result banner. Gating it on IsBusy would trap the previous "
                + "operation's message on screen for the whole of the next one, the same reason "
                + "Migration's dismiss at 442 and DhcpAuthorization's at 40 are exempt. No handler "
                + "on this page READS result - every one of them only assigns it, from a `new "
                + "RoomOperationResult` or from an await - so a dismiss landing mid-flight cannot "
                + "produce the NullReferenceException that made DhcpAuthorization's dismiss a live "
                + "defect, and this exemption needs no prerequisite",
                ConditionThatKeepsItTrue:
                "no handler starts reading result back. The moment one does - result.Success after "
                + "an await is the shape - this control can null it under them, and the exemption "
                + "needs a PostAwaitLiveRead and a PrerequisiteBeforeExemptionHolds before it holds "
                + "again"),

            // The jobs scope. Line is the only key for 508 and 572: the two Details buttons were
            // byte-identical, same label and same handler in two different loops, which is exactly
            // the case ExemptControl.Snippet exists for and exactly the case a snippet could not
            // disambiguate. They now carry distinct titles, so the snippet is a real key again.
            new ExemptControl(508, "title=\"Show or hide the rows of this active job\"",
                "expands or collapses one ACTIVE job's rows. A view toggle keyed by job.Id: it "
                + "reads no collection the background callback replaces, and an id means the same "
                + "job whichever snapshot of the list is current. It must stay clickable during a "
                + "form operation - submitting a CSV is precisely when the operator needs to watch "
                + "the job it created",
                ConditionThatKeepsItTrue:
                "ToggleJobDetails keeps taking an id and keeps reading nothing but BulkJobs.GetRows"),

            new ExemptControl(511, "@onclick=\"() => CancelJob(job.Id)\"",
                "cancels or dequeues a running server-side job, and it belongs to the jobs scope, "
                + "not the form scope. The form predicate has no authority over it in either "
                + "direction: nothing the panel does raises IsBusy, and nothing IsBusy describes is "
                + "happening to this job. Gating it on IsBusy would make a runaway bulk job - which "
                + "keeps writing to Exchange after the browser is closed - uncancellable for the "
                + "whole of an unrelated CSV parse. CancelJob takes job.Id and calls straight "
                + "through, so the callback swapping the list under it changes nothing",
                ConditionThatKeepsItTrue:
                "CancelJob keeps taking an id and keeps making no read of activeJobs. If it ever "
                + "needs the row, it must take the rendered BulkJob the way RemoveJob now does"),

            new ExemptControl(572, "title=\"Show or hide the rows of this finished job\"",
                "the FINISHED-job half of the same view toggle, in the second loop. Textually "
                + "identical to 508 until the titles were added; see the note above this group",
                ConditionThatKeepsItTrue:
                "as 508"),

            new ExemptControl(575, "@onclick=\"() => RemoveJob(job)\"",
                "hard-deletes one finished job record, and it is the control that taught this page "
                + "its lesson. It is exempt from IsBusy for the jobs-scope reason - tidying the job "
                + "list has nothing to do with a form operation, and gating it would only stop the "
                + "operator doing housekeeping while a CSV parses - but exempt is not the same as "
                + "safe, and this one was not safe. It passed job.Id and then looked the row back "
                + "up in the LIVE recentJobs to build its audit record; recentJobs is both replaced "
                + "by the background callback and a windowed read, so a job finishing elsewhere "
                + "could push the operator's row out from under the click. The lookup missed, the "
                + "delete went through by id anyway, and a durable record was destroyed with an "
                + "audit row carrying no ticket and no old values. It now takes the rendered "
                + "BulkJob. No gate could have closed that - the staleness is between render and "
                + "click - and no assertion in this suite holds it either; see the remarks",
                ConditionThatKeepsItTrue:
                "RemoveJob keeps taking the rendered BulkJob and never looks a row up in "
                + "recentJobs, activeJobs or any other collection RefreshJobs replaces"),
        ],

        // Verified by hand census against the file, not inherited: every one of the fourteen
        // @onclick attributes on this page is on a <button>. No anchor, no div/span/td handler, no
        // @onchange, no @onsubmit and no <form>. Get-ClickGateAudit.ps1 agrees (OtherClick: 0).
        NonButtonTargets = [],

        // Twenty-three, censused by hand and cross-checked against the scanner's two-flag report.
        // All twenty-three were gated on the bare isLoading flag before this conversion and now
        // name the predicate. The four child components are the ones NonButtonTargets could never
        // have covered - ClickGateSource locates @onclick only - and two of them, the
        // RecipientAutocomplete pair, are the instances the page-3 entry predicted would land here.
        DomSyncedControls =
        [
            new DomSyncedControl(70, "@bind-Value=\"roomEmail\"", "RecipientAutocomplete",
                "Disabled=\"@IsBusy\""),
            new DomSyncedControl(75, "@bind=\"city\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(79, "@bind=\"countryOrRegion\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(85, "@bind=\"state\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(89, "@bind=\"building\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(96, "@bind=\"capacity\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(100, "@bind=\"floor\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(104, "@bind=\"floorLabel\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(110, "@bind=\"displayDevice\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(114, "@bind=\"videoDevice\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(119, "@bind-Value=\"timezone\"", "TimezonePicker",
                "Disabled=\"@IsBusy\""),
            new DomSyncedControl(123, "@bind=\"ticketNumber\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(140, "@bind=\"csvFinderTicket\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(144, "OnChange=\"HandleFinderCsvUpload\"", "InputFile",
                "disabled=\"@IsBusy\""),
            new DomSyncedControl(260, "@bind-Value=\"typeRoomEmail\"", "RecipientAutocomplete",
                "Disabled=\"@IsBusy\""),
            new DomSyncedControl(264, "@bind=\"selectedRoomType\"", "select", "disabled=\"@IsBusy\""),
            new DomSyncedControl(278, "@bind=\"typeSite\"", "select", "disabled=\"@IsBusy\""),
            new DomSyncedControl(286, "@bind-Value=\"typeTimezone\"", "TimezonePicker",
                "Disabled=\"@IsBusy\""),
            new DomSyncedControl(290, "@bind=\"typeArbiter\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(293, "@bind=\"typeRemovePerms\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(310, "@bind=\"typeTicketNumber\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(327, "@bind=\"csvTypeTicket\"", "input", "disabled=\"@IsBusy\""),
            new DomSyncedControl(331, "OnChange=\"HandleTypeCsvUpload\"", "InputFile",
                "disabled=\"@IsBusy\""),
        ],

        UngatedDomSyncedControls = [],

        // Six, and the first is the one that matters: RefreshJobs is called by both CSV Apply
        // handlers while they are still busy, AND by a background callback where a busy flag is
        // meaningless. The other five are the shared helpers every busy handler calls; each is
        // listed because a guard there is silent by construction - the caller reports success and
        // the side effect simply did not happen.
        ForbiddenGuardSites =
        [
            new ForbiddenGuardSite("RefreshJobs", ["ApplyFinderCsv", "ApplyTypeCsv", "RemoveJob"],
                "Both Apply handlers call it immediately after enqueueing, while isLoading and "
                + "isCsvProcessing are still true, so IsBusy is true at that call. A guard here "
                + "makes the confirming refresh a silent no-op: the banner says the job was "
                + "submitted while the Bulk Jobs tab shows no such job, and the operator submits it "
                + "again. Worse, OnJobChanged calls this from the runner's thread on every job "
                + "event, where IsBusy describes nothing relevant - a guard would freeze the live "
                + "jobs view for the whole of any form operation and leave a running job's progress "
                + "silently stale."),

            new ForbiddenGuardSite("ReauthorizeAsync",
                ["SetupSingleRoom", "ApplyFinderCsv", "SetSingleRoomType", "ApplyTypeCsv"],
                "Every mutating handler calls it after raising isLoading. It returns bool and the "
                + "callers treat false as a denial, so a busy guard here would make every operation "
                + "on the page report 'Authorization denied.' and stop - a refusal wearing the "
                + "wrong reason, which is worse than no refusal because the operator escalates it."),

            new ForbiddenGuardSite("EnqueueConferenceRoomJob", ["ApplyFinderCsv", "ApplyTypeCsv"],
                "Called by both Apply handlers while busy. It is the only thing that submits a "
                + "bulk job; a guard here would leave both handlers reporting 'Submitted N room(s) "
                + "as a background job' with nothing enqueued."),

            new ForbiddenGuardSite("AuditFinderAction",
                ["SetupSingleRoom", "HandleFinderCsvUpload", "ApplyFinderCsv"],
                "Every caller is busy at the call. A guard here silently drops the audit record "
                + "for every Room Finder action, including the failures - and the Constitution "
                + "makes the audit non-negotiable, so a no-op that reports nothing is the worst "
                + "available outcome."),

            new ForbiddenGuardSite("AuditTypeAction",
                ["SetSingleRoomType", "HandleTypeCsvUpload", "ApplyTypeCsv"],
                "As AuditFinderAction, on the Room Type half."),

            new ForbiddenGuardSite("NotifyRoomAdminAsync", ["SetupSingleRoom", "SetSingleRoomType"],
                "Both single-room handlers call it while busy, on both the success and the failure "
                + "path. It is the mandatory admin notification (Constitution, Notifications); a "
                + "guard here would silently stop every room write being notified while the "
                + "handler still reported success."),
        ],

        // No handler on this page carries a busy guard, and none should: every refusal is in
        // markup, on a control the disabled attribute reaches. Nothing to pin here.
        ExactlyOneGuard = [],

        // The four gated buttons, each with the emptiness clauses IsBusy is OR-ed in front of and
        // must never replace. Two of the six clauses are the SOLE enforcement of their condition -
        // neither SetupSingleRoom nor SetSingleRoomType checks its room address anywhere - which is
        // the IntuneDevices 403 shape, and the reason a mechanical rewrite to the bare predicate
        // would be a live defect rather than a tidy-up.
        AnnotatedControls =
        [
            new AnnotatedControl(125, "@onclick=\"SetupSingleRoom\"",
                [
                    "string.IsNullOrWhiteSpace(roomEmail)",
                    "string.IsNullOrWhiteSpace(ticketNumber)",
                ],
                RendersOnlyWhen:
                "always, while the Room Finder tab is open. The roomEmail clause IS the only "
                + "enforcement: SetupSingleRoom never checks the address, it just trims it and "
                + "hands it to the protected-principal gate and then to Set-Place. The ticket "
                + "clause is belt and braces - ServiceNow validation would reject an empty ticket - "
                + "but it is still a precondition and not a busy condition"),

            new AnnotatedControl(158, "@onclick=\"ApplyFinderCsv\"",
                ["string.IsNullOrWhiteSpace(csvFinderTicket)"],
                RendersOnlyWhen:
                "only while finderPreview.Count > 0 && !finderApplied - the staged triple. That "
                + "reachability is the whole reason finderPreview and finderApplied are registered "
                + "ExcludedFields: fold either into IsBusy and this button is disabled at the only "
                + "moment it renders. The clause is re-checked by ServiceNow validation, and the "
                + "warning panel at 152-157 explains it to the operator in prose"),

            new AnnotatedControl(312, "@onclick=\"SetSingleRoomType\"",
                [
                    "string.IsNullOrWhiteSpace(typeRoomEmail)",
                    "string.IsNullOrWhiteSpace(selectedRoomType)",
                    "string.IsNullOrWhiteSpace(typeTicketNumber)",
                ],
                RendersOnlyWhen:
                "always, while the Room Type tab is open. typeRoomEmail is the sole enforcement, as "
                + "roomEmail is at 125. selectedRoomType is re-checked by the Enum.TryParse below "
                + "the awaits, so that one is belt and braces"),

            new AnnotatedControl(339, "@onclick=\"ApplyTypeCsv\"",
                ["string.IsNullOrWhiteSpace(csvTypeTicket)"],
                RendersOnlyWhen:
                "only while typePreview.Count > 0 && !typeApplied - the second staged triple, same "
                + "shape as 158"),
        ],

        SpinnerExpressions =
        [
            // Two distinct conditions, each written twice (127 and 314; 160 and 341), one pair per
            // tab. Neither is the predicate and neither may be collapsed into it: the single-room
            // spinner suppresses itself during a CSV apply because the Apply button beside it is
            // already showing one, and the CSV spinner is on isCsvProcessing alone so a single-room
            // setup does not put a spinner on Apply Changes. Containment cannot tell the two copies
            // apart, so deleting exactly one of a pair passes this - the line numbers here are the
            // only record that there are two of each.
            "@if (isLoading && !isCsvProcessing)",
            "@if (isCsvProcessing)",
        ],

        // Twenty obligations. Falsification 6 for this page, and the larger half of the fix: the
        // gate narrows these windows and only the locals close them. Five more exist in the page
        // and cannot be written here - SetupSingleRoom's city, building, capacity, floor and
        // timezone - because the old-value audit dictionary uses those five words as key literals
        // after the await and the matcher is textual. See the remarks; that is prose and unenforced.
        PostAwaitLiveReads =
        [
            new PostAwaitLiveRead("SetupSingleRoom", "roomEmail", "email",
                SnapshotShape.CapturedAtEntry,
                "the room whose metadata is written, audited and emailed. It was already captured, "
                + "but BELOW await Task.Yield(), which is a real yield back to the message loop - "
                + "so a suggestion click or a keystroke queued behind the click could retarget the "
                + "Set-Place write before the capture ran"),

            new PostAwaitLiveRead("SetupSingleRoom", "ticketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the ticket validated against ServiceNow must be the ticket recorded in the audit "
                + "row and the admin email. Captured above the yield for the same reason as "
                + "roomEmail"),

            new PostAwaitLiveRead("SetupSingleRoom", "countryOrRegion", "country",
                SnapshotShape.CapturedAtEntry,
                "written to the room's Place metadata. Read live it was read TWICE - once for the "
                + "write and again, later, for the audit row's new values - so the record of what "
                + "was applied could disagree with what was applied"),

            new PostAwaitLiveRead("SetupSingleRoom", "state", "stateName",
                SnapshotShape.CapturedAtEntry,
                "as countryOrRegion: written to Place metadata and recorded in the same audit row"),

            new PostAwaitLiveRead("SetupSingleRoom", "floorLabel", "floorText",
                SnapshotShape.CapturedAtEntry,
                "as countryOrRegion"),

            new PostAwaitLiveRead("SetupSingleRoom", "displayDevice", "display",
                SnapshotShape.CapturedAtEntry,
                "as countryOrRegion"),

            new PostAwaitLiveRead("SetupSingleRoom", "videoDevice", "video",
                SnapshotShape.CapturedAtEntry,
                "as countryOrRegion"),

            new PostAwaitLiveRead("SetSingleRoomType", "typeRoomEmail", "email",
                SnapshotShape.CapturedAtEntry,
                "the room whose type, mail tip, booking policy and calendar permissions are "
                + "rewritten. Captured above await Task.Yield() rather than below it"),

            new PostAwaitLiveRead("SetSingleRoomType", "typeTicketNumber", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the ticket validated must be the ticket recorded"),

            new PostAwaitLiveRead("SetSingleRoomType", "selectedRoomType", "typeName",
                SnapshotShape.CapturedAtEntry,
                "which template is applied. It was read live three times after the awaits - to "
                + "parse the enum, to decide whether the Site value is used, and again for the "
                + "audit row - so a select changed during the ServiceNow round trip could have the "
                + "page parse one type, site-qualify a second and record a third"),

            new PostAwaitLiveRead("SetSingleRoomType", "typeTimezone", "tz",
                SnapshotShape.CapturedAtEntry,
                "the room's working-hours timezone, written and then audited from two separate "
                + "live reads"),

            new PostAwaitLiveRead("SetSingleRoomType", "typeSite", "site",
                SnapshotShape.CapturedAtEntry,
                "which site's admin group arbitrates a Restricted room. Read live it could be taken "
                + "from a select the operator changed after the click was accepted"),

            new PostAwaitLiveRead("SetSingleRoomType", "typeArbiter", "arbiter",
                SnapshotShape.CapturedAtEntry,
                "the group that approves bookings. A keystroke landing in the round trip would "
                + "hand approval rights to a different group than the one on screen"),

            new PostAwaitLiveRead("SetSingleRoomType", "typeRemovePerms", "removePerms",
                SnapshotShape.CapturedAtEntry,
                "THE destructive one on this page. It decides whether every existing calendar "
                + "permission on the room is deleted before the new ones are applied - the page "
                + "renders a red warning for it at 302-307. Read live after four awaits, a checkbox "
                + "toggled during the ServiceNow round trip turned a non-destructive Set Type into "
                + "a destructive one the operator never confirmed, or the reverse; and the audit "
                + "row read it a fifth time, after the write, so it could record the answer that "
                + "was not used"),

            new PostAwaitLiveRead("ApplyFinderCsv", "csvFinderTicket", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the bulk ticket, and the defect falsification 6 names by name. The catch re-read "
                + "the LIVE field - csvFinderTicket.Trim() - because the old local was declared "
                + "inside the try and out of scope there, so on any failure the audit row recorded "
                + "whatever was in an oninput-bound box at that moment rather than the ticket "
                + "validated against ServiceNow. The capture is now above the try as well as above "
                + "the first await, which is what puts it in scope for the catch"),

            new PostAwaitLiveRead("ApplyFinderCsv", "finderCsvData", "csvRows",
                SnapshotShape.CapturedAtEntry,
                "the parsed rows this click submits as a durable server-side job. "
                + "HandleFinderCsvUpload REPLACES the list (finderCsvData = new()) at its own "
                + "entry, so a file chosen in the round trip before the InputFile's disabled "
                + "attribute reaches the browser would have this handler enqueue rows from a parse "
                + "the operator never previewed - or, mid-parse, an emptied list reported as 'No "
                + "resolvable rooms to submit'"),

            new PostAwaitLiveRead("ApplyFinderCsv", "finderPreview", "previewRows",
                SnapshotShape.CapturedAtEntry,
                "the index-aligned approval list that decides WHICH of those rows are submitted. It "
                + "is replaced by the same handler on the same path, and the two lists must be the "
                + "matched pair the operator approved - a mismatch submits rows by index against "
                + "another parse's resolution flags"),

            new PostAwaitLiveRead("ApplyTypeCsv", "csvTypeTicket", "ticket",
                SnapshotShape.CapturedAtEntry,
                "the same live-read-in-the-catch defect on the Room Type bulk path"),

            new PostAwaitLiveRead("ApplyTypeCsv", "typeCsvData", "csvRows",
                SnapshotShape.CapturedAtEntry,
                "as finderCsvData, and this is the payload that rewrites calendar permissions in "
                + "bulk"),

            new PostAwaitLiveRead("ApplyTypeCsv", "typePreview", "previewRows",
                SnapshotShape.CapturedAtEntry,
                "as finderPreview"),
        ],

        // No raise on this page sits near an early return: all six busy handlers raise as their
        // first statement above the try, and none has a guard above the raise.
        RaiseMustFollowEarlyReturn = [],

        // Censused by hand against the file: this page has no @onkeydown, @onkeyup or @onkeypress,
        // no @onsubmit, no @onchange and no <form>, so there is no key press that reaches an
        // operation and none that reaches nothing either. Both lists are empty, and the forward
        // direction of EveryKeyboardPathIsRegisteredOrRecordedHarmless is what keeps that true: a
        // handler added here later fails the suite and names itself.
        KeyboardPaths = [],

        HarmlessKeyboardPaths = [],
    };

    /// <summary>
    /// Self-Service Groups: tier 1, page 9 of 9, converted 2026-09-19 under
    /// docs/ClickGatingAudit-Plan.md Revision 1 (owner-approved scope, .agents/decisions.md
    /// 2026-09-18, option A). The only page whose adversarial verdict came back "unsound" rather
    /// than "sound with corrections", and the reason it was sequenced last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The view-switch dilemma, and why both obvious answers are wrong.</b> The page renders two
    /// mutually exclusive views - browse while <c>selected == null</c> (31-186), manage otherwise
    /// (189-451) - with disjoint in-flight flag sets. Gate "Back to groups"
    /// and the operator is trapped in the manage view: <c>ChangeMember</c>,
    /// <c>RemoveListedMember</c>, <c>RemoveSelectedAsync</c> and <c>AddResolvedAsync</c> all end
    /// with <c>await LoadMembers()</c>, which raises isLoadingMembers, so EVERY successful
    /// membership change closes with a window the operator cannot leave - and a bulk remove of
    /// fifty members holds that window open for the whole batch, one authorization round trip,
    /// write, read-back, audit and email per row. Exempt it under a single page-wide predicate and
    /// the manage flags leak into a browse view that renders no spinner for any of them: Load and
    /// Search grey out with nothing on screen saying why.
    /// </para>
    /// <para>
    /// <b>The third answer, in two halves.</b> First, the flags are scoped so there is nothing to
    /// leak: <c>IsBrowseBusy</c> (isLoading, isSearching) and <c>IsManageBusy</c> (isChanging,
    /// isLoadingMembers, isResolving) are disjoint, so no browse control consults a manage flag and
    /// no manage control consults a browse flag. Each scope has real flags and real controls, which
    /// is the bar ConferenceRooms set: neither is an always-false member invented to make a list
    /// look symmetrical. Second, the exit is exempt and the ENTRANCE is gated. "Back to groups"
    /// carries no gate at all, and the two "Manage members" buttons (128 and 179) - the only way
    /// back into the manage view - are gated on IsManageBusy. A manage flag that survives the
    /// switch therefore greys exactly one control, the door back into the scope that owns it, and
    /// a browse-view banner says so, because the browse view has no spinner to explain it.
    /// </para>
    /// <para>
    /// <b>What makes the exemption safe is a snapshot, not a flag reset.</b> The tempting fix -
    /// having BackToGroups lower isChanging, isLoadingMembers and isResolving - is the hazard the
    /// brief warned about and was rejected: those three are lowered in the finallys of operations
    /// still in flight, so clearing them from outside means the in-flight operation's own finally
    /// later lowers a flag a NEW view has raised. What actually makes leaving mid-flight safe is
    /// that no handler outliving the switch reads <c>selected</c> for anything but the
    /// <c>ReferenceEquals(selected, group)</c> still-looking-at-it test. That was true of the four
    /// mutating handlers and NOT of LoadMembers, which is the live defect this slice found: its
    /// catch logged <c>selected.Name</c> after the await, so a Back click landing while a member
    /// read failed raised a NullReferenceException INSIDE a catch block. With no ErrorBoundary
    /// anywhere in the app that escapes the handler and tears the circuit down. LoadMembers now
    /// snapshots its group at entry, and the obligation is registered below.
    /// </para>
    /// <para>
    /// <b>The tie between the exemption and the snapshot is prose, and that is a limitation, not a
    /// choice.</b> <see cref="ExemptControl.PrerequisiteBeforeExemptionHolds"/> is exactly the
    /// field this wants - the BackToGroups exemption is safe only because
    /// <c>PostAwaitLiveRead("LoadMembers", "selected", "group")</c> holds - but
    /// <see cref="ClickGateTests.EveryExemptionPrerequisiteIsEnforcedAndNotJustDescribed"/> reads
    /// the tied field out of the exemption's own SNIPPET, via a regex that only matches an inline
    /// <c>() =&gt; field = null</c> handler. BackToGroups is a method reference, so the snippet
    /// names no field and setting the prerequisite would fail the assertion rather than enforce
    /// anything. The obligation is registered on its own, so
    /// <see cref="ClickGateTests.NoHandlerReadsARegisteredFieldLiveAfterItsFirstAwait"/> holds the
    /// snapshot; what is unenforced is only the LINK saying the exemption depends on it, and this
    /// paragraph plus ConditionThatKeepsItTrue at 205 is the whole of that record.
    /// </para>
    /// <para>
    /// <b>Thirty-one staged fields, censused by hand.</b> Six are the two view-scoped predicates'
    /// members; twenty-two are registered as ExcludedFields below. Four shapes on this page would
    /// each have compiled and looked plausible: <c>loaded</c> and <c>authChecked</c> are set once
    /// and never cleared, so either would deaden its scope permanently; <c>showBulkAdd</c> and
    /// <c>showRemoveConfirm</c> are plain bools that RENDER the controls they would disable, so
    /// folding either in disables a destructive confirmation at the only moment it is ever shown;
    /// <c>pendingOp</c> is raised and nulled in the same four finallys as isChanging and so reads
    /// exactly like a second in-flight flag, when it is a which-spinner selector; and
    /// <c>pendingGroupRemoval</c> is the nullable staged-confirmation shape that would make
    /// "Remove group" unclickable at the only moment it renders. Precedent for not trusting the
    /// prediction: page 5 predicted seven and found twelve, page 7 predicted three and found
    /// twenty-two, page 8 predicted "two triples" and found forty.
    /// </para>
    /// <para>
    /// <b>One keyboard path, and it was already correct.</b> The search box at 148 routes
    /// <c>@onkeydown</c> to OnSearchKeyDown, which reaches SearchGroup - the 2eb8c15 shape exactly,
    /// and the box already carried a disabled attribute, so widening it from isSearching to
    /// IsBrowseBusy closes the typing path and the Enter path together. It is registered as a
    /// KeyboardPath with its gated twin at 151, so neither half can be narrowed alone. Nothing else
    /// on the page binds a key event: no other @onkeydown, no @onkeyup, no @onkeypress, no
    /// &lt;form&gt; and no @onsubmit, censused by hand rather than inherited. The
    /// ADIdentityAutocomplete at 226 carries the component's own Enter path, closed inside the
    /// component by 3c21270 for all of its call sites; this page passes Disabled and registers the
    /// call site, and reaches into nothing.
    /// </para>
    /// <para>
    /// <b>Two post-await live reads fixed, two deliberate ones kept, and three that cannot be
    /// registered.</b> Fixed: LoadMembers' <c>selected</c> (above) and ResolvePasteAsync's
    /// <c>pasteText</c>, which was parsed AFTER await Task.Yield() on a textarea bound with
    /// <c>@bind:event="oninput"</c> - a keystroke landing in the round trip before the disabled
    /// attribute reached the browser had the operator approve a preview of text that was never on
    /// screen when the click was accepted. Kept deliberately: the
    /// <c>ReferenceEquals(selected, group)</c> tests in all four mutating handlers, which MUST read
    /// the field live - that is the whole point of the test - and would fail this suite if
    /// registered. Cannot be registered, and so unenforced: RemoveSelectedAsync's
    /// <c>selectedGuids</c> and AddResolvedAsync's <c>resolution</c> are already snapshotted
    /// through <c>SelectedMembers()</c> and a Where, and the CapturedAtEntry matcher requires the
    /// capture line to name the live field textually, which <c>var rows = SelectedMembers();</c>
    /// does not (the resolution half does, and IS registered). And <c>changeResult</c> is read
    /// after an await in ChangeMember and RemoveListedMember, at the still-looking-at-it test - but
    /// unreachably so: no await separates the assignment from the read, so the browser never sees
    /// the banner, and its dismiss button cannot be clicked, in that window. That is why the
    /// dismiss at 300 needs no prerequisite; the day an await appears between them it does.
    /// </para>
    /// <para>
    /// <b>Not fixed, and not a gating problem.</b> Re-entering the manage view for a DIFFERENT
    /// group while a previous group's operation is still in flight lets that operation publish its
    /// <c>changeResult</c> banner and its <c>bulkOutcome</c> table into the new group's view - the
    /// text names the old group, so it is misleading rather than wrong, and the gate on 128/179
    /// narrows the window to one round trip but cannot close it. Closing it means stamping the
    /// group onto the result and discarding a result whose group is no longer selected, which is a
    /// change to what those two fields are rather than to how a control refuses a click, and it is
    /// out of this slice's scope. Recorded here rather than left to be rediscovered.
    /// </para>
    /// <para>
    /// <b>The standing exemption is untouched.</b> Self-Service Groups does not consult Protected
    /// Group Targets (owner ruling 2026-08-31, .agents/decisions.md, carried in the Constitution);
    /// its member-protection check stays, inside the service. Nothing in this conversion goes near
    /// either.
    /// </para>
    /// </remarks>
    private static PageGateEntry SelfServiceGroups => new()
    {
        Page = "SelfServiceGroups.razor",
        ExpectedLineCount = 1155,

        // Two, and the list field exists for exactly this. Both scopes have real flags and real
        // controls; see the remarks for why a single page-wide predicate cannot work here and why
        // the control that crosses between them is gated by neither.
        Predicates =
        [
            new PredicateScope("IsBrowseBusy",
                ["isLoading", "isSearching"],
                AppliesWhen:
                "the BROWSE view only (selected == null, markup 31-186): the owned-groups load, the "
                + "in-list filter and its Clear, and the single-group search box and button. Not "
                + "the two Manage members buttons at 128 and 179, which open the other view "
                + "and are gated on IsManageBusy instead"),

            new PredicateScope("IsManageBusy",
                ["isChanging", "isLoadingMembers", "isResolving"],
                AppliesWhen:
                "the MANAGE view (selected != null, markup 189-451), plus the two Manage members "
                + "buttons that open it. Explicitly NOT Back to groups at 205, which "
                + "is the exit and must stay clickable - see the remarks on this entry, which are "
                + "the only record of that reasoning"),
        ],

        // The hand census. Twenty-two, over four groups: the session/view state that would deaden a
        // scope permanently, the two staged confirmations, the two banner models, and the form and
        // selection values every gated control already refuses typing into.
        ExcludedFields =
        [
            new ExcludedField("authChecked", "the whole page body; 18-28 returns early while unset",
                "means 'authorization has been resolved'. Set true once in OnInitializedAsync and "
                + "never cleared, so in either predicate the page would be permanently and "
                + "irrecoverably dead from the first render onward"),
            new ExcludedField("selected", "the view switch itself at 31 and 189",
                "THE view selector, and the most dangerous wrong answer on this page. 'selected is "
                + "not null' reads like state that is happening; it means 'the manage view is the "
                + "one on screen'. In IsManageBusy every control in the manage view would be "
                + "disabled at the only moment any of them renders"),
            new ExcludedField("loaded", "the owned-groups card at 77-138",
                "means 'the last load succeeded'. It renders the list, so in IsBrowseBusy the "
                + "browse view would go dead the moment a load succeeded and stay dead"),
            new ExcludedField("loadError", "the failure alert at 72-76",
                "the load-failure banner's model. It is non-null for as long as the error is on "
                + "screen, which is until the next load - so it would deaden the Load button that "
                + "is the only way to clear it"),
            new ExcludedField("searchMessage", "the not-found alert at 158-161",
                "as loadError, on the search half"),
            new ExcludedField("searchResult", "the result row at 162-184, and Manage members at 179",
                "the found group. The Manage members button renders ONLY while it is set, so "
                + "folding it in disables that button at the only moment it is ever shown"),
            new ExcludedField("changeResult", "the shared result banner at 296-302",
                "the banner's model, non-null for the whole time an outcome is on screen. In "
                + "IsManageBusy the manage view would be dead after every operation until the "
                + "operator dismissed it - and the dismiss at 300 is the one control that would "
                + "still work"),
            new ExcludedField("bulkOutcome", "the per-row outcome table at 356-377",
                "as changeResult, for the two batch paths"),
            new ExcludedField("memberLoadError", "the member-read failure alert at 312-315",
                "as loadError, on the member list"),
            new ExcludedField("pendingOp", "the four spinners at 234, 239, 266 and 343",
                "a nullable raised and nulled in the same four finallys as isChanging, which is the "
                + "whole of the in-flight shape and is why a structural scan reports it. It is a "
                + "WHICH-SPINNER selector: isChanging says the page is working, pendingOp says "
                + "which button to put the spinner on. Folding it in changes no behaviour today and "
                + "would silently become a second, unowned busy flag the first time it is set "
                + "anywhere else"),
            new ExcludedField("pendingGroupRemoval", "the inline D2 warning row at 425-444",
                "staged confirmation for a nested-group removal. The warning row, its Remove group "
                + "button at 437 and its Cancel at 439 render ONLY while it is set, so folding it "
                + "in disables the confirmation of the page's most destructive action at the only "
                + "moment it renders - and its Cancel too, so the row could never be dismissed"),
            new ExcludedField("showRemoveConfirm", "the bulk-remove confirmation at 334-354",
                "a BOOL staged confirmation, which is the shape that compiles and looks plausible. "
                + "It renders the confirm block containing Remove N member(s) at 342 and Cancel at "
                + "346, so in IsManageBusy a confirmed batch removal could never be executed again "
                + "and the panel could never be closed"),
            new ExcludedField("showBulkAdd", "the whole bulk-add card at 251-293",
                "a BOOL view toggle with the same shape and the same consequence: it renders the "
                + "paste box, Resolve and Add resolved, so folding it in kills the bulk add path at "
                + "the only moment it exists. It means 'the operator opened the panel', not 'the "
                + "page is working'"),
            new ExcludedField("resolution", "the resolution table at 274-290 and Add resolved at 263",
                "the staged batch preview. ResolvedCount derives from it and is Add resolved's own "
                + "emptiness clause; the table renders only while it is non-null. Same consequence "
                + "as the two confirmations above"),
            new ExcludedField("selectedGuids", "the row checkboxes at 398 and Remove selected at 329",
                "which members are ticked. SelectedCount derives from it and is Remove selected's "
                + "own clause; it is a selection, not an operation"),
            new ExcludedField("groupMembers", "the member table at 379-447",
                "the loaded members. Data, replaced wholesale by every LoadMembers"),
            new ExcludedField("ownedGroups", "the owned-groups table at 110-134", "as groupMembers"),
            new ExcludedField("filterTerm", "the filter input at 91 and its Clear at 95",
                "a client-side filter value, and the Clear button's own render condition"),
            new ExcludedField("searchName", "the search input at 148 and Search at 151",
                "a form value; also the Search button's emptiness clause, which is OR-ed with "
                + "IsBrowseBusy and must never be folded into it"),
            new ExcludedField("memberIdentity", "the autocomplete at 226, Add at 232, Remove at 237",
                "a form value; also the emptiness clause on both single-member buttons"),
            new ExcludedField("pasteText", "the textarea at 256 and Resolve at 259",
                "a form value; also Resolve's emptiness clause"),
            new ExcludedField("callerSid", "nothing; it is the identity every service call is made as",
                "the caller's Windows SID, read once in OnInitializedAsync and never nulled. A "
                + "nullable that is only ever raised is the inverse of an in-flight flag, and every "
                + "handler already treats a blank one as a hard failure"),
        ],

        // Three, and the first is this page's whole decision. The other two are banner dismisses,
        // the same shape as ConferenceRooms 452 and DhcpAuthorization 40.
        ExemptControls =
        [
            new ExemptControl(205, "@onclick=\"BackToGroups\"",
                "the view switch out of the manage view, and the control the adversarial review "
                + "called this page unsound over. Gating it on IsManageBusy traps the operator: all "
                + "four mutating handlers end with await LoadMembers(), so every successful "
                + "membership change closes with an isLoadingMembers window the operator cannot "
                + "leave, and a fifty-member bulk remove holds that window open for the whole "
                + "batch. Exempting it is safe because the flags it leaves raised are consulted by "
                + "NOTHING in the browse view - the two predicates are disjoint - and the only way "
                + "back into the scope that owns them, Manage members at 128 and 179, is gated on "
                + "IsManageBusy. A flag reset here was considered and rejected: isChanging, "
                + "isLoadingMembers and isResolving are lowered in the finallys of operations still "
                + "in flight, so clearing them from outside means an old operation's finally later "
                + "lowers a flag a new view has raised",
                ConditionThatKeepsItTrue:
                "BackToGroups stays a synchronous state reset with no await and no service call, "
                + "and every handler that can outlive the switch keeps reading its own entry "
                + "snapshot rather than `selected`. LoadMembers is the one that did not, and its "
                + "catch dereferenced the nulled field: the registered PostAwaitLiveRead for "
                + "LoadMembers/selected is this exemption's real precondition. It cannot be "
                + "declared as PrerequisiteBeforeExemptionHolds - that field reads the tied field "
                + "out of this snippet and only works for an inline `() => field = null` handler - "
                + "so the link is prose and the remarks say so"),

            new ExemptControl(300, "@onclick=\"() => changeResult = null\"",
                "dismisses the shared result banner. Gating it would trap the previous operation's "
                + "message on screen for the whole of the next one, the same reason Migration's "
                + "dismiss at 442, DhcpAuthorization's at 40 and ConferenceRooms' at 452 are exempt",
                ConditionThatKeepsItTrue:
                "no await appears between a handler ASSIGNING changeResult and reading it back at "
                + "its ReferenceEquals(selected, group) refresh test. ChangeMember and "
                + "RemoveListedMember both read it there, so the read exists - it is unreachable "
                + "only because the browser never renders the banner, and so cannot dispatch this "
                + "dismiss, inside that synchronous stretch. Put an await between them and this "
                + "control can null the result under them: the refresh is then silently skipped and "
                + "the member list stays stale after a successful write"),

            new ExemptControl(361, "@onclick=\"() => bulkOutcome = null\"",
                "dismisses the per-row outcome table of the last batch, on the same reasoning as "
                + "300 and with a cleaner case: no handler on this page reads bulkOutcome at all, "
                + "every one of them only assigns it",
                ConditionThatKeepsItTrue:
                "no handler starts reading bulkOutcome back. The moment one does it needs the same "
                + "analysis as 300"),
        ],

        // Verified by hand against the file: all twenty @onclick attributes on this page are on a
        // <button>. No anchor, no div/span/td handler, no <form> and no @onsubmit. The two
        // @onchange attributes are on <input type="checkbox"> elements, which the DOM-synced sweep
        // below covers and this list structurally cannot.
        NonButtonTargets = [],

        // Six, censused by hand. Four inputs, one textarea and one child component; two of the
        // inputs are the bulk-selection checkboxes, which render server state into the DOM through
        // checked= and are the controls a handler guard would corrupt rather than refuse.
        DomSyncedControls =
        [
            new DomSyncedControl(91, "@bind=\"filterTerm\"", "input", "disabled=\"@IsBrowseBusy\""),
            new DomSyncedControl(148, "@bind=\"searchName\"", "input", "disabled=\"@IsBrowseBusy\""),
            new DomSyncedControl(226, "ObjectKind=\"User\"", "ADIdentityAutocomplete",
                "Disabled=\"IsManageBusy\""),
            new DomSyncedControl(256, "@bind=\"pasteText\"", "textarea", "disabled=\"@IsManageBusy\""),
            new DomSyncedControl(383, "checked=\"@AllSelectableSelected\"", "input",
                "disabled=\"@(IsManageBusy || SelectableMembers().Count == 0)\""),
            new DomSyncedControl(398, "@onchange=\"e => ToggleSelected(member, e.Value is true)\"",
                "input", "disabled=\"@(IsManageBusy || !CanRemove(member))\""),
        ],

        UngatedDomSyncedControls = [],

        // Three, and all three are shared per-member or per-batch helpers that a busy handler calls
        // while it is busy. A guard in any of them is silent by construction: the caller carries on
        // and reports the outcome it thinks it got.
        ForbiddenGuardSites =
        [
            new ForbiddenGuardSite("ChangeOneAsync", ["ChangeMember", "AddResolvedAsync"],
                "The ONE per-member typed path, called with isChanging already true by both the "
                + "single Add/Remove and every row of the bulk add. It returns a BulkRowOutcome and "
                + "the callers render it, so a guard here would report whatever the early return "
                + "produced as the row's outcome while no authorization re-check, no write, no "
                + "audit and no notification happened - and the batch summary would aggregate it. "
                + "The Constitution makes the per-row audit and the affected-user notification "
                + "non-negotiable; a guard here drops both silently."),

            new ForbiddenGuardSite("RemoveOneAsync", ["RemoveListedMember", "RemoveSelectedAsync"],
                "The removal half of the same shape, reached by the single Remove, by the D2 "
                + "nested-group confirmation and by every row of the bulk remove. Same consequence, "
                + "on the destructive direction."),

            new ForbiddenGuardSite("AuditBatch", ["RemoveSelectedAsync", "AddResolvedAsync"],
                "Both batch handlers call it while isChanging is true, on the success path AND on "
                + "both early-failure paths. It writes the one summary audit record for the batch; "
                + "a guard here destroys the record of a bulk membership change while the operator "
                + "is told it ran."),
        ],

        // Four. Each mutating entry point owns a single-flight refusal above its first await, and
        // the shared per-member handler it calls must carry none - two guards on one path is not
        // belt and braces here, because the caller raises isChanging before calling and the callee
        // guard would then refuse every row of every batch.
        ExactlyOneGuard =
        [
            new ExactlyOneGuardOf("ChangeMember", ["ChangeOneAsync"],
                "if (selected == null || isChanging || string.IsNullOrWhiteSpace(identity))",
                "The typed Add/Remove pair at 232 and 237 dispatch into ChangeMember, and the "
                + "browser's copy of their disabled attribute is one round trip stale, so the "
                + "refusal has to exist in code as well as in markup. It belongs in ChangeMember "
                + "because that is where isChanging is raised; in ChangeOneAsync it would refuse "
                + "every row of AddResolvedAsync's batch, which runs with isChanging already true."),

            new ExactlyOneGuardOf("AddResolvedAsync", ["ChangeOneAsync"],
                "if (group is null || rows.Count == 0 || isChanging)",
                "The bulk-add entry point, guarding the same callee for the same reason. Its guard "
                + "also sits below the snapshots of group and rows, deliberately: the refusal has "
                + "to test the rows this click was accepted for."),

            new ExactlyOneGuardOf("RemoveListedMember", ["RemoveOneAsync"],
                "if (selected == null || isChanging)",
                "The single Remove and the D2 confirmed nested-group removal both land here, and "
                + "this is the page's most destructive single action. The guard belongs here rather "
                + "than in RemoveOneAsync, which RemoveSelectedAsync calls once per row with "
                + "isChanging already true."),

            new ExactlyOneGuardOf("RemoveSelectedAsync", ["RemoveOneAsync"],
                "if (group is null || rows.Count == 0 || isChanging)",
                "The bulk-remove entry point, guarding the same callee for the same reason as "
                + "AddResolvedAsync guards ChangeOneAsync."),
        ],

        // Eight. Six carry a non-busy clause the predicate must be OR-ed in front of and must never
        // replace; two carry no clause and are here for their reachability, because they are the
        // browse-view controls gated on the MANAGE predicate and a reader who has not read the
        // remarks will take that for a mistake.
        AnnotatedControls =
        [
            new AnnotatedControl(128, "@onclick=\"() => SelectGroup(group)\"",
                ["IsManageBusy"],
                RendersOnlyWhen:
                "only in the browse view, and gated on IsManageBusy rather than IsBrowseBusy on "
                + "purpose. It is the door back into the manage view, so it is the one control that "
                + "must refuse while a manage-scope operation started before the operator pressed "
                + "Back is still in flight. This is the other half of the exemption at 205; neither "
                + "can be changed without the other. The PreserveClause is the page predicate "
                + "itself, which is off-label for a field documented as holding NON-busy clauses, "
                + "and it is kept deliberately now that the hole which produced it has been closed "
                + "at source. It arrived as a workaround: this conversion first carried a "
                + "title=\"@(IsManageBusy ? ... : null)\" on this button to explain the greying, "
                + "and stripping the disabled attribute outright then left the whole suite green, "
                + "because EveryClickableButtonConsultsAPredicateOrIsRegisteredExempt matched the "
                + "whole TAG text and the title still named the predicate. That sweep now reads the "
                + "disabled attribute VALUE, so the workaround's own reason is gone. The entry "
                + "stays because it pins what the sweep still cannot: this page registers TWO "
                + "predicates and the sweep is satisfied by either one, while this button must be "
                + "gated on the MANAGE predicate. IsBrowseBusy is the plausible wrong edit for a "
                + "control that renders in the browse view. Measured rather than argued, as "
                + "before: swapping this gate to IsBrowseBusy leaves the tightened sweep green and "
                + "fails here alone, so this entry is the only record of WHICH predicate. The "
                + "title is gone too - the cue is now a browse-view banner, which a disabled "
                + "control cannot suppress."),

            new AnnotatedControl(151, "@onclick=\"SearchGroup\"",
                ["string.IsNullOrWhiteSpace(searchName)"],
                RendersOnlyWhen:
                "always, while the browse view is open. The emptiness clause is re-checked by "
                + "SearchGroup's own guard, so it is belt and braces rather than sole enforcement - "
                + "but it is a precondition and not a busy condition, and a mechanical rewrite to "
                + "the bare predicate would delete it."),

            new AnnotatedControl(179, "@onclick=\"() => SelectGroup(searchResult)\"",
                ["IsManageBusy"],
                RendersOnlyWhen:
                "only while searchResult is non-null, which is why searchResult is a registered "
                + "ExcludedField. Gated on IsManageBusy, and its clause registered, for the same "
                + "reasons as 128."),

            new AnnotatedControl(232, "@onclick=\"() => ChangeMember(MembershipOperation.Add)\"",
                ["string.IsNullOrWhiteSpace(memberIdentity)"],
                RendersOnlyWhen:
                "always, while the manage view is open. Re-checked by ChangeMember's own guard."),

            new AnnotatedControl(237, "@onclick=\"() => ChangeMember(MembershipOperation.Remove)\"",
                ["string.IsNullOrWhiteSpace(memberIdentity)"],
                RendersOnlyWhen: "as 232, on the remove direction."),

            new AnnotatedControl(259, "@onclick=\"ResolvePasteAsync\"",
                ["string.IsNullOrWhiteSpace(pasteText)"],
                RendersOnlyWhen:
                "only while showBulkAdd is set. THE sole enforcement of its clause: ResolvePasteAsync "
                + "carries no emptiness guard at all, so deleting this clause makes every click on "
                + "an empty box a live AD batch query. That is the IntuneDevices 403 shape."),

            new AnnotatedControl(263, "@onclick=\"AddResolvedAsync\"",
                ["ResolvedCount == 0"],
                RendersOnlyWhen:
                "only while showBulkAdd is set. The clause is re-checked by AddResolvedAsync's "
                + "rows.Count == 0 guard, and the title beside it names the same condition in prose "
                + "- which is exactly why AnnotatedControlsKeepTheirNonBusyClauses reads the "
                + "disabled attribute value rather than the tag."),

            new AnnotatedControl(329, "@onclick=\"() => showRemoveConfirm = true\"",
                ["SelectedCount == 0"],
                RendersOnlyWhen:
                "only while the member list is non-empty. THE sole enforcement of its clause on the "
                + "staging step: nothing stops showRemoveConfirm being set with no rows ticked, and "
                + "the confirm block's own `&& SelectedCount > 0` is what then hides it - so "
                + "deleting this clause does not run a removal, it makes the button do nothing "
                + "visible. Its title names the same condition in prose."),
        ],

        // Eight distinct spinner conditions, none of which is either predicate and none of which
        // may be collapsed into one. The first is the pair that matters most: the Load spinner is
        // on isLoading alone so a single-group search does not put a spinner on the Load button,
        // and the Search spinner is on isSearching alone for the mirror reason. In the manage view
        // the three isChanging spinners are split by pendingOp so exactly one button spins, and the
        // fourth adds `resolution != null` so a single Add does not spin the bulk Add resolved.
        // Containment cannot tell two copies of one string apart, so deleting exactly one of the
        // two `@if (isLoading)` occurrences (62 and 65) passes - this comment is the only record
        // that there are two.
        SpinnerExpressions =
        [
            "@if (isLoading)",
            "@if (isSearching)",
            "@if (isLoadingMembers)",
            "@if (isChanging)",
            "@if (isChanging && pendingOp == MembershipOperation.Add)",
            "@if (isChanging && pendingOp == MembershipOperation.Remove)",
            "@if (isChanging && pendingOp == MembershipOperation.Add && resolution != null)",
            "@if (isResolving)",
        ],

        // Four. Two were found and fixed by this slice; two were already correct and are pinned so
        // they stay that way. See the remarks for the three obligations that exist in the page and
        // cannot be written here, and for the two live reads that are deliberate.
        PostAwaitLiveReads =
        [
            new PostAwaitLiveRead("LoadMembers", "selected", "group",
                SnapshotShape.CapturedAtEntry,
                "THE defect this slice found, and the precondition the exemption at 205 rests on. "
                + "The catch logged selected.Name after the await. Back to groups is ungated and "
                + "nulls selected, so a Back click landing while the member read failed raised a "
                + "NullReferenceException INSIDE a catch block; with no ErrorBoundary anywhere in "
                + "the app that escapes the handler and tears the circuit down, and the operator "
                + "loses the page rather than seeing an error. The load must also report the group "
                + "it was started for, not whatever is selected when it finishes"),

            new PostAwaitLiveRead("ResolvePasteAsync", "pasteText", "text",
                SnapshotShape.CapturedAtEntry,
                "the pasted identity list. It was parsed AFTER await Task.Yield() from a textarea "
                + "bound with @bind:event=\"oninput\", so a keystroke landing in the round trip "
                + "before the disabled attribute reached the browser had the operator approve a "
                + "resolution preview built from text that was never on screen when the click was "
                + "accepted - and Add resolved then writes that preview to AD"),

            new PostAwaitLiveRead("ChangeMember", "memberIdentity", "identity",
                SnapshotShape.CapturedAtEntry,
                "the user being added to or removed from the group: the value written to AD, "
                + "audited, and named in both the admin email and the affected-member "
                + "notification. Already captured at entry; pinned here because the autocomplete "
                + "at 226 writes this field and a suggestion click queued behind the button click "
                + "would otherwise retarget the write"),

            new PostAwaitLiveRead("AddResolvedAsync", "resolution", "rows",
                SnapshotShape.CapturedAtEntry,
                "the resolved lines this click commits. ResolvePasteAsync REPLACES the list, so a "
                + "Resolve dispatched in the round trip before the disabled attributes reach the "
                + "browser would have this handler write rows from a parse the operator never "
                + "approved. Already snapshotted; pinned so it stays that way"),
        ],

        // Seven, one per handler that raises a flag, and every one of them matters more here than
        // on a single-predicate page: each flag is a member of a predicate that gates a whole VIEW,
        // so a raise moved above its early return does not grey one button, it deadens every
        // control in that view permanently the first time the guard fires.
        RaiseMustFollowEarlyReturn =
        [
            new RaiseAfterEarlyReturn("LoadMembers", "isLoadingMembers", "group == null",
                "Nothing lowers isLoadingMembers on the no-group path. Raised above it, the whole "
                + "manage view dies the first time LoadMembers is reached with nothing selected - "
                + "including Back to groups' destination, so the operator sees a dead page."),

            new RaiseAfterEarlyReturn("ChangeMember", "isChanging",
                "selected == null || isChanging || string.IsNullOrWhiteSpace(identity)",
                "The guard fires on every click with an empty identity box, which is the ordinary "
                + "mistake rather than an edge case. Raised above it, the manage view is dead from "
                + "that click onward and the only escape is Back to groups."),

            new RaiseAfterEarlyReturn("RemoveListedMember", "isChanging",
                "selected == null || isChanging",
                "As ChangeMember, on the list-driven removal."),

            new RaiseAfterEarlyReturn("RemoveSelectedAsync", "isChanging",
                "group is null || rows.Count == 0 || isChanging",
                "The rows.Count == 0 arm is reachable whenever the selection is emptied between "
                + "render and click. Raised above it, the manage view dies."),

            new RaiseAfterEarlyReturn("AddResolvedAsync", "isChanging",
                "group is null || rows.Count == 0 || isChanging",
                "As RemoveSelectedAsync, on the bulk add."),

            new RaiseAfterEarlyReturn("LoadOwnedGroups", "isLoading", "isLoading",
                "Nothing lowers isLoading on the already-running path. Raised above the guard, the "
                + "browse view is dead on the second dispatch of a double click - and the browse "
                + "view is where the page opens, so the operator cannot reach anything at all."),

            new RaiseAfterEarlyReturn("SearchGroup", "isSearching",
                "isSearching || string.IsNullOrWhiteSpace(searchName)",
                "Reached by the Enter key as well as the button, and the empty-box arm is the "
                + "ordinary mistake. Raised above it, the browse view dies."),
        ],

        // One, and it is the 2eb8c15 shape: an @onkeydown reaching an operation on an element whose
        // gated twin button sits beside it. The element already carried a disabled attribute, so
        // this conversion widened it from isSearching to IsBrowseBusy rather than adding one - a
        // disabled input fires no key events, which closes the typing path and the Enter path
        // together. Registered with its twin at 151 so neither half can be narrowed alone.
        KeyboardPaths =
        [
            new KeyboardPath(148, "@bind=\"searchName\"", "input", "keydown", "OnSearchKeyDown",
                "SearchGroup", KeyboardRefusal.DisabledAttribute, "disabled=\"@IsBrowseBusy\"",
                "Enter in the search box runs a live directory search under the module credential "
                + "while the Search button beside it is refusing clicks. Not destructive, but it is "
                + "the exact reading-as-correct-while-firing shape 2eb8c15 measured on Migration, "
                + "and OnSearchKeyDown's own !isSearching test would not survive a rename of the "
                + "flag the way the registered gate does.",
                GatedTwinButtonLine: 151),
        ],

        // Empty, and censused by hand rather than inherited: the search box is the page's only
        // keyboard handler of any kind. The forward direction of
        // EveryKeyboardPathIsRegisteredOrRecordedHarmless is what keeps that true - a handler added
        // here later fails the suite and names itself.
        HarmlessKeyboardPaths = [],
    };

    /// <summary>
    /// Defender for Endpoint Devices: registered at birth, with the page's slice (S2 of
    /// docs/DefenderEndpointDevices-Plan.md), rather than converted afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The smallest entry in this file, and that is a property of the page rather than of the
    /// bookkeeping. The module reads and exports and mutates nothing, so there is exactly one
    /// operation, one in-flight flag and one predicate; there is no staged-confirmation state, so
    /// nothing belongs in ExcludedFields; there is no banner to dismiss and no per-row action, so
    /// no control needs an exemption; and every click target is a &lt;button&gt;, so NonButtonTargets
    /// is empty. Censused by hand against the file rather than inherited from the plan.
    /// </para>
    /// <para>
    /// No keyboard handler of any kind: the filter card carries a select and a checkbox, neither of
    /// which has an @onkeydown, and there is no free-text box for Enter to submit. KeyboardPaths and
    /// HarmlessKeyboardPaths are therefore both empty, and the forward direction of
    /// <see cref="ClickGateTests.EveryKeyboardPathIsRegisteredOrRecordedHarmless"/> is what keeps
    /// that true if one arrives later.
    /// </para>
    /// <para>
    /// isLoading is raised in LoadAsync alone, deliberately NOT in OnInitializedAsync the way
    /// ServiceHealth.razor raises its own flag. ServiceHealth is unconverted so nothing checks it;
    /// here that shape would be a raise with no try behind it, which is the stuck-flag defect
    /// <see cref="ClickGateTests.EveryRegisteredFlagIsLoweredInAFinally"/> exists for - and with a
    /// page-wide predicate a stuck flag does not grey one button, it deadens the page. The first
    /// render's spinner is keyed on <c>result == null</c> instead, which costs nothing because the
    /// deferred load starts in OnAfterRenderAsync on that same render.
    /// </para>
    /// <para>
    /// The two filters are the whole of PostAwaitLiveReads. Both are operator-writable and the
    /// browser's copy of a disabled attribute is one round trip stale, so a change landing in the
    /// gap between the click and the render would otherwise let the audit record name one filter
    /// set while the service was asked for another - the CapturedAtEntry shape, exactly as
    /// DhcpAuthorization's three form fields.
    /// </para>
    /// </remarks>
    private static PageGateEntry DefenderEndpointDevices => new()
    {
        Page = "DefenderEndpointDevices.razor",
        ExpectedLineCount = 430,

        Predicates =
        [
            new PredicateScope("IsBusy",
                ["isLoading"],
                AppliesWhen: "the whole page"),
        ],

        NonButtonTargets = [],

        DomSyncedControls =
        [
            new DomSyncedControl(65, "@bind=\"onboardingStatus\"", "select", "disabled=\"@IsBusy\""),
            new DomSyncedControl(75, "@bind=\"windowsOnly\"", "input", "disabled=\"@IsBusy\""),
        ],

        UngatedDomSyncedControls = [],

        PostAwaitLiveReads =
        [
            new PostAwaitLiveRead("LoadAsync", "onboardingStatus", "status",
                SnapshotShape.CapturedAtEntry,
                "the onboarding status the operator saw when the click was accepted is the filter "
                + "that must be sent AND the filter the audit record names. Read live after the "
                + "await, a change landing in the stale-attribute window would desync the audit row "
                + "from the request that produced the list on screen"),

            new PostAwaitLiveRead("LoadAsync", "windowsOnly", "windows",
                SnapshotShape.CapturedAtEntry,
                "same obligation on the other filter. The Windows rule is applied to the fetched "
                + "set, so reading it live would let the rendered list be narrowed by one value "
                + "while the audit record claims another"),
        ],

        KeyboardPaths = [],

        HarmlessKeyboardPaths = [],
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
    /// Every control that renders server state into the DOM and IS gated, with the gate recorded
    /// verbatim. An input, a select, a textarea or a child component taking a Disabled parameter.
    /// </summary>
    /// <remarks>
    /// Forced by a measured blind spot, not by symmetry. The NamedLocations conversion ran seven
    /// guard mutations and six bit; the one that did not was stripping Disabled="@IsBusy" from
    /// CountryCodePicker at 172, which left the suite at 0 failed / 63 passed. That control latches
    /// _initialized on its first parameter push and Apply is its sole ValueChanged path, so a click
    /// the server does not take desyncs it permanently and the next save writes the OLD country set
    /// to Graph. <see cref="NonButtonTargets"/> could not cover it: that list is keyed to what
    /// ClickGateSource.NonButtonClickTargets() can locate, which is @onclick only, so registering an
    /// input or a child component there fails
    /// <see cref="ClickGateTests.EveryNonButtonTargetIsRefusedByItsDeclaredMechanism"/> rather than
    /// protecting anything. Both page authors therefore left these controls in prose; this is that
    /// prose promoted to data.
    /// </remarks>
    public IReadOnlyList<DomSyncedControl> DomSyncedControls { get; init; } = [];

    /// <summary>
    /// DOM-synced controls that carry no busy gate, each with the recorded reason. Inert controls
    /// exist - NamedLocations 153 is a statically disabled read-only display - so this has to be
    /// possible; it is explicit so an oversight cannot pass for a decision. Same bargain as
    /// <see cref="NotYetConverted"/>: an entry here is a deliberate, reviewable registry edit.
    /// </summary>
    public IReadOnlyList<UngatedDomSyncedControl> UngatedDomSyncedControls { get; init; } = [];

    /// <summary>
    /// Methods that must NOT carry a guard, because a busy handler calls them. Six of the eleven
    /// reconnoitred pages call a refresh or shared helper from a handler that is already busy, and
    /// a guard there turns the confirming refresh into a silent no-op.
    /// </summary>
    public IReadOnlyList<ForbiddenGuardSite> ForbiddenGuardSites { get; init; } = [];

    /// <summary>
    /// Caller/callee pairs where the refusal must live in exactly one named method: the positive
    /// counterpart of <see cref="ForbiddenGuardSites"/>, which can only say where a guard may not
    /// go.
    /// </summary>
    /// <remarks>
    /// Listed in docs/ClickGatingAudit-Plan.md Revision 1 and missing from the slice that shipped
    /// the rest of this registry, which is how the gap was found rather than predicted. Without it,
    /// deleting MailboxPermissions.ConfirmOnPrem's guard outright passes every other assertion here
    /// - the forbidden site stays clean and nothing requires a handler guard anywhere - while the
    /// destructive on-prem write is left open to a second dispatch in the round trip before the
    /// Continue button's disabled attribute reaches the browser.
    /// </remarks>
    public IReadOnlyList<ExactlyOneGuardOf> ExactlyOneGuard { get; init; } = [];

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

    /// <summary>
    /// Every keyboard handler bound in markup that reaches an operation, and how each is refused.
    /// A key press that runs an operation is a click-equivalent, and the owner ruling of 2026-09-17
    /// - a control is actionable only when its action will definitively execute - does not care
    /// which key produced it.
    /// </summary>
    /// <remarks>
    /// Forced by a measured defect, not by symmetry. Commit 2eb8c15 found two live holes on the
    /// converted precedent page: Migration's search box and staged-ticket box carried no disabled
    /// attribute and routed <c>@onkeydown</c> to handlers calling SearchUser and
    /// ConfirmPendingAction with no busy guard, while the Search and Confirm buttons beside them
    /// were gated on IsBusy. The button greyed out and Enter still fired the operation; on the
    /// staged-ticket box that operation is destructive, and it avoided double execution only by
    /// accident. Seven bespoke tripwires and a review missed it because nothing in this suite
    /// matched a keyboard handler at all:
    /// <see cref="ClickGateTests.EveryNonButtonClickTargetIsRegistered"/> finds <c>@onclick</c>
    /// only, and the DOM-synced assertions key on the disabled attribute, so a keyboard path on an
    /// element with no gate is invisible to both.
    /// </remarks>
    public IReadOnlyList<KeyboardPath> KeyboardPaths { get; init; } = [];

    /// <summary>
    /// Keyboard handlers that reach no operation - a filter, a panel close, an arrow-key move - each
    /// with the recorded reason. Explicit for the same reason
    /// <see cref="UngatedDomSyncedControls"/> is: an inferred exemption is an oversight that looks
    /// like a decision.
    /// </summary>
    public IReadOnlyList<HarmlessKeyboardPath> HarmlessKeyboardPaths { get; init; } = [];
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
    /// The gap is in a shared component and cannot be closed from this page. The worked example was
    /// ADIdentityAutocomplete, which gated its input but not its suggestion rows across nine sites;
    /// that one is now fixed in the component itself. The mechanism stays because the CATEGORY is
    /// permanent: a page cannot close a defect that lives inside a component it merely instantiates,
    /// and the page-level registry cannot see inside one either - it inspects the call site, where
    /// Disabled= was already present and correct the whole time.
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

/// <summary>
/// A control that renders server state into the DOM and is gated in markup.
/// </summary>
/// <param name="Line">Where it starts. The registry key, as everywhere else in this file.</param>
/// <param name="Snippet">
/// A distinctive substring of the live tag, on the tag's FIRST line so a multi-line element does
/// not make the literal depend on indentation. It is the disambiguator, not decoration: the line
/// alone is not a key, for the same reason <see cref="ExemptControl.Snippet"/> exists.
/// </param>
/// <param name="Tag">
/// The element or component name as written - "input", "select", "textarea", or the PascalCase
/// component. Asserted, so a select silently becoming an input fails rather than passing.
/// </param>
/// <param name="DisabledExpression">
/// The WHOLE disabled=/Disabled= attribute, verbatim, e.g. <c>disabled="@IsBusy"</c>. Whole rather
/// than just the identifier: a bare name can be matched incidentally elsewhere in the tag and
/// proves nothing. Verbatim rather than reconstructed, in the same spirit as
/// <see cref="PageGateEntry.SpinnerExpressions"/> - narrow the gate and the assertion fails instead
/// of quietly accepting the narrower one.
/// </param>
public sealed record DomSyncedControl(
    int Line,
    string Snippet,
    string Tag,
    string DisabledExpression);

/// <param name="WhyUngated">
/// Why this DOM-synced control carries no busy gate. Two honest kinds of answer: the control is
/// inert (no binding, no handler, nothing a click can desync), or the gap is real and recorded
/// pending a decision. Both are fine; silence is not.
/// </param>
public sealed record UngatedDomSyncedControl(
    int Line,
    string Snippet,
    string Tag,
    string WhyUngated);

public sealed record ForbiddenGuardSite(
    string Method,
    IReadOnlyList<string> CalledWhileBusyFrom,
    string Consequence);

/// <summary>
/// Where the one refusal for a caller/callee pair lives, and which methods must therefore carry
/// none. Two guards on one path is not belt and braces: the second becomes a silent no-op the
/// moment the first raises a flag before calling, and no guard at all leaves the click open.
/// </summary>
/// <param name="GuardedMethod">The single method that carries the refusal.</param>
/// <param name="UnguardedMethods">
/// Every method on the same path that must carry none. Named individually rather than inferred,
/// because "the callee" is a fact about the code that this scan cannot establish and a reader can.
/// </param>
/// <param name="Guard">
/// The guard's if-condition verbatim, up to the closing bracket - e.g.
/// <c>if (IsBusy || !onPremConfirmPending)</c>. Verbatim rather than reconstructed, in the same
/// spirit as <see cref="DomSyncedControl.DisabledExpression"/>: narrow the guard and the assertion
/// fails instead of quietly accepting the narrower one. The condition rather than the whole
/// statement, so a two-line guard does not put the page's indentation in the registry; that it
/// actually RETURNS is checked against the file rather than taken from this string.
/// </param>
/// <param name="WhyThisOne">
/// Why the refusal belongs on <paramref name="GuardedMethod"/> and nowhere else. Which of two
/// methods owns a refusal is a decision, and an unrecorded decision cannot be told from an accident.
/// </param>
public sealed record ExactlyOneGuardOf(
    string GuardedMethod,
    IReadOnlyList<string> UnguardedMethods,
    string Guard,
    string WhyThisOne);

/// <param name="PreserveClauses">
/// Non-busy clauses that must remain in the disabled expression, OR-ed with the predicate rather
/// than replaced by it. IntuneDevices line 403 is why this exists: its typed-device-name clause is
/// the only enforcement of that confirmation anywhere, and a mechanical rewrite would delete it.
/// Each string is looked for in the control's disabled attribute VALUE, not anywhere in its tag, so
/// a clause a tooltip also names is enforced against the gate and not against the tooltip. Writing
/// the leading "|| " is optional and is the convention where a gate's clause is mirrored in prose;
/// it additionally pins the clause as one term of an OR rather than the whole condition.
/// </param>
/// <param name="RendersOnlyWhen">Reachability the scanner cannot see, as prose for a reviewer.</param>
public sealed record AnnotatedControl(
    int Line,
    string Snippet,
    IReadOnlyList<string> PreserveClauses,
    string? RendersOnlyWhen = null);

/// <summary>How a keyboard path is stopped from reaching an operation while the page is busy.</summary>
public enum KeyboardRefusal
{
    /// <summary>
    /// The element carries a disabled=/Disabled= attribute naming a busy signal. A disabled form
    /// control fires no key events at all, so one attribute closes the key path and the typing path
    /// together - which is why it is the mechanism both of Migration's Enter paths took. It is only
    /// a refusal on an element the attribute reaches: input, select, textarea, button. An anchor, a
    /// div or a span ignores it entirely and keeps firing keydown, which is what
    /// <see cref="ClickGateTests.NoKeyboardPathIsRefusedByADisabledAttributeAnElementIgnores"/>
    /// exists to catch.
    /// </summary>
    DisabledAttribute,

    /// <summary>
    /// The handler refuses for itself, with an early return on a busy signal. Legitimate for a key
    /// press - unlike a click on a DOM-synced control, a keydown changes no rendered server state,
    /// so refusing it desyncs nothing - but weaker: the operator presses Enter into silence with no
    /// visible cue, and the element still needs its own gate for whatever it is bound to.
    /// </summary>
    HandlerGuard,
}

/// <summary>
/// A keyboard handler bound in markup that reaches an operation, with the refusal that stops it.
/// </summary>
/// <param name="Line">
/// Where the ELEMENT carrying the handler starts - not where the @onkey attribute is written, which
/// is often a continuation line. The tag's start line is the registry key everywhere else in this
/// file, and keying on it is what lets a DisabledAttribute path be checked against the
/// <see cref="DomSyncedControl"/> entry for the same control.
/// </param>
/// <param name="Snippet">A distinctive substring of the live tag, on the tag's first line.</param>
/// <param name="Tag">The element name as written, asserted so an input becoming a div fails.</param>
/// <param name="Event">The DOM event without the @on prefix: "keydown", "keyup" or "keypress".</param>
/// <param name="Handler">The method the attribute binds to.</param>
/// <param name="ReachesOperation">
/// The operation a key press reaches, asserted to be named in <paramref name="Handler"/>'s body. It
/// is what makes this entry a gating obligation rather than a note: repoint the handler at
/// something else and the entry fails instead of quietly covering a different operation.
/// </param>
/// <param name="Gate">
/// The refusal verbatim: the WHOLE disabled=/Disabled= attribute for
/// <see cref="KeyboardRefusal.DisabledAttribute"/>, or the guard statement - e.g.
/// <c>if (IsBusy) return;</c> - for <see cref="KeyboardRefusal.HandlerGuard"/>. Verbatim rather
/// than reconstructed, in the same spirit as <see cref="DomSyncedControl.DisabledExpression"/>:
/// narrow the gate and the assertion fails instead of quietly accepting the narrower one.
/// </param>
/// <param name="Why">What a key press does if the refusal goes, in operator-visible terms.</param>
/// <param name="GatedTwinButtonLine">
/// The button that reaches the same operation and is gated, if there is one. This is the 2eb8c15
/// defect made checkable: the hole was not that the keyboard path was ungated in the abstract, it
/// was that the button beside it WAS gated, so the page read as correct while Enter still fired.
/// Setting this asserts the button is still there, still reaches
/// <paramref name="ReachesOperation"/>, and still names a busy predicate. Null means no such button
/// exists, and then <paramref name="Why"/> is the only record of that - the honest escape hatch.
/// </param>
public sealed record KeyboardPath(
    int Line,
    string Snippet,
    string Tag,
    string Event,
    string Handler,
    string ReachesOperation,
    KeyboardRefusal Refusal,
    string Gate,
    string Why,
    int? GatedTwinButtonLine = null);

/// <param name="WhyHarmless">
/// Why this key press reaches no operation. Two honest kinds of answer: it only filters, sorts,
/// moves a highlight or closes a panel, or it is guarded somewhere this scan can see. Both are
/// fine; silence is not.
/// </param>
public sealed record HarmlessKeyboardPath(
    int Line,
    string Snippet,
    string Tag,
    string Event,
    string Handler,
    string WhyHarmless);
