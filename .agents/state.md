# Agent State

Current work and blockers only. Rules live in `docs/ProjectConstitution.md` and
`.agents/repo-guidance.md`, decisions in `.agents/decisions.md`, and machine observations in
`.agents/machines.md`. Each plan owns its implementation and manual acceptance checklist.
Superseded descriptions are verbatim in `docs/history/state-archive.md` (Archived 2026-09-15).

## Now

- **Branch `master`, working tree clean. Nothing is in flight.**
  The owner's issue queue is `C:\Users\mcoelho\Desktop\queue.txt` (machine-local, not in the
  repo, and not ours to write to). Queue items 1, 3 and 7 are landed, with no open review
  findings. The button gate is closed out - the owner accepted it on 2026-09-18 ("seems to
  work well enough") and added the app-wide audit to their own queue themselves, so do not
  re-raise it here as an open item.
  Work is local and unpushed - git owns the count, so ask it
  (`git rev-list --count 523b69d..HEAD`) rather than trusting a number written here; both
  remotes sat at `523b69d`, verified
  with `git ls-remote` on 2026-09-17. **Do not push, and do not treat the remote lag as
  drift.** Note for the next agent: `origin` (LAN gitea) was unreachable on 2026-09-18 -
  `SEC_E_CERT_EXPIRED`, its TLS certificate has expired - so only `github` could be checked.
  That is a host condition, not repo drift.

- **Service Health now shows its spinner on the first load; only the manual checks remain.**
  `docs/ServiceHealthLoadFeedback-Plan.md` is Implemented and owns the acceptance checklist.
  Landed 2026-09-18 in `d1ed96e`; ServiceHealth module version 1.3.2, no base app bump.
  Queue item 7.
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
  page - no bUnit harness exists - so the plan's manual checks are the only evidence the
  operator sees the fix. The implementation codereview has not been dispatched.
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

- **Owner-reported issues, raised 2026-09-15. Issues 1, 3 and 7 are landed; 2 is not started.**
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
     obvious when loading.* **Landed 2026-09-18** - see the Now entry and
     `docs/ServiceHealthLoadFeedback-Plan.md`. Code-side closed; the plan's manual acceptance
     checklist and the implementation codereview remain.

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
