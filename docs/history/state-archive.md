# State Archive

Landed and superseded `## Now` entries rotated out of `.agents/state.md` by the
state-hygiene sweep, kept verbatim for history. Newest first. The sweep is
`.agents/playbooks/drift.md`; the older sections below were written when `catchup`
still ran it inline.

> **Terminology correction (2026-07-28):** the archived text below repeatedly says
> "manual-validation-on-dev / no dev tenant." That wording is wrong and is retained only
> as verbatim history. There is no separate dev tenant by design: both the dev and prod
> instances run on this server and connect to the same live PROD AD/Exchange. The dev
> deploy proves the app loads; functional validation is done against real PROD data and
> can be run from the dev instance. Read every "no dev tenant" below as "live validation
> not yet performed."

## Archived 2026-09-23 (drift sweep)

Preserved verbatim. Rotated out of `.agents/state.md` because each entry was completed,
explicitly superseded, or falsified by current evidence. What is still live has a home
elsewhere and is named here:

- **Access tab labels** landed, deployed and verified; the module-version history is in the
  `ModuleCatalog.cs` comment on the `MessageTrace` descriptor. The one rule it carried that had
  no other home - the aliases must never be renamed - is kept live in `.agents/state.md`.
- **Branch/queue status** entry: the queue.txt path moved to `.agents/machines.md`; the push
  status in it was deleted rather than rotated, per the 2026-07-11 ruling.
- **Service Health load feedback** and **Migration button gating** are both Implemented and
  owner-accepted; the gating rule itself is `.agents/decisions.md` 2026-09-17.
- **Both CloudPasswordReset hold/investigation entries** are falsified: the module is built and
  reviewed (`docs/CloudPasswordReset-Plan.md`, `Status: Implemented`), which the entries below
  say had not happened. Residuals stay in `## Blockers`.
- **The three `## Next` entries** were a superseded pick rationale, a set of owner-reported
  issues with 1/3/7 closed and 2 living in the weekend-run entry, and a queue enumeration the
  owner's own `queue.txt` owns and whose "not yet started" header was false for four of six
  items.

- **ACCESS TAB LABELS: DONE, `bdd01a8`, DEPLOYED AND VERIFIED BY THE OWNER 2026-09-22.** Owner ruling 2026-09-22: *"the access names
  are stupid. 'MessageTrace' vs 'MessageTraceSearch'? trace is what we're gating. header analysis
  vs trace. name them more clearly in the UI."* `ModulePermission` gained an optional `DisplayName`
  defaulting to null; `Components/Pages/ModuleConfig.razor` heads each grant with it and keeps the
  alias beside it in muted text. `MessageTrace` declares "Header Analysis" and "Trace Search";
  every other module declares none and renders exactly as before, pinned by its own test.
  **The aliases were NOT renamed and must not be** - each is the section-access storage key and the
  store is fail-closed, so a rename orphans the groups granted against it and denies them rather
  than failing open. The alias stays on screen for the same reason it stays in the code: it is what
  the denial log line names.
  Both bumps fired per the Constitution's two rules: base app `2.21.1` -> `2.22.0` (shared record
  and config page), module `1.5.1` -> `1.5.2`. Suite 2954/0/3; two mutations, each failing only its
  own test. Constitution "Planning Rules" puts UI polish outside the written-plan requirement, so
  no plan was written.

- **Branch `master`, working tree clean. Nothing is in flight.**
  The owner's issue queue is `C:\Users\mcoelho\Desktop\queue.txt` (machine-local, not in the
  repo, and not ours to write to). Queue items 1, 3 and 7 are landed and closed, with no open
  review findings; the owner has since added items 8 and 9. The button gate is closed out - the owner accepted it on 2026-09-18 ("seems to
  work well enough") and added the app-wide audit to their own queue themselves, so do not
  re-raise it here as an open item.
  **Both remotes are level with local at `6f3ee22`**, verified with `git ls-remote` on
  2026-09-22 after the owner ran `pushall`; `origin` (LAN gitea) was reachable, so the
  `SEC_E_CERT_EXPIRED` TLS failure seen on 2026-09-18 remains transient rather than a standing
  fault. Supersedes the `3e19aef` note. Re-verify with `git ls-remote` rather than trusting this
  line; push policy is unchanged (`.agents/push-policy.md`).

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

## Archived 2026-09-15 (drift sweep)

Preserved verbatim. The entry below recorded a completed records correction whose durable
content now has canonical homes: the evidence in `.agents/machines.md` (ASHBIAMWEB1 / Deploy
and the dated deployment receipt), and the still-open caveats in `.agents/state.md`
`## Blockers` under "Falsified deployment/configuration blockers". Nothing is closed by this
rotation.

- **Deployment/configuration basis corrected 2026-09-14, as of `a16c316`.**
  `.agents/machines.md` (ASHBIAMWEB1 / Deploy) owns the verified deployed base versions, shared
  database path and configuration observations. Old deployment and initial Graph-configuration
  blockers are falsified. Assembly metadata and database rows do not prove live authentication,
  page appearance or manual acceptance. Exact deployed module versions were not checked.

## Archived 2026-09-14 (drift sweep)

Superseded descriptions below are preserved verbatim. Deployment and configuration claims
were falsified by the dated ASHBIAMWEB1 probes in .agents/machines.md. Streams with pending
manual checks remain live in .agents/state.md; this archive does not close those checks.

- **SERVICE HEALTH MODULE: 1.3.1 IMPLEMENTED 2026-09-09, NOT YET DEPLOYED, NOT CONFIGURED.**
  Module `ServiceHealth` (route `/service-health`, `EnabledByDefault = false`) - a read-only
  port of the standalone Flask dashboard at `D:\source\servicehealthmonitor`: Microsoft 365
  service status plus open incidents/advisories with Microsoft's own update timeline, read
  from Graph `admin/serviceAnnouncement`. Base app version deliberately NOT bumped (new
  module; Constitution "Deployment And Versioning", `.agents/decisions.md` 2026-07-21).
  Behaviour has been right since 1.1.0; 1.2.0, 1.3.0 and 1.3.1 are presentation. **1.3.0 is the
  owner's closing design ruling ("just make it look like the fucking original"): the page is
  now a fidelity port of the original dashboard's appearance - new scoped stylesheet
  `Components/Pages/ServiceHealth.razor.css` (the first one under `Components/Pages/`)
  reproducing the original's geometry with every colour mapped onto a `--ui-*` theme token,
  a flat brand header band (owner ruling 2026-09-09: no gradients), four summary cards, the
  original filter row plus the retained sort dropdown, a compact service-card grid, and the
  "Current Issues" section below whose cards the grid filters. Do not redesign this page
  and do not add charts - five rounds of design proposals were rejected, and the earlier
  praise for a donut chart was explicitly retracted.** Two earlier decisions were reversed by that ruling and are recorded in the
  plan's round 4: the separate incident section is back (filtered by the clicked service),
  and the wildcard text filter is replaced by the original's service dropdown, so
  `FilterServices` now matches the service id exactly. "Active Issues" counts issues with no
  `endDateTime`, the original's definition, which closes the old 17-vs-15 discrepancy in
  favour of 15. Incident HTML renders formatted, never stripped (owner ruling 2026-09-08:
  L1/L2 forward it to executives); the trust boundary is `HtmlSanitizer` 9.2.1039 in
  `ServiceHealthService`, and `MarkupString` on the page may only ever touch a field that
  came through it - a source-text test enforces that. `docs/ServiceHealth-Plan.md` Status
  Implemented. **TWO OPERATIONAL PREREQUISITES, BOTH OWNER-RUN, BEFORE THE PAGE CAN WORK:
  (1) create the Delinea record for the EXISTING app registration
  `e4fa5e51-d226-4a02-9a8b-d8de27133cdb` (tenant `eaa689b4-8f87-40e0-9c6f-7228de4d754a`,
  already consented for `ServiceHealth.Read.All`; the secret lives in a DPAPI file on
  ASHBEXUTIL1 today) with Tenant ID / Application ID / Client Secret fields, and set its id
  in Module Config as `Graph App Delinea Secret ID`; (2) enable the module and grant the
  `ServiceHealth` section access group.** Until (1) the page shows a not-configured banner
  rather than an empty board. NEXT: owner deploys dev to pick up 1.3.1 and accepts or
  rejects the look, then does the two steps above and runs the plan's manual checks. A
  `/codereview codex` pass on the module is still outstanding.
- **SHARED CONFIG DATABASE: IMPLEMENTED 2026-09-04, NOT DEPLOYED, NOT CUT OVER.**
  `docs/SharedConfigDb-Plan.md` Status Implemented; slices S1 `be507f0` (ConfigStore:Path
  key, must-exist open, tolerant migrator with table+column check, additive-only tripwire,
  base app `2.18.0` -> `2.19.0`), S2 `5a9c832` (ConfigChangeWatcher in the four caching
  readers, token recorded as of each load), S3 `5776965` (backup from the resolved path,
  deploy/promote Write-Fail on an absent configured file, promote loses the copy /
  `-SkipConfigFragments` / `-Refresh` / DB rollback, pipeline wording), S4 `8657b57`
  (`tools/Move-ConfigDbToShared.ps1` cutover, installer `-ConfigStorePath`), S5 = the
  docs commit that set the plan Implemented (Constitution, repo-guidance invariants 2-3,
  README, machines.md, this entry). A test-only commit `129d091` ahead of S1 fixed a
  pre-existing format-gate failure. Final: .NET 2320/0/3, Pester 125/0, ScriptAnalyzer 0
  errors, 45 mutation probes all biting. Owner rulings and the decision: `.agents/decisions.md`
  2026-09-04 (supersedes the 2026-06-18 dev-wins rule). Two FLAGS for the owner, recorded
  in plan section 9: the decision entry's "`data_version`" wording vs the implemented
  change token (read at most once per 2 s per reader); the additive-only migration rule is
  in the decision, repo-guidance and a tripwire test but not in the Constitution.
  **CUTOVER IS OWNER-RUN, ELEVATED, IN THIS EXACT ORDER: (1) `deploy-pipeline.ps1 -Dev`
  (dev gets 2.19.0); (2) `deploy-pipeline.ps1 -Prod` - the LAST two-database promotion,
  prod must be on 2.19.0 before the file is shared or the first dev-only migration stops
  prod; (3) `tools\Move-ConfigDbToShared.ps1 -PlanOnly` (eight steps printed, nothing
  changed; it refuses if either DLL is below 2.19.0); (4) `-Apply`; then the plan's
  section 8 manual checks (both logs show "Config store schema ready" once; a setting saved
  on dev shows on prod within seconds).** Until step 4 both instances keep their own
  `config\exchangeadmin.db` and behave exactly as before (no key = today's path). Jobs and
  usage databases stay per instance. **NEXT: owner deploys dev, then decides when to cut
  over; UsageTelemetry-Plan is now implemented at `2.20.0` (entry below).**
  Codereview (codex, 2026-09-04) over the implementation closed three findings, all
  fixed and probed: scdi-1 `3f8bd0e` (EXO pool now drains itself when the shared
  ExchangeOnline config changed in the other process; base app `2.19.1`), scdi-2
  `b481d2e` (drive-relative `D:x.db` refused), scdi-3 `a1103cc` (cutover script verifies
  the shared file before "nothing to do"); plan section 10 has the log. The current build
  is `2.19.1` (>= the cutover script's 2.19.0 floor).

- **USAGE TELEMETRY: IMPLEMENTED 2026-09-04, NOT DEPLOYED.**
  `docs/UsageTelemetry-Plan.md` (drafted `4af217e`, review fold
  `55064d7`). Owner request 2026-09-04: anonymous, lightweight telemetry - "theme, modules
  opened and not used"; ruling on the offered fork: **event rows** (`.agents/decisions.md`
  2026-09-04); the Home page Important Notice must state telemetry is active (owner, same
  day; AC13, conditional on the kill switch so it is never false). Codex openreview over
  `2938db7..4af217e`: `acceptable_with_changes`, three findings + one material change, all
  folded in (`.agents/review/findings/ute-{1,2,3}.md`). **Two were mine to have caught:**
  ute-1 put the table in the config DB that `promote-dev-to-prod.ps1` replaces wholesale
  with dev's (now its own `config/exchangeadmin-usage.db`, never promoted or backed up, no <!-- lint: allow (owner ruled leave-it, 2026-09-08: runtime usage DB is intentionally created outside source control) -->
  migrator step); ute-2 hooked `LogModuleAction` only, while MailboxPermissions,
  ConferenceRooms, Migration and MfaReset audit through their own methods (now the common
  `WriteAuditEvent`). ute-3: action rows now carry the throwaway session id through a
  `CircuitHandler.CreateInboundActivityHandler` + `AsyncLocal` ambient. Six slices; base
  app bump in S1 (the plan text says `2.19.0`, but SharedConfigDb landed first and took it -
  the bump is now `2.19.0` -> `2.20.0`), `AdminEventLog` `1.1.0` -> `1.2.0` in S5.
  S1 landed: `config/exchangeadmin-usage.db` with its own factory and <!-- lint: allow (owner ruled leave-it, 2026-09-08: runtime usage DB is intentionally created outside source control) -->
  `UsageEventRepository` (idempotent table, no migrator step), base app `2.20.0`.
  S2 landed: `UsageSession` + `UsageSessionCircuitHandler` (ambient per-circuit id),
  `UsageTelemetryService` (kill switch defaulting on, fail-closed on an unreadable
  config, `ModuleOf` legacy-category map, swallow-everything `Enqueue`), the
  `AuditService.WriteAuditEvent` telemetry hook as its last statement, and DI.
  S3 landed: invisible `Components/Layout/UsageTracker.razor` inside `<Authorized>` in
  `MainLayout` (session + open + theme, all after the FIRST interactive render so a
  prerender pass cannot double-count), `ThemePicker` records the theme after `setTheme`.
  S4 landed: startup prune of rows older than `UsageTelemetryService.RetentionDays` (90)
  in its own try/catch beside the export-retention pass, the `UsageTelemetryEnabled`
  Boolean config field on `AdminEventLog` (default on), and the Home notice bullet gated
  on the same `Enabled()` read the recorder uses.
  S5 landed: an Events/Usage toggle on the Event Log page - Usage hides the events table,
  the undo panel and every filter but the date range, and shows three anonymous aggregates
  (per-module opens/actions/failed/sessions/actions-per-open with EVERY catalog module
  listed, zeros included, plus any audited category that maps to no module under its own
  name; per-theme sessions; a one-line visit summary), all behind the same `EventLog`
  policy and with no new permission; `AdminEventLog` `1.1.0` -> `1.2.0`.
  S6 landed: README "Usage telemetry" under the Admin Event Log page (what is and is not
  recorded, the 90-day retention, where the kill switch lives), repo-guidance invariant 3
  naming both `exchangeadmin-jobs.db` and `exchangeadmin-usage.db` as per-instance files <!-- lint: allow (owner ruled leave-it, 2026-09-08: runtime usage DB is intentionally created outside source control) -->
  deliberately neither backed up nor promoted, and the plan set to Implemented with its
  traceability table, seven recorded deviations and an implementation-log entry.
  Final: 2373/0/3, format clean, `git diff --check` clean, 33 mutation probes across the
  six slices; no `.ps1`/`.psm1` touched, so ScriptAnalyzer/Pester were not in this
  stream gate. The build is now `2.20.0`, still above the cutover script minimum 2.19.0.
  That codereview has since run: codex over `e33b201~1..dea899b` returned four MEDIUM
  findings, all admitted and all closed, one commit each with a mutation-proved guard -
  utei-2 (a malformed kill-switch value read as ON in the service while the config page
  showed OFF), utei-3 (the kill-switch config read sat on the caller's path, so a locked
  shared config DB could delay an audited operation - now cached behind a
  `ConfigChangeWatcher` + 30s TTL), utei-1 (the bulk job pump inherited the enqueueing
  circuit's `UsageSession` through `Task.Run`, cross-attributing other operators' rows -
  the pump now starts with execution-context flow suppressed) and utei-4 (a date-range
  edit made in the Usage view left the events table, its count and its CSV on the old
  range - `ShowEventsView` now reloads when the range moved). Records in
  `.agents/review/findings/utei-{1,2,3,4}.md`; the batch carries base `2.20.0` -> `2.20.1`
  and `AdminEventLog` `1.2.0` -> `1.2.1`. Final: 2385/0/3, format clean, `git diff --check`
  clean.
  **NEXT: nothing is queued in this stream. The owner-run dev deploy picks up `2.20.2`
  (usage rows only start being written then).**

- **SAVE-THEN-PROMPT BUG 2026-09-08 (owner-reported, fixed, base `2.20.1` -> `2.20.2`):**
  saving a module enablement change wrote the change, then asked twice whether to abandon
  unsaved changes that no longer existed - an in-app "1 unsaved change" confirm and the
  browser's "Leave site?" - and only then showed the saved state. Nothing was ever at risk:
  the write had already committed. Cause: `UnsavedChangesGuard` takes the dirty flag as a
  PARAMETER and arms the browser's `beforeunload` from it, so a cleared flag only reaches
  the browser on a render; Blazor renders an event handler at its first await and again when
  it completes, and these handlers navigate instead of completing, so the browser was still
  holding the pre-save flag when the forced reload arrived. Fix: one explicit
  `StateHasChanged()` between the clear and the navigate, at all four sites that force a
  reload - `ModuleConfig.razor` `ReloadAsync` (Discard) and `SaveModuleEnablementAsync`,
  `AdminSettings.razor` `DiscardChanges` and `SaveEnablement`. Both pages can enable a
  module and both carried the identical defect, so both were fixed. Guard:
  `AdminPageDirtyStateTests.ClearingDirtyStateIsRenderedBeforeAForcedReload`, a 4-case
  source-text theory anchored inside each method body via `AuditCategoryFilingTests.MethodBody`
  (relocated there from `UsageTrackerWiringTests` so both suites share one copy). Mutation
  probe: renders removed -> exactly those 4 cases fail, 27 pass. Final: 2389/0/3, format
  clean, `git diff --check` clean. No module version bump - `module-config/{ModuleId}` and
  `admin-settings` are app-wide admin infrastructure, not catalog module routes.
  **NEXT: nothing code-side. Confirm on the next dev deploy that saving a module enablement
  change no longer prompts.**

- **DEV VALIDATION FIXES 2026-09-02: three owner findings from the first look at dev
  `2.15.0`, all IMPLEMENTED the same day, NOT DEPLOYED (ride the next dev deploy).**
  (1) The admin save bar counted dirty SECTIONS and called them "changes" - it now counts
  actual pending edits by diffing each section against what was loaded (`ad73573`, base
  `2.16.0`; `AdminPageDirtyState` per-section counts), plus the pre-existing gap that a
  protected-principal PATTERN add or directory-read secret-id edit never marked the
  section dirty (`31c720a`). (2) Intune notify/Entra-removal defaults left Module Config
  for act-time-only choice (`50f466d`, module `1.1.0`; see the Intune entry's D2 note).
  (3) Module permissions carried no explanation, so the Access tab showed bare aliases -
  `ModulePermission` now requires a `Description`, all 42 permissions across 27 modules
  have one, the Access tab renders it under each heading, a catalog tripwire enforces
  non-blank (`f42fdf0`, base `2.17.0`; spec and dev guide updated). Two permissions were
  found to grant nothing and now say so on screen: `ExchangeOnline` (no page or code
  consumes it) and `AdminSettings` (built from `Security:AdminGroups`, not section access)
  - recorded, not changed; owner's call whether to remove them.
  **Second round, same day, after the owner tested `2.17.0` on dev (both Graph app
  registrations PROVEN live: Risky Users search returned rows, a blank Intune search
  returned 50 devices):** (4) Self-Service Groups could not remove the cross-domain
  nested `Organization Management` (WINROOT) from `ExchangeWebAdmins` - the remove path
  resolved the member by GUID with no -Server (the listing was fixed for this 2026-08-28,
  the remove was not); fixed by threading the row's DN and routing the resolve, the
  membership pre-check (which asked the MEMBER for a back-link it cannot have and would
  have no-op'd silently) and the write to the owning domains, in BOTH group modules
  (`8d0710a`, SelfServiceGroups `1.7.0`, GroupManagement `2.7.0`). (5) Risky Users
  actions "Dismiss | Safe | Compromised" were ambiguous for L2 - now "Close as handled /
  This was the real user / Account was breached" with a one-line consequence in the
  confirm bar (`cf4c691`, RiskyUsers `1.1.0`). (6) Intune search was exact-match `eq`
  and found nothing for a real UPN - now `startswith` on deviceName and UPN, `eq` on
  serial, plus a plain-language "What do these actions do?" panel, button tooltips and
  a consequence line per confirm bar for Delete/Retire/Wipe/Entra/email (`f17ecfd`,
  IntuneDevices `1.2.0`). **The `startswith` filter form is UNCONFIRMED against the live
  tenant** - if a dev search reports a failed search naming a 400, the filter needs a
  per-field split. Also found: `GraphTokenClient.GetWithStatusAsync` discards the error
  body on non-success, so a rejected filter cannot quote Graph's reason without a base
  bump - recorded, not changed.
  **Third round, 2026-09-03, after the owner tested the second on dev:** (7) the
  `startswith` form ALSO returned 200/empty - even an exact device name copied from the
  blank list found nothing. Root cause is the SHAPE: three properties joined with `or` in
  one `$filter` evaluates to nothing on this tenant (both `eq` and `startswith`). Now ONE
  Graph request per field (deviceName startswith, UPN startswith, serial eq), merged by
  id, only the typed value percent-encoded, and any returned row that does not actually
  match is hidden and counted on screen (`acfabe9`, IntuneDevices `1.3.0`). **VERIFIED on
  dev 2026-09-03: "intune is searching correctly."** Also the search button/hint
  alignment (`dc4e9fc`). (8) The cross-domain remove got past the resolve and then failed
  at the WRITE: `Remove-ADGroupMember -Members <dn> -Server <group DC>` resolves the
  MEMBER on the group's DC, where a WINROOT DN does not exist. Both group modules now
  write the group's `member` attribute directly (`Set-ADGroup -Add/-Remove @{member=dn}`,
  `1a4b342`, SelfServiceGroups `1.8.0`, GroupManagement `2.8.0`); read-back still decides
  success. **NOT YET VERIFIED on dev: `ExchangeWebAdmins` is now a Protected Group Target
  on dev (owner: nobody with group-module access may make themselves a portal admin), so
  the original repro is refused by design. Test with a THROWAWAY ANALOG group holding a
  WINROOT member instead.** Suite 2220/0/3 as of `1a4b342`.
  **NEXT: owner: dev deploy is at `2.17.0` written 2026-09-03 12:25 (assembly) - if
  that build predates `1a4b342`, redeploy, then the throwaway-group remove/re-add check.
  The bulk actions below (`2.18.0`) will ride the same deploy.**

- **GROUP BULK ACTIONS: IMPLEMENTED 2026-09-03 (owner goal-directive the same day:
  "continue with the plan and codereview with codex (default) then implement upon
  consensus"). NOT DEPLOYED.** `docs/GroupBulkActions-Plan.md` (drafted `2e89f7a`,
  codex openreview `acceptable_with_changes` with gba-1..3 all folded in at `9652842`
  BEFORE any code - `.agents/review/findings/gba-{1,2,3}.md`), then all six slices in one
  session: S1 `568f80b` (pure `Services/BulkIdentityList.cs` + `BulkOutcomeSummary`, base
  app `2.17.0` -> `2.18.0` per gba-2), S2 `edf50e4` (GroupManagement bulk remove, `2.9.0`),
  S3 `e2bfc29` (GroupManagement paste-list bulk add, forest-wide resolution, `2.10.0`), S4
  `3923229` (SelfServiceGroups bulk remove with the nested-group warning in the
  confirmation, `1.9.0`), S5 `14e16f6` (SelfServiceGroups paste-list bulk add, users-only
  home-domain resolution with the group-scope reason, `1.10.0`), S6 README + plan status +
  this entry. Suite 2284/0/3 after S5, every slice mutation-probed.
  **The load-bearing shape, worth knowing before touching either page again:** each page
  now has ONE per-member handler (`RemoveOneAsync` / `AddOneAsync` on the admin page,
  `RemoveOneAsync` / `ChangeOneAsync` on self-service) that the single button AND the bulk
  loop both call; the module authorization re-check lives INSIDE it so it runs immediately
  before every row's write (gba-1 - the drafted plan had hoisted it to once per batch, and
  codex caught it); the service write paths are untouched. A batch's summary audit
  (`<Module>_Bulk{Remove,Add}Members`) reports `success` only when every row is Done
  (Known Failure Class 2) and carries `requested/done/notDone/members` in `extra`.
  **D1 implemented on its drafted default - owner may overrule:** a batch sends ONE
  administrator email listing every row's outcome instead of one per member (per-member
  AUDIT events unchanged; affected-user emails in self-service stay per member). Reverting
  to per-member emails is a one-line change per page (`sendAdminEmail: false` -> `true` in
  each bulk loop, drop the summary email).
  **Recorded lesson (S2):** a non-vacuity probe restore must copy from a scratchpad backup,
  never `git checkout` - the first S2 probe wiped the uncommitted slice and it was re-applied.
  **NEXT: the plan's seven manual checks (section 8) on dev against a THROWAWAY ANALOG
  group (`ExchangeWebAdmins` is a Protected Group Target on dev); check 7 records whether a
  WINROOT user by UPN previews Not found in self-service (expected - home-domain binding,
  same as the single Add). A codex defect-hunt (`codereview`) over `568f80b..HEAD` is the
  obvious review step and needs an owner go.**
  Owner: *"we need checkboxes and bulk actions for group management
  modules. removing a single entry at a time is slow and cumbersome."* Both on-prem group
  modules (`GroupManagement`, `SelfServiceGroups`); `M365GroupManagement` stays OUT (owner
  ruling 2026-08-11). Agreed shape, written to stand without the chat:
  (1) **Bulk remove:** checkbox per member row plus select-all; "Remove selected" -> one
  confirmation listing the names, one ticket where the module already requires one; each
  member then runs the EXISTING per-member path unchanged (protection check, servicer
  override, attribute write, read-back), sequentially, so one refusal never hides another;
  a per-row outcome table (removed / refused with reason / failed with reason); one audit
  event per member as today PLUS one batch summary event. Known Failure Class 2 is the
  whole risk here: never a blanket success.
  (2) **Bulk add via paste list:** a textarea taking usernames, emails or UPNs (one per
  line or comma-separated); one click resolves ALL lines against AD as a batch (queries
  run together, not one spinner each); a resolution table shows each line as resolved
  (with the AD name), not found, or ambiguous; only resolved rows are committable and the
  rest stay listed with their reason; one commit runs the per-member add path for the
  resolved set with the same per-row outcome table. Self-service adds USERS only (its
  standing rule); the existing typeahead stays for one-off adds. **Rejected by the owner:
  a "staged picker" (typeahead feeding a pending list) - it still types one at a time and
  waits on AD per entry; the paste list with batch resolution is the answer to "we'd lose
  the AD validation on the input control", not a replacement for it.**
  Constitution: a written plan is required (new write surface over the protection gates) -
  written, reviewed and implemented as recorded at the top of this entry.

- **CSV EXPORT FOR FIVE MODULES: IMPLEMENTED 2026-09-01 (owner go the same day).
  NOT DEPLOYED.** `docs/ModuleCsvExport-Plan.md`; all seven slices landed: S1
  `45f14e5` (shared `CsvExport.Write` helper + AC1b formula neutralization, base
  app `2.12.0` -> `2.13.0`), S2 `33ba4ea` (DhcpAuthorization export, module
  `1.3.0`), S3 `6b96d5c` (NamedLocations export, module `1.1.0`), S4 `71d25b0`
  (BlockedSenders export, module `1.4.0`), S5 `8061610` (BitLockerRecovery keys
  export with ticket, AC4b `ExportRecoveryKeysCsv` bulk-disclosure audit, module
  `1.2.0`), S6 `134c90c` (Migration status export, module `1.8.0`), S7 `b84cfb5`
  (README export bullets; BlockedSenders had no existing README section, so a
  minimal one was added). Every slice mutation-probed by design at
  implementation. Full suite after S6: 1881 passed / 0 failed / 3 skipped.
  **D1 honored:** the BitLocker export contains the recovery keys (owner
  ruling, plan section 1); the download is itself an audited bulk-disclosure
  event (`ExportRecoveryKeysCsv`, ticket + row count) and the key material
  never enters the audit log.
  **NEXT: nothing code-side - the plan's four manual checks (section 8) need a
  deployed instance and ride the next dev deploy.**

- **PROTECTED TARGETS ANSWER AT FIRST QUERY: IMPLEMENTED 2026-08-31 (owner go after
  testing on dev: refusal "should happen as soon as the group is queried; preferably
  protected groups won't show members either"). NOT DEPLOYED.** GroupManagement only:
  `CheckTargetProtectionAsync` runs at group selection - a non-servicer gets the
  refusal immediately and the panel (Load Members, add box, member table) never
  renders; the member read itself gates server-side as the fail-closed backstop; a
  servicer sees a Protected badge and works normally. Write-path gates unchanged
  (defense in depth). Self-service untouched per the same day's AC4 ruling.
  `GroupManagement 2.6.0`, no base bump. 7 new tests (behavioral via the seamed
  harness + tripwires), probe: both gates neutered, 5 tests failed, restored.
  **NEXT: rides the next dev deploy; then select a protected group as a non-servicer
  and confirm the immediate refusal with no member list.**
  **Two adjacent PRE-EXISTING gaps found while reading - BOTH CLOSED as of `8d0710a`
  (2026-09-02): the DN paths were already routed by fsr-1, and the name-only fallback of
  `ResolveGroupForWrite` now resolves through the forest global catalog and re-reads the
  single match in its own domain. See the DEV VALIDATION FIXES entry.**

- **EVENT LOG CSV TICKET: IMPLEMENTED 2026-08-27 (owner go the same day). ON DEV since
  2026-08-31 (the `2.10.0` deploy); NOT on prod.**
  `docs/EventLogCsvTicket-Plan.md` (S1 `d54b33f`, S2 the plan-closing commit). Stored
  audit/trace `ticket` field appended as the ninth CSV column, named `Ticket`; no
  ServiceNow lookup, no on-screen column, no filter. Module `AdminEventLog`
  `1.0.3` -> `1.1.0`, no base app bump. Six new tests, mutation-proven; full suite
  1707/0/3.
  **NEXT: run the plan's four manual checks (section 8) on dev - unblocked 2026-08-31.**

- **INTUNE DEVICES MODULE: IMPLEMENTED 2026-09-01 (owner go the same day). NOT DEPLOYED.**
  `docs/IntuneDeviceManagement-Plan.md` (Status now `Implemented 2026-09-01`). All seven
  slices landed: S0 `aa31d49` (status-returning Graph mutation helpers; base app version
  bumped to `2.14.0`), S1 `d26906e` (models and read-only service), S2 `5d0d308` (catalog
  entry + read-only UI; module `IntuneDevices 1.0.0`), S3 `c339e83` (Delete), S4 `723d7c0`
  (Retire and Wipe), S5 `931294c` (Entra ID device object removal), S6 `080273a`
  (affected-user notification with suppression visibility). Versions: base app `2.14.0`
  (bumped in S0), module `IntuneDevices 1.0.0` (all seven slices landed before any deploy,
  so it ships once). Full suite 2162/0/3.
  Owner request 2026-08-14: *"we need to plan a module for managing intune devices, pulling
  device details, and deleting"*, then *"review the plan with codex"*. New module,
  Microsoft Graph v1.0 Intune device management, independent of the plans around it -- no
  shared code, no ordering constraint.
  **D1 (owner, 2026-08-14): all three destructive actions, at two permission tiers.**
  *"options for all of the above with different permission levels for 1 and 2+3. Two
  permission levels."* Delete the Intune record sits behind `IntuneDevicesDelete`; Retire
  and Wipe share `IntuneDevicesPrivileged`; read sits behind the main `IntuneDevices`
  permission. All three fail closed. **The distinction the question existed to surface, and
  it is the operator-facing one:** Intune "Delete" removes the management record only.
  Company data stays on the device until it next checks in, and if it never checks in,
  forever. The Entra ID device object survives all three actions - Microsoft's own guidance
  is to remove it as a separate step. Implemented in S3+S4.
  **D2 (owner, 2026-08-14), a standing design rule for this module, not just an answer:**
  *"anything that can be an option should be an option. do not build in restraints. make
  email the user an option."* **Config half SUPERSEDED 2026-09-02 (owner, during dev
  validation: "the email options should not live in global module settings"): the four
  Boolean config fields are gone (`50f466d`, module `1.1.0`, `.agents/decisions.md`
  2026-09-02); the per-action checkbox stays, starting off for Delete, on for Retire and
  Wipe, and the Entra-removal checkbox starts off.** The operator decides at the moment of
  acting; the audit event records what actually happened. `EmailService`'s app-wide `_notifyUsers` switch outranks anything the
  module sets - a ticked box on a deployment with user notifications off states so on
  screen and in the audit, rather than reading as decorative. Implemented in S6.
  **D3 (owner, 2026-08-14): removing the Entra ID device object is in, as an option.**
  *"yes, add it as an option."* Its own granular permission `IntuneDevicesEntraDelete`
  (never riding `IntuneDevicesPrivileged`), because `Device.ReadWrite.All` is a DIRECTORY
  scope covering every device object in the tenant - the widest grant in the module, and
  not an Intune scope at all. Implemented in S5.
  **The azureADDeviceId-vs-object-id trap, verified against Learn before S5 was written and
  worth remembering if this area is touched again:** `managedDevice.azureADDeviceId` is the
  Entra **`deviceId`**, not the directory **object id** - Learn's own `device: get` example
  shows both GUIDs on one device. `DELETE /devices/{id}` wants the object id; passing the
  `deviceId` to it 404s against a device that is present and fine. S5 addresses the object
  by its alternate key, `DELETE /devices(deviceId='...')`, the only form that takes what
  the module has. `azureADDeviceId` is captured before the Intune action runs, since a
  deleted Intune record cannot be read for it afterward; the Intune and Entra outcomes are
  reported and audited independently, so a half-finished result is never a plain success.
  **openreview `codex` (`gpt-5.5-dzs` @ xhigh, grade fallback), two passes, both
  `acceptable_with_changes`, five findings total, all admitted and folded in** -
  `.agents/review/findings/idm-{1,2,3}.md` over `b868e5c..6aef9e3` (idm-1 HIGH forced S0
  onto the plan and with it the base version bump; idm-2 HIGH withdrew an unverified claim
  that wipe's default body was a full factory reset; idm-3 MEDIUM was the third
  `ppsvc-1`/`pgwt-1`-shaped unreachable-capability finding, fixed by adding `IntuneDevices`
  to `ModuleConfig.razor`'s servicer opt-in set in S3), and `idm-{4,5}.md` over
  `6aef9e3..236b91b` (both durable-record hygiene, neither touching the plan's substance).
  No part of this plan is unreviewed.
  **NEXT: owner-side app registration + Delinea secret**
  (`DeviceManagementManagedDevices.Read.All`, `.ReadWrite.All`, `.PrivilegedOperations.All`,
  and `Device.ReadWrite.All`, admin-consented, kept as four distinct scopes on purpose -
  blocks the first live Graph call, not any code), **then the plan's manual checks ride the
  next dev deploy.**

- **RISKY USERS MODULE: IMPLEMENTED 2026-09-01 (owner go the same day). NOT DEPLOYED.**
  `docs/RiskyUsersModule-Plan.md` (plan revision `42d736f`; status now `Implemented`).
  All seven slices landed: plan revision `42d736f` (re-sequencing S2 ahead of S1+S3),
  S2 `e003af9`, S1+S3 `c68c7b6`, S4 `962cf38`, S5 `dee1add`, S6 `930d762`, S7 `3602859`
  (README + token-log). Module `RiskyUsers` stays `1.0.0` - all seven slices landed
  before any deploy, so it ships once (the plan's own versioning rule). Full suite
  1951/0/3.
  Owner request 2026-08-12: *"explore adding a module to this app that can managed Risky
  Users in Azure"*, then *"plan it, review the plan with codex, and add it to the list of
  things to implement."* New module, Entra ID Protection via Graph v1.0, independent of
  every other stream.
  **D1 RULED (owner, 2026-08-12): remediation IS in scope** - *"yes, manage means
  manage, not read-only view."* The full module shipped: read (list/history) plus
  Dismiss/Confirm Safe/Confirm Compromised behind `RiskyUsersRemediate`, one Graph call
  per user (the three action endpoints take `userIds` as an array and return one bare
  `204` for the whole batch, so a per-user call is the only way to get a per-user
  outcome - Known Failure Class 2 written into the API itself).
  **D2 RULED 2026-08-31 (owner: "it should be logged, but not alert emailed"): reads
  audit, never alert-email.** Recorded in `.agents/decisions.md` 2026-08-31; AC17
  asserts the audit-only shape and it is implemented that way - `EmailService` is not
  reachable from the read path.
  **The identity-model constraint that shaped the write phase: risky users are CLOUD
  identities.** The repo's group, OU and SamAccountName protection rules all evaluate
  from an on-prem DN and structurally cannot match a cloud-only principal (Constitution,
  Protected Principals, final bullet). S6 uses the `MfaReset.razor:262-364` two-branch
  protected-principal shape and improves on it: `riskyUser.id` IS the Entra object id,
  populated into `EntraObjectId` on the unresolved branch. Verified during S6 that
  `ProtectedPrincipalService.CheckAsync` -> `MatchesIdentity`
  (`Services/ProtectedPrincipalService.cs:684-720`) actually consults `EntraObjectId`, so
  object-id protection genuinely bites here, not just in principle. The servicer override
  (`ProtectedServicer:RiskyUsers`) is honoured on both branches; no such row exists in
  either config store on first deploy - scope, not oversight.
  **S6 judgment call, recorded rather than silently diverging: RiskyUsers validates its
  ticket through `ServiceNowService.ValidateTicketAsync` directly**
  (`Components/Pages/RiskyUsers.razor:577`), not through the `ITicketValidator` /
  `TicketValidationService` seam and per-module `ValidateTickets` Boolean switch that
  `docs/BitLockerMandatoryTicket-Plan.md` S1 introduced the same day. Both are valid
  shapes and they were not reconciled with each other. The owner may want the newer seam
  adopted here later; unscheduled, not a defect.
  **openreview `codex` (`gpt-5.5-dzs` @ xhigh, grade fallback) over `d877294..a2c4c77`:
  `acceptable_with_changes`, THREE findings, all admitted, all folded in** -
  `.agents/review/findings/ru-{1,2,3}.md`, all `[x]` in `.agents/review/index.md`. ru-1
  (HIGH): the plan argued this module meets the alerting clause, then listed D2 as
  "blocks nothing" - fixed by making D2 an explicit pre-ship gate, now satisfied. ru-2
  (MEDIUM): a slice boundary drawn on conceptual grouping instead of compile order -
  fixed by the `42d736f` re-sequencing (S2 before S1+S3). ru-3 (MEDIUM): the test plan
  pointed at a `private sealed` test helper and missed that the descriptor breaks the
  hardcoded catalog/alias counts - both fixed in the S1+S3 commit.
  **External prerequisite, still outstanding: the dedicated Entra app registration**
  (`IdentityRiskyUser.Read.All` and `.ReadWrite.All`, admin-consented) plus its own
  Delinea secret. Blocks the first live Graph call, not any code - all seven slices were
  built and tested entirely against the seamed test harness.
  **NEXT: owner-side app registration + Delinea secret** (blocks the first live Graph
  call), **then the plan's manual checks ride the next dev deploy.**

- **NESTED GROUP MEMBERSHIP: IMPLEMENTED 2026-08-27 (owner goal-directive the same day). ON
  DEV since 2026-08-31 (the `2.10.0` deploy); NOT on prod.** S1 `386e8d2`, S2 `695e73f`, S3 `4fc9d3d`, S4 `3f2ab21`, S5a `ba3b6c8`,
  S5b `8c4042c`, S5c `a014068`; S6 is the commit that set this status. Versions: app `2.9.0`,
  `SelfServiceGroups 1.4.0`, `GroupManagement 2.3.0`. Range reviews (codex gpt-5.6-sol@xhigh,
  per-major-item): S1+S2 clean; S3+S4 raised gmn-4/gmn-5 (both MEDIUM, fixed and verified);
  S5a-S6 raised gmn-6..gmn-9 (two HIGH - including a real resolved-USER protection bypass in
  the new write paths - and two MEDIUM; all four fixed one commit each, `0b4b72e` `b8379dc`
  `dc503e1` `1c47d64`, all verified by codex with independent guard proofs). Every review
  loop on this stream is CLOSED. The plan's manual checks are NOT run - they need a deployed
  instance.
  `docs/GroupMemberNesting-Plan.md` (`074bfdb`, revised through `c7897d1`).
  Owner report 2026-08-11: *"group self-management module needs to handle nested groups.
  when trying to add a group to a group, nothing resolves."*
  **Not a defect - `SelfServiceGroups` is user-only in four places by construction**
  (typeahead `ObjectKind="User"`; `AdOwnershipFilter.cs:97` `objectCategory=person`;
  `IsMemberOfGroup` on `Get-ADUser`; `GroupMemberClassifier` removable=user-only). The
  operator saw *"did not match exactly one user"*, which reads as a typo rather than a
  scope limit.
  **Owner rulings D1-D5, all in the plan (canonical there):** self-service NEVER adds a
  group (ITSD ticket instead) and says so up front; it MAY remove one behind a warning
  that re-adding needs a ticket; `GroupManagement`, being admin-audienced, gets full
  group add/remove; the shared protection blind spot is closed rather than worked around;
  the servicer override for `GroupManagement` needs no code.
  **The find that made this more than a UX change: `ProtectedPrincipalService.cs:747`
  runs `Get-ADUser` to ask whether a target sits inside a protected group.** Hand it a
  group DN and AD returns zero rows with no error, which `:761` records as "no match" -
  a silent ALLOW, not a fail-closed refusal. Harmless today because nothing can target a
  group; live the moment the admin module can. **This is the repo's fail-closed rule
  inverted in a shared file, and it was found by reading the call, not by any test.**
  **D5 corrects a premise in the owner's own request.** The servicer override for
  `GroupManagement` already exists - `GroupManagementService.cs:16,84` and
  `ModuleConfig.razor:655`. What is missing is a granted group, which is a Module Config
  action, not code. No module has a `ProtectedServicer:GroupManagement` row today.
  **openreview `codex` (`gpt-5.5-dzs` @ xhigh, grade fallback) over `618235e..074bfdb`:
  `acceptable_with_changes`, THREE findings, all admitted, all folded in** -
  `.agents/review/findings/gmn-{1,2,3}.md`, all `[x]` in `.agents/review/index.md`.
  **All three were the same shape and it is worth naming: a correct goal wired to a
  mechanism that cannot reach it.** gmn-1 (HIGH): S1 made the protection check
  group-aware, but `GroupManagementService.CheckProtectedAsync` filters the group out one
  call earlier via a user-only resolver, so the fix landed below the layer that drops the
  target - AC13 would have failed with every S1 test green. gmn-2 (HIGH): the cycle
  guard's LDAP filter asked the MIRROR of its own stated question, so it would refuse
  legitimate adds and allow real cycles, and it sat in the page while the write is in the
  service - the exact page-only shape `GroupManagementService.cs:36-38` records this
  module already shipping and being bypassed. gmn-3 (MEDIUM): the picker returned a bare
  sAMAccountName while group search is deliberately forest-wide, so a chosen WINROOT group
  could resolve to its ANALOG namesake.
  **None of the three would have been caught by implementing the plan faithfully - a
  faithful implementation is what produces them.** Reviewing the plan before writing code
  is what made them cheap.
  **D6 closed the last open question and the plan is APPROVED.** Owner: *"same as for
  users"* - a group member notifies on the existing `NotifyAffectedUser` predicate with no
  class check added; the group's `mail` is the address, and no `mail` means no
  notification, exactly as for a user. **I had raised this as a fork with a recommendation;
  the owner's response was that it was ceremonial and did not need their focus.** The
  reusable rule: where an existing predicate already answers the question, applying it is
  the work - a fork is only warranted when the options genuinely diverge.
  **2026-08-28 validation finding, FIXED - on dev since 2026-08-31, not on prod: both member
  listings faulted on a cross-domain nested member.** `Get-ADGroupMember` makes ADWS resolve every member
  server-side and faults the whole read when a member sits in another forest domain the module
  credential cannot chase - `Organization Management` (WINROOT), nested in `ExchangeWebAdmins`
  since 2026-05-12, broke the self-service member list on dev (ADWS `GetADGroupMemberFault`;
  the same read succeeds under an operator identity, so it is credential/chase-dependent).
  **NOT a 2.9.0 regression: the failing code is byte-identical in 2.8.1** - the nesting
  validation simply pointed the list at a nested-group case for the first time. Both group
  modules now read the group's `member` attribute (the Comms10k pattern, which also lifts the
  cmdlet's 5000-object cap) and resolve each member routed to its own domain; an unresolvable
  member degrades to a DN-named read-only row rather than failing the list.
  `GroupManagement 2.3.1`, `SelfServiceGroups 1.4.1`, no base bump.
  **Review loops CLOSED 2026-08-28.** lst-1..3 and pgwt-4..9 all fixed and verified -
  the seven substantive fixes ACCEPTED guard-confirmed in an OWNER-RUN interactive codex
  round (`.agents/review/manual-verify.*`) after the headless workspace-write sandbox
  fault (still recorded in `.agents/machines.md` - probe before the next headless
  verification dispatch). pgwt-3 remains declined at intake
  (`.agents/review/pgwt-3.contested.md`), owner-overrulable any time.
  **Listing fix VERIFIED on dev 2026-08-31** (browser-driven, owner-attended):
  ExchangeWebAdmins lists 13/13 members including the WINROOT `Organization Management`
  nested group, in BOTH group modules. **NEXT: the remaining nesting manual checks** (real
  nested add/remove on a throwaway group - owner's, it writes AD - and the cross-domain
  picker case). All review loops closed - gmn-4 through gmn-9 fixed and verified.

- **PROTECTED ON-PREM GROUPS AS WRITE TARGETS: IMPLEMENTED 2026-08-28 (owner go the same
  day: "continue with the next task"). ON DEV since 2026-08-31 (the `2.10.0` deploy); NOT
  on prod.** S1 `2984df0`, S2 `1217e14`, S3
  `1f8f863`, S4 `645bd37`; versions app `2.10.0`, `GroupManagement 2.4.0`,
  `SelfServiceGroups 1.5.0`; suite 1829/0/3, every slice non-vacuity-probed, M365
  verified untouched over the range. Canonical detail (slices, the target-gate rule set,
  the AC6/AC8 reconciliation recorded before code) lives in the plan's Status and
  Revision 2026-08-28 sections.
  **AC4 REVERSED 2026-08-31 (owner ruling during dev validation, `.agents/decisions.md`):
  self-service is never gated by Protected Group Targets - owners always edit owned
  groups there. S3's gate and its test file removed, `SelfServiceGroups 1.6.0`, ON DEV
  since 2026-08-31 (second same-day deploy, verified from the live page: app 2.10.0,
  SelfServiceGroups 1.6.0). The GroupManagement admin gate stands - it guards the app's
  privileged credential, the real boundary. The owner then populated Protected Group
  Targets on dev with real groups that STAY (2026-08-31) - the feature is live in
  anger on dev; prod still has none of this.**
  **NEXT: dev manual checks - browser-driven 2026-08-31 (owner-attended): listing fix
  verified in BOTH modules (13/13 incl. the cross-domain group); admin refusal checks
  interrupted by the owner mid-run and superseded by the AC4 ruling; Event Log CSV
  checks not run. The `ADEXNLQ_Users` test row was removed from Protected Group
  Targets by the owner the same day - dev settings are back to the pre-test state.**
  `docs/ProtectedGroupWriteTarget-Plan.md` (`503c1a8`, revised `7c5f8a6`,
  scope narrowed after).
  **Found by the owner reading the nesting plan, and it is the larger hole of the two.**
  **The group modules protection-check the MEMBER being added or removed and never the
  GROUP being written into.** Protection stops you touching a protected person and does
  nothing to stop you granting an ordinary person protected access. **An operator with
  `GroupManagementOnPrem` can add any unprotected account to `Domain Admins`** with
  `Domain Admins` listed as protected and no gate firing
  (`GroupManagementService.cs:253,304`; the page delegates and pre-checks nothing by
  design, `GroupManagement.razor:271-276`). Self-service has the same shape, gated on DACL
  ownership only.
  **SCOPE IS ON-PREM ONLY. Owner, 2026-08-11: *"we're not touching the cloud groups
  module."*** An earlier draft covered `M365GroupManagement`; **that was scope I added
  unasked** while surveying which modules shared the defect, and the owner removed it -
  *"who's talking about o365 groups and why? that wasn't part of my prompt."* **The rule
  it earns: a survey that finds more instances of a defect is not authorization to fix
  them.** Report them and let the owner choose. It cost a third of a review pass and a
  plan section that had to be cut.
  **UNSCHEDULED, NOT IN ANY PLAN, recorded so it is not lost: a protected M365 group can
  be renamed or DELETED outright.** `M365GroupManagementService.UpdateGroupAsync:125` and
  `DeleteGroupAsync:143` have no protection gate of any kind; the page gates on a ticket
  number only (`M365GroupManagement.razor:286`). Adding an OWNER to a protected M365 group
  is ungated too (`:255,276`). **And it cannot be fixed by config alone: an M365 group
  cannot be marked protected at all** - both admin pickers are AD-only
  (`AdminSettings.razor:144,172`) and `AddValidatedAsync:660` refuses anything
  `ADSearch.ValidateExists` cannot resolve. Nobody is working this.
  **Hard dependency on the nesting plan's S1.** Without the group-aware check and the DN
  self-match, every gate this plan adds returns "not protected" for a group and the whole
  change is inert while appearing to work. AC6 pins it: reverting S1 must make a test here
  fail.
  **openreview `codex` over `2eedaa9..503c1a8`: `acceptable_with_changes`, two findings,
  both admitted** - `.agents/review/findings/pgwt-{1,2}.md`. pgwt-1 (HIGH) was entirely
  about M365 and is **mooted by the scope cut**; its record is kept because the gap is real
  and unowned, and the criterion it earned survives as AC7. pgwt-2 (MEDIUM) applies
  unchanged: the plan reused a DN-only resolver, and `CheckPatternMatches:612-613` returns
  at its first line when `SamAccountName` is empty - so a group protected by `adm-*` would
  read as unprotected with every `Groups`-list test green.
  **The review's recommendation mattered more than its findings: settle the target identity
  model BEFORE implementation.** **T0: a separate Protected Targets list that reinterprets
  nothing already stored.** Re-reading the existing `Groups` list as target protection would
  make every broadly-listed group unmanageable the moment the build deploys - the `sidf-1`
  shape. AC8 is the anti-lockout criterion.
  **NEXT: owner go.** No open question in the plan.

## Next

**THE PAUSE IS LIFTED. Owner, 2026-08-27: the token budget was reset early, so the
2026-09-01 restart date no longer applies. The queued plans below start on a normal
per-plan owner go, as ever.** (History: work was paused 2026-08-12 because the August
AI budget was ~90% spent. Two owner-directed exceptions were taken during the pause:
the Migration size-check fix, 2026-08-13, and the Event Log CSV Ticket implementation,
2026-08-27, both in `## Now`.)

**What "ready to go" means here, and all of it is FREE of AI budget** - four items, all
owner-side, none of them needing an agent:

1. ~~A go on `docs/ProtectedGroupWriteTarget-Plan.md`~~ - **DONE: implemented 2026-08-28**
   (see `## Now`); nothing owner-side remains on it except the deploy-time manual checks.
2. ~~A D2 ruling on `docs/RiskyUsersModule-Plan.md`~~ - **DONE: D2 ruled 2026-08-31**
   (reads are audited, never alert-emailed; `.agents/decisions.md` 2026-08-31). The
   module is now implemented (see `## Now`); nothing owner-side remains on it except
   the app registration below and the deploy-time manual checks.
3. **The Risky Users Entra app registration** plus its Delinea secret
   (`IdentityRiskyUser.Read.All` and `.ReadWrite.All`, admin-consented). Blocks the
   first live Graph call. S1-S4 can be built and tested without it.
4. **The Intune Devices Entra app registration** plus its Delinea secret
   (`DeviceManagementManagedDevices.Read.All`, `.ReadWrite.All`, `.PrivilegedOperations.All`
   and `Device.ReadWrite.All`, admin-consented). Blocks the first live Graph call, not the
   build. Keep the four scopes distinct. The fourth is a directory scope, wider than the other
   three, and is the one to weigh before consenting.

With those done, the remaining plans are cold-startable at any time with no
conversation needed: `docs/RiskyUsersModule-Plan.md` at its S1 and
`docs/IntuneDeviceManagement-Plan.md` at its S0 - independent of everything else and of
each other.

**QUEUE PRIORITY, owner-ordered 2026-08-31 ("priority: 5, 4, 2, 1, 3"), P1 INSERTED
by owner ruling 2026-09-01 (items below shift down one):**

0. **P1 (owner, 2026-09-01): Boolean config fields render as checkboxes - IMPLEMENTED
   the same day (owner go), single slice.** `docs/BooleanConfigControls-Plan.md`;
   ruling in `.agents/decisions.md` 2026-09-01 ("no compromise").
   `ConfigFieldType.Boolean` + checkbox rendering on Module Config;
   `PreventSelfGrant` converted; tripwire test blocks any future boolean-defaulted
   text field. Base app `2.11.0` -> `2.12.0`, no module bumps. Suite 1854/0/3,
   probe-proven. NOT DEPLOYED - manual checks (plan section 8) ride the next dev
   deploy. **BitLocker S2 is now unblocked** (`ValidateTickets` declares Boolean).
1. BitLocker mandatory Ticket field - PLAN DRAFTED 2026-08-31, REVISED same day for the
   owner's ServiceNow ruling (`docs/BitLockerMandatoryTicket-Plan.md`, no open owner
   decision), CODEX-REVIEWED same day (openreview over `a9b0ebc..533c1fe`,
   `acceptable_with_changes`; btv-1 folded in - see the plan's Review log). **S1
   IMPLEMENTED 2026-09-01 (owner go the same day), commit `280311f`:**
   `ITicketValidator`/`TicketValidationService` over the dormant ServiceNow client,
   `ServiceNowService.Enabled`, DI, 10 tests mutation-proven in 3 batches, base app
   `2.10.0` -> `2.11.0` (bump moved into S1 per the csv-2 rule, plan revised
   `5b48b39`). Suite 1852/0/3, format clean. **S2 IMPLEMENTED 2026-09-01 (owner go
   the same day), commit `fd7cb1f`:** ticket gate first-statement in both search
   methods (Rejected AND Unavailable refuse before the archive opens), page ticket
   input with button/Enter gating, `searchTicket` captured at search time so the
   reveal audit carries the ticket that authorized the visible results,
   `ValidateTickets` Boolean config field (default false). 5 new tests (2
   behavioral + 3 source guards), mutation-proven in 2 batches; suite 1859/0/3,
   format clean. No version bump in S2 by design - base bumped in S1, module bump
   is S3. **S3 IMPLEMENTED 2026-09-01 (owner go the same day, same session),
   commit `e6534f1`: module `1.1.0`, docs, plan status Implemented with the
   section 9 traceability completed. ALL SLICES LANDED; NOT DEPLOYED.**
   **NEXT: nothing code-side - the plan's four manual checks (section 8) need a
   deployed instance and ride the next dev deploy.** This also unblocks the CSV
   export plan's S5 (item 6/queue item 2, which depends on this stream landing
   first). (Item 7 in the older list below)
2. CSV export for five modules - **IMPLEMENTED 2026-09-01** (owner go the same
   day); see `## Now`. All seven slices landed (S1-S6 code, S7 README);
   `docs/ModuleCsvExport-Plan.md` status is `Implemented`, section 9
   traceability completed. Manual checks ride the next dev deploy. Do not
   restart.
3. Risky Users module - **IMPLEMENTED 2026-09-01** (owner go the same day); see
   `## Now`. All seven slices landed; `docs/RiskyUsersModule-Plan.md` status is
   `Implemented`. NOT DEPLOYED - the app registration and Delinea secret still block
   the first live Graph call; manual checks ride the next dev deploy. Do not restart.
4. Intune Devices module - **IMPLEMENTED 2026-09-01**; see `## Now`. NOT DEPLOYED - the
   app registration and Delinea secret still block the first live Graph call; manual
   checks ride the next dev deploy. Do not restart.
5. Sidebar Home link removal - IMPLEMENTED 2026-09-01 (`2128610`, base `2.15.0`; see
   item 5 in `## Next up`). NOT DEPLOYED.
Queue complete 2026-09-01: every item above is implemented. The owner
ruled the same day that one session may run all slices (fresh-session-per-slice
protocol withdrawn), with Sonnet/Opus subagents doing the coding.

**The older "Queue status (corrected 2026-08-28, pgwt-9)" numbered list that used to sit
here is superseded by the QUEUE PRIORITY list above and is archived verbatim**
(`docs/history/state-archive.md`, Archived 2026-09-02) - it had drifted (item 5 still read
`docs/TokenBudget-Plan.md` as "Draft, awaiting a go" after that stream was already DONE).

**Migration batch selection is DONE, accepted, and deployed to both** (app `2.8.0` / Migration
`1.7.0` at the time; the full record is archived in `docs/history/state-archive.md`, Archived
2026-08-14). Nothing outstanding on it.

**Dev and prod are level** -- the version is owned by the `Deployed:` entry under `## Blockers`.
Everything below is the protected-principal stream, which is code-complete, deployed, and now
configured; only its manual checks remain on it.

**The servicer group is `ANALOG\ExchangeWebAdminsExecSupport`** (SID
`S-1-5-21-8915387-325452579-1788637320-710891`), read from the live `section_access` table in both
`config/exchangeadmin.db` files on 2026-08-11. It holds three `ProtectedServicer:` grants - <!-- lint: allow (owner ruled leave-it, 2026-08-11: untracked environment database file) -->
`MailboxPermissions`, `CalendarPermissions`, `OutOfOffice` - and, after the owner closed the gap
below on 2026-08-11, the matching module grants for all three plus `CalendarPermissionsOnPrem` and
`MailboxPermissionsOnPrem`. Re-read from both live stores after the change: every servicer grant
now has its module grant behind it, identically on dev and prod. The other nine servicer-capable
module ids
(`ADAttributeEditor`, `EmergencyDisable`, `MfaReset`, `Comms10k`, `GroupManagement`,
`M365GroupManagement`, `LicensingUpdates`, `Migration`, `SelfServiceGroups`) have no
`ProtectedServicer:` row anywhere; recorded as scope, not oversight.

1. **Owner: the group has the CalendarPermissions servicer grant but no CalendarPermissions module
   grant.** A member who is not also in `ExchangeWebAdmins` or `ExchangeWebPerms` cannot open that
   page, so that servicer grant is unreachable. Either add the module grant or drop the servicer
   row - as configured it is a grant that nobody can use. Group membership was not checkable from
   the app host (no AD cmdlets), so whether any member is affected is unverified.
   **The owner ruled 2026-08-11 that the gap itself is a UI problem, not a config one: a servicer
   grant conveying no module access must be visible where the grant is made.** Done in `46c8257`
   (app `2.8.1`, deployed to both 2026-08-11) - Module Config now states it in the standing warning,
   flags each affected row with a "no module access" badge, and raises a callout when any row is
   flagged. The
   check compares stored SIDs and does not expand nested membership, so it can flag a group that
   reaches the module through another; the wording is conditional for that reason, and every
   genuinely stranded grant is still flagged.
   **The config gap that prompted it is CLOSED** (owner, 2026-08-11): Exec Support now holds
   `CalendarPermissions`, and the on-prem pair as well. Nothing should be badged today - which also
   means the badge has never been seen firing on a real row, so its rendering is unproven.
2. **DEFERRED to real prod use by the owner, 2026-08-11 - not a task anyone is waiting on. The
   load-bearing manual check: a member of the servicer group acts on a protected principal, the
   action SUCCEEDS, and the audit record names the group that permitted it.** Nothing automated
   proves the capability works end to end - every guard is either a source-level tripwire or a
   decision tested in isolation. Worth doing on a module with a page gate (AD Attribute Editor,
   where the operator should also see the override banner) AND a batch module (Migration or
   Licensing), since those took different implementation shapes.
   **What deferring costs, so a later reader can weigh it:** the first real exercise of this
   capability will be someone doing actual work under time pressure, and if the chain is broken they
   meet a refusal then, not in a test. That is the owner's accepted trade, not an oversight.
3. The inverse, and just as important: **an operator NOT in the group is still refused** on the
   same target, and the refusal is audited.
4. Also unverified on a real run: the per-target notes in a batch. A Migration batch mixing a
   protected-and-serviced target with an ordinary one should produce one note NAMING that target,
   not a batch-level "something was serviced".

**A caution that survived this whole work stream and still applies:** a green suite in this repo
says nothing about what an operator sees. There is no bUnit harness, so no test renders a page -
and every one of the three review findings lived in a page or a call site, in code whose commit
message claimed it worked.

The owner decision that WAS outstanding - which group gets the servicer grant - is settled:
`ANALOG\ExchangeWebAdminsExecSupport`, on three modules, live in both config databases, each with
its module grant behind it. What is still unknown is **who is in it**, which is an AD question this
host cannot answer, and whether the capability works for a real member - check 2, deferred to prod
use.

**With that, nothing on this work stream is waiting on anyone.** Checks 2-4 are deferred by owner
decision, not queued. Treat the stream as closed unless a real prod run turns something up.

## Blockers

None live. The queued plans are waiting on an owner go, which is a gate, not a blocker -- see
`## Next`.

**Deployed versions are owned by the single `Deployed:` entry below.** The older per-work-stream
entries that used to sit here recorded where each stream stood *when it landed*; they are
history, not current state, and were never maintained. They are archived verbatim in
`docs/history/state-archive.md` (Archived 2026-08-14). Their unrun manual checks are owned by
their own `docs/*-Plan.md` files and consolidated for the older streams in
`docs/DevValidation-2.3.34.md`; two of them -- `docs/OperatorEmailResolution-Plan.md` and
`docs/ProtectedPrincipalResolution-Plan.md` -- also record that no independent implementation
review was ever obtained.

- **IN FLIGHT: make the remaining uncovered lines testable. ONE OF THREE FILES DONE.**
  Owner ruling 2026-08-05, verbatim: *"I don't care what's left. I do not want to have to deal with
  this in the future. make it work."* That is a standing instruction to finish the job, not to
  report on it again.
  **`SectionAccessGroupDirectory` DONE 2026-08-05 (`6642587`): 0% -> 98%**, security-critical
  coverage **66.0% -> 72.9%** locally (943/1294). Not yet confirmed on CI, and the floor is
  deliberately NOT raised until a green CI run reports a real figure.
  **Remaining uncovered, measured at `6642587`:**

  | lines | % | file |
  |---|---|---|
  | 171 | 63% | `Services/ProtectedPrincipalService.cs` |
  | 148 | 46% | `Services/PermissionValidator.cs` |
  | 31 | -- | four files at 80-98%, small remainders |

  **Nearly all of it is one shape: code that calls PowerShell to reach AD or EXO.** Biggest single
  blocks: `PermissionValidator.TryExpandGroupAsync` (70), `EnsureInitializedAsync` (27),
  `ValidateSelfGrantAsync` (21); `ProtectedPrincipalService.CheckTransitiveGroupMembership` (53),
  `ResolveViaActiveDirectory` (35), `ResolveProtectedGroupDn` (29). Tests cannot reach any of it
  without a domain-joined host with RSAT.
  **The fix is a seam over the PowerShell calls**, then tests against a fake. Same move already
  used three times here (`MailboxPermissionOutcome`, `CalendarFolderIdentity`,
  `SectionAccessDirectoryReading`) -- but those extracted PURE logic beside the I/O, which is
  cheap. This is the harder version: abstracting the I/O itself.
  **`ISectionAccessDirectoryCommands` (`6642587`) is the worked example to copy for the other two.**
  Its load-bearing shape: the command result carries rows and error as INDEPENDENT values, because
  a cmdlet that emits rows AND reports an error proved nothing about how many objects exist --
  collapsing them lets a partial failure read as a confident single answer. Rows stay nullable so
  the null-pipeline-row guard remains reachable. The service keeps its public constructor (DI
  unchanged) and takes a FACTORY on an internal one, preserving the session-per-lookup lifetime.
  **The remaining two are harder than this one was:** both sit on live request paths and both need
  a `PSCredential` (the Delinea directory-read secret) rather than the app-pool identity, so the
  seam has to carry credentials without widening who can see them.
  **RISK, and why this is not routine test work:** these are the live authorization paths that
  decide who may modify protected mailboxes. They reached PROD on 2026-08-04 and their manual
  checks have never been run. `sidf-1` was exactly this failure mode -- a change near this code
  locked every admin out of the page needed to repair it, caught only by review.
  **Constraints agreed before stopping:** pure extraction with no behaviour change; the existing
  authorization suites must pass UNMODIFIED as the proof (editing them signals a behaviour change
  and is a stop); one commit per piece so any single step is revertible; nothing considered done
  until CI is green.

- **MessageTrace null-pipeline-row NRE — FIXED in repo 2026-07-29 and now on BOTH instances.**
  **Basis corrected 2026-08-14:** this entry read "on dev in `2.3.31`, NOT on prod (prod is
  `2.3.30`, still carrying the defect)", which is falsified -- both hosts run `2.8.1`
  (`Deployed:` entry above), so the promotion this entry was waiting on happened.
  Plan `docs/MessageTraceNullRow-Plan.md` Status: Implemented. Live prod symptom: an EXO
  summary search failed with `Object reference not set to an instance of an object.` (banner
  doubled because `:348` and `:423` both format the same string). Root cause at
  `Services/MessageTraceService.cs:386`: the `Get-MessageTraceV2` pipeline returned a
  collection containing a **null element** and the loop dereferenced it; the `?.` chain guarded
  the property and its value but not `msg` itself. Latent since `b70b59d` (2026-06-04), NOT an
  MT-detail regression; data-dependent (the detail export emailed successfully the same day).
  Same defect class fixed in all four mapping loops (`:277`, `:314`, `:384`, `:501`) — every
  `GetProperty*` helper takes a non-nullable `PSObject` and dereferences `.Properties`.
  MessageTrace module `1.2.0 -> 1.2.1`, no base app bump. 830 tests green; non-vacuity proven
  per guard against the exact production exception.
  **OPEN:** live re-run of the failing search, now possible on either instance. The "then promote
  to prod" half of this item is done. **OPEN (OQ-1, non-blocking):** why EXO emits a null row at
  all is undiagnosed; the guard is correct regardless.

- **App version:** owned by `<VersionPrefix>` in `ExchangeAdminWeb.csproj` -- read the number
  there, never from here. The per-release history of that number is archived verbatim in
  `docs/history/state-archive.md` (Archived 2026-08-14).
- **Deployed: dev `2.17.0` (DLL written 2026-09-03 12:25; which module versions that build
  carries is unverified - read Module Config on dev; if the group modules show below
  `1.8.0`/`2.8.0`, `1a4b342` is not on dev yet), prod `2.8.1` (unchanged, see below).**
  Superseded record follows.
- **Deployed (superseded 2026-09-03): dev `2.15.0` (DLL written 2026-09-02 09:13), prod `2.8.1` (unchanged,
  2026-08-13 16:52:57)** -- dev re-verified from the assembly 2026-09-02 after the owner's
  deploy. **The 2026-09-02 deploy was DEV ONLY.** Dev `2.15.0` carries over `2.10.0`:
  boolean config checkboxes (2.12.0), BitLocker mandatory ticket (2.11.0 + module 1.1.0),
  CSV export for five modules (2.13.0 + five module bumps), the Risky Users module (1.0.0),
  the Intune Devices module (1.0.0, Graph status helpers 2.14.0), and the sidebar Home link
  removal (2.15.0). Their manual checks are now runnable; none has been run yet. The two new
  Graph modules also need `GraphDelineaSecretId` set in Module Config (owner, in progress
  2026-09-02 - both app registrations exist, Delinea secrets being created). Dev `2.10.0`
  carried over prod: nesting (app 2.9.0 work), the Event Log CSV ticket column, the
  cross-domain member-listing fix with the lst-1..3 review fixes, and the protected
  write-target feature with the pgwt-4..9 review fixes. Prod promotion is the owner's call
  after the dev manual checks.
  **That timestamp is the 2026-08-13 Migration size-check deploy, and it is on BOTH hosts** --
  the question `## Now` records the owner as never having answered. This is assembly-timestamp
  evidence only; the Migration module version was not read off either host, and the app version
  is unchanged from the 2026-08-11 `2.8.1` build, so nothing in the sidebar distinguishes them.
  **`2.8.1` carries, over `2.8.0`:** the Module Config servicer-grant warning (`46c8257`) -- the
  editor states that a servicer grant conveys no module access, badges any servicer group with no
  direct grant on the module's main permission, and raises a callout when a row is flagged. No
  authorization decision changed.
  **Verifying a Razor page change from the deployed DLL needs care, and a naive string probe lies
  three ways:** assembly literals are UTF-16 (a UTF-8 read finds nothing), `-match` is
  case-sensitive against the wrong encoding, and Razor splits literal markup at every `@expression`
  -- so a sentence interpolating `@module.DisplayName` is never one contiguous string. Probe short
  fragments that sit between expressions (`badge bg-danger`), and compare the deployed DLL against
  a LOCAL build of the same commit rather than against expectations. Method names are compiled away
  entirely and prove nothing either way.
  **Superseded deployed-version records (`2.8.0` and earlier) are archived verbatim** in
  `docs/history/state-archive.md` (Archived 2026-08-14).
- **2026-07-21 landed slices** (ff443ca, c2e2f6f, 502dd0e, 8c6f83f, 9dd39cd, b978362, 71d1daa)
  archived verbatim: `docs/history/state-archive.md` (Archived 2026-07-29).
- **AccountLockoutRemediation: TURNED OFF by owner** (2026-07-21). Does not work in this environment:
  WinRM reaches only ~5 of 38 domain controllers (HTTP 400 / Access denied / unreachable); permanent
  (owner: "won't be changed"). Discovery hides unreachable DCs (looks like "no lockouts found"); sweep
  silently drops the ~33 it can't reach. Owner disabled the module (runtime enablement, no code change).
- **Toolkit bug filed:** roethlar/AgentGovernanceBootstrap#7 -- completing a tracked item should
  auto-update the state record, not gate it behind an owner ask.

## Next up (prioritized)

Live backlog only. Items need an approved plan before code unless noted.

**-1. Make the remaining uncovered lines testable** (owner: *"make it work"*, 2026-08-05).
   **NO LONGER "the next code task" - resolved by the owner 2026-08-14 into two halves with
   different fates.** The item predated the four-plan queue and the pause and was never
   re-prioritised against them; the `drift` sweep flagged the contradiction.
   `SectionAccessGroupDirectory` is DONE (`6642587`, via `docs/CoverageRatchetRepair-Plan.md`).
   The remaining two files split cleanly, and the split is the ruling:

   **(a) `ProtectedPrincipalService` (63%) - ROLLED INTO THE QUEUED PLANS. Not separate work.**
   All four queued plans modify and test it (verified 2026-08-14 by reading each plan), and
   `docs/GroupMemberNesting-Plan.md` S1 changes this exact file at `:747`. Doing a standalone
   coverage refactor first would collide with S1 rather than help it. Coverage rises as a
   by-product; **after those plans land, re-measure on a GREEN CI run and raise the ratchet** -
   a one-line diff, per the instructions inside `.agents/review/coverage-floor.txt`.

   **(b) `PermissionValidator` (46%) - the real remaining gap, and nothing in the queue closes
   it.** No queued plan touches the file, and it is one of the paths the coverage floor gates
   (`tools/Test-CoverageFloor.ps1:72`). It is credential-carrying and on a live request path, so
   the work is the `ISectionAccessDirectoryCommands` seam extraction again.
   **Not small, and it needs its own plan** - `docs/ProjectConstitution.md` requires a written
   plan for authorization changes, and this is the authorization core. Queued **behind** the four
   plans; it is not "next" and must not be re-labelled as such without an owner ruling.
   Not startable on the August remainder: the comparable `SectionAccessGroupDirectory` work
   landed on 2026-08-05, a $395 day.

**-0.9. Eyeball a disabled submit button on dev** (accent, not blue). No plan needed -- it is the
   verification step for work already landed. **The deploy half is done:** both instances are
   deployed (version owned by the `Deployed:` entry under `## Blockers`), so only the look is
   outstanding.

**-0.4. PROD carries months of unvalidated work** -- through the current deployed build (version
   owned by the `Deployed:` entry under `## Blockers`), and its manual checks have never been
   run. Highest-consequence single check:
   `ANALOG\ExchangeWebAdmins` can still open Admin Settings (the `sidf-1` lockout scenario,
   hardest to recover from). See item 0 below for the consolidated list.

0. **Work through `docs/DevValidation-2.3.34.md` on dev (owner, Monday 2026-08-03).** The
   single consolidated checklist for everything that reached dev unvalidated -- four work
   streams' manual checks, ordered by consequence rather than by plan. Sections A-B are the
   protection controls and the reported L1/L2 friction; A1 (alias-addressed protected user
   is denied) is the GAP 4 regression test and must be re-run on prod after promotion.
   Nothing in it has been run. It copies no reasoning: each item cites its source plan.
1. **Live-validate the Bulk Job Runner (owner-deferred, 2026-07-20).** Runs from the dev instance
   against real PROD AD/Exchange (the only tenant there is; both instances on this server point at
   it). The runner *logic* is already covered by xUnit without a live run -- lifecycle (FIFO queue,
   cancel, recycle->Interrupted via `Initialize_FlipsOrphanedNonTerminalJobsToInterrupted`), per-row
   failure aggregation, completion notification (all variants), and the protected-principal block on
   **both** Finder and Type paths (`ConferenceRoomBulkProcessorTests`, closes GAP 3). What stays
   unvalidated until a live run: the Blazor UI (submit/progress/reconnect) and an actual EXO/AD room
   write. Do not close out until performed.
2. **Live-validate the ConferenceRooms PP gate + GM-3 self-service groups** — both landed and
   codex-reviewed; the only outstanding work on each is live/UI validation from the dev instance
   against PROD (`docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md`,
   `docs/SelfServiceGroupManagement-Plan.md`). Foldable into the same live session as item 1.
3. **Module packaging/import — DEFERRED (owner, 2026-07-22)** as low-value/high-cost. Not to be
   worked on or raised as next; no plan. End-state direction retained only as history in
   `.agents/decisions.md` (2026-07-22 deferral, refining 2026-06-29 & 06-18).
4. **AccountLockout user-notification — PARKED with the module (owner, 2026-07-22).** The whole
   `AccountLockoutRemediation` module is disabled/deferred (unusable in this environment); the
   user-notification question is parked with it and will be decided only if the module is picked
   back up. Not to be worked on or raised as next.
5. **Remove the redundant sidebar Home link - IMPLEMENTED 2026-09-01 (owner goal-directive
   the same day), commit `2128610`, base `2.14.0` -> `2.15.0`, no module bump.** The `Home`
   NavLink and both dead `.nav-home` CSS rules (`NavMenu.razor.css`, `app.css`) are gone; the
   brand link stays. One source-guard test, probe-proven. Suite 2163/0/3. NOT DEPLOYED -
   eyeball the sidebar on the next dev deploy (no bUnit harness). Original ruling follows.
   (owner, 2026-08-27). `NavMenu.razor:14` (the brand
   link, `Application:Name` falling back to "Admin Portal") and `NavMenu.razor:23` (the `Home`
   `NavLink`) both target `href=""` -- two controls, one destination. Owner's call is to drop
   `Home` and keep the brand link. Shared layout, so this is a base app version bump and no
   module bump. UI-only; no plan required beyond an owner go, but note the repo has no bUnit
   harness, so nothing automated will prove the sidebar still renders -- eyeball it on dev.
   Also check `nav-home` CSS for a rule that becomes dead.
6. **CSV export for five modules that have none** (owner, 2026-08-27): `DhcpAuthorization`
   (`ModuleCatalog.cs:467`) exports the current authorized-server list; `NamedLocations` (`:430`),
   `BlockedSenders` (`:265`), `BitLockerRecovery` (`:484`) and `Migration` status (`:174`) export
   their result sets, with the export offered only when results are present. Needs a plan: five
   modules is five module version bumps, and if the export goes through a shared helper it is a
   base app bump as well. **PLAN DRAFTED 2026-08-31: `docs/ModuleCsvExport-Plan.md`.** Its
   answers to the two questions this item posed: a new shared `CsvExport.Write` static helper
   generalizing the Event Log formatter's WriteField quoting contract (base bump; the Event Log
   formatter itself is untouched), and the BitLocker export's key question is D1 in that plan -
   RULED 2026-09-01: keys ARE exported (the owner overruled the drafted no-keys default; the
   export is a distinct audited bulk-disclosure event and keys never enter the audit itself).
   Interacts with item
   7 as planned: the BitLocker export carries the `searchTicket` the ticket plan adds, so its
   S5 waits for item 7's implementation.
7. **Mandatory Ticket field on BitLocker search** (owner, 2026-08-27). `BitLockerRecovery`
   (`ModuleCatalog.cs:484`) must require a ticket number before the search runs and before any
   result is displayed. **PLAN DRAFTED 2026-08-31, REVISED the same day:
   `docs/BitLockerMandatoryTicket-Plan.md`, awaiting a go to implement.** The owner ruled
   mid-planning that ServiceNow ticket validation IS coming app-wide, so the plan now also
   builds the shared backend-agnostic `ITicketValidator` seam with a per-module
   `ValidateTickets` Module Config switch (off = any non-blank text, the default; on =
   validate through the EXISTING dormant `Services/ServiceNowService.cs` client - while
   `ServiceNow:Enabled` is false, on refuses with a dormant-integration message: fail
   closed, never decorative). A ServiceNow client already exists and eight pages already
   call it; the per-module switch is the missing piece (`.agents/decisions.md`
   2026-08-31, corrected entry). Rewiring the OTHER ticket fields through the validator
   is future go-live work, which also must move `ServiceNow:Password` out of appsettings
   into the PAM store (recorded pre-existing Constitution gap). The ticket reaches both the search and reveal
   audit events through the `ticketNumber` parameters `AuditService` already has.
   Versions: module `1.0.2` -> `1.1.0` AND a base app bump (shared service + DI).
   Enforcement is in `BitLockerRecoveryService` (required parameter, fail-closed guard
   first), not the page, because UI hiding is not security.

Landed items 2, 5 and 6 of the previous numbering (single-room Finder PP gap, GM-3 task set, ASCII
sweep + lint gate) are archived: `docs/history/state-archive.md` (Archived 2026-07-30).

Ops track (not engineering): configure ConferenceRooms AD `DelineaSecretId` in the prod instance
(gates CR-1 in prod); `deploy.ps1` native `-PlanOnly` (workaround: `deploy-pipeline -PlanOnly`).

## Blockers / open gaps

- **OPEN — AccountLockoutRemediation not yet exercised on dev** (owner deferred, 2026-06-29). Run
  the package's own Manual Validation steps (live 4740 read, WinRM, quser/logoff parsing, real
  dry-run+logoff, protected-block) when ready. Gates the rule-4 user-notify decision above.
  Note the module is currently disabled by the owner as unusable in this environment (see
  `## Blockers`).
- **All known protected-principal *coverage* gaps CLOSED (in repo):** GAP 1 (`M365GroupManagementService`,
  2026-06-29), GAP 2 (`MigrationService`, 2026-06-30), GAP 3 (ConferenceRooms Finder bulk,
  2026-07-02), and the single-room Finder page path (2026-07-21, commit 2a97d09 — consolidated
  into `ConferenceRoomProtectionGate`). Every mutating module routes through the gate. Governing
  rule: `.agents/decisions.md` 2026-06-29 + Constitution §Protected Principals. This closes
  *which callers are gated*; GAP 4 below is a defect in *what the gate resolves*.
- **GAP 4 — FIXED IN REPO 2026-07-30 (`2.3.33`) and NOW DEPLOYED to both instances** (the
  deployed version is owned by the `Deployed:` entry under `## Blockers`; the earlier "dev
  `2.3.32`, prod `2.3.30`, still live on both" basis is falsified). **The fix is unproven in the field: its
  regression check has never been run on either instance** -- see the bottom of this entry.
  The defect: protected principals were reachable by
  secondary SMTP alias (found 2026-07-30, verified against live AD).
  `ProtectedPrincipalService.ResolveViaActiveDirectory`
  (`Services/ProtectedPrincipalService.cs:290-291`) queries only
  `(|(userPrincipalName=)(mail=)(sAMAccountName=))` — never `proxyAddresses` — and
  `MatchesIdentity` (`:438-465`) does not carry aliases in its candidate set. All 4 protected
  `user` rows in prod `protected_principal` carry 3 secondary aliases each; the CEO row
  `vincent.roche@analog.com` also answers to `VRoche@O365.analog.com`,
  `VRoche@analog.mail.onmicrosoft.com`, `Vincent.Roche@exchange.analog.com`. The app's exact
  filter returns 0 matches for the alias; the same filter plus `(proxyAddresses=smtp:...)`
  returns 1.
  - **Masked in MailboxPermissions**, where 0 matches means `null` means blanket denial
    (`Services/PermissionValidator.cs:124-131`) — the denial is currently the only control.
  - **Live in ConferenceRooms and GroupManagement**, which treat `NotFound` as *not protected*
    and allow (`Services/ConferenceRoomProtectionGate.cs:56-58`,
    `Services/GroupManagementService.cs:44-50`).
  - **Binding consequence:** relaxing `NotFound` to "allow" anywhere without simultaneously
    broadening resolution converts the masked bypass into a live one. Recorded here because it
    constrains any future work in this area, not only the fix below.
  - **Fix landed** (`docs/ProtectedPrincipalResolution-Plan.md`, Implemented 2026-07-30, commits
    `6faa92d`..`0eca01e`): resolution now falls back to Exchange, which returns the canonical
    primary address, so the alias case stops resolving `NotFound`. The `NotFound`-allows rule in
    ConferenceRooms and GroupManagement is deliberately unchanged -- the bypass closes because
    the alias no longer reaches it. The AD filter was **not** broadened to `proxyAddresses` (plan
    Non-Goals: two mechanisms for one job).
  - **Regression check, not yet run:** an alias as target must be denied citing the CEO user
    rule. Required on dev, then again on prod after promotion.
- **MailboxPermissions denies cloud-only mailboxes and mail-enabled groups — FIXED IN REPO
  2026-07-30 and DEPLOYED to both instances** as of 2026-08-04 (the deployed version is owned by
  the `Deployed:` entry under `## Blockers`; the earlier "still live on prod (`2.3.30`)" basis is
  falsified). **Unverified in the field --
  the L1/L2 friction has not been re-tested since the deploy.** Reported by the owner
  2026-07-30 as L1/L2 support friction. 16 prod denials 2026-06-30..2026-07-30 over 7 targets; 4
  were permanent under the AD-only filter (`Jabil.support@analog.com`,
  `sporting.tickets@analog.com` cloud-only; `adspstaff@analog.com`, `globalevents@analog.com`
  mail-enabled groups), 2 were AD-sync timing and self-resolved, 1 was malformed input. Not a
  regression from 2.3.32 — the code last changed functionally 2026-05-29. Fixed by the same work
  as GAP 4; the malformed-input case now gets an accurate not-found message rather than the
  outage-sounding one. **Open (plan OQ-2, non-blocking):** whether
  `sporting.tickets@analog.com` and `Jabil.support@analog.com` should be administratively
  reachable at all, or are artifacts of an unfinished decommission. Worth an answer before
  treating the friction as fully closed.

### Remaining superseded state notes

## Verification

- Commands are owned by `.agents/repo-guidance.md` (Verification) — read them there, not here.
  Deploy-host dependency for the ops scripts: `sqlite3.exe` on PATH.
- **Non-vacuous rule:** a change shipping with a new test must be proven — revert the fix, see the
  test fail, restore. Full policy: `AGENTS.md` (Verification) and `.agents/repo-guidance.md`.
  Per-work-stream manual-check lists live in each `docs/*-Plan.md`, consolidated for the
  outstanding ones in `docs/DevValidation-2.3.34.md`.

## Findings (environment / CI — still live)

- CI is real: it fails on real problems. Trust it. (`.github/workflows/ci.yml`, `windows-latest`.)
  Note: `dotnet format --verify-no-changes` treats analyzer *warnings* as fatal, so a stray
  warning (not just a failing test) reddens build-test. This bit master 2026-07-20..07-21: the
  Bulk Job Runner (`971555f`) left an xUnit1051 warning that kept build-test red for ~13 commits
  until fixed in `8c6f83f` (2026-07-21). Lesson: run the format check locally, not just the tests.
- On local macOS, a missing Windows COM DLL can nondeterministically drop xUnit collections (totals
  vary) — trust the failure *list*, not the total. `windows-latest` CI is unaffected. macOS builds
  need `-p:EnableWindowsTargeting=true`; Pester needs `pwsh` +
  `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`.
- Host-local tool facts (`sqlite3.exe` on PATH, Pester/PSScriptAnalyzer versions, which shell the
  suites run under) live in `.agents/machines.md`, not here.
- **RSAT IS installed on this dev box** (verified 2026-07-31: importing the `ActiveDirectory`
  module in a bare runspace succeeds with no errors), so
  `ADDirectorySearchService.IsAvailable` is **true** here and **false** on CI
  (`windows-latest`). Any test written as `if (!svc.IsAvailable) { ...assert... }` therefore
  **silently skips locally and only really runs on CI** -- it passes whether or not the code is
  correct. `ADDirectorySearchServiceTests.cs:100`, `:111`, `:121` are written that way. Assert
  AD-dependent logic through a pure function instead (see
  `ADDirectorySearchService.ClassifyOutcome`); a slice-1 non-vacuity probe caught this pattern
  passing with the fix reverted.
  - Corollary: a test that skips when a fixture is missing must use `Assert.SkipWhen`, never a
    bare early `return` -- a silent return is indistinguishable from a pass. Caught again in
    slice 3, where a live OU test searched for the literal `"OU="` expecting to match every DN
    (AD does not substring-match `distinguishedName`), got zero rows, and passed green with the
    mapping code deliberately broken. `ExchangeAdminWeb.Tests/ADDirectoryLiveTests.cs` is the
    pattern to copy: probe real name fragments to discover a fixture, skip loudly if none.
  - **SWEPT 2026-07-31: no `IsAvailable`-gated silent skips remain.** All three in
    `ADDirectorySearchServiceTests` (`:100`, `:111`, `:122`) now use `Assert.SkipWhen` and
    report as skipped here rather than passing. Note the asymmetry that remains by design:
    those three assert the fail-soft contract and so run on CI and skip on this box, while
    `ADDirectoryLiveTests` does the reverse. **Neither file alone proves the service works** --
    the pure-function tests are what hold on every host.
  - What live tests are FOR: pure functions cannot prove a PowerShell property name is right.
    `Properties["DisplayName"]` where the cmdlet returns `Name` compiles, passes every unit
    test, and yields an empty string at runtime. That class of bug needs a real directory.
- `deploy.ps1` still lacks a native `-PlanOnly` (deferred with owner visibility;
  `deploy-pipeline -PlanOnly` covers the prod dry-run requirement).

## Active sources

- `AGENTS.md` — process/behavioral contract (Prime Invariants first).
- `docs/ProjectConstitution.md` — highest engineering authority.
- `.agents/decisions.md` — durable decisions (most recent: 2026-07-31, protected-principal admin
  input validated under the app-pool identity rather than the Delinea directory-read secret).
- Active plans: `docs/BulkJobRunner-Plan.md` (Implemented, live validation pending);
  `docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md` (Implemented 2026-07-21,
  live/UI validation pending); `docs/MessageTraceDownloadLink-Plan.md` (Implemented 2026-07-29,
  all four slices landed; **on dev as `2.3.31`, 9 manual post-deploy checks not run**);
  `docs/OperatorEmailResolution-Plan.md` (**Implemented 2026-07-29** -- app `2.3.32`; on both
  instances since 2026-08-04; 8 manual post-deploy checks not run; implementation openreview not
  obtained);
  `docs/ProtectedPrincipalResolution-Plan.md` (**Implemented 2026-07-30** -- app `2.3.33`; on
  both instances since 2026-08-04; 6 manual post-deploy checks not run; no independent review);
  `docs/ProtectedPrincipalInputValidation-Plan.md` (**Implemented 2026-07-31**, all four slices
  landed -- the plan file says so; the "Approved, not started" reading here was stale);
  `docs/SectionAccessSidStorage-Plan.md` (**Implemented 2026-08-03**, all 4 slices landed -- the
  plan file says so; the "slice 1 of 4" reading here was stale. No open owner gates);
  `docs/CoverageRatchetRepair-Plan.md` (**Implemented 2026-08-05**, all three slices);
  `docs/AdminUIRedesign-Plan.md` (**In progress** -- manual checks unrun).
- **Plan-status drift, unresolved (flagged 2026-07-30, owner ruling needed):** three plans still
  carry a pre-landing `Status:` although code evidence says they shipped —
  `docs/BlockedSendersLoadTiming-Plan.md` (Approved; deferred load is live at
  `Components/Pages/BlockedSenders.razor:169`, module `1.0.2`), `docs/Comms10kReplaceUx-Plan.md`
  (Approved; module is at the plan's target `1.0.4`, commit `5e0c19e`), and
  `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md` (Approved -- In progress; implemented by
  `430305a`, module now `2.3.0` vs the plan's `2.0.12`). Not corrected in this sweep: marking a
  plan Implemented is a completion claim, and the ConferenceRooms one may be genuinely partial.
- Review loop finding pp-finder-1: implemented and committed (`.agents/review/index.md`).
- Review loop findings ppv-1..4 (2026-07-31): all four fixed and committed; see
  `docs/history/state-archive.md` (Archived 2026-08-14) and `.agents/review/index.md`. Dispatch artifacts (prompt, schema, raw verdict) are tracked at
  `.agents/review/ppvalidation.*` so the pass is reproducible.

## Unrecorded repo memory

- None known. Engineering rules → `docs/ProjectConstitution.md`; module contract →
  `docs/AdminModuleSpec.md`; work-stream history → `docs/*-Plan.md` + git log.

## Archived 2026-09-02 (drift sweep)

Rotated out of `.agents/state.md` verbatim: four `## Now` entries that were fully landed
with nothing further outstanding on their own stream (as distinct from the 2026-09-01
implemented-but-not-deployed streams, which stay live because their manual checks are
still pending), plus a superseded `## Next` queue list and a stale reviewer-transport
note.

### Rotated from `## Now`

- **Review loop CLOSED 2026-08-31.** fsr-1 (`f6a4eb1`) and fsr-2 (`6f4d972`) both closed
on coder-side proofs under the same-day ruling: reviewer verification rounds are
CRITICAL-only and each needs an explicit owner go (`.agents/decisions.md`).

- **GROUP SEARCH FOREST SCOPE: IMPLEMENTED 2026-08-31 (owner go: "make this search work
  first"). ON DEV since 2026-08-31 (third same-day deploy) and VERIFIED live: the
  "domain admins" search returns rows from BOTH domains with the Domain column (AD and
  WINROOT), checked browser-side after the owner's deploy.** Group Management's group
  search previously queried only the app
  credential's home domain and showed no domain per row - the owner hit it validating
  protected targets on dev ("Domain Admins" ambiguous, other domains unreachable). The
  search now queries the forest global catalog (same success-only-cache and ":3268"
  guard as `ADDirectorySearchService.ResolveGlobalCatalog`, fail-soft to local) and
  results carry a Domain column. `GroupManagement 2.5.0`, no base bump. 7 new tests
  (pure DomainLabel + wiring tripwires), non-vacuity probed (wiring reverted, tripwire
  failed, restored). **The owner deferred replacing the search with an autocomplete
  ("not sure about autocomplete yet") - noted, not planned.**
  **NEXT: nothing - dev check done 2026-08-31. Prod promotion is the owner's call.**

- **TOKEN BUDGET: IMPLEMENTED 2026-08-27 (owner go the same day; S1-S4 all landed).**
  Baseline correction `b2b2887` first (the plan's August figures had counted transcript lines,
  not billed requests); S1 tool + `transcript-root:` entry in `.agents/machines.md` `fb2a44b`;
  S2 Pester (17 tests, four-mutation non-vacuity proof) `be97436`; S3 baseline + log `fe1c7bf`;
  S4 is the Token Budget section in `.agents/repo-guidance.md`, landed in the commit that set
  the plan's status to Implemented.
  `docs/TokenBudget-Plan.md` (`c2ae60c`, revised `5b54222`). Owner request 2026-08-14: a
  token-budget-friendly implementation approach for September, with usage tracking built in.
  Canonical detail is in the plan; do not duplicate it here.
  **D1 RULED 2026-08-14, AMENDED by the owner 2026-08-27: Fable 5 implements end to end,**
  one fresh session per slice; codex/GPT-5.5 reviews, Gemini reserved as an owner-dispatched
  third harness. Rationale and the Sonnet fallback live in `.agents/decisions.md` (2026-08-27).
  **D2 WITHDRAWN - `.agents/playbooks/drift.md` already owns it.** Reducing this file is the
  sweep's first checklist item, and it rotates `## Now` entries **verbatim** to
  `docs/history/state-archive.md` rather than rewriting them. Invoke with `playbook drift`, or
  `catchup` which offers it. It was put to the owner as a decision in error - the owner caught
  it. **A push-status paragraph was deleted from this file at the same time**, per the same
  playbook's 2026-07-11 deleted-on-sight ruling. **Size, the volatile figure this argument rests
  on:** 138 KB / ~51,800 tokens (roughly 18% of every request) when the plan was written; 58 KB
  after the 2026-08-14 drift sweep. Re-measure rather than quoting either number.
  **Measured baselines: CORRECTED 2026-08-27 - the 2026-08-14 figures counted transcript
  lines, not billed requests** (one request writes 1-5 identical-usage JSONL lines). Canonical
  corrected numbers live in the plan's "Correction 2026-08-27" under Measured baseline
  (`b2b2887`), and in `.agents/token-baseline.json` once S3 lands; do not quote the old
  7,876-request / ~$2,311 figures. Per-request unit facts stand: a re-prime at 280K context
  costs ~$1.75, the cache TTL is 5 minutes, idle gaps are billed.
  **Haiku 4.5 is permanently disqualified** - 200K context, 72.3% of requests exceed it. Do not
  re-propose. Sonnet 4.6 and every older Opus are strictly dominated on price and capability.
  **DONE 2026-09-01: baseline regenerated for the full August** (owner go the same day).
  Full-month totals: 5,987 requests, est $1,709.00 at opus-5 list rates, mean context
  400.6K, 4,409 requests over 200K; the 2026-08-27 early cut it replaces read 5,299 /
  $1,519.50, so 08-28..08-31 added ~$190. Canonical figures live in
  `.agents/token-baseline.json`; re-measure rather than quoting these.
  **NEXT: nothing on this stream** - the protocol section in `.agents/repo-guidance.md`
  governs how the September queue is implemented.

- **MIGRATION SIZE CHECK: FIXED AND DEPLOYED 2026-08-13** (`b4029c6`). Migration `1.7.0` ->
  `1.7.1`. No base app bump (module-scoped behaviour only).
  **WHICH ENVIRONMENTS was never stated by the owner. Measured 2026-08-14 instead: BOTH.** Each
  host's `ExchangeAdminWeb.dll` was written 2026-08-13 16:52:57, dev and prod alike - see the
  `Deployed:` entry under `## Blockers`. That is assembly-timestamp evidence, not a module-version
  read. **The app version is unchanged at `2.8.1`, so the sidebar cannot tell this build from the
  previous one**; the only way to confirm which host carries the fix from inside the app is the
  Migration module version reading `1.7.1` in Module Config.
  **The deploy also carried everything else outstanding at the time**, including the Migration
  batch selection and favicon work already on both hosts and the five Risky Users PLAN commits
  (docs only, no code).
  Owner: *"it blocks users whose combined mail + archive size > 100GB, but that's wrong.
  both can be up to 99GB. not combined."* Then: *"fix the size check to be per mailbox,
  not total archive+primary. each mailbox needs to be 99gb or less. combined can be
  whatever."*
  **The rule now lives in ONE place** - `MigrationEligibilityResult.MailboxExceedsQuota` /
  `ArchiveExceedsQuota` / `ExceedsQuota` in `Models/MigrationModels.cs`. The service sets the
  sizes and reads those properties rather than doing its own arithmetic, so the page badge and
  the eligibility verdict cannot drift apart. `CloudQuotaGB` default is now `99` in BOTH places
  that carried `100` (`ModuleCatalog.cs` DefaultValue and the `MigrationService.cs:35` fallback).
  **A STORED CONFIG VALUE STILL WINS AND WAS NOT CHECKED.** If dev or prod has `CloudQuotaGB`
  set in `config/exchangeadmin.db`, it overrides the new default and a stored `100` would allow <!-- lint: allow (owner ruled leave-it, 2026-07-27: runtime config DB is intentionally created outside source control) -->
  a 100 GB mailbox. Read it on both hosts and set it to 99 if present.
  **9 new tests in `MigrationQuotaTests.cs`, proven non-vacuous**: reverting the model to the
  combined rule fails 6 of the 9; restored, all 9 pass. 1701 passed / 0 failed / 3 skipped
  (pre-existing AD skips), build + format + `git diff --check` + ASCII clean.
  **What this fix deliberately did NOT touch, because the go named the size check only:** the
  size-lookup failure path at `MigrationService.cs:185-189,201-208` still marks a user
  **Ineligible** when the on-prem size cannot be read. Since size is not a criterion in the
  source script at all, that is arguably a second invented block - an on-prem connection hiccup
  currently reads to the operator as "this user cannot migrate". Unraised as work; owner's call.
  **Provenance finding worth keeping: the size gate has no basis in the source script.**
  `D:\source\scripts\Exchange\CheckMigrationElligibility.ps1` checks five things - migration in
  progress, already a cloud mailbox, `SEC_ITAR_USERS` membership, not-a-cloud-mailbox on
  move-back, and `AuxArchive` on move-back. All five are implemented. There is NO size check in
  it, and no plan or decision in this repo ever asked for one; it appears in
  `docs/MigrationEligibilityProtectedFlag-Plan.md:48` as an already-existing fact. The owner
  kept the gate and corrected its arithmetic rather than removing it.
  **The one check the script has that the module does not: an on-prem AD account precondition.**
  The script wraps its whole body in `if ($ntid.SamAccountName)` after a `Get-Recipient`, so a
  mail contact, mail user, distribution group or cloud-only object gets no verdict at all. The
  module has no `Get-Recipient` and no SamAccountName gate - it uses the address it is given -
  so those objects receive a normal Eligible/Ineligible answer. Recorded, not worked.
  **Also recorded, not worked: `ExchangeServiceBase.cs:605-609` looks the user up in AD by
  `UserPrincipalName -eq <the email address typed in>`, where the script used SamAccountName.**
  Where a UPN differs from the primary SMTP address the lookup returns nobody, `:614` returns,
  and the excluded-group check is silently skipped for that person - a fail-open on the only
  compliance-motivated rule in the script. The owner confirmed 2026-08-13 that ITAR users ARE
  currently excluded, which is consistent with this: it works for everyone whose UPN matches
  their email and silently misses everyone else. Unproven either way. To settle it, run the
  eligibility check on a `SEC_ITAR_USERS` member whose UPN is not their primary SMTP address.
  **NEXT: nothing on this stream.** Deploy is the owner's call and carries the earlier
  Migration/favicon work with it.

### Rotated from `## Next`

- **Queue status (corrected 2026-08-28, pgwt-9):**

1. `docs/GroupMemberNesting-Plan.md` - **IMPLEMENTED 2026-08-27** (see `## Now`); manual
   checks ride the next deploy. Do not restart.
2. `docs/ProtectedGroupWriteTarget-Plan.md` - **IMPLEMENTED 2026-08-28** (see `## Now`);
   manual checks ride the next deploy. Do not restart.
3. `docs/RiskyUsersModule-Plan.md` - **IMPLEMENTED 2026-09-01** (see `## Now`); all
   seven slices landed. NOT DEPLOYED - manual checks ride the next dev deploy. Do not
   restart.
4. `docs/IntuneDeviceManagement-Plan.md` - **IMPLEMENTED 2026-09-01** (see `## Now`); all
   seven slices landed. NOT DEPLOYED - the app registration and Delinea secret still
   block the first live Graph call; manual checks ride the next dev deploy. Do not
   restart.
5. `docs/TokenBudget-Plan.md` - **Draft, awaiting a go. D1 ruled, D2 withdrawn; no owner
   decision is outstanding.** Not a feature: how the other four get implemented, plus
   `tools/Get-TokenUsage.ps1` and a tracked baseline. Independent of 1-4 and worth landing
   first, since it changes what the others cost. Start at S1. See the entry in `## Now`.

All of the above are docs-only so far. (Stale even when archived: item 5 no longer
matched the Token Budget entry in `## Now`, which was DONE by the time this was rotated.)

- **Do not re-derive the reviewer transport.** `.agents/review/harnesses.local.json` was a
current cache hit for `codex-cli 0.147.0`; both openreview passes that session ran clean
through it. The `refresh_token ... revoked` line on stderr was documented noise on the
API-key path and did not affect either run (exit 0, `capability_ok: true` both times).
**Superseded 2026-09-02: `.agents/machines.md` now records `codex-cli 0.152.0`** - re-verify
against that file rather than this archived version number.

## Archived 2026-08-14 (drift sweep)

Rotated out of `.agents/state.md` verbatim. The first two entries came from `## Now`
(both declared closed with nothing outstanding); the rest are the work-stream entries that
had drifted under `## Blockers`, which that section itself labelled history rather than
current state.

### Rotated from `## Now`

- **Migration Status batch selection: DONE, ACCEPTED, and DEPLOYED TO BOTH 2026-08-10** --
  *"looks fine. ran through a few checks, calling it good."* Two rounds; Migration `1.5.0` ->
  `1.6.0` -> `1.7.0`. **Dev and prod both run app `2.8.0`** (verified from both assemblies).
  `docs/MigrationBatchSelection-Plan.md`, decisions D1-D8.
  **Acceptance, not a completed check list.** Which of the plan's checks were run is unknown; the
  load-bearing ones (mixed selection per button, `CompletedWithErrors` actionable, deselect and the
  "queued" wording) are recorded as unverified. Anyone re-opening this should not read the owner's
  go as evidence those passed.
  **A VERSION ERROR the owner caught from the deployed page, and it is the recurring one.** Round 1
  set Migration `1.6.0`; three later commits changed Migration behaviour (`1ef7fae`, `2ff7d7f`,
  `52eb7e9`) and none bumped it again, so dev ran the round-2 code under the same module version as
  the build tested before any of it existed. **Two builds sharing one version is worse than a wrong
  number** -- the `2.5.1` failure repeating. What made it hard to see: the base app version WAS
  correctly bumped to `2.8.0` in the same window for the favicon, so the sidebar read right and the
  module version looked right by association. **The two versioning rules fire independently; a
  correct app bump is not evidence about the module.** Fixed to `1.7.0`.
  **Round 1 passed 4 of its manual checks and failed on the ACTION MODEL.** Ticking two batches and
  clicking Resume returned *"No batches to act on. Skipped 2: ... (Completed), ...
  (CompletedWithErrors)."*
  **`CompletedWithErrors` is a real Exchange batch status that appeared NOWHERE in this codebase.**
  Every status comparison on the page was an exact match against a hardcoded list, so such a batch
  could not be deleted, could not be resumed, was not swept by Clear Completed, and drew the
  unknown-status grey badge. **Pre-existing; the checkboxes only made it visible** - and made it
  worse in one way, since the operator now ticks a row and is told there is nothing to do, where
  before the button simply never rendered.
  **THE LESSON, and it generalises past this page: an exhaustive-looking status allowlist written
  from the statuses a developer has seen is a silent filter, not a safety rail.** No test could have
  caught it -- every test used the same status list the code did. It appeared THREE times in this
  one page (Delete, Resume, and the per-user Report button), each hiding something the operator
  wanted, and all three were found by using the app, not by review or tests.
  **Owner's action model (D3), which replaced mine:** Delete acts on ANYTHING ticked (Exchange
  decides; a refusal is that row's own named failure); Remove Completed acts on exactly `Completed`,
  never `CompletedWithErrors` -- *"a batch that finished with errors is not a batch that finished"*,
  and sweeping it destroys the evidence; Resume/Retry acts on anything idle but restartable.
  **D4: Resume eligibility is defined by EXCLUSION** -- everything except the working statuses
  (`Syncing`, `Starting`, `Stopping`, `Completing`, `Removing`) and `Completed`. That inversion is
  the fix's substance: an unanticipated status now defaults to VISIBLE and lets Exchange refuse it,
  rather than silently vanishing from the UI. **`Completed` had to be excluded explicitly -- the
  first draft of this rule excluded only the working statuses, and the owner caught it:
  idle-and-restartable is not the same as idle.**
  **D6: the standalone Clear Completed sweep is REMOVED.** All-or-nothing destruction sitting beside
  "Clear selection" sharing a word; owner: *"unacceptable."* Buttons are now Delete / Remove
  Completed / Resume-Retry / Untick all -- no two share a leading verb.
  **D7: Report is offered on every user row**, not only rows that already look broken.
  **D8, from a second dev pass: the selection did not clear after an action, and the result claimed
  work that had not happened.** *"it says it removed but didn't deselect. removal isn't instant and
  the message is unclear since the 'removed' entry is still there."*
  (a) `PruneSelection` only drops batches that have LEFT the table, and removal is asynchronous -
  the batch sits at `Removing` and stays ticked, inviting a second click on work already in flight.
  Batches Exchange ACCEPTED are now deselected; ones that FAILED stay ticked, because nothing was
  queued for them and retrying is the likely next move. A blanket `Clear()` would have taken the
  failures and the skips with it, which are the rows the operator still needs.
  (b) **The cmdlets return when Exchange ACCEPTS the request, not when the work is done**, so
  "Removed 1 batch(es)" rendered over a row still visible on screen. Verbs are now "Queued removal
  of" / "Queued restart of" plus a line saying Exchange finishes in the background. **The same lie
  lived in `MigrationService` for the single-row path** and is fixed there too - fixing only the
  bulk wording would have left it on the row that reported it.
  **The pattern across D3-D8 is one thing: this page kept ASSERTING states it had not verified** -
  that a status list was exhaustive, that a queued removal was a completed one. Both are the same
  error in different clothes, and both were found by an operator using the app.
  **What round 1 got right and should not be re-litigated:** checkbox mechanics, `BatchName`-keyed
  selection surviving a re-sort, the inline ticket field beneath both batch and user rows, and the
  single aggregating executor (per-batch audit inside the loop, audit failures as warnings, one
  notification per run). Checks 1, 4, 5 and 5b all PASSED on dev.
  **Also withdrawn: manual check 7** ("delete a batch in EAC, refresh, confirm it de-ticks"). It
  presumed a local store of migrations; `GetMigrationBatchesAsync` runs `Get-MigrationBatch` against
  Exchange on every load, so there is no cache to go stale. I wrote it on a caching reflex that does
  not apply here.
  **`mbs-1` (MEDIUM, `1ef7fae`) was found by a `codereview` pass over round 1** and is closed:
  per-user actions prompted for the ticket at the top of the table because the inline confirm
  matched a BATCH NAME while `StageUserAction` sets an EMAIL. The plan's D1 covered that case and
  the implementation delivered only the outer half. **23 guards, ten mutation probes and 1645 green
  tests all passed while it was broken, because none of them reads the plan.**
  Detail in `.agents/review/findings/mbs-1.md`; the rule it earns is that when an owner ruling and
  a later implementation note disagree, the ruling wins and the note is what gets corrected.
  Owner report 2026-08-10: *"the exchange migration status page needs checkboxes on each row to
  allow batch clear/delete and resume. the ticket number entry field for individual items needs to
  be closer to the actual button people hit ... because we routinely have ~50+ in-flight and the
  ticket number entry field is buried at the top of the table and UI doesn't make that obvious."*
  **Two independent defects, both only visible at 50+ rows.** (1) No multi-select anywhere in the
  status tab - the only bulk affordance is all-or-nothing `Clear Completed`. (2) `StageBatchAction`
  renders the ticket confirm bar ABOVE the table, so clicking Resume on row 47 puts the input
  off-screen while that row's buttons all go disabled - the visible feedback is that the buttons
  stopped working.
  **Owner rulings, both in the plan's Decisions section (canonical there, not duplicated in
  `.agents/decisions.md`):** D1 outer Batches table only, no checkboxes on the inner per-user table.
  D2(a) a bulk action acts on the eligible rows and NAMES every skipped row with its status - a
  skip is not a failure, skips are not audited (no write attempted), the bulk buttons stay enabled
  whatever the selection, and skipped rows stay ticked. D2(b), disabling the button on a mixed
  selection, was rejected as a return to acting one row at a time.
  **Slice 1 (`Services/MigrationBatchActionPlanner.cs`, 36 tests):** pure partition of a selection
  into eligible/skipped, selection pruning across a reload, and the SINGLE definition of which
  statuses each action permits - the per-row Resume/Delete buttons now read it too, because two
  copies of "which statuses may be deleted" is how a bulk action and a row button come to disagree
  about the same batch. Keyed on `BatchName`, never row index: the table re-sorts on every header
  click, so an index-keyed selection silently retargets to whatever now occupies that position.
  **Slice 2 (page):** checkbox column, select-all over loaded batches, selection toolbar, and
  `ExecuteBulkBatchAction` EXTRACTED from `ClearCompletedBatches` rather than written a second time
  - that code already had the subtle parts right (one audit event per batch inside the loop;
  audit-write failures as WARNINGS so an audit failure cannot make a completed removal look failed;
  one summary notification per run, not fifty). Clear Completed now routes through it, and a guard
  asserts it still does: if it forks again there are two sets of aggregation rules.
  **Slice 3:** the ticket confirm bar moved from above the table to directly beneath the acting
  row, as one `RenderFragment` rendered in two places. The top-of-table position survives for
  actions naming no single row (Clear Completed, both bulk actions) and for a row that has since
  disappeared - otherwise the bar becomes unreachable.
  **Slice 4:** Migration `1.5.0` -> `1.6.0`, README, this file.
  **Verification: 1642+ passed, 0 failed, build/format/`git diff --check`/ASCII clean. TEN
  mutations, each confirmed on disk before trusting the verdict and confirmed gone after** -
  checkbox removed, audit hoisted out of the loop, per-row Delete forking its own status chain,
  Clear Completed forking its own loop, a stale plan reused at confirm time, pruning dropped from
  the reload, the inline confirm reverted, the top-of-table fallback deleted. All caught.
  **Two of my own guards were wrong and only the probe found them - this is the reusable part.**
  (1) Disabling the membership check in `PruneSelection` left all 34 slice-1 tests GREEN, because
  every prune test happened to select every loaded row. That mutant ADDS unticked rows to the
  selection on the next reload, and the following Delete removes batches the operator never chose.
  **A test that only checks nothing was wrongly REMOVED from a set says nothing about what was
  wrongly ADDED to it.**
  (2) `GetBatchRowMarkup` sliced the row markup up to the first
  `@if (expandedBatch == batch.BatchName && batchUsers != null)` - text that also appears earlier
  inside the Details button - so the slice stopped short of the markup it covered and reported a
  real change as missing. Now brace-balanced. **A marker that occurs more than once is not a
  boundary.**
  **Environmental trap worth keeping: `Copy-Item` restoring a probe backup carries the BACKUP's
  timestamp, so MSBuild judged the DLL up to date and kept testing the mutant** - three tests
  "failed" against correct restored source. Reading the file back to verify the restore was not
  enough; the build has its own idea of current. Touch the file after any timestamp-preserving
  restore.
  **NEXT: nothing on this work stream.** It is accepted and closed on dev. The only outstanding
  item is a PROD deploy, which carries this plus the favicon and is the owner's call.

- **Protected-principal servicing: CODE COMPLETE at `80d2759`, DEPLOYED to dev and prod 2026-08-10
  as `2.7.0` (verified from both assemblies). All 15 modules, all 3 review findings fixed. 1586
  passed / 0 failed / 3 skipped, format clean. THE SERVICER GROUP NOW EXISTS AND IS CONFIGURED -
  `ANALOG\ExchangeWebAdminsExecSupport` (SID `S-1-5-21-8915387-325452579-1788637320-710891`),
  granted on dev and prod identically for `ProtectedServicer:MailboxPermissions`,
  `ProtectedServicer:CalendarPermissions` and `ProtectedServicer:OutOfOffice`. NO MANUAL CHECK RUN,
  so the capability is configured but unproven end to end.** That is the whole of what remains;
  see `## Next`.
  Owner: *"all of them. every place where a principal is protected we need to allow a priv
  group to act on them anyway."*
  **THE THREE FINDINGS, all fixed** - `.agents/review/findings/pps-{1,2,3}.md`, all `[x]` in
  `.agents/review/index.md`. All three were the same shape, which is now this repo's signature
  failure: **the service was right and the PAGE, or the call site, was wrong.**
  **pps-2 (`efd25ad`)**: `ADAttributeEditor.razor` blocked at lookup with no servicer consultation
  and hid the edit UI, so the serviced save gate was unreachable; undo preview took no principal
  while execute did. **This was the Emergency Disable two-gate shape I recorded during the work and
  then applied only to the six remaining SERVICES, never to the pages of the nine already done.**
  The rule it earns: *a page gate that hides the write UI is part of the authorization decision,
  not a display detail.* A serviced operator now also sees a banner - an override they cannot see
  is one they cannot decline.
  **pps-3 (`a345342`)**: the undo service evaluated `NoteFor(...) is null` inside a boolean and
  discarded the note on the ALLOW path; Emergency Disable kept its note in the operation trace and
  out of the audit event. **The helper returns a nullable note precisely so permission and record
  cannot be separated; a bare null test defeats that by design.** A guard now forbids that call
  shape across `Services/` and `Components/Pages/`, with one bounded exemption for the no-audit
  preview path.
  **pps-1 (`57ab7a5`)**: bulk CSV called the back-compat overload that can never service, so a
  servicer was allowed one row at a time and refused for the same mailbox in a CSV; and
  `ExecuteOnPrem` re-checked authorization but not protection after its confirmation dialog.
  **The reviewer named only Mailbox for the on-prem half - Calendar had the identical defect and
  was found by reading the pair rather than assuming they had diverged.**
  **Two caveats on the review, both load-bearing for whoever reads it next.** Its auth token was
  revoked mid-run (`refresh_token_invalidated`), so it **never produced a final consolidated
  report** - the findings were recovered from the reasoning trace in `.git/codex-review-out.txt`
  (untracked, may not survive) and each was verified against the current code before recording.
  Severities and proposed fixes are MINE, not the reviewer's. And it found these by treating the
  commit messages as CLAIMS to check: my own `e1547e7` message asserting these modules honour
  servicing is what made the gap visible.
  **Its analysis phase confirmed clean:** DI lifetimes (no captive dependency), ASCII, the opt-in
  list against real gates for all 15 modules, Migration's per-target notes surviving the
  audit-failure rewrap, Self-Service not letting the grant bypass ownership, Conference Rooms'
  deliberate null-principal bulk refusal, and Licensing's request-thread decision.
  Plan `docs/ProtectedPrincipalServicerAllModules-Plan.md`, revised after grok review (5 findings,
  3 HIGH, all folded in).
  **The commits, one module or slice each:** `b351005`/`1ba7d49`/`8fb0592` (audit `extra` channel,
  servicer service Scoped -> Singleton, Conference Rooms), `e1547e7` (seven modules at once: MFA
  Reset, Emergency Disable, Comms-10k, AD Attribute Editor + undo, Mailbox, Calendar, Out of
  Office), `c0fc79e` GroupManagement, `5f07fe8` M365GroupManagement, `037365c` SelfServiceGroups,
  `c745f78` Migration, `15c001d` AccountLockoutRemediation, `6f7f2ac` LicensingUpdates.
  **`Services/ProtectedPrincipalServicing.cs` is the shared helper every module uses** -
  `NoteFor(...)` returns the audit note or null-to-refuse, `Extra(...)` wraps it for an audit call.
  Returning a nullable note rather than a bool is deliberate: a caller cannot allow the action while
  forgetting to record why.
  **The invariants every gate holds, and what to check any future module against:** protection is
  evaluated FIRST and never weakened; fail-closed outranks servicing (unavailable / ambiguous /
  check-failed still deny, because they do not know whether the target is protected, so there is no
  refusal to override); a null acting principal REFUSES; the grant is PER MODULE; the note names
  both the authorising group and the rules overridden; and it travels in the audit event's `extra`,
  never `errorDetail`, which is written as null on success and would silently discard it.
  **Five shapes the plan did not predict, all generalisable:**
  (1) **Emergency Disable has TWO gates** - the page hides the write UI at lookup, the service
  blocks again at the write. Both must honour servicing or the servicer never reaches the button.
  (2) **`ValidateTargetMailboxAsync` serves THREE modules**, so it returns a `TargetValidation`
  record and takes an explicit `moduleId`; a borrowed id would let a Mailbox grant authorise
  Calendar or Out of Office.
  (3) **Migration's gate was a DELEGATE**, and per-target: `PartitionByProtectionAsync` returns the
  serviced notes as a LIST beside allowed/excluded, one per override, because a batch-level "some
  target was serviced" cannot say which - the question an audit exists to answer.
  (4) **Off-request-thread work cannot decide servicing.** LicensingUpdates' `ApplyChanges` runs
  under `Task.Run`, where the acting principal is not ambient and reading one would attribute the
  override to whoever owns the thread. The decision moved to `EvaluateProtectionAsync` on the
  request thread; `ApplyChanges` receives already-decided notes.
  (5) **A note keyed for one loop is not keyed for the other.** LicensingUpdates keys the apply loop
  on `PrincipalKey` (ObjectGuid when present) and the audit loop only ever sees a UPN, so a single
  view would have made the write with no record of who permitted it. Two views, both tested.
  **Three services needed an internal TEST SEAM** (`SelfServiceGroupService.CheckMemberProtectedAsync`,
  `AccountLockoutRemediationService.GuardTargetUsersAsync`,
  `LicensingUpdatesService.EvaluateProtectionAsync`): each sits behind a credential fetch and a live
  directory read, so the servicer path is unreachable from the public method in a test. The project
  already exposes internals to the test assembly; all three stay decisions with no side effects.
  **Every existing protection suite passes UNMODIFIED in substance** - the only edits were
  constructing a servicer service over a store with no grant (so it denies) and passing
  `actingUser: null`. That was the standing gate: editing one to accommodate a servicer path would
  mean a refusal quietly became an allow.
  **Non-vacuity proven per module by reverting the servicer path**, each time confirming the revert
  had actually landed before trusting the verdict and confirming it was gone after: GroupManagement
  2 of 6 fail, M365 4 of 8, SelfServiceGroups 1 of 6, Migration 2 of 8, AccountLockout 1 of 7,
  Licensing 3 of 8 - in every case exactly the allow-path tests, with the refusal tests still
  passing.
  **Slice 0a caught a real defect in itself:** nine methods gained the `extra` parameter and
  `LogLookupAction` was left without the merge - a parameter accepted and silently dropped.
  `AuditExtraChannelTests` found it. That is the most repeatable mistake in this work.

### Rotated from `## Blockers` (landed work-stream history)

- **The protected-principal SERVICER capability was unreachable for a day, and the export UI was
  unusable. Both fixed 2026-08-07. NOT DEPLOYED.**
  `docs/ProtectedPrincipalServicerAdminUI-Plan.md` and `docs/MessageTraceExportUX-Plan.md`, both
  Implemented, both reviewed clean by **grok** (`grok-4.5-build`, 0 findings) as plans before any
  code was written. MessageTrace `1.4.0 -> 1.4.1`.
  **SERVICER: marked IMPLEMENTED 2026-08-06 while no operator could reach it.** The service was
  registered, consumed by `BlockedSenderProtectionGate` and unit-tested - and nothing wrote the
  `ProtectedServicer:<moduleId>` key it reads, so no group could ever be granted it. Verified at
  the time of the fix: zero such rows in BOTH live config stores.
  **Rule this earns, and it generalises: a capability is not implemented until the person meant to
  use it can reach it.** Registered + consumed + unit-tested is not usable, and marking the plan
  Implemented on the first three hid the missing fourth. `docs/ProtectedPrincipalBreakGlass-Plan.md`
  is corrected in place rather than quietly re-marked.
  **The hazard that shaped the fix: `SaveSectionAccess` -> `SaveAll` -> `ClearAndInsert` REPLACES
  the whole section-access store.** `ModuleConfig.razor` is safe only because it reads every alias
  and writes the full map back, so the new editor JOINS that read-modify-write instead of adding a
  second save path. Two behavioural tests against a real SQLite store pin it - the failure would be
  silent destruction of authorization state, whose only symptom is a team quietly losing access.
  **Caught while implementing:** adding the alias to `policyAliases` for the save path also made the
  ORDINARY grant loop render it again - unwarned, captioned as plain module access, presenting a
  protection bypass as a normal grant. Excluded, and guarded.
  **EXPORT UX: nothing misbehaved and the feature was still unusable.** Two different exports (all
  results/summary CSV; max-50 per-message detail) shared one panel that named neither, a correct
  "export to get them all" pointed at the wrong control, and a correctly-disabled button gave no
  reason. Owner: *"it's unclear how to get anything... the download button doesn't work."*
  **Presentation was the defect** - no threshold, service or delivery mechanism changed.
  **`ppsvc-1` (HIGH) was found by the codex review of the landed diff, and it is the case for
  running that review even when a change looks safe.** On a server where section access has never
  been configured, `GetGroupsForSection` falls back to the legacy app-wide `Security:AllowedGroups`
  unless the section is fail-closed - and the fail-closed set is built from CATALOG POLICY ALIASES,
  which a `ProtectedServicer:` key deliberately is not. So the most privileged grant in the app
  defaulted to its widest audience.
  **Worse than the review stated, and checking rather than accepting is what found it:** the review
  located it at the admin page pre-populating the editor. `ProtectedPrincipalServicerService.Evaluate`
  reads the same method, so the bypass was live on an unconfigured store with **no admin
  involvement at all** and no stored row to find afterwards. The page-only remedy it also offered
  would have left the real hole open. Fixed in the service: any key under the servicer prefix is
  fail-closed by construction, prefix-matched because the keys are built per module at runtime.
  **What makes this worth recording: the commit had already been reviewed clean as a PLAN by grok,
  was written against a plan that named the storage hazard explicitly, and shipped with 8 passing
  guards.** The defect was in none of that - it was a pre-existing fallback in a file the diff never
  touched, reachable only because the new key was not the KIND of thing the existing fail-closed set
  knew about. A plan review cannot see that, and no test suite that never runs against an
  unconfigured store can either.
  **NEXT: deploy, then the manual checks.** Load-bearing: save a module's ordinary access and
  confirm a configured servicer grant SURVIVES (the whole-store-replace hazard, directly), and a
  servicer-group member actually unblocking a protected sender - the only end-to-end proof the
  capability does anything at all.

- **Message Analysis: the 90-day search reached dev/prod BROKEN in `2.6.0` and is repaired in
  `4b976e9`. MessageTrace `1.3.1 -> 1.4.0`. NOT DEPLOYED; 7 manual checks unrun.**
  `docs/MessageTraceHistoricalRetirement-Plan.md` Status: Implemented.
  **The owner found it by using the app.** Any range wider than 9 days still went to
  `Start-HistoricalSearch` and told the operator results would be emailed, while the chunked
  in-app search built in `03a9999` sat unused in the same build. And the emailed report is one
  those operators often cannot open: `Get-HistoricalSearch` returns only a portal `FileUrl`
  needing an interactive sign-in, which is the barrier the work existed to remove.
  **How it survived three passes: `72b8047` deleted the page branch correctly but rested on a
  false premise; `90486d2` reverted it WHOLE, taking the correct deletion with the bad premise;
  `03a9999` restored only the service half.** A revert that undoes two things and a repair that
  redoes one is not a shape diff review catches. The 1.4.0 version bump was lost the same way,
  so the catalog understated this module through the whole work stream.
  **1483 tests passed against the defect.** No bUnit harness exists, so no test can see which
  branch a Razor handler takes; the planner and chunking tests were all green and all irrelevant.
  `MessageTracePageRoutingTests` is the answer - a source-level tripwire, explicitly not
  behavioural coverage, because a reintroduced branch is exactly what a tripwire can see.
  **Its first cut was itself too weak and the probe caught it:** the day-count guard was scoped to
  `RunTrace`'s body, but the defect declared the comparison as a FIELD, so it passed with the
  defect reinstated. Now scoped to the whole page.
  **Rule this earns, and it generalises past this module: a plan marked Implemented on a green
  suite, in a repo where no test can render the page, states more than the evidence supports.**
  `docs/MessageTraceAccuracy-Plan.md` said exactly that in its own caveat and was still marked
  Implemented; it is now corrected in place.
  **NEXT: deploy and run the 7 manual checks.** Load-bearing: a 30-day search renders rows in the
  page with no email promise, and no gap or duplicate row at a chunk boundary - the second is
  invisible to every test, because the rows either side of a missing window look continuous.

- **BitLocker Recovery module INTEGRATED 2026-08-07. New module `BitLockerRecovery 1.0.0`, no base
  app bump (Constitution: adding a module does not bump the base version). NOT DEPLOYED; 12 manual
  checks unrun.** `docs/BitLockerRecoveryModule-Plan.md` Status: Implemented.
  Module count 24 -> 25, configurable policy aliases 33 -> 34. **1471 passed / 0 failed / 3 skipped**
  (the 3 skips are the pre-existing AD-unavailable tests), build/format/`git diff --check`/ASCII all
  clean. Guard proven non-vacuous: removing the descriptor fails 3 catalog tests.
  **Authored outside this repo** as an isolated package at
  `D:\source\scripts\BitLocker\ExchangeAdminWebModule` (it lives beside the `Export-BitLockerKey.ps1`
  task that writes the archive it reads). The package is the upstream; the host now carries a copy.
  **Read-only module, so no ticket, no protected-principal check, no confirmation dialog** -- those
  guard writes and this performs none. `FailClosed: true` and disabled by default anyway, because a
  recovery key decrypts a whole disk. **The REVEAL is the audited security event, not the search.**
  **Three defects were caught by review before integration, and the pattern is worth keeping: the
  package validator passed clean at every step while all three were live.** It is a shape checker,
  not a compiler.
  (1) A per-computer `PowerShell.Create()` inside the result loop, each re-importing the AD module --
  51 runspaces for a default search, 501 at the cap; now one `InitialSessionState` runspace per
  search, matching `ADDirectorySearchService`.
  (2) A failed live-AD lookup discarded successful ARCHIVE rows and returned a bare failure -- on a
  recovery call that is the worst possible direction to fail. Now returns the rows with a warning,
  the `MessageTraceResponse.IsPartial` shape.
  (3) `ExecutionPolicy` unqualified -- **the module did not compile at all**, found only by building
  it against the host DLL out-of-tree. `Microsoft.PowerShell.ExecutionPolicy` is the enum's real
  home, which is why all six existing host call sites write it fully qualified.
  A fourth (unguarded `Directory.Delete` in test teardown, failing 29 of 30 tests on held SQLite
  handles) surfaced in the same out-of-tree run; the host's own tests all wrap this in try/catch.
  **`codereview` generation pass over `81fd069..e39e18f` returned 2 findings, both real, both
  fixed** -- `blr-1` (HIGH, `53f3ac5`) and `blr-2` (MEDIUM, `61552d9`); see
  `.agents/review/findings/blr-*.md`.
  **blr-1 is the load-bearing lesson and it is the same shape as ppv-1 and sid-1: the guard was
  built correctly and the reasoning about what it covered was wrong.** The module took real care
  to keep a recovery key out of the audit record on the REVEAL path -- and then wrote one there
  from the SEARCH path, because the page audited the raw contents of a box that is *documented to
  accept a pasted 48-digit recovery key*. So the leak sat on the happy path, in a durable store
  readable by more people than may reveal a key, and it never tripped the `RevealRecoveryKey`
  event that exists to record exactly that disclosure. Three earlier review rounds over this same
  code missed it, including two of my own.
  blr-2: a live search that hit its result cap before finding a key rendered "searched
  successfully" -- the module's own fail-closed rule ("no key exists" and "I could not look" must
  not look alike) violated by the page, while both services reported truncation correctly.
  **DEPLOYED TO DEV by the owner 2026-08-07 and exercised. One defect found that way: `blr-3`,
  fixed.** Archive search, live AD fallback, and reveal all work; the archive holds 124,942 rows.
  **`blr-3` (HIGH): a SECOND search rendered "No recovery keys found ... searched successfully"
  for the whole time the live AD query was in flight**, then replaced it with the keys it had just
  said did not exist. `SearchAsync` emptied `results` but never cleared `searched`, so the
  zero-results branch rendered over emptied data. Live AD takes seconds - long enough for an
  operator on a recovery call to read a definitive "no key on file" and act on it.
  **The first search after a page load was always fine**, which is why it survived: the obvious
  manual test passes. Only the owner's second-search sequence exposed it.
  **Third instance of one pattern, and it is now the thing to watch in this repo: the service was
  correct and the PAGE was wrong** (`blr-2`, the MessageTrace historical branch, now `blr-3`). The
  page is the only part no test can see - no bUnit harness - so service correctness keeps proving
  nothing about what an operator gets. Source-level assertions are the only automation that
  reaches it.
  **A near-miss in the fix's own proof, worth remembering:** the first non-vacuity probe used
  `\r\n`-suffixed replacements that silently matched nothing, so 1 of 3 guards fired and the other
  two looked weak. Verifying the file contents AFTER the revert - rather than trusting the
  reverting script - showed the revert had not applied. **A non-vacuity probe that does not
  confirm its own revert landed can manufacture a false verdict in either direction.**
  **`blr-4` (MEDIUM) was CAUSED BY the blr-3 fix and found on PROD.** Suppressing the stale result
  left the results area blank for the seconds a live AD query takes, so the page read as hung.
  **Removing a wrong answer is only half a fix** - a blank region is not neutral, and an operator
  who thinks the app froze reloads mid-search. Fixed with an in-flight indicator, plus a forced
  render and `Task.Yield` before the work: `Microsoft.Data.Sqlite`'s `*Async` methods complete
  SYNCHRONOUSLY, so the handler can finish the whole archive query without ever yielding to the
  renderer, and an indicator that is never painted is not an indicator.
  **Two of blr-4's three guards were false coverage on the first cut**, and only the mutation probe
  exposed it: they matched `@if (isSearching)` on the Search button's own spinner and message text
  still sitting inside the disabled block, so both passed against the broken page. **A guard that a
  broken page satisfies is worse than no guard, because it reads as coverage.** Now anchored to the
  markup each condition gates. Second time in two days a probe caught a weak proof (see the `\r\n`
  revert that silently matched nothing, above) - **the probe is doing more work than the tests.**
  Module `1.0.0 -> 1.0.1` for blr-1/2/3: they are behaviour changes landing after the module first
  reached dev, so the version must distinguish the two builds.
  **The isolated package at `D:\source\scripts\BitLocker\ExchangeAdminWebModule` is now STALE.**
  The host copies of `BitLockerRecovery.razor` and `BitLockerRecoveryTests.cs` carry blr-1/2/3 and
  the package does not (the three service files are still identical). **The host is authoritative
  from here** - a future re-copy from that package would silently revert three fixes, one of them
  a cleartext-key leak into the audit log.
  **NEXT: the remaining manual checks.** Unrun: an archive-only (deleted-from-AD) machine shows
  `Archive only`; a broken archive path errors rather than showing an empty table; search by
  pasted 48-digit key redacts in the audit record (`blr-1`).

- **HANDOFF 2026-08-05, as of `e9ad05d`. Tree clean, CI green.**
  **Coverage 64.7% -> 66.0%** over the gated security-critical scope; the floor was raised with it
  (`b5df487`) -- the value itself lives in `.agents/review/coverage-floor.txt`, which owns it.
  CI went green at `fd8fa69` -- first success since 2026-07-30 -- after two unrelated
  defects were fixed: the host-dependent `"DA"` test (`506c2d4`) and the coverage dilution
  (`8d614b4`, `4daf5d9`).

- **CI WAS GREEN as of `fd8fa69`** (2026-08-05, run 31021097853) -- first success since
  2026-07-30. Both jobs passed. CI's own figures at that commit: `1321 passed, 0 failed,
  9 skipped`, coverage `65.1% (844 / 1296)`, floor satisfied. Two separate defects had to be fixed
  to get there: the host-dependent `"DA"` test (`506c2d4`) and the coverage dilution.
  **Volatile -- three commits have landed since (`a224c9a`, `03f7e31`, `be7c02a`, all docs and
  governance). Check the current run rather than trusting this line.**

- **Button states themed 2026-08-04, app `2.5.5`, NOT DEPLOYED.** `2.5.4` reached dev and the
  owner still saw blue buttons, with screenshots. **`2.5.4` was not wrong, it was incomplete** --
  and the screenshots carried the diagnosis: same Gruvbox page, checkboxes orange but submit
  button blue; and on M365 Group Management an *enabled* `btn-primary` rendered orange while
  *disabled* ones rendered blue. **Every blue button in every screenshot was DISABLED** (empty
  forms).
  Cause: Bootstrap 5.0 hardcodes `.btn-primary{background-color:#0d6efd}` instead of reading
  `--bs-primary`, and states its variants on **two-class selectors**
  (`.btn-primary.disabled, .btn-primary:disabled`, `:hover`, `:focus`, `:active`), which outrank a
  single-class override. So `2.5.4`'s rule won for a resting button and lost for every other
  state. Fixed by stating each state per variant at matching specificity; `!important` deliberately
  avoided.
  **Rule this earns: "the rule exists and reads a token" is not the same question as "the rule
  wins."** Specificity is invisible to every check that only greps for token usage -- which is
  what let this ship twice. `EveryBootstrapColourVariantOverridesItsDisabledState` and
  `NoBootstrapBrandColourIsHardcodedInOurStylesheet` now cover it.
  **Also worth keeping: a screenshot of a DISABLED control is not evidence about the enabled one,
  and vice versa.** The inconsistency in those images was the clue, not noise.

- **Export retention + admin bulk jobs view -- LANDED 2026-08-04, app `2.5.2`, new module
  `AdminBulkJobs 1.0.0`. NOT DEPLOYED.** `docs/AdminBulkJobs-Plan.md` Status: Implemented;
  **9 manual checks unrun.**
  **Version correction (owner caught it 2026-08-04).** These landed with NO base bump, leaving the
  repo reading `2.5.1` while dev and prod were already running a *different* `2.5.1` -- verified
  from both assemblies (`FileVersion 2.5.1.0`, dev written 19:13, prod 18:39). The new module
  correctly bumps nothing (Constitution: a new module does not bump the base version), but moving
  export retention in-process is shared app-wide startup behaviour and ships real behaviour change
  -- the app now deletes files it never deleted before -- so it earns a base bump. Now `2.5.2`.
  **Rule this cost: two builds carrying one version number is worse than a wrong number**, because
  during an incident nothing distinguishes them. Check the deployed assembly before assuming the
  repo is ahead.
  **DURABLE RULING, owner 2026-08-04: "there are and will be no scheduled tasks."** This supersedes
  `docs/MessageTraceDownloadLink-Plan.md` D1 and the fallback suggestion at
  `docs/FutureModules-Plan.md:308`; both are annotated in place. Unattended work in this app is a
  one-shot call in the `Program.cs` startup pass -- never a timer, never a hosted worker, never an
  external task.
  **What prompted it: export retention was documented for months and never performed.** Measured,
  not inferred: `schtasks /query` on this host returned **266 tasks, none belonging to this app**,
  while `MessageTraceExportStore` stated as fact that one deleted exports older than 30 days,
  `README` repeated it to operators, and **openreview F4 rejected a configurable retention key
  specifically to avoid disagreeing with that task.** Nothing enforced the window: exports
  accumulate forever, and past day 30 the reports page shows **Expired for a file still on disk** --
  wrong in the direction that matters. Exposure small by luck: 2 files, 0.6 KB each, 6 days old.
  **First fix was wrong and was withdrawn.** I shipped two PowerShell scripts plus a task
  installer, which reproduced the missing-external-dependency shape rather than removing it -- an
  install step someone must remember on every host forever, whose absence is invisible. The owner
  ruled it out; the scripts and their Pester file were deleted in the same commit that added
  `MessageTraceExportStore.PruneExpired`, called at startup beside the existing job-record prune.
  Records and the files they describe now expire by one mechanism.
  **The narrow scope is load-bearing: the export directory sits INSIDE the audit log root**, so the
  sweep matches an anchored filename pattern, is non-recursive, and uses an exclusive cutoff; most
  of the 11 tests assert what SURVIVES. It never throws -- retention must not be able to stop the
  app booting. The 30-day constant stays a constant, which reinstates F4's reasoning rather than
  overturning it.
  **`AdminBulkJobs` closes the gap the Conference Rooms scoping fix opened.** After that fix a
  *running* Message Analysis export was visible nowhere (`/message-analysis/reports` lists only
  terminal exports) and `GetActiveJobs()`/`GetRecentJobs()` had no caller. The new page at
  `/admin-bulk-jobs` is the legitimate home for both, with a **Module column** -- the column whose
  absence let a MessageTrace row pass as a Conference Rooms job. **`FailClosed: true`**, because
  aggregating every module's submitters, tickets and targets is exactly what the section-access
  boundary exists to prevent leaking. Cancel and Remove here are deliberately NOT module-scoped:
  crossing modules is the page's purpose, and both audit.
  **No base app bump, per the Constitution** ("Adding a new module does not bump the base app
  version"). A `2.5.2` bump was made and reverted on reading that rule.
  **ON DEV as of `2.5.3`. Checks 2 and 3 PASS on real data**, observed after the 22:24 restart:
  both real exports (6 days old, inside the window) survive, and all 87 `.jsonl` audit logs in the
  parent directory are intact -- that second one is the deletion the anchored pattern exists to
  prevent, now proven against a real audit tree rather than a temp fixture.
  **The deleting path has NOT run in production conditions:** no export on either instance has
  reached 30 days yet, so only the guards are proven live. Checks 4-9 (the admin page) unrun.

- **Conference Rooms bulk jobs panel -- ALL 6 SLICES LANDED 2026-08-04, app `2.5.1`,
  ConferenceRooms `2.3.2`, NOT DEPLOYED.** `docs/ConferenceRoomsBulkJobPanel-Plan.md` Status:
  Implemented; **10 manual checks unrun** (the panel is markup, so they are the only evidence).
  Reported as "an old test job with no way to do anything about it".
  **The reported row was not a Conference Rooms job.** Identified against the live dev jobs
  database, not inferred: it is a `MessageTrace_DetailExport` from 2026-07-29, the only row in
  `bulk_job` on that instance. It appeared because of two defects that hid each other -- the
  panel read `GetActiveJobs()`/`GetRecentJobs()`, both **unfiltered across every module**, and
  `JobKindLabel` was a two-way ternary that rendered anything not-Finder as "Room Type (bulk)".
  Had the label been honest the leak would have been obvious on sight.
  **The severe half was never reported and was found by reading the code:** the panel renders a
  Cancel button per active row and `CancelJob` takes only an id, so **a Conference Rooms operator
  could cancel a running Message Analysis export.** Submitter and ticket of another module's work
  also crossed a section-access boundary.
  **Owner rulings.** Jobs moved to their own tab (`"move the jobs to another tab. out of the main
  UI, because it's going to push the actual module down further and further"`) -- which withdrew
  the plan's D1 retirement gate entirely, since the shared-dismiss objection that made it hard to
  rule stops mattering once the panel is off the working surface. Plus a per-job Remove: hard
  delete, terminal-only (enforced in SQL -- deleting an active job would leave the runner holding
  a token for a missing row), module-scoped, and **audited**, which is what makes a hard delete
  acceptable since the audit log is a separate store.
  **Retention stayed at 30 days after a conflict was raised rather than implemented.** The owner
  asked for 90; `PruneFinishedBefore` DELETES terminal rows at 30 and the store is shared with
  Message Analysis, so a 90-day panel would show nothing between day 31 and 90. Owner ruled 30.
  **NEXT: deploy `2.5.1` to dev and run the 10 manual checks.** Check 1 (the reported row is gone)
  and check 6 (no Cancel offered for a running foreign job) are the load-bearing ones.

- **Theme support -- ALL 5 SLICES LANDED 2026-08-04, app `2.5.0`, NOT DEPLOYED.**
  `docs/ThemeSupport-Plan.md` Status: Approved (owner directive: *"add theme support properly.
  include 8-10 most popular themes in a dropdown selector that replaces the dark/light icon"*).
  Ten themes -- Light, OLED Black, Solarized Light/Dark, Dracula, Nord, Gruvbox Dark, Monokai,
  One Dark, Tokyo Night -- in a grouped `<select>` replacing the sun/moon toggle.
  **The bulk of the work was not the themes.** 61 rules in `app.css` keyed off the `dark` class
  rather than reading a token, so a third theme would have rendered light-mode cards, form fields
  and tables on a dark canvas. Slice 1 converted all of them; a theme is now pure data and the
  eleventh is a copy-paste. 12 tint tokens added so the coloured alerts and table rows stop being
  the hardcoded exception. **The `dark` class survives for exactly one rule, `color-scheme`,**
  which is not styling -- it is how the browser is told to paint scrollbars, native select popups
  and date pickers, which no stylesheet can reach.
  **`UiThemeCssTests` is the load-bearing guard and the reason this is safe to extend:** a theme
  block missing a token silently inherits Light's value, so a dark theme missing `--ui-fg` renders
  near-black on near-black -- an invisible page, not a crash and not a compile error. It also
  forbids any rule naming a theme or keying off `dark`, scanning the isolation stylesheets too, so
  it doubles as the guard against the `app.css`/`NavMenu.razor.css` mirror trap.
  **A real bug was caught by re-reading the rendered script, not by a test:** the JS lookup tested
  its map value for truthiness, but the value IS the isDark flag -- so every LIGHT theme read as
  unknown and fell back to the default. Solarized Light would have been unselectable with nothing
  failing. Fixed to a membership test; a regression test pins the C# twin to the same rule.
  Legacy `localStorage` value `dark` migrates to OLED, so an existing user sees no change.
  **NEXT: deploy `2.5.0` to dev and run the plan's 6 manual checks.** Check 2 (scrollbars and
  native selects follow the theme) is the one automation cannot reach at all.

- **Admin UI redesign — ALL 6 SLICES LANDED 2026-08-04; `2.4.0` DEPLOYED TO DEV by the owner and
  SEEN. Two defects reported from that first look, both fixed in `2.4.1` (in repo, NOT deployed).**
  `docs/AdminUIRedesign-Plan.md` Status: In progress (manual checks unrun). Owner rejected the
  existing UI outright — "it does not look like a professional app, it looks like a vibe coded
  toy... the important security group entry system is half-baked" — after seven mockup rounds
  were rejected for keeping the same materials. What was finally approved is **structural, not
  cosmetic**: tabbed panes each with their own scroll, so the page never grows with the group
  count; grants as aligned table rows, never chips; one save bar per page naming the dirty
  section. The three approved mockups are kept in `docs/mockups/` (`q1-tabbed.html`,
  `q2-adminsettings.html`, `q3-picker.html`); 15 rejected drafts deleted so there is
  no ambiguity about which is current.
  **Owner ruling D1: app-wide**, so all 22 pages' chrome changed and all 22 need a smoke pass.
  **Slices 1-2** (`01a5efc`, `a934e07`): token layer driving both themes with Bootstrap's `--bs-*`
  pointed at it — that indirection is what lets the ~20 unconverted pages follow the theme
  without markup changes. OLED dark (`#000` canvas, silver text, cyan accent). Nav rows 3rem ->
  1.9rem so all 22 modules fit. **Nav icons were a real fix, not a restyle:** all 24 SVGs are
  hardcoded `fill='white'` and vanish on a light sidebar, so each is now a CSS mask taking
  `currentColor`.
  **Slices 4-5** (`708a375`): both admin pages rebuilt. Eight Save buttons -> two save bars.
  **Deliberately NOT built:** the diagnostics tab from mockup q2 — new capability, not a redesign
  of existing capability, and its OQ-2 (live probes vs cached) is undecided.
  **Two bugs fixed rather than restyled around:**
  **B1** (`4489730`, `1b77dd4`) the picker could not see WINROOT — `Get-ADGroup` was issued with
  no `-Server`, so only the joined domain was searched; now targets the forest global catalog.
  Two further defects surfaced *while verifying that fix*, both invisible to unit tests: reading
  `GlobalCatalogs` off the returned PSObject yields empty, producing the server string `":3268"`
  which `Get-ADGroup` ACCEPTS while quietly serving locally; and `ResolveGlobalCatalog` cached its
  own FAILURES, so one transient `Get-ADForest` error would have pinned the picker to
  local-domain-only for the process lifetime — silently undoing B1 in production.
  **B2** (`857be64`) the app had **no unsaved-changes guard anywhere** (verified: no
  `beforeunload`, `NavigationLock` or `OnLocationChanging` in any component). `AdminPageDirtyState`
  is an extracted service with 14 tests because page fields cannot be tested here.
  **Two process lessons recorded in the plan, both earned:** a flaky live test was chased rather
  than muted and turned out to be hiding the real caching defect above (I attributed it to two
  wrong causes first); and scripted line-range edits silently deleted two whole markup blocks
  while the project **still built clean**, because absent Razor markup is not a compile error —
  caught only by diffing against pre-edit backups.
  **Slice 7 — post-deploy corrections (`e442df4`, `31142d9`, `d360cfe`), app `2.4.1`.** Neither
  defect was findable off-host; both needed a running instance.
  **(a) Every module in the nav read as greyed out / disabled.** Slice 2 migrated only the
  CSS-isolation copy in `NavMenu.razor.css`. **`wwwroot/app.css` carries a deliberate MIRROR of
  the same nav rules** — present because isolation scoping has been unreliable on published IIS —
  and it was never touched, so it still had the painted-white icons and the old rails. Worse,
  `--sidebar-bg` was never a token at all: hardcoded `#2c3345` light / `#1a1d2e` dark. The pane
  stayed a dark slate slab while its labels took the light token set's grey. Both copies now
  resolve to `--ui-nav-bg`. **Rule: any nav/shell rule edited in `NavMenu.razor.css` must be
  edited in the `app.css` mirror in the same change** — a green build proves nothing about which
  copy the browser used.
  **(b) The protected-principal lists had no row delineation.** The slice-5 note justified leaving
  them alone because they were "already row-per-entry rather than chips" — which conflated
  row-per-entry with row-*delineated*. They were borderless lines in one bordered box. Now the
  same `.adm-tbl` as the Access grants table. **The lesson is the reasoning, not the CSS: "not the
  thing the owner rejected" is not the same as "good."**
  **NEXT: deploy `2.4.1` to dev (`.\tools\deploy-pipeline.ps1 -Dev`, ELEVATED) and run the plan's
  manual checks.** Checks 1-6 runnable; 7-8 exercise the rebuilt panes. None has been run.

- **Section-access groups stored as SIDs — ALL 4 SLICES LANDED + REVIEWED 2026-08-03, app
  `2.3.35`, NOT DEPLOYED (dev is `2.3.34`, prod `2.3.30`).**
  `docs/SectionAccessSidStorage-Plan.md` Status: Implemented. A `codereview` generation pass
  over `b872861..0a50d01` returned **2 findings, both real, both fixed** — `sid-1` (HIGH,
  `54e762d`) and `sid-2` (MEDIUM, `019b814`); see `.agents/review/findings/sid-*.md`.
  **sid-1 is the load-bearing lesson and it contradicted my own slice-3 commit message:** I
  wrote that an unmigrated store "fails CLOSED" under exact comparison. It does not.
  `WindowsPrincipal.IsInRole` resolves NAMES as well as SIDs — measured,
  `IsInRole("Domain Users")` is **true** — so until the fix a deferred or halted migration left
  name rows authorizing exactly as before, with the cross-domain ambiguity intact, during
  precisely the window the migration exists to survive. Non-SID values are now discarded at all
  three comparison sites (handler, checker, job snapshot). Same shape as ppv-1: the guards were
  sound, the reasoning about what they guaranteed was not.
  **Frontier pass DONE 2026-08-04** over the whole range including those fixes: **1 more HIGH
  finding, `sidf-1` (`4f1de2b`)** — and it was a defect **the sid-1 fix introduced**. That fix
  filtered non-SID values on EVERY requirement, including the static `Security:AdminGroups` from
  appsettings, which no migration converts and which is deployed here as
  `ANALOG\ExchangeWebAdmins` (verified against the live prod file, not the sample). Deploying
  `2.3.35` would have denied every admin `/admin-settings` — the page needed to repair
  section-access fallout, so the failure removed its own remedy. The filter is now scoped to
  `ResolveDynamically`, the flag that distinguishes the migrated store from appsettings.
  **Frontier tier resolved (owner ruling):** the old pin `gpt-5.6-sol` 404s; codex at its default
  model is the strongest available here, so frontier = standard pair with `grade: fallback`, and
  effort `max` is rejected by this gateway (xhigh is the ceiling). A future escalation must halt
  to the owner rather than redispatch. Recorded in `.agents/review/harnesses.local.json`.
  **Still open (sidf-1 Known gaps):** `Security:AllowedGroups`/`AdminGroups` remain name-based, so
  the cross-domain ambiguity is closed for module access and **still open for admin access**.
  Pre-existing and an explicit plan Non-Goal, not a regression — but it wants its own decision.
  **NEXT: the plan's 6 manual post-deploy checks on dev — none run.** Authorization cannot be
  proven off-host and a mistake locks people out of every module, so dev first. Check 6 (app
  boots and authorizes from stored SIDs with AD unreachable) and check 3 (`winroot\Enterprise
  Admins` still reaches DhcpAuthorization) are the load-bearing ones.

- **BOTH protected-principal work streams — CODE COMPLETE + REVIEWED 2026-07-31, ON DEV as
  `2.3.34` (deployed by the owner 2026-07-31, verified from the assembly). Manual checks NOT
  run — owner checking Monday. Prod is still `2.3.30` and carries every defect below.**
  A `codereview` generation pass over `10d1593..521bb6e` (both streams, 10 commits, ~2950
  lines) returned **4 findings, all real, all fixed** — see `.agents/review/index.md` rows
  `ppv-1..4` and `.agents/review/findings/ppv-*.md`. **ppv-1 was HIGH and is the load-bearing
  lesson: the Exchange-fallback work closed the alias bypass for on-prem principals and
  REINSTATED it for cloud-only ones**, because `ResolveWithExchangeFallbackAsync` branched on
  address equality instead of the `ExistsOnPrem` flag that same work had introduced and never
  read. Both streams had been reported complete with "every guard proven non-vacuous" before
  the review found it — the guards were sound, the gap was in what was thought to test.
  Fixes: `a6927b2` (ppv-1), `0940964` (ppv-2, a DN with an escaped comma was mangled by
  `DOMAIN\` stripping), `49b134d` (ppv-3, Save mid-validation dropped the pending entry),
  `9a43455` (ppv-4, live tests reported PASSED not SKIPPED with no directory).

- **Protected-principal admin input validation — CODE COMPLETE 2026-07-31, all 4 slices on
  `master`; on dev as `2.3.34`, NOT yet exercised through the real page.**
  `docs/ProtectedPrincipalInputValidation-Plan.md` Status: Implemented. Owner asked why
  an O365 group cannot be added to protected principals; investigation found the real defect is
  that `Components/Pages/AdminSettings.razor:394-397` saves **any** typed string — the
  `ADIdentityAutocomplete` on Users and Groups (`:127`, `:149`) only suggests, and the add
  handlers never check that the value came from a suggestion. OUs (`:171`) have no picker at all.
  An unresolvable **user** or **OU** row silently matches nothing; an unresolvable **group** row
  is worse in a different way — `CheckGroupMembershipAsync` (`Services/ProtectedPrincipalService.cs:629-633`)
  fails **closed**, so it turns every check into a denial that reads as a directory fault.
  **Owner rulings:** *(a)* cloud-only objects are **non-protected by design**, so refusing an
  Entra-only group is correct behavior and Graph is a non-goal; *(b)* **D1** AD-unreachable
  refuses the Add with "try again later" (admin-only page, and nothing works without AD anyway);
  *(c)* **D2** validation runs under the **app-pool identity, not the Delinea secret** — least
  privilege, recorded in `.agents/decisions.md` 2026-07-31 **with its environment-scope limit**.
  Key design constraint: `ADDirectorySearchService.Search` is fail-soft (returns `[]` on
  unavailable, throttle timeout, exception, and short term alike), so reusing it would report a
  correct entry as a typo during an outage — a new `ValidateExists` carries an explicit
  Found/NotFound/Unavailable outcome, and exact-match filters replace the autocomplete's wildcard
  (`jdoe` must not match `jdoe2`; same reasoning `FindUserBySid` already documents at `:110-123`).
  **Slice 1 DONE** (`4aa310e`): `ADDirectorySearchService.ValidateExists` with
  Found/NotFound/Unavailable. Exact-match filters mirroring what the protection engine resolves;
  `DOMAIN\` prefix stripped with a trailing-backslash guard (stripping there leaves an empty
  term, and an empty exact-match filter matches EVERY object). 28 tests.
  **Slice 2 DONE** (`67f2412`): the three add-handlers gate on the outcome. Decision logic lives
  in `Services/ProtectedPrincipalEntryValidator.cs`, not the page (no bUnit harness — same reason
  `MessageTraceExportListing` exists). An accepted entry is stored in the directory's **canonical
  form** (DN for groups/OUs, UPN then mail for users) so the saved rule matches what the engine
  resolves rather than depending on which format was typed — this was not in the plan, added
  during implementation. 20 tests.
  **Slice 3 DONE** (`3f9cec8`): OU picker. `Search` gains an OU branch that is deliberately NOT
  part of `Any`. Also removed three dead keydown handlers — unreachable before this work began.
  **Slice 4 DONE**: already-saved rows that AD says do not exist get a "not in AD" badge, swept
  from `OnAfterRenderAsync` so N lock-serialized lookups never delay first paint. Versions bumped
  here: app `2.3.33 -> 2.3.34`, `AdminSettings 1.0.1 -> 1.0.2`.
  **OQ-2 CLOSED:** `Get-ADOrganizationalUnit` works under the app-pool identity here (verified
  directly), so the OU picker was not dropped.
  **The inverted-rule pair is the subtle part of this work stream:** a failed lookup REFUSES a
  new entry but stays SILENT about an existing one. Both follow from "a directory that did not
  answer is not evidence about the object", yet they point opposite ways — badging every row
  during an outage would read as "your protection rules have been lost". A test pins them apart
  so a later refactor cannot collapse them into one helper.
  **NEXT: the plan's 10 manual checks** — none run. Checks 4 (an O365-only group is refused) and
  7 (AD unreachable gives the retry message, NOT not-found) are load-bearing.

- **Protected-principal resolution via Exchange — CODE COMPLETE 2026-07-30, all 4 slices on
  `master`; NOT deployed (dev is `2.3.32`, repo is now `2.3.33`). 6 manual checks unrun.**
  `docs/ProtectedPrincipalResolution-Plan.md` Status: Implemented. Triggered by an owner
  report of L1/L2 friction: Mailbox Permissions denies cloud-only mailboxes and mail-enabled
  groups with "Protected-principal identity resolution is unavailable", which reads as an
  outage but is an affirmative AD miss. Investigation found a second, unreported defect — the
  alias bypass recorded as GAP 4 under Blockers, which is *live* in ConferenceRooms and
  GroupManagement. Design routes an AD miss through the existing
  `ExchangeIdentityResolver.ResolveToObjectIdAsync` (`Services/ExchangeIdentityResolver.cs:10-31`,
  already registered `Program.cs:173`, unused by the protection path) so Exchange returns the
  canonical primary address; that closes the alias hole and makes groups and cloud-only
  mailboxes resolvable in one change. **No open owner gates.** D1 (fall back to EXO) ruled go;
  D3 ruled "anywhere it's broken it needs to be fixed" — all three gated modules in scope.
  D2 and D4 were withdrawn as decisions on owner challenge: EXO-down is unobservable as a
  policy choice (MailboxPermissions writes via the same pool, GroupManagement never touches
  EXO), and the cloud-only branch relaxes nothing because a cloud-only mailbox cannot be an
  on-prem group member in the first place (`Services/GroupManagementService.cs:246-247` throws
  before the write). Both reduced to consequences of D1; reasoning kept in the plan so they are
  not revived as questions.
  **Slice 1 DONE** (`6faa92d`): `ResolvedRecipient` + `IIdentityResolver.ResolveRecipientAsync`
  on `ExchangeIdentityResolver`. The load-bearing property is that `null` means Exchange
  affirmatively reported no such recipient and nothing else -- a lookup that could not run
  throws, because a caller may allow on a null. `IsRecipientNotFound` and `MapRecipient` are
  `internal static` so that boundary is testable without a live EXO session. 18 tests.
  **Slice 2 DONE** (`76bfead`): `ProtectedPrincipalService.ResolveWithExchangeFallbackAsync`.
  Only `NotFound` falls through to Exchange -- Resolved/Ambiguous/Unavailable return exactly as
  AD produced them, so nothing that denies today starts allowing. An Exchange lookup that could
  not run returns `Unavailable`, never `NotFound`. The service is a singleton and
  `IIdentityResolver` is scoped, so the fallback opens its own DI scope; the scope factory is an
  optional ctor param (nine test files construct the service directly) and a null factory fails
  closed. 13 tests.
  **Slice 3 DONE** (`eb30786`): the cloud-only branch returns a Resolved principal with a **null
  DN** -- that is the point, since group/OU/pattern rules read an on-prem DN a cloud-only object
  cannot have. Those rules are inapplicable, not skipped, and the branch logs which ones were
  not evaluated because both degrade silently (`:582`, `:682`). Constitution edit landed here.
  **Slice 4 DONE** (`0eca01e`): all three gates switched; four distinct messages replace the
  blanket denial. Versions bumped: app `2.3.32 -> 2.3.33`, `MailboxPermissions 1.0.3 -> 1.0.4`,
  `ConferenceRooms 2.3.0 -> 2.3.1`, `GroupManagement 2.1.0 -> 2.1.1`. Both ConferenceRooms test
  fakes scripted `ResolveWithStatusAsync`, which the gates no longer call -- the full suite
  caught that as 4 failures; their overrides moved to the seam actually used.
  **974 tests green**, build/format/ASCII/`git diff --check` clean. Non-vacuity proven per guard
  by reverting each: Exchange-throw-is-Unavailable 1 failure, fall-through-on-any-status 2,
  alias re-resolution 3, fabricated cloud-only DN 1, dropped cloud-only address 2, blanket
  denial restored 2, ConferenceRooms gate back to AD-only 7, GroupManagement the same 2.
  **NEXT: the plan's 6 manual post-deploy checks on dev** -- none run. Check 3 is the
  load-bearing one (an alias as target must be **denied** citing the CEO user rule); it is the
  GAP 4 regression test and must be re-run on prod after promotion. **No independent review**
  has been obtained for this work.

- **MessageTrace export delivery: reports page + notification link — CODE COMPLETE 2026-07-29,
  ON DEV as `2.3.31`, not on prod.** Plan `docs/MessageTraceDownloadLink-Plan.md` Status:
  Implemented. Replaces the emailed zip attachment with a Downloadable Reports page inside the
  app; the email carries a link to that page, so an arbitrary notification recipient is safe (the
  data never leaves the login gate) and admins leave the trace-data path. Supersedes
  `docs/MessageTraceDetail-Plan.md` decisions 5 + 6. Owner rulings recorded in the plan:
  **D1** retention is out-of-process (a host scheduled task deletes exports older than 30 days;
  the app never deletes and must render a missing file as "expired"); **D2** the gate is the
  existing `MessageTrace` module policy with no per-user ownership check, and the ticket number is
  an audit prompt only, never an authorization control; **D3** delivery is a Razor page reusing the
  existing base64 + `downloadFile` JS blob mechanism — **no HTTP endpoint** (owner rejected the
  first draft's minimal-API premise; routing through a page also makes the ticket prompt real
  rather than an empty `?ticket=` in an emailed URL); **D4** (ruled 2026-07-29) the recipient box
  is pre-filled with the operator's own address, editable and clearable -- a default, not a floor,
  and never a required field. Base app `2.3.30 -> 2.3.31` + MessageTrace `1.2.1 -> 1.3.0`.
  All four decisions ruled, no open owner gates, plan approved by the owner 2026-07-29.
  **Slice 1 DONE** (`b007ad5`): `MessageTraceExportStore` -- export-path resolver, GUID-"N" jobId
  whitelist, traversal guard, pinned 30-day constant; 19 tests.
  **Slice 2 DONE** (`87941b0`): `MessageTraceExportListing` (page logic as a testable service --
  the repo has no bUnit harness), `Components/Pages/MessageTraceReports.razor` at
  `/message-analysis/reports`, and `GetFinishedByType` on `BulkJobRepository`/`BulkJobService`.
  Build/format/ASCII clean, 875 tests green; non-vacuity proven per guard by reverting each
  (ticket check 3 failures, Failed-vs-Expired 2, unfiltered limit 1, malformed payload 1).
  One fact recorded in code at the point of use: `<ModuleVersion />` resolves the descriptor by
  route and so renders nothing on the sub-route; kept per `docs/AdminModuleSpec.md` (PAGE009)
  rather than hand-rolling the lookup.
  **Slice 3 DONE** (`e4d2497`): the completion mail now carries a link to the reports page instead
  of the export. `EmailService.SendMessageTraceResultAsync` (no attachment, states the retention
  expiry and the ticket prompt) + new `SendMessageTraceFailureAsync`; `NormalizeRecipients`
  replaces `ResolveMessageTraceRecipients` and **adds nothing**, so the configured admin address is
  never merged into a trace-export recipient set (owner ruling). F3: `ResolveReportsUrl` returns
  null -- never a relative path -- when `Application:PublicBaseUrl` is unset or non-absolute, and
  the caller falls back to prose; it never fails the send. F1: the save result branches the
  notification and stamps the job record. New `BulkJobRepository.AppendMessage` /
  `BulkJobService.AppendJobMessage` -- additive, so a note can never erase a cancel/interrupt
  reason -- resolves the slice-2 inherited gap: `MessageTraceExportState.Failed` is now reachable
  in production. `Application:PublicBaseUrl` added to `Install-ExchangeAdminWeb.ps1` (blank
  default, environment-neutral), `deploy.ps1`, `promote-dev-to-prod.ps1` (empty leaves prod
  untouched), and `README.md`. Build/format/ASCII clean, **897 xUnit + 65 Pester green**;
  PSScriptAnalyzer adds one finding per touched script, each in a category already dominant there,
  none at Error severity. Non-vacuity proven per guard by reverting each: save-failure branch 4
  failures, no-relative-link rule 2, admin exclusion 6.
  **Slice 4 DONE** (`2f0b99c`): removed the two strings the redesign made false
  (`MessageTrace.razor` "never a typed-in address" + the zipped-report banner) and
  `DestinationDisplay()`, which merged the admin address into a display of who gets trace data;
  the page's `IConfiguration` injection went with it. Added the D4(a) recipient box -- pre-filled
  with the operator's claim address, freely editable, **valid when cleared**, no required-field
  validation -- backed by `EmailService.ParseRecipientInput` (comma/semicolon split, format-only
  validation accepting exactly what `NormalizeRecipients` keeps, no domain allow-listing). New
  `MessageTraceDetailJobPayload.Recipients` carries the set; **null** (a job enqueued before the
  box existed) falls back to the submitter, an **empty list** deliberately does not.
  `DownloadSelectedDetails()` untouched, so the reports page still lists emailed/bulk exports
  only. Versions bumped here: base app `2.3.30 -> 2.3.31`, MessageTrace `1.2.1 -> 1.3.0`.
  919 tests green; non-vacuity proven (cleared-box rule 1 failure, format validation 8).
  Follow-up `86f55ce`: corrected the `SaveFailedMarker` comment that still claimed nothing wrote
  the marker.
  **DEPLOYED TO DEV as `2.3.31` (owner, 2026-07-29).** Not on prod (prod stays `2.3.30`).
  **NEXT: run the plan's 9 manual post-deploy checks on dev.** None of them has been run.
  Highest-value ones: an 11+ message export arrives as a link with no attachment; the ticket
  prompt gates the download; an unwritable export dir yields the **failure** notice and a
  **Failed** row (not Expired) with the job still completing; with `Application:PublicBaseUrl`
  unset the mail contains prose and no bare `/message-analysis/reports`. The dev deploy also
  carried the MessageTrace NRE fix (`68bfd25`), so re-running the search that failed in prod is
  worth folding into the same session -- still prod-unfixed until `2.3.31` is promoted.
  **Independently reviewed 2026-07-29** (openreview, codex-commercial / gpt-5.6-sol / max, range
  `68bfd25..1e98eaf`): verdict **findings** (4), all repaired in the plan; record at
  `.agents/review/findings/mt-export-delivery-plan.md`. The one that mattered (F1, HIGH): removing
  the mail attachment makes the saved file the sole delivery, so `SaveToLogPath`'s swallowed catch
  would turn a disk-full failure into a "ready" email plus a row the page mislabels as Expired.
  The plan now branches the notification on the save result and separates **Failed** from
  **Expired**. Also: a non-blank ticket is now required at download (F2); an unset
  `Application:PublicBaseUrl` omits the hyperlink instead of emitting an email-unresolvable
  relative path, and the key is added to both config writers (F3); the global
  `Export:RetentionDays` key is dropped for a pinned 30-day constant -- it broke the
  Constitution's module-config rule and created a second retention truth that could drift from
  the host scheduled task (F4). Plus F5, coder-raised: the D3 rewrite had dropped the
  Constitution-conflict record about writing state into an externally-pruned directory; restored.
  **Non-blocking assumptions the owner may reverse:** OQ-3 (required ticket is this plan's reading
  of "requiring", not an explicit ruling) and OQ-4 (the Failed state is scope the owner did not
  request).
  **Deploy caveat:** `Application:PublicBaseUrl` is written only on a fresh install or with
  `-Force`, so an upgrade leaves the key absent unless it is set by hand on the host. Absent is a
  supported state -- the mail then carries prose and no hyperlink (that is manual check 9), not a
  broken relative link.

- **Operator email resolution from AD -- CODE COMPLETE 2026-07-29, ON DEV as `2.3.32`, not on
  prod. 8 manual checks not yet run.**
  `docs/OperatorEmailResolution-Plan.md` Status: Approved (owner, 2026-07-29). Owner reported on dev running `2.3.31`
  (2026-07-29, confirmed) that the new recipient box does not pre-fill. Pre-fill *is* the design
  (D4(a) of the download-link plan), so this is a bug. Root cause at
  `Components/Pages/MessageTrace.razor:610-613`: `userEmail` is read from `ClaimTypes.Email` /
  `email` / `ClaimTypes.Upn`, but the app is **Negotiate-only** (`Program.cs:38-39`) and a
  Kerberos/NTLM token carries the account name and group SIDs only -- none of those three claims
  exist, so `userEmail` is `""` on every request. Pre-existing since `e62ae73` (the page's
  original commit), **not** a slice-4 regression. Second broken consumer: historical search
  (`:322`, `:709-714`) hard-refuses with "Your authenticated email address is required...".
  Owner rulings in the plan: **D1** fix at the source by looking the address up in AD, not by
  patching the one box ("2nd"); **D2** read `mail`, fall back to `userPrincipalName` ("UPN is the
  same as email here") -- synthesizing `sam@domain` is explicitly rejected as a silent
  wrong-address failure; **D3** when the lookup yields nothing historical search keeps refusing,
  only the message changes to name the real cause ("if AD is unreachable, the whole app stops
  being relevant") -- no typed-address escape hatch. Design: new fail-soft singleton
  `Services/OperatorEmailResolver.cs` reusing `ADDirectorySearchService`'s pooled runspace, with
  an **exact case-insensitive `SamAccountName` filter** and `null` on 0 or 2+ matches --
  `Search` is a wildcard substring autocomplete query, so `jdoe` also returns `jdoe2` and taking
  the first row would eventually mail trace data to the wrong person; that is the plan's
  highest-value test. Planned bumps: app `2.3.31 -> 2.3.32`, MessageTrace `1.3.0 -> 1.3.1`.
  **Historical search has never been used** (owner) -- which is why a permanently blocking guard
  survived unreported -- but the owner ruled **keep it** (plan OQ-3, CLOSED): it is L2's only
  route to a beyond-realtime search without escalating to L3-4, so removal was considered and
  rejected and must not be re-raised. Consequence (plan OQ-2): everything past `:710` is
  unproven, not regressed; manual check 5 does not gate this plan, but **its outcome must be
  recorded here either way**, and a failure earns its own follow-up plan rather than being
  absorbed into this one. Plan commits `b7b9c94`, `3962904`, `186d4e0`.
  **Independently reviewed 2026-07-29** (openreview, codex-commercial / gpt-5.6-sol / max,
  range `64b211a..ace6230`): verdict **findings** (3), all accepted; record at
  `.agents/review/findings/operator-email-resolution-plan.md`. **F1 (HIGH) replaced the
  plan's central mechanism:** resolve by the authenticated **primary SID**
  (`ClaimTypes.PrimarySid`, which Negotiate does populate --
  `Components/Pages/SelfServiceGroups.razor:325`) through a bound `Get-ADUser -Identity`,
  mirroring `SelfServiceGroupService.ResolveCallerDn`
  (`Services/SelfServiceGroups/SelfServiceGroupService.cs:672-685`, whose doc comment
  already rejects samAccountName as an identity form by name). The samAccountName-through-
  autocomplete design fails four ways an exact-match post-filter cannot close, the worst
  being a cross-trust name collision returning exactly one confidently-wrong row. **F2
  (MEDIUM):** the resolver is `ResolveAsync` doing its work under `Task.Run` -- the shared
  AD lock can block 30s (`ADDirectorySearchService.cs:80`) and would freeze the circuit;
  a late result fills the recipient box only while it is still untouched. **F3 (LOW):** the
  deployed-version contradiction, flagged in the version block below rather than guessed.
  Also confirmed: `ADDirectorySearchService` is `sealed`, so the narrow test interface is
  required, not optional.
  **Slice 1 DONE** (`8594813`): `IOperatorDirectory` (one-member seam, needed because the AD
  service is sealed), `ADDirectorySearchService.FindUserBySid` (bound `Get-ADUser -Identity <sid>`
  on the existing pooled runspace, fail-soft, SID never logged -- it identifies the operator and
  the call runs on every page load), and `OperatorEmailResolver` (SID-format gate before any
  directory call, `mail` then UPN, `Task.Run`). 16 tests; non-vacuity proven per guard by
  reverting each: SID gate 8 failures, UPN fallback 3, SID pass-through 5, fail-soft catch 1.
  **Slice 2 DONE** (`928dd0a`): `MessageTrace.razor` reads `ClaimTypes.PrimarySid` and defers the
  lookup to `OnAfterRenderAsync`, so the AD service's 30s throttle lock never sits on the render
  path. The resolve is cached as a **`Task`, not a result**, so a Search click racing the deferred
  first resolve awaits the same AD call instead of issuing a second behind that lock. A
  `recipientTouched` flag (the box moved from `@bind` to explicit `value`/`@oninput` to observe
  first input) stops the late result overwriting a typed address. The historical-search guard
  awaits the same task rather than reading a possibly-unpopulated field, and its refusal now names
  Active Directory instead of blaming the operator's account (D3); guard structure unchanged.
  **Versions DONE** (`14f4ef1`): app `2.3.31 -> 2.3.32`, MessageTrace `1.3.0 -> 1.3.1`.
  Build/format/ASCII/`git diff --check` clean, **935 tests green**.
  **Implementation openreview ATTEMPTED TWICE, NOT OBTAINED (2026-07-29).** Both dispatches to
  `codex-commercial` (MCP) / `gpt-5.6-sol` / high over `55ec9af..14f4ef1` died on a 1800s silent
  transport abort, and the `codex` CLI fallback fails auth at the gateway (`Failed to refresh
  token` + missing `x-portkey-*` header). Per the playbook's fail-closed rule a missing envelope
  is not a clean pass, so **this code carries no independent review** -- re-dispatch when the
  harness is healthy, or the owner adjudicates shipping without it. Record:
  `.agents/review/findings/operator-email-resolution-plan.md`.
  **NEXT: the plan's 8 manual post-deploy checks** -- none run; page behavior is not
  unit-testable (no bUnit harness), so they are the only evidence this works. Check 7 is the
  load-bearing one: it confirms `ClaimTypes.PrimarySid` is actually populated on this deployment.
  Check 5 (historical search accepted rather than refused) is **exploratory, not a gate** (OQ-2)
  -- but its outcome must be recorded here either way.

- **MessageTrace per-message delivery-detail (MT-detail) — DONE, landed 2026-07-27.**
  Plan `docs/MessageTraceDetail-Plan.md` Status: Implemented; all 9 slices on `master`,
  each codex-reviewed accepted. MessageTrace module 1.1.1 -> 1.2.0, no base app bump.
  Full record archived: `docs/history/state-archive.md`. Live validation not yet performed
  (runs against real PROD EXO/on-prem AD, from the dev instance): detail drill-in, download/email/zip delivery,
  Blazor selection UI (enumerated in the plan).

- **GM-3 self-service group management (on-prem AD only) — task set DONE, landed 2026-07-27;
  two follow-ups landed 2026-07-28.**
  Plan `docs/SelfServiceGroupManagement-Plan.md` Status: Approved / on-prem only; all 6 tasks
  (plan section 7) complete and codex-reviewed. M365/delegated-Entra dropped 2026-07-22.
  Full record + the scope-narrowing and ACL-scan-drop history archived:
  `docs/history/state-archive.md`.
  - **2026-07-28 DACL-read fix (`28efc88`):** eligibility read was `Get-Acl AD:\<DN>`, which
    returned an empty `.Access` in the service runspace, fail-closed-excluding every group (page
    always showed "no groups"). Now reads `Get-ADGroup -Properties nTSecurityDescriptor`. Module
    1.1.0 -> 1.1.1.
  - **2026-07-28 member listing + AD picker (`a190664`,`fd16982`,`428a19e`,`80fe2a5`):** plan
    `docs/SelfServiceGroupsMemberListingAndPicker-Plan.md` (Approved 2026-07-28). Manage panel now
    lists current members with per-user Remove; member add box uses the shared
    `ADIdentityAutocomplete` (Option A: suggestions under app-pool identity, write stays isolated +
    re-validated — `.agents/decisions.md` 2026-07-28). Module 1.1.1 -> 1.2.0. Build/format/824
    tests green (17 new `GroupMemberClassifierTests`, non-vacuity proven).
  SelfServiceGroups module now at **1.2.0** (added at 1.0.0, no base app bump).
  Live validation not yet performed (runs against real PROD AD, from the dev instance): live AD
  add/remove, member list render + remove, audit write, admin + affected-user email, the Blazor
  page flow. Owner will deploy + test the DACL fix; a proper plan+review is the fallback if it
  does not resolve the "no groups" symptom.

### App version history

- **App version `2.5.5`** (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`, read there -- that file
  owns the number). `2.5.5` was the Bootstrap button-state fix, `2.5.4` the Bootstrap palette
  bridge + Dracula correction, `2.5.3` the theme palette rework.
  `2.5.2` was in-process export retention (shared app-wide startup behaviour, and it deletes files
  the app never deleted before). The `AdminBulkJobs` module landed in the same stretch and
  correctly bumps nothing -- Constitution: adding a module does not bump the base version.
  `2.5.1` was the Conference Rooms bulk jobs work (the repository/service reads are shared
  infrastructure); ConferenceRooms module `2.3.1 -> 2.3.2` with it. `2.5.0` was theme support --
  minor rather than patch, new user-visible capability app-wide. `2.4.1` was the two post-deploy
  UI corrections (`AdminSettings` module `1.1.0 -> 1.1.1` with it); `2.4.0` the app-wide admin UI
  redesign, bumped from `2.3.36`, which qualified group display names as `DOMAIN\Name`.
  Previously bumped from
  `2.3.34` for section-access SID storage (shared authorization path); `2.3.34` was
  protected-principal admin input validation; `2.3.33` (`0eca01e`) was
  Exchange-backed protected-principal resolution, `2.3.32` (`14f4ef1`)
  the operator-email resolver, `2.3.31` (`2f0b99c`) the MessageTrace
  export delivery redesign, `2.3.30` (`456e07c`) retired the `Security:ExcludedUsers` appsettings
  fallback and `2.3.29` (`3eac48a`) was the app-wide log-root fail-fast change.

### Superseded deployed-version records

  **Superseded: dev and prod both ran `2.8.0` from 2026-08-10 14:29:33 to 2026-08-11 11:13:20**
  (`favicon.ico` 21497 bytes on both, matching the repo). The paragraph below describes that build.
  **`2.8.0` carries, over `2.7.0`:** the Migration Status batch-selection work (Migration module
  `1.7.0` -- three bulk actions with distinct targets, no status allowlists, inline ticket entry,
  deselect-on-accept, queued-not-done wording) and the replacement favicon.
  **The owner found a module-version error on the first `2.8.0` dev deploy by reading it off the
  page:** the app version was correctly `2.8.0` while Migration still said `1.6.0`, three behaviour
  commits stale. **A correct app bump is not evidence about a module bump** -- the two rules fire
  independently, and the correct one made the wrong one look right by association. Fixed before
  this deploy.
  **Superseded record, kept for incident tracing: dev and prod both ran `2.7.0` from 09:35:16 to
  14:29:33 on 2026-08-10.** The paragraph below describes that build.
  **This is the first build where protected-principal servicing actually works.** `2.6.0`, which
  both instances ran until now, carried the same feature with all three review findings live: the
  page gate hid the capability in AD Attribute Editor, the undo path allowed overrides with no
  audit record, and bulk CSV refused what the single form allowed. **Nothing but the version number
  distinguishes those two builds, so an incident spanning 2026-08-07 to 2026-08-10 must check which
  was running.**
  **The capability grants nothing until a servicer group is configured** per module in Module
  Config, and no manual check has confirmed it end to end -- see `## Next`.
  **Supersedes the "dev `2.5.5`, prod `2.5.5`" record below** (2026-08-05, verified from the
  assembly at the time; its eyeball check of a disabled submit button remains unrun).
  Both instances had been four months behind on `2.3.34` until 2026-08-04 and now carry
  section-access SID storage, the `sidf-1` admin-lockout fix, the full UI redesign, ten themes, the
  module-scoped jobs panel, in-process export retention and the `AdminBulkJobs` page. That clears
  the long backlog recorded below -- **and none of those work streams' manual checks has been run
  on either instance.**
  **Prod is level with dev** (re-verified 2026-08-05; the earlier "one behind" note is stale).
  **Caution: version numbers alone cannot identify a build here.** Two different `2.5.1` builds
  shipped 34 minutes apart earlier the same evening, straddling commits. Hash the deployed file
  against the repo when it matters, as was done for `app.css` above.
  Deployed-version claims below this line predate 2026-08-04; treat the specific build numbers in
  them as history, not as current state.
  **Dev now carries, none of it validated against a live directory yet:** the operator-email
  resolver + MessageTrace export redesign + MessageTrace NRE fix (`2.3.31`-`2.3.32`),
  Exchange-backed protected-principal resolution (`2.3.33`), protected-principal admin input
  validation (`2.3.34`), and the four `ppv-*` review fixes.
  **Prod carries NONE of them.** Still live on prod: the GAP 4 alias bypass, the L1/L2
  cloud-only + mail-enabled-group denial friction, unvalidated admin input, and the
  MessageTrace null-row NRE. `2.3.30` was deployed to dev, validated, then promoted, and
  supersedes all prior deployed builds, so prod does carry the `2.3.28` Bulk Job Runner, the
  `2.3.29` log-root fail-fast, and the `2.3.30` ExcludedUsers-fallback retirement. Prior prod
  baseline was `2.3.27` (validated 2026-06-29). (Settles the conflict openreview F3 flagged:
  the "not deployed anywhere" claim was the stale half. OQ-4 in
  `docs/OperatorEmailResolution-Plan.md` is closed.)

## Archived 2026-08-05 (drift sweep)

### Coverage ratchet repair implemented (floor 65.06 -> 65.12)

- **Coverage ratchet repair IMPLEMENTED 2026-08-05** (owner approved; `8d614b4` slice 1,
  `4daf5d9` slice 3, `b5df487` slice 2). **CONFIRMED ON CI**, not just locally.
  Floor raised **65.06 -> 65.12**, taken from the CI figure in its own commit -- raising it in the
  same commit as the improvement would have set a floor from a number no CI run had produced.
  **The plan's "measure, do not assume" clause earned itself:** the three planned extractions
  landed at 64.9% -- better, still 3 lines short -- so two more were pulled from the same file
  rather than touching the floor. `SectionAccessDirectoryReading` is at 100%.
  **Slice 2 (raise the floor) is DELIBERATELY NOT DONE.** Local margin is 0.06 points (65.12 vs
  65.06) and CI's arithmetic differs, so raising it from a local number would be guessing -- which
  is how a floor becomes unreachable. Raise only after a green CI run reports a real value, in its
  own one-line commit.
  **Measurement error worth keeping:** an intermediate run was reported as "gate exit: 0" when the
  gate had failed -- `$LASTEXITCODE` read after a `Select-Object` pipeline carries the pipeline's
  code, not the script's. Caught only because the reported figure was arithmetically below the
  floor it claimed to clear. **Read a gate's verdict line, not an exit code taken through a pipe.**

### Coverage ratchet repair planned and reviewed

- **Coverage ratchet repair PLANNED and REVIEWED 2026-08-05 -- plan now In progress.**
  **The shortfall is SIX LINES** (1015/1569 = 64.69% against a 65.06 floor), measured before
  choosing a shape -- which ruled out treating it as a "write a big test suite" problem. Cause is
  dilution, not regression: `0e35e7b` added 49 lines to `Services/SectionAccessGroupDirectory.cs`,
  a file at **0/115**, so the ratio fell while the numerator stood still.
  That file is untested **by construction** -- every path opens a PowerShell runspace and imports
  `ActiveDirectory`. The plan applies the extraction the repo already used twice for this exact
  shape (`MailboxPermissionOutcome`, `CalendarFolderIdentity`): move the three pure decision points
  where a wrong answer is a silent authorization defect, leave the runspace calls alone.
  **openreview codex (gpt-5.5-dzs @ xhigh, grade `fallback`) over `506c2d4..62d84d9`:
  `acceptable_with_changes`; 3 findings, all ADMITTED after independent verification. Repair
  re-review over `62d84d9..e9ed249`: `best_approach`, no findings.**
  Two of those findings are worth carrying forward as habits, not just fixes:
  **(a) I claimed the unextracted fail-closed paths were "covered by the live tests where a host
  allows". They are not** -- no test constructs the real service, and no live-AD file mentions it.
  A plan asserting coverage that does not exist is worse than one admitting a gap, because it
  stops anyone looking. Corrected in place so the record shows the gap is accepted, not absent.
  **(b) The stale-coverage-report hazard was filed as "not observed"** -- it had already misfired
  earlier in the same session. It is now slice 3, a guard in the tool, because a procedure that
  relies on remembering an incantation is weaker than a check.
  Also self-caught: the plan estimated "20-25 lines" for the extraction; counting gives **15**, and
  extraction adds coverable declaration lines, so the margin over 6 is thinner than it read. Slice
  2 measures rather than assumes and names the next candidate if it falls short.
  **D1 (whether the gated scope should keep `ProtectedPrincipalService` at 62% and
  `PermissionValidator` at 46%, which will dilute it again) is open and blocks nothing.**
  **NEXT: owner approval, then slices 1-3.**

### CI test failure fixed and confirmed on CI

- **CI test failure FIXED and CONFIRMED ON CI 2026-08-05** (`506c2d4`, pushed in `0f67e62`).
  Run 31016572894: Test passes -- **1288 passed, 0 failed, 9 skipped** (the 9th skip is the new
  domain-only case, skipping loudly on the standalone runner exactly as designed). The `powershell`
  job passes.
  **`master` is still red, now solely on the coverage gate** -- `64.1% (817 / 1275)` vs the 65.06
  floor -- which is what the prediction below said would surface once Test stopped exiting first.
  **New fact the plan now carries: CI and the dev box do not measure the same denominator.** CI is
  1275 lines to the dev box's 1569, because live-directory tests skip there so their code is never
  instrumented. The shortfall is **13 lines on CI, 6 locally**; a local `Test-CoverageFloor.ps1`
  pass is therefore NOT evidence the gate will pass on CI.

### CI test failure diagnosed: the "DA" SDDL alias host dependency

- **CI test failure FIXED 2026-08-05.** `master` had been red since `ba9fe4f` (2026-08-04) on
  `SectionAccessGroupIdentityTests.RefusesSddlAliases(alias: "DA")`, and the record said the
  coverage gate was the cause. **It was not** -- the failing step is Test; the coverage gate never
  runs because Test exits first.
  **The test asserted an environment, not a behaviour.** `"DA"` is the only SDDL alias needing a
  joined DOMAIN to resolve: here it resolves and is refused as *"not canonical"*; on GitHub's
  standalone runner it throws and is refused as *"not a valid SID"*. Both are correct refusals, so
  the security property held the whole time -- only the wording assertion was environment-specific.
  Measured both branches before touching anything (`ZZ`/`QQ` throw here too, confirming the
  unresolvable path).
  Split into: the two aliases that resolve anywhere (`BA`, `WD`) in the original theory; a
  domain-only case using `Assert.SkipUnless`, which skips LOUDLY per the repo rule that a silent
  early return is indistinguishable from a pass; and a host-agnostic case asserting the part that
  actually matters for authorization -- `"DA"` is never a usable stored group value, whatever the
  reason string says. The skip probes by *resolving the alias*, not by asking the OS about domain
  membership: a joined machine can still fail to resolve it.
  **This file's own header already required these tests to be directory-free so they hold on CI.**
  The invariant was written down and the test still violated it -- the header was not enough on its
  own, which is why the host-agnostic assertion exists rather than only the skip.
  Verified the way the defect demanded -- by reproducing CI's condition, not by trusting a green
  run on a domain-joined box: pointing the probe at an alias that never resolves anywhere gives
  **54 passed, 1 skipped, 0 failed**, so the domain-only case skips and the host-agnostic case
  still holds. Non-vacuity separately proven by deleting the canonical-SID check, which fails all
  four alias tests including the new one.
  **NEXT once this lands: the coverage ratchet becomes the new CI failure** (64.7% vs 65.06). It
  was always failing; the Test step was simply reaching the exit first.

### Handoff snapshot at b106dce

- **HANDOFF 2026-08-05, as of `b106dce`. Tree clean, nothing in flight, nothing blocked.**
  **Repo `2.5.5`; dev `2.5.4`; prod `2.5.2`.** Dev and prod versions were verified from the
  assemblies during the session, not assumed -- do the same before trusting them again, and note
  that two *different* builds shipped as `2.5.1` earlier that evening, so hash `wwwroot/app.css`
  against the repo when it matters.
  **Immediate next action: deploy `2.5.5` to dev** (`.\tools\deploy-pipeline.ps1 -Dev`, ELEVATED)
  and look at a form with an empty required field -- the disabled submit button should carry the
  theme accent, not Bootstrap blue. That is the whole of what `2.5.5` changes.
  **Nothing is awaiting an owner ruling.** Everything below is either landed or queued work.
  **Session shape worth knowing:** the last four commits are one defect found four times in the
  same place -- themes looked flat (`2.5.3`), Bootstrap's palette was never bridged (`2.5.4`),
  then button *states* were still unbridged (`2.5.5`). Each round the verification answered a
  narrower question than the owner's eyes did. If a fifth round appears, suspect specificity or an
  unstyled Bootstrap class before suspecting the tokens.
  **Two probe failures this session, same shape:** a literal string replacement silently matched
  nothing and the probe reported PASS. Assert the file actually changed before believing a
  non-vacuity result.

### Bootstrap palette bridged + Dracula corrected (app 2.5.4)

- **Bootstrap palette bridged + Dracula corrected 2026-08-04, app `2.5.4`, ON DEV** (verified
  `FileVersion 2.5.4.0`, deployed 22:38; dev `app.css` hashed identical to the repo at that
  point). Superseded same day by `2.5.5` above.
  Owner after `2.5.3` reached dev: *"buttons and checkboxes are still blue on every theme. dracula
  isn't using green or yellow and doesn't match the dracula theme. that one I know well, and this
  is a bad implementation."* Both correct. `docs/ThemeSupport-Plan.md` slice 7.
  **Why blue survived a full theme rework -- the number is the finding:** this app uses **24
  colour-bearing Bootstrap classes and the theme layer restyled 3.** Checkboxes, switches, radios,
  spinners (159 uses), badges (43), `.text-*`/`.bg-*` utilities and the success/danger/warning
  buttons are all painted by Bootstrap from `--bs-primary: #0d6efd`, which nothing overrode --
  slice 1 mapped 8 `--bs-*` variables and stopped short of the semantic palette. Fixed at the
  variable layer, which repairs the other 21 classes at the source; explicit rules remain only
  where Bootstrap uses a shorthand that ignores the variable or bakes the colour into an SVG data
  URI (the switch knob).
  **Lesson worth keeping: a token layer only reaches what consumes it.** The tokens were correct
  and the themes were correct; the framework simply was not reading them. Verifying "the theme
  system works" on the pages we restyled proved nothing about the 21 classes we had not.
  **`-rgb` companions are a deliberate second copy** of each accent (Bootstrap needs raw triplets
  for alpha blends) and they already drifted during development -- Dracula's warn moved yellow ->
  orange and the triplet did not follow, which is near-invisible since only translucent overlays
  go wrong. `EveryRgbTripletMatchesItsHexToken` derives the expected value from the hex; they are
  also in `RequiredTokens` so a theme cannot omit them.
  **Dracula specifically:** the accent values were spec-correct, but the canvas `#1e1f29` was
  invented rather than the palette's own ANSI black `#21222c`, and the state tints were off-hue --
  danger tinted toward pink while its foreground was red, and warn used Yellow where Orange
  `#ffb86c` is the palette's warning colour.

### Theme palettes reworked (app 2.5.3)

- **Theme palettes reworked 2026-08-04, app `2.5.3`, ON DEV.** Deployed by the owner 22:24 and
  verified: dev assembly `FileVersion 2.5.3.0`, and dev `wwwroot/app.css` is **byte-identical to
  the repo** (SHA256 match), so the reworked palettes are genuinely live rather than a stale copy
  surviving the mirror. Owner rejected the first cut on sight after `2.5.1` reached dev: *"the
  themes aren't implemented optimally. I see really only two main colors."*
  `docs/ThemeSupport-Plan.md` slice 6.
  **The mechanism was fine; the VALUES were wrong** -- so this was a data-only fix with no rule
  touched, which is the token layer working as intended.
  Measured cause, three parts: (a) canvas/surface/header sat within ~12 luminance points, below the
  ~20 a step needs to read as a step, so cards did not sit on the page and table headers did not
  separate from rows -- every palette collapsed to background-plus-text; (b) the accent reached
  only links, the primary button, the focus ring and a tab underline, so the colour that makes a
  theme recognisable never appeared on a working page; (c) the sidebar shared the page background
  and read as empty margin. Spread is now 16-33, canvases use each project's own darker variant
  rather than one hue at four brightnesses, card headers carry a 2px accent rule, and the nav has
  its own tone.
  **Two new tests pin what was broken:** surface-separation guards, because the existing contract
  tests only checked tokens EXIST -- which says nothing about them being distinguishable. The
  canvas->surface guard caught Tokyo Night at 5.1 during development and forced a deeper canvas.
  **A false-passing probe is worth remembering:** the first non-vacuity attempt used a literal
  string replace that silently did not match, and reported PASS. Only the null-reference noise
  beside it gave it away. A probe that does not visibly change the file proves nothing -- assert
  the edit applied before trusting the result.

### Coverage ratchet failing - resolved 2026-08-05 (CI green, floor raised)

- **OPEN, PRE-EXISTING -- the coverage ratchet is FAILING and no one noticed.** 64.7% against a
  65.06 floor. **Not caused by the theme work:** measured in a scratch worktree at `6f89f1c` with
  no theme code and got `1015 / 1569`, identical. Cause traced to `0e35e7b` ("show section-access
  groups as DOMAIN\Name"), which added 49 lines to `Services/SectionAccessGroupDirectory.cs` --
  a **0%-covered** file -- after the floor was set at `9a66cf4`. Growing an uncovered file lowers
  the ratio; the gate is correct and is reporting a real regression.
  **The floor was NOT lowered** -- `.agents/review/coverage-floor.txt` says in terms that doing so
  converts the gate into decoration, which is finding tsr-1, already made once here.
  **Fix needs a seam:** `SectionAccessGroupDirectory` talks to live AD, so it cannot be tested as
  it stands -- same shape as the `MailboxPermissionOutcome` / `CalendarFolderIdentity` extractions
  in `docs/TestSuiteRemediation-Plan.md`. **CI on `master` is red until this is fixed.**

### Section-access groups stored as SIDs - slice detail

- **Section-access SIDs — slice detail (all landed 2026-08-03).**
  `docs/SectionAccessSidStorage-Plan.md` Status: Approved, no open owner gates. The defect: a
  bare group name does not identify a group, and both comparison sites
  (`GroupAuthorizationHandler:97-101`, `GroupMembershipChecker:38-45`) strip any `DOMAIN\`
  prefix and accept either form, so a foreign-domain same-named group is indistinguishable.
  Exposure measured, not assumed: of 10 trusts only **2 are BiDirectional**
  (`winroot.analog.com`, `maxim-ic.internal`), so the collision surface is 3 domains, not 10 —
  narrow, not zero, and it sits in the field deciding entry to privileged modules.
  **Two measurements constrain the whole work stream.** (a) The Windows token already carries
  **333 group SIDs**, never names — the app converts SIDs to names in order to compare names, so
  storing SIDs REMOVES a translation. `WindowsPrincipal.IsInRole` accepts a SID string directly.
  (b) Prod log 2026-08-03: **1687 authorizations via `user.IsInRole`, 0 via the claims path** —
  `ClaimTypes.Role` is never populated under Negotiate. `GroupMembershipChecker` is therefore
  dead in the live path but NOT dead code (the bulk job runner's off-circuit re-check needs it),
  so changing only the handler would leave the job runner comparing names against SIDs.
  **Slice 1 DONE**: `Authorization/SectionAccessGroupIdentity.cs` — pure, directory-free, so the
  decisions the migration depends on hold on CI too. Refuses SDDL aliases (`new
  SecurityIdentifier("BA")` SUCCEEDS and yields BUILTIN\Administrators; `"DA"` is an account SID
  passing every check but the round-trip), well-known SIDs via `IsAccountSid()` so no blocklist
  needs maintaining, and the bare domain SID (parses, round-trips, `IsAccountSid()` true, yet
  names a domain). 43 tests; non-vacuity proven per guard (6/3/1/1/1/2/3 failures on revert).
  **Two data facts pinned in code, both verified against live AD 2026-08-03:** the NetBIOS domain
  half is load-bearing — `Enterprise Admins` without `-Server` returns **0** matches, so the
  current normalization's stripping would turn a live cross-domain grant into an unresolvable
  row; and the lookup must query `sAMAccountName`, `cn` AND `name`, because
  `$KOO300-S3AMUVVBVMI1` is a sAMAccountName whose `cn` is `Employees-All`.
  **All 18 distinct prod values resolve to exactly one group** (58 rows: 46 `ANALOG\`, 1
  `winroot\`, 11 bare). There is no unresolved-row class in this data — the plan's D2 was
  withdrawn on owner challenge after the apparent exception turned out to be a probe bug
  (`Get-ADGroup -Filter` expands `$` as a PowerShell variable).
  **Slice 2 DONE** (`16ef8c0`): schema v6 (`group_display_name`), pure
  `SectionAccessSidMigrationPlanner`, `SectionAccessGroupDirectory` (AD + the NetBIOS->DNS
  crossRef mapping), `SectionAccessSidMigration` runner. Never blocks boot, never half-writes:
  one unconvertible row stops the whole write, and a directory failure propagates rather than
  becoming "no such group" — an outage must not send an admin to fix correct data. The AD
  lookup is a **separate service** from `ADDirectorySearchService` because that one is fail-soft
  by design and this one must throw.
  **Slice 3 DONE** (`f361281`): app `2.3.34 -> 2.3.35`. Reads `ClaimTypes.GroupSid` (which
  Negotiate populates) instead of `ClaimTypes.Role` (which it does not); normalization deleted.
  **Slice 4 DONE** (`0a50d01`): picker returns a SID with no name fallback, badges show names,
  free-typed text refused. `SaveAll` carries display names across its delete-and-reinsert —
  without that every admin save would blank names the idempotent migration would never restore.

## Archived 2026-07-30 (catchup sweep)

### Retire `Security:ExcludedUsers` appsettings fallback — complete code + host, both environments

- **Retire `Security:ExcludedUsers` appsettings fallback — DONE (code half), landed 2026-07-28.**
  Plan `docs/RetireExcludedUsersAppsettingsFallback-Plan.md` Status: Implemented;
  `.agents/decisions.md` 2026-07-28. Both readers (`PermissionValidator.GetConfiguredExclusions`,
  `ProtectedPrincipalService.GetLegacyExclusions`) no longer fall back to the invisible
  `Security:ExcludedUsers` appsettings array; exclusions come only from the DB protected-principal
  store + `MailboxPermissions/ExcludedUsers` module config. Base app `2.3.29 -> 2.3.30`.
  Commits `4dff069`(plan) `f5b329b`(slice1) `942dd10`(slice2) `5c7cc93`(slice3 tests)
  `c35f056`(slice4 docs) `456e07c`(version). Build/format/827 tests green; two new guard tests
  non-vacuity-proven (fallback restored -> both fail).
  **Slice 5 host cleanup DONE (owner, 2026-07-29):** the `Security.ExcludedUsers` block was
  removed from the deployed `appsettings.json` on both dev and prod (`PreventSelfGrant`,
  `AllowedGroups` left in place). The ExcludedUsers-fallback retirement is now fully complete,
  code + host, both environments.

### Log-root fail-fast — landed and validated in prod

- **Log-root fail-fast IMPLEMENTED** (2026-07-22, `docs/RemoveHardcodedLogRoot-Plan.md`).
  Hardcoded `E:\WWWOutput` fallback removed from all three services; startup guard aborts boot if
  `Audit:LogRoot` is unset/blank. Commits `fa40485` (helper + guard), `b14fce6` (services),
  `821a2f8` (docs), `3eac48a` (app version bump 2.3.28 -> 2.3.29). Build + all 676 tests green.
  **Deploy note:** the new build fails to start if `Audit:LogRoot` is unset; the target env's
  `appsettings.json` must set it before deploying `2.3.29`.
- **RESOLVED (2026-07-29):** `2.3.29`'s log-root fail-fast is now validated in prod — it ships
  inside `2.3.30`, which the owner deployed + validated + promoted to prod. The startup guard is
  inherently exercised: the app cannot boot without `Audit:LogRoot`, and it booted.

### Bulk Job Runner — landed; only live validation remains (tracked in `state.md` Next up)

`docs/BulkJobRunner-Plan.md` (Status: Implemented) · `.agents/decisions.md` 2026-07-02.
App `2.3.27`→`2.3.28`; ConferenceRooms module `2.1.0`→`2.2.0`.

ConferenceRooms bulk apply (Finder/Type CSV) now runs as a durable server-side job (separate
`config/exchangeadmin-jobs.db`, never promoted). Self-pumping singleton runner (not a hosted
timer); single active job + FIFO queue; startup flips non-terminal jobs to Interrupted (no
resume); always cancellable; per-row failure aggregation; completion email fires from the job.
Off-circuit auth = option (a) (capture the authorization decision at submit, re-check per row via
shared pure `GroupMembershipChecker`). Protected-principal gate enforced in-job per row on
**both** Finder and Type bulk paths (closes GAP 3). Deploy scripts warn (not block) on active jobs
before recycle (`tools/JobStateWarning.psm1`). ~671 xUnit + 65 Pester green (as of `9d26b5f`);
build/format/diff-check clean; each slice codex-reviewed with findings fixed before commit.
(Dev deploy done 2026-07-20.)

### Landed `Next up` items rotated out (2, 5, 6)

2. **Single-room Finder protected-principal gap** — **DONE** (2026-07-21, commit 2a97d09;
   `docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md` Implemented). Consolidated the
   module PP check into one `ConferenceRoomProtectionGate` (C2-G). Only remaining follow-up is
   live-instance/UI validation not yet performed (runs against PROD from the dev instance).
5. **GM-3 self-service group management (on-prem AD only) — DONE 2026-07-27.** All 6 tasks (plan
   section 7) landed and codex-reviewed; see the `## Now` pointer and this archive.
   Only follow-up is live validation, not yet performed (runs against PROD AD from the dev
   instance). Not next.
6. **ASCII cleanup sweep + enforcement lint** -- **DONE** (2026-07-21). Scope narrowed by owner to
   code/logging only (`.cs`/`.ps1`/`.psm1`); docs, `.razor` UI, and `EmailService.cs` email emoji
   excluded. (a) Sweep landed commit `c2e2f6f` (329/329 char swaps, 77 files, 672 tests green).
   (b) CI gate `tools/Test-AsciiOnly.ps1` wired into `.github/workflows/ci.yml` `powershell` job,
   non-vacuity proven. See `.agents/decisions.md` 2026-07-21.

### Closed blockers rotated out

- **CLOSED (2026-07-24) — ptk blocker + AD scan-sizing.** At the 2026-07-24 close ptk had been
  removed (server + the shell-blocking hook that forced AD calls through it), so the "ptk down is
  a STOP; no direct PowerShell fallback" rule no longer applied. (2026-07-28: ptk is available
  again this session — use it per global guidance when present.) The one AD read it had gated (a
  domain-wide `Get-ADGroup` count) was run directly: **41,368 groups** (now in the archive). Moot
  regardless: the scaled-back task-2 design (2026-07-24) dropped the domain-wide scan entirely.
  (The trailing fragment carried in this entry — the single-room Room Finder PP-gate description —
  was a paste artifact duplicating the PP-gaps entry; the canonical record is
  `docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md` and commit `2a97d09`.)
- **CLOSED (2026-07-30) — prod BlockedSenders version uncertainty.** The recorded doubt was that
  the two BlockedSenders fixes (`17910f3`→1.0.1, `cde778f`→1.0.2) are module bumps, not app bumps,
  so a prod app version could not confirm them. Resolved by direct evidence: prod runs `2.3.30`
  (deployed assembly `D:\inetpub\ExchangeAdminWeb\ExchangeAdminWeb.dll`, FileVersion `2.3.30.0`)
  and both commits are ancestors of the `2.3.30` version-bump commit `456e07c`
  (`git merge-base --is-ancestor`, verified 2026-07-30). Prod includes both fixes.

## Archived 2026-07-29 (catchup sweep)

### 2026-07-21 landed slices (all pushed + CI green)

- **This session landed (2026-07-21), all pushed + CI green:**
  - `ff443ca` -- decision+docs: new module does not bump base app version (Constitution +
    decisions.md + repo-guidance; resolved the long-open versioning exception).
  - `c2e2f6f` -- ASCII sweep of code/logging (77 `.cs`/`.ps1`, 329/329 char swaps). Scope narrowed
    by owner to code/logging only; docs, `.razor` UI, `EmailService.cs` email emoji excluded.
  - `502dd0e` -- ASCII CI lint gate `tools/Test-AsciiOnly.ps1` (excludes `EmailService.cs`), wired
    into `.github/workflows/ci.yml` powershell job.
  - `8c6f83f` -- fixed xUnit1051 warning that had reddened CI since 2026-07-20 (format check treats
    analyzer warnings as fatal).
  - `9dd39cd` -- state note recording that format-warning trap.
  - `b978362` -- fixed `ConferenceRoomProtectionGateTests` hardcoded `E:\WWWOutput` log path (was
    masked until format gate went green; failed only on CI, not the ADI dev box).
  - `71d1daa` -- Approved plan `docs/RemoveHardcodedLogRoot-Plan.md`.

## Archived 2026-07-28 (catchup sweep)

### MessageTrace per-message delivery-detail (MT-detail) — landed complete 2026-07-27

- **MessageTrace per-message delivery-detail (MT-detail) — COMPLETE 2026-07-27.**
  `docs/MessageTraceDetail-Plan.md` Status: Implemented. All 9 slices landed on
  `master`, each committed and codex-reviewed accepted (records
  `.agents/review/findings/mt-detail-slice1..9.md`, index rows mt-detail-slice1..9).
  Core defect fixed: the trace no longer collapses a message's per-hop trail; a
  per-row Details drill-in shows every event (on-prem never collapses, free;
  cloud costs one `Get-MessageTraceDetailV2` per click), plus checkbox selection
  with threshold-driven download (1-10) / off-circuit email (1-50, cap 50) of a
  zipped CSV to the authenticated mailbox + configured admins ONLY (never a typed
  address; exfiltration-gated), pre-zip CSV saved under
  `<AuditLogRoot>\ExchangeAdminWeb\MessageTraceExports\`. MessageTrace module
  `1.1.1 -> 1.2.0`; no base app bump (reuses bulk-job runner / audit root /
  admin-email config / EXO+on-prem creds). Slice-9 verification: build 0 errors,
  807/807 tests, format/diff-check clean, `MessageTrace.razor` pure ASCII.
  **Manual-validation-on-dev / deferred (no dev tenant):** live EXO/on-prem
  detail drill-in, download parity, email/zip delivery, and the Blazor selection
  UI — enumerated in the plan's "Manual validation still required" section.
  Reviewer transport = codex headless CLI per the standing 2026-07-27 owner
  ruling (see GM-3 note below). Commits `.prompt.txt`/`.result.txt` scratch left
  untracked pending the owner's commit-vs-clean decision.

### GM-3 self-service group management — on-prem AD, task set (plan section 7) COMPLETE 2026-07-27

- **GM-3 SCOPE NARROWED AGAIN 2026-07-22 → on-prem AD ONLY. M365/delegated-Entra DROPPED entirely.**
  (`.agents/decisions.md` "on-prem AD only"; plan `docs/SelfServiceGroupManagement-Plan.md` revised,
  Status: Approved / on-prem only.) Trigger: the delegated design forced the actor↔Entra binding
  decision (F1); the owner's real need was cross-identity (Windows on-prem login acting on an
  Azure-only -CLD account's groups), which is better served by the Microsoft portal. On-prem AD
  self-service is the value the portal doesn't give.
  - **DROPPED (moot now):** second auth scheme, Microsoft.Identity.Web/MSAL, token cache,
    actor↔Entra binding, `/me/ownedObjects`, dedicated Entra registration, task 0 (§6.8), delegated
    security-review gate. Codex F1/F2/F3/F4/F8/F12/F13 moot.
  - **RETAINED = the whole feature now:** on-prem ownership reverse-lookup (`managedBy` +
    `msExchCoManagedByLink`), fail-closed eligibility allowlist (F5), user-only add/remove with
    pre-write re-checks + protected-principal (F7, F9), injection-safe resolution (F11), audit +
    affected-user notify on on-prem security-group changes (F10). No background worker needed.
  - **Reverted in-progress delegated code (slice-1 steps 1-2 from commits `4be93a3`, `22c0510`):**
    removed the Microsoft.Identity.Web package, `Services/SelfServiceGroups/DelegatedEntra*`, and the
    `ISecretFieldsReader` seam on `DelineaService`. Build green at clean baseline.
  - **Revised on-prem task set (plan §7):** 1 on-prem reverse-lookup ✅ → 2 fail-closed eligibility ✅
    → 3 module descriptor + page skeleton ✅ → 4 list + in-list filter (AC9) → 5 member add/remove with
    pre-write re-checks + audit/notify → 6 verification/manual-validation note.
  - **Task 1 DONE + codex-reviewed** (commits `0afb2bf`, `9633fa7`, `1f65674`, `7922d42`):
    `Services/SelfServiceGroups/` — `ManageableGroup` (model, `CanManageMembers` defaults false),
    `AdOwnershipFilter` (pure injection-safe RFC 4515 LDAP filter, codex F11, 10 xUnit tests
    non-vacuous by exact-output assert), `SelfServiceGroupService.GetOwnedGroupsAsync` (resolves caller
    once by immutable SID via bound -Identity, then bound -LDAPFilter query; own module cred; throws on
    hard AD failure so page shows error not empty, AC8). Codex review fixes: F-high = SID provenance
    (only a genuine Windows SID accepted, alternate identity forms rejected, AC6) + SID tests; F-med =
    dropped silent 500-group cap (Known Failure Class #2). Codex F-med on `ResolveOwnerDisplay`
    SilentlyContinue KEPT as designed — it is the plan's F12 fan-out behavior (one owner's failed
    lookup shows the DN, not a whole-load failure; main query is still -Stop). Build + format +
    diff-check clean; 697 tests green. Live AD query is manual-validation-on-dev (no dev tenant).
  - **TASK 2 REDEFINED 2026-07-23 — owner rejected the plan's admin allowlist** ("any group manager
    can just open ADUC and manage the group themselves — an arbitrary security speedbump with no
    point"). New eligibility rule: **"show groups the user can actually update."** = the caller's SID
    (or a group SID they hold) has GenericAll / GenericWrite / WriteProperty-on-`member` / WriteProperty-
    on-all-props on the group. `managedBy` "manager can update membership" is just one such ACE, so this
    SUBSUMES task 1's manager lookup. SelfMembership (self-only) does NOT qualify. This diverges from the
    Approved plan and codex F5 — plan §6.3/task 2 and `.agents/decisions.md` MUST be updated before code.
  - **Discovery DONE (read-only, `tools/Discover-GroupMembershipDelegation.ps1`, UNCOMMITTED).** Answers
    "how do L1/L2 get edit rights when not in the manager field": on a 400-group sample across the two
    biggest OUs, after stripping inherited domain-admin noise + service accounts, **82 real delegations,
    almost all DIRECT ACEs naming individual people** (69 users, e.g. 9 groups for one person); the 13
    unresolved SIDs are deleted accounts. **ZERO helpdesk-group delegation.** So non-managers get rights
    via direct per-group ACEs on themselves — not via a group. `nTSecurityDescriptor` is NOT searchable
    by trustee, so there is no cheap per-user LDAP query for "groups I can edit"; you must read ACLs.
  - **Scan universe sized (2026-07-24):** domain-wide `Get-ADGroup -Filter *` = **41,368 groups**
    (~2x the two sampled OUs). At the discovery script's ~0.017s/ACL that is ~11.7 min single-thread /
    ~5.9 min at throttle=2. Owner ruled **no OU scope is viable** — the AD has grown since NT4.0 and any
    OU allowlist would be brittle and silently miss groups. So a global ACL scan would have to cover the
    full 41k.
  - **DESIGN SUPERSEDED / SCALED BACK (owner, 2026-07-24) — the global ACL-scan design is DROPPED.**
    The 41k full-domain scan (cached global map, single-flight lock, multi-minute blocking wait) is too
    expensive/risky for the value. Replaced with two cheap targeted lookups, no scan / no cache / no lock:
    1. **Passive list = managedBy-manager groups only.** Show groups where the caller is the declared
       `managedBy` manager AND "Manager can update membership" is on (the WriteProperty-on-`member` ACE
       granted to the manager). This is essentially what **task 1 already built** — task 1 becomes the
       list. NOT the broad "any editable group" rule; the 2026-07-23 ACE-based eligibility (GenericAll/
       GenericWrite/etc.) is NO LONGER the list rule.
    2. **On-demand single-group search.** User types a specific group name; resolve it and check whether
       the caller can manage its membership; if yes, return it (manageable); if no, return an error
       telling them to contact the IT Support Desk. Handles the case where a user knows they have rights
       (e.g. a direct per-group ACE, per the discovery finding) and knows the group name, without any
       domain-wide scan.
  - **Doc edits DONE (2026-07-24, commit `ec31788`):** scaled-back task-2 design written into
    `docs/SelfServiceGroupManagement-Plan.md` §6.3/task 2 (allowlist AND 2026-07-23 ACE-scan both
    replaced; F5 note + Status header updated) and the supersession recorded in `.agents/decisions.md`.
    The 41k scan-sizing work and the global-map design are history, kept above only as the rationale
    for dropping the scan.
  - **TASK 2 DONE + codex-reviewed** (`.agents/review/findings/gm3-task2-slice1.md`, `-slice2.md`):
    (a) list-time eligibility = manager-can-update-membership (WriteProperty-on-`member`), enforced per
    candidate via credentialed-drive DACL read (`GroupMembershipAce` pure classifier keys on rights BITS
    not the shared `member` schema GUID); (b) on-demand single-group search
    (`SearchManageableGroupAsync`, injection-safe RFC 4515, not-found/ambiguous/not-manageable all return
    the same "contact IT Support Desk" message). Slice-2 codex round caught + fixed a HIGH: the SID gate
    accepted SDDL aliases (BA/DA/SY/WD) — now requires canonical-SID round-trip (`e748e32`).
  - **TASK 3 DONE + codex-reviewed** (`ba22cf5` slice, `47f357a` review record;
    `.agents/review/findings/gm3-task3-slice1.md`): `SelfServiceGroups` module descriptor
    (`Modules/ModuleCatalog.cs`: Access-only FailClosed, Directory & Groups, SortOrder 165,
    EnabledByDefault=false, v1.0.0, on-prem `DelineaSecretId`), DI registration (`Program.cs`,
    Scoped), and `Components/Pages/SelfServiceGroups.razor` (`[Authorize]` + OnInitializedAsync
    re-check, `<ModuleVersion/>`, load button with required spinner + disabled state per plan 6.4,
    owned-groups table, on-demand single-group search box; caller SID from the PrimarySid claim, AC6).
    Adding a module did NOT bump the base app version. Count-guards updated (23 modules, 32 aliases),
    proven non-vacuous. codex verdict accepted, no material issue.
  - **TASK 4 DONE + codex-reviewed** (`f17f3de` slice; `.agents/review/findings/gm3-task4-slice1.md`,
    index row `gm3-task4-slice1`): in-list filter (AC9). `Services/SelfServiceGroups/ManageableGroupFilter.cs`
    (pure UI-free helper: case-insensitive SUBSTRING match across Name/SamAccountName/Description, blank
    term returns all, input order preserved) wired into `Components/Pages/SelfServiceGroups.razor` (filter
    input over the loaded list only — no directory round-trip; filtered-empty renders a message distinct
    from the AC8 load-failure error; `filterTerm` reset each load). 12 xUnit tests, proven non-vacuous
    (Contains->StartsWith fails 3, restore passes 12, `git diff --stat` empty). Full suite 728/728,
    build/format/ASCII-lint clean. codex-commercial (MCP transport, default model/effort) verdict accepted,
    no material issue. Review dispatched read-only/static-code-judgment-only: two prior dispatches (codex
    cli, then a first codex-commercial run) both died at ~30 min trying to run the test suite — the cli on
    the sandbox's blocked NuGet feed (isolated snapshot can't `dotnet restore`), the MCP run on the
    transport idle timeout during that silent test run. Runtime guard proof is the coder's job and was
    done; the reviewer's contribution is static judgment, which needs no build.
  - **TASK 5 IN PROGRESS** — member add/remove with pre-write re-checks + audit/notify (the ONLY mutation
    in the first cut). USER-ONLY members; resolve exactly one immutable member id; before each write
    re-check module permission + re-read group + re-check eligibility + re-check ownership by immutable
    id + ProtectedPrincipalService.CheckAsync on the affected member; fail-closed; serialize same-group
    ops; idempotent desired-state; post-write read-back reconciliation; NO background worker/outbox.
    Owner decisions settled B/B: notify = audit-first best-effort, no background worker (Decision 1);
    TOCTOU = accept + document the ms race, least-privilege write cred as backstop (Decision 2).
    - **Slice 5a DONE + codex-reviewed** (`08a2a53` slice, `1dac5d5` review; findings `gm3-task5-slice5a`):
      pure decision core `MembershipChangeReconciler` (idempotent desired-state + read-back
      reconciliation, 6 xUnit tests). Accepted, static-only, no material issue.
    - **Slice 5b DONE + codex-reviewed** (`6fd722f` slice; F1 fix `246a197`, F2 fix `5ef1b0d`; `46e4bb6`
      review record; findings `gm3-task5-slice5b`): live AD write path `SelfServiceGroupService.ChangeMemberAsync`
      — resolves the affected member ONCE (own cred, RFC 4515 person/user-only filter) into a
      ResolvedDirectoryPrincipal, checks + writes THAT principal (F1), reconciles the write even when the
      Invoke throws with a guarded read-back (F2). 740/740 tests, build/format/ASCII clean. codex-commercial
      (MCP, default) reopened on the slice commit for F1/F2, accepted after both one-per-commit fixes.
    - **Slice 5c DONE + codex-reviewed** (`164b83a` return metadata + `MembershipChangeResult` record + 8
      tests, `aafc13e` email method, `b461fed` page UI + version bump; `.agents/review/findings/gm3-task5-slice5c.md`,
      index row `gm3-task5-slice5c`): `Components/Pages/SelfServiceGroups.razor` Manage-members panel calls
      `ChangeMemberAsync` (add/remove USER by typed identity — no member-list method in first cut); page
      re-checks the SelfServiceGroups policy before the write (defense in depth, AC5); audit-first
      best-effort notify (owner decision B) — `Audit.LogModuleAction` FIRST (own try/catch, never masks),
      then best-effort `Email.SendAdminNotificationAsync`, then affected-user
      `Email.SendGroupMembershipUserNotificationAsync` gated by `MembershipChangeResult.NotifyAffectedUser`
      (success AND real change AND security group AND known address — AC10, Constitution Notifications).
      `ChangeMemberAsync` now returns `MembershipChangeResult` (PermissionResult + notify metadata from the
      SAME single member resolution — no F1 regression). SelfServiceGroups module 1.0.0 -> 1.1.0 (no base
      app bump). Build 0 errors, 748/748 tests, ASCII/format/diff-check clean; gate non-vacuity proven
      (IsSecurityGroup term inverted -> distribution test fails -> restore -> 8/8). Live add/remove+notify
      is manual-validation-on-dev (no dev tenant).
    - **REVIEWER TRANSPORT (2026-07-27):** the codex-commercial MCP reviewer CANNOT see un-pushed local
      HEAD — under a no-shell constraint it has no local file reader and falls back to the connected GitHub
      repo, so a local-only SHA returns `invalid`; without the constraint it runs `dotnet` and dies at the
      30-min idle timeout on the blocked NuGet feed. Owner ruling: use **codex headless CLI** for these
      reviews — `codex exec -s read-only --output-last-message <file> - < promptfile` (prompt via stdin;
      too long for an arg), run in background. It has direct read-only LOCAL repo access, so it reviews
      un-pushed commits fine. A recurring `Failed to refresh token` line in its log is benign (review still
      runs). Do NOT use `--skip-git-repo-check`. codex-cli 0.145.0 on this machine.
    - **Task 6 DONE (docs-only)** — verification + manual-validation note, the last task in the plan §7 set.
      Filled plan §9 traceability (AC->automated-vs-manual mapping) and bumped the plan Status/Last-verified
      header to `1920eb8 (2026-07-27) — task set COMPLETE`. Verification at that commit: build 0 errors,
      748/748 tests, ASCII lint + `dotnet format --verify-no-changes` + `git diff --check HEAD` all clean.
      Manual-validation-on-dev / deferred (no dev tenant): live AD add/remove, audit-record write, admin +
      affected-user email sends, and the Blazor page flow. No delegated security-review gate (M365 half
      dropped — no cloud tokens). **GM-3 task set (plan §7) is now COMPLETE.**
  - codex invocation notes: wrapper takes prompt as an ARG. The revised plan is now TOO LONG to pass
    as an arg (node "filename or extension is too long") — instead give codex a SHORT prompt telling
    it to Read the plan file itself (it has read-only repo access; this worked, task `bhqsbvopo`).
    Do NOT pipe `2>&1 | Tee-Object` — trips the wrapper's stdout-encoding requirement. Run in
    background (reasoning effort is "max", ~5-15 min/round, exceeds a 10-min foreground cap).
    Do NOT add `--skip-git-repo-check` (trips the safety classifier).
