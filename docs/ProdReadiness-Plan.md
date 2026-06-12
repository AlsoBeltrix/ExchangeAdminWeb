# ProdReadiness Plan

Status: Approved
Owner: Michael
Last verified against code: commit 0021502 / 2026-06-12

<!-- Sections marked [YOU] are written or approved by Michael, in plain language.
     Sections marked [MODEL] are drafted by the model and only skimmed by Michael.
     This is a change ticket for source code. Treat it like one. -->

Findings register: `docs/ProdReadinessReview-2026-06-12.md` (75 confirmed findings,
29 leads, 6+1 refuted). This plan addresses all of them. Q3 resolved: Michael will
not release to prod until all phases are complete, so every phase gates the release;
the phase split is execution order and review checkpoints, not a release boundary.

## 1. Goal  [YOU — 3 to 6 sentences]

Get ExchangeAdminWeb ready for a real production release. The app was mostly developed
by GPT and Claude Opus across many sessions; a 2026-06-12 multi-agent review found
gotchas and inconsistencies from that multi-model workflow which must be fixed before
prod. Specifically: the test suite and CI must actually run; the fail-closed
authorization and pre-write re-check conventions adopted by newer modules must be
backported to the legacy Exchange modules; audit gaps must be closed; the deploy
pipeline must stop reporting success on failure and must support dry-run; and the UI
freezing that is felt in daily use ("I click a button and nothing happens until I click
again, most of the time") must be fixed.

Context from Michael: on-prem Exchange is being decommissioned this year and all
conference rooms are cloud mailboxes, so the dead ConferenceRooms on-prem credential
path is retired, not repaired (Q1 resolved 2026-06-12: Michael confirmed it is
Exchange **on-prem** being decommissioned). The Delinea bootstrap via Windows Credential Manager
(`Delinea_Client`) is working as designed and must be preserved.

## 2. Non-goals  [YOU — bullets]

- No ServiceNow validation/writeback (Constitution: out of scope unless requested).
- No new modules or features; remediation only.
- No repair or extension of on-prem Exchange code paths beyond making their failures
  explicit and auditable (on-prem decommissioning confirmed — Q1 resolved). Existing
  on-prem branches that work stay untouched until decommissioning removes them.
- No refactor sweeps beyond the named consistency findings; one finding per commit.
- No removal of `CredentialManagerService` / the PasswordVault bootstrap — it is
  load-bearing (review's "dead code" claim was wrong).
- Low-severity style/naming drift is recorded in the register, not fixed in this
  work stream unless a Phase task already touches the same lines.
- No multi-PAM abstraction. Michael's principle (Q4): all secrets live in the
  deployment's preferred PAM, which for our deployment is Delinea. Support for other
  PAMs (e.g. CyberArk for a future customer) is a recorded future consideration, not
  part of this work stream.

## 3. Acceptance criteria  [YOU approve each; model may propose]

Phase 1 — make verification real:
- AC1: A GitHub Actions run on `master` shows the full xUnit suite executed (≥370
  tests, nonzero count visible in the log) and a deliberately broken test makes the
  run fail. Local documented command runs the same suite.
- AC2: `tests/ps/` exists with Pester coverage for the deploy invariants that have
  already bitten (robocopy exclusions for `appsettings*.json`/`config`/`logs`,
  `$LASTEXITCODE` handling, plan-only produces no side effects), and the CI Pester
  step executes them.

Phase 2 — security backports:
- AC3: Every mutating module permission in `ModuleCatalog` is `FailClosed: true`.
  With `config/sectionaccess.json` missing or corrupt, MailboxPermissions,
  CalendarPermissions, Migration (all three permissions), and OutOfOffice deny
  access; a test proves it. (Rollout pre-check: Q2.)
- AC4: MailboxPermissions cloud+bulk, CalendarPermissions cloud+bulk, and
  AdminEventLog undo re-check authorization immediately before the write; a
  de-permissioned live circuit gets a denial, not a mutation. Guard-revert proven
  (revert the fix, watch the new test fail).
- AC5: `ProtectedPrincipalService` fails closed when the MailboxPermissions module
  config is corrupt (matches `PermissionValidator` behavior); test proves corrupt
  config ⇒ check returns Failed, not NotProtected.
- AC6: Converting a room to CEO/Restricted reports failure (not success) when any
  existing calendar permission cannot be enumerated or removed.
- AC7: `GroupAuthorizationHandler` has direct unit tests covering: empty groups ⇒
  deny, disabled module ⇒ deny, DOMAIN\group normalization, case-insensitive match,
  non-member ⇒ deny.

Phase 3 — operational truth (deploy + audit + UI):
- AC8: A failed `deploy.ps1` (e.g. failed `dotnet publish`) makes
  `deploy-pipeline.ps1 -Dev` fail loudly; nothing prints "deployment complete".
  `Write-Fail` throws per the repo error model.
- AC9: The documented pipeline path supports a plan-only/dry-run invocation for prod
  promotion (promote's native dry-run reachable; consent switch not auto-asserted).
- AC10: Every failed user-facing mutation writes an audit event: ADAttributeEditor
  failed saves (the dead `HadErrors` branch replaced with try/catch auditing) and
  Migration batch-removal failures included; an audit-write failure never makes a
  completed operation report as failed.
- AC11: MFA reset distinguishes "user has no methods" (HTTP 200, empty list) from
  "Graph request failed" (403/404/429/5xx) — the latter reports failure with status.
  Same null-collapse fixed where `GraphTokenClient.GetAsync` consumers treat failure
  as empty (NamedLocations first page).
- AC12: Audit records carry the correct per-circuit client IP: a session open longer
  than 1 hour still audits with its real IP, and two concurrent sessions of the same
  account audit with their own IPs. No mutation is blocked by IP lookup failure
  (degrade to "Unknown", never throw).
- AC13: Every operation button gives immediate visual feedback and renders its result
  without a second click: no fire-and-forget handlers (`_ = SomeAsync()`) without
  re-render, and no synchronous `Connect-ExchangeOnline` on the circuit dispatcher.
  Michael confirms the felt symptom is gone in dev.
- AC14: ConferenceRooms on-prem path retired (Q1 resolved): `OnPremDelineaSecretId` field
  removed from catalog + example config, and the Set-RemoteMailbox step either
  removed or made an explicit, audited "unsupported (on-prem deprecated)" failure.

Phase 4 — cleanup backlog (pre-release per Q3; ordered last):
- AC15: Docs/agent-state drift fixed in one sweep: AGENTS.md CI claim, `.agents/state.md`,
  `.agents/repo-map.json` CI entry, `AdminModuleSpec.md` version header, README
  (project structure, Workspace template, installer pointer), plan files missing
  `Status:` headers.
- AC16: Remaining confirmed medium findings from the register either fixed (one per
  commit) or explicitly accepted as risk in this plan's §10 review log.

## 4. Failure behavior  [YOU own — this is the risk section of a change ticket]

| Step / dependency | If it fails | The user sees | System state afterward |
|---|---|---|---|
| First real CI run (AC1) may surface latent test failures on windows-latest | Build red on push | Failing checks on GitHub | No deploy impact; fix-forward before proceeding |
| FailClosed flip (AC3) where prod section access was never configured for legacy modules | Legitimate admins locked out of MailboxPermissions/CalendarPermissions/Migration | "Access denied" | Fail-closed by design; pre-check prod `sectionaccess.json` before rollout (Q2) and configure groups first |
| Pre-write re-check (AC4) added with wrong policy alias | Denials for permitted users | Operation aborts with auth error | No writes occur (fail-closed); fix alias, re-test |
| `Write-Fail` exit→throw change (AC8) alters behavior of any caller relying on `exit 1` | Pipeline aborts where it previously continued | Terminating error in console | Desired behavior; Q5 resolved — repo grep confirms `deploy-pipeline.ps1` is the only invoker (installer is standalone per Invariant 1) |
| ClientInfo per-circuit capture (AC12) fails on a circuit | IP unavailable | Audit shows "Unknown" IP | Mutation still proceeds; never block on IP |
| EXO connect moved off dispatcher (AC13) | Connect failure unchanged | Existing error path | Pool semantics unchanged; only thread changes |
| Removing on-prem room path (AC14) while an on-prem-mastered room still exists | Cloud write rejected for that room | Explicit audited failure naming the deprecation | No partial state; today's behavior already fails (credentials never resolved) |

## 5. Rollback / blast radius  [YOU own]

- One finding per commit (repo Git rule), so every change reverts independently with
  `git revert`. No data migrations; no config-schema changes except deleting the
  never-read `OnPremDelineaSecretId` field (old config files keep the key harmlessly).
- Versioning: shared-infrastructure changes (auth handler tests, audit plumbing,
  ClientInfo, ExoConnectionPool, deploy scripts, CI) bump the base app version once
  per release batch; module-scoped behavior changes (MailboxPermissions,
  CalendarPermissions, Migration, MfaReset, ConferenceRooms, AdminEventLog) bump each
  module's `Version` in `ModuleCatalog.cs`. Both rules fire independently.
- Highest blast radius: AC3 (could lock admins out — mitigated by Q2 pre-check) and
  AC8 (changes deploy failure semantics — mitigated by Pester coverage from AC2 and a
  dev-only deploy first).
- Prod promotion happens only after a full dev validation pass; promote retains its
  backup/rollback path, now reachable in dry-run form (AC9).

## 6. Design sketch  [MODEL — Michael skims]

All claims below verified against current code this session (file:line cited).

**CI/tests (AC1, AC2).** No `.sln` exists; `ExchangeAdminWeb.csproj:7` excludes
`ExchangeAdminWeb.Tests\**`, so `dotnet build`/`dotnet test` at repo root
(`.github/workflows/ci.yml:17-19`) resolve only the web csproj and run zero tests
(reproduced locally on SDK 10.0.300). Add `ExchangeAdminWeb.slnx` referencing both
projects; point CI `Build`/`Test` and the AGENTS.md command at it. Add
`-p:EnableWindowsTargeting` guidance for macOS dev in AGENTS.md commands. Fix the
Windows-only test `AuditServiceTests.LogMailboxPermission_ThrowsOnWriteFailure`
(`ExchangeAdminWeb.Tests/AuditServiceTests.cs:196-201` uses `Z:\...`, valid on
macOS) with a cross-platform invalid path or `[SkippableFact]`-style OS guard. Create
`tests/ps/` Pester suites; harden the CI Pester step (`ci.yml:44-46`) with
`-SkipPublisherCheck` and make it fail (not no-op) now that the directory exists; add a
`dotnet format --verify-no-changes` step.

**FailClosed backport (AC3).** `Modules/ModuleCatalog.cs`: main permissions for
MailboxPermissions (~line 130), CalendarPermissions (~150), Migration + its two
granulars (~169-170), OutOfOffice (~245), DelegationReport (~194), RecipientLookup
(~227) lack `FailClosed: true` while every newer module has it (e.g. read-only
MessageTrace at 209). Flip the mutating ones (Q2 decides read-only ones).
`SectionAccessService.GetGroupsForSection` already implements the fail-closed
semantics; only the flags change.

**Pre-write re-checks (AC4).** Copy the existing `ReauthorizeAsync` pattern (already
proven in ConferenceRooms.razor) into `MailboxPermissions.razor` `SubmitSingle`/
`ProcessBulk`, `CalendarPermissions.razor` `SubmitSingle`/`ProcessBulk` (their on-prem
branches already re-check `*OnPrem`; the cloud paths don't), and
`AdminEventLog.razor` `ExecuteUndo` (re-check `EventLog` + `UndoAuditedActions`,
abort on failure). Authorization changes require this written plan per the
Constitution — that requirement is satisfied by this document.

**Protected principals (AC5).** `ProtectedPrincipalService.GetLegacyExclusions`
(~line 346) reads `MailboxPermissions/ExcludedUsers` without an `IsModuleCorrupt`
check, silently losing exclusions on corrupt config; mirror the fail-closed handling
`PermissionValidator` already implements for the same data.

**CEO room conversion (AC6).** `ConferenceRoomService.SetRoomTypeAsync` step 3
(~line 545) uses `InvokeOptional` + `-ErrorAction SilentlyContinue`, which clears the
error stream uninspected; switch the enumeration and each removal to
`InvokeBestEffort` and record the step failed when errors were captured, so the
existing `failedSteps` aggregation surfaces it.

**Deploy truth (AC8, AC9).** `deploy.ps1:39` `Write-Fail` = `Write-Host; exit 1` —
change to `throw` per AGENTS.md Invariant 5 (installer already throws).
`tools/deploy-pipeline.ps1:80,159` apply robocopy semantics (`-ge 8`) to PS child
scripts — change to `-ne 0` (belt-and-braces once Write-Fail throws). Add `-PlanOnly`
to deploy-pipeline that (a) passes a new plan-only mode through to `deploy.ps1`
(retrofit `Invoke-PlanOrAction` from `tools/Install-ExchangeAdminWeb.ps1`) and
(b) for `-Prod` omits `Apply`/`IUnderstandThisOverwritesProd` (currently hardcoded
`$true` at lines 149-150), letting `promote-dev-to-prod.ps1`'s native dry-run run.
Move the staging cleanup (`deploy.ps1:469`) into the `finally` so failed upgrades
don't strand staging folders containing live `appsettings.json`.

**Audit gaps (AC10, AC11, AC12).** ADAttributeEditor: `Set-ADUser` runs with
`-ErrorAction Stop`, so failures throw past the `if (ps.HadErrors)` audit branch
(`ADAttributeEditorService.cs:595-604` — dead code); wrap in try/catch, audit, return
failed result; fix the stale comment at `ADAttributeEditorUndoService.cs:227`.
Migration `ClearCompletedBatches` (`Migration.razor:~1254`): audit the failure branch
and the auth denial; report audit-write failures as warnings, not operation failures.
MfaReset: `GraphTokenClient.GetAsync` returns null on any non-success status; change
it to surface status (result object or throw) and fix the three divergent consumers
(MfaReset blanket-success, NamedLocations empty-list, M365 warn-only).
ClientInfo: `Services/ClientInfoService.cs` is a static username-keyed cache with
1-hour TTL (lines 7, 27, 37) — circuits outliving the TTL audit as "Unknown" and
concurrent same-user sessions cross-attribute IPs. Replace with per-circuit capture:
a `CircuitHandler` that snapshots the IP at circuit establishment into the
circuit-scoped `ClientInfoService` instance (which exists but is currently never
populated inside circuits); keep the static cache only as fallback.

**UI responsiveness (AC13).** Two confirmed mechanisms matching the felt symptom:
(1) fire-and-forget handlers — e.g. `MfaReset.razor:161-165` `HandleKeyDown` does
`_ = ListMethods()`; Blazor doesn't await it, so completion never triggers a
re-render — the result appears only on the next UI event (the second click).
Sweep all pages for `_ =`/unawaited handler patterns; make handlers `async Task`
or wrap completion in `InvokeAsync(StateHasChanged)`.
(2) `ExoConnectionPool.BorrowAsync` calls `CreateConnected()` synchronously
(`ExoConnectionPool.cs:120`), running multi-second `Connect-ExchangeOnline` on the
circuit dispatcher and freezing all UI events for that user; move connect off the
dispatcher (`Task.Run` inside the pool, which is singleton state anyway).
Also stop autocomplete keystrokes borrowing the 5-slot EXO mutation pool
(`RecipientAutocomplete.razor:104`) — debounce + dedicated read path; and remove the
per-query runspace + global lock in `ADDirectorySearchService.cs:80`. Verify with the
running app (the felt symptom), not just tests.

**ConferenceRooms on-prem retirement (AC14).** Catalog field `OnPremDelineaSecretId`
(`ModuleCatalog.cs:332`) is read by nothing — `ModuleCredentialService.cs:27` reads
literal `"DelineaSecretId"` — so the documented field is dead and on-prem-mastered
room writes always fail today. Per the deprecation decision: delete the field and the
example-config key, and make `SetRemoteMailboxAsync` an explicit audited unsupported
step (or delete it) instead of wiring the key.

**Docs/state sweep (AC15).** AGENTS.md:117-119 ("no working CI, ci.yml misplaced in
repo root") is false — `ci.yml` is at `.github/workflows/ci.yml`; `.agents/state.md`
describes landed commit `e1dbac1` as uncommitted/unpushed; `.agents/repo-map.json` CI
entry stale; `AdminModuleSpec.md` header vs csproj 2.3.5; README items per register.
Each doc fixed to match code (code is the evidence for behavior).

**Consistency backlog (AC16).** Register mediums: shared `GetGraphClientAsync`
helper (5 copies), PSCredential factory (8 copies), audit category misfiling,
ModuleConfigService atomic legacy migration + case-sensitivity mismatch,
last-write-wins module-config saves, Comms10k ADWS 5000-member ceiling, icacls exit
checks, promote rollback message, test-delinea.ps1 secret printing, plaintext
SMTP/ServiceNow secrets (Q4). Fixed one per commit or risk-accepted in §10.

## 7. Task breakdown  [MODEL — Michael skims]

Ordered; each task = one commit unless noted. Phase boundaries are review checkpoints.

Phase 1 — verification (release-gating)
1. Add `ExchangeAdminWeb.slnx`; update CI + AGENTS.md commands; observe first real CI
   run with ≥370 tests; fix any latent failures it surfaces. (AC1)
2. Fix Windows-only path assumption in `AuditServiceTests` so the suite is green on
   macOS dev and Windows CI. (AC1)
3. Create `tests/ps/` with Pester suites for deploy invariants; CI Pester step:
   `-SkipPublisherCheck`, fail on missing tests; add `dotnet format` CI step. (AC2)

Phase 2 — security (release-gating; this plan = the required written plan)
4. Preflight: on the deployed instance, confirm each legacy module alias
   (MailboxPermissions, CalendarPermissions, Migration, MigrationCreate,
   MigrationManage, OutOfOffice) has a non-empty group list in
   `config/sectionaccess.json` (Q2: Michael reports per-module access groups are in
   use — sidebar already hides modules from users outside the group — so this is a
   safety net, expected to pass). Then flip `FailClosed: true` on mutating legacy
   permissions + test. (AC3)
5. MailboxPermissions cloud/bulk pre-write re-check + test (guard-revert proven). (AC4)
6. CalendarPermissions cloud/bulk pre-write re-check + test. (AC4)
7. AdminEventLog `ExecuteUndo` re-check (`EventLog` + `UndoAuditedActions`) + test. (AC4)
8. `ProtectedPrincipalService` corrupt-config fail-closed + test. (AC5)
9. `SetRoomTypeAsync` permission-removal error surfacing (`InvokeBestEffort`) + test. (AC6)
10. `GroupAuthorizationHandlerTests` (new file, 5 scenarios). (AC7)

Phase 3 — operational truth (release-gating)
11. `deploy.ps1` `Write-Fail` → throw; staging cleanup into `finally`; Pester. (AC8)
12. `deploy-pipeline.ps1` exit checks `-ne 0`; `-PlanOnly` flow incl. prod dry-run;
    Pester. (AC8, AC9)
13. ADAttributeEditor failure-path audit (try/catch around save; stale comment). (AC10)
14. Migration batch-removal failure + denial audits; audit-failure isolation. (AC10)
15. `GraphTokenClient` status surfacing + three consumer fixes. (AC11)
16. ClientInfo per-circuit IP capture via `CircuitHandler` + tests. (AC12)
17. Fire-and-forget handler sweep across pages (single commit, mechanical). (AC13)
18. `ExoConnectionPool` connect off dispatcher. (AC13)
19. Autocomplete: stop borrowing mutation pool; AD search per-query runspace/lock fix.
    (AC13; may split into two commits)
20. Manual verification with Michael in dev: click-responsiveness confirmed. (AC13)
21. ConferenceRooms on-prem field + Set-RemoteMailbox retirement (Q1 resolved). (AC14)

Phase 4 — cleanup backlog (pre-release per Q3)
22. Docs/agent-state drift sweep (one commit per doc). (AC15)
23. Register mediums, one per commit, severity order; risk-accept leftovers in §10.
    (AC16)
24. Secrets-in-PAM (Q4 resolution): route the ServiceNow password through a Delinea
    secret ID when `ServiceNow:Enabled` is true (plaintext appsettings value retained
    only as legacy upgrade fallback per the Constitution); confirm and document that
    SMTP runs credential-less against the open relay (EmailService already defaults
    `SmtpUsername`/`SmtpPassword` to empty — verified `Services/EmailService.cs:28-29`).
    (AC16)

Version bumps: base app version once per phase batch that touches shared
infrastructure; module versions per touched module (rule fires independently).

## 8. Test plan  [MODEL writes; YOU check the mapping only]

- AC1: CI run log shows test count ≥370; deliberate red-test canary run; local
  `dotnet test ExchangeAdminWeb.slnx` (with `-p:EnableWindowsTargeting=true` on macOS).
- AC2: Pester suites in `tests/ps/` covering robocopy exclusion args, exit-code
  thresholds, plan-only no-side-effects (script parses + targeted function tests);
  CI Pester step green and non-vacuous.
- AC3: xUnit: catalog assertion that every mutating permission is FailClosed;
  SectionAccess-style integration test (temp config dir): missing + corrupt fragment
  ⇒ deny for each backported alias.
- AC4: xUnit/bUnit-style handler tests where practical; otherwise integration test of
  the re-check helper + guard-revert procedure recorded in the commit message.
- AC5: xUnit: corrupt `module-config-MailboxPermissions.json` ⇒ `CheckTargetAsync`
  returns Failed (mirrors existing `EmergencyDisableServiceTests` fail-closed tests).
- AC6: xUnit: stub error stream on removal ⇒ step Success=false ⇒ operation reports
  partial failure (extends `ConferenceRoomSyncedRoomTests` patterns).
- AC7: New `GroupAuthorizationHandlerTests` (5 scenarios, real `SectionAccessService`
  over temp files as `SectionAccessServiceTests` already does).
- AC8: Pester: child-script `exit 1` ⇒ pipeline throws; `Write-Fail` throws.
- AC9: Pester: `-PlanOnly -Prod` invokes promote without `Apply`; no mutations (assert
  via promote's own dry-run output).
- AC10: xUnit: throwing `Set-ADUser` (substituted invoker) ⇒ audit failure event
  written; Migration removal failure ⇒ audit event; audit-write throw ⇒ operation
  still reports success.
- AC11: xUnit: `GraphTokenClient` 403/404/429 ⇒ MfaReset result Success=false with
  status; 200-empty ⇒ Success "no methods".
- AC12: xUnit on the new circuit-scoped capture (store-then-read across simulated
  sessions; concurrent same-user isolation); manual: tab open >1h then mutate, audit
  shows real IP.
- AC13: Manual in dev with Michael (felt symptom); plus analyzer-style grep gate in CI
  or test asserting no `_ = ` fire-and-forget in `Components/Pages` (best-effort).
- AC14: xUnit: ConferenceRooms ConfigFields no longer contains the dead key; room-type
  operation on a room requiring on-prem write yields the explicit audited
  unsupported failure.
- AC15: drift skill pass re-comparing each doc claim to code; no code tests needed.
- AC16: per-finding tests as listed in the register's suggested fixes.

## 9. Traceability check  [MODEL fills when iteration ends; YOU read]

(Empty until plan iteration ends.)

## 10. Review log  [MODEL appends each round]

- 2026-06-12, round 1: plan drafted from `docs/ProdReadinessReview-2026-06-12.md`
  (multi-agent review, adversarially verified). Awaiting Michael's answers to Q1–Q5
  and approval. CredentialManagerService "dead code" finding refuted during drafting
  (DelineaService.cs:36 call site) and corrected in the register.
- 2026-06-12, round 2: Q1 answered by Michael — Exchange **on-prem** is being
  decommissioned this year (his earlier "exchange online" wording was a slip); all
  rooms are cloud mailboxes. AC14/task 21 confirmed as retire-not-repair. Q2–Q5 still
  open; Status remains Draft.
- 2026-06-12, round 3: Q2 — per-module access groups are in use (sidebar hides modules
  from non-members), so the FailClosed flip should be a no-op for current users; the
  deployed-config preflight in task 4 stays as a safety net. Q4 — SMTP is an open
  relay in our environment (no creds; EmailService already supports empty creds);
  principle recorded: all secrets live in the deployment's PAM (Delinea for us;
  multi-PAM support is a future consideration, now a §2 non-goal); ServiceNow password
  routed via Delinea in new task 24. Q5 — repo grep confirms deploy-pipeline.ps1 is
  the only deploy.ps1 caller; exit→throw change is safe. Q3 (which phases gate the
  release) rephrased and still awaiting Michael. Status remains Draft.
- 2026-06-12, round 4: Q3 answered — phase priority agreed, and Michael will not
  release to prod until all phases are done, so all phases gate the release. All open
  questions resolved; Status flipped to Approved. Implementation begins at Phase 1,
  task 1.
- 2026-06-12, round 5: Phases 1–3 implemented (tasks 1–19, 21). Notes against plan:
  (a) the first real CI run surfaced 38 Windows-only test failures caused by harnesses
  falling back to the hardcoded ADI path E:\WWWOutput — harnesses fixed; the hardcoded
  default itself moves to task 23/AC16; (b) deploy.ps1 did NOT get a native
  -PlanOnly/Invoke-PlanOrAction retrofit (§6 sketch item) — deploy-pipeline -PlanOnly
  prints the planned dev invocation and skips, while the prod path runs promote's
  native dry run, which satisfies AC9 as written; the deploy.ps1 retrofit is recorded
  as deferred scope for Michael to accept or schedule; (c) base app version bumped
  2.3.5 → 2.3.6 (shared infra: GraphTokenClient, ExoConnectionPool, CircuitHandler,
  deploy scripts, CI). Task 20 (manual UI verification of AC13 with Michael in dev)
  remains open and gates Phase 3 sign-off.
- 2026-06-12, round 6 (INCIDENT): the task-20 dev deploy triggered loss of runtime
  section-access/enablement state on dev. Full writeup, root cause, session errors, and
  required fixes: `docs/Incident-2026-06-12-DevConfigLoss.md`. New owner-directed
  requirement: the app must never write module-enablement state at startup. New tasks
  (incident fixes 1–8 in that doc) must be approved and slotted before Phase 4 proceeds.
  All deploys are on hold until incident fixes 1 (and 2 if confirmed by the appsettings
  diff) land. Task 20 blocked pending dev recovery confirmation. Handoff prepared for a
  successor session.
- 2026-06-12, round 7: Michael requested assessment/fix for whether the deploy pipeline
  is usable after manual dev config recovery. Scope approved for this slice: make the
  dev deploy path stop rewriting upgrade `appsettings.json`; keep fresh-install
  generation and prod promotion unchanged; add Pester coverage for the invariant.
  This resolves incident fix #2's deploy-side mitigation by warning about
  obsolete/missing keys instead of whole-file `ConvertTo-Json` rewrite. Utility deploy
  script changes do not bump the app version because they do not change the deployed app
  binaries. Startup enablement writes remain a separate blocker.
- 2026-06-12, round 8: incident diagnostics run on the server (Michael approved).
  Evidence (hashes, app logs, git history) rewrites the incident root cause: the
  pre/post-deploy `appsettings.json` are byte-identical (no deploy rewrite — fix #2's
  suspected mechanism refuted), the startup enablement write never fired on 6/12 (no
  migration log line; the 12:49 `modules-enabled.json` write was Michael's own Save on
  /admin-settings), and the actual cause is commit `f7df81a` (legacy mutating permissions
  made FailClosed) landing on a dev box that had no `sectionaccess.json` and no legacy
  `Security:SectionAccess` — fail-closed sections bypass the `Security:AllowedGroups`
  fallback by design. Dev recovery is confirmed healthy from logs (residual benign
  denials only for disabled LicensingUpdates/TestAccountPool, absent from prod's
  fragment). Incident fix #1 implemented in this slice per standing owner direction:
  `RunUpgradeMigration` reduced to a read-only warning (`WarnIfExchangeOnlineUnset`);
  enablement is written only by `SaveEnablement`; five startup-no-write tests added
  (three proven red against the old code). Base app version 2.3.6 → 2.3.7 (shared
  service). Deploy hold can lift once Michael accepts these findings; prod deploy of
  the FailClosed change stays gated on the §Deploy-notes alias check (prod's fragment
  already covers the six aliases with non-empty groups).
