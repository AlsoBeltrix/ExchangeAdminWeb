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
            // Pages 1 to 3 of 9 - DhcpAuthorization.razor, NamedLocations.razor and
            // MailboxPermissions.razor - are converted and live in Pages above.
            ["CalendarPermissions.razor"] = "tier 1, page 4 of 9; the structural twin of "
                + "MailboxPermissions.razor. Inherit that page's decisions rather than "
                + "re-litigating them, and note it shares the RecipientAutocomplete gap",
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
    /// <b>The shared component is not fixed here and its blast radius is bigger than the plan says.</b>
    /// Revision 1 schedules one contract change, ADIdentityAutocomplete, at page 6. There are two.
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
    /// is not enough. The ForbiddenGuardSite below is the enforced half. <b>Its enforcement is
    /// partial and that is measured, not assumed:</b>
    /// <see cref="ClickGateTests.ForbiddenGuardSitesCarryNoGuard"/> matches the literal
    /// <c>if (IsBusy) return;</c>, so planting a COMPOUND guard in ExecuteOnPrem - the shape
    /// ConfirmOnPrem itself uses - slips past it. Probe M6 of this conversion demonstrated exactly
    /// that. Widening that assertion is queued with the ExactlyOneGuardOf work.
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
    /// <b>One obligation is deliberately absent, and its absence is the honest answer rather than a
    /// gap.</b> autoMapping IS captured at entry into autoMap and every use below the first await
    /// reads the local - the hazard falsification 2 describes is closed in the code. It is not
    /// registered because AuditService.LogMailboxPermission takes a parameter of that name and the
    /// success-path call site passes it by name, and ClickGateTests.ReadOf cannot tell the label
    /// <c>autoMapping:</c> from a field read. The remedy is to teach ReadOf to exclude
    /// <c>identifier:</c> in argument position, exactly as it already excludes assignment, not to
    /// bend a readable call site into a ninth positional boolean. Queued with the ExactlyOneGuardOf
    /// work; until then this obligation is enforced by a reader.
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
        // autoMapping is missing from this list deliberately and is closed in the code; see the
        // remarks on this entry for why it cannot be registered yet.
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
