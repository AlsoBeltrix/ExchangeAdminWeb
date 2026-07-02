# Agent State

First place to read for current repo state. Keep it short; update it when important
repo facts change. Resolved work lives in the plan/decision/incident docs, not here.

## Now

- App version `2.3.27` (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`).
- **Bulk Job Runner — APPROVED, NOT STARTED (2026-07-02; `docs/BulkJobRunner-Plan.md`,
  Approved).** Big base-app change: bulk room apply moves off the Blazor circuit into a durable
  server-side job so batches of any size (1000+) survive a dropped browser connection (fixes the
  ~40-room split workaround). Decisions locked: reusable `BulkJobService` (ConferenceRooms first
  caller, per-row work behind `IBulkRoomProcessor` seam); **separate operational SQLite `.db`**
  (`config/exchangeadmin-jobs.db`, env-local, NEVER promoted, excluded from config
  backup/promote); **no resume across restart** (startup flips Running+Queued → Interrupted);
  second job **queued** (one at a time — shared `ExoConnectionPool`); always cancellable;
  self-pumping singleton runner via `IServiceScopeFactory` (NOT a hosted timer — narrows, not
  overturns, 2026-06-17); explicit `InitializeAsync()` startup hook in Program.cs seeding block;
  completion email moves page→job; deploy scripts (`deploy.ps1`, `deploy-pipeline.ps1`,
  `promote-dev-to-prod.ps1`) warn (not block) on Running/Queued before recycle.
  **Authorization: option (a)** — submission-time `AuthorizeAsync` + captured role-claim
  snapshot re-checked per row via a shared pure group-checker (app has no SAM→groups lookup;
  jobs have no live principal). **Protected-principal: keep + add to BOTH Finder and Type**
  (no carve-out) — this also fixes a **pre-existing gap** (Finder bulk had NO PP check today;
  only Type did — the 2026-06-29 sweep entry that called ConferenceRooms gated was wrong for
  Finder). Plan reviewed via codex loop (8 findings→resolved). App version + ConferenceRooms
  module version both bump on implementation. NO CODE until owner says go.
- **Migration eligibility protected-principal flag DONE (2026-06-30; module `Migration`
  1.2.0→1.3.0, app version unchanged; `docs/MigrationEligibilityProtectedFlag-Plan.md`,
  Implemented; commits `acf877d`, `2fb842c`, + docs/version slice).** Check Eligibility now
  flags protected principals as an axis **orthogonal** to the Ex/AD verdict (does NOT change
  Eligible/Ineligible). Owner decision (2026-06-30, decisions.md): protected+eligible shows
  **Eligible**+escalate flag; protected+ineligible shows **Ineligible**+escalate flag.
  **Single-user**: protected ⇒ Create Migration Batch button suppressed (same as ineligible).
  **Bulk/group**: create flow unchanged (GAP 2 gate still filters+reports at creation); table
  just shows the protected marker. Fail-closed on Unavailable/Ambiguous/CheckFailed. Reuses
  existing `CheckProtectedAsync` via new `ApplyProtectionFlagAsync` seam in
  `CheckMigrationEligibilityAsync`; check is a read — no new denial audit/alert at check time
  (GAP 2 does that at create), existing check audit/notification record protected status. 4 new
  unit tests, proven non-vacuous; 593/593 green, format/diff-check clean. **Manual validation
  on dev DONE (owner, 2026-06-30).**
- **GAP 2 — Migration protected-principal gate DONE (2026-06-30; module `Migration`
  1.1.3→1.2.0, app version unchanged; `docs/MigrationProtectedPrincipalGate-Plan.md`,
  Implemented; commits `0b855ac`, `5d72978`, + docs/version slice).** Closes the last
  protected-principal gap from the 2026-06-29 sweep. `CreateMigrationBatchAsync` now
  partitions every target through the protected-principal gate **before** any side effect
  (CSV build / `New-MigrationBatch`), on both ToCloud and ToOnPrem. Owner decision
  (2026-06-30, decisions.md): protected targets are **filtered out and reported, never
  silently**, and **one protected target never blocks the whole batch** — the rest migrate;
  exclusions show in a persistent UI warning, get their own audit denial rows, and are listed
  in the admin notification. All-protected (incl. single target) ⇒ nothing created, plain
  refusal. Fail-closed on Unavailable/Ambiguous/exception. Same accepted cloud-only `NotFound`
  limitation as GroupManagement/M365 (most relevant on move-back). New `PermissionResult.
  ExcludedTargets` field (backward compatible). 4 new tests, proven non-vacuous; 589/589 green,
  format/diff-check clean. **Manual validation on dev DONE (owner, 2026-06-30).**
- **Comms-10k replace UX clarity DONE (2026-06-29; module 1.0.3→1.0.4, app version
  unchanged; `docs/Comms10kReplaceUx-Plan.md`, Approved).** Bug report: comms team said
  membership "did not sync" (validated 4309 but Entra still showed old 4307). Logs
  (`E:\WWWOutput\ExchangeAdminWeb`) proved no write occurred — only a `Comms10k_Export`
  on 2026-06-29; last real `Comms10k_Replace` was 2026-06-11. Root cause: UX trap — user
  stopped after Validate, read "resolved" as "applied". Fix (Razor only, no service/logic
  change): (1) relabel post-Validate result "Validated — not applied yet … list has not
  changed"; (2) persistent "Pending apply — list unchanged" badge; (3) stronger
  Replace CTA (btn-lg + helper text); (4) success note that Entra count updates on next
  directory sync, not instantly; (5) **ticket number made optional** (owner: comms team
  won't always have one) — Validate no longer disabled without a ticket; an entered ticket
  is still validated where ServiceNow is on (Constitution:104 — ticket is plain audit
  metadata unless validation requested). 585/585 green, format/diff-check clean. Manual
  validation on dev pending.
- **Notifications now mandatory (decision 2026-06-29, docs-only).** Every mutating action
  → admin notification; every security-sensitive read → admin alert; every permission/access
  change → also notify the affected user. Always via the shared `Services/EmailService.cs`
  (no bespoke mailers). Canonical rule: `docs/ProjectConstitution.md` §Auditing And Tracing →
  Notifications; Guide/Spec point to it. `.agents/decisions.md` 2026-06-29 has the full record
  and names the superseded discretionary guidance. **Enforcement sweep DONE 2026-06-30** — see
  the Notifications enforcement sweep entry below.
- **Notifications enforcement sweep DONE (2026-06-30; `docs/NotificationsEnforcementSweep-Plan.md`,
  Implemented; decisions.md 2026-06-30; commits `bd68d10`, `6e83ef9`, `14c6219`, + docs slice).**
  Read-only audit of all 20 non-system modules: rule 1 (admin-notify on mutation) mostly already
  honoured; 3 silent gaps fixed — `MfaReset` (1.0.3→1.0.4), `ConferenceRooms` (2.0.11→2.0.12),
  `AccountLockoutRemediation` (1.0.0→1.0.1, +3 non-vacuous tests). All **patch** bumps (conformance,
  not capability; owner). App version unchanged. `EmailService` admin overloads made `virtual`
  (test seam, no behaviour change). Rule 3: gap modules are admins-only; **AccountLockout
  user-notify OPEN, gated on real testing**. Rule 2 (alert on security reads): classified
  **non-applicable** for this app (reads only expose AD/address-book data, all audit) — alerting
  **deferred indefinitely**, never user-notify; Constitution §Notifications rule-2 wording narrowed
  to match. **Manual dev validation pending** for MfaReset + ConferenceRooms (page changes).
- **EmergencyDisable synced-user fix DONE (2026-06-29, commit `c1c80a3`; module 1.0.4→1.0.5,
  app version unchanged; `docs/EmergencyDisableSyncedUser-Plan.md`, Implemented).** For users
  synced from on-prem AD, `accountEnabled` is on-prem mastered and Entra rejects a direct Graph
  PATCH; the module already disables the AD master + revokes Entra sessions but then attempted
  the doomed PATCH and marked the whole op failed. Now reads `onPremisesSyncEnabled` in the
  existing pre-read; synced users skip the PATCH and record the Entra-disable step as SKIPPED
  (not failed); overall success accepts OK or SKIPPED for that step. Cloud-only users unchanged.
  Also added `GraphTokenClient.PatchWithStatusAsync` (status + sanitized error.code/message, no
  tokens/raw bodies) to surface the real Graph error on a genuine PATCH failure; `PatchAsync`
  kept as wrapper. +13 tests, non-vacuous verified. **Manual validation DONE on dev 2.3.27
  (owner, 2026-06-29) — synced-user path good.**
- **BlockedSenders module added DONE (2026-06-29, commit `5e4172e`; module 1.0.0, app version
  unchanged — new-module bump deferred per owner; `docs/BlockedSenders-Plan.md`, Implemented).**
  New cloud-only module: view EXO blocked senders (`Get-BlockedSenderAddress`) and unblock one
  by address (`Remove-BlockedSenderAddress`). Rides the shared EXO pool via `ExchangeServiceBase`,
  `DependsOn ExchangeOnline`, no module credential. Split permissions: `BlockedSenders` (view,
  fail-closed) + `BlockedSendersUnblock` (write, fail-closed, re-checked before the cmdlet).
  Audit on list/unblock/denied, operation trace on the write, admin email notification on
  success/failure. Ticket + explicit confirmation required before unblock. **Catalog now 22
  modules / 31 configurable aliases** (was 21 / 29). 553/553 green (14 new), non-vacuous verified.
  **Manual validation on dev 2.3.27 (owner, 2026-06-29): functionally good. Found slow/blank
  page load on open — fixed, see BlockedSenders load-timing fix below.**
- **BlockedSenders load-timing fix DONE (2026-06-29, commit `17910f3`; module 1.0.0→1.0.1,
  app version unchanged; `docs/BlockedSendersLoadTiming-Plan.md`, Approved).**
  The module was the only page that auto-called EXO in `OnInitializedAsync`; under
  InteractiveServer prerender that blocked the whole page for the 10-15s `Get-BlockedSenderAddress`
  round-trip before any HTML (or the spinner) reached the browser — clicking the sidebar item
  gave no feedback for 10-15s. Moved the list load to `OnAfterRenderAsync(firstRender)` (one-shot
  `loadStarted` guard); page + spinner now render immediately, list fills in after. Spinner-only
  feedback, as before (owner). One file (`BlockedSenders.razor`) + version bump. 585/585 green,
  format/diff-check clean.
- **BlockedSenders refresh-hang fix DONE (2026-06-29, commit `cde778f`; module 1.0.1→1.0.2,
  app version unchanged).** Follow-on to the load-timing fix above: owner reported the page
  loads but the spinner never stops and Refresh stays greyed out. Root cause — the list load
  runs from `OnAfterRenderAsync`, after which Blazor does NOT auto-render, so `isLoading=false`
  never reached the UI (data arrived in memory but the screen stayed frozen on the loading
  state). Added `StateHasChanged()` in `LoadBlockedSenders`' `finally` (harmless on the
  button-click path). One file (`BlockedSenders.razor`) + version bump. 585/585 green,
  format/diff-check clean. **Validated on dev (owner, 2026-06-29) — refresh completes, button
  re-enables. NOTE: prod still runs BlockedSenders 1.0.0** (the 1.0.1/1.0.2 fixes are module
  bumps, not app bumps, so the 2.3.27 prod cutover did not necessarily include them — confirm
  the prod build commit if the fix is needed in prod).
- **AccountLockoutRemediation module incorporated DONE (2026-06-26, app 2.3.26→2.3.27;
  `docs/AccountLockoutRemediation-Incorporation-Plan.md`, Status: Implemented; commits
  `0ca909a`, `2550c55`, + docs/version slice).** The validated package was spliced into the
  host tree (page/service/models/test/doc moved, catalog descriptor at SortOrder 780, DI
  line, catalog count tests 20→21 modules / 27→29 configurable aliases). Two compile errors
  the package shape-validator does NOT catch were fixed during incorporation (CS0136
  method-vs-block `message` collision → renamed `summary`; CS8030/CS9174 collection-expr
  ternary in `DiscoverPdcAsync` → `Array.Empty<string>()`). Added 10 service unit tests
  (throttle clamp proven non-vacuous; guard paths). Module ships at 1.0.0; 22 modules total
  now. 539/539 green; build/format/diff-check clean. **Manual validation still DEFERRED by
  owner (2026-06-29) even though dev is on 2.3.27** (live 4740 read, WinRM, quser/logoff
  parsing, real dry-run+logoff, protected-block) — run the package's own Manual Validation
  steps when ready. Staged copy under
  `_not_for_github/example_scripts/AccountLockoutRemediation/` left in place (gitignored).
- **Graph secret key migration DONE (2026-06-26, app 2.3.25→2.3.26;
  `docs/GraphSecretKeyMigration-Plan.md`, Status: Implemented; commits `2eb9c98`,
  `063964e`).** Resolves the "MFA Reset stranded config key" issue and the identical latent
  bug in two sibling Graph modules. Catalog-driven, idempotent startup migration
  (`ModuleConfigService.MigrateGraphSecretKeys()`, run in `Program.cs` after seeding) moves
  any value stranded under the renamed old key `DelineaSecretId` to `GraphDelineaSecretId`
  for every module that declares the new key (MfaReset, NamedLocations, M365GroupManagement,
  EmergencyDisable); on-prem modules using `DelineaSecretId` as their current key are
  untouched. The `?? DelineaSecretId` service fallback was then removed from all six read
  sites (MfaReset/NamedLocations/M365GroupManagement). Module versions →1.0.3 for the three
  changed; EmergencyDisable unchanged. 6 new tests, all proven non-vacuous; 527/527 green;
  build/format/diff-check clean. **Deployed to dev and verified by owner 2026-06-26:** the
  recovered Secret ID shows correctly on the module config page. Fully closed.
- **Final whole-branch review DONE (2026-06-26, app 2.3.24→2.3.25).** SDD review of the
  whole `e8b155c~1..HEAD` range (3 streams: EXO retry, SQLite store, guide+validator),
  one `reviewer` subagent per stream + cross-cutting pass. All three verdicts SHIP; build
  clean, format clean, `git diff --check` clean. One security-relevant finding fixed before
  done (owner: "fix before done"):
  - **Fail-closed parity fix (commits `cb4b984`, `eed13f4`):** during the one-time
    JSON→SQLite legacy import, `SectionAccessService` and `ModuleEnablementService` failed
    *open* when the DB write threw (e.g. SQLite busy) — they fell through to the permissive
    appsettings/`EnabledByDefault` path instead of denying. The two sibling authorization
    stores (`ProtectedPrincipal`, `ADAttributeEditor`) already guarded this. Wrapped both
    `ImportIfMissing` calls so a DB-write throw fails closed and leaves the legacy file for
    the next startup to retry. Two new tests, each proven non-vacuous. 521/521 xUnit green.
  - **Deferred review nits — DONE (commit `940a125`):** `AdminModuleSpec.md` section-access
    location corrected to each module's config page (`/module-config/{ModuleId}`) +
    "regardless of stored state" DB-era wording; `IConfigStore.cs`/`ConfigChangeToken.cs`
    comments corrected to state the change token is advisory and NOT consulted by the
    TTL-caching readers (they accept ≤30s staleness, plan-permitted). Wiring the readers to
    the token remains an unscheduled future option, not a defect.
- **PowerShell 5.1 ASCII fix for ops scripts (commit `46acddc`, 2026-06-26).** The SQLite
  Phase D deploy scripts had em dashes (U+2014) / section signs (U+00A7) in comments/strings
  and are UTF-8 *without BOM*. Windows PowerShell 5.1 (required by `deploy.ps1` — the IIS
  `WebAdministration` provider won't load under PS7) reads BOM-less files as ANSI, mangling
  those chars into cascading parse errors; PS7 (UTF-8 default) was unaffected, so it stayed
  latent until the first 5.1 dev deploy this session. Fixed six files: `deploy.ps1`,
  `tools/SqliteConfigBackup.psm1` (imported by deploy.ps1 — would have broken the deploy even
  after fixing deploy.ps1 alone), `tools/promote-dev-to-prod.ps1` (prod promote),
  `tools/Install-ExchangeAdminWeb.ps1`, and two Pester files — all now pure ASCII, verified
  parsing under a simulated 5.1 ANSI read; Pester 59/59 green. **This bug would also have hit
  the prod cutover** (prod on 2.3.11 has never run these scripts); now cleared.
- **CR-BUG-1 (EXO pool dead-runspace) FIXED** (`docs/ExoDeadConnectionRetry-Plan.md`,
  Status: *Implemented*, app 2.3.23→2.3.24). The pool auto-retries a dead EXO session once on
  a fresh borrow, gated to read-only + single-write ops (opt-in `allowRetry`, default off);
  the 7 multi-write delegates keep discard-and-fail so a committed write is never repeated.
  All 10 pool callers route through one `ExoConnectionPool.RunWithRetryAsync` helper (incl.
  `PermissionValidator`).
  - **Review #2 (2026-06-26, app 2.3.24):** retry trigger narrowed — DISCARD on any
    connection error (broad), but RETRY only on the pre-cmdlet "must call Connect-ExchangeOnline"
    signature (`IsRetriablePrecheckError`), so a single write whose session drops *after*
    Exchange accepted it is never re-submitted. Also excluded git-ignored `_not_for_github\`
    from csproj compile globs (was breaking local builds; not part of the bug fix).
- **SQLite config store work stream COMPLETE** (`docs/SqliteConfigStore-Plan.md`, Status:
  *Implemented*). All Phases A–E done (app 2.3.21, 2026-06-24). 508/508 tests green.
  - Phase E note: all service test rewrites were completed inline during Phases B-D;
    Phase E delivered the docs sweep (Constitution, AGENTS.md, AdminModuleSpec.md,
    example JSON retired) and version bump to 2.3.21.

- **Module Developer Guide Rewrite COMPLETE** (`docs/ModuleDeveloperGuideRewrite-Plan.md`,
  Status: *Implemented*, app 2.3.22, 2026-06-25). Re-verified + rewrote
  `docs/AdminModuleDeveloperGuide.md` and `docs/AdminModuleSpec.md` against the SQLite-era
  codebase, and added `<ModuleVersion />` validator enforcement (Error `PAGE009` in
  `tools/validate-module-package.ps1` + execution test `tests/ps/ValidatorChecks.Tests.ps1`,
  proven non-vacuous). A Codex review pass was folded in (one-pass, owner-approved): Graph
  credential key corrected to `GraphDelineaSecretId` in spec + guide, guide host baseline →
  2.3.22, this state block repaired. 508/508 xUnit + 59/59 Pester green.
  - Final whole-branch review now DONE (see top of Now). Branch is review-complete.

## Next up (prioritized)

Live backlog only (DONE items moved out). All items need an approved plan before code.

1. **Module packaging/import** — needs `docs/ModulePackaging-Plan.md` written + approved.
   End state confirmed 2026-06-29 (UI `.zip` upload, no full rebuild; precompiled-vs-runtime
   open). First leg = module contract / self-registration seam. See Queued work + decisions.md.
2. **Versioning-rule fix** (OPEN blocker, below): record a `decision` that new modules do not
   bump the base app version, then fix Constitution §Deployment And Versioning + AGENTS.md
   invariant #6. Small, docs-only; tied to the module-packaging end state.
3. **Bulk Job Runner** — APPROVED, ready to implement (see "Now" block + `docs/BulkJobRunner-Plan.md`).
   Root cause of the ~40-room split was the bulk loop living inside the Blazor circuit; the fix is
   the durable server-side job, not a timeout tweak. NO CODE until owner says go.
4. **GM-3** self-service group management — needs own plan; depends on M365 work (done).
5. **AccountLockout user-notification** (OPEN, gated on testing) — decide whether a logged-off
   user is notified, after the module is actually exercised on dev. See decisions.md 2026-06-30.

Separate track (ops, not engineering): configure ConferenceRooms AD `DelineaSecretId` in the
prod instance (gates CR-1 in prod); `deploy.ps1` native `-PlanOnly` (workaround exists).

## Blockers

- None blocking current work.
- **OPEN — versioning rule is wrong for new modules (owner, 2026-06-26; not yet fixed).**
  Current rule (Constitution §Deployment And Versioning; AGENTS.md invariant #6) bumps the
  base app version for any "shared/app-wide" change, and this session bumped 2.3.26→2.3.27
  for *adding* the AccountLockoutRemediation module. Owner: **bumping the base app version
  for every new module is wrong.** End-state intent: modules distributed as `.zip` and
  uploaded via the web UI — installing a module must NOT require a recompile or an app
  version bump (only the module's own `Version` moves). This is the deferred
  runtime-upload/dynamic-load capability from `.agents/decisions.md` 2026-06-18; the
  versioning rule must change in step with it. Not yet actioned (owner: "address later").
  When actioned: record a `decision` ("new modules do not bump base app version") and fix
  the Constitution + AGENTS.md #6 wording. The 2.3.27 bump is already committed (`3e84d50`).
- **RESOLVED 2026-06-29 — protected principals are off-limits to every mutating module, no
  carve-outs.** (Recorded in `.agents/decisions.md` 2026-06-29; Constitution §Protected
  Principals updated with an explicit bullet.) The earlier owner position ("protection is only
  about granting permissions, not routine group management") was reversed: the end state is that
  no module may mutate a protected principal — account state, permissions, group membership,
  directory attributes, anything — across EmergencyDisable, AD Attribute Editor, Group
  Management, and the planned M365 member/owner feature. The guard binds to the *target* of the
  write; it must refuse, fail closed, and audit the denial. The on-prem `GroupManagementService`
  `CheckProtectedAsync` gate is therefore correct and stays. **Follow-up (read-only sweep,
  pending):** verify every mutating module actually routes its target through the
  protected-principal check before writing — confirm there is no module that writes without
  gating. Findings go below; any gap becomes its own planned fix.
  - **Sweep DONE 2026-06-29 (read-only audit; two gaps found, both verified in source).** Of
    14 mutating modules, 12 gate the write target through the protected-principal check
    (`ProtectedPrincipalService.CheckAsync` / `PermissionValidator.ValidateTargetMailboxAsync`
    / a local `CheckProtectedAsync`): EmergencyDisable, ADAttributeEditor (+Undo),
    GroupManagement, AccountLockoutRemediation, ConferenceRooms, LicensingUpdates, MfaReset,
    OutOfOffice, MailboxPermissions, CalendarPermissions, Comms10k. BlockedSenders targets a
    sender address (not a principal) — N/A.
  - **GAP 1 — `M365GroupManagementService` — CLOSED 2026-06-29 (principal-write surface).**
    The M365 member/owner management feature shipped (module 1.1.0; commits `211c6eb`,
    `03c443a`; `docs/M365MemberOwnerManagement-Plan.md`, Implemented). Member/owner add/remove
    now routes the target identity through an in-service protected-principal gate
    (`CheckProtectedAsync` → `ProtectedPrincipalService.CheckAsync`, fail closed on
    Unavailable/Ambiguous/CheckFailed) before any Graph write, mirroring GroupManagement.
    Group create/update/delete remain ungated **by design** (owner decision 2026-06-29:
    member/owner only, no protected-*group* gating; see `.agents/decisions.md`). Known
    accepted limitation: AD-based resolution treats a cloud-only NotFound as not protected.
  - **GAP 2 — `MigrationService` — CLOSED 2026-06-30** (module `Migration` 1.1.3→1.2.0,
    app version unchanged; commits `0b855ac`, `5d72978`, + docs/version slice;
    `docs/MigrationProtectedPrincipalGate-Plan.md`, Implemented). `CreateMigrationBatchAsync`
    now partitions every target through the protected-principal gate **before** any side
    effect (CSV build / `New-MigrationBatch`); protected targets are filtered out, the rest
    are migrated, and exclusions are reported in the UI (always-visible warning), audited as
    denial rows, and listed in the admin notification. All-protected (incl. single target) ⇒
    nothing created, clear refusal. Fail-closed on Unavailable/Ambiguous/exception. Owner
    decision (2026-06-30, decisions.md): filter-and-report, never silent, one protected target
    never blocks the batch. Same accepted cloud-only NotFound limitation as the other modules.
    4 new tests, proven non-vacuous; 589/589 green.
  - **GAP 1 and GAP 2 both CLOSED. No protected-principal gating gaps remain.**
- **Prod-deploy hold LIFTED — prod cut over to 2.3.27 (owner, 2026-06-29).** The deferred
  prod hold (owner direction 2026-06-18) is done: prod moved from 2.3.11 (pre-SQLite) straight
  to app **`2.3.27`**, so the full JSON→SQLite legacy import ran on first startup (the path the
  fail-closed parity fix hardens). Sub-TODO that still gates CR-1 in prod: configure the
  ConferenceRooms AD `DelineaSecretId` in the deployed prod instance.
- **Deployed versions (confirmed by owner 2026-06-29):** dev and prod are both on app
  **`2.3.27`**, validated good (GM-1, M365 member/owner, EmergencyDisable synced-user,
  BlockedSenders all confirmed on dev; AccountLockoutRemediation manual validation still
  deferred by owner). The BlockedSenders refresh fix is validated on dev (commit `cde778f`,
  module 1.0.2). **Unverified detail:** the two BlockedSenders module-version fixes
  (`17910f3` → 1.0.1, `cde778f` → 1.0.2) are module bumps, not app bumps, so "prod = app
  2.3.27" does not by itself confirm prod was built from a commit that includes them — confirm
  the prod build commit if the BlockedSenders behaviour matters in prod.

## Verification

- Code: `dotnet build ExchangeAdminWeb.slnx -c Release` then
  `dotnet test ExchangeAdminWeb.slnx`. Add `dotnet format ExchangeAdminWeb.csproj
  --verify-no-changes --no-restore` and `git diff --check HEAD` where practical.
  (Always target the `.slnx`; bare `dotnet test` runs zero tests.)
- PowerShell: `Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps`.
  Deploy-host dependency for the ops scripts: `sqlite3.exe` on PATH.
- When a change ships with a new test, prove it non-vacuous (revert the fix, see the test
  fail, restore). Full policy + manual-check list: `.agents/repo-map.json`, `AGENTS.md`.

## Findings (environment / CI — still live)

- CI is real: a failing test fails the run. Trust it.
- On local macOS, a missing Windows COM DLL can nondeterministically drop xUnit
  collections (totals vary) — trust the failure *list*, not the total. `windows-latest`
  CI is unaffected. macOS builds need `-p:EnableWindowsTargeting=true`; Pester needs
  `pwsh` + `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`.
- `deploy.ps1` still lacks a native `-PlanOnly` (deferred with owner visibility;
  `deploy-pipeline -PlanOnly` covers the prod dry-run requirement).

## Known issues (pre-existing, NOT SQLite-caused)

- **Protected-principal gating gaps — BOTH CLOSED (sweep 2026-06-29).** The 2026-06-29
  decision requires every mutating module to gate its write target through the
  protected-principal check; a read-only sweep found two modules that did not. Both are now
  fixed.
  - **GAP 1 — `M365GroupManagementService` — CLOSED 2026-06-29.** Member/owner add/remove now
    gates the target through the protected-principal check before any Graph write (module
    1.1.0; commits `211c6eb`, `03c443a`; `docs/M365MemberOwnerManagement-Plan.md`). Group
    create/update/delete intentionally ungated (owner decision; see Now section + decisions.md).
  - **GAP 2 — `MigrationService` — CLOSED 2026-06-30.** Batch creation now partitions targets
    through the protected-principal gate before any write; protected targets filtered out and
    reported (UI/audit/email), all-protected ⇒ nothing created (module 1.2.0; commits
    `0b855ac`, `5d72978`; `docs/MigrationProtectedPrincipalGate-Plan.md`). See Now section +
    decisions.md.
  - Full sweep result (12/14 modules already gated; the other 2 now fixed) is in the Now section.

- **MFA Reset stranded legacy config key — RESOLVED 2026-06-26 (app 2.3.26).** The Graph
  Delinea secret was renamed `DelineaSecretId` → `GraphDelineaSecretId`; environments
  configured before the rename held the value under the OLD key, so the config page showed
  blank while the service worked via a `?? DelineaSecretId` fallback. Fixed by a catalog-driven
  idempotent startup migration plus removal of the fallback — see the Now section and
  `docs/GraphSecretKeyMigration-Plan.md` (Status: Implemented). Code path verified by tests;
  deployed to dev and the recovered Secret ID confirmed on the config page (owner,
  2026-06-26). The same fix also cleared the identical latent bug in NamedLocations and
  M365GroupManagement.

## Queued work (forward-looking — no other doc home)

These have no plan doc yet; do not start without the noted plan/approval.

- **Module packaging/import.** Near-term direction set 2026-06-18 (`.agents/decisions.md`):
  `.zip` package + validator, rebuild-to-install, runtime upload deferred. **End state
  confirmed by owner 2026-06-29 (`.agents/decisions.md`): the main app loads a module from
  the UI as a `.zip` upload, no full app rebuild for that module; precompiled-vs-runtime left
  open.** Motivated by a one-line BlockedSenders fix being unable to reach prod (prod still
  runs BlockedSenders 1.0.0) because a module is not an installable unit today. Long-term —
  nothing to build now. Needs `docs/ModulePackaging-Plan.md` written + approved before any
  implementation; the prereq first leg is a module contract / self-registration seam. Related:
  the OPEN versioning-rule blocker (new modules should not bump the base app version).
- **GM-1 — DONE 2026-06-29** (module 2.1.0; commits `c2ac624`, `d8bd2a6`;
  `docs/GroupManagementSearch-Plan.md`, Implemented). Search now ranks exact-first (then
  prefix, then contains; alphabetical within tier) via the pure `RankGroups` in
  `GroupManagementService`; fetches up to 200 from AD, shows top 100 in a scrollable frame.
  Page reordered so controls stay on top and results scroll below. Manual validation deferred
  to next dev deploy.
- **GM-2 (investigated 2026-06-26 — NOT the originally-reported bug).** Live test on dev
  (2.3.26) showed search *works*: it returns only Unified/M365 groups by design
  (`M365GroupManagementService.SearchGroupsAsync` filters
  `groupTypes/any(g:g eq 'Unified') and startsWith(displayName,...)`). The earlier
  "finds no groups at all" report was a synced **security** group (Source: Windows Server AD,
  not Unified) being correctly excluded — the grey "No M365 groups found" message, not the
  red HTTP-status error banner, confirms a clean 200 with empty `value`. Verified the query
  needs no `ConsistencyLevel: eventual`/`$count` per MS advanced-queries table (group
  `displayName`/`startsWith` and `groupTypes/any`/`eq` are both Default-supported). The
  failure-masking fix (`7048a3e`, app 2.3.5) is already in dev, so failures now surface as
  errors, not empty lists. The separate member/owner management gap this investigation also
  surfaced is now BUILT (2026-06-29, module 1.1.0) — see Now section #1 and Blockers GAP 1.
- **GM-3 (new module, needs own plan — DECIDE LATER): self-service group management.**
  Owner direction 2026-06-17, plan separately (`docs/SelfServiceGroupManagement-Plan.md`),
  nothing built until approved. Key requirements: likely a separate module; do NOT preload
  the user's manageable groups (explicit "show groups I manage" button with a slow-load
  warning); search restricted to groups the user manages with GM-1 fixes applied; reject
  any modification to non-managed groups at the service/authorization layer (UI hiding is
  not security). Open: how "manages" is determined, on-prem vs M365 vs both, making the
  lookup tolerable. Depends on GM-1/GM-2 being understood first.

## Recently completed (pointers only — full detail in the named docs)

- **AccountLockoutRemediation module** — incorporated 2026-06-26 (app 2.3.27),
  `docs/AccountLockoutRemediation-Incorporation-Plan.md` (Implemented). Manual validation
  (live AD 4740 read, WinRM, quser/logoff, real dry-run+logoff, protected-block) still
  pending its first dev deploy — run the package's own Manual Validation steps then.

- **CR-BUG-1 EXO pool dead-runspace auto-retry** — `docs/ExoDeadConnectionRetry-Plan.md`
  (Implemented, app 2.3.23, commit `39ce87a`).
- **SQLite Phases A–D** — `docs/SqliteConfigStore-Plan.md` + git log (`e8b155c`..`cf837e8`).
- **ProdReadiness work stream** — COMPLETE, plan Status **Implemented**
  (`docs/ProdReadiness-Plan.md` §10 round 17, `a5ab6aa`); all AC1–AC16 met.
- **2026-06-12 dev config-loss incident** — Remediated
  (`docs/Incident-2026-06-12-DevConfigLoss.md`); real cause `f7df81a` FailClosed on a dev
  box lacking `sectionaccess.json`. Much of the deploy-hardening it produced is
  obsoleted-by-design by the SQLite store.
- **TestAccountPool module removed** (app 2.3.10), **Conference Rooms** RoomListOU removal
  + partial-apply reporting (`8d4f0d6`, module 2.0.10) — decisions in `.agents/decisions.md`.

## Active Sources

- `AGENTS.md`
- `docs/ProjectConstitution.md` (highest engineering authority)
- `docs/SqliteConfigStore-Plan.md` (active work stream)
- `.agents/decisions.md`
- `.agents/repo-map.json`

## Unrecorded Repo Memory

- None known. Engineering rules: `docs/ProjectConstitution.md`; module contract:
  `docs/AdminModuleSpec.md`; work-stream history: `docs/*-Plan.md`.
