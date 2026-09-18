# Agent State

Current work and blockers only. Rules live in `docs/ProjectConstitution.md` and
`.agents/repo-guidance.md`, decisions in `.agents/decisions.md`, and machine observations in
`.agents/machines.md`. Each plan owns its implementation and manual acceptance checklist.
Superseded descriptions are verbatim in `docs/history/state-archive.md` (Archived 2026-09-15).

## Now

- **A WEEKEND BACKLOG RUN IS IN PROGRESS (started 2026-09-18). Read
  `.agents/decisions.md` 2026-09-18 "Weekend backlog run" before doing anything here.**
  The owner set the goal "work through as much of the queue.txt backlog as you can over the
  weekend... only stop if you cannot proceed without me" and settled three gates: plans
  self-approve after a **codex review loop to consensus** instead of an owner gate; priority
  across queue items is the working agent's call ("I don't give a fuck" - do not ask again);
  and pushes to both remotes are standing. Implementation subagents are authorized, which
  overrules the Token Budget one-slice-one-session rule **for this run only**. All of that
  scopes down when the run ends; the underlying subagent guidance conflict in Blockers is
  still unsettled for normal operation.
  Per-item state, all plans `Status: Draft` and none implemented:
  - **Queue 2, `status.cloud.microsoft`** - `docs/ServiceHealthPublicStatus-Plan.md` (`16ebdf4`).
    A machine-readable JSON API exists and answers unauthenticated; an iframe is impossible,
    not merely fragile (live response sends `X-Frame-Options: DENY`). **Codex round 1 returned
    `unsound`**: the endpoints come out of a minified SPA bundle and are undocumented, so they
    cannot be the primary source; also a deferred version bump, an unproven 5-second budget, a
    test seam that would make the guards vacuous, and a totality claim the catch does not
    prove. Revision in flight. **The owner's premise is confirmed but sharper than expected:**
    the public page carries one sentence per surface, not a second incident list, so it will
    never show an Exchange incident Graph is missing - it answers "are Microsoft's own admin
    surfaces up". That reframing is its open question 1 and may change whether it is worth
    building at all.
  - **Queue 4, message trace vs header analysis permissions** -
    `docs/MessageTracePermissionSplit-Plan.md` (`155eaf7`). Header analysis touches no Exchange,
    Graph or directory call (`HeaderAnalysisService.cs:1-3`, `:123`), so it is the low-privilege
    half; main permission keeps it, a new fail-closed granular gates trace. **Deploy hazard,
    stated first in the plan on purpose: a new alias denies EVERY operator including the owner
    until a group is stored against it, and the alias cannot be granted before the descriptor
    is deployed** - hence three commits with a mandatory deploy boundary. Codex review in
    flight.
  - **Queue 5, other tenants/domains** - `docs/MessageTraceMultiTenant-Plan.md` (`50e1724`).
    **May be zero work.** The cloud query passes no domain, organization or accepted-domain
    filter (`MessageTraceService.cs:434-450`); the only scoping is the session's `-Organization`
    (`ExoConnectionPool.cs:440-445`), which names a tenant, so extra accepted domains on the
    existing tenant are already covered. Slice 0 is one read-only search to turn that inference
    into a measurement. Separate tenants are real work whose hardest blocker is that per-tenant
    authorization has no expression in the current model: policies register once at startup
    from a static list, there is no `IAuthorizationPolicyProvider` anywhere, and invariant 7
    forbids naming a tenant in source. **Blocked on the owner:** which domains, and are they
    accepted domains or separate tenants.
  - **Queue 6, containerize** - `docs/Containerization-Feasibility.md` (`e45dc50`).
    **Answered: do not.** 13-17 sessions to reach what the existing installer reaches in 3-4.
    The blocker is the Delinea bootstrap credential living in the Windows **per-user**
    credential locker (`CredentialManagerService.cs:1,11-12`), which is also why the csproj
    carries an OS-versioned TFM and why both deploy paths set `loadUserProfile`. A container
    has no profile and the credential cannot be baked into an image without breaking credential
    isolation. Recommends hardening `tools/Install-ExchangeAdminWeb.ps1` instead; that plan is
    NOT written. **Needs an owner ruling.**
  - **Queue 8, Defender for Endpoint** - `docs/DefenderEndpointDevices-Plan.md` (`b12a7b1`).
    The deliverable the owner asked for by name is the app-registration requirement list.
    Microsoft Graph has **no** Defender device-inventory resource, so this uses the
    WindowsDefenderATP API. `Machine.Read.All` alone covers the list; discovery sources
    additionally need `ThreatHunting.Read.All`, which is tenant-wide and needs a **Privileged
    Role Administrator** to consent - that trade is its open question 1. "Can be onboarded" is
    the portal label; the API literal is `CanBeOnboarded`. **"Domain" is only partly
    obtainable** - no domain property on the machine resource, so AD domain/OU membership
    cannot be exported. Codex round 1 closed (3 findings); **round 2 returned `unsound`** on one
    surviving defect worth remembering: de-duplicating a `$skip` page sequence on device id
    removes overlaps but **cannot detect a gap**, so a short page reads as "collection ended"
    and a partial export looks complete. Round 3 revision in flight, replacing `$skip` with a
    positive-proof completion rule.
  - **Queue 9, click-gating** - slice 1 complete (see below); tier 1 page 1
    (`DhcpAuthorization`) in progress. Eight pages remain after it.

- **A live defect was found while scoping queue 4 and is NOT fixed. It needs its own commit.**
  `Components/Pages/MessageTraceReports.razor` carries only the main `MessageTrace` policy
  (`:4`, `:126`), and `Exports.GetExports()` (`:143`) calls
  `_jobs.GetFinishedByType(ModuleName, JobType, ListLimit)` with **no submitter filter** - the
  table renders `@item.SubmittedBy` per row (`:78`) precisely because it is an all-operators
  listing - while `Download` (`:146`) serves any listed export to any policy holder. So every
  operator can download every other operator's full message-trace detail exports. This is
  independent of the permission split; the split makes it worse by admitting header-only
  operators to that page. Not caused by this run's work.


- **A defect in committed TEST infrastructure, found in plan review and not yet fixed.**
  `ExchangeAdminWeb.Tests/ClickGateSource.cs:213-228`, `ExtractBlock`, counts `{` and `}`
  without being quote-aware, so a brace inside a string or interpolated string miscounts the
  depth and the extracted block ends in the wrong place - usually over-capturing into the code
  that follows. **The asymmetry is the tell:** `Tags()` in the same file IS quote-aware and its
  doc comment explains exactly why a naive scan breaks; `ExtractBlock` never got the same
  treatment. Consequence measured in the queue-4 review: an assertion looking for a `return`
  inside a denial block can find one that is actually in later code, so a handler that performs
  a protected operation after an authorization denial would pass. It is used by the live
  click-gating suite, so any assertion built on the block's END offset is suspect; assertions
  that only test containment within a generously-sized block are less affected. **Its own slice
  with its own guard proof** (an interpolated string containing a brace, placed inside an
  extracted block); deliberately not folded into another agent's work.
- **Branch `master`, working tree clean. Nothing is in flight.**
  The owner's issue queue is `C:\Users\mcoelho\Desktop\queue.txt` (machine-local, not in the
  repo, and not ours to write to). Queue items 1, 3 and 7 are landed and closed, with no open
  review findings; the owner has since added items 8 and 9. The button gate is closed out - the owner accepted it on 2026-09-18 ("seems to
  work well enough") and added the app-wide audit to their own queue themselves, so do not
  re-raise it here as an open item.
  **Both remotes are level with local at `3e19aef`**, verified with `git ls-remote` on
  2026-09-18; `origin` (LAN gitea) was reachable on that check, so the `SEC_E_CERT_EXPIRED`
  TLS failure seen earlier the same day was transient. This supersedes the earlier
  "local and unpushed, both remotes at `523b69d`" note. Re-verify with `git ls-remote` rather
  than trusting this line; push policy is unchanged (`.agents/push-policy.md`).

- **Service Health now shows its spinner on the first load. Closed, nothing outstanding.**
  `docs/ServiceHealthLoadFeedback-Plan.md` is Implemented. The owner deployed to dev, ran its
  manual acceptance checklist on 2026-09-18 and reported "this passes", which is the evidence
  of record for this item. Landed 2026-09-18 in `d1ed96e`; ServiceHealth module version 1.3.2,
  no base app bump. Queue item 7.
  **The complaint was never "there is no spinner"** - the page already had three, and they
  were all correct. Root cause: the page prerenders (`Program.cs:374`, no `prerender: false`
  anywhere in the app), prerendering emits no HTML until `OnInitializedAsync` completes, and
  that method awaited the whole Graph round trip. So the browser's first byte of the page was
  the finished board and no spinner could ever render. Worse, enhanced navigation
  (`Components/App.razor` listens for `blazor:enhancedload`) leaves the *previous* page
  rendered and interactive during the fetch, so the UI looked completely idle - hence the
  repeated clicks. Fix follows the `BlockedSenders.razor:166-181` precedent: the load moved to
  `OnAfterRenderAsync` behind a `firstRender && !loadStarted && authChecked` guard, with
  `StateHasChanged()` added to `LoadAsync`'s `finally` because Blazor does not auto-render
  after `OnAfterRenderAsync`. The two Graph collections also now run under `Task.WhenAll`
  instead of serially.
  **Resolved along the way, so nobody re-opens it:** the authorization round trip is NOT part
  of this problem. `GroupAuthorizationHandler.HandleRequirementAsync`
  (`Authorization/GroupAuthorizationHandler.cs:48-65`) is synchronous over in-memory state and
  returns `Task.CompletedTask` - no directory or network I/O - which is why the auth check
  stays in `OnInitializedAsync` here and in the precedent.
  Five source-level tripwires guard it, all stripping comments before matching. Guard proof:
  steps 1, 2 and 3 each mutated back independently, each 1 failed / 34 passed against its
  named tripwire, all restores byte-identical by SHA256. Full suite green at 2510 passed /
  0 failed / 3 skipped. Nothing here reaches the rendered
  page - no bUnit harness exists - which is exactly why the owner's dev-deploy pass is what
  closed it. The implementation codereview was never dispatched: the owner accepted the manual
  result and moved to the next item. Do not re-open it on your own.
  **Noted, not fixed:** because prerender and the interactive circuit each ran
  `OnInitializedAsync`, every Service Health view used to write *two* `ServiceHealthView`
  audit entries; this change incidentally drops it to one. Whether other pages duplicate their
  audit the same way is unexamined and unscoped.

- **Every control on Mailbox Migrations is gated on one in-flight predicate.**
  `docs/MigrationButtonGating-Plan.md` is Implemented. Landed 2026-09-17 in `00da11e` (the
  prerequisite `loadingBatchUsers` leak) and the commit on top of it; Migration module 1.9.0,
  no base app bump. The owner's rule - a control is clickable only when its click will
  definitively execute - is recorded as a general rule in `.agents/decisions.md` (2026-09-17).
  **It was applied to `Components/Pages/Migration.razor` only. Sweeping the rest of the app is
  an unscoped follow-up that nobody has approved** - do not treat other pages' ungated buttons
  as drift, and do not start the sweep without an explicit go.
  The page now has one `IsBusy` predicate over eight in-flight flags; 27 of 31 buttons consult
  it, and the four that do not are named with reasons in the tests. Staged-confirmation state is
  deliberately excluded: folding it in would have disabled Confirm at the only moment it renders,
  and no destructive action could ever be executed again - caught by the codex review of the
  plan, before any code, when all three then-proposed tests would have passed the broken shape.
  The tab strip is anchors, which ignore `disabled`, so the refusal lives in `SelectTab` /
  `SelectStatusTab`; the guard cannot move into `LoadMigrationStatus`, because
  `ExecuteBulkBatchAction` calls that to refresh the table while it is itself still busy.
  Seven source-level tripwires guard it (the plan specified six; the seventh enforces the tab
  guard, which would otherwise have shipped unenforced). Two of them initially failed against
  correct code because the scanners read the words "await" and "finally" out of the new
  explanatory comments - **a source scanner in this repo must strip comments before matching.**
  Guard proof: three representative controls (Delete row action, Show report, batch Details)
  each mutated back to their pre-gate form, each failing exactly
  `EveryButtonConsultsTheBusyPredicate`, 1 failed / 59 passed, restores byte-identical.
  Nothing here reaches the rendered page - no bUnit harness exists.

- **Mailbox Migrations no longer shows a stale open report; only the manual checks remain.**
  `docs/MigrationStaleReport-Plan.md` is Implemented and owns the acceptance checklist. Landed
  2026-09-17 in `3e4f13d`, with the review fix `2b93fdc` on top; Migration module version 1.8.2,
  no base app bump.
  The earlier batch-name-caching hypothesis was FALSIFIED by reading the page: the report was
  never keyed on batch name, and that fix would not have touched the reported repro, which
  reuses the name. Actual cause: `userReport` is a one-time snapshot, the render guard at
  `Migration.razor:771` keys on the email address alone, and seven paths reloaded or discarded
  `batchUsers` without clearing it. One `CloseUserReport()` helper now owns the clear at all
  seven, and a `reportGeneration` counter drops the result of a fetch a reload superseded.
  Ten source-level tripwires guard it, anchored per branch; all three guard-proof mutations
  bit. Nothing here reaches the rendered page - no bUnit harness exists - so the plan's manual
  checks are the only evidence that the operator sees the fix. **Owner 2026-09-17: those
  checks are deferred indefinitely, not skipped** - migrations are production actions, and the
  owner will verify opportunistically the next time a real migration comes up. Do not re-queue
  them as blocking work and do not treat their absence as drift. **The implementation codereview is done** -
  codex, `@azure-openai-eus2-global/gpt-5.5-dzs` at xhigh, standard tier, over
  `1250220..3e4f13d`, verdict **findings (1)**, 2026-09-17, capability proof passed. Four of the five things it was
  asked about came back clean and stand as reviewed - no eighth reload path, the
  generation guard correct on all three continuation paths, no half-populated field
  combination, and the module-only bump right. The fifth produced **`msr-1`** (MEDIUM,
  `.agents/review/findings/msr-1.md`), **fixed 2026-09-17 in `2b93fdc`**: closing the report
  before the await was not enough, because the refresh-in-place paths leave the old rows and
  their **Report** button rendered for the whole Exchange call. One helper,
  `ReplaceBatchUsers`, now owns every assignment of `batchUsers` and closes the report
  itself, so the close lands after the await. **The lesson worth keeping is about the tests,
  not the code:** the original ten tripwires asserted that the close precedes the refetch,
  which the defective code did, so they passed the bug - the guard proof's first mutation is
  literally the pre-fix code and only the three new tripwires caught it. Assert the negative
  per occurrence (nothing assigns `batchUsers` outside the helper), not the ordering per
  method.

- **Module ordering is alphabetical and `SortOrder` is gone; the sidebar check is unrun.**
  `docs/AlphabeticalModuleOrdering-Plan.md` is Implemented and owns the manual acceptance
  checklist. Landed 2026-09-15 in `e5a8ccd`, `73be51c`, `7057f9c`, `12b699f`, `5090996`,
  `b657ac0`; app version 2.21.0. Every catalog-driven list now orders by `DisplayName` with
  `StringComparer.OrdinalIgnoreCase`, nav categories are named from `Modules/ModuleCategories.cs`,
  and the bottom sidebar block is selected by `ModuleCategories.Administration` rather than the
  old integer threshold. A new module declares no position: the author picks a category and the
  name decides the order. Blazor markup has no test harness here, so the rendered sidebar,
  Module Config tree and home tiles are unverified by automation - run the plan's manual checks
  after a dev deploy. The plan's implementation codereview has not been dispatched.

- **CloudPasswordReset: the owner is populating `employeeID` on Entra CLD accounts (2026-09-15).**
  This replaces heuristic name/alias matching with a direct key: the CLD account's `employeeID`
  matches the AD employee record's employee identifier, and that record supplies the destination
  mailbox. The owner directs reopening module work on this basis. The name/alias/legacy-suffix
  rules proposed in `.agents/research/cloud-password-owner-runtime.md` are consequently candidates
  for demotion to fallback or deletion, and the 89/82 split from the old experiment is superseded
  as a coverage claim once enrollment completes. Open: enrollment is in progress, not complete, so
  the resolver's behavior for an unpopulated `employeeID` (refuse vs fall back) is undecided, as is
  whether `employeeID` alone is sufficient without a corroborating name check. Implementation
  remains on hold pending an updated plan.

- **CloudPasswordReset: runtime owner investigation reopened; implementation remains on hold.**
  The owner requested completion of the runtime-association investigation and authorized the
  M365Connections/PTK connection. Scope is individually owned employee CLD accounts. L2 alone
  uses the app; ServiceNow and the employee do not interact with it. No advance enrollment,
  maintained owner map, or operator-supplied delivery address. Requirements are recorded in
  `.agents/decisions.md` (2026-09-14); current evidence, proposed rules and remaining validation
  are canonical in `.agents/research/cloud-password-owner-runtime.md`. Inspect the local
  diagnostic receipt listed in `.agents/machines.md`, independently classify unresolved
  employee candidates, then approve an updated plan before implementation. The old held
  `docs/CloudPasswordReset-Plan.md` still describes operator-entered delivery and is not the
  current proposed solution. No module code or new permissions have shipped. The abandoned
  tooling remains deleted; its earlier survey approval is not standing query authority.
  D4 remains unresolved. Survey-data disposition and unnecessary survey-registration grants
  remain open outside module implementation; see Blockers and `.agents/machines.md`.

- **Service Health is implemented; design acceptance and manual checks remain.**
  `docs/ServiceHealth-Plan.md` owns the design and checklist. The original dashboard appearance
  is binding: no redesign, charts or gradients; incident HTML must pass through the sanitizer.
  Enablement, Graph secret ID and section access are now set; secret validity and live Graph
  authentication were not checked. Implementation codereview remains outstanding and needs a go.

- **Shared config cutover is reflected in both deployed appsettings files.**
  `docs/SharedConfigDb-Plan.md` is Implemented and its implementation codereview is closed.
  Do not repeat the one-time cutover based on the old state. Section 8 manual checks (startup
  logs and cross-instance refresh) remain unrecorded. Section 9's decision-wording and
  Constitution flags remain open. Jobs and usage stores stay per instance.

- **Usage telemetry and the save-then-prompt fix are landed.**
  `docs/UsageTelemetry-Plan.md` owns the telemetry implementation and acceptance checklist;
  `.agents/review/findings/utei-{1,2,3,4}.md` own the closed findings. The forced-reload fix is
  guarded by `AdminPageDirtyStateTests.ClearingDirtyStateIsRenderedBeforeAForcedReload`.
  Nothing is queued code-side. Run telemetry's manual checks and confirm saving module
  enablement no longer asks to discard already-saved changes.

- **Group bulk actions and cross-domain fixes are landed; live writes remain unverified.**
  `docs/GroupBulkActions-Plan.md` owns the checklist. Validate remove/re-add and bulk operations
  on a throwaway group: the original admin group is protected and cannot serve as the repro.
  Self-service's home-domain users-only add scope remains intentional. D1 used its drafted
  default (one batch administrator email, per-member audit and affected-user notices);
  the owner may overrule it. Implementation codereview remains outstanding and needs a go.

- **Intune Devices and Risky Users are implemented; remaining manual checks stay live.**
  `docs/IntuneDeviceManagement-Plan.md` and `docs/RiskyUsersModule-Plan.md` own scope and checks.
  Both registrations were proven live in owner validation recorded 2026-09-02; shared-store
  configuration remains present in the dated machine receipt. The owner confirmed Intune
  search on dev 2026-09-03 after `acfabe9`, superseding the earlier combined-filter uncertainty.
  Intune notification/Entra removal are act-time choices, not Module Config defaults.
  Risky Users reads audit without alert emails. Its direct ServiceNow validation, instead of
  the newer per-module seam, remains an unscheduled judgment call, not an admitted defect.

- **The remaining implemented queue awaits manual acceptance only. Do not restart it.**
  Checklists: `docs/BooleanConfigControls-Plan.md`, `docs/BitLockerMandatoryTicket-Plan.md`,
  `docs/ModuleCsvExport-Plan.md`, and `docs/EventLogCsvTicket-Plan.md`. The sidebar Home link
  removal (`2128610`) also needs a visual check; the brand link stays. BitLocker CSV includes
  keys under the owner's ruling, with ticketed disclosure audit and no keys in audit logs.

- **Nesting and protected group targets are implemented; remaining checks stay live.**
  `docs/GroupMemberNesting-Plan.md` and `docs/ProtectedGroupWriteTarget-Plan.md` own the lists.
  Cross-domain member listing was confirmed in both group modules on dev 2026-08-31;
  nested add/remove, cross-domain picker behavior and admin refusal still need applicable
  checks. Self-service is exempt from Protected Group Targets by the 2026-08-31 ruling;
  member protection stays. Review loops are closed; pgwt-3 remains declined at intake
  (`.agents/review/pgwt-3.contested.md`).

## Next

- **Queue 9's click-gating sweep is APPROVED IN PART and in progress.**
  `docs/ClickGatingAudit-Plan.md` owns the findings, tiering, effort estimate and acceptance
  checklist. **Owner approved 2026-09-18: slice 0 + slice 1 + tier 1 only** - the scanner and
  shared test harness, the two prerequisite stuck-flag fixes, and the nine pages that can execute
  a destructive AD/Exchange/Graph write. **Tiers 2, 3 and 4 are NOT approved**; they are
  re-decided once the shared harness makes the per-page cost a measurement rather than an
  estimate. Do not exceed that scope.
  Audit headline: 273 clickable buttons across 33 pages, **205 across 30 pages not gated against
  concurrent work**, and only 3 pages have any page-level busy predicate (Migration's `IsBusy`,
  done; `IntuneDevices` and `RiskyUsers` have `ActionsDisabled`, both incomplete).
  **Slice 0 is COMPLETE.** Part A (`f0658c0`): `tools/Get-ClickGateAudit.ps1` + 14 Pester tests;
  the tool reproduces the plan's published totals exactly and is calibrated against Migration
  (8 flags, `IsBusy`, exemptions at 223/442/784/836, anchors at 30/33/36). Part B (`8c6596b`):
  `ClickGateSource.cs`, `ClickGateRegistry.cs`, `ClickGateTests.cs` - 14 shared assertions over a
  per-page registry, seeded with **Migration only** (the one page whose gating is known-good;
  registering the partial IntuneDevices/RiskyUsers predicates would have shipped a red suite).
  The self-enforcing property is `EveryPageIsRegisteredOrDeclaredUnconverted`: a new page is in
  neither `Pages` nor `NotYetConverted`, so the suite fails and names it.
  **The owner enabled ultracode for that session**, which supersedes the Token Budget rule
  against orchestrating subagents for its duration; recon ran as a read-only workflow.

- **SLICE 1 IS COMPLETE. Tier 1 is unblocked; the next item is page 1, `DhcpAuthorization`.**
  Both prerequisite stuck flags are fixed. Neither page was converted - slice 1 only removed the
  hazard that would have turned a dead button into a dead page - and both stay in
  `NotYetConverted`, BlockedSenders as tier 3 and MessageTrace as tier 4, neither approved.
  - **`BlockedSenders.razor` `ConfirmUnblock`** - landed 2026-09-18 in `e915476`; module
    1.4.0 -> 1.4.1. The preflight (authorization round trip, protection gate, `BeginOperation`)
    now runs inside one try whose catch converts the throw into a `PermissionResult.Fail`,
    audits an `UnblockSender_Denied` row and **returns** - fail closed, so an unreadable
    authorization answer can never become an unauthorized Exchange write. Six tripwires; three
    mutations, each failing exactly its named assertion. **The `return` is additionally
    compiler-enforced:** dropping it is CS0165 on the unassigned `gate`/`scope` locals.
    **It is deliberately NOT a try/finally** - that moves the `isLoading` clear after the
    `LoadBlockedSenders` refresh, which a busy guard in that callee would then silently no-op.
    Note for whoever converts this page: `OnAfterRenderAsync` calls `LoadBlockedSenders` at 180
    while `isLoading` is ALREADY true from 170, so a guard inside `LoadBlockedSenders` kills the
    initial load outright - `loadStarted` is latched at 179 and never retries.
  - **`MessageTrace.razor` `ToggleDetail`** - landed 2026-09-18; module 1.4.1 -> 1.4.2. Now a
    **token-guarded finally**. The guard inside the finally is load-bearing: a bare clear would
    let a superseded fetch lower the NEWER request's flag and re-enable the button mid-flight.
    Four tripwires, the negative one per occurrence per the msr-1 lesson (every
    `detailLoading = false;` in the method must fall inside the finally block; no ordering
    assertion). Four mutations, all real test failures rather than build failures.
  - **Open, not fixed, and a separate finding needing its own commit:** the unwrapped
    `Audit.LogLookupAction` calls inside catch blocks are the house idiom on `MessageTrace`
    (six sites) and on `AnalyzeHeadersAsync`. The new finally lowers the flag on the way out,
    but the throw still escapes the handler and, with no `ErrorBoundary` in this app, takes the
    circuit. Same class as the `BlockedSenderService.UnblockSenderAsync` timeout gap.

- **The click-gating design was falsified by reconnaissance and re-scoped; work is unblocked.**
  An 11-page read-only recon (34 agents, no errors) over slice 1 + tier 1 landed 2026-09-18 and
  is recorded as **Revision 1 in `docs/ClickGatingAudit-Plan.md`** - read that before touching
  any page. The audit's counts stand; its design and estimate do not. **Owner settled the
  re-scope on 2026-09-18: option A, all nine pages at full depth, 20-29 sessions plus 2-3
  dev-deploy passes** (`.agents/decisions.md` 2026-09-18, which also amends points 1 and 4 of
  the 2026-09-17 rule). Tiers 2-4 remain unapproved. Work order: slice 0B, slice 1, then the
  nine pages in Revision 1's revised order.
  The four falsifications that matter most, all verified against source:
  1. **There is no `ErrorBoundary` anywhere in the app** (zero matches in `Components/`,
     `Services/`, `Program.cs`). A throw from a handler tears the circuit down, so it does not
     leave a live page with a stuck flag - which means the planned "lower every flag in a
     `finally`" assertion **does not close the risk it was written for**. The real page-deadener
     is a call with no timeout or cancellation token; `BlockedSenderService.UnblockSenderAsync`
     (`Services/BlockedSenderService.cs:48`) is the confirmed live example.
  2. **A handler guard on a DOM-synced control corrupts data** - the plan prescribed exactly
     that for non-button targets. The field is unchanged, the render diff emits no correction,
     the browser keeps the operator's action. On `NamedLocations` it writes the OLD country set
     to Graph permanently, because `CountryCodePicker` latches `_initialized` (45, 49-56).
     Three refusal mechanisms are needed where the plan had one.
  3. **The scanner cannot see the controls that matter.** Its "7 non-button targets app-wide" is
     an `@onclick`-only count; `@onchange`, `InputFile`, `@onkeydown`, `@bind` and child
     `Disabled=` are invisible. Across these 11 pages alone the real number is about **35**, all
     needing hand enumeration with nothing detecting an omission.
  4. **A page-wide predicate is provably wrong on `SelfServiceGroups`** (two mutually exclusive
     views; the only adversarial verdict that came back "unsound") **and `ConferenceRooms`**
     (background `OnJobChanged` callback owns the jobs panel). The registry needs a list of
     scoped predicates, not one name.
  Revised estimate for the approved scope: **20-29 sessions + 2-3 owner passes**, up from 8-11.
  Unsettled and worth an empirical check on a dev deploy: whether Blazor Server can interleave
  event callbacks across an `await`. The plan proceeds on the labelled assumption that it can,
  and stays robust either way (attribute plus in-handler guard).

- **Two live defects found by that recon, neither caused nor fixed by the gating work. Not
  scheduled; they need an owner go.**
  1. **`IntuneDevices.razor:403`** - the `WipeNameConfirmed` clause is the **only** enforcement
     of the typed device-name confirmation on a factory wipe anywhere in the codebase.
     `ExecuteActionAsync` re-checks authorization, ticket and protected principal but never
     re-reads `wipeConfirmName`, so the second key on the app's most destructive action is UI
     only. A mechanical predicate rewrite of that line would delete it outright.
  2. **`Services/BlockedSenderService.cs:48`** - `UnblockSenderAsync` takes no
     `CancellationToken` and the file has no timeout, so an Exchange Online stall deadens the
     Blocked Senders page with no recovery but a reload.
  **Two live stuck-flag defects were found and confirmed by reading the source, independent of
  the sweep:** `BlockedSenders.razor:278` (`isLoading` raised, cleared only post-await at :305,
  :336, :406, never in a `finally`; the exposure is the uncaught `AuthorizeAsync` at :290-291,
  since the service and notification calls are individually caught) and
  `MessageTrace.razor:976` (`detailLoading`, same shape). These are prerequisites: widening a
  flag that can stick into a page-wide gate turns a dead button into a dead page.
  The audit scanner is **not yet in the repo** - it lives only in a temp file, so its algorithm
  is written into the plan and committing it as `tools/Get-ClickGateAudit.ps1` with Pester
  coverage is slice 0. Known false positives are disclosed there too. Nothing here reaches the
  rendered page; no bUnit harness exists.

- **Superseded by the entry above: next agreed item was queue 9, the app-wide click-gating audit.** Picked 2026-09-18 when the
  owner closed item 7 and said "pick next item from the updated list"; they did not name one,
  so this is the working agent's pick and the owner may override it. Reasons it was chosen
  over 8, 4, 5, 6 and 2: the rule is already settled and written down, the Migration work is
  the worked precedent, it needs no new credential or app registration, and its deliverable is
  an audit and an effort estimate rather than code - so it sizes the rest of the queue. Item 8
  is the bigger prize but opens on a blocker only the owner can clear (a new app registration),
  so surface its permissions ask early if 9 stalls. Item 2 stays blocked on a decision.
  That audit is now delivered as `docs/ClickGatingAudit-Plan.md`; the paragraph above is kept
  only for the reasoning behind picking 9 over 8, 4, 5, 6 and 2. **First action for the next
  session: wait for the owner's scope answer on that plan. Do not start fixing.**

- **Owner-reported issues, raised 2026-09-15. Issues 1, 3 and 7 are closed; 2 is not started.**
  1. *Licensing Updates sat in the wrong nav category.* Landed 2026-09-15 as `7d4b976`: it is
     now `Category = ModuleCategories.IdentityAndAccess`, module version 1.1.1. Category is nav
     grouping only - section-access keys are per-module policy aliases - so the move did not
     touch authorization. Closed.
  2. *Service Health misses `status.cloud.microsoft`.* Microsoft splits its status reporting;
     the module currently reads only the Graph service-health source. Needs a decision on how
     that second source is obtained (no documented Graph equivalent is established) before any
     design. Interacts with `docs/ServiceHealth-Plan.md`, whose appearance is binding.
     Blocked on that decision, which is why item 7 was taken first.
  3. *Mailbox Migrations shows a stale open report.* Landed 2026-09-17 as `3e4f13d`, reviewed,
     and its one finding fixed in `2b93fdc`; see the Now entry above and
     `docs/MigrationStaleReport-Plan.md`. Code-side closed; only the plan's owner-deferred
     manual acceptance checklist remains.

- **Queue items not yet started, in the owner's own words.** Numbering is the queue's.
  4. *Break out permissions for message trace vs header analysis.* Two capabilities behind one
     policy alias today. Self-contained; the obvious alternative to item 7 if that stalls.
  5. *Explore adding other owned tenants/domains to message trace.* Exploratory; scope and
     credential model are both undefined.
  6. *Containerize the app so it can be deployed elsewhere rapidly.* The owner's own note asks
     whether Docker works with IIS. Large; interacts with the whole deploy pipeline and the
     shared-config-DB invariant. Do not start without a ruling.
  7. *O365 status module is slow to load, inviting repeated clicks or refreshes; it needs to be
     obvious when loading.* **Landed 2026-09-18 in `d1ed96e` and closed** - the owner ran the
     manual acceptance checklist on a dev deploy and it passed. See the Now entry and
     `docs/ServiceHealthLoadFeedback-Plan.md`. Nothing outstanding.
  8. *New module for Microsoft Defender for Endpoint.* List and export all devices; the
     specific ask is every Windows device in the "Can be onboarded" state, with discovery
     sources, IP, domain, OS and other identifying detail, exportable. **The owner's own note
     says it needs a new app registration and asks to be told the permissions and requirements
     up front** - that ask is the first deliverable, and it blocks any code, because the app
     registration is the owner's to create. Largest item in the queue.
  9. *Audit the app for clicks allowed when the system is not ready to process them*, the class
     fixed in Mailbox Migrations, then plan to fix all of them and give the owner an idea of
     the effort. This is the app-wide sweep that state.md has twice said needs an explicit go;
     the owner queueing it is that go, but the deliverable they asked for is an audit plus a
     plan with an effort estimate, **not** a code sweep. The rule it applies is already
     recorded: `.agents/decisions.md` 2026-09-17, "a control is clickable only when its click
     will definitively execute". `docs/MigrationButtonGating-Plan.md` is the worked precedent.
     **The audit and estimate are delivered** (2026-09-18, `docs/ClickGatingAudit-Plan.md`,
     Draft); the item stays open pending the owner's answer on how much of it to approve.

- **Manual validation is outstanding operational work.** Start with
  `docs/DevValidation-2.3.34.md`: Admin Settings access, protected-user alias refusal and the
  reported MailboxPermissions friction are high-consequence unverified behavior. It owns older
  streams' checks; newer plans above own their own. Both instances address live production
  AD/Exchange, so manual writes still need authority for their named scope.
- **Coverage follow-up:** the 2026-08-14 ruling supersedes the standalone "one of three files
  done" task. `ProtectedPrincipalService` work was folded into the now-landed queued plans;
  re-measure on green CI and ratchet per `.agents/review/coverage-floor.txt`.
  `PermissionValidator` still needs its own approved plan for the credential-carrying I/O seam.
  Old percentages are dropped; the floor file owns the measured baseline.
- **Owner-deferred validation stays deferred:** Bulk Job Runner live UI and writes,
  ConferenceRooms protection, GM-3 self-service groups, and servicer override end-to-end proof,
  inverse denial and per-target batch notes. Sources: `docs/BulkJobRunner-Plan.md`,
  `docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md`,
  `docs/SelfServiceGroupManagement-Plan.md`, and the archived 2026-08-11 servicer record.
  The owner accepted waiting for real production servicer use; do not re-queue it as urgent.
- **Module packaging/import stays deferred** (owner 2026-07-22, `.agents/decisions.md`).
  **AccountLockoutRemediation and its notification question stay parked** with the disabled
  module; disablement is re-verified in `.agents/machines.md`. No resumption authorized.

## Blockers

- **Falsified deployment/configuration blockers, checked 2026-09-14 as of `a16c316`:**
  the old dev/prod versions, incomplete shared cutover and missing initial ServiceHealth,
  RiskyUsers and IntuneDevices configuration disagree with the host. Evidence is canonical in
  `.agents/machines.md`. Manual acceptance and exact deployed module versions remain unverified.
- **SQLite upgrade is no longer blocked by package availability.** The 2026-06-26 decision's
  "no patched package exists" basis is falsified, checked 2026-09-14 as of `a16c316`:
  local restore still resolves `SQLitePCLRaw.lib.e_sqlite3/2.1.11`, while
  [NuGet publishes newer builds](https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3),
  including 3.53.3 (the package version identifies its SQLite engine).
  The [advisory](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) still lists affected versions
  through 2.1.11 and its patched-version field says None. Compatibility is not established.
  Next action: plan and verify a supported update; do not suppress NU1903.
- **CloudPasswordReset residuals:** owner disposition of remaining survey export(s); current
  inventory differs from the old three-file claim (machine receipt). Survey registration
  `16866221-3e2b-4ea5-8aae-157b043b6d7c` was recorded as holding unnecessary
  `Directory.ReadWrite.All`, `User.ReadWrite.All`, `Group.ReadWrite.All` and
  `UserAuthenticationMethod.ReadWrite.All` grants. Consent was not re-queried under the hold;
  revocation needs separate authority.
- **Unsettled guidance conflicts:** the 2026-09-01 owner ruling in the archived queue permits
  one session for all slices with coding subagents; `.agents/repo-guidance.md` Token Budget
  and the 2026-08-27 decision still require a fresh Fable session per slice. Also, the 2026-07-31
  protected-principal input-validation rationale relies on this deployment's read visibility,
  while the 2026-09-11 rule rejects environmental safety assumptions. No contested rule changed;
  owner reconciliation is required.
- **SharedConfigDb record flags:** the decision says SQLite `data_version` / next read;
  implementation uses the store's change token with a throttle. The additive-only rule has
  operative homes in the decision, guidance and test but is absent from the Constitution.
  Evidence: `docs/SharedConfigDb-Plan.md` section 9. No ruling inferred.
- **Plan-status drift remains:** `docs/BlockedSendersLoadTiming-Plan.md` and
  `docs/Comms10kReplaceUx-Plan.md` still say Approved;
  `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md` says Approved / In progress as of `a16c316`.
  Implementation was recorded but full completion is unverified. Do not silently relabel them.
  `docs/AdminUIRedesign-Plan.md` remains In progress with manual checks.
- **Unscheduled M365 protection gap:** group update/delete and owner adds were recorded as
  ungated, and protection configuration cannot identify a cloud-only group. Update/delete
  still lack a check in `Services/M365GroupManagementService.cs` as of `a16c316`.
  The owner excluded this module from the on-prem target work; no work approved.
- **Older questions:** `docs/MessageTraceNullRow-Plan.md` needs a live rerun; the upstream
  null-row cause remains undiagnosed (OQ-1). `docs/ProtectedPrincipalResolution-Plan.md`
  retains OQ-2 on whether the reported cloud-only mailbox targets should remain reachable.
  Alias-protection and MailboxPermissions fixes are not field-validated by deployment alone.
- **Ops gaps:** ConferenceRooms AD secret configuration on prod was previously outstanding
  and was not checked here. `deploy.ps1` still lacks native `-PlanOnly`; the pipeline dry-run
  is the recorded workaround. Reviewer sandbox capability was not re-probed; use dated
  machine notes, not superseded CLI-version assumptions.

## Verification

Commands and mandatory guards are owned by `.agents/repo-guidance.md` and `AGENTS.md`.
Last run 2026-09-18 for the Service Health load-feedback slice: build Release (0 errors),
`dotnet test ExchangeAdminWeb.slnx` (2510 passed / 0 failed / 3 skipped), `dotnet format
--verify-no-changes` and `git diff --check HEAD`, all clean. No PowerShell was touched, so
PSScriptAnalyzer and Pester were not run. Browser acceptance, AD/Graph queries and reviewer
dispatches were not run. Current CI status/test counts do not
belong here. Per-finding status is owned by `.agents/review/index.md`.

## Active sources

- `AGENTS.md`, `.agents/repo-guidance.md`, and `docs/ProjectConstitution.md` govern work.
- `.agents/decisions.md` owns decisions; `.agents/machines.md` owns host facts.
- Referenced plans own scope, status and acceptance checklists.
- `Modules/ModuleCatalog.cs` owns module versions and enumeration;
  `ExchangeAdminWeb.csproj` owns the current base app version.
