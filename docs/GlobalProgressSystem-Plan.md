# Global Progress System - Plan

Status: **Draft at revision 2. Not approved. No code written.**
Revision 2 folds in the 2026-10-01 approach review, which returned "Acceptable with changes:
the approach stands" and named five required changes. All five are applied; see section 12.
Queue item 24, owner-declared P1 on 2026-10-01.
Base app version: shared infrastructure, so `<VersionPrefix>` in `ExchangeAdminWeb.csproj`
bumps. Each module's adoption additionally bumps that module's `Version` in
`Modules/ModuleCatalog.cs`. Both versioning rules fire independently
(`docs/ProjectConstitution.md`, Deployment And Versioning).

Scope touches: a new service and a new shared component, `Components/Layout/MainLayout.razor`,
`Components/App.razor`, `Program.cs`, `Services/Jobs/*`, and - one at a time, in later slices -
every page under `Components/Pages/`.

---

## 1. What the owner asked for

Verbatim, 2026-10-01:

> "It's not obvious in most modules that a click landed. we need a golbal progress meter system
> so all modules behave the same and prioritize keeping the user up to date throughout the
> process."

Clarified by the owner in the same session, and these clarifications are binding:

1. **This is one system, not a harmonisation of 36 existing ones.** Owner: *"if each module has
   its own spinner, I don't want those updated to look the same. that's not a global system."*
   Modules do not draw progress. Modules **report** to the progress system, and the progress
   system is the only thing that renders anything.
2. **Accuracy is a requirement, not a nicety.** Owner: *"clearly, obviously, and accurately
   displays progress so no user is ever left wondering if the click worked."*
3. **Orphaning results is not acceptable, and a progress bar does not excuse it.** Owner:
   *"navigating away from a page if doing so will orphan the results is not acceptable. a
   progress system doesn't fix that. if there's no place for the data to land, showing its
   progress is just protracted doom."*
4. **The navigation behaviour is specified.** Owner: *"show a popup saying there's an op
   in-progress and warn that navigating away will kill it, then actually kill it if the user
   clicks okay, keep on the page if they click cancel. for long running ops that use the
   background runner, tell the user how to check and retrieve results."*
5. **Comms-10k moves to the background runner.** Owner, asked whether a mid-write replace
   should be killable: *"comms-10k should use the background jobs runner."*

Point 5 generalises, and the generalisation is this plan's central rule. See section 4.

---

## 2. Measured facts

Everything in this section was measured against the tree at `839831e`. Do not trust it after a
page edit; re-measure.

### 2.1 There is no shared feedback component of any kind

`Components/Shared/` holds 9 components - six autocompletes/pickers, `TicketNumberInput`,
`UnsavedChangesGuard`, `ModuleVersion`. **None is a spinner, progress bar, busy overlay, status
banner, toast or alert.** `Components/Layout/MainLayout.razor` has no busy region; it contains
`#blazor-error-ui` and nothing else of this kind.

There is no UI notification service, toast container or snackbar. `wwwroot/app.css:1335-1339`
themes a Bootstrap `.toast` class that **no markup in the app uses**.

The Constitution's "Notifications" section (`docs/ProjectConstitution.md:76-82`) is about
**email** via `EmailService`, not UI. It is unaffected by this work and remains mandatory.

### 2.2 Operator feedback today is 36 hand-rolled copies

- Busy state is a per-page private field. No base class, no service, no interface.
- 19 pages use `isLoading`; around 25 further one-off verbs exist (`isSearching`,
  `isOperating`, `isResolving`, `saving`, `busy`, `isDownloadingCsv`, ...).
- 14 pages have a page-level predicate; **22 of 36 have none at all.**
  `AdminBulkJobs.razor` and `AccountLockoutRemediation.razor` have zero in-flight flags.
- Six structurally different spinner idioms are in use, plus 146 hand-written
  `alert alert-*` divs backed by ~30 differently-named fields.

### 2.3 The biggest single cause of "my click did nothing" is navigation, not buttons

`Program.cs:436` calls `AddInteractiveServerRenderMode()` and no call site passes
`prerender: false`. The page is rendered on the server before the browser receives anything, so
a spinner inside `OnInitializedAsync` **can never paint on first navigation**. The app also uses
Blazor enhanced navigation, so the previous page stays fully rendered and interactive for the
whole load. The operator sees no change at all and clicks again.

This is already diagnosed in `docs/ServiceHealthLoadFeedback-Plan.md:44-66`, which fixed it on
five pages by deferring the load to `OnAfterRenderAsync` behind a `loadStarted` one-shot guard -
`AdminSettings`, `BlockedSenders`, `DefenderEndpointDevices`, `MessageTrace`, `ServiceHealth`.
That plan explicitly declared the sweep out of scope (`:98-100`). **The other 31 pages load in
`OnInitializedAsync` and are unfixed.**

`Components/App.razor:73` already subscribes to `blazor:enhancedload` for theme reapplication,
so the browser-side navigation hook point exists and is proven in this app.

### 2.4 Nothing in the app can be cancelled

- **No page uses `CancellationToken`.** Zero occurrences across all 36 `.razor` files.
- Only two pages implement `IDisposable` - `AdminBulkJobs` and `ConferenceRooms` - and both do
  so only to unsubscribe from `BulkJobService.JobChanged`.
- Consequence, confirmed by structure: navigating away does not stop the work. The operation
  runs to completion on the server and its result is assigned into fields of a component that
  no longer exists. Nothing is shown, nothing is logged as lost, and returning to the module
  looks identical to never having clicked.

**"Actually kill it" therefore does not exist today and is new work in every module that gets
it.** This is the largest single cost in this plan and it is unavoidable: the owner's
requirement is explicit.

### 2.5 The background runner already does the right thing, for three modules

`Services/Jobs/` - `BulkJobService` (singleton, `Program.cs:106`), `BulkJobRepository`,
`JobStoreMigrator`, `IBulkJobProcessor`, `BulkJobProcessorRegistry`.

- Durable in SQLite at `config/exchangeadmin-jobs.db`, which is per-instance and deliberately
  neither backed up nor promoted (`.agents/repo-guidance.md`, Architectural Invariant 3).
- `BulkJob` carries real numeric progress: `TotalRows`, `ProcessedRows`, `SuccessCount`,
  `PartialCount`, `FailedCount`, `HeartbeatAtUtc` (`Services/Jobs/BulkJobModels.cs:93-95`).
- `BulkJobStatus` is `Queued | Running | Completed | Cancelled | Interrupted`. `Stalled` is a
  display-only classification derived from a stale heartbeat, never stored
  (`BulkJobService.DisplayStatus`, threshold `BulkJobs:StaleHeartbeatMinutes`, default 5).
- **Cancellation already exists here**: `BulkJobRepository.RequestCancel` /
  `IsCancelRequested`, surfaced as `BulkJob.CancelRequested`.
- Survives navigation, survives app restart (non-terminal jobs flip to `Interrupted` at
  startup, `BulkJobService.InitializeAsync`).
- `public event Action<string>? JobChanged` (`BulkJobService.cs:49`) is raised on enqueue,
  start, every row and terminal transition. Subscriber throws are swallowed and logged.

Enqueued today by exactly three pages: `ConferenceRooms.razor:1433`, `MessageTrace.razor:1170`,
`Migration.razor:2667`.

**Known shortfalls of the existing runner, which this plan must fix rather than inherit:**

- Neither job view auto-refreshes. Both require a manual **Refresh** click
  (`AdminBulkJobs.razor:44`). `BulkJobService.cs:47` records the original reasoning:
  "poll-on-event is enough". That reasoning does not survive requirement 2.
- Nothing tells the operator a job ended. There is no completion or failure notification.
- The same progress table is implemented twice and independently -
  `AdminBulkJobs.razor:56-115` and `ConferenceRooms.razor:479-520` - with different column
  counts, different status-label helpers and different job-kind labelling.
- Progress is rendered as a text fraction. There is no `<progress>` element and no
  `.progress-bar` in any markup, although `wwwroot/app.css:730-732` already themes one.
- `BulkJobService` is `sealed` with **no interface**; pages depend on the concrete type.

### 2.6 What the stakeholder actually complained about

Comms-10k reviewer, 2026-10-01, point 2, verbatim: *"We currently have no visibility into
whether the member synchronization is still in progress ... a clear notification indicating
whether the member loading process has been successfully completed or has failed."*

That is this work item, and it is satisfied by section 4's rule plus a completion
notification - not by a spinner.

---

## 3. What is broken, stated as defects

- **D1.** Clicking a module in the sidebar produces no visual change for the duration of the
  load, on 31 of 36 pages. The old page remains interactive, so the operator re-clicks.
- **D2.** 22 of 36 pages give no in-page indication that a click was accepted.
- **D3.** Work that outlives its page is silently discarded, in 33 of 36 modules. No result,
  no error, no record the operator can see.
- **D4.** Nothing can be cancelled, so the only way to stop work is to orphan it (D3).
- **D5.** The three modules that do persist results require the operator to know about the
  Bulk Jobs page, navigate to it, and press Refresh. Nothing announces completion or failure.
- **D6.** Where feedback exists it is inconsistent in wording, placement and shape, so it
  carries no learnable meaning.

---

## 4. The rule this plan is built on

**Durability first, display second.** A progress indicator is only honest if the operation it
describes has somewhere to put its answer. Therefore:

> **Any operation that writes more than one item, or that can run longer than a few seconds,
> runs on the background runner.** Its result is persisted, leaving the page is safe, and there
> is nothing to kill.
>
> **Everything else runs in the page, is cancellable, and is short enough that cancelling it
> loses nothing.**

This is the owner's Comms-10k ruling generalised, and it is what makes requirement 4
implementable without the half-written-group problem: the popup can only ever offer to kill
work that is safe to kill, because heavy work is no longer in the page to be killed.

**Corollary - the honesty rule.** The system renders exactly what the module reported and never
synthesises a percentage. Three permitted shapes, and no others:

| Shape | When | Rendered as |
| --- | --- | --- |
| Indeterminate | the module cannot know the size of the work | moving bar, label only |
| Step `n` of `m` | the module knows its stages | determinate bar + "step n of m" |
| `x` of `y` items | the module is iterating a known collection | determinate bar + count |

A module that does not know its total reports indeterminate. It must not guess, and there is no
API that would let it.

---

## 5. Design

Three parts. Modules touch only the first.

### 5.1 The reporting channel - `IActivityProgress`

A **per-circuit (scoped)** service. Modules call it; modules render nothing.

**Lifetime rule, and it is load-bearing: `IActivityProgress` must never be injected into a
singleton.** Most module services are registered `AddSingleton` - `Comms10kService`
(`Program.cs:177`), `IntuneDeviceService` (`:176`), `M365GroupManagementService` (`:191`),
`NamedLocationsService` (`:190`), `RiskyUsersService` (`:195`), `ServiceHealthService` (`:199`),
`DefenderEndpointDeviceService` (`:204`), `TrueLastLogonService` (`:211`),
`DhcpAuthorizationService` (`:213`) and others. Injecting a per-circuit service into any of them
captures the first circuit's progress sink for the lifetime of the process and reports one
operator's work to another. Only some services are `AddScoped` (`ConferenceRoomService`,
`GroupManagementService`, `SelfServiceGroupService`, `EmergencyDisableService`,
`DelegationReportService`, ...), and the split is not a reliable guide to write by.

**Therefore progress is page-owned and passed down, never resolved inside a service:**

- The **page** begins the activity and holds the handle.
- A service that needs to report passes through an `IProgress<ActivityUpdate>` and/or a
  `CancellationToken` **as method arguments**, supplied by the page. Services take no
  constructor dependency on the progress system, so their lifetime stays irrelevant.
- **Background work does not use this channel at all.** A job reports by writing to the job
  store, which is already durable and already per-job. The display reads it from there.

A test enforces the rule; see section 8.

Shape (indicative, settled in S1 against the code):

- `IActivityHandle Begin(string moduleId, string label, ActivitySize size)` where
  `ActivitySize` is `Indeterminate`, `Steps(int total)` or `Items(int total)`.
- `handle.Report(int done, string? detail = null)` - ignored for `Indeterminate`.
- `handle.Step(string label)` - advances a step-shaped activity.
- `handle.Complete(bool success, string? message = null)`.
- `handle.CancellationToken` - the token the module must honour. This is how requirement 4's
  "actually kill it" reaches the work.
- `IDisposable`, so a `using` guarantees the activity ends even on an exception path.

It is a **service, not page state**, for the reason recorded in
`Services/AdminPageDirtyState.cs:8-13`: this repo has no bUnit harness, nothing can render a
page in a test, so anything left in markup is untestable. `AdminPageDirtyState` is the existing
precedent for a shared UI-state service in this app and this follows its shape.

The service is per-circuit. It also exposes the current user's **background jobs**, read from
`BulkJobService`, so the display has one source of truth for "what is running for me".

### 5.2 The display - one component in the app frame

A single component rendered by `Components/Layout/MainLayout.razor`, inside the existing
`top-row`. Behaviour:

- Idle: renders nothing at all.
- One or more activities running: a slim determinate or indeterminate bar with the label; the
  count when the activity supplied one.
- More than one: a collapsed summary that expands to a list.
- An activity ends: a short-lived result line, success or failure, that the operator does not
  have to be on the originating page to see.
- Background jobs appear in the same list, marked as safe to leave, with a link to the Bulk
  Jobs page. Live-updated by subscribing to `BulkJobService.JobChanged` - this closes D5's
  "press Refresh yourself".

**The display must show only the current operator's jobs, and `GetActiveJobs()` is the wrong
API for it.** `BulkJobService.GetActiveJobs()` (`:179`) spans every module, and the comment
immediately below it (`:218-221`) records that a page built on it "both discloses and" reaches
across module boundaries - which is why `GetActiveJobsByModule` exists. The app frame is
rendered on every page for every operator, so it is the worst possible place to leak another
operator's or another module's work. Neither existing accessor is user-scoped: `BulkJob` carries
`SubmittedBy`, but no query filters on it. **S1 adds a user-scoped query** - repository method
plus service accessor - and the display uses only that. The admin-wide view stays where it is,
on the Bulk Jobs page, behind its existing authorization.

**Subscription contract for `JobChanged`, which the display must follow** (the two existing
subscribers already do - `AdminBulkJobs.razor:170/190/297`, `ConferenceRooms.razor:732/764/883`):

- marshal the handler onto the renderer with `await InvokeAsync(StateHasChanged)` - the event is
  raised from the job pump's thread, not the circuit's;
- guard against a disposed component before touching state;
- unsubscribe in `Dispose`. The display is a long-lived layout component, so a leaked handler
  here persists for the life of the process.

**Navigation progress is part of this component and is the first thing it does.** Driven from
`Components/App.razor` by the browser-side enhanced-navigation events, alongside the existing
`blazor:enhancedload` subscription at `:73`. The exact start-side event name must be confirmed
against the installed Blazor version in S1 and not assumed from this document.

This alone fixes D1 on all 36 pages with no page edits.

### 5.3 The navigation guard

When navigation is attempted while activities are live:

- **Foreground activity in flight** - modal: an operation is in progress; leaving will cancel
  it. **OK** cancels the token and navigates. **Cancel** stays on the page.
- **Only background jobs in flight** - modal: leaving is safe; the job continues; here is where
  to collect the result, with a link to the Bulk Jobs page. The operator is not blocked.
- **Both** - the foreground warning wins, and the message names the background job separately.

`Components/Shared/UnsavedChangesGuard.razor` is the existing precedent and it uses Blazor's
`<NavigationLock>` with `ConfirmExternalNavigation` plus an `OnLocationChanging` handler
(`:11`, `:26`). Its header records that before it, the app had no `beforeunload`,
`NavigationLock` or `OnLocationChanging` anywhere. Use the same mechanism; read that file before
writing a second one, and do not invent a third.

---

## 6. Constraints the implementation must respect

These are measured, not anticipated. Violating any one fails the build.

1. **`ClickGateTests.TheRegisteredLineCountStillMatchesTheFile`
   (`ExchangeAdminWeb.Tests/ClickGateTests.cs:1740`) asserts exact total line-count equality on
   12 pages.** Adding or removing one line in any of them fails. Each such edit requires
   re-anchoring per `.agents/playbooks/clickgate-reanchor.md`. Do not hand-compute an offset.
2. **491 line-keyed facts across those 12 pages** (`DomSyncedControl` 114, `ExcludedField` 120,
   `PostAwaitLiveRead` 92, `AnnotatedControl` 41, `ExemptControl` 37, and others) shift when a
   line is inserted above them.
3. **`SpinnerExpressionsAreNotSimplifiedIntoThePredicate`
   (`ClickGateTests.cs:1723`) matches 9 pages' spinner conditions verbatim.** Removing a
   module's own spinner - which this plan does by design - requires deleting its
   `SpinnerExpressions` entry in the same commit, with the reason recorded.
4. **`PageHandlerHygieneTests.Pages_DoNotFireAndForgetAsyncWork`
   (`ExchangeAdminWeb.Tests/PageHandlerHygieneTests.cs:15-31`)** is a zero-tolerance regex over
   all 36 pages. No `_ = Progress.Something(...)` anywhere.
5. **`EveryPageIsRegisteredOrDeclaredUnconverted` (`ClickGateTests.cs:55`)** - any new `.razor`
   page must be added to `ClickGateRegistry.Pages` or `NotYetConverted` with a reason.
6. **Any new control in the display component** (an expand toggle, a cancel button) must be
   registered in the click-gate census - `EveryClickableButtonConsultsAPredicateOrIsRegisteredExempt`
   (`:98`), `EveryDomSyncedControlIsRegisteredOrRecordedUngated` (`:564`).
7. **The jobs schema is append-only and version-gated** (`JobStoreMigrator.cs:19`,
   `TargetVersion => Migrations.Length`). A new column means a v2 step appended, never an edit.
8. **`BulkJobService` is `sealed` with no interface.** A progress abstraction cannot be
   introduced by swapping an implementation; compose, do not substitute.
9. **No bUnit.** Every UI guarantee here is a source-level scan or an owner's manual pass on a
   dev deploy. Put logic in services so it is testable; what stays in markup is not.
10. **Click-gating tiers 2-4 remain unapproved** (`ClickGateRegistry.cs:55-60`,
    `docs/ClickGatingAudit-Plan.md`). This plan is not a click-gating conversion and must not
    become one. Removing a spinner from an unconverted page is in scope; adding a gating
    predicate to it is not, and needs its own owner go.
11. **Constitution, unchanged and unaffected:** every mutating action still writes an audit
    event and still sends its `EmailService` notification. Progress display is additional and
    substitutes for neither. Moving an operation to the background runner must carry its audit
    and notification calls with it, not drop them.
12. **`OperationTraceService` is the existing backend start/step/complete channel**
    (`Services/OperationTraceService.cs`) and writes to JSONL, never to a browser. It is
    adopted by only 11 files. Do not conflate the two: this plan may emit a trace step
    alongside an activity, but the progress system is not a trace reader.

---

## 7. Slices

One slice per session. Each lands independently, builds green, and is committed on its own.

### S1 - The system, and navigation progress

The whole framework plus the single highest-value fix, with **no page edits**.

- `IActivityProgress` / `ActivityProgressService`, DI registration in `Program.cs`.
- The display component, rendered from `MainLayout.razor`.
- Enhanced-navigation start/end wiring in `App.razor`.
- A **user-scoped active-jobs query** on `BulkJobRepository` and `BulkJobService`, filtering on
  `SubmittedBy`, because the display must not use `GetActiveJobs()` (section 5.2).
- The display subscribes to `BulkJobService.JobChanged` under the contract in section 5.2, so
  background jobs appear live and D5's manual Refresh stops being the only way to see them.
- No module reports anything yet. Nothing in `Components/Pages/` is touched.

**S1's first task is to prove the navigation signal, before anything else is built.** The
end-side event is proven in this app (`App.razor:73` subscribes to `blazor:enhancedload`). The
start-side signal is **not** - the event name and its firing behaviour must be confirmed
empirically against the installed Blazor version on a dev deploy, not taken from this document
and not inferred from documentation. If no usable start-side event exists, the fallback is a
delegated click handler on the sidebar that raises the bar on navigation intent and clears it
on `blazor:enhancedload`. Design that fallback in S1 rather than discovering the need for it
later.

**Until that proof lands, S1 is not described as fixing D1.** The claim is conditional on the
signal, and the acceptance check in section 10 item 1 is what settles it. Base app version
bumps.

### S2 - The navigation guard and cancellation

- The guard, both popup flavours, built on or alongside `UnsavedChangesGuard`.
- `CancellationToken` threaded from `IActivityProgress` through to the handle.
- No module honours it yet; a module becomes cancellable when it adopts in S4+.

Fixes D4's mechanism. Base app version bumps.

### S3 - Comms-10k onto the background runner

Owner-directed. A new `IBulkJobProcessor`, registered in `BulkJobProcessorRegistry` and
`Program.cs`, satisfying `BulkJobProcessorWiringTests`.

**The shape mismatch must be resolved in this slice's design, before code.** The runner is
row-oriented: `IBulkJobProcessor.CountRows(job)` stamps a total once, and
`BulkJobRepository.RecordRow` recomputes progress by SQL aggregate per row, with each row
carrying `Success | Partial | Failed`. Comms-10k is not that shape. It is one staged operation -
resolve addresses, clear the `member` attribute, refill in batches, read back - where the list
is empty and then partial for several seconds, and which reports five outcomes for the
operation as a whole: succeeded; succeeded with unremovable primary-group members; partly
applied; could not confirm; refused before any change
(`docs/Comms10kBulkResolveScale-Plan.md` S4).

**One member is not one safe row and must not be modelled as one.** A per-member row would
report "9,998 of 10,000 succeeded" for a clear-then-refill that actually left the list broken,
which is exactly the dishonesty section 4's rule exists to prevent.

The slice must choose one and say which, with its reasoning, before implementing:

- **(a) Map stages to rows** - `CountRows` returns the stage count, each stage records one row,
  and the operation's five outcomes land in the job's terminal `Message`. No schema change.
  Progress is coarse ("step 3 of 5") but never lies.
- **(b) Extend the runner for staged jobs** - an appended v2 migration step adding a stage
  label and within-stage counts, so the refill can report "6,200 of 10,000 written" inside
  stage 3. Honest and finer-grained, but it changes shared job infrastructure and must respect
  `JobStoreMigrator`'s append-only rule and the recorded decision that only row-level progress
  is durable (`BulkJobModels.cs:110-111`).

Recommendation: **(a)**, because it needs no change to shared infrastructure and the owner's
requirement is that the operator knows it is running and how it ended - not that they watch a
member counter. Raise (b) only if (a) proves too coarse in acceptance.

Audit and `EmailService` notification move with the operation and must fire from the processor.

This answers the stakeholder's point 2 directly and makes the 10,000-member replace safe to
navigate away from.

Comms-10k module version bumps. No base app bump.

### S4 onward - module adoption, slowest first

One module per slice. For each:

1. Replace its own busy flag and spinner with `IActivityProgress` calls.
2. Delete its spinner markup and, where the page is click-gate-converted, its
   `SpinnerExpressions` registry entry.
3. Re-anchor `ClickGateRegistry` if the page's line count moved.
4. Honour the cancellation token, or move the work to the runner per section 4's rule.
5. Bump that module's version.

Candidate order, from measured slowness - to be confirmed against the code at the time, not
taken from this list:

Migration (`Get-MigrationUserStatistics`, twenty minutes or more by the page's own copy),
SelfServiceGroups (per-group DACL read over every candidate group), GroupManagement (bulk
add/remove with transitive protected-group resolution), ConferenceRooms, BitLockerRecovery
(live AD search), TrueLastLogon, DelegationReport (three nested permission loops per
recipient), ServiceHealth, DefenderEndpointDevices, MessageTrace.

**The queue does not have to finish before the system is useful.** S1 alone fixes the
commonest complaint. Adoption can stop and resume between other work.

---

## 8. Tests

No bUnit, so every assertion below is either a service-level unit test or a source scan. State
which, per test, when writing them.

- **Service, unit-testable:** an activity that is begun and never completed is reported as
  still running; `Dispose` without `Complete` records a failure, mirroring
  `OperationScope.Dispose`'s existing behaviour; an `Indeterminate` activity ignores `Report`;
  an `Items` activity never reports more than its total; cancelling a handle sets its token;
  concurrent activities on one circuit are listed independently.
- **Honesty rule, unit-testable:** there is no API that yields a percentage for an
  `Indeterminate` activity. Assert the type, not the behaviour - the guarantee must be
  structural.
- **Source scan:** no `.razor` under `Components/Pages/` renders a `spinner-border` once that
  page has adopted. This is the tripwire that stops module-owned spinners coming back. It must
  strip comments before matching - three guards in this repo have already failed by matching
  the prose that explained the antipattern rather than the code
  (`.agents/state.md`, 2026-09-30 session notes).
- **Source scan:** `MainLayout.razor` renders the display component.
- **Lifetime tripwire, source scan:** no type registered `AddSingleton` in `Program.cs` takes
  `IActivityProgress` as a constructor parameter. This is the guard for section 5.1's rule and
  it must exist before the first module adopts, not after. Strip comments before matching.
- **Scoping tripwire, source scan:** the display component does not reference
  `GetActiveJobs()`. Section 5.2's leak is silent at runtime and invisible to any functional
  test, so a structural guard is the only thing that catches it.
- **Subscription hygiene, source scan:** the display component implements `IDisposable` and
  unsubscribes from `JobChanged`.
- **Jobs:** the Comms-10k processor is mapped and constructible
  (`BulkJobProcessorWiringTests`), and its five outcomes survive the move to a job message.
- **Registry:** `ClickGateRegistry` line counts updated in the same commit as any page edit.

Every new test must be proven non-vacuous: revert the fix, watch it fail, restore, confirm green
(`.agents/repo-guidance.md`, Verification).

---

## 9. Verification

Per slice, before claiming completion:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

Suite baseline at `839831e` is 3487 passed / 0 failed / 3 skipped.

No PowerShell changes are expected; if any land, add `Invoke-ScriptAnalyzer -Path . -Recurse`
and `Invoke-Pester tests/ps`.

---

## 10. Acceptance - the owner's, on a dev deploy

Nothing in this repo can render a Blazor page. Every item below is a manual check and none of
it can be done by an agent.

1. Click a module in the sidebar. A progress indicator appears immediately, before the page
   content does. Repeat on a slow module (Service Health cold, Migration).
2. Start a short operation. The bar shows it, names it, and clears on completion with a visible
   result.
3. Start a short operation and navigate away mid-flight. The popup appears. **Cancel** keeps you
   on the page and the operation continues. **OK** navigates and the operation stops.
4. Start a Comms-10k replace. It appears as a background job. Navigate away freely. The popup
   says leaving is safe and tells you where to collect the result.
5. Let that job finish while on another module. You are told it finished, without visiting the
   Bulk Jobs page.
6. Force a failure. You are told it failed, with the reason, wherever you are.
7. Confirm no module shows its own spinner any more, for each adopted module.
8. **Two operators at once.** Have a second person start a job while you have one running. You
   see only your own, and they see only theirs. This is the check for review finding 2, and it
   cannot be caught any other way on this stack.

---

## 11. Risks

- **The line-keyed registry is the main source of accidental breakage.** Every page edit in
  S4+ risks it. Mitigation: S1 and S2 touch no pages at all, so the framework is proven before
  any page is opened.
- **Moving an operation to the background runner can drop its audit or notification call.**
  The Constitution requires both. Mitigation: S3's tests assert both still fire from the
  processor.
- **A cancellation token that is accepted and then ignored is worse than no cancel button**,
  because the popup will have promised the work stopped. Mitigation: a module is only described
  as cancellable once its token is honoured at a real await; until then its work goes to the
  runner instead.
- **Scope creep into click-gating.** The tiers are owner-gated. Mitigation: constraint 10.
- **The adoption queue is long and may stall part-finished**, leaving two feedback styles in
  the app at once. Accepted: S1 makes the app better immediately and the mixed state is
  strictly better than today's 36 styles.

---

## 12. Review

Approach review, 2026-10-01, unprimed (`.agents/playbooks/openreview.md`).

```text
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier-at-recorded-max
Harness: codex-cli 0.159.0, `codex exec`, sandbox read-only
Pins: base 839831e, head f854575
Capability proof: `git diff --stat 839831e..f854575` reproduced verbatim by the reviewer
```

The Portkey catalog on this host exposes exactly one model
(`@azure-openai-eus2-global/gpt-5.5-dzs`) whose declared `supported_reasoning_levels` top out
at `xhigh`; there is no `max` level to request, so `xhigh` is this harness's recorded ceiling.

**Verdict: "Acceptable with changes: the approach stands; identify required changes."**

The reviewer independently derived the same approach - per-circuit progress service, one layout
component, navigation progress at the shell, `NavigationLock`-style guarding, and long or
multi-item work moved into `BulkJobService` rather than made honest in page-local spinners.

Five required changes, all five verified against the code before being accepted, all five
applied in revision 2:

| # | Finding | Verified by | Applied in |
| --- | --- | --- | --- |
| 1 | A per-circuit progress service must not be injected into singleton services | `Program.cs:177` and 8 further `AddSingleton` module services | 5.1, section 8 tripwire |
| 2 | The layout display must not read `GetActiveJobs()`; it needs a user-scoped query | `BulkJobService.cs:179` and its own warning at `:218-221` | 5.2, S1, section 8 tripwire |
| 3 | The `JobChanged` subscription contract must be specified | existing subscribers at `AdminBulkJobs.razor:170/190/297`, `ConferenceRooms.razor:732/764/883` | 5.2, section 8 |
| 4 | S3 needs a Comms-10k job shape; the runner is row-oriented and the operation is not | `IBulkJobProcessor.CountRows`, `BulkJobRepository.RecordRow`, `BulkJobModels.cs:110-111` | S3, two named options with a recommendation |
| 5 | S1's "fixes D1 app-wide" must stay conditional until the start-side navigation signal is proven | `App.razor:73` proves only the end-side event | S1, with a named fallback |

Finding 1 is the most serious: it is silent at runtime, it cross-wires one operator's progress
into another's session, and nothing in the existing suite would have caught it.

No review has been run on revision 2.

## 13. Open questions for the owner

None blocking. S1 through S3 are fully specified by the clarifications in section 1.

To be raised when S4 begins, not before: whether a module whose only slow operation is a read
(Delegation Report, True Last Logon) should move to the background runner as well, or stay in
the page with a cancel. Section 4's rule says runner; the counter-argument is that a read has
no result worth persisting past the circuit. Do not decide this in advance of needing it.
